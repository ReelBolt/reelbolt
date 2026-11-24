package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/mux"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// This file is D5c: request/response ops from the engine to a runner's socket
// through ANY gateway replica, plus the scaling signal.
//
//	POST /internal/v1/sessions/{id}/ops   one op, request and response
//	GET  /internal/v1/scaling             the desired CloudPool slot count
//
// The routing seam is runner_sessions.gateway_instance, which D5b populates at
// assignment: the session is pinned to one runner and one gateway pod for its
// whole life, so "where is this socket?" is a column read, not a shared cache.
// A replica that does not hold the session forwards the request over the
// headless service to the pod the column names; nothing needs Redis and no
// replica needs to know the fleet.
//
// Three things this file deliberately does NOT do:
//
//   - It does not create jobs, leases or sessions. D5b owns those, and a session
//     that does not exist is answered 404 rather than created on demand.
//   - It does not fix the schema's one-open-session-per-execution rule
//     (ix_runner_sessions_execution_id). Two concurrent sessions for one
//     execution cannot both have a row; making the engine serialise them is D6's
//     job. This surface reports what the schema actually holds.
//   - It does not treat a connected runner as available capacity. A runner
//     advertising zero job types (no ffmpeg, say) still connects, and counting
//     its slots would overstate the pool to the scaler. See runnerCapacity.

// ErrSessionNotFound is returned when no runner_sessions row matches.
var ErrSessionNotFound = errors.New("gateway: session not found")

// proxiedHeader marks a request that another replica has already forwarded, so
// the surface can never bounce one op around a ring of replicas. Its value is
// the instance that forwarded it, which is what makes a misroute legible.
const proxiedHeader = "X-Reelbolt-Proxied-By"

// proxiedToHeader names the instance that actually served a proxied op.
const proxiedToHeader = "X-Reelbolt-Proxied-To"

// maxSessionOpBody bounds the op request body. It is one protocol payload plus
// the JSON envelope around it: an op's args are bounded by the same 4 MiB the
// socket enforces, and the engine is expected to stage bytes through the object
// store rather than inline them.
const maxSessionOpBody = protocol.MaxPayloadBytes + 4096

// sessionOpSettleGrace is how long a pending op is left registered after its own
// deadline, so a response that arrives late is still correlated rather than
// logged as unmatched.
const sessionOpSettleGrace = 5 * time.Second

// RunnerSession is the gateway's view of one runner_sessions row.
//
// The gateway never migrates this table (the WorkflowEngine's EF migrations own
// it) and writes only the columns D5b's assignment sets plus last_op_at.
type RunnerSession struct {
	ID              string
	JobID           string
	ExecutionID     string
	RunnerID        string
	GatewayInstance string
	OpenedAt        time.Time
	LastOpAt        time.Time
	ClosedAt        time.Time
}

// Open reports whether the session is still open.
func (s RunnerSession) Open() bool { return s.ClosedAt.IsZero() }

const sessionColumns = `id::text, job_id::text, execution_id::text, runner_id::text,
	coalesce(gateway_instance, ''), opened_at, last_op_at, closed_at`

// SessionByID reads one session by its id.
func (s *PostgresJobStore) SessionByID(ctx context.Context, sessionID string) (RunnerSession, error) {
	if !validRunnerID(sessionID) {
		return RunnerSession{}, ErrSessionNotFound
	}
	rows, err := s.pool.Query(ctx, "SELECT "+sessionColumns+" FROM runner_sessions WHERE id = $1::uuid", sessionID)
	if err != nil {
		return RunnerSession{}, fmt.Errorf("read runner_sessions: %w", err)
	}
	defer rows.Close()
	if !rows.Next() {
		if err := rows.Err(); err != nil {
			return RunnerSession{}, fmt.Errorf("read runner_sessions: %w", err)
		}
		return RunnerSession{}, ErrSessionNotFound
	}
	session, err := scanSessionRow(rows)
	if err != nil {
		return RunnerSession{}, err
	}
	return session, rows.Err()
}

// TouchSessionOp records that an op ran on the session, which is what the idle
// rule reads ("Session limits" in docs/runner-protocol.md). It is best effort:
// an op's result must not depend on a bookkeeping write, so the caller logs and
// carries on.
func (s *PostgresJobStore) TouchSessionOp(ctx context.Context, sessionID string, now time.Time) error {
	if !validRunnerID(sessionID) {
		return nil
	}
	_, err := s.pool.Exec(ctx,
		"UPDATE runner_sessions SET last_op_at = $2 WHERE id = $1::uuid", sessionID, now)
	if err != nil {
		return fmt.Errorf("touch runner_sessions: %w", err)
	}
	return nil
}

