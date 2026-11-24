package container

import (
	"context"
	"errors"
	"strings"
	"sync"
	"testing"
	"time"
)

// recordingRunner wraps fakeWorkspaceRunner, records every CLI call and lets a
// test intercept chosen ones. The intercept runs outside the recorder's lock so
// it may block.
type recordingRunner struct {
	mu        sync.Mutex
	calls     []string
	base      Runner
	intercept func(args []string) ([]byte, error, bool)
}

func (r *recordingRunner) run(ctx context.Context, args ...string) ([]byte, error) {
	r.mu.Lock()
	r.calls = append(r.calls, strings.Join(args, " "))
	r.mu.Unlock()
	if r.intercept != nil {
		if out, err, handled := r.intercept(args); handled {
			return out, err
		}
	}
	return r.base(ctx, args...)
}

func (r *recordingRunner) record(event string) {
	r.mu.Lock()
	r.calls = append(r.calls, event)
	r.mu.Unlock()
}

// count returns how many recorded calls begin with prefix.
func (r *recordingRunner) count(prefix string) int {
	r.mu.Lock()
	defer r.mu.Unlock()
	n := 0
	for _, c := range r.calls {
		if strings.HasPrefix(c, prefix) {
			n++
		}
	}
	return n
}

func (r *recordingRunner) snapshot() []string {
	r.mu.Lock()
	defer r.mu.Unlock()
	return append([]string(nil), r.calls...)
}

// isCommandExec reports whether args are the `exec -w /workspace ...` of the
// command itself, as opposed to the headless-shell probe (`exec <name> sh -c`).
func isCommandExec(args []string) bool {
	return len(args) > 1 && args[0] == "exec" && args[1] == "-w"
}

func newJanitorDriver(t *testing.T, r *recordingRunner) (*Driver, string) {
	t.Helper()
	r.base = fakeWorkspaceRunner(t)
	d := NewWithRunner(Config{
		SandboxRoot:  t.TempDir(),
		MaxSandboxes: 4,
		SandboxTTL:   time.Hour,
		ExecTimeout:  time.Minute,
	}, "docker", r.run)
	execution := "11111111-2222-4333-8444-555555555555"
	if _, _, err := d.Create(execution); err != nil {
		t.Fatalf("Create: %v", err)
	}
	return d, execution
}

// ageSandbox backdates LastActivity as if the sandbox had been quiet for idle.
func ageSandbox(d *Driver, execution string, idle time.Duration) {
	d.mu.Lock()
	defer d.mu.Unlock()
	sb, _ := d.lookupLocked(execution)
	sb.LastActivity = time.Now().UTC().Add(-idle)
}

func sandboxState(d *Driver, execution string) (stopped bool, inFlight int, present bool) {
	d.mu.RLock()
	defer d.mu.RUnlock()
	sb, ok := d.lookupLocked(execution)
	if !ok {
		return false, 0, false
	}
	return sb.IsStopped, sb.inFlight, true
}

// A render outlasting the 5-minute idle rule must not have its container
// stopped under it (the exit 137 at "Rendered 724/3555"), nor deleted by the
// TTL sweep; once it ends the normal idle rules apply again.
func TestJanitorLeavesASandboxWithAnExecInFlight(t *testing.T) {
	started := make(chan struct{})
	finish := make(chan struct{})
	rec := &recordingRunner{}
	rec.intercept = func(args []string) ([]byte, error, bool) {
		if isCommandExec(args) {
			close(started)
			<-finish
			return []byte("rendered"), nil, true
		}
		return nil, nil, false
	}
	d, execution := newJanitorDriver(t, rec)

	type result struct {
		out string
		err error
	}
	done := make(chan result, 1)
	go func() {
		out, err := d.RunExec(execution, ExecRequest{Command: "npx", Args: []string{"remotion", "render", "Main"}})
		done <- result{out, err}
	}()
	<-started

	// Stale well past both the idle-stop window and the TTL.
	ageSandbox(d, execution, 2*time.Hour)
	d.cleanupInactive()

	if rec.count("stop ") != 0 || rec.count("rm ") != 0 {
		t.Fatalf("the janitor touched a sandbox with an exec in flight: %v", rec.snapshot())
	}
	if stopped, inFlight, present := sandboxState(d, execution); !present || stopped || inFlight != 1 {
		t.Fatalf("state = stopped:%v inFlight:%d present:%v, want running with 1 exec in flight", stopped, inFlight, present)
	}

	close(finish)
	res := <-done
	if res.err != nil || res.out != "rendered" {
		t.Fatalf("RunExec = %q, %v", res.out, res.err)
	}
	if _, inFlight, _ := sandboxState(d, execution); inFlight != 0 {
		t.Fatalf("inFlight = %d after the exec returned, want 0", inFlight)
	}

	// The exec's end counts as activity: right after it, nothing is idle.
	d.cleanupInactive()
	if rec.count("stop ") != 0 || rec.count("rm ") != 0 {
		t.Fatalf("the janitor acted on a sandbox that had just finished an exec: %v", rec.snapshot())
	}
}

