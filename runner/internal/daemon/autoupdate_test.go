package daemon

import (
	"context"
	"encoding/json"
	"errors"
	"log"
	"os"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/identity"
	"github.com/vecchiotom/reelbolt/runner/internal/update"
)

// fakeUpdater stands in for the signed auto-update. It records whether it was
// asked to run, whether the daemon installed a job-activity gate, and what the
// gate said when the apply was attempted.
type fakeUpdater struct {
	calls   int
	gate    update.Gate
	busyAt  bool
	outcome update.Outcome
	err     error
}

func (f *fakeUpdater) SetGate(g update.Gate) { f.gate = g }

func (f *fakeUpdater) RunOnce(context.Context) (update.Outcome, error) {
	f.calls++
	if f.gate != nil {
		f.busyAt = f.gate.Busy()
	}
	return f.outcome, f.err
}

// fakeSession is a job session that does nothing but exist, so the in-flight
// count can be exercised.
type fakeSession struct{ closed int }

func (s *fakeSession) Handle(context.Context, string, json.RawMessage) (any, *protocol.OpError) {
	return nil, nil
}
func (s *fakeSession) Close(context.Context) { s.closed++ }

func TestJobsInFlightAreCounted(t *testing.T) {
	// The count is what makes "never update mid-job" enforceable: it is taken
	// when the agent creates a session and released when the agent closes it.
	d := &Daemon{act: &activity{}}
	session := &fakeSession{}
	s := sessions{
		media: agent.FactoryFunc(func(context.Context, *agent.Job) (agent.Session, error) {
			return session, nil
		}),
		act: d.act,
	}
	if d.Busy() {
		t.Fatal("a daemon with no job is busy")
	}
	live, err := s.New(context.Background(), &agent.Job{JobType: protocol.JobVideoCompile})
	if err != nil {
		t.Fatalf("sessions.New: %v", err)
	}
	if !d.Busy() {
		t.Fatal("a daemon running a job is not busy")
	}
	live.Close(context.Background())
	if d.Busy() {
		t.Fatal("a daemon whose job ended is still busy")
	}
	// Close is documented as called exactly once, and the guard makes a second
	// call harmless rather than corrupting the count.
	live.Close(context.Background())
	if d.Busy() {
		t.Fatal("a double close corrupted the in-flight count")
	}
	if session.closed != 1 {
		t.Fatalf("the underlying session was closed %d times", session.closed)
	}
}

func TestAFailedSessionCreationIsNotCounted(t *testing.T) {
	d := &Daemon{act: &activity{}}
	s := sessions{
		media: agent.FactoryFunc(func(context.Context, *agent.Job) (agent.Session, error) {
			return nil, errors.New("no runtime")
		}),
		act: d.act,
	}
	if _, err := s.New(context.Background(), &agent.Job{JobType: protocol.JobVideoCompile}); err == nil {
		t.Fatal("a failing executor returned no error")
	}
	if d.Busy() {
		t.Fatal("a session that was never created is counted as in flight")
	}
}

func TestAnUnknownJobTypeIsNotCounted(t *testing.T) {
	d := &Daemon{act: &activity{}}
	s := sessions{act: d.act}
	if _, err := s.New(context.Background(), &agent.Job{JobType: "unknown.job"}); err == nil {
		t.Fatal("an unknown job type was accepted")
	}
	if d.Busy() {
		t.Fatal("a refused job is counted as in flight")
	}
}

func TestTheDaemonInstallsItsJobGateOnTheUpdater(t *testing.T) {
	// The gate is handed to the updater rather than checked only by the daemon,
	// so the refusal is enforced at the moment of the swap.
	updater := &fakeUpdater{}
	var cfg = pairedConfig(t)
	dir := isolatedConfigDir(t)
	d, err := New(Options{
		Config:  cfg,
		Keys:    identity.Open(dir, nil),
		Logger:  log.New(os.Stderr, "test: ", 0),
		Prober:  &fakeProber{result: mediaOnlyResult()},
		Agent:   newFakeRunner(nil),
		Updater: updater,
	})
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	d.Busy()
	if updater.gate == nil {
		t.Fatal("the daemon did not install a job gate on the updater")
	}
	if updater.gate.Busy() {
		t.Fatal("the gate says the daemon is busy before any job")
	}
}

