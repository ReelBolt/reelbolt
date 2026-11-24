package main

import (
	"sync"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// These tests need no database: the op table and the instance-to-endpoint rule
// are pure logic, and the Go CI leg has no Postgres. The wire-level behaviour
// (proxying, cancellation, the metrics) is in sessions_integration_test.go.

func TestOpTableAllowsOneOpPerSession(t *testing.T) {
	table := newOpTable()
	first := &pendingOp{opID: "op-1", jobID: "job-1", sessionID: "s-1", ch: make(chan opOutcome, 1)}
	if existing, ok := table.begin(first); !ok || existing != nil {
		t.Fatalf("the first op was refused: %v", existing)
	}

	// A second op on the same session is refused, and the caller is told which
	// op is in the way (the specification's op_in_flight).
	second := &pendingOp{opID: "op-2", jobID: "job-1", sessionID: "s-1", ch: make(chan opOutcome, 1)}
	existing, ok := table.begin(second)
	if ok {
		t.Fatal("a second concurrent op on one session was accepted")
	}
	if existing != first {
		t.Errorf("the refusal names %v, want the op already in flight", existing)
	}

	// A repeated opId is refused too, even on another session.
	third := &pendingOp{opID: "op-1", jobID: "job-9", sessionID: "s-9", ch: make(chan opOutcome, 1)}
	if _, ok := table.begin(third); ok {
		t.Error("a repeated opId was accepted")
	}

	// A different session may proceed.
	fourth := &pendingOp{opID: "op-4", jobID: "job-2", sessionID: "s-2", ch: make(chan opOutcome, 1)}
	if _, ok := table.begin(fourth); !ok {
		t.Error("a second session was refused")
	}
	if table.size() != 2 {
		t.Errorf("size = %d, want 2", table.size())
	}
}

func TestOpTableDeliversExactlyOnce(t *testing.T) {
	table := newOpTable()
	p := &pendingOp{opID: "op-1", jobID: "job-1", sessionID: "s-1", ch: make(chan opOutcome, 1)}
	table.begin(p)

	payload := protocol.OpResponsePayload{OpID: "op-1", OK: true}
	if !table.deliver("op-1", payload) {
		t.Fatal("the first delivery was refused")
	}
	outcome := <-p.ch
	if outcome.payload == nil || !outcome.payload.OK {
		t.Fatalf("outcome = %+v", outcome)
	}
	// A repeat is not an error, but nobody is waiting for it any more.
	if table.deliver("op-1", payload) {
		t.Error("a duplicate op.response was delivered twice")
	}
	if table.size() != 0 {
		t.Errorf("size = %d, want 0 after delivery", table.size())
	}
	if table.take("op-1") != nil {
		t.Error("take returned an entry after delivery")
	}
}

func TestOpTableTakeWinsTheRaceWithDelivery(t *testing.T) {
	table := newOpTable()
	p := &pendingOp{opID: "op-1", jobID: "job-1", sessionID: "s-1", ch: make(chan opOutcome, 1)}
	table.begin(p)

	// What a waiter does when its deadline fires first: it takes the entry, and a
	// "take" that returns something is how it knows an answer did NOT win the
	// race, so it reports a timeout without touching the channel.
	if taken := table.take("op-1"); taken != p {
		t.Fatalf("take = %v, want the pending op", taken)
	}
	// The late answer has nowhere to go now, which is the documented cost of
	// giving up: the worker's op cache is what makes a retry safe.
	if table.deliver("op-1", protocol.OpResponsePayload{OpID: "op-1", OK: true}) {
		t.Fatal("a delivery found a waiter that had already given up")
	}
	select {
	case outcome := <-p.ch:
		t.Fatalf("an unpublished outcome arrived on the channel: %+v", outcome)
	default:
	}
}

func TestOpTableAbortReleasesTheWaiter(t *testing.T) {
	table := newOpTable()
	p := &pendingOp{opID: "op-1", jobID: "job-1", sessionID: "s-1", ch: make(chan opOutcome, 1)}
	table.begin(p)

	if n := table.abortJob("job-1", protocol.CancelUser, false); n != 1 {
		t.Fatalf("abortJob = %d, want 1", n)
	}
	outcome := <-p.ch
	if outcome.payload != nil || outcome.reason != protocol.CancelUser || outcome.sessionLost {
		t.Fatalf("outcome = %+v, want a plain cancellation", outcome)
	}
	if table.size() != 0 {
		t.Errorf("size = %d, want 0", table.size())
	}
	// Aborting a job nothing waits on is a no-op, not an error: cancellation and
	// lease release run for every job.
	if n := table.abortJob("job-1", "again", true); n != 0 {
		t.Errorf("a second abort = %d, want 0", n)
	}
}

func TestOpTableAbortMarksSessionLoss(t *testing.T) {
	table := newOpTable()
	p := &pendingOp{opID: "op-1", jobID: "job-1", sessionID: "s-1", ch: make(chan opOutcome, 1)}
	table.begin(p)
	table.abortJob("job-1", "lease_expired", true)
	if outcome := <-p.ch; !outcome.sessionLost {
		t.Fatalf("outcome = %+v, want session loss", outcome)
	}
}

func TestOpTablePendingForJob(t *testing.T) {
	table := newOpTable()
	if table.pendingForJob("job-1") != nil {
		t.Fatal("an empty table reported a pending op")
	}
	p := &pendingOp{opID: "op-1", jobID: "job-1", sessionID: "s-1", ch: make(chan opOutcome, 1)}
	table.begin(p)
	if got := table.pendingForJob("job-1"); got != p {
		t.Fatalf("pendingForJob = %v, want the registered op", got)
	}
}

func TestOpTableIsRaceFree(t *testing.T) {
	table := newOpTable()
	var wg sync.WaitGroup
	for i := 0; i < 32; i++ {
		wg.Add(1)
		go func(n int) {
			defer wg.Done()
			id := "op-" + string(rune('a'+n%26))
			p := &pendingOp{opID: id, jobID: "job", sessionID: id, ch: make(chan opOutcome, 1)}
			if _, ok := table.begin(p); !ok {
				return
			}
			table.deliver(id, protocol.OpResponsePayload{OpID: id, OK: true})
			table.take(id)
			table.abortJob("job", "race", true)
		}(i)
	}
	wg.Wait()
	if table.size() != 0 {
		t.Errorf("size = %d, want 0 once every goroutine has run", table.size())
	}
}

// ── gatewayEndpoint ─────────────────────────────────────────────────────────

func TestGatewayEndpoint(t *testing.T) {
	cases := []struct {
		name     string
		instance string
		port     string
		want     string
		wantErr  bool
	}{
		// A real deployment stores the pod IP (the downward API) and a peer
		// replica listens on the same port this one does.
		{name: "bare pod ip", instance: "10.0.3.17", port: "8090", want: "10.0.3.17:8090"},
		{name: "bare host", instance: "gateway-2", port: "8090", want: "gateway-2:8090"},
		// A test (or an operator) may put a port in the column, which wins.
		{name: "explicit port", instance: "127.0.0.1:38417", port: "8090", want: "127.0.0.1:38417"},
		{name: "explicit port, no default", instance: "127.0.0.1:1234", port: "", want: "127.0.0.1:1234"},
		{name: "bare ipv6", instance: "::1", port: "8090", want: "[::1]:8090"},
		{name: "bracketed ipv6 with port", instance: "[fd00::2]:8090", port: "8090", want: "[fd00::2]:8090"},
		{name: "no default port", instance: "10.0.3.17", port: "", want: "10.0.3.17:8090"},
		{name: "empty", instance: "  ", port: "8090", wantErr: true},
		{name: "port with no host", instance: ":8090", port: "8090", wantErr: true},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, err := gatewayEndpoint(tc.instance, tc.port)
			if tc.wantErr {
				if err == nil {
					t.Fatalf("gatewayEndpoint(%q) = %q, want an error", tc.instance, got)
				}
				return
			}
			if err != nil {
				t.Fatalf("gatewayEndpoint(%q): %v", tc.instance, err)
			}
			if got != tc.want {
				t.Errorf("gatewayEndpoint(%q, %q) = %q, want %q", tc.instance, tc.port, got, tc.want)
			}
		})
	}
}