// An exec that fails must still give its in-flight slot back, or the sandbox
// could never be reaped.
func TestFailedExecReleasesItsInFlightSlot(t *testing.T) {
	rec := &recordingRunner{}
	rec.intercept = func(args []string) ([]byte, error, bool) {
		if isCommandExec(args) {
			return []byte("build failed"), errors.New("exit status 1"), true
		}
		return nil, nil, false
	}
	d, execution := newJanitorDriver(t, rec)
	if _, err := d.RunExec(execution, ExecRequest{Command: "npm", Args: []string{"run", "build"}}); err == nil {
		t.Fatal("expected the failing exec to return an error")
	}
	if _, inFlight, _ := sandboxState(d, execution); inFlight != 0 {
		t.Fatalf("inFlight = %d after a failed exec, want 0", inFlight)
	}
}

// Idle behaviour is unchanged: stopped after 5 minutes, removed past the TTL.
func TestJanitorStillStopsAndDeletesIdleSandboxes(t *testing.T) {
	rec := &recordingRunner{}
	d, execution := newJanitorDriver(t, rec)

	d.cleanupInactive()
	if rec.count("stop ") != 0 {
		t.Fatalf("a fresh sandbox was stopped: %v", rec.snapshot())
	}

	ageSandbox(d, execution, 10*time.Minute)
	d.cleanupInactive()
	if rec.count("stop ") != 1 {
		t.Fatalf("an idle sandbox was not stopped: %v", rec.snapshot())
	}
	if stopped, _, present := sandboxState(d, execution); !present || !stopped {
		t.Fatalf("idle sandbox state = stopped:%v present:%v, want stopped and still registered", stopped, present)
	}

	ageSandbox(d, execution, 2*time.Hour)
	d.cleanupInactive()
	if rec.count("rm -f ") != 1 {
		t.Fatalf("a sandbox past the TTL was not removed: %v", rec.snapshot())
	}
	if _, _, present := sandboxState(d, execution); present {
		t.Fatal("a sandbox past the TTL is still registered")
	}
}

// A restart arriving while `docker stop` is still completing must wait for it:
// starting first and being stopped afterwards is what left a dead container
// that answered every exec with "is not running".
func TestRestartWaitsForAnInProgressStop(t *testing.T) {
	stopBegan := make(chan struct{})
	releaseStop := make(chan struct{})
	rec := &recordingRunner{}
	rec.intercept = func(args []string) ([]byte, error, bool) {
		switch args[0] {
		case "stop":
			close(stopBegan)
			<-releaseStop
			rec.record("event:stop-returned")
			return nil, nil, true
		case "start":
			rec.record("event:start-issued")
		}
		return nil, nil, false
	}
	d, execution := newJanitorDriver(t, rec)
	ageSandbox(d, execution, 10*time.Minute)

	janitorDone := make(chan struct{})
	go func() {
		d.cleanupInactive()
		close(janitorDone)
	}()
	<-stopBegan

	// While the stop is outstanding, both entry points that restart a stopped
	// sandbox are called.
	createDone := make(chan error, 1)
	go func() {
		_, _, err := d.Create(execution)
		createDone <- err
	}()
	getDone := make(chan error, 1)
	go func() {
		_, err := d.GetByExecution(execution)
		getDone <- err
	}()

	time.Sleep(150 * time.Millisecond)
	if rec.count("start ") != 0 {
		t.Fatalf("docker start was issued while docker stop was still running: %v", rec.snapshot())
	}

	close(releaseStop)
	<-janitorDone
	if err := <-createDone; err != nil {
		t.Fatalf("Create: %v", err)
	}
	if err := <-getDone; err != nil {
		t.Fatalf("GetByExecution: %v", err)
	}

	var order []string
	for _, c := range rec.snapshot() {
		if c == "event:stop-returned" || strings.HasPrefix(c, "start ") {
			order = append(order, c)
		}
	}
	if len(order) != 2 || order[0] != "event:stop-returned" || !strings.HasPrefix(order[1], "start ") {
		t.Fatalf("expected exactly stop-returned then one start, got %v (all: %v)", order, rec.snapshot())
	}
	if stopped, _, _ := sandboxState(d, execution); stopped {
		t.Fatal("the sandbox is still marked stopped after the restart")
	}
}

