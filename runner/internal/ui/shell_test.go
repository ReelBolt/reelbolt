package ui

import (
	"context"
	"errors"
	"io"
	"log"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/daemon"
	"github.com/vecchiotom/reelbolt/runner/internal/governor"
	"github.com/vecchiotom/reelbolt/runner/internal/identity"
)

// ---- fakes ----

// quiet discards the shell log lines; a test that prints them is unreadable.
func quiet() *log.Logger { return log.New(io.Discard, "", 0) }

type fakeProber struct {
	res daemon.ProbeResult
	err error
}

func (f fakeProber) Probe(context.Context, daemon.ProbeRequest) (daemon.ProbeResult, error) {
	return f.res, f.err
}

func capableProbe() daemon.ProbeResult {
	return daemon.ProbeResult{
		Capabilities: protocol.Capabilities{
			AgentVersion:  "test",
			OS:            "linux",
			Arch:          "amd64",
			Slots:         1,
			JobTypes:      []string{protocol.JobVideoCompile},
			FfmpegVersion: "7.1",
		},
	}
}

// fakeRunner is the worker agent the daemon drives.
type fakeRunner struct {
	started chan struct{}
	once    bool
}

func newFakeRunner() *fakeRunner { return &fakeRunner{started: make(chan struct{})} }

func (f *fakeRunner) Run(ctx context.Context) error {
	if !f.once {
		f.once = true
		close(f.started)
	}
	<-ctx.Done()
	return nil
}

func (f *fakeRunner) Drain(string, time.Duration) error { return nil }
func (f *fakeRunner) Shutdown(context.Context) error    { return nil }

func testConfig(t *testing.T) *config.Config {
	t.Helper()
	cfg := &config.Config{
		DeviceID:       "dev-1",
		DeviceName:     "test-machine",
		OrganizationID: "org-1",
		GatewayURL:     "wss://runner.reelbolt.ai/v1/connect",
		// One second rather than five: a stop (and the restart that applies new
		// settings) waits this long, and the tests stop a lot of runners.
		DrainSeconds: 1,
	}
	cfg.ApplyDefaults()
	if err := cfg.Validate(); err != nil {
		t.Fatalf("test config is invalid: %v", err)
	}
	return cfg
}

func newTestShell(t *testing.T, mutate func(*Options)) (*Shell, func()) {
	t.Helper()
	dir := t.TempDir()
	opts := Options{
		Config:      testConfig(t),
		Keys:        identity.Open(dir, quiet()),
		Logger:      quiet(),
		Version:     "1.2.3",
		Prober:      fakeProber{res: capableProbe()},
		Agent:       newFakeRunner(),
		ControlDir:  dir,
		ScratchRoot: filepath.Join(dir, "jobs"),
		Autostart:   &fakeAutostart{},
		// A settings write from a view (E11's telemetry switch, and the
		// settings screen's Save) must land in this test's own directory: an
		// empty path means the developer's real runner.json, and a test that
		// rewrites that would be a very unpleasant surprise.
		ConfigPath: filepath.Join(dir, "runner.json"),
		// The media cache, likewise, lives in the test's directory and not in
		// the developer's own cache.
		MediaCacheDir: filepath.Join(dir, "media"),
		// Unpair asks ReelBolt to forget the device; a test never reaches it.
		ForgetMe: func(context.Context) error { return errors.New("offline in tests") },
	}
	if mutate != nil {
		mutate(&opts)
	}
	s, err := New(opts)
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cleanup := func() {
		cancel()
		s.StopRunner()
		s.CancelDownload()
		// Wait for the runner to finish stopping: it writes into the test's
		// directory until it has, and t.TempDir's cleanup races it otherwise.
		s.WaitStopped(15 * time.Second)
		s.Release()
	}
	s.Start(ctx)
	waitFor(t, 5*time.Second, func() bool { return s.View().Runner.Running })
	return s, cleanup
}