// PoolWorkload counts the CloudPool work that exists right now: jobs waiting for
// a slot and jobs holding one. It is what GET /internal/v1/scaling turns into a
// desired slot count.
type PoolWorkload struct {
	Queued  int64
	Running int64
	// Orphaned is the part of Running that no connected worker is serving: an
	// Assigned or Running job leased by this gateway instance to a runner that is
	// no longer connected to it and has stopped renewing. It is not running
	// anywhere; it waits for a worker (the runner returns, or the lease lapses and
	// the reaper requeues the job, or expires it when it is a session job). It counts as demand, so a job that was assigned
	// before, and is reused or retried at attempt 1 or more, wakes a node just like
	// a first-time job.
	Orphaned int64
}

// orphanGrace is how long a lease must have gone without a renewal before its
// job counts as orphaned: a runner that drops and reconnects inside it keeps its
// job and must not cost a server.
const orphanGrace = 30 * time.Second

// PoolWorkload reads the CloudPool backlog. OrgRunner jobs are excluded: the
// scaling signal is about the pool we own, and a customer's desktop is not
// something a replica count can create.
//
// instance and connectedRunnerIDs name this replica and the runners connected
// to it; staleBefore is the lease expiry below which a lease has stopped being
// renewed. Orphaned is only judged for this replica's own leases: another
// replica's runners are not visible here.
func (s *PostgresJobStore) PoolWorkload(ctx context.Context, instance string, connectedRunnerIDs []string, staleBefore time.Time) (PoolWorkload, error) {
	var out PoolWorkload
	if connectedRunnerIDs == nil {
		connectedRunnerIDs = []string{}
	}
	err := s.pool.QueryRow(ctx, "SELECT "+
		"count(*) FILTER (WHERE status = $1), "+
		"count(*) FILTER (WHERE status IN ($2, $3)), "+
		"count(*) FILTER (WHERE status IN ($2, $3) AND coalesce(gateway_instance, '') = $5 "+
		"AND NOT (coalesce(lease_owner_runner_id::text, '') = ANY($6::text[])) "+
		"AND (lease_expires_at IS NULL OR lease_expires_at < $7)) "+
		"FROM runner_jobs WHERE target = $4",
		jobQueued, jobAssigned, jobRunning, targetCloudPool, instance, connectedRunnerIDs, staleBefore).
		Scan(&out.Queued, &out.Running, &out.Orphaned)
	if err != nil {
		return PoolWorkload{}, fmt.Errorf("pool workload: %w", err)
	}
	return out, nil
}

func scanSessionRow(row rowScanner) (RunnerSession, error) {
	var (
		session RunnerSession
		lastOp  *time.Time
		closed  *time.Time
	)
	err := row.Scan(&session.ID, &session.JobID, &session.ExecutionID, &session.RunnerID,
		&session.GatewayInstance, &session.OpenedAt, &lastOp, &closed)
	if err != nil {
		return RunnerSession{}, fmt.Errorf("scan runner_sessions: %w", err)
	}
	if lastOp != nil {
		session.LastOpAt = lastOp.UTC()
	}
	if closed != nil {
		session.ClosedAt = closed.UTC()
	}
	session.OpenedAt = session.OpenedAt.UTC()
	return session, nil
}

// --- op correlation ---------------------------------------------------------

// opOutcome is what the waiting handler receives: the runner's answer, or the
// reason the op will never be answered.
type opOutcome struct {
	payload *protocol.OpResponsePayload
	reason  string
	// sessionLost marks an abort the engine must treat as session loss (open a
	// new session, or fail the step) rather than as a plain cancellation.
	sessionLost bool
}

// pendingOp is one op.request that has been forwarded and not yet answered.
type pendingOp struct {
	opID      string
	jobID     string
	sessionID string
	runnerID  string
	// env is the exact envelope that was sent, kept so a runner that reconnects
	// can be sent the same op.request again ("Reconnect and resume" in
	// docs/runner-protocol.md). The worker answers a repeated opId from its own
	// cache, which is what makes the re-send safe.
	env     protocol.Envelope
	started time.Time
	// ch carries exactly one outcome and is buffered, so whoever holds the entry
	// under the table's lock can publish without ever blocking.
	ch chan opOutcome
}