func TestProxyDeadlineExceedsTheOpDeadline(t *testing.T) {
	g := &gateway{cfg: appConfig{ProxyTimeout: 30 * time.Second}}
	// The owning replica must report the timeout, so the hop outlives the op.
	if got := g.proxyDeadline(sessionOpRequest{TimeoutSec: 5}); got <= 5*time.Second {
		t.Errorf("deadline = %s, want more than the op's 5s", got)
	}
	// An unset or absurd timeout falls back to the job default, never to zero.
	if got := g.proxyDeadline(sessionOpRequest{}); got <= time.Duration(jobTimeoutDefaultSec)*time.Second {
		t.Errorf("deadline = %s, want the job default plus slack", got)
	}
	if got := g.proxyDeadline(sessionOpRequest{TimeoutSec: jobTimeoutMaxSec * 2}); got <= time.Duration(jobTimeoutDefaultSec)*time.Second {
		t.Errorf("deadline = %s, want the cap applied", got)
	}
}

// ── configuration ───────────────────────────────────────────────────────────

// TestLoadConfigDefaultsForTheScalingSignal pins the defaults an operator gets
// without setting anything: one spare pool slot, and a peer replica reachable on
// the port this one listens on.
func TestLoadConfigDefaultsForTheScalingSignal(t *testing.T) {
	cfg, err := loadConfig()
	if err != nil {
		t.Fatalf("loadConfig: %v", err)
	}
	if cfg.PoolBufferSlots != 1 {
		t.Errorf("PoolBufferSlots = %d, want 1", cfg.PoolBufferSlots)
	}
	if cfg.InternalPort != cfg.Port {
		t.Errorf("InternalPort = %q, want the listening port %q", cfg.InternalPort, cfg.Port)
	}
	if cfg.OpTimeoutSec != 0 {
		t.Errorf("OpTimeoutSec = %d, want 0 (the job's own timeout)", cfg.OpTimeoutSec)
	}
	if cfg.ProxyTimeout <= 0 {
		t.Errorf("ProxyTimeout = %s, want a positive hop budget", cfg.ProxyTimeout)
	}
}