func waitFor(t *testing.T, limit time.Duration, ok func() bool) {
	t.Helper()
	deadline := time.Now().Add(limit)
	for time.Now().Before(deadline) {
		if ok() {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("the condition did not hold in time")
}

// ---- the shell ----

func TestShellRunsTheDaemonInProcessAndPauses(t *testing.T) {
	s, cleanup := newTestShell(t, nil)
	defer cleanup()

	v := s.View()
	if v.State != StateIdle {
		t.Fatalf("state = %q, want idle (%s)", v.State, v.Detail)
	}
	if !v.Runner.InProcess {
		t.Error("the shell does not report its own runner as in-process")
	}
	if !v.Runtime.Capable() {
		t.Fatalf("runtime not capable: %+v", v.Runtime)
	}

	if err := s.Pause("installing a driver"); err != nil {
		t.Fatalf("Pause: %v", err)
	}
	waitFor(t, 3*time.Second, func() bool { return s.View().State == StatePaused })
	v = s.View()
	if v.Remedy == nil || v.Remedy.Kind != RemedyResume {
		t.Fatalf("remedy = %+v, want resume", v.Remedy)
	}
	if !strings.Contains(v.Detail, "installing a driver") {
		t.Errorf("the reason the user gave is not shown: %q", v.Detail)
	}

	if err := s.Resume(); err != nil {
		t.Fatalf("Resume: %v", err)
	}
	waitFor(t, 3*time.Second, func() bool { return s.View().State != StatePaused })
}

// TestResumeMessageNamesWhatIsStillWrong is the case a user actually hits:
// resumed, and the result is not what they expected.
func TestResumeMessageNamesWhatIsStillWrong(t *testing.T) {
	v := Build(base())
	v.Runner.Held = true
	v.Runner.Allowed = false
	v.Runner.ReasonDetail = "installing a driver"
	if got := resumeMessage(v); !strings.Contains(got, "installing a driver") {
		t.Errorf("resume message = %q", got)
	}
	if got := resumeMessage(Build(base())); !strings.Contains(got, "taking work again") {
		t.Errorf("resume message = %q", got)
	}
}

// TestShellReflectsARunnerAnotherProcessStarted: a person looking at this
// machine does not care which process started the runner.
func TestShellReflectsARunnerAnotherProcessStarted(t *testing.T) {
	dir := t.TempDir()
	s, err := New(Options{
		MediaCacheDir: filepath.Join(dir, "media"),
		Config:        testConfig(t),
		Keys:          identity.Open(dir, quiet()),
		Logger:        quiet(),
		ControlDir:    dir,
		ScratchRoot:   filepath.Join(dir, "jobs"),
	})
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	// A live document written by a DIFFERENT process, exactly as
	// "reelbolt-runner run" publishes it.
	ctl := &governor.Control{
		RequestPath: filepath.Join(dir, "governor.json"),
		LivePath:    filepath.Join(dir, "runner.live.json"),
	}
	now := time.Now().UTC().Format(time.RFC3339)
	err = ctl.Publish(governor.Live{
		PID: os.Getpid() + 1, StartedAt: now, HeartbeatAt: now,
		Status: governor.Status{
			Allowed: false, Reason: "paused", Held: true,
			HeldBy: governor.SourceUser, HeldReason: "from the command line",
		},
	})
	if err != nil {
		t.Fatalf("publish: %v", err)
	}
	view := s.View()
	if !view.Runner.Running {
		t.Fatalf("state = %q; the shell ignored a runner that is running", view.State)
	}
	if view.Runner.InProcess {
		t.Error("the shell claimed a runner it did not start")
	}
	if view.State != StatePaused {
		t.Fatalf("state = %q, want paused", view.State)
	}
	if view.Remedy == nil || view.Remedy.Kind != RemedyResume {
		t.Fatalf("remedy = %+v, want resume", view.Remedy)
	}
}

func TestStopRunnerStopsItAndTheViewSaysSo(t *testing.T) {
	s, cleanup := newTestShell(t, nil)
	defer cleanup()
	s.StopRunner()
	// The daemon drains before it stops, waiting DrainSeconds (5s by default)
	// for in-flight work. That is the documented stop sequence, so the test
	// waits for it rather than shortening it.
	waitFor(t, 15*time.Second, func() bool { return !s.View().Runner.Running })
	view := s.View()
	if view.State != StateStopped {
		t.Fatalf("state = %q, want stopped", view.State)
	}
	if view.Remedy == nil || view.Remedy.Kind != RemedyStart {
		t.Fatalf("remedy = %+v, want start", view.Remedy)
	}
}

// ---- the single-instance lock ----

func TestTheSingleInstanceLockLetsOneShellIn(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "desktop.lock")
	first, err := TakeInstanceLock(path)
	if err != nil {
		t.Fatalf("first lock: %v", err)
	}
	if _, err := TakeInstanceLock(path); !errors.Is(err, ErrAlreadyRunning) {
		t.Fatalf("second lock = %v, want ErrAlreadyRunning", err)
	}
	if first.Holder() != os.Getpid() {
		t.Errorf("holder = %d, want this pid", first.Holder())
	}
	first.Release()
	second, err := TakeInstanceLock(path)
	if err != nil {
		t.Fatalf("lock after release: %v", err)
	}
	second.Release()
	second.Release()
}

// ---- the cache ----

func TestDirUsageCountsWhatIsThere(t *testing.T) {
	root := t.TempDir()
	if err := os.MkdirAll(filepath.Join(root, "job-1"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "job-1", "a.bin"), make([]byte, 100), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "loose.txt"), []byte("hello"), 0o600); err != nil {
		t.Fatal(err)
	}
	bytes, entries, err := DirUsage(root)
	if err != nil {
		t.Fatalf("DirUsage: %v", err)
	}
	if bytes != 105 {
		t.Errorf("bytes = %d, want 105", bytes)
	}
	if entries != 1 {
		t.Errorf("entries = %d, want the one directory", entries)
	}
	if b, e, err := DirUsage(filepath.Join(root, "nothing-here")); err != nil || b != 0 || e != 0 {
		t.Errorf("a missing root = (%d, %d, %v), want zeros", b, e, err)
	}
}

