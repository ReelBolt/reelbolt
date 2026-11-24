package agent

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

const (
	testJobID     = "7d1c2e0a-5b7e-4a43-9d57-3f0c1a9b2e11"
	testSessionID = "0b6f3c58-91d2-4c7a-8e34-5a2d7f1e9c40"
)

// The runner speaks first: hello carries a signature over the host it dialled,
// and the gateway must be able to verify it against the device's registered
// public key.
func TestHandshakeSignsTheHelloForTheDialledHost(t *testing.T) {
	id, priv := newDeviceKey(t)
	ta := startTestAgent(t, func(cfg *Config) {
		cfg.Identity = Identity{DeviceID: "device-1", PrivateKey: priv}
	})
	c := ta.gw.awaitDial(t)
	frame := c.nextOf(t, protocol.TypeHello)
	hello, err := protocol.DecodePayload[protocol.HelloPayload](frame.env)
	if err != nil {
		t.Fatalf("hello payload: %v", err)
	}
	if hello.Auth.Kind != protocol.AuthKindDevice {
		t.Fatalf("auth kind = %q, want device", hello.Auth.Kind)
	}
	if err := protocol.VerifyHelloSignature(id, hello.Auth.DeviceID, hello.Auth.TS, hello.Auth.Nonce, "runner.example.invalid", hello.Auth.Sig); err != nil {
		t.Fatalf("the hello signature does not verify: %v", err)
	}
	// The host is bound: the same hello must not verify at another gateway,
	// which is what stops a hostile gateway replaying it somewhere else.
	if err := protocol.VerifyHelloSignature(id, hello.Auth.DeviceID, hello.Auth.TS, hello.Auth.Nonce, "other.invalid", hello.Auth.Sig); err == nil {
		t.Fatal("the signature verified against a host it was not signed for")
	}
	// The nonce is at least the 16 bytes the gateway requires.
	nonce, err := decodeBase64URL(hello.Auth.Nonce)
	if err != nil || len(nonce) < 16 {
		t.Fatalf("nonce = %q (%d bytes, err %v), want at least 16 bytes", hello.Auth.Nonce, len(nonce), err)
	}
	if hello.ProtocolVersion != protocol.ProtocolVersion {
		t.Fatalf("protocolVersion = %q", hello.ProtocolVersion)
	}
	if !hello.Capabilities.Supports(protocol.JobRemotionWorkspace) {
		t.Fatal("the hello did not advertise the Remotion job type")
	}
}

// A pool worker authenticates with a credential instead of a key, and its
// hello carries no device signature at all.
func TestPoolIdentitySendsACredential(t *testing.T) {
	ta := startTestAgent(t, func(cfg *Config) {
		cfg.Identity = Identity{Credential: "pool-secret", WorkerName: "worker-7"}
	})
	c := ta.gw.awaitDial(t)
	hello, err := protocol.DecodePayload[protocol.HelloPayload](c.nextOf(t, protocol.TypeHello).env)
	if err != nil {
		t.Fatalf("hello payload: %v", err)
	}
	if hello.Auth.Kind != protocol.AuthKindPool || hello.Auth.Credential != "pool-secret" || hello.Auth.WorkerName != "worker-7" {
		t.Fatalf("pool auth = %+v", hello.Auth)
	}
	if hello.Auth.Sig != "" || hello.Auth.Nonce != "" {
		t.Fatal("a pool hello carried device-auth fields")
	}
}

// The gateway assigns work only against free slots, so ready follows welcome.
func TestReadyFollowsWelcome(t *testing.T) {
	ta := startTestAgent(t, withSlots(3))
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	ready, err := protocol.DecodePayload[protocol.ReadyPayload](c.nextOf(t, protocol.TypeReady).env)
	if err != nil {
		t.Fatalf("ready payload: %v", err)
	}
	if ready.FreeSlots != 3 {
		t.Fatalf("freeSlots = %d, want 3", ready.FreeSlots)
	}
}

