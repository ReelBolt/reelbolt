package agent

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"time"

	"github.com/google/uuid"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// ProgressSink receives a job's non-authoritative progress. Nothing in it
// changes what a customer is charged, so a session reports it as a courtesy and
// never blocks on it.
type ProgressSink interface {
	Progress(stage string, percent *int, detail string)
}

// Job is one assignment the runner has accepted: everything the ops of that
// session need, and nothing else.
//
// JobToken is a secret for the life of one job at one epoch. It is deliberately
// not printable: LogValue redacts it, and the library never writes it to the
// log itself.
type Job struct {
	JobID      string
	SessionID  string
	JobType    string
	OrgID      string
	Attempt    int
	LeaseEpoch int64
	JobToken   string
	Timeout    time.Duration
	Limits     protocol.AssignLimits
	Requires   protocol.AssignRequires
	Spec       json.RawMessage
	// Display is assign.display: the workspace, project and workflow names
	// the gateway looked up for people to read. It is the zero value when the
	// gateway sent none (an older gateway, or a lookup that did not answer in
	// time). Display-only: nothing may decide anything by it.
	Display protocol.AssignDisplay
	// ScratchDir is a directory the agent created for this job alone and
	// deletes when the job ends. Sessions must keep everything they write
	// inside it.
	ScratchDir string
	// Sink publishes progress. Never nil for a job the agent created.
	Sink ProgressSink

	end func()
}

// SpecInto decodes the job-type-specific spec.
func (j *Job) SpecInto(v any) error { return json.Unmarshal(j.Spec, v) }

// Done ends the job from the session's side. A session calls it after handling
// the `close` op, which is how the engine ends a session.
func (j *Job) Done() {
	if j.end != nil {
		j.end()
	}
}

// LogValue is the log-safe rendering: the job token never reaches a log file.
func (j *Job) LogValue() any {
	return map[string]any{
		"jobId":      j.JobID,
		"sessionId":  j.SessionID,
		"jobType":    j.JobType,
		"orgId":      j.OrgID,
		"attempt":    j.Attempt,
		"leaseEpoch": j.LeaseEpoch,
		"jobToken":   "[redacted]",
	}
}

// Session executes the ops of one job.
//
// Handle is called serially — a session handles one op at a time, which is the
// protocol's rule and also what makes a session's internal state safe without
// its own locking. It returns either a result, which is marshalled into
// op.response.result, or a protocol.OpError, which becomes
// op.response.error.
type Session interface {
	Handle(ctx context.Context, op string, args json.RawMessage) (any, *protocol.OpError)
	// Close releases everything the session holds. It is always called, exactly
	// once, after the job ends however it ends.
	Close(ctx context.Context)
}

// Factory builds the session for an assigned job.
type Factory interface {
	New(ctx context.Context, job *Job) (Session, error)
}

// FactoryFunc adapts a function to Factory.
type FactoryFunc func(ctx context.Context, job *Job) (Session, error)

// New implements Factory.
func (f FactoryFunc) New(ctx context.Context, job *Job) (Session, error) { return f(ctx, job) }

// JobError is how a session explains a failure that should end a job. Class is
// the runner's claim about whose fault it was; the platform derives the class
// that counts for billing itself, so claiming "platform" buys nothing.
type JobError struct {
	// Class is one of protocol.FailPlatform, protocol.FailRunner,
	// protocol.FailInput.
	Class string
	// Code is the stable machine-readable reason.
	Code string
	// Message is for the user and the engine log.
	Message string
	// Retryable is a hint; the engine decides.
	Retryable bool
}

func (e *JobError) Error() string { return e.Code + ": " + e.Message }

// jobState is a held job: its session, its context and its op cache.
type jobState struct {
	job *Job

	ready    chan struct{}
	session  Session
	startErr error
	ctx      context.Context
	cancel   context.CancelFunc
	mu       sync.Mutex // one op at a time
	ops      *opCache
	done     chan struct{}
	stopOnce sync.Once
	reason   EndReason
	timeoutT *time.Timer
}

