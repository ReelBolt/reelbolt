package agent

import (
	"context"
	"encoding/json"
	"sync"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// endAwareSession records the end the agent reports and the order of the two
// calls: SetEnd must arrive before Close, or a session that builds its own
// report inside Close would report nothing.
type endAwareSession struct {
	*fakeSession
	mu     sync.Mutex
	ends   []JobEnd
	events []string
}

func (s *endAwareSession) SetEnd(end JobEnd) {
	s.mu.Lock()
	s.ends = append(s.ends, end)
	s.events = append(s.events, "setEnd")
	s.mu.Unlock()
}

func (s *endAwareSession) Close(ctx context.Context) {
	s.mu.Lock()
	s.events = append(s.events, "close")
	s.mu.Unlock()
	s.fakeSession.Close(ctx)
}

func (s *endAwareSession) seen() ([]JobEnd, []string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]JobEnd(nil), s.ends...), append([]string(nil), s.events...)
}

type endAwareFactory struct {
	mu       sync.Mutex
	sessions []*endAwareSession
	jobs     []*Job
}

func (f *endAwareFactory) New(_ context.Context, job *Job) (Session, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	s := &endAwareSession{fakeSession: newFakeSession()}
	f.sessions = append(f.sessions, s)
	f.jobs = append(f.jobs, job)
	return s, nil
}

func (f *endAwareFactory) last() *endAwareSession {
	f.mu.Lock()
	defer f.mu.Unlock()
	if len(f.sessions) == 0 {
		return nil
	}
	return f.sessions[len(f.sessions)-1]
}

// startEndAware starts an agent with an end-aware factory and takes one job.
func startEndAware(t *testing.T, mutate func(*protocol.AssignPayload)) (*testAgent, *fakeConn, *endAwareFactory) {
	t.Helper()
	factory := &endAwareFactory{}
	ta := startTestAgent(t, withFactory(factory))
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)
	payload := assignPayload(protocol.JobRemotionWorkspace, 4, testSessionID)
	if mutate != nil {
		mutate(&payload)
	}
	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, payload)
	c.nextOf(t, protocol.TypeAccept)
	waitFor(t, "the session to start", func() bool { return factory.last() != nil })
	return ta, c, factory
}

// waitEnd waits for the session's single end report.
func waitEnd(t *testing.T, s *endAwareSession) JobEnd {
	t.Helper()
	waitFor(t, "the session to be told how its job ended", func() bool {
		_, events := s.seen()
		return len(events) >= 2
	})
	ends, events := s.seen()
	if len(ends) != 1 {
		t.Fatalf("SetEnd was called %d times, want exactly once", len(ends))
	}
	if events[0] != "setEnd" || events[1] != "close" {
		t.Fatalf("calls = %v, want SetEnd before Close", events)
	}
	return ends[0]
}

func TestTheEngineClosingASessionIsReportedAsClosed(t *testing.T) {
	_, c, factory := startEndAware(t, nil)
	c.sendJob(t, protocol.TypeOpRequest, testJobID, testSessionID, protocol.OpRequestPayload{
		OpID: "op-close", Op: protocol.OpClose, Args: json.RawMessage(`{}`), TimeoutSec: 5,
	})
	if end := waitEnd(t, factory.last()); end.Reason != EndClosed || end.Code != "" {
		t.Fatalf("end = %+v, want closed with no code", end)
	}
}

func TestACancelIsReportedAsCancelled(t *testing.T) {
	_, c, factory := startEndAware(t, nil)
	c.sendJob(t, protocol.TypeCancel, testJobID, testSessionID, protocol.CancelPayload{Reason: protocol.CancelUser, GraceSec: 5})
	if end := waitEnd(t, factory.last()); end.Reason != EndCancelled {
		t.Fatalf("end = %+v, want cancelled", end)
	}
}

func TestALostLeaseIsReportedAsLeaseLost(t *testing.T) {
	_, c, factory := startEndAware(t, nil)
	c.send(t, protocol.TypeHeartbeat, protocol.HeartbeatPayload{
		ServerTime: time.Now().UTC().Format(time.RFC3339),
		Leases:     []protocol.LeaseStatus{{JobID: testJobID, OK: false}},
	})
	if end := waitEnd(t, factory.last()); end.Reason != EndLeaseLost {
		t.Fatalf("end = %+v, want lease_lost", end)
	}
}

func TestATimeoutIsReportedWithTheProtocolsCode(t *testing.T) {
	_, _, factory := startEndAware(t, func(p *protocol.AssignPayload) { p.TimeoutSec = 1 })
	end := waitEnd(t, factory.last())
	if end.Reason != EndTimeout || end.Class != protocol.FailRunner || end.Code != protocol.ErrCodeTimeout {
		t.Fatalf("end = %+v, want timeout/runner/timeout", end)
	}
}

func TestAShutdownIsReportedAsDrain(t *testing.T) {
	ta, _, factory := startEndAware(t, nil)
	if err := ta.Shutdown(context.Background()); err != nil {
		t.Fatalf("Shutdown: %v", err)
	}
	if end := waitEnd(t, factory.last()); end.Reason != EndDrain {
		t.Fatalf("end = %+v, want drain", end)
	}
}

// A runner that cannot reach the gateway within the lease window stops the
// job, and says so in its own words rather than as a lease the gateway took.
func TestLosingTheConnectionPastTheLeaseIsReportedAsDisconnected(t *testing.T) {
	factory := &endAwareFactory{}
	ta := startTestAgent(t, withFactory(factory), func(cfg *Config) {
		cfg.LeaseLostAfter = 50 * time.Millisecond
		// Long enough that the runner is still disconnected when the lease
		// window passes.
		cfg.Backoff = Backoff{Base: 2 * time.Second, Max: 2 * time.Second, ResetAfter: time.Hour}
	})
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)
	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 4, testSessionID))
	c.nextOf(t, protocol.TypeAccept)
	waitFor(t, "the session to start", func() bool { return factory.last() != nil })
	_ = c.Close(protocol.CloseGoingAway, "network gone")
	if end := waitEnd(t, factory.last()); end.Reason != EndDisconnected {
		t.Fatalf("end = %+v, want disconnected", end)
	}
}

// assign.display reaches the job the session is built for, and an assign
// without one leaves it zero.
func TestAssignDisplayReachesTheJob(t *testing.T) {
	display := protocol.AssignDisplay{WorkspaceName: "Acme", ProjectName: "Launch", WorkflowName: "Promo", ExecutionID: "e-1"}
	_, _, factory := startEndAware(t, func(p *protocol.AssignPayload) { p.Display = &display })
	factory.mu.Lock()
	job := factory.jobs[0]
	factory.mu.Unlock()
	if job.Display != display {
		t.Fatalf("job.Display = %+v, want %+v", job.Display, display)
	}
}

func TestAnAssignWithoutDisplayLeavesItZero(t *testing.T) {
	_, _, factory := startEndAware(t, nil)
	factory.mu.Lock()
	job := factory.jobs[0]
	factory.mu.Unlock()
	if job.Display != (protocol.AssignDisplay{}) {
		t.Fatalf("job.Display = %+v, want zero", job.Display)
	}
}
