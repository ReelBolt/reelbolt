package daemon

import (
	"context"
	"log"
	"os"
	"path/filepath"
	"sync"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/governor"
	"github.com/vecchiotom/reelbolt/runner/internal/identity"
	"github.com/vecchiotom/reelbolt/runner/internal/runtime"
)

// ---- fixtures ----

// agentFactory hands out a fresh fake worker agent per connection, which is
// what a resume needs: the real worker agent cannot un-drain itself, so coming
// back from a pause means a new one.
type agentFactory struct {
	mu      sync.Mutex
	built   int
	runners []*fakeRunner
}

func (f *agentFactory) New() (Runner, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	r := newFakeRunner(nil)
	f.runners = append(f.runners, r)
	f.built++
	return r, nil
}

func (f *agentFactory) count() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.built
}

func (f *agentFactory) last() *fakeRunner {
	f.mu.Lock()
	defer f.mu.Unlock()
	if len(f.runners) == 0 {
		return nil
	}
	return f.runners[len(f.runners)-1]
}

// governorDaemon builds a daemon whose control channel and machine reader are
// test doubles.
func governorDaemon(t *testing.T, cfg *config.Config, probe ProbeResult, machine governor.Signals, factory *agentFactory) (*Daemon, *governor.Control) {
	t.Helper()
	dir := isolatedConfigDir(t)
	t.Setenv("XDG_CACHE_HOME", dir)
	keys := identity.Open(dir, nil)
	d, err := New(Options{
		Config:       cfg,
		Keys:         keys,
		Logger:       log.New(os.Stderr, "test: ", 0),
		Prober:       &fakeProber{result: probe},
		Machine:      machine,
		Interval:     20 * time.Millisecond,
		AgentFactory: func() (Runner, error) { return factory.New() },
	})
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	control, err := config.CacheDir()
	if err != nil {
		t.Fatal(err)
	}
	return d, &governor.Control{
		RequestPath: filepath.Join(control, "governor.json"),
		LivePath:    filepath.Join(control, "runner.live.json"),
	}
}

// runningMachine is a machine that is on mains and whose user is present.
func runningMachine() *fakeMachine { return &fakeMachine{onAC: true, powerOK: true, idleOK: true} }

// fakeMachine is the daemon's copy of the governor's Signals, so the daemon
// tests do not reach for the real D-Bus or sysfs.
// fakeMachine is the daemon copy of the governor Signals, so the daemon
// tests do not reach for the real D-Bus or sysfs. The governor reads it from
// its own goroutine, so a test that changes it has to take the lock.
type fakeMachine struct {
	mu      sync.Mutex
	idle    time.Duration
	idleOK  bool
	onAC    bool
	powerOK bool
}

func (f *fakeMachine) set(idle time.Duration) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.idle = idle
}

func (f *fakeMachine) IdleFor() (time.Duration, bool) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.idle, f.idleOK
}

func (f *fakeMachine) OnAC() (bool, bool) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.onAC, f.powerOK
}

func (f *fakeMachine) Describe() string { return "a test machine" }

func waitFor(t *testing.T, what string, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(10 * time.Second)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("timed out waiting for %s", what)
}

// ---- the pause/resume cycle ----