// EndReason is how this runner's part of a job ended. It is what the agent
// itself knows, and it is deliberately narrower than "did the job succeed":
// the engine decides that from the ops' results, and a session the engine
// closed normally may still belong to a workflow run that later failed.
type EndReason string

// The end reasons, which also decide what the runner tells the gateway when a
// job stops (see finishJob).
const (
	// EndClosed: the engine ended the session with the close op.
	EndClosed EndReason = "close"
	// EndCancelled: cancel{user}, cancel{execution_ended}, a revoked device, or
	// any cancel reason a later protocol adds.
	EndCancelled EndReason = "cancelled"
	// EndLeaseLost: the gateway took the lease back (cancel{lease_lost}, or it
	// did not resume the job after a reconnect); the job is reassigned.
	EndLeaseLost EndReason = "lease_lost"
	// EndDisconnected: the runner could not get back to the gateway within the
	// lease window, so the gateway has already reassigned the job.
	EndDisconnected EndReason = "disconnected"
	// EndTimeout: the session outlived assign.timeoutSec.
	EndTimeout EndReason = "timeout"
	// EndDrain: the runner is going away (a drain, a shutdown, a gateway
	// shutdown); sessions are rehydrated by the engine elsewhere.
	EndDrain EndReason = "drain"
	// EndFailed: the session could not start on this machine. EndAware never
	// receives it from this agent - a factory that failed produced no session
	// to tell, and the gateway is sent a fail instead - but it is the reason a
	// consumer maps to "could not run here", so it is part of the vocabulary.
	EndFailed EndReason = "failed"
)

// The internal names the job state machine has always used.
const (
	reasonClose        = EndClosed
	reasonCancelled    = EndCancelled
	reasonLeaseLost    = EndLeaseLost
	reasonDisconnected = EndDisconnected
	reasonTimeout      = EndTimeout
	reasonDrain        = EndDrain
	reasonFailed       = EndFailed
)

// JobEnd is how this runner's part of one job ended.
type JobEnd struct {
	// Reason is one of the End* constants. It is "" only when the agent's own
	// lifetime ended underneath a job that had no reason recorded, which a
	// caller should treat like EndDrain: the runner stopped.
	Reason EndReason
	// Class and Code are the protocol's bounded failure reason when Reason is
	// EndFailed or EndTimeout (for example "runner" and "timeout"). They are
	// never free text.
	Class string
	Code  string
}

// EndAware is an optional interface a Session may implement to learn how its
// job ended. The agent calls SetEnd exactly once, immediately BEFORE Close, on
// the session's own goroutine, so an implementation can read the end inside
// Close without locking against the agent.
//
// It is optional so that Session stays the two-method interface every
// executor already implements: a session that does not care is unchanged. A
// session that never started (the factory returned an error) gets neither
// call, because there is no session to tell.
type EndAware interface {
	SetEnd(JobEnd)
}

// stop ends the job for the given reason. The first reason wins: a cancel that
// races a timeout must not rewrite what happened.
func (js *jobState) stop(reason EndReason) {
	js.stopOnce.Do(func() {
		js.reason = reason
		js.cancel()
	})
}

func (js *jobState) stopReason() EndReason {
	js.stopOnce.Do(func() {})
	return js.reason
}

// end is the JobEnd a session is told about. The bounded class and code ride
// along where the runner itself reports one to the gateway.
func (js *jobState) end() JobEnd {
	reason := js.stopReason()
	end := JobEnd{Reason: reason}
	if reason == reasonTimeout {
		end.Class, end.Code = protocol.FailRunner, protocol.ErrCodeTimeout
	}
	return end
}