// A job is claimed by accepting it, and its ops are answered with the job's own
// epoch and token: the fencing the gateway applies needs both.
func TestAssignIsAcceptedAndOpsAreAnswered(t *testing.T) {
	ta := startTestAgent(t)
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)

	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 3, testSessionID))
	accept, err := protocol.DecodePayload[protocol.AcceptPayload](c.nextOf(t, protocol.TypeAccept).env)
	if err != nil {
		t.Fatalf("accept payload: %v", err)
	}
	if accept.LeaseEpoch != 3 || accept.JobToken != "TEST.job.token" {
		t.Fatalf("accept did not echo the assignment's epoch and token: %+v", accept)
	}

	waitFor(t, "the job's session to start", func() bool { return ta.factory.sessionCount() == 1 })
	ta.factory.mu.Lock()
	job := ta.factory.jobs[0]
	ta.factory.mu.Unlock()
	if job.JobID != testJobID || job.SessionID != testSessionID || job.OrgID == "" {
		t.Fatalf("the job handed to the session is wrong: %+v", job)
	}
	if job.ScratchDir == "" {
		t.Fatal("the job has no scratch directory")
	}

	c.sendJob(t, protocol.TypeOpRequest, testJobID, testSessionID, protocol.OpRequestPayload{
		OpID: "op-000001", Op: protocol.OpStatus, Args: json.RawMessage([]byte("{}")), TimeoutSec: 5,
	})
	resp, err := protocol.DecodePayload[protocol.OpResponsePayload](c.nextOf(t, protocol.TypeOpResponse).env)
	if err != nil {
		t.Fatalf("op.response payload: %v", err)
	}
	if !resp.OK || resp.OpID != "op-000001" {
		t.Fatalf("op.response = %+v", resp)
	}
	if resp.LeaseEpoch != 3 || resp.JobToken != "TEST.job.token" {
		t.Fatalf("op.response did not carry the job's epoch and token: %+v", resp)
	}
}

// A runner with no free slot declines rather than accepting work it cannot run,
// and the job goes back to Queued without consuming an attempt.
func TestAssignIsRejectedWhenThereIsNoFreeSlot(t *testing.T) {
	ta := startTestAgent(t, withSlots(1))
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)

	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID))
	c.nextOf(t, protocol.TypeAccept)

	second := "8e2d3f1b-6c8f-4b54-ae68-4a1d2b0c3f22"
	c.sendJob(t, protocol.TypeAssign, second, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID))
	reject, err := protocol.DecodePayload[protocol.RejectPayload](c.nextOf(t, protocol.TypeReject).env)
	if err != nil {
		t.Fatalf("reject payload: %v", err)
	}
	if reject.Reason != protocol.RejectBusy {
		t.Fatalf("reject reason = %q, want busy", reject.Reason)
	}
}

// A job type the runner never advertised is refused with capability_mismatch,
// not accepted and then failed.
func TestAssignIsRejectedForAnUnsupportedJobType(t *testing.T) {
	ta := startTestAgent(t, withCapabilities(testCapabilities(false)))
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)

	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID))
	reject, err := protocol.DecodePayload[protocol.RejectPayload](c.nextOf(t, protocol.TypeReject).env)
	if err != nil {
		t.Fatalf("reject payload: %v", err)
	}
	if reject.Reason != protocol.RejectCapabilityMismatch {
		t.Fatalf("reject reason = %q, want capability_mismatch", reject.Reason)
	}
}

// A job that needs a container runtime is refused with the reason the
// specification reserves for exactly that, so the engine can tell the user why.
func TestAssignIsRejectedWithoutAContainerRuntime(t *testing.T) {
	ta := startTestAgent(t, withCapabilities(testCapabilities(false)))
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)

	payload := assignPayload(protocol.JobVideoCompile, 1, testSessionID)
	payload.Requires = protocol.AssignRequires{Container: protocol.ContainerDocker}
	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, payload)
	reject, err := protocol.DecodePayload[protocol.RejectPayload](c.nextOf(t, protocol.TypeReject).env)
	if err != nil {
		t.Fatalf("reject payload: %v", err)
	}
	if reject.Reason != protocol.RejectNoContainerRuntime {
		t.Fatalf("reject reason = %q, want no_container_runtime", reject.Reason)
	}
}

// Heartbeats carry the held jobs with their epochs, because that listing is
// what renews each lease; free slots ride along so the gateway can assign
// against them.
func TestHeartbeatListsHeldJobsAndFreeSlots(t *testing.T) {
	ta := startTestAgent(t, withSlots(2))
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)

	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 4, testSessionID))
	c.nextOf(t, protocol.TypeAccept)
	waitFor(t, "the session to start", func() bool { return ta.factory.sessionCount() == 1 })

	hb := c.nextOf(t, protocol.TypeHeartbeat)
	payload, err := protocol.DecodePayload[protocol.HeartbeatPayload](hb.env)
	if err != nil {
		t.Fatalf("heartbeat payload: %v", err)
	}
	if payload.FreeSlots == nil || *payload.FreeSlots != 1 {
		t.Fatalf("freeSlots = %v, want 1", payload.FreeSlots)
	}
	if len(payload.Running) != 1 || payload.Running[0].JobID != testJobID || payload.Running[0].LeaseEpoch != 4 {
		t.Fatalf("heartbeat did not list the held job: %+v", payload.Running)
	}
}