// The whole point of the governor in one test: a pause takes the runner off the
// gateway (drain, then shutdown), it stays off, and a resume brings a NEW agent
// back — the old one cannot be un-drained.
func TestAPauseTakesTheRunnerOffTheGatewayAndResumeBringsItBack(t *testing.T) {
	cfg := pairedConfig(t)
	factory := &agentFactory{}
	d, _ := governorDaemon(t, cfg, mediaOnlyResult(), runningMachine(), factory)

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	served := make(chan error, 1)
	go func() { served <- d.run(ctx, make(chan os.Signal, 1)) }()

	waitFor(t, "the first agent to start", func() bool { return factory.count() == 1 })
	first := factory.last()
	<-first.started

	d.Pause("the test asked")
	waitFor(t, "the first agent to be drained", func() bool {
		return len(first.order()) >= 3
	})
	order := first.order()
	want := []string{"drain:" + ControlPauseReason, "shutdown", "run-returned"}
	for i := range want {
		if i >= len(order) || order[i] != want[i] {
			t.Fatalf("pause sequence = %v, want %v", order, want)
		}
	}

	// Paused: no second connection, and the daemon says so.
	time.Sleep(100 * time.Millisecond)
	if factory.count() != 1 {
		t.Fatalf("a paused runner must not reconnect (built %d agents)", factory.count())
	}
	st := d.Governor().Status()
	if st.Allowed || !st.Held || st.HeldBy != governor.SourceUser {
		t.Fatalf("status = %+v", st)
	}

	d.Resume()
	waitFor(t, "a new agent after the resume", func() bool { return factory.count() == 2 })

	cancel()
	select {
	case err := <-served:
		if err != nil {
			t.Fatalf("run = %v", err)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("run did not return after the context ended")
	}
}

// A runner the schedule has stopped never connects at all: a machine that is
// not working should not be holding a socket, a heartbeat and a lease.
func TestAScheduledRunnerNeverConnectsUntilTheScheduleAllows(t *testing.T) {
	cfg := pairedConfig(t)
	schedule := governor.Schedule{Mode: governor.ModeIdle, IdleMinutes: 5}
	schedule.ApplyDefaults()
	cfg.Schedule = schedule
	machine := &fakeMachine{idleOK: true, idle: 0, onAC: true, powerOK: true}
	factory := &agentFactory{}
	d, _ := governorDaemon(t, cfg, mediaOnlyResult(), machine, factory)

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	served := make(chan error, 1)
	go func() { served <- d.run(ctx, make(chan os.Signal, 1)) }()

	time.Sleep(200 * time.Millisecond)
	if factory.count() != 0 {
		t.Fatalf("a runner whose user is present must not connect (built %d)", factory.count())
	}
	if st := d.Governor().Status(); st.Allowed || st.Reason != governor.ReasonUserActive {
		t.Fatalf("status = %+v", st)
	}

	// The user walks away.
	machine.set(30 * time.Minute)
	waitFor(t, "the runner to connect once the user left", func() bool { return factory.count() == 1 })

	// And coming back stops it again.
	machine.set(0)
	waitFor(t, "the runner to stop once the user returned", func() bool {
		return !d.Governor().Allowed()
	})
	waitFor(t, "the agent to be taken off the gateway", func() bool {
		r := factory.last()
		return r != nil && len(r.order()) >= 2 && r.order()[1] == "shutdown"
	})

	// A signal while paused is a stop, not a hang.
	cancel()
	select {
	case err := <-served:
		if err != nil {
			t.Fatalf("run = %v", err)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("a stopped signal while paused must end the run")
	}
}

// ---- the local trigger ----

// The command line's pause reaches a running daemon through one small file.
func TestAControlRequestPausesARunningRunner(t *testing.T) {
	cfg := pairedConfig(t)
	factory := &agentFactory{}
	d, control := governorDaemon(t, cfg, mediaOnlyResult(), runningMachine(), factory)

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	served := make(chan error, 1)
	go func() { served <- d.run(ctx, make(chan os.Signal, 1)) }()
	waitFor(t, "the agent to start", func() bool { return factory.count() == 1 })

	if err := control.Request(governor.ActionPause, "the test", "because"); err != nil {
		t.Fatal(err)
	}
	waitFor(t, "the daemon to consume the request", func() bool { return !d.Governor().Allowed() })
	waitFor(t, "the agent to be taken off the gateway", func() bool {
		order := factory.last().order()
		return len(order) >= 2 && order[1] == "shutdown"
	})
	if _, err := os.Stat(control.RequestPath); !os.IsNotExist(err) {
		t.Fatal("a consumed request must not be left on disk")
	}

	// And the command line can bring it back.
	if err := control.Request(governor.ActionResume, "the test", ""); err != nil {
		t.Fatal(err)
	}
	waitFor(t, "the runner to come back", func() bool { return factory.count() == 2 })
	cancel()
	<-served
}

// The failure mode this feature is written around: an unreadable control
// request must be dropped, and must never leave the machine paused.
func TestACorruptControlRequestDoesNotPause(t *testing.T) {
	cfg := pairedConfig(t)
	factory := &agentFactory{}
	d, control := governorDaemon(t, cfg, mediaOnlyResult(), runningMachine(), factory)

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	served := make(chan error, 1)
	go func() { served <- d.run(ctx, make(chan os.Signal, 1)) }()
	waitFor(t, "the agent to start", func() bool { return factory.count() == 1 })

	if err := os.WriteFile(control.RequestPath, []byte("{not json"), 0o600); err != nil {
		t.Fatal(err)
	}
	// The watcher polls once a second: give it two passes and then some.
	time.Sleep(2500 * time.Millisecond)
	if !d.Governor().Allowed() {
		t.Fatalf("a corrupt request paused the runner: %+v", d.Governor().Status())
	}
	if _, err := os.Stat(control.RequestPath); !os.IsNotExist(err) {
		t.Fatal("the corrupt request was not consumed, so it would be retried for ever")
	}
	cancel()
	<-served
}

// And the other half: a request that was never consumed — because the process
// that would have consumed it died — must not pause the NEXT process.
func TestAStaleControlRequestIsIgnoredByAFreshRunner(t *testing.T) {
	cfg := pairedConfig(t)
	factory := &agentFactory{}
	d, control := governorDaemon(t, cfg, mediaOnlyResult(), runningMachine(), factory)

	if err := control.Request(governor.ActionPause, "a previous run", "stale"); err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	served := make(chan error, 1)
	go func() { served <- d.run(ctx, make(chan os.Signal, 1)) }()

	waitFor(t, "the runner to connect despite a stale pause request", func() bool { return factory.count() == 1 })
	if !d.Governor().Allowed() {
		t.Fatalf("a pause request from an earlier process stopped a fresh runner: %+v", d.Governor().Status())
	}
	cancel()
	<-served
}

// ---- the live document ----

// "status" and the desktop shell read one document; it has to be there while
// the daemon runs and gone once it stops, or a command would act on a stale
// answer.
func TestTheRunnerPublishesItsStateWhileItRuns(t *testing.T) {
	cfg := pairedConfig(t)
	factory := &agentFactory{}
	d, control := governorDaemon(t, cfg, mediaOnlyResult(), runningMachine(), factory)

	ctx, cancel := context.WithCancel(context.Background())
	served := make(chan error, 1)
	go func() { served <- d.run(ctx, make(chan os.Signal, 1)) }()
	waitFor(t, "the agent to start", func() bool { return factory.count() == 1 })

	live, err := control.ReadLive()
	if err != nil {
		t.Fatalf("ReadLive: %v", err)
	}
	if live.PID != os.Getpid() || !live.Fresh(time.Now()) {
		t.Fatalf("live = %+v", live)
	}
	if !live.Status.Allowed || live.Status.Schedule == "" || live.Status.Mode != governor.ModeAlways {
		t.Fatalf("the live document must carry the governor's state: %+v", live.Status)
	}

	d.Pause("the test asked")
	waitFor(t, "the live document to report the pause", func() bool {
		l, err := control.ReadLive()
		return err == nil && l.Status.Held && !l.Status.Allowed
	})

	cancel()
	select {
	case <-served:
	case <-time.After(10 * time.Second):
		t.Fatal("run did not return")
	}
	if _, err := os.Stat(control.LivePath); !os.IsNotExist(err) {
		t.Fatal("a stopped runner must remove its live document")
	}
}

// ---- limits ----

// The caps come from runner.json, not from a constant: this is the assertion
// that E5's hard-coded 4g / 2 cpus are actually gone.
func TestLimitsComeFromTheConfiguration(t *testing.T) {
	cfg := pairedConfig(t)
	cfg.Limits = governor.LimitsConfig{
		Memory:         2 << 30,
		CPUs:           1.5,
		PIDs:           128,
		FFmpegThreads:  1,
		Priority:       governor.PriorityLow,
		DiskQuotaBytes: 3 << 30,
	}
	cfg.ApplyDefaults()
	d, _ := governorDaemon(t, cfg, mediaOnlyResult(), runningMachine(), &agentFactory{})

	l := d.Limits()
	if l.MemoryArg() != "2g" || l.CPUArg() != "1.5" || l.PIDs != 128 || l.FFmpegThreads != 1 ||
		l.Priority != governor.PriorityLow || l.DiskQuotaBytes != 3<<30 {
		t.Fatalf("limits = %s", l.Describe())
	}
	// And the agent's own scratch quota is the same number, rather than the
	// library's unrelated default.
	if d.limits.DiskQuotaBytes != 3<<30 {
		t.Fatalf("the scratch quota is not the configured one: %+v", d.limits)
	}
}

// A workspace executor that could be built without a memory or CPU cap is the
// bug this WP exists to close, so the constructor refuses one.
func TestAWorkspaceExecutorIsNeverBuiltWithoutLimits(t *testing.T) {
	_, err := newRemotionExecutor(workspaceConfig{
		Image: "reelbolt-sandbox-runtime:local",
		Root:  t.TempDir(),
		Slots: 1,
	}, runtime.Candidate{})
	if err == nil {
		t.Fatal("a workspace executor was built with no resource limits")
	}
}