// opTable correlates an opId with the engine request waiting for it.
//
// Three indexes over one entry: by opId (the runner's answer names only that),
// by session (the specification allows one op in flight per session, so a second
// request is refused rather than queued), and by job (a job that ends or loses
// its lease must release whatever is waiting on it, and the caller only knows
// the job).
type opTable struct {
	mu        sync.Mutex
	byOpID    map[string]*pendingOp
	bySession map[string]*pendingOp
	byJob     map[string]*pendingOp
}

func newOpTable() *opTable {
	return &opTable{
		byOpID:    map[string]*pendingOp{},
		bySession: map[string]*pendingOp{},
		byJob:     map[string]*pendingOp{},
	}
}

// begin registers an op and reports whether it may be forwarded. It refuses when
// the session already has an op in flight ("A session handles one op at a time")
// or when the same opId is already registered, and returns the entry that is in
// the way so the caller can report which op it collided with.
func (t *opTable) begin(p *pendingOp) (*pendingOp, bool) {
	t.mu.Lock()
	defer t.mu.Unlock()
	if existing := t.byOpID[p.opID]; existing != nil {
		return existing, false
	}
	if existing := t.bySession[p.sessionID]; existing != nil {
		return existing, false
	}
	t.byOpID[p.opID] = p
	t.bySession[p.sessionID] = p
	t.byJob[p.jobID] = p
	return nil, true
}

// take removes an op, reporting nil when something else already claimed it (a
// delivered answer, an abort, or the expiry watchdog).
func (t *opTable) take(opID string) *pendingOp {
	t.mu.Lock()
	defer t.mu.Unlock()
	p := t.byOpID[opID]
	if p == nil {
		return nil
	}
	t.removeLocked(p)
	return p
}

// deliver hands an op.response to the waiter. It reports false when nobody is
// waiting: a repeated response, or an answer for an op the engine already gave
// up on. Publishing happens under the lock, and the channel is buffered, so a
// caller that observes "take returned nil" knows the value is already in the
// channel rather than still in flight.
func (t *opTable) deliver(opID string, payload protocol.OpResponsePayload) bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	p := t.byOpID[opID]
	if p == nil {
		return false
	}
	t.removeLocked(p)
	p.ch <- opOutcome{payload: &payload}
	return true
}

// abortJob releases whatever is waiting on a job, because the job can no longer
// answer: it was cancelled, it ended, or its lease was taken back.
func (t *opTable) abortJob(jobID, reason string, sessionLost bool) int {
	t.mu.Lock()
	defer t.mu.Unlock()
	p := t.byJob[jobID]
	if p == nil {
		return 0
	}
	t.removeLocked(p)
	p.ch <- opOutcome{reason: reason, sessionLost: sessionLost}
	return 1
}

// pendingForJob returns the op in flight for a job, or nil.
func (t *opTable) pendingForJob(jobID string) *pendingOp {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.byJob[jobID]
}

// size counts the ops currently in flight, for tests and the health payload.
func (t *opTable) size() int {
	t.mu.Lock()
	defer t.mu.Unlock()
	return len(t.byOpID)
}

func (t *opTable) removeLocked(p *pendingOp) {
	delete(t.byOpID, p.opID)
	if t.bySession[p.sessionID] == p {
		delete(t.bySession, p.sessionID)
	}
	if t.byJob[p.jobID] == p {
		delete(t.byJob, p.jobID)
	}
}

// --- the engine-facing session surface --------------------------------------

// sessionOpRequest is POST /internal/v1/sessions/{id}/ops.
type sessionOpRequest struct {
	// OpID is unique within the session. The gateway mints one when the engine
	// does not, so a replayed request still correlates.
	OpID string `json:"opId,omitempty"`
	Op   string `json:"op"`
	// Args is the op's payload. Its shape depends on Op; the gateway never reads
	// it, because only the worker may interpret an op's arguments.
	Args json.RawMessage `json:"args,omitempty"`
	// TimeoutSec bounds this op. Zero means the job's own timeout ("exec and
	// render up to the job timeout").
	TimeoutSec int `json:"timeoutSec,omitempty"`
}

// sessionOpResult is the successful shape of an op: the runner's op.response,
// passed through.
type sessionOpResult struct {
	SessionID  string            `json:"sessionId"`
	JobID      string            `json:"jobId"`
	OpID       string            `json:"opId"`
	OK         bool              `json:"ok"`
	Result     json.RawMessage   `json:"result,omitempty"`
	Error      *protocol.OpError `json:"error,omitempty"`
	DurationMs int64             `json:"durationMs"`
	ServedBy   string            `json:"servedBy"`
}