// handleAssign decides whether to take a job, accepts it and starts its
// session. The specification gives a runner 10 seconds to answer; this answers
// immediately, because the answer never depends on anything slow.
func (a *Agent) handleAssign(ctx context.Context, c *connection, env protocol.Envelope) {
	payload, err := protocol.DecodePayload[protocol.AssignPayload](env)
	if err != nil {
		a.logf("assign payload is not usable: %v", err)
		return
	}
	if reason, message := a.rejection(payload); reason != "" {
		a.logf("rejecting job %s (%s): %s", env.JobID, payload.JobType, reason)
		_ = c.sendJob(protocol.TypeReject, env.JobID, env.SessionID, protocol.RejectPayload{
			LeaseEpoch: payload.LeaseEpoch,
			JobToken:   payload.JobToken,
			Reason:     reason,
			Message:    message,
		})
		return
	}

	scratch, err := jobScratchDir(a.cfg.ScratchRoot, env.JobID)
	if err != nil {
		a.logf("rejecting job: %v", err)
		_ = c.sendJob(protocol.TypeReject, env.JobID, env.SessionID, protocol.RejectPayload{
			LeaseEpoch: payload.LeaseEpoch,
			JobToken:   payload.JobToken,
			Reason:     protocol.RejectCapabilityMismatch,
			Message:    "the job id is not usable as a scratch directory name",
		})
		return
	}
	if err := os.MkdirAll(scratch, 0o700); err != nil {
		a.logf("cannot create the job's scratch directory: %v", err)
		_ = c.sendJob(protocol.TypeReject, env.JobID, env.SessionID, protocol.RejectPayload{
			LeaseEpoch: payload.LeaseEpoch,
			JobToken:   payload.JobToken,
			Reason:     protocol.RejectCapabilityMismatch,
			Message:    "the runner could not create a scratch directory for this job",
		})
		return
	}

	jobCtx, cancel := context.WithCancel(a.life)
	job := &Job{
		JobID:      env.JobID,
		SessionID:  env.SessionID,
		JobType:    payload.JobType,
		OrgID:      payload.OrgID,
		Attempt:    payload.Attempt,
		LeaseEpoch: payload.LeaseEpoch,
		JobToken:   payload.JobToken,
		Timeout:    time.Duration(payload.TimeoutSec) * time.Second,
		Limits:     payload.Limits,
		Requires:   payload.Requires,
		Spec:       payload.Spec,
		ScratchDir: scratch,
	}
	if payload.Display != nil {
		job.Display = *payload.Display
	}
	js := &jobState{
		job:    job,
		ready:  make(chan struct{}),
		ctx:    jobCtx,
		cancel: cancel,
		ops:    newOpCache(protocol.OpResultCacheSize),
		done:   make(chan struct{}),
	}
	job.end = func() { js.stop(reasonClose) }
	job.Sink = &progressSink{a: a, js: js}

	// Register before accepting: a heartbeat that lands between the accept and
	// the session starting must already list the job, or the gateway would
	// renew nothing for it and reap it out from under the runner.
	a.mu.Lock()
	if a.draining || len(a.jobs) >= a.cfg.Capabilities.Slots {
		a.mu.Unlock()
		cancel()
		_ = os.RemoveAll(scratch)
		_ = c.sendJob(protocol.TypeReject, env.JobID, env.SessionID, protocol.RejectPayload{
			LeaseEpoch: payload.LeaseEpoch,
			JobToken:   payload.JobToken,
			Reason:     protocol.RejectBusy,
		})
		return
	}
	a.jobs[env.JobID] = js
	a.mu.Unlock()

	if err := c.sendJob(protocol.TypeAccept, env.JobID, env.SessionID, protocol.AcceptPayload{
		LeaseEpoch: payload.LeaseEpoch,
		JobToken:   payload.JobToken,
	}); err != nil {
		a.logf("accept for job %s did not reach the gateway: %v", env.JobID, err)
		a.forgetJob(env.JobID)
		cancel()
		_ = os.RemoveAll(scratch)
		return
	}
	a.logf("accepted job %s (%s, epoch %d, attempt %d)", env.JobID, payload.JobType, payload.LeaseEpoch, payload.Attempt)
	_ = ctx

	go a.runJob(js)
}

