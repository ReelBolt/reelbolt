package main

import (
	"context"
	"net/http"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// The specification ends a session's job three ways: "complete (outcome
// succeeded or cancelled), fail, or by the engine closing the session". The
// runner deliberately sends nothing for the third — pkg/agent's finishJob
// documents that the close op "is itself the terminal transition" — so the
// gateway is the only place that transition can happen.
//
// D13 is what found the gap: it is the first thing to drive a real close op end
// to end, and the smoke test showed a job sitting in Running after a perfectly
// successful session until the lease expired and the reaper called it Expired.
// The engine would have been told session_lost for a step that worked.
//
// opRefuse answers every op with a worker-side failure, which is how the second
// test checks the transition is wired to "a close succeeded" rather than to "a
// close was attempted".
func opRefuse(code string) func(protocol.OpRequestPayload) (protocol.OpResponsePayload, bool) {
	return func(p protocol.OpRequestPayload) (protocol.OpResponsePayload, bool) {
		return protocol.OpResponsePayload{
			OpID:  p.OpID,
			OK:    false,
			Error: &protocol.OpError{Code: code, Message: "the worker refused this op"},
		}, true
	}
}

// TestClosingASessionFinishesTheJob is the acceptance test for the transition
// nothing implemented: close, and the job is Succeeded with no further message
// from the runner.
func TestClosingASessionFinishesTheJob(t *testing.T) {
	qg := newQueueGateway(t, nil)
	r := qg.newQueueRunner(t, testOrgA)
	session := claimSession(t, qg, r, protocol.JobVideoCompile)
	runner := startFakeRunner(t, session.ws, session.token, session.epoch, opEcho("{}"))

	status, body, _ := postOp(t, qg, session.sessionID,
		"{\"opId\":\"op-close-1\",\"op\":\"close\",\"args\":{},\"timeoutSec\":10}")
	if status != http.StatusOK {
		t.Fatalf("close op = %d %v", status, body)
	}
	if body["ok"] != true {
		t.Fatalf("close op body = %v, want ok", body)
	}
	if ops := runner.receivedOps(); len(ops) != 1 || ops[0].Op != protocol.OpClose {
		t.Fatalf("the runner received %+v, want exactly the close op", ops)
	}

	// The transition is the point of this test. No complete, no fail: nothing
	// else is sent on the socket.
	waitForStatus(t, qg, session.jobID, jobSucceeded)

	job, err := qg.jobs.GetJob(context.Background(), session.jobID)
	if err != nil {
		t.Fatalf("GetJob: %v", err)
	}
	if !job.Terminal() {
		t.Fatalf("job status = %s, want a terminal one", job.Status)
	}
	if job.LeaseOwnerRunnerID != "" {
		t.Errorf("the finished job still holds a lease owned by %s", job.LeaseOwnerRunnerID)
	}
	if job.CompletedAt.IsZero() {
		t.Error("the finished job has no completed_at")
	}

	// And the session row is closed, which is what stops the idle rule and the
	// one-open-session-per-execution index from seeing a session that is over.
	stored, err := qg.jobs.SessionByID(context.Background(), session.sessionID)
	if err != nil {
		t.Fatalf("SessionByID: %v", err)
	}
	if stored.Open() {
		t.Error("the session is still open after its job finished")
	}
}

// TestAFailedCloseDoesNotFinishTheJob keeps the transition honest: it is the
// engine ending the session that ends the job, not the word "close" appearing on
// the wire. A worker that refuses the op leaves the job running, and the engine
// can retry or fail the step itself.
func TestAFailedCloseDoesNotFinishTheJob(t *testing.T) {
	qg := newQueueGateway(t, nil)
	r := qg.newQueueRunner(t, testOrgA)
	session := claimSession(t, qg, r, protocol.JobVideoCompile)
	startFakeRunner(t, session.ws, session.token, session.epoch, opRefuse(protocol.ErrCodeUnsupportedOp))

	status, body, _ := postOp(t, qg, session.sessionID,
		"{\"opId\":\"op-close-2\",\"op\":\"close\",\"args\":{},\"timeoutSec\":10}")
	if status != http.StatusOK {
		t.Fatalf("close op = %d %v", status, body)
	}
	if body["ok"] != false {
		t.Fatalf("close op body = %v, want ok:false (a failed op is a 200)", body)
	}

	// Give the transition a chance to happen wrongly before asserting it did
	// not: a single status read immediately after the response would pass
	// either way.
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		view, code := qg.getJob(t, session.jobID, "")
		if code != http.StatusOK {
			t.Fatalf("GET job = %d", code)
		}
		if view.Status != jobRunning {
			t.Fatalf("a refused close moved the job to %s, want it left %s", view.Status, jobRunning)
		}
		time.Sleep(100 * time.Millisecond)
	}
}

// TestAClosedSessionRefusesFurtherOps is the other half of ending a session: the
// engine that sends an op after closing must be told the session is gone, not
// left waiting for an answer nobody will send.
func TestAClosedSessionRefusesFurtherOps(t *testing.T) {
	qg := newQueueGateway(t, nil)
	r := qg.newQueueRunner(t, testOrgA)
	session := claimSession(t, qg, r, protocol.JobVideoCompile)
	startFakeRunner(t, session.ws, session.token, session.epoch, opEcho("{}"))

	status, body, _ := postOp(t, qg, session.sessionID,
		"{\"opId\":\"op-close-3\",\"op\":\"close\",\"args\":{},\"timeoutSec\":10}")
	if status != http.StatusOK || body["ok"] != true {
		t.Fatalf("close op = %d %v", status, body)
	}
	waitForStatus(t, qg, session.jobID, jobSucceeded)

	status, body, _ = postOp(t, qg, session.sessionID,
		"{\"opId\":\"op-after-1\",\"op\":\"stat\",\"args\":{\"path\":\"out.mp4\"},\"timeoutSec\":10}")
	if status != http.StatusGone {
		t.Fatalf("an op after close = %d %v, want 410 session_lost", status, body)
	}
	if body["error"] != "session_lost" {
		t.Errorf("body = %v, want session_lost", body)
	}
}