func (g *gateway) handleSessionOp(w http.ResponseWriter, r *http.Request) {
	started := g.now().UTC()
	if g.jobs == nil {
		writeJSON(w, http.StatusServiceUnavailable, map[string]any{
			"error": "internal_api_disabled", "message": "this gateway has no job queue attached",
		})
		return
	}
	sessionID := mux.Vars(r)["id"]
	if !validRunnerID(sessionID) {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "bad_request", "message": "id must be a uuid"})
		return
	}
	body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, maxSessionOpBody))
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "bad_request", "message": err.Error()})
		return
	}
	var req sessionOpRequest
	if err := json.Unmarshal(body, &req); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "bad_request", "message": err.Error()})
		return
	}
	req.Op = strings.TrimSpace(req.Op)
	if req.Op == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "bad_request", "message": "op is required"})
		return
	}
	opID := strings.TrimSpace(req.OpID)
	if opID == "" {
		opID = uuid.NewString()
	}
	if len(opID) > 200 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "bad_request", "message": "opId is longer than 200 bytes"})
		return
	}
	if len(req.Args) == 0 {
		req.Args = json.RawMessage("{}")
	}

	session, err := g.jobs.SessionByID(r.Context(), sessionID)
	if err != nil {
		if errors.Is(err, ErrSessionNotFound) {
			writeJSON(w, http.StatusNotFound, map[string]any{
				"error": "session_not_found", "sessionId": sessionID,
			})
			return
		}
		log.Printf("internal api: reading session %s failed: %v", shortID(sessionID), err)
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "internal"})
		return
	}

	// The live socket is the truth about who can serve an op, and
	// gateway_instance is only where the session was opened: a runner that
	// reconnected to another replica has its connection there, not on the pod the
	// column still names. Serving it here is what makes resume work for ops.
	if c := g.registry.get(session.RunnerID); c != nil {
		g.runSessionOp(w, r, session, c, req, opID, started)
		return
	}

	// Not here. Forward to the replica that owns the session, unless this request
	// is itself a forwarded one: one hop, never a ring.
	if r.Header.Get(proxiedHeader) == "" && session.GatewayInstance != "" && session.GatewayInstance != g.cfg.GatewayInstance {
		g.proxySessionOp(w, r, session, body, req)
		return
	}
	g.writeSessionLost(w, session, opID, "runner_offline",
		fmt.Sprintf("no live connection for runner %s on %s", shortID(session.RunnerID), g.cfg.GatewayInstance))
}