// A heartbeat whose lease the gateway no longer honours stops that job: the
// epoch may already belong to another runner.
func TestLeaseLostStopsTheJob(t *testing.T) {
	ta := startTestAgent(t)
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)
	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID))
	c.nextOf(t, protocol.TypeAccept)
	waitFor(t, "the session to start", func() bool { return ta.factory.sessionCount() == 1 })

	c.send(t, protocol.TypeHeartbeat, protocol.HeartbeatPayload{
		ServerTime: time.Now().UTC().Format(time.RFC3339),
		Leases:     []protocol.LeaseStatus{{JobID: testJobID, OK: false}},
	})
	session := ta.factory.last()
	waitFor(t, "the session to be closed", func() bool { return session.closeCount() == 1 })

	// The job is no longer held, so it is no longer listed and no slot is lost.
	waitFor(t, "the job to be forgotten", func() bool { return len(ta.heldLeases()) == 0 })
	// Nothing is reported for a lost lease: the gateway has already reassigned
	// the job, and a message under a fenced epoch would be discarded anyway.
	c.noFrameOf(t, protocol.TypeComplete, 100*time.Millisecond)
}

// A cancel stops the job within the grace window and acknowledges it: that
// acknowledgement is what tells the gateway the runner has let go.
func TestCancelStopsTheJobAndCompletesCancelled(t *testing.T) {
	ta := startTestAgent(t)
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)
	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 2, testSessionID))
	c.nextOf(t, protocol.TypeAccept)
	waitFor(t, "the session to start", func() bool { return ta.factory.sessionCount() == 1 })
	session := ta.factory.last()

	c.sendJob(t, protocol.TypeCancel, testJobID, testSessionID, protocol.CancelPayload{Reason: protocol.CancelUser, GraceSec: 5})

	complete, err := protocol.DecodePayload[protocol.CompletePayload](c.nextOf(t, protocol.TypeComplete).env)
	if err != nil {
		t.Fatalf("complete payload: %v", err)
	}
	if complete.Outcome != protocol.OutcomeCancelled {
		t.Fatalf("outcome = %q, want cancelled", complete.Outcome)
	}
	if complete.LeaseEpoch != 2 || complete.JobToken != "TEST.job.token" {
		t.Fatalf("complete did not carry the job's epoch and token: %+v", complete)
	}
	waitFor(t, "the session to be closed", func() bool { return session.closeCount() >= 1 })
}

// A cancel reaches a running op: the op's context is the job's, so stopping the
// job stops the work rather than leaving an encode running until it finishes.
func TestCancelInterruptsARunningOp(t *testing.T) {
	ta := startTestAgent(t)
	interrupted := make(chan struct{})
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)
	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID))
	c.nextOf(t, protocol.TypeAccept)
	waitFor(t, "the session to start", func() bool { return ta.factory.sessionCount() == 1 })
	s := ta.factory.last()
	s.handle = func(ctx context.Context, op string, args json.RawMessage) (any, *protocol.OpError) {
		<-ctx.Done()
		close(interrupted)
		return nil, OpError(protocol.ErrCodeCancelled, "stopped", false)
	}

	c.sendJob(t, protocol.TypeOpRequest, testJobID, testSessionID, protocol.OpRequestPayload{
		OpID: "op-long", Op: "ffmpeg", Args: json.RawMessage([]byte("{}")), TimeoutSec: 600,
	})
	time.Sleep(20 * time.Millisecond)
	c.sendJob(t, protocol.TypeCancel, testJobID, testSessionID, protocol.CancelPayload{Reason: protocol.CancelUser, GraceSec: 5})
	select {
	case <-interrupted:
	case <-time.After(2 * time.Second):
		t.Fatal("the cancel never reached the running op")
	}
}