// jobScratchDir is the job's directory under root. The id arrives from the gateway; it must be one
// plain path element, so it can never name anything outside root.
func jobScratchDir(root, jobID string) (string, error) {
	if jobID == "" || len(jobID) > 128 {
		return "", errors.New("job id is empty or too long")
	}
	for _, r := range jobID {
		ok := r == '-' || r == '_' || (r >= '0' && r <= '9') || (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z')
		if !ok {
			return "", errors.New("job id has characters outside [A-Za-z0-9_-]")
		}
	}
	return filepath.Join(filepath.Clean(root), jobID), nil
}

// rejection reports why a job must be declined, or "" to take it. Every check
// is one the specification names as a reject reason, so the gateway can requeue
// the job without consuming an attempt.
func (a *Agent) rejection(payload protocol.AssignPayload) (string, string) {
	if a.isDraining() {
		return protocol.RejectShuttingDown, "the runner is shutting down"
	}
	if a.freeSlots() <= 0 {
		return protocol.RejectBusy, "the runner has no free slot"
	}
	if payload.JobToken == "" {
		return protocol.RejectCapabilityMismatch, "the assignment carried no job token"
	}
	if !a.cfg.Capabilities.Supports(payload.JobType) {
		return protocol.RejectCapabilityMismatch, fmt.Sprintf("this runner does not run %s", payload.JobType)
	}
	req := payload.Requires
	if req.Container != "" && req.Container != a.cfg.Capabilities.Container {
		if a.cfg.Capabilities.Container == protocol.ContainerNone {
			return protocol.RejectNoContainerRuntime, "this runner has no container runtime"
		}
		return protocol.RejectCapabilityMismatch, fmt.Sprintf("this job needs the %s runtime", req.Container)
	}
	if req.RuntimeImageDigest != "" {
		have := ""
		if a.cfg.Capabilities.RuntimeImageDigest != nil {
			have = *a.cfg.Capabilities.RuntimeImageDigest
		}
		if have != req.RuntimeImageDigest {
			return protocol.RejectCapabilityMismatch, "this runner has a different runtime image"
		}
	}
	return "", ""
}

// runJob owns a job's session for its whole life: it starts the session, waits
// for the job to end however it ends, releases everything, and tells the
// gateway what happened.
func (a *Agent) runJob(js *jobState) {
	defer close(js.done)
	if js.job.Timeout > 0 {
		js.timeoutT = time.AfterFunc(js.job.Timeout, func() { js.stop(reasonTimeout) })
	}
	session, err := a.cfg.Sessions.New(js.ctx, js.job)
	if err != nil {
		js.startErr = err
		close(js.ready)
		js.stop(reasonFailed)
		a.failJob(js, err)
		a.finishJob(js)
		return
	}
	js.session = session
	close(js.ready)

	<-js.ctx.Done()
	if js.timeoutT != nil {
		js.timeoutT.Stop()
	}
	if aware, ok := session.(EndAware); ok {
		aware.SetEnd(js.end())
	}
	closeCtx, cancel := context.WithTimeout(context.Background(), a.cfg.CancelGrace)
	session.Close(closeCtx)
	cancel()
	if err := os.RemoveAll(js.job.ScratchDir); err != nil {
		a.logf("could not remove the scratch directory of job %s: %v", js.job.JobID, err)
	}
	a.finishJob(js)
}

// finishJob sends the terminal message a normal end implies and frees the slot.
//
// The mapping is the specification's: an acknowledged cancellation sends
// complete{outcome:cancelled}; a session that outlived assign.timeoutSec fails
// with a timeout; a lost lease and a drain send nothing, because the gateway
// has already reassigned or is about to reap the job and a message under a
// fenced epoch would be discarded anyway; a `close` op sends nothing either,
// because the engine closing the session is itself the terminal transition.
func (a *Agent) finishJob(js *jobState) {
	reason := js.stopReason()
	switch reason {
	case reasonCancelled:
		a.sendTerminal(js, protocol.TypeComplete, protocol.CompletePayload{
			LeaseEpoch: js.job.LeaseEpoch,
			JobToken:   js.job.JobToken,
			Outcome:    protocol.OutcomeCancelled,
		})
	case reasonTimeout:
		a.sendTerminal(js, protocol.TypeFail, protocol.FailPayload{
			LeaseEpoch: js.job.LeaseEpoch,
			JobToken:   js.job.JobToken,
			Class:      protocol.FailRunner,
			Code:       protocol.ErrCodeTimeout,
			Message:    "the session outlived the time the gateway allowed for it",
			Retryable:  true,
		})
	}
	a.forgetJob(js.job.JobID)
	a.logf("job %s ended (%s)", js.job.JobID, reason)
	a.sendReadyIfConnected()
}

// failJob reports a session that never started or that died on its own.
func (a *Agent) failJob(js *jobState, err error) {
	class, code, message, retryable := protocol.FailRunner, "job_setup_failed", err.Error(), true
	var jobErr *JobError
	if errors.As(err, &jobErr) {
		if jobErr.Class != "" {
			class = jobErr.Class
		}
		if jobErr.Code != "" {
			code = jobErr.Code
		}
		if jobErr.Message != "" {
			message = jobErr.Message
		}
		retryable = jobErr.Retryable
	}
	a.sendTerminal(js, protocol.TypeFail, protocol.FailPayload{
		LeaseEpoch: js.job.LeaseEpoch,
		JobToken:   js.job.JobToken,
		Class:      class,
		Code:       code,
		Message:    message,
		Retryable:  retryable,
	})
}

// sendTerminal sends one job-scoped message on whatever connection is current.
// A message that cannot be sent is dropped: the connection is gone, the
// gateway's own lease handling covers it, and blocking here would stall the
// teardown of a job that is already over.
func (a *Agent) sendTerminal(js *jobState, typ protocol.MessageType, payload any) {
	c := a.currentConn()
	if c == nil {
		return
	}
	if err := c.sendJob(typ, js.job.JobID, js.job.SessionID, payload); err != nil {
		a.logf("could not send %s for job %s: %v", typ, js.job.JobID, err)
	}
}

// handleCancel stops a job the gateway cancelled, within the grace window the
// cancel names.
func (a *Agent) handleCancel(jobID string, payload protocol.CancelPayload) {
	a.logf("job %s cancelled (%s)", jobID, payload.Reason)
	grace := time.Duration(payload.GraceSec) * time.Second
	if grace <= 0 {
		grace = a.cfg.CancelGrace
	}
	a.stopJobWithin(jobID, payload.Reason, grace)
}

// stopJob stops a job for a reason the gateway did not send (a lost lease, a
// drain, a shutdown).
func (a *Agent) stopJob(jobID, reason string) {
	a.stopJobWithin(jobID, reason, a.cfg.CancelGrace)
}

// stopJobFor stops a job for an end reason the protocol has no cancel reason
// for (the runner's own lost connection).
func (a *Agent) stopJobFor(jobID string, end EndReason) {
	a.stopJobEnd(jobID, end, a.cfg.CancelGrace)
}

// stopJobWithin ends the job and waits, at most grace, for its session to
// release the container or process before the runner moves on.
func (a *Agent) stopJobWithin(jobID, reason string, grace time.Duration) {
	a.stopJobEnd(jobID, cancelReasonToEnd(reason), grace)
}

// stopJobEnd is stopJobWithin with the end reason already decided.
func (a *Agent) stopJobEnd(jobID string, end EndReason, grace time.Duration) {
	a.mu.Lock()
	js, ok := a.jobs[jobID]
	if ok {
		// Drop it from the held set first: the next heartbeat must not renew
		// the lease of a job the runner is stopping.
		delete(a.jobs, jobID)
	}
	a.mu.Unlock()
	if !ok {
		return
	}
	js.stop(end)
	if grace <= 0 {
		grace = a.cfg.CancelGrace
	}
	select {
	case <-js.done:
	case <-time.After(grace):
		a.logf("job %s did not stop within %s; abandoning its session", jobID, grace)
	}
}

// cancelReasonToEnd maps the protocol's cancel reasons onto the runner's
// end reasons.
func cancelReasonToEnd(reason string) EndReason {
	switch reason {
	case protocol.CancelUser, protocol.CancelExecutionEnded:
		return reasonCancelled
	case protocol.CancelLeaseLost:
		return reasonLeaseLost
	case protocol.CancelShutdown:
		return reasonDrain
	default:
		// "revoked" and any reason a later version adds: the job stops, and the
		// gateway is told it was cancelled if the socket is still there.
		return reasonCancelled
	}
}

// reconcileHeld stops every job the gateway did not resume. Their leases have
// been reassigned; a resumed result would be fenced out, and keeping them
// running would burn a customer's machine for nothing.
func (a *Agent) reconcileHeld(welcome protocol.WelcomePayload) {
	resumed := make(map[string]struct{}, len(welcome.Resumed))
	for _, l := range welcome.Resumed {
		resumed[l.JobID] = struct{}{}
	}
	a.mu.Lock()
	ids := make([]string, 0, len(a.jobs))
	for id := range a.jobs {
		ids = append(ids, id)
	}
	a.mu.Unlock()
	for _, id := range ids {
		if _, ok := resumed[id]; ok {
			continue
		}
		a.logf("job %s was not resumed after reconnecting; stopping it", id)
		a.stopJob(id, protocol.CancelLeaseLost)
	}
}

// watchHeldWhileDisconnected stops the held jobs if the runner cannot get back
// to the gateway within the lease window. Past that the gateway has already
// reaped them, so a laptop that slept through a deploy stops rather than
// finishing work nobody is waiting for.
func (a *Agent) watchHeldWhileDisconnected() {
	a.mu.Lock()
	ids := make([]string, 0, len(a.jobs))
	for id := range a.jobs {
		ids = append(ids, id)
	}
	a.mu.Unlock()
	if len(ids) == 0 {
		return
	}
	go func() {
		timer := time.NewTimer(a.cfg.LeaseLostAfter)
		defer timer.Stop()
		<-timer.C
		if a.currentConn() != nil {
			return
		}
		for _, id := range ids {
			a.mu.Lock()
			_, still := a.jobs[id]
			a.mu.Unlock()
			if !still {
				continue
			}
			a.logf("job %s lost its lease while the runner was disconnected", id)
			a.stopJobFor(id, reasonDisconnected)
		}
	}()
}

// teardownSessions stops every session, without waiting for their jobs to
// finish: the specification says a draining runner does not wait for sessions
// and they are rehydrated by the engine.
func (a *Agent) teardownSessions(reason string) {
	a.mu.Lock()
	ids := make([]string, 0, len(a.jobs))
	for id := range a.jobs {
		ids = append(ids, id)
	}
	a.mu.Unlock()
	if len(ids) > 0 {
		a.logf("stopping %d held session(s): %s", len(ids), reason)
	}
	for _, id := range ids {
		a.stopJob(id, protocol.CancelShutdown)
	}
}

// teardownAll stops every job and waits for the sessions to release their
// resources.
func (a *Agent) teardownAll(reason string) {
	a.teardownSessions(reason)
}

func (a *Agent) job(jobID string) *jobState {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.jobs[jobID]
}

func (a *Agent) forgetJob(jobID string) {
	a.mu.Lock()
	delete(a.jobs, jobID)
	a.mu.Unlock()
}

func (a *Agent) currentConn() *connection {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.conn
}

// progressSink publishes non-authoritative progress on whichever connection is
// current.
type progressSink struct {
	a  *Agent
	js *jobState
}

func (s *progressSink) Progress(stage string, percent *int, detail string) {
	c := s.a.currentConn()
	if c == nil || s.js.ctx.Err() != nil {
		return
	}
	payload := protocol.ProgressPayload{
		LeaseEpoch: s.js.job.LeaseEpoch,
		JobToken:   s.js.job.JobToken,
		Stage:      stage,
		Percent:    percent,
		Detail:     detail,
	}
	if err := c.sendJob(protocol.TypeProgress, s.js.job.JobID, s.js.job.SessionID, payload); err != nil && !errors.Is(err, ErrNotConnected) {
		s.a.logf("progress for job %s did not reach the gateway: %v", s.js.job.JobID, err)
	}
}

// NewScopeID generates the identifier a session names its workspace by. It is
// a fresh UUID rather than the engine's execution id so that two jobs of one
// execution cannot collide in a driver that keys workspaces by that id.
func NewScopeID() string { return uuid.NewString() }