// runSessionOp forwards one op to a runner connected to this replica and waits
// for the answer.
func (g *gateway) runSessionOp(
	w http.ResponseWriter,
	r *http.Request,
	session RunnerSession,
	c *conn,
	req sessionOpRequest,
	opID string,
	started time.Time,
) {
	ctx := r.Context()
	job, err := g.jobs.GetJob(ctx, session.JobID)
	if err != nil {
		if errors.Is(err, ErrJobNotFound) {
			g.writeSessionLost(w, session, opID, "job_not_found", "the session's job no longer exists")
			return
		}
		log.Printf("internal api: reading job %s failed: %v", shortID(session.JobID), err)
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "internal"})
		return
	}
	// A closed session, a terminal job, a job nobody holds and a job another
	// runner now holds are all the same answer to the engine: this session is
	// gone and must be re-opened (or the step failed).
	if !session.Open() {
		g.writeSessionLost(w, session, opID, "session_closed", "the session is closed")
		return
	}
	if job.Terminal() || !job.Leased() || job.LeaseOwnerRunnerID != session.RunnerID {
		g.writeSessionLost(w, session, opID, "lease_lost",
			fmt.Sprintf("job %s is %s and not held by this session's runner", shortID(job.ID), job.Status))
		return
	}

	timeout := g.opTimeout(job, c.caps, req.TimeoutSec)
	p := &pendingOp{
		opID: opID, jobID: session.JobID, sessionID: session.ID, runnerID: session.RunnerID,
		started: started, ch: make(chan opOutcome, 1),
	}
	if existing, ok := g.ops.begin(p); !ok {
		// "The gateway MUST answer a second concurrent op request with HTTP 409
		// op_in_flight and not forward it."
		g.metrics.sessionOps("op_in_flight")
		writeJSON(w, http.StatusConflict, map[string]any{
			"error": "op_in_flight", "sessionId": session.ID, "opId": existing.opID,
			"message": "the session already has an op in flight",
		})
		return
	}

	env, err := protocol.NewJob(protocol.TypeOpRequest, session.JobID, session.ID,
		protocol.OpRequestPayload{
			OpID:       opID,
			Op:         req.Op,
			Args:       req.Args,
			TimeoutSec: int(timeout / time.Second),
		})
	if err != nil {
		g.ops.take(opID)
		log.Printf("%s: building op.request %s failed: %v", c.logTag, shortID(opID), err)
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "internal"})
		return
	}
	p.env = env

	sendCtx, cancelSend := context.WithTimeout(ctx, g.writeTimeout)
	err = c.send(sendCtx, env)
	cancelSend()
	if err != nil {
		g.ops.take(opID)
		log.Printf("%s: sending op.request %s failed: %v", c.logTag, shortID(opID), err)
		g.writeSessionLost(w, session, opID, "runner_offline", "the connection could not be written to")
		return
	}
	g.metrics.countMessage(string(protocol.TypeOpRequest), false)
	g.metrics.sessionOps("request")
	g.touchSessionOp(ctx, session.ID)

	// The watchdog is hygiene: it releases the entry when every waiter has gone
	// away, so a session cannot be wedged by an abandoned op.
	watchdog := time.AfterFunc(timeout+sessionOpSettleGrace, func() {
		if g.ops.take(opID) != nil {
			g.metrics.sessionOps("abandoned")
		}
	})
	defer watchdog.Stop()

	timer := time.NewTimer(timeout)
	defer timer.Stop()
	select {
	case outcome := <-p.ch:
		g.writeOpOutcome(w, session, opID, req.Op, outcome, started)
	case <-timer.C:
		if g.ops.take(opID) == nil {
			// An answer won the race; it is already in the channel.
			g.writeOpOutcome(w, session, opID, req.Op, <-p.ch, started)
			return
		}
		g.metrics.sessionOps("timeout")
		writeJSON(w, http.StatusGatewayTimeout, map[string]any{
			"error": "op_timeout", "sessionId": session.ID, "opId": opID,
			"timeoutSec": int(timeout / time.Second),
			"message":    "the runner did not answer in time; the session may still be running the op",
		})
	case <-ctx.Done():
		// The engine gave up. The entry is left registered on purpose: the worker
		// may still answer, and its answer must free the session rather than be
		// logged as unmatched. The watchdog above is the backstop.
		g.metrics.sessionOps("caller_gone")
	}
}

// writeOpOutcome turns what the waiter received into an HTTP answer.
//
// op is the op being answered, because one of them ends the job: see
// finishClosedSession.
func (g *gateway) writeOpOutcome(w http.ResponseWriter, session RunnerSession, opID, op string, outcome opOutcome, started time.Time) {
	duration := g.now().UTC().Sub(started).Milliseconds()
	if outcome.payload != nil {
		// A failed op is a successful round trip: ok:false carrying the worker's
		// own error is a 200, exactly as op.response defines it. Only the
		// transport failing is an HTTP error.
		if outcome.payload.OK {
			g.metrics.sessionOps("ok")
		} else {
			g.metrics.sessionOps("op_error")
		}
		writeJSON(w, http.StatusOK, sessionOpResult{
			SessionID: session.ID, JobID: session.JobID, OpID: opID,
			OK: outcome.payload.OK, Result: outcome.payload.Result, Error: outcome.payload.Error,
			DurationMs: duration, ServedBy: g.cfg.GatewayInstance,
		})
		// A successful close is the engine ending the session, and the
		// specification says that is how a session's job ends ("The job ends with
		// complete ..., fail, or by the engine closing the session"). The runner
		// deliberately sends nothing for it — pkg/agent's finishJob says so — so
		// this is the ONLY place that transition can happen. Without it the job
		// stays Running until the lease expires and the reaper calls it Expired,
		// and the engine is told session_lost for a step that actually succeeded.
		if outcome.payload.OK && op == protocol.OpClose {
			g.finishClosedSession(session)
		}
		return
	}
	if outcome.sessionLost {
		g.metrics.sessionOps("session_lost")
		g.writeSessionLost(w, session, opID, outcome.reason, "the op was released because the session ended")
		return
	}
	g.metrics.sessionOps("cancelled")
	writeJSON(w, http.StatusConflict, map[string]any{
		"error": "op_cancelled", "reason": outcome.reason,
		"sessionId": session.ID, "opId": opID,
	})
}