func TestClearCacheRemovesLeftovers(t *testing.T) {
	s, cleanup := newTestShell(t, nil)
	defer cleanup()
	scratch := s.scratchRoot()
	if err := os.MkdirAll(filepath.Join(scratch, "job-1"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(scratch, "job-1", "big.bin"), make([]byte, 2048), 0o600); err != nil {
		t.Fatal(err)
	}
	freed, err := s.ClearCache()
	if err != nil {
		t.Fatalf("ClearCache: %v", err)
	}
	if freed < 2048 {
		t.Errorf("freed = %d, want at least the file", freed)
	}
	entries, err := os.ReadDir(scratch)
	if err != nil && !errors.Is(err, os.ErrNotExist) {
		t.Fatal(err)
	}
	if len(entries) != 0 {
		t.Errorf("scratch still holds %d entries", len(entries))
	}
}

// TestClearCacheRefusesWhileAJobIsRunning: deleting a running job scratch does
// not free anything useful and does break the job.
func TestClearCacheRefusesWhileAJobIsRunning(t *testing.T) {
	s, cleanup := newTestShell(t, nil)
	defer cleanup()
	s.Ledger().ObserveJob(daemon.JobEvent{Kind: daemon.JobStarted, JobID: "j1", JobType: protocol.JobVideoCompile})
	if _, err := s.ClearCache(); !errors.Is(err, ErrBusy) {
		t.Fatalf("ClearCache = %v, want ErrBusy", err)
	}
	s.Ledger().ObserveJob(daemon.JobEvent{Kind: daemon.JobFinished, JobID: "j1"})
	if _, err := s.ClearCache(); err != nil {
		t.Fatalf("ClearCache after the job ended: %v", err)
	}
}

// ---- actions ----

func TestDoHandlesEveryActionTheViewsCanOffer(t *testing.T) {
	var opened []string
	shown, quit := 0, 0
	s, cleanup := newTestShell(t, func(o *Options) {
		// Port 1 refuses instantly, so the pairing action fails without a
		// network round trip rather than hanging the test.
		o.PairingBaseURL = "http://127.0.0.1:1"
	})
	defer cleanup()
	a := NewApp(s, AppOptions{
		OpenURL:      func(u string) error { opened = append(opened, u); return nil },
		ShowWindow:   func() { shown++ },
		Quit:         func() { quit++ },
		Autostart:    &fakeAutostart{},
		DashboardURL: "https://app.reelbolt.ai",
	})
	for _, action := range Actions() {
		_, err := a.Do(action, "")
		if err != nil && strings.Contains(err.Error(), "is not an action this build knows") {
			t.Errorf("Do does not handle %q, which Actions() advertises", action)
		}
	}
	if _, err := a.Do("not-an-action", ""); err == nil {
		t.Fatal("an unknown action was accepted")
	}
	if quit != 1 {
		t.Errorf("Quit ran %d times", quit)
	}
	if shown == 0 {
		t.Error("no navigation action showed the window")
	}
	if !hasString(opened, "https://app.reelbolt.ai") {
		t.Errorf("dashboard was not opened: %v", opened)
	}
}

func TestDoReturnsTheViewAfterTheAction(t *testing.T) {
	s, cleanup := newTestShell(t, nil)
	defer cleanup()
	a := NewApp(s, AppOptions{OpenURL: func(string) error { return nil }, Autostart: &fakeAutostart{}})
	res, err := a.Do(ActionPause, "test")
	if err != nil {
		t.Fatalf("Do: %v", err)
	}
	if !res.OK || res.Message == "" {
		t.Errorf("result = %+v", res)
	}
	if !res.View.State.Known() {
		t.Errorf("the result carries no view: %+v", res.View)
	}
}

func hasString(list []string, want string) bool {
	for _, s := range list {
		if s == want {
			return true
		}
	}
	return false
}