// A gateway restart mid-job is survivable: the runner reconnects, offers the
// jobs it still holds, and the ones the gateway resumes carry on.
func TestReconnectResumesHeldJobs(t *testing.T) {
	ta := startTestAgent(t)
	first := ta.gw.awaitDial(t)
	first.handshake(t)
	first.nextOf(t, protocol.TypeReady)
	first.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID))
	first.nextOf(t, protocol.TypeAccept)
	waitFor(t, "the session to start", func() bool { return ta.factory.sessionCount() == 1 })
	session := ta.factory.last()

	// The gateway goes away without a close handshake.
	_ = first.Close(protocol.CloseCode(1006), "gateway restarted")

	second := ta.gw.awaitDial(t)
	frame := second.nextOf(t, protocol.TypeHello)
	hello, err := protocol.DecodePayload[protocol.HelloPayload](frame.env)
	if err != nil {
		t.Fatalf("hello payload: %v", err)
	}
	if len(hello.Running) != 1 || hello.Running[0].JobID != testJobID || hello.Running[0].LeaseEpoch != 1 {
		t.Fatalf("the reconnect did not offer the held job: %+v", hello.Running)
	}
	second.send(t, protocol.TypeWelcome, protocol.WelcomePayload{
		RunnerID: "runner-1", Kind: protocol.KindDesktop,
		ProtocolVersion: protocol.ProtocolVersion, HeartbeatSec: protocol.HeartbeatSec,
		ServerTime: time.Now().UTC().Format(time.RFC3339),
		Resumed:    []protocol.JobLease{{JobID: testJobID, LeaseEpoch: 1}},
	})
	second.nextOf(t, protocol.TypeReady)

	// The job is still held and still answers ops on the new connection.
	if session.closeCount() != 0 {
		t.Fatal("a resumed job's session was closed")
	}
	second.sendJob(t, protocol.TypeOpRequest, testJobID, testSessionID, protocol.OpRequestPayload{
		OpID: "op-after-reconnect", Op: protocol.OpStatus, Args: json.RawMessage([]byte("{}")), TimeoutSec: 5,
	})
	if resp := decodeOpResponse(t, second); !resp.OK {
		t.Fatalf("op after reconnect failed: %+v", resp)
	}
	// Already-executed ops are answered from the cache without running again.
	if ops := session.opList(); len(ops) != 1 {
		t.Fatalf("ops executed = %v, want exactly one", ops)
	}
}

// A job the gateway did not resume is stopped: its lease has been given to
// someone else, and finishing it would be work nobody is waiting for.
func TestReconnectStopsAJobThatWasNotResumed(t *testing.T) {
	ta := startTestAgent(t)
	first := ta.gw.awaitDial(t)
	first.handshake(t)
	first.nextOf(t, protocol.TypeReady)
	first.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID))
	first.nextOf(t, protocol.TypeAccept)
	waitFor(t, "the session to start", func() bool { return ta.factory.sessionCount() == 1 })
	session := ta.factory.last()

	_ = first.Close(protocol.CloseCode(1006), "gateway restarted")
	second := ta.gw.awaitDial(t)
	second.handshake(t)
	waitFor(t, "the un-resumed job to be stopped", func() bool { return session.closeCount() == 1 })
	waitFor(t, "the job to be forgotten", func() bool { return len(ta.heldLeases()) == 0 })
}

// A superseded connection is final. Reconnecting would fight the newer runner
// for the socket forever, which is exactly what the 4409 rule exists to stop.
func TestSupersededIsTerminal(t *testing.T) {
	ta := startTestAgent(t)
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)

	_ = c.Close(protocol.CloseSuperseded, "superseded by a newer connection")
	err := ta.runErr(3 * time.Second)
	if !errors.Is(err, ErrSuperseded) {
		t.Fatalf("Run returned %v, want ErrSuperseded", err)
	}
	if ta.gw.dialsCount() != 1 {
		t.Fatalf("the agent dialled %d times after being superseded, want 1", ta.gw.dialsCount())
	}
}