// The janitor must not select a sandbox that is mid-transition, and a stop
// requested twice at once must run `docker stop` once.
func TestConcurrentStopsRunDockerStopOnce(t *testing.T) {
	release := make(chan struct{})
	began := make(chan struct{}, 2)
	rec := &recordingRunner{}
	rec.intercept = func(args []string) ([]byte, error, bool) {
		if args[0] == "stop" {
			began <- struct{}{}
			<-release
			return nil, nil, true
		}
		return nil, nil, false
	}
	d, execution := newJanitorDriver(t, rec)

	var wg sync.WaitGroup
	wg.Add(1)
	go func() {
		defer wg.Done()
		if err := d.StopByExecution(execution); err != nil {
			t.Errorf("StopByExecution: %v", err)
		}
	}()
	<-began
	ageSandbox(d, execution, 10*time.Minute)
	d.cleanupInactive() // skips: a transition is in progress
	close(release)
	wg.Wait()
	if n := rec.count("stop "); n != 1 {
		t.Fatalf("docker stop ran %d times, want 1: %v", n, rec.snapshot())
	}
}

const daemonNotRunning = "Error response from daemon: container rf-sbx-x is not running"

// Defence in depth: if the container is down despite the driver's bookkeeping,
// an exec that answers "is not running" brings it back and runs once more.
func TestExecRevivesAContainerThatIsNotRunning(t *testing.T) {
	var mu sync.Mutex
	execAttempts := 0
	rec := &recordingRunner{}
	rec.intercept = func(args []string) ([]byte, error, bool) {
		if isCommandExec(args) {
			mu.Lock()
			execAttempts++
			n := execAttempts
			mu.Unlock()
			if n == 1 {
				return []byte(daemonNotRunning), errors.New("exit status 1"), true
			}
			return []byte("ok"), nil, true
		}
		return nil, nil, false
	}
	d, execution := newJanitorDriver(t, rec)

	out, err := d.RunExec(execution, ExecRequest{Command: "npm", Args: []string{"run", "build"}})
	if err != nil || out != "ok" {
		t.Fatalf("RunExec = %q, %v; want the retry to succeed", out, err)
	}
	if rec.count("start ") != 1 {
		t.Fatalf("expected one docker start, got: %v", rec.snapshot())
	}
	if stopped, inFlight, _ := sandboxState(d, execution); stopped || inFlight != 0 {
		t.Fatalf("state = stopped:%v inFlight:%d, want running and idle", stopped, inFlight)
	}
}

// When the container stays down, the error is explicit about what happened and
// what to do, and carries the sentinel the HTTP layer maps to a code.
func TestExecReportsASandboxThatStaysDown(t *testing.T) {
	rec := &recordingRunner{}
	rec.intercept = func(args []string) ([]byte, error, bool) {
		if isCommandExec(args) {
			return []byte(daemonNotRunning), errors.New("exit status 1"), true
		}
		return nil, nil, false
	}
	d, execution := newJanitorDriver(t, rec)

	_, err := d.RunExec(execution, ExecRequest{Command: "npm", Args: []string{"run", "build"}})
	if !errors.Is(err, ErrSandboxNotRunning) {
		t.Fatalf("err = %v, want ErrSandboxNotRunning", err)
	}
	if !strings.Contains(err.Error(), "retry") {
		t.Fatalf("the error does not tell the model to retry: %v", err)
	}
	if rec.count("start ") != 1 {
		t.Fatalf("expected exactly one restart attempt, got: %v", rec.snapshot())
	}
}

// A command that merely prints the words must not be mistaken for a down container.
func TestIsNotRunningIgnoresCommandOutput(t *testing.T) {
	if !isNotRunning(daemonNotRunning) {
		t.Error("the daemon's refusal was not recognised")
	}
	if isNotRunning("the dev server is not running") {
		t.Error("ordinary output containing the words was treated as a refusal")
	}
	if isNotRunning(strings.Repeat("x", 2000) + daemonNotRunning) {
		t.Error("long command output was treated as a refusal")
	}
}