// finishClosedSession ends the job of a session the engine closed.
//
// It is the counterpart of handleComplete for the case the protocol routes
// through the close op instead of through a runner-sent message, and it runs
// under a fresh context: the caller's request may be gone by now (the engine got
// its answer and left), and a terminal transition must not depend on that.
//
// It never fails the op: a job that could not be finished is logged and left to
// the reaper, which is the same treatment a lost connection gets. It also does
// not abort the job's pending ops: the gateway allows one op in flight per
// session, the close is it, and its entry has already been taken by the time the
// answer is written.
func (g *gateway) finishClosedSession(session RunnerSession) {
	if g.jobs == nil {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	job, err := g.jobs.GetJob(ctx, session.JobID)
	if err != nil {
		log.Printf("internal api: closing session %s: reading job %s failed: %v",
			shortID(session.ID), shortID(session.JobID), err)
		return
	}
	if job.Terminal() {
		// Already ended: a cancel or a fail won the race. Nothing to do.
		return
	}
	outcome, err := g.jobs.FinishJob(ctx, FinishRequest{
		JobID:    job.ID,
		RunnerID: session.RunnerID,
		Epoch:    job.LeaseEpoch,
		Status:   jobSucceeded,
		// No result: the engine's own copy of the session's output is the
		// authority, and inventing a result field here would put a second,
		// less-informed answer on the wire.
		Now: g.now().UTC(),
	})
	if err != nil || outcome != LeaseApplied {
		log.Printf("internal api: closing session %s did not finish job %s (outcome=%v, err=%v); the reaper will expire it",
			shortID(session.ID), shortID(job.ID), outcome, err)
		return
	}
	g.clearPendingCancel(job.ID)
	g.metrics.leaseEvent(jobSucceeded)
	log.Printf("session %s closed by the engine: job %s is %s", shortID(session.ID), shortID(job.ID), jobSucceeded)
	g.pokeAssigners()
}

// writeSessionLost answers "this session cannot serve an op". 410 rather than
// 404: the session existed and is gone, which is what tells the engine to open a
// new one (with journal hydration for Remotion) or fail the step.
func (g *gateway) writeSessionLost(w http.ResponseWriter, session RunnerSession, opID, reason, detail string) {
	writeJSON(w, http.StatusGone, map[string]any{
		"error": "session_lost", "reason": reason, "message": detail,
		"sessionId": session.ID, "jobId": session.JobID, "opId": opID,
	})
}

// proxySessionOp forwards an op to the replica named by
// runner_sessions.gateway_instance over the headless service. The response is
// passed through verbatim: the owner is the only replica that can answer, so
// anything this replica said about it would be an invention.
func (g *gateway) proxySessionOp(
	w http.ResponseWriter,
	r *http.Request,
	session RunnerSession,
	body []byte,
	req sessionOpRequest,
) {
	target, err := gatewayEndpoint(session.GatewayInstance, g.cfg.InternalPort)
	if err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error": "session_owner_unreachable", "message": err.Error(), "sessionId": session.ID,
		})
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), g.proxyDeadline(req))
	defer cancel()
	url := "http://" + target + "/internal/v1/sessions/" + session.ID + "/ops"
	forward, err := http.NewRequestWithContext(ctx, http.MethodPost, url, bytes.NewReader(body))
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "internal"})
		return
	}
	forward.Header.Set("Content-Type", "application/json")
	forward.Header.Set(internalTokenHeader, "Bearer "+g.cfg.InternalToken)
	forward.Header.Set(proxiedHeader, g.cfg.GatewayInstance)

	resp, err := g.proxyClient.Do(forward)
	if err != nil {
		g.metrics.sessionOps("proxy_failed")
		log.Printf("internal api: proxying session %s to %s failed: %v", shortID(session.ID), target, err)
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error": "session_owner_unreachable", "message": err.Error(),
			"sessionId": session.ID, "owner": session.GatewayInstance,
		})
		return
	}
	defer resp.Body.Close()
	g.metrics.sessionOps("proxy_forwarded")
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set(proxiedToHeader, target)
	w.WriteHeader(resp.StatusCode)
	if _, err := io.Copy(w, io.LimitReader(resp.Body, maxSessionOpBody)); err != nil {
		log.Printf("internal api: relaying the op answer from %s failed: %v", target, err)
	}
}