// Every refusal that means "this identity may not be here" is final, and a
// refusal that means "try later" is not.
func TestCloseCodePolicy(t *testing.T) {
	cases := []struct {
		code       protocol.CloseCode
		want       error
		terminal   bool
		reconnects bool
	}{
		{protocol.CloseUnsupportedVersion, ErrUnsupportedVersion, true, false},
		{protocol.CloseUnauthenticated, ErrUnauthenticated, true, false},
		{protocol.CloseForbidden, ErrForbidden, true, false},
		{protocol.CloseMessageTooLarge, ErrRefused, true, false},
		{protocol.CloseHeartbeatTimeout, ErrRefused, false, true},
		{protocol.CloseGoingAway, ErrRefused, false, true},
		{protocol.CloseInternal, ErrRefused, false, true},
		{protocol.CloseRateLimited, ErrRefused, false, true},
	}
	for _, tc := range cases {
		t.Run(fmt.Sprintf("close_%d", int(tc.code)), func(t *testing.T) {
			ta := startTestAgent(t)
			c := ta.gw.awaitDial(t)
			c.handshake(t)
			c.nextOf(t, protocol.TypeReady)
			_ = c.Close(tc.code, "test")

			if tc.terminal {
				err := ta.runErr(3 * time.Second)
				if !errors.Is(err, tc.want) {
					t.Fatalf("Run returned %v, want %v", err, tc.want)
				}
				if ta.gw.dialsCount() != 1 {
					t.Fatalf("dialled %d times, want 1", ta.gw.dialsCount())
				}
				return
			}
			// Non-terminal: the runner comes back.
			second := ta.gw.awaitDial(t)
			second.handshake(t)
		})
	}
}

// The runner's own drain is the orderly path: announce, say goodbye, close.
func TestDrainSendsDrainThenByeAndCloses(t *testing.T) {
	ta := startTestAgent(t)
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)

	if err := ta.Drain("sigterm", 120*time.Second); err != nil {
		t.Fatalf("Drain: %v", err)
	}
	drain, err := protocol.DecodePayload[protocol.DrainPayload](c.nextOf(t, protocol.TypeDrain).env)
	if err != nil {
		t.Fatalf("drain payload: %v", err)
	}
	if drain.Reason != "sigterm" || drain.DeadlineSec != 120 {
		t.Fatalf("drain = %+v", drain)
	}
	// A drained runner takes no new work.
	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID))
	reject, err := protocol.DecodePayload[protocol.RejectPayload](c.nextOf(t, protocol.TypeReject).env)
	if err != nil {
		t.Fatalf("reject payload: %v", err)
	}
	if reject.Reason != protocol.RejectShuttingDown {
		t.Fatalf("reject reason = %q, want shutting_down", reject.Reason)
	}

	if err := ta.Shutdown(context.Background()); err != nil {
		t.Fatalf("Shutdown: %v", err)
	}
	bye, err := protocol.DecodePayload[protocol.ByePayload](c.nextOf(t, protocol.TypeBye).env)
	if err != nil {
		t.Fatalf("bye payload: %v", err)
	}
	if bye.Reason != protocol.CancelShutdown {
		t.Fatalf("bye reason = %q", bye.Reason)
	}
	waitFor(t, "the close frame", func() bool { return c.closedWith() == protocol.CloseGoingAway })
}

// A drain tears sessions down instead of waiting: the specification says a
// draining runner does not wait for sessions and they are rehydrated elsewhere.
func TestDrainClosesSessions(t *testing.T) {
	ta := startTestAgent(t)
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)
	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID))
	c.nextOf(t, protocol.TypeAccept)
	waitFor(t, "the session to start", func() bool { return ta.factory.sessionCount() == 1 })
	session := ta.factory.last()

	if err := ta.Drain("sigterm", 120*time.Second); err != nil {
		t.Fatalf("Drain: %v", err)
	}
	waitFor(t, "the session to be closed", func() bool { return session.closeCount() == 1 })
}

// A gateway message the runner does not know is ignored, which is how new
// message types ship inside one major version.
func TestUnknownMessageTypeIsIgnored(t *testing.T) {
	ta := startTestAgent(t)
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)

	c.sendRaw(t, "{\"v\":1,\"type\":\"invented.from.the.future\",\"id\":\"x\",\"payload\":{\"anything\":true}}")
	// The connection is still usable: the agent reads and acts on what follows.
	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID))
	c.nextOf(t, protocol.TypeAccept)
}

// A malformed frame is a protocol violation and closes with 4400.
func TestMalformedFrameClosesWith4400(t *testing.T) {
	ta := startTestAgent(t)
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)

	c.sendRaw(t, "this is not json")
	err := ta.runErr(3 * time.Second)
	if !IsTerminal(err) {
		t.Fatalf("Run returned %v, want a terminal error", err)
	}
	if code := c.closedWith(); code != protocol.CloseUnsupportedVersion {
		t.Fatalf("closed with %d, want %d", code, protocol.CloseUnsupportedVersion)
	}
}