func TestTheUpdateLoopSkipsWhileAJobRuns(t *testing.T) {
	updater := &fakeUpdater{outcome: update.Outcome{Applied: true, Version: "1.0.0"}}
	d := &Daemon{updater: updater, act: &activity{}, stopRequest: make(chan struct{}, 1)}

	d.act.begin()
	d.tryUpdate(context.Background())
	if updater.calls != 0 {
		t.Fatalf("the updater ran %d times with a job in flight", updater.calls)
	}
	d.act.end()

	d.tryUpdate(context.Background())
	if updater.calls != 1 {
		t.Fatalf("the updater ran %d times with no job, want 1", updater.calls)
	}
	if updater.busyAt {
		t.Fatal("the updater was asked to apply while the gate said busy")
	}
}

func TestASuccessfulUpdateRequestsAStop(t *testing.T) {
	// An installed update that never runs is not an update. The daemon stops so
	// a supervisor restarts it on the new binary.
	updater := &fakeUpdater{outcome: update.Outcome{Applied: true, Version: "1.0.0", Reason: "installed"}}
	d := &Daemon{updater: updater, act: &activity{}, stopRequest: make(chan struct{}, 1)}

	d.tryUpdate(context.Background())
	select {
	case <-d.stopRequest:
	default:
		t.Fatal("a successful update did not request a stop")
	}
	// A second attempt in the same process is a no-op: the new binary only
	// takes over at the next start.
	d.tryUpdate(context.Background())
	if updater.calls != 1 {
		t.Fatalf("the updater ran %d times after a successful update", updater.calls)
	}
}

func TestAFailedUpdateDoesNotStopTheDaemon(t *testing.T) {
	for _, outcome := range []update.Outcome{
		{Action: update.ActionHoldBack, Reason: "not this device"},
		{Action: update.ActionUpToDate, Reason: "already current"},
		{Action: update.ActionRefuseDowngrade, Reason: "older"},
	} {
		updater := &fakeUpdater{outcome: outcome}
		d := &Daemon{updater: updater, act: &activity{}, stopRequest: make(chan struct{}, 1)}
		d.tryUpdate(context.Background())
		select {
		case <-d.stopRequest:
			t.Fatalf("%s stopped the daemon", outcome.Action)
		default:
		}
		if updater.calls != 1 {
			t.Fatalf("the updater ran %d times", updater.calls)
		}
	}
}

func TestAnUnreachableUpdateHostIsNotFatal(t *testing.T) {
	// The runner serves the jobs it has, whatever the download server is doing.
	updater := &fakeUpdater{err: update.ErrHostUnreachable}
	d := &Daemon{updater: updater, act: &activity{}, stopRequest: make(chan struct{}, 1)}
	d.tryUpdate(context.Background())
	select {
	case <-d.stopRequest:
		t.Fatal("an unreachable update host stopped the daemon")
	default:
	}
	if updater.calls != 1 {
		t.Fatalf("the updater ran %d times", updater.calls)
	}
}

func TestNoUpdaterMeansNoUpdateWork(t *testing.T) {
	d := &Daemon{act: &activity{}, stopRequest: make(chan struct{}, 1)}
	d.tryUpdate(context.Background()) // must not panic
	if d.updateInterval() != DefaultUpdateInterval {
		t.Fatalf("updateInterval = %s, want the default", d.updateInterval())
	}
	d.updateEvery = time.Minute
	if d.updateInterval() != time.Minute {
		t.Fatalf("updateInterval = %s, want the configured one", d.updateInterval())
	}
}

func TestAStopRequestEndsTheUpdateLoop(t *testing.T) {
	// shouldStop consumes the request, so the loop's own guard sees it before
	// the timer fires again.
	updater := &fakeUpdater{outcome: update.Outcome{Applied: true, Version: "1.0.0"}}
	d := &Daemon{updater: updater, act: &activity{}, stopRequest: make(chan struct{}, 1)}
	d.requestStop()
	if !d.shouldStop() {
		t.Fatal("a requested stop was not seen")
	}
	if d.shouldStop() {
		t.Fatal("the stop request was not consumed")
	}
}
