package agent

import (
	"context"
	"encoding/json"
	"errors"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// opOutcome is one op's result, kept so that a repeated opId after a reconnect
// returns the same answer instead of running the op twice.
type opOutcome struct {
	ok     bool
	result json.RawMessage
	err    *protocol.OpError
}

// opCache is the last protocol.OpResultCacheSize op results of one session,
// plus a short memory of the ids that fell out of it.
//
// The distinction matters. A repeat of a cached id returns the cached response,
// which is what makes resuming after a reconnect safe for ops that already ran.
// A repeat of an id that has aged out is answered op_expired, which the engine
// treats as session loss. Without the second list the runner could not tell "I
// have done this" from "I have never seen this", and would silently re-run an
// op whose result it had merely forgotten.
type opCache struct {
	max        int
	order      []string
	results    map[string]opOutcome
	evicted    []string
	evictedSet map[string]struct{}
}

func newOpCache(max int) *opCache {
	if max < 1 {
		max = 1
	}
	return &opCache{
		max:        max,
		results:    make(map[string]opOutcome, max),
		evictedSet: make(map[string]struct{}, max),
	}
}

func (c *opCache) get(opID string) (opOutcome, bool) {
	out, ok := c.results[opID]
	return out, ok
}

func (c *opCache) wasEvicted(opID string) bool {
	_, ok := c.evictedSet[opID]
	return ok
}

func (c *opCache) put(opID string, out opOutcome) {
	if _, exists := c.results[opID]; !exists {
		c.order = append(c.order, opID)
	}
	c.results[opID] = out
	for len(c.order) > c.max {
		oldest := c.order[0]
		c.order = c.order[1:]
		delete(c.results, oldest)
		c.rememberEvicted(oldest)
	}
}

func (c *opCache) rememberEvicted(opID string) {
	if _, ok := c.evictedSet[opID]; ok {
		return
	}
	c.evicted = append(c.evicted, opID)
	c.evictedSet[opID] = struct{}{}
	for len(c.evicted) > c.max {
		oldest := c.evicted[0]
		c.evicted = c.evicted[1:]
		delete(c.evictedSet, oldest)
	}
}

// handleOpRequest answers one op.request. The op runs in its own goroutine so
// that a long render never delays the heartbeat, the cancel or the next read;
// the per-job mutex is what keeps a session to one op at a time, which the
// protocol requires of the gateway and the worker alike.
func (a *Agent) handleOpRequest(env protocol.Envelope) {
	payload, err := protocol.DecodePayload[protocol.OpRequestPayload](env)
	if err != nil {
		a.logf("op.request payload for job %s is not usable: %v", env.JobID, err)
		return
	}
	js := a.job(env.JobID)
	if js == nil {
		// An op for a job this runner does not hold: the lease moved, or the
		// engine is talking to a session that ended. Answer rather than stay
		// silent, so the engine does not wait for its own timeout.
		a.sendOpOutcome(env, "", opOutcome{
			err: OpError(protocol.ErrCodeCancelled, "this runner does not hold that job", false),
		})
		return
	}
	go a.executeOp(js, env, payload)
}

func (a *Agent) executeOp(js *jobState, env protocol.Envelope, payload protocol.OpRequestPayload) {
	// One op at a time per session. A duplicate opId that arrives while the
	// first copy is running blocks here and then finds the cached result.
	js.mu.Lock()
	defer js.mu.Unlock()

	if outcome, ok := js.ops.get(payload.OpID); ok {
		a.sendOpOutcome(env, payload.OpID, outcome)
		return
	}
	if js.ops.wasEvicted(payload.OpID) {
		a.sendOpOutcome(env, payload.OpID, opOutcome{
			err: OpError(protocol.ErrCodeOpExpired, "that op id is no longer in this session's result cache", false),
		})
		return
	}

	select {
	case <-js.ready:
	case <-js.ctx.Done():
		a.sendOpOutcome(env, payload.OpID, opOutcome{
			err: OpError(protocol.ErrCodeCancelled, "the job ended before this op could run", false),
		})
		return
	}
	if js.startErr != nil || js.session == nil {
		a.sendOpOutcome(env, payload.OpID, opOutcome{
			err: OpError(protocol.ErrCodeContainerUnavailable, "the job's session is not available", true),
		})
		return
	}

	ctx, cancel := a.opContext(js, payload)
	defer cancel()

	result, opErr := js.session.Handle(ctx, payload.Op, payload.Args)
	if opErr == nil && ctx.Err() != nil && js.ctx.Err() == nil {
		// The op's own deadline passed. Reported as a timeout rather than as a
		// silent empty result, because the engine decides what to do about it.
		opErr = OpError(protocol.ErrCodeTimeout, "the op exceeded its timeoutSec", true)
	}
	outcome := opOutcome{ok: opErr == nil, err: opErr}
	if opErr == nil {
		raw, err := json.Marshal(result)
		if err != nil {
			outcome = opOutcome{err: OpError(protocol.ErrCodeUnsupportedOp,
				"the op produced a result that cannot be encoded: "+err.Error(), false)}
		} else {
			outcome.result = raw
		}
	}
	js.ops.put(payload.OpID, outcome)
	a.sendOpOutcome(env, payload.OpID, outcome)

	if payload.Op == protocol.OpClose {
		// The engine ended the session: nothing else follows and the job is
		// over. No complete is sent, because the engine closing the session is
		// itself the terminal transition the specification names.
		js.job.Done()
	}
}

// opContext bounds one op. The op's own timeoutSec wins; otherwise the
// configured default applies. Either way the job's context is the parent, so a
// cancel stops the op immediately.
func (a *Agent) opContext(js *jobState, payload protocol.OpRequestPayload) (context.Context, context.CancelFunc) {
	timeout := a.cfg.OpTimeout
	if payload.TimeoutSec > 0 {
		timeout = time.Duration(payload.TimeoutSec) * time.Second
	}
	if timeout <= 0 {
		return context.WithCancel(js.ctx)
	}
	return context.WithTimeout(js.ctx, timeout)
}

// sendOpOutcome writes an op.response on whatever connection is current.
//
// A response produced just before a disconnect is lost, and that is correct:
// the gateway re-sends the op.request on the new connection and the op cache
// answers it without running the op a second time.
func (a *Agent) sendOpOutcome(env protocol.Envelope, opID string, outcome opOutcome) {
	c := a.currentConn()
	if c == nil {
		return
	}
	js := a.job(env.JobID)
	if js == nil {
		// A job we do not hold. There is no epoch or token to echo, so the
		// gateway would fence the answer anyway; staying silent is honest and
		// keeps a spoofed job id from getting a signed-looking response.
		return
	}
	payload := protocol.OpResponsePayload{
		LeaseEpoch: js.job.LeaseEpoch,
		JobToken:   js.job.JobToken,
		OpID:       opID,
		OK:         outcome.ok,
		Result:     outcome.result,
		Error:      outcome.err,
	}
	if !FitsInline(payload) {
		// An oversize frame would make the gateway close the socket (1009) and
		// the op would hang to op_timeout; answer with a small error instead.
		payload.OK = false
		payload.Result = nil
		payload.Error = OpError(protocol.ErrCodeFileTooLarge, "the op result would not fit one payload once encoded", false)
	}
	if err := c.sendJob(protocol.TypeOpResponse, env.JobID, env.SessionID, payload); err != nil && !errors.Is(err, ErrNotConnected) {
		a.logf("op.response for job %s did not reach the gateway: %v", env.JobID, err)
	}
}