// An agent older than welcome.minAgentVersion must not take jobs.
func TestOutdatedAgentStopsTakingJobs(t *testing.T) {
	ta := startTestAgent(t)
	c := ta.gw.awaitDial(t)
	c.nextOf(t, protocol.TypeHello)
	c.send(t, protocol.TypeWelcome, protocol.WelcomePayload{
		RunnerID: "runner-1", Kind: protocol.KindDesktop,
		ProtocolVersion: protocol.ProtocolVersion, HeartbeatSec: protocol.HeartbeatSec,
		ServerTime:      time.Now().UTC().Format(time.RFC3339),
		MinAgentVersion: "99.0.0",
		Resumed:         []protocol.JobLease{},
	})
	waitFor(t, "the outdated flag to be set", func() bool { return ta.Outdated() })

	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID))
	reject, err := protocol.DecodePayload[protocol.RejectPayload](c.nextOf(t, protocol.TypeReject).env)
	if err != nil {
		t.Fatalf("reject payload: %v", err)
	}
	if reject.Reason != protocol.RejectShuttingDown {
		t.Fatalf("reject reason = %q, want shutting_down", reject.Reason)
	}
}

// A session that cannot start fails the job with the class and code the
// executor chose, rather than leaving the engine waiting.
func TestSessionStartFailureFailsTheJob(t *testing.T) {
	ta := startTestAgent(t)
	ta.factory.err = &JobError{Class: protocol.FailRunner, Code: protocol.ErrCodeContainerUnavailable, Message: "no runtime", Retryable: true}
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)

	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID))
	c.nextOf(t, protocol.TypeAccept)
	fail, err := protocol.DecodePayload[protocol.FailPayload](c.nextOf(t, protocol.TypeFail).env)
	if err != nil {
		t.Fatalf("fail payload: %v", err)
	}
	if fail.Class != protocol.FailRunner || fail.Code != protocol.ErrCodeContainerUnavailable || !fail.Retryable {
		t.Fatalf("fail = %+v", fail)
	}
	waitFor(t, "the job to be forgotten", func() bool { return len(ta.heldLeases()) == 0 })
}

// A session that outlives assign.timeoutSec ends with a timeout failure.
func TestSessionTimeoutFailsTheJob(t *testing.T) {
	ta := startTestAgent(t)
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)
	payload := assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID)
	payload.TimeoutSec = 1
	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, payload)
	c.nextOf(t, protocol.TypeAccept)

	fail, err := protocol.DecodePayload[protocol.FailPayload](c.nextOf(t, protocol.TypeFail).env)
	if err != nil {
		t.Fatalf("fail payload: %v", err)
	}
	if fail.Code != protocol.ErrCodeTimeout || fail.Class != protocol.FailRunner {
		t.Fatalf("fail = %+v", fail)
	}
}

func decodeOpResponse(t *testing.T, c *fakeConn) protocol.OpResponsePayload {
	t.Helper()
	resp, err := protocol.DecodePayload[protocol.OpResponsePayload](c.nextOf(t, protocol.TypeOpResponse).env)
	if err != nil {
		t.Fatalf("op.response payload: %v", err)
	}
	return resp
}

func decodeBase64URL(s string) ([]byte, error) {
	return base64.RawURLEncoding.DecodeString(s)
}

// An op result too big for one payload is answered with a small error at once,
// never an oversize frame and never a hang until op_timeout.
func TestOversizeOpResultGetsAnErrorResponse(t *testing.T) {
	ta := startTestAgent(t)
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)
	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID))
	c.nextOf(t, protocol.TypeAccept)
	waitFor(t, "the session to start", func() bool { return ta.factory.sessionCount() == 1 })
	ta.factory.last().handle = func(ctx context.Context, op string, args json.RawMessage) (any, *protocol.OpError) {
		return map[string]string{"blob": strings.Repeat("x", protocol.MaxPayloadBytes)}, nil
	}
	c.sendJob(t, protocol.TypeOpRequest, testJobID, testSessionID, protocol.OpRequestPayload{
		OpID: "op-big", Op: "ffmpeg", Args: json.RawMessage([]byte("{}")), TimeoutSec: 600,
	})
	got := c.nextOf(t, protocol.TypeOpResponse)
	if len(got.raw) > protocol.MaxPayloadBytes {
		t.Fatalf("an oversize frame was sent: %d bytes", len(got.raw))
	}
	resp, err := protocol.DecodePayload[protocol.OpResponsePayload](got.env)
	if err != nil {
		t.Fatalf("op.response payload: %v", err)
	}
	if resp.OK || resp.Error == nil || resp.Error.Code != protocol.ErrCodeFileTooLarge || resp.Error.Retryable {
		t.Fatalf("op.response = %+v, want file_too_large", resp)
	}
}
