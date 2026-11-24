package main

import (
	"context"
	"encoding/json"
	"errors"
	"log"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// This file is the runner-facing half of the job state machine: claiming work
// against free slots, offering it with a lease and a job token, and fencing
// every message the runner sends back.
//
// The durable half is queue.go; the sweeps that take leases back are reaper.go.

const (
	// rejectionMemory is how long the gateway remembers that a runner rejected a
	// job: "the gateway may remember the rejection to avoid immediately
	// re-offering it to the same runner".
	rejectionMemory = 30 * time.Second
	// jobTimeoutDefaultSec bounds a session's lifetime when the job's spec does
	// not carry one.
	jobTimeoutDefaultSec = 1800
	// jobTimeoutMaxSec is the protocol's cap on a session's lifetime.
	jobTimeoutMaxSec = 14400
	// jobIdleTimeoutSec closes a session with no op for that long.
	jobIdleTimeoutSec = 1800
	// jobTokenGraceSec is the margin between the job's own deadline and the job
	// token's exp: "no later than the job timeout plus 5 minutes".
	jobTokenGraceSec = 300
)

// jobSpecLimits is the optional part of a job spec the gateway itself reads to
// build the assignment. Everything else in spec_json is opaque to it: the job
// type decides the shape.
type jobSpecLimits struct {
	TimeoutSec     int   `json:"timeoutSec,omitempty"`
	IdleTimeoutSec int   `json:"idleTimeoutSec,omitempty"`
	MaxOutputBytes int64 `json:"maxOutputBytes,omitempty"`
}

// assignWork tries to hand the runner as much eligible work as it has free
// slots for.
func (g *gateway) assignWork(ctx context.Context, c *conn) {
	if g.jobs == nil || g.draining.Load() {
		return
	}
	snap := c.snapshot()
	if snap.Draining {
		return
	}
	// freeSlots is the runner's own count and already excludes the jobs it says
	// it is running. Offers that have not been answered yet are not in it, and
	// neither are the jobs accepted since the last heartbeat, so both are
	// subtracted here. Without that, one slot could be promised twice: once to a
	// job the runner has not answered for, and again to a job it just accepted.
	want := snap.FreeSlots - len(snap.Offers) - snap.SinceHeartbeat
	if want <= 0 {
		return
	}

	claimCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()

	// The platform kill switches come first (F11): a paused platform, or one
	// whose settings cannot be read, assigns nothing at all. Nothing is lost by
	// refusing here — the D5b queue is durable, so the jobs stay Queued and the
	// next round after the switch is released takes them.
	gates, allowed := g.claimGate(claimCtx)
	if !allowed {
		return
	}

	jobs, err := g.jobs.ClaimJobs(claimCtx, ClaimRequest{
		RunnerID:      c.runnerID,
		Kind:          c.kind,
		OrgID:         c.orgID,
		Caps:          c.caps,
		Want:          want,
		Now:           g.now().UTC(),
		LeaseBy:       g.cfg.leaseDuration(),
		Instance:      g.cfg.GatewayInstance,
		ExcludeJobIDs: c.rejectedJobs(g.now().UTC(), rejectionMemory),
		PoolOrgCap:    g.cfg.PoolOrgSlotCap,
		Gates:         gates,
	})
	if err != nil {
		g.metrics.claimFailures.Inc()
		log.Printf("%s: claiming work failed: %v", c.logTag, err)
		return
	}
	for _, job := range jobs {
		if err := g.offer(ctx, c, job); err != nil {
			// The offer never reached the runner, so the lease is handed straight
			// back rather than waiting 45 seconds for the reaper. The epoch comes
			// from the row, so this cannot release a newer assignment.
			g.releaseLease(context.WithoutCancel(ctx), c, job, "offer_failed")
			log.Printf("%s: offering job %s failed: %v", c.logTag, shortID(job.ID), err)
			continue
		}
	}
}

// Claim deferral reasons: the reason label of
// reelbolt_runner_gateway_claims_deferred_total. They are separate values
// because the operator's next move differs — an engaged switch is a decision
// somebody made, an unreadable store is an incident — and one of the two is
// worth paging on and the other is not.
const (
	deferReasonComputePaused      = "compute_paused"
	deferReasonSwitchesUnreadable = "switches_unreadable"
)

// claimGate decides whether this runner may be assigned work right now, and what
// per-candidate decisions the claim must carry.
//
// The boolean answers "may I assign at all", and every way of not knowing
// answers FALSE: an engaged compute switch, a store that could not be read, and
// a gateway with no switches facade at all. That is deliberate — the failure
// direction of a kill switch is "refuse" — and it is why the caller has no way
// to assign without asking.
//
// It deliberately does not log. A store that cannot be read is logged once on
// the transition into that state by the switches facade, and the counter below
// is what says how often it is costing work; a line here would be one per runner
// per assignment round, which is noise proportional to fleet size. The handshake
// path does log, because there a connection it could not authorize is being let
// in, which happens once per connection rather than once per five seconds.
func (g *gateway) claimGate(ctx context.Context) (ClaimGates, bool) {
	switches, err := g.switches.Snapshot(ctx)
	if err != nil {
		g.metrics.claimDeferredFor(deferReasonSwitchesUnreadable)
		return ClaimGates{}, false
	}
	if switches.ComputePaused() {
		g.metrics.claimDeferredFor(deferReasonComputePaused)
		return ClaimGates{}, false
	}
	return switches.ClaimGates(), true
}

// offer sends one assign and arms the accept watchdog.
func (g *gateway) offer(ctx context.Context, c *conn, job Job) error {
	timeoutSec, idleSec, maxBytes := g.assignLimits(job, c.caps)
	claims := protocol.JobTokenClaims{
		JobID:     job.ID,
		SessionID: job.SessionID,
		Epoch:     job.LeaseEpoch,
		OrgID:     job.OrganizationID,
		Exp:       g.now().UTC().Add(time.Duration(timeoutSec+jobTokenGraceSec) * time.Second).Unix(),
	}
	token, err := g.tokens.mint(claims)
	if err != nil {
		return err
	}
	spec := json.RawMessage(job.SpecJSON)
	if len(spec) == 0 {
		spec = json.RawMessage(`{}`)
	}
	requires, err := ParseRequiredCaps(job.RequiredCapsJSON)
	if err != nil {
		return err
	}
	payload := protocol.AssignPayload{
		JobType:    job.JobType,
		LeaseEpoch: job.LeaseEpoch,
		JobToken:   token,
		Attempt:    job.Attempt,
		TimeoutSec: timeoutSec,
		OrgID:      job.OrganizationID,
		Limits:     protocol.AssignLimits{MaxOutputBytes: maxBytes, IdleTimeoutSec: idleSec},
		Requires: protocol.AssignRequires{
			RuntimeImageDigest: requires.RuntimeImageDigest,
			Container:          requires.Container,
			FontsetVersion:     requires.FontsetVersion,
			FfmpegMajor:        requires.FfmpegMajor,
		},
		Spec: spec,
		// Best effort and bounded: a lookup that fails or is slow leaves the
		// names out and the job is offered anyway (display.go).
		Display: g.jobDisplay(ctx, job),
	}
	env, err := protocol.NewJob(protocol.TypeAssign, job.ID, job.SessionID, payload)
	if err != nil {
		return err
	}
	c.addOffer(job.ID, job.LeaseEpoch)
	if err := c.send(ctx, env); err != nil {
		c.removeOffer(job.ID)
		return err
	}
	g.metrics.leaseEvent("assigned")
	g.metrics.countMessage(string(protocol.TypeAssign), false)
	log.Printf("%s: assigned job %s type=%s epoch=%d attempt=%d",
		c.logTag, shortID(job.ID), job.JobType, job.LeaseEpoch, job.Attempt)

	// "The runner sends accept within 10 seconds ... No answer in 10 seconds is
	// treated as a reject and the lease is released." The watchdog is per offer,
	// and its release is fenced by the epoch, so a job that was reassigned in the
	// meantime is untouched.
	g.scheduleAcceptTimeout(c, job)
	return nil
}

// assignLimits resolves the assignment's caps from the job spec, the runner's
// advertised capabilities and the protocol's defaults.
func (g *gateway) assignLimits(job Job, caps protocol.Capabilities) (timeoutSec, idleSec int, maxOutputBytes int64) {
	var limits jobSpecLimits
	if job.SpecJSON != "" {
		// A spec the gateway cannot read is not fatal: the defaults below are
		// valid for every job type, and refusing to assign would strand the job.
		_ = json.Unmarshal([]byte(job.SpecJSON), &limits)
	}
	timeoutSec = limits.TimeoutSec
	if timeoutSec <= 0 || timeoutSec > jobTimeoutMaxSec {
		timeoutSec = jobTimeoutDefaultSec
	}
	idleSec = limits.IdleTimeoutSec
	if idleSec <= 0 {
		idleSec = jobIdleTimeoutSec
	}
	maxOutputBytes = limits.MaxOutputBytes
	if caps.MaxOutputBytes > 0 && (maxOutputBytes <= 0 || caps.MaxOutputBytes < maxOutputBytes) {
		maxOutputBytes = caps.MaxOutputBytes
	}
	return timeoutSec, idleSec, maxOutputBytes
}

// scheduleAcceptTimeout arms the 10-second accept window.
func (g *gateway) scheduleAcceptTimeout(c *conn, job Job) {
	timeout := g.cfg.acceptTimeout()
	time.AfterFunc(timeout, func() {
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		// A job the runner already accepted or rejected is no longer an open
		// offer; RejectJob is fenced by the epoch and status, so a late watchdog
		// cannot undo a transition that already happened.
		outcome, err := g.jobs.RejectJob(ctx, job.ID, c.runnerID, job.LeaseEpoch, g.now().UTC())
		if err != nil {
			log.Printf("%s: accept watchdog for job %s failed: %v", c.logTag, shortID(job.ID), err)
			return
		}
		if outcome != LeaseApplied {
			c.removeOffer(job.ID)
			return
		}
		c.removeOffer(job.ID)
		g.metrics.leaseEvent("accept_timeout")
		log.Printf("%s: job %s was not accepted within %s, released", c.logTag, shortID(job.ID), timeout)
	})
}

// releaseLease hands a lease back immediately, without waiting for the reaper.
func (g *gateway) releaseLease(ctx context.Context, c *conn, job Job, reason string) {
	outcome, err := g.jobs.RejectJob(ctx, job.ID, c.runnerID, job.LeaseEpoch, g.now().UTC())
	if err != nil {
		log.Printf("%s: releasing job %s failed: %v", c.logTag, shortID(job.ID), err)
		return
	}
	c.removeOffer(job.ID)
	if outcome == LeaseApplied {
		g.metrics.leaseEvent(reason)
	}
}

// fenceToken checks the job token and its claims against the message. It is the
// cheap half of fencing and runs before any database work: a message whose token
// is missing, forged, expired or minted for another job, epoch or organisation
// never reaches the queue.
func (g *gateway) fenceToken(c *conn, env protocol.Envelope, epoch int64, token string) (protocol.JobTokenClaims, bool) {
	claims, err := g.tokens.verify(token, g.now().UTC())
	if err != nil {
		return protocol.JobTokenClaims{}, false
	}
	if claims.JobID != env.JobID || claims.Epoch != epoch {
		return protocol.JobTokenClaims{}, false
	}
	// Least authority: a desktop device's token may only name its own
	// organisation's job. A pool worker's tokens name whichever organisation
	// owns the job it was given, which is why this is checked for the device
	// path only.
	if c.kind != protocol.KindCloudPool && c.orgID != "" && claims.OrgID != c.orgID {
		return protocol.JobTokenClaims{}, false
	}
	return claims, true
}

// leaseHeld reports whether the database still has this runner holding that
// epoch. It is the read half of fencing, used by the messages that do not
// themselves write a lease transition.
func (g *gateway) leaseHeld(ctx context.Context, c *conn, jobID string, epoch int64) (Job, bool) {
	job, err := g.jobs.GetJob(ctx, jobID)
	if err != nil {
		if !errors.Is(err, ErrJobNotFound) {
			log.Printf("%s: reading job %s failed: %v", c.logTag, shortID(jobID), err)
		}
		return Job{}, false
	}
	if job.LeaseOwnerRunnerID != c.runnerID || job.LeaseEpoch != epoch || !job.Leased() {
		return Job{}, false
	}
	return job, true
}

// leaseLost tells a runner to stop a job whose lease the gateway no longer
// honours. "The gateway MUST ... tell that runner to stop with a cancel (reason
// lease_lost)". It is best effort: the connection may already be gone, and the
// heartbeat's leases answer carries the same message on the next beat.
func (g *gateway) leaseLost(ctx context.Context, c *conn, jobID string) {
	g.metrics.leaseEvent("fenced")
	// "the gateway ... discards any later message with that epoch": a job that is
	// already finished has nothing left to stop, so a second complete or fail for
	// it is dropped without telling the runner off for a job it has finished.
	if g.jobs != nil {
		if job, err := g.jobs.GetJob(ctx, jobID); err == nil && job.Terminal() {
			g.metrics.leaseEvent("discarded_terminal")
			log.Printf("%s: discarding a message for job %s, which is already %s",
				c.logTag, shortID(jobID), job.Status)
			return
		}
	}
	log.Printf("%s: rejecting a message for job %s with a stale epoch or token", c.logTag, shortID(jobID))
	env, err := protocol.NewJob(protocol.TypeCancel, jobID, "",
		protocol.CancelPayload{Reason: protocol.CancelLeaseLost, GraceSec: int(g.cfg.cancelGrace() / time.Second)})
	if err != nil {
		return
	}
	if err := c.send(ctx, env); err != nil {
		log.Printf("%s: sending cancel{lease_lost} for %s failed: %v", c.logTag, shortID(jobID), err)
		return
	}
	g.metrics.countMessage(string(protocol.TypeCancel), false)
}

// handleAccept moves an assigned job to Running.
func (g *gateway) handleAccept(ctx context.Context, c *conn, env protocol.Envelope) {
	payload, err := protocol.DecodePayload[protocol.AcceptPayload](env)
	if err != nil {
		log.Printf("%s: accept payload: %v", c.logTag, err)
		return
	}
	if _, ok := g.fenceToken(c, env, payload.LeaseEpoch, payload.JobToken); !ok {
		g.leaseLost(ctx, c, env.JobID)
		return
	}
	outcome, err := g.jobs.AcceptJob(ctx, env.JobID, c.runnerID, payload.LeaseEpoch, g.now().UTC())
	if err != nil {
		log.Printf("%s: accept job %s failed: %v", c.logTag, shortID(env.JobID), err)
		return
	}
	if outcome != LeaseApplied {
		g.leaseLost(ctx, c, env.JobID)
		return
	}
	c.removeOffer(env.JobID)
	c.addSinceHeartbeat(env.JobID)
	g.metrics.leaseEvent("accepted")
	g.touch(ctx, c)
}

// handleReject returns a job to the queue. The attempt count is not incremented.
func (g *gateway) handleReject(ctx context.Context, c *conn, env protocol.Envelope) {
	payload, err := protocol.DecodePayload[protocol.RejectPayload](env)
	if err != nil {
		log.Printf("%s: reject payload: %v", c.logTag, err)
		return
	}
	if _, ok := g.fenceToken(c, env, payload.LeaseEpoch, payload.JobToken); !ok {
		g.leaseLost(ctx, c, env.JobID)
		return
	}
	outcome, err := g.jobs.RejectJob(ctx, env.JobID, c.runnerID, payload.LeaseEpoch, g.now().UTC())
	if err != nil {
		log.Printf("%s: reject job %s failed: %v", c.logTag, shortID(env.JobID), err)
		return
	}
	c.forgetJob(env.JobID)
	if outcome != LeaseApplied {
		g.leaseLost(ctx, c, env.JobID)
		return
	}
	c.markRejected(env.JobID, g.now().UTC())
	g.clearPendingCancel(env.JobID)
	g.metrics.leaseEvent("rejected")
	g.touch(ctx, c)
	// Somebody else may be able to take it. The reject above already returned it
	// to Queued, so an idle runner on this instance picks it up on its next beat.
	g.pokeAssigners()
}

// handleProgress accepts non-authoritative progress. Nothing in it changes what
// a customer is charged.
func (g *gateway) handleProgress(ctx context.Context, c *conn, env protocol.Envelope) {
	payload, err := protocol.DecodePayload[protocol.ProgressPayload](env)
	if err != nil {
		log.Printf("%s: progress payload: %v", c.logTag, err)
		return
	}
	if _, ok := g.fenceToken(c, env, payload.LeaseEpoch, payload.JobToken); !ok {
		g.leaseLost(ctx, c, env.JobID)
		return
	}
	if _, ok := g.leaseHeld(ctx, c, env.JobID, payload.LeaseEpoch); !ok {
		g.leaseLost(ctx, c, env.JobID)
		return
	}
}

// handleOpResponse fences an op result and routes it to the engine request that
// is waiting for it.
//
// D5b owns the fence, D5c owns the routing. The fence is what makes the routing
// safe to do at all: a result carrying a stale epoch, an unknown job or a forged
// token is rejected and the sender told to stop, so a slow runner cannot deliver
// an op result into another runner's session. Only a result that passed the
// fence reaches the op table, and the opId is then looked up in the table of
// THIS replica — which is correct by construction, because the request was
// forwarded here precisely because the socket is here (see handleSessionOp).
func (g *gateway) handleOpResponse(ctx context.Context, c *conn, env protocol.Envelope) {
	payload, err := protocol.DecodePayload[protocol.OpResponsePayload](env)
	if err != nil {
		log.Printf("%s: op.response payload: %v", c.logTag, err)
		return
	}
	if _, ok := g.fenceToken(c, env, payload.LeaseEpoch, payload.JobToken); !ok {
		g.leaseLost(ctx, c, env.JobID)
		return
	}
	if _, ok := g.leaseHeld(ctx, c, env.JobID, payload.LeaseEpoch); !ok {
		g.leaseLost(ctx, c, env.JobID)
		return
	}
	g.metrics.opResponses.Inc()
	if !g.ops.deliver(payload.OpID, payload) {
		// Nobody is waiting: a duplicate answer, or one for an op the engine gave
		// up on. Not an error — the worker's own cache answers a repeated opId, so
		// a duplicate is expected after a reconnect.
		g.metrics.sessionOps("unmatched")
	}
}

// handleComplete ends a job successfully or as an acknowledged cancellation.
func (g *gateway) handleComplete(ctx context.Context, c *conn, env protocol.Envelope) {
	payload, err := protocol.DecodePayload[protocol.CompletePayload](env)
	if err != nil {
		log.Printf("%s: complete payload: %v", c.logTag, err)
		return
	}
	if _, ok := g.fenceToken(c, env, payload.LeaseEpoch, payload.JobToken); !ok {
		g.leaseLost(ctx, c, env.JobID)
		return
	}
	var status string
	switch payload.Outcome {
	case protocol.OutcomeSucceeded:
		status = jobSucceeded
	case protocol.OutcomeCancelled:
		status = jobCancelled
	default:
		// "Enum-like string values ... are open sets: an unrecognised value MUST
		// be handled as an unknown case, not as a parse error." An outcome the
		// gateway does not know is not a success.
		log.Printf("%s: complete for job %s has unknown outcome %q, treating it as a failure",
			c.logTag, shortID(env.JobID), payload.Outcome)
		status = jobFailed
	}
	// result and metrics are informational: billing is priced from the
	// platform-side job spec, never from anything the runner reports.
	detail := map[string]any{}
	if len(payload.Result) > 0 {
		detail["result"] = payload.Result
	}
	if len(payload.Metrics) > 0 {
		detail["metrics"] = payload.Metrics
	}
	raw, _ := json.Marshal(detail)
	errorClass := ""
	if status == jobFailed {
		errorClass = protocol.FailRunner
	}
	outcome, err := g.jobs.FinishJob(ctx, FinishRequest{
		JobID: env.JobID, RunnerID: c.runnerID, Epoch: payload.LeaseEpoch,
		Status: status, Result: raw, ErrorClass: errorClass, Now: g.now().UTC(),
	})
	if err != nil {
		log.Printf("%s: complete job %s failed: %v", c.logTag, shortID(env.JobID), err)
		return
	}
	if outcome != LeaseApplied {
		g.leaseLost(ctx, c, env.JobID)
		return
	}
	c.forgetJob(env.JobID)
	g.clearPendingCancel(env.JobID)
	// The job is over, so nothing will answer an op that is still in flight on it.
	g.ops.abortJob(env.JobID, "job_ended", true)
	g.metrics.leaseEvent(status)
	g.touch(ctx, c)
	g.pokeAssigners()
}

// handleFail ends a job as failed. The class is the runner's claim, recorded but
// not trusted: the class that counts for billing is the one the platform derives
// itself (lease expiry and output verification are platform-observed).
func (g *gateway) handleFail(ctx context.Context, c *conn, env protocol.Envelope) {
	payload, err := protocol.DecodePayload[protocol.FailPayload](env)
	if err != nil {
		log.Printf("%s: fail payload: %v", c.logTag, err)
		return
	}
	if _, ok := g.fenceToken(c, env, payload.LeaseEpoch, payload.JobToken); !ok {
		g.leaseLost(ctx, c, env.JobID)
		return
	}
	class := payload.Class
	switch class {
	case protocol.FailPlatform, protocol.FailRunner, protocol.FailInput:
	default:
		// An unknown class is not an error; it is simply not a class the
		// platform recognises, so it is recorded as the runner's claim verbatim
		// and treated as a runner fault by anything that reads it.
		class = protocol.FailRunner
	}
	detail, err := json.Marshal(payload)
	if err != nil {
		detail = []byte("{}")
	}
	outcome, err := g.jobs.FinishJob(ctx, FinishRequest{
		JobID: env.JobID, RunnerID: c.runnerID, Epoch: payload.LeaseEpoch,
		Status: jobFailed, Result: detail, ErrorClass: class, Now: g.now().UTC(),
	})
	if err != nil {
		log.Printf("%s: fail job %s failed: %v", c.logTag, shortID(env.JobID), err)
		return
	}
	if outcome != LeaseApplied {
		g.leaseLost(ctx, c, env.JobID)
		return
	}
	c.forgetJob(env.JobID)
	g.clearPendingCancel(env.JobID)
	g.metrics.leaseEvent("failed")
	log.Printf("%s: job %s failed code=%s class=%s retryable=%t",
		c.logTag, shortID(env.JobID), payload.Code, payload.Class, payload.Retryable)
	g.touch(ctx, c)
	g.pokeAssigners()
}

// renewLeases answers a heartbeat's running list: each valid listing renews that
// job's lease to now plus 45 seconds, and any lease the gateway no longer honours
// comes back ok:false so the runner stops it.
func (g *gateway) renewLeases(ctx context.Context, c *conn, running []protocol.JobLease) []protocol.LeaseStatus {
	statuses := make([]protocol.LeaseStatus, 0, len(running))
	until := g.now().UTC().Add(g.cfg.leaseDuration())
	for _, held := range running {
		// No queue means no lease to confirm, and an unconfirmed lease is one the
		// runner must stop: never a silent "keep working".
		outcome := LeaseRejected
		if g.jobs != nil {
			var err error
			outcome, err = g.jobs.RenewLease(ctx, held.JobID, c.runnerID, held.LeaseEpoch, until, g.now().UTC())
			if err != nil {
				log.Printf("%s: renewing lease for job %s failed: %v", c.logTag, shortID(held.JobID), err)
				outcome = LeaseRejected
			}
		}
		ok := outcome == LeaseApplied
		if ok {
			c.removeOffer(held.JobID)
			g.metrics.leaseEvent("renewed")
		}
		statuses = append(statuses, protocol.LeaseStatus{JobID: held.JobID, OK: ok})
	}
	return statuses
}

// resumeWork is the reconnect path: a runner that comes back while holding jobs
// lists them in hello.running. "If a job's lease has not expired and the epoch
// matches, the gateway keeps it, answers with welcome.resumed ... Jobs not
// resumed receive cancel and the runner MUST stop them."
//
// D5a deliberately answered every claimed job with cancel{lease_lost} because it
// held no job state; this replaces that no-op with the rule the specification
// states.
// It returns the resumed leases and the ones the gateway does not honour; the
// caller sends the cancels AFTER welcome, because welcome.resumed is what tells
// the runner which of its jobs survive.
func (g *gateway) resumeWork(ctx context.Context, c *conn, hello protocol.HelloPayload) (resumed []protocol.JobLease, notResumed []string) {
	resumed = make([]protocol.JobLease, 0, len(hello.Running))
	if len(hello.Running) == 0 {
		return resumed, nil
	}
	until := g.now().UTC().Add(g.cfg.leaseDuration())
	for _, held := range hello.Running {
		outcome := LeaseRejected
		if g.jobs != nil {
			var err error
			outcome, err = g.jobs.RenewLease(ctx, held.JobID, c.runnerID, held.LeaseEpoch, until, g.now().UTC())
			if err != nil {
				log.Printf("%s: resume of job %s failed: %v", c.logTag, shortID(held.JobID), err)
			}
		}
		if outcome == LeaseApplied {
			resumed = append(resumed, held)
			c.addSinceHeartbeat(held.JobID)
			g.metrics.leaseEvent("resumed")
			continue
		}
		// Not resumed: the runner MUST NOT keep working on a job the gateway did
		// not list in welcome.resumed, so it is told to stop once welcome is out.
		notResumed = append(notResumed, held.JobID)
		g.metrics.leaseEvent("not_resumed")
	}
	return resumed, notResumed
}

// resendPendingOps re-sends the op.request that a resumed job was in the middle
// of when its runner reconnected.
//
// "Reconnect and resume": the runner that comes back lists its jobs in
// hello.running, and the gateway re-sends any op.request that had no response.
// The worker answers a repeated opId from its own cache, so an op it already
// executed is not executed twice, and one that was still running is simply
// joined. Without this a reconnected runner would hold a session with an op the
// engine is still blocked on, and nothing would ever unblock it.
func (g *gateway) resendPendingOps(ctx context.Context, c *conn, resumed []protocol.JobLease) {
	for _, held := range resumed {
		pending := g.ops.pendingForJob(held.JobID)
		if pending == nil {
			continue
		}
		if pending.runnerID != c.runnerID {
			// The job was resumed by another device, which cannot be: a job is
			// pinned to one runner. Left registered deliberately, so the watchdog
			// releases it rather than this path guessing.
			continue
		}
		if err := c.send(ctx, pending.env); err != nil {
			log.Printf("%s: re-sending op.request %s failed: %v", c.logTag, shortID(pending.opID), err)
			continue
		}
		g.metrics.countMessage(string(protocol.TypeOpRequest), false)
		g.metrics.sessionOps("resent")
		log.Printf("%s: resumed job %s with op %s still unanswered, re-sent",
			c.logTag, shortID(held.JobID), shortID(pending.opID))
	}
}

// cancelJob asks the runner holding a job to stop, and arranges for the job to
// be marked Cancelled after the grace window if the runner does not acknowledge
// it first. "The gateway marks the job Cancelled when it receives [complete
// with outcome cancelled] or after 10 seconds, whichever comes first."
//
// The deadline is held in memory on the instance that accepted the cancellation,
// which is enough because the outcome does not depend on this replica:
//
//   - the job row is the authority, so the engine's long-poll and the reaper see
//     the same state wherever they run;
//   - a runner connected to ANOTHER replica is not sent this cancel — its next
//     heartbeat asks about the job and is answered ok:false, which is the same
//     instruction by another route;
//   - a restart inside the 10-second window loses only the deadline, and the
//     lease reaper still takes the job back 45 seconds later.
//
// Reaching another replica's socket directly is D5c's job, not this one's.
func (g *gateway) cancelJob(ctx context.Context, job Job, reason string) {
	if job.Terminal() {
		return
	}
	if job.Status == jobQueued {
		if _, _, err := g.jobs.CancelQueued(ctx, job.ID, g.now().UTC()); err != nil {
			log.Printf("cancelling queued job %s failed: %v", shortID(job.ID), err)
			return
		}
		g.metrics.leaseEvent("cancelled")
		return
	}
	grace := g.cfg.cancelGrace()
	g.addPendingCancel(pendingCancel{
		jobID: job.ID, epoch: job.LeaseEpoch, reason: reason,
		deadline: g.now().UTC().Add(grace),
	})
	// The runner that holds it may be connected to this replica.
	if c := g.registry.get(job.LeaseOwnerRunnerID); c != nil {
		env, err := protocol.NewJob(protocol.TypeCancel, job.ID, "",
			protocol.CancelPayload{Reason: reason, GraceSec: int(g.cfg.cancelGrace() / time.Second)})
		if err == nil {
			if err := c.send(ctx, env); err != nil {
				log.Printf("%s: sending cancel for job %s failed: %v", c.logTag, shortID(job.ID), err)
			} else {
				g.metrics.countMessage(string(protocol.TypeCancel), false)
			}
		}
	}
	// Whatever the engine has in flight on this job is released now rather than
	// when the runner acknowledges: the op cannot produce a usable result any
	// more, and the engine's call was counted against the cancellation window.
	if n := g.ops.abortJob(job.ID, reason, false); n > 0 {
		log.Printf("job %s cancelled: released %d in-flight session op(s)", shortID(job.ID), n)
	}
	time.AfterFunc(grace, func() {
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		g.expirePendingCancel(ctx, job.ID, job.LeaseEpoch)
	})
}

// expirePendingCancel marks a cancelled job Cancelled if the runner has not
// reported the cancellation itself.
func (g *gateway) expirePendingCancel(ctx context.Context, jobID string, epoch int64) {
	if !g.takePendingCancel(jobID) {
		return
	}
	outcome, err := g.jobs.CancelLease(ctx, jobID, epoch, g.now().UTC())
	if err != nil {
		log.Printf("cancelling job %s after the grace window failed: %v", shortID(jobID), err)
		return
	}
	if outcome == LeaseApplied {
		g.metrics.leaseEvent("cancelled")
		log.Printf("job %s cancelled after the grace window elapsed", shortID(jobID))
	}
}

// cancelRunnerWork cancels every job a runner holds and applies the lease-expiry
// rule to them. It is the revocation path: the device's socket is dropped and
// its jobs do not sit on a dead machine waiting for their lease to lapse.
func (g *gateway) cancelRunnerWork(ctx context.Context, c *conn, reason string) {
	if g.jobs == nil {
		return
	}
	jobs, err := g.jobs.ActiveJobsForRunner(ctx, c.runnerID)
	if err != nil {
		log.Printf("%s: reading the runner's jobs failed: %v", c.logTag, err)
		return
	}
	for _, job := range jobs {
		env, err := protocol.NewJob(protocol.TypeCancel, job.ID, "",
			protocol.CancelPayload{Reason: reason, GraceSec: int(g.cfg.cancelGrace() / time.Second)})
		if err == nil {
			if err := c.send(ctx, env); err != nil {
				log.Printf("%s: sending cancel for job %s failed: %v", c.logTag, shortID(job.ID), err)
			} else {
				g.metrics.countMessage(string(protocol.TypeCancel), false)
			}
		}
		// The device is being dropped, so any op the engine is waiting on is
		// released with it rather than left to time out.
		g.ops.abortJob(job.ID, reason, true)
	}
	released, err := g.jobs.ReleaseRunnerLeases(ctx, c.runnerID, g.now().UTC())
	if err != nil {
		log.Printf("%s: releasing the runner's leases failed: %v", c.logTag, err)
		return
	}
	for _, job := range released {
		g.leaseEventFor(job)
	}
}

// leaseEventFor counts what became of a lease the gateway took back.
func (g *gateway) leaseEventFor(job ExpiredJob) {
	switch job.Status {
	case jobQueued:
		g.metrics.leaseEvent("reaped_requeued")
	case jobFailed:
		g.metrics.leaseEvent("reaped_failed")
	case jobExpired:
		g.metrics.leaseEvent("reaped_expired")
	}
}