// proxyDeadline bounds the hop. It is deliberately longer than the op's own
// deadline: the owning replica must be the one that reports the timeout, because
// it is the one that knows whether the runner is still working.
func (g *gateway) proxyDeadline(req sessionOpRequest) time.Duration {
	sec := req.TimeoutSec
	if sec <= 0 || sec > jobTimeoutMaxSec {
		sec = jobTimeoutDefaultSec
	}
	return time.Duration(sec)*time.Second + g.cfg.ProxyTimeout
}

// gatewayEndpoint turns a gateway_instance into a dialable host:port. The column
// holds a pod IP (the downward API) in a real deployment, and a host or
// host:port in a test; a bare host takes the configured internal port.
func gatewayEndpoint(instance, defaultPort string) (string, error) {
	instance = strings.TrimSpace(instance)
	if instance == "" {
		return "", errors.New("the session names no gateway instance")
	}
	if host, port, err := net.SplitHostPort(instance); err == nil {
		if host == "" || port == "" {
			return "", fmt.Errorf("gateway instance %q has no host", instance)
		}
		return net.JoinHostPort(host, port), nil
	}
	port := strings.TrimSpace(defaultPort)
	if port == "" {
		port = "8090"
	}
	return net.JoinHostPort(instance, port), nil
}

// resolveOpTimeout is the deadline of one op: the engine's own value when it
// gave one, otherwise the job's timeout ("exec and render up to the job
// timeout"), always capped by the protocol's maximum and by the operator's
// optional cap.
func (g *gateway) opTimeout(job Job, caps protocol.Capabilities, requested int) time.Duration {
	jobTimeoutSec, _, _ := g.assignLimits(job, caps)
	sec := requested
	if sec <= 0 || sec > jobTimeoutMaxSec {
		sec = jobTimeoutSec
	}
	if g.cfg.OpTimeoutSec > 0 && g.cfg.OpTimeoutSec < sec {
		sec = g.cfg.OpTimeoutSec
	}
	if sec <= 0 {
		sec = jobTimeoutDefaultSec
	}
	return time.Duration(sec) * time.Second
}

// touchSessionOp refreshes last_op_at. It never fails an op.
func (g *gateway) touchSessionOp(ctx context.Context, sessionID string) {
	writeCtx, cancel := context.WithTimeout(context.WithoutCancel(ctx), 5*time.Second)
	defer cancel()
	if err := g.jobs.TouchSessionOp(writeCtx, sessionID, g.now().UTC()); err != nil {
		log.Printf("internal api: recording last_op_at for session %s failed: %v", shortID(sessionID), err)
	}
}

// --- the scaling signal -----------------------------------------------------

// scalingResponse is GET /internal/v1/scaling.
//
// desiredCloudPoolSlots is the plan's formula, queued + running + buffer, and it
// is what a pool autoscaler sets its replica count from. Every other field is
// context: the per-instance numbers are this replica's view of its own sockets,
// which is the honest scope of an in-memory registry.
type scalingResponse struct {
	DesiredCloudPoolSlots int   `json:"desiredCloudPoolSlots"`
	Queued                int64 `json:"queued"`
	// Orphaned is the part of Running whose worker is gone (see PoolWorkload).
	// AwaitingWorker = Queued + Orphaned: every job waiting for a pool worker,
	// whether it is queued for the first time, requeued after a failure, or
	// leased to a worker that has disappeared. This is the waker's demand.
	Orphaned       int64 `json:"orphaned"`
	AwaitingWorker int64 `json:"awaitingWorker"`
	Running        int64 `json:"running"`
	Buffer         int   `json:"buffer"`

	ConnectedCloudPool int `json:"connectedCloudPool"`
	// AvailableCloudPool excludes the runners that advertise no job types at all:
	// a runner with no ffmpeg and no container connects, sits in the connected
	// count, and can take nothing. Counting it as capacity would scale the pool
	// down on the strength of a machine that is idle because it is useless.
	AvailableCloudPool int `json:"availableCloudPool"`
	// FreeSlotsCloudPool and FreeSlotsOrgRunner count this replica's runners only.
	// They exclude draining runners, and advertise no more than each runner's own
	// free-slot count minus the offers it has not answered.
	FreeSlotsCloudPool int `json:"freeSlotsCloudPool"`
	FreeSlotsOrgRunner int `json:"freeSlotsOrgRunner"`
	// RunnersWithoutJobTypes and DrainingRunners account for every connected
	// runner that is NOT in AvailableCloudPool, so a zero there is not the only
	// way to tell "nothing is available" from "nothing is connected".
	RunnersWithoutJobTypes int    `json:"runnersWithoutJobTypes"`
	DrainingRunners        int    `json:"drainingRunners"`
	GatewayInstance        string `json:"gatewayInstance"`
}