// ── op timeouts ─────────────────────────────────────────────────────────────

func TestOpTimeoutFollowsTheJobUnlessTheEngineAsksForLess(t *testing.T) {
	g := &gateway{cfg: appConfig{}}
	caps := protocol.Capabilities{Slots: 1}
	job := Job{SpecJSON: `{"timeoutSec":900}`}

	if got := g.opTimeout(job, caps, 0); got != 900*time.Second {
		t.Errorf("opTimeout with no request = %s, want the job's 900s", got)
	}
	if got := g.opTimeout(job, caps, 30); got != 30*time.Second {
		t.Errorf("opTimeout(30) = %s, want the smaller value", got)
	}
	// A request above the protocol's cap cannot outlive a session.
	if got := g.opTimeout(job, caps, jobTimeoutMaxSec*4); got != 900*time.Second {
		t.Errorf("opTimeout(oversized) = %s, want the job's own timeout", got)
	}
	// The operator's cap wins over everything.
	g.cfg.OpTimeoutSec = 15
	if got := g.opTimeout(job, caps, 0); got != 15*time.Second {
		t.Errorf("opTimeout with a cap = %s, want 15s", got)
	}
	// A job with no readable spec still gets a deadline.
	g.cfg.OpTimeoutSec = 0
	if got := g.opTimeout(Job{SpecJSON: "not json"}, caps, 0); got != time.Duration(jobTimeoutDefaultSec)*time.Second {
		t.Errorf("opTimeout without a spec = %s, want the default", got)
	}
}