// handleScaling answers "how many CloudPool slots should exist?".
func (g *gateway) handleScaling(w http.ResponseWriter, r *http.Request) {
	if g.jobs == nil {
		writeJSON(w, http.StatusServiceUnavailable, map[string]any{
			"error": "internal_api_disabled", "message": "this gateway has no job queue attached",
		})
		return
	}
	var connectedIDs []string
	for _, c := range g.registry.snapshot() {
		connectedIDs = append(connectedIDs, c.runnerID)
	}
	staleBefore := g.now().UTC().Add(g.cfg.leaseDuration() - orphanGrace)
	work, err := g.jobs.PoolWorkload(r.Context(), g.cfg.GatewayInstance, connectedIDs, staleBefore)
	if err != nil {
		log.Printf("internal api: reading the pool workload failed: %v", err)
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "internal"})
		return
	}
	capacity := g.runnerCapacity()
	writeJSON(w, http.StatusOK, scalingResponse{
		DesiredCloudPoolSlots:  int(work.Queued+work.Running) + g.cfg.PoolBufferSlots,
		Queued:                 work.Queued,
		Orphaned:               work.Orphaned,
		AwaitingWorker:         work.Queued + work.Orphaned,
		Running:                work.Running,
		Buffer:                 g.cfg.PoolBufferSlots,
		ConnectedCloudPool:     capacity.connected[protocol.KindCloudPool],
		AvailableCloudPool:     capacity.usable[protocol.KindCloudPool],
		FreeSlotsCloudPool:     capacity.free[targetCloudPool],
		FreeSlotsOrgRunner:     capacity.free[targetOrgRunner],
		RunnersWithoutJobTypes: capacity.withoutJobTypes,
		DrainingRunners:        capacity.draining,
		GatewayInstance:        g.cfg.GatewayInstance,
	})
}

// runnerCapacity is this replica's view of the runners connected to it. It is
// the single source of truth for both the free-slot gauge and the scaling
// endpoint, so the metric a scaler scrapes and the number the endpoint returns
// cannot disagree.
type runnerCapacity struct {
	connected map[string]int
	usable    map[string]int
	free      map[string]int
	// withoutJobTypes counts connections that advertise nothing at all. A runner
	// in that state is connected and useless, and it is reported rather than
	// silently dropped.
	withoutJobTypes int
	draining        int
}

func (g *gateway) runnerCapacity() runnerCapacity {
	capacity := runnerCapacity{
		connected: map[string]int{protocol.KindDesktop: 0, protocol.KindCloudPool: 0},
		usable:    map[string]int{protocol.KindDesktop: 0, protocol.KindCloudPool: 0},
		free:      map[string]int{targetCloudPool: 0, targetOrgRunner: 0},
	}
	for _, c := range g.registry.snapshot() {
		target, err := targetForKind(c.kind)
		if err != nil {
			continue
		}
		snap := c.snapshot()
		capacity.connected[c.kind]++
		if len(snap.Caps.JobTypes) == 0 {
			capacity.withoutJobTypes++
			continue
		}
		if snap.Draining {
			// A draining runner takes no new work: its slots are not capacity,
			// however free they look.
			capacity.draining++
			continue
		}
		capacity.usable[c.kind]++
		// A slot promised to an unanswered offer is not free, and reporting it as
		// free would overstate the pool to the scaler.
		if available := snap.FreeSlots - len(snap.Offers); available > 0 {
			capacity.free[target] += available
		}
	}
	return capacity
}

// updateConnectedGauges republishes reelbolt_runner_connected{kind} and
// reelbolt_runner_free_slots{target}.
//
// The free-slot gauge is D5b's registration, read here rather than re-registered
// (a second MustRegister of the same name panics). What D5c adds is what the
// number MEANS: a runner that advertises no job types, or that is draining, has
// no capacity to report, and counting its slots is how a scaler talks itself
// into shrinking the pool while work is waiting.
func (g *gateway) updateConnectedGauges() {
	capacity := g.runnerCapacity()
	for _, kind := range []string{protocol.KindDesktop, protocol.KindCloudPool} {
		g.metrics.setConnected(kind, capacity.connected[kind])
	}
	for target, n := range capacity.free {
		g.metrics.setFreeSlots(target, n)
	}
}
