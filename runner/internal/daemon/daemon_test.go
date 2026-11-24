package daemon

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"errors"
	"fmt"
	"log"
	"os"
	"path/filepath"
	"sync"
	"syscall"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/identity"
	"github.com/vecchiotom/reelbolt/runner/internal/runtime"
	"github.com/vecchiotom/reelbolt/runner/internal/runtimeimage"
)

// ---- test doubles ----

type fakeProber struct {
	result ProbeResult
	err    error
	got    ProbeRequest
}

func (f *fakeProber) Probe(_ context.Context, req ProbeRequest) (ProbeResult, error) {
	f.got = req
	return f.result, f.err
}

// fakeRunner is the worker agent as the daemon sees it: it blocks until the
// daemon shuts it down, and records the order of what happened.
type fakeRunner struct {
	mu       sync.Mutex
	events   []string
	runErr   error
	block    chan struct{}
	started  chan struct{}
	startOne sync.Once
}

func newFakeRunner(runErr error) *fakeRunner {
	return &fakeRunner{block: make(chan struct{}), started: make(chan struct{}), runErr: runErr}
}

func (f *fakeRunner) record(s string) {
	f.mu.Lock()
	f.events = append(f.events, s)
	f.mu.Unlock()
}

func (f *fakeRunner) Run(ctx context.Context) error {
	f.startOne.Do(func() { close(f.started) })
	if f.runErr != nil {
		// A terminal refusal ends the run before it ever settles:
		// that is what the agent does when the gateway closes 4400/4401/4403.
		return f.runErr
	}
	select {
	case <-f.block:
		f.record("run-returned")
	case <-ctx.Done():
		f.record("run-cancelled")
	}
	return f.runErr
}

func (f *fakeRunner) Drain(reason string, _ time.Duration) error {
	f.record("drain:" + reason)
	return nil
}

func (f *fakeRunner) Shutdown(_ context.Context) error {
	f.record("shutdown")
	f.mu.Lock()
	closed := false
	select {
	case <-f.block:
		closed = true
	default:
	}
	f.mu.Unlock()
	if !closed {
		close(f.block)
	}
	return nil
}

func (f *fakeRunner) order() []string {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([]string, len(f.events))
	copy(out, f.events)
	return out
}

func testPrivateKey(t *testing.T) ed25519.PrivateKey {
	t.Helper()
	_, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	return priv
}

func capabilitiesWith(jobTypes ...string) protocol.Capabilities {
	return protocol.Capabilities{
		ProtocolVersion: protocol.ProtocolVersion,
		AgentVersion:    "test",
		OS:              "linux",
		Arch:            "amd64",
		Slots:           1,
		Container:       protocol.ContainerNone,
		FfmpegVersion:   "7.1",
		FontsetVersion:  "1",
		JobTypes:        jobTypes,
		MaxOutputBytes:  1 << 30,
	}
}

func mediaOnlyResult() ProbeResult {
	return ProbeResult{
		Capabilities: capabilitiesWith(protocol.JobVideoCompile, protocol.JobVideoAnalyzeEx),
		Image:        imageSource("reelbolt-sandbox-runtime:local"),
	}
}

func imageSource(ref string) runtimeimage.Source {
	return runtimeimage.Source{Ref: ref, Origin: "test"}
}

func pairedConfig(t *testing.T) *config.Config {
	t.Helper()
	c := &config.Config{
		GatewayURL:   "wss://runner.reelbolt.ai/v1/connect",
		DeviceID:     "11111111-2222-3333-4444-555555555555",
		DeviceName:   "test-machine",
		Slots:        1,
		DrainSeconds: 1,
		ScratchRoot:  filepath.Join(t.TempDir(), "jobs"),
	}
	c.ApplyDefaults()
	c.ScratchRoot = filepath.Join(t.TempDir(), "jobs")
	// The media cache is off in the shared fixture: a test that does not ask
	// for it must not create one in the developer's own cache directory.
	// mediacache_test.go turns it on, in a temporary directory.
	c.MediaCache.SetMaxBytes(0)
	return c
}

// isolatedConfigDir points config.Path() at a temporary directory, so a test
// that writes the configuration cannot touch the developer's own runner.json.
func isolatedConfigDir(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	t.Setenv("XDG_CONFIG_HOME", dir) // Linux
	t.Setenv("XDG_CACHE_HOME", dir)  // Linux: the governor's control and live documents
	t.Setenv("AppData", dir)         // Windows
	t.Setenv("HOME", dir)            // last resort
	return dir
}

func newTestDaemon(t *testing.T, cfg *config.Config, probe ProbeResult, runner Runner) *Daemon {
	t.Helper()
	dir := isolatedConfigDir(t)
	// An in-memory keyring: a test must not file its key in the developer's
	// own OS keyring, where the next run's "no stored key" test would find it.
	keys := identity.NewWithRing(dir, &memRing{}, nil)
	d, err := New(Options{
		Config: cfg,
		Keys:   keys,
		Logger: log.New(os.Stderr, "test: ", 0),
		Prober: &fakeProber{result: probe},
		Agent:  runner,
	})
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	return d
}

// ---- construction ----

func TestNewRefusesAnUnpairedMachine(t *testing.T) {
	cfg := &config.Config{Slots: 1}
	cfg.ApplyDefaults()
	_, err := New(Options{Config: cfg, Keys: identity.Open(t.TempDir(), nil)})
	if !errors.Is(err, ErrNotPaired) {
		t.Fatalf("New = %v, want ErrNotPaired", err)
	}
}

func TestNewRefusesAMachineThatCanRunNothing(t *testing.T) {
	cfg := pairedConfig(t)
	probe := ProbeResult{Capabilities: protocol.Capabilities{
		ProtocolVersion: protocol.ProtocolVersion, Slots: 1, Container: protocol.ContainerNone,
		JobTypes: []string{},
	}}
	_, err := New(Options{
		Config: cfg,
		Keys:   identity.Open(t.TempDir(), nil),
		Prober: &fakeProber{result: probe},
	})
	if err == nil {
		t.Fatal("a machine with no ffmpeg and no runtime must not start")
	}
}

func TestNewAsksTheProberForTheConfiguredFacts(t *testing.T) {
	cfg := pairedConfig(t)
	cfg.Slots = 4
	cfg.ContainerCLI = "podman"
	prober := &fakeProber{result: mediaOnlyResult()}
	if _, err := New(Options{
		Config:         cfg,
		Keys:           identity.Open(t.TempDir(), nil),
		Prober:         prober,
		Agent:          newFakeRunner(nil),
		Version:        "9.9.9",
		FontsetVersion: "3",
	}); err != nil {
		t.Fatalf("New: %v", err)
	}
	if prober.got.Slots != 4 || prober.got.ContainerCLI != "podman" {
		t.Errorf("probe request = %+v", prober.got)
	}
	if prober.got.Version != "9.9.9" || prober.got.FontsetVersion != "3" {
		t.Errorf("version facts were not passed through: %+v", prober.got)
	}
	if prober.got.ScratchRoot != cfg.ScratchRoot {
		t.Errorf("ScratchRoot = %q, want %q", prober.got.ScratchRoot, cfg.ScratchRoot)
	}
}

func TestNewWithoutAStoredKeyRefusesToBuildAnAgent(t *testing.T) {
	cfg := pairedConfig(t)
	_, err := New(Options{
		Config: cfg,
		Keys:   identity.Open(t.TempDir(), nil),
		Prober: &fakeProber{result: mediaOnlyResult()},
	})
	if err == nil {
		t.Fatal("a paired machine with no device key must not start")
	}
	if !errors.Is(err, identity.ErrNoKey) {
		t.Fatalf("New = %v, want it to wrap identity.ErrNoKey", err)
	}
}

func TestNewBuildsTheMediaExecutorWhenAffmpegWasFound(t *testing.T) {
	cfg := pairedConfig(t)
	cfg.ScratchRoot = filepath.Join(t.TempDir(), "jobs")
	dir := t.TempDir()
	// An in-memory keyring: a test must not file its key in the developer's
	// own OS keyring, where the next run's "no stored key" test would find it.
	keys := identity.NewWithRing(dir, &memRing{}, nil)
	if err := keys.Save(cfg.DeviceID, testPrivateKey(t)); err != nil {
		t.Fatal(err)
	}
	d, err := New(Options{
		Config: cfg,
		Keys:   keys,
		Prober: &fakeProber{result: mediaOnlyResult()},
	})
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	if d.sessions.media == nil {
		t.Error("no media executor was built although ffmpeg was probed")
	}
	if d.sessions.remotion != nil {
		t.Error("a remotion executor was built without a verified runtime")
	}
	// The workspace root sits beside the scratch root, so a job's directory and
	// the containers' directories share one filesystem and one cleanup story.
	if filepath.Dir(d.WorkspaceRoot()) != filepath.Dir(d.ScratchRoot()) {
		t.Errorf("workspace root %q is not beside scratch root %q", d.WorkspaceRoot(), d.ScratchRoot())
	}
}

// ---- the stop sequence ----

func TestSIGTERMDrainsThenShutsDown(t *testing.T) {
	runner := newFakeRunner(nil)
	d := newTestDaemon(t, pairedConfig(t), mediaOnlyResult(), runner)

	sigCh := make(chan os.Signal, 2)
	done := make(chan error, 1)
	go func() { done <- d.serve(context.Background(), sigCh) }()

	select {
	case <-runner.started:
	case <-time.After(5 * time.Second):
		t.Fatal("the agent never started")
	}
	sigCh <- syscall.SIGTERM

	select {
	case err := <-done:
		if err != nil {
			t.Fatalf("serve returned %v after a clean stop", err)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("serve did not return after SIGTERM")
	}

	order := runner.order()
	want := []string{"drain:shutdown", "shutdown", "run-returned"}
	if len(order) != len(want) {
		t.Fatalf("events = %v, want %v", order, want)
	}
	for i := range want {
		if order[i] != want[i] {
			t.Fatalf("events = %v, want %v", order, want)
		}
	}
}

func TestContextCancellationAlsoDrains(t *testing.T) {
	runner := newFakeRunner(nil)
	d := newTestDaemon(t, pairedConfig(t), mediaOnlyResult(), runner)

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { done <- d.serve(ctx, make(chan os.Signal, 1)) }()
	<-runner.started
	cancel()
	select {
	case err := <-done:
		if err != nil {
			t.Fatalf("serve = %v", err)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("serve did not return after the context ended")
	}
	if order := runner.order(); len(order) < 2 || order[0] != "drain:shutdown" || order[1] != "shutdown" {
		t.Fatalf("events = %v, want a drain then a shutdown", order)
	}
}

// TestAnIdleStopDoesNotWaitTheWholeDrain: with nothing in flight there is
// nothing to wait for, so a stop takes the grace period, not DrainSeconds.
func TestAnIdleStopDoesNotWaitTheWholeDrain(t *testing.T) {
	cfg := pairedConfig(t)
	cfg.DrainSeconds = 30
	runner := newFakeRunner(nil)
	d := newTestDaemon(t, cfg, mediaOnlyResult(), runner)

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { done <- d.serve(ctx, make(chan os.Signal, 1)) }()
	<-runner.started
	start := time.Now()
	cancel()
	select {
	case err := <-done:
		if err != nil {
			t.Fatalf("serve = %v", err)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("an idle stop waited for the drain deadline")
	}
	if took := time.Since(start); took > 5*time.Second {
		t.Fatalf("an idle stop took %s", took)
	}
}

// TestAStopWaitsForTheJobInFlight: the early exit is for an idle runner only;
// a job in flight still gets the drain to finish.
func TestAStopWaitsForTheJobInFlight(t *testing.T) {
	cfg := pairedConfig(t)
	cfg.DrainSeconds = 30
	runner := newFakeRunner(nil)
	d := newTestDaemon(t, cfg, mediaOnlyResult(), runner)
	d.act.begin()

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { done <- d.serve(ctx, make(chan os.Signal, 1)) }()
	<-runner.started
	cancel()
	select {
	case <-done:
		t.Fatal("the stop did not wait for the job in flight")
	case <-time.After(time.Second):
	}
	d.act.end()
	select {
	case err := <-done:
		if err != nil {
			t.Fatalf("serve = %v", err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("the stop kept waiting after the job ended")
	}
}

func TestASecondSignalCutsTheDrainShort(t *testing.T) {
	cfg := pairedConfig(t)
	cfg.DrainSeconds = 30 // the drain would block for half a minute if it waited
	runner := newFakeRunner(nil)
	d := newTestDaemon(t, cfg, mediaOnlyResult(), runner)

	sigCh := make(chan os.Signal, 2)
	done := make(chan error, 1)
	go func() { done <- d.serve(context.Background(), sigCh) }()
	<-runner.started
	sigCh <- syscall.SIGTERM
	sigCh <- syscall.SIGTERM

	select {
	case err := <-done:
		if err != nil {
			t.Fatalf("serve = %v", err)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("the second signal did not end the drain")
	}
}

// ---- revocation ----

func TestARevokedDeviceIsUnpairedButKeepsItsKey(t *testing.T) {
	isolatedConfigDir(t)
	cfg := pairedConfig(t)
	dir := t.TempDir()
	// An in-memory keyring: a test must not file its key in the developer's
	// own OS keyring, where the next run's "no stored key" test would find it.
	keys := identity.NewWithRing(dir, &memRing{}, nil)
	_, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	if err := keys.Save(cfg.DeviceID, priv); err != nil {
		t.Fatal(err)
	}

	runner := newFakeRunner(fmt.Errorf("connection closed: %w", agent.ErrUnauthenticated))
	d, err := New(Options{
		Config: cfg,
		Keys:   keys,
		Logger: log.New(os.Stderr, "test: ", 0),
		Prober: &fakeProber{result: mediaOnlyResult()},
		Agent:  runner,
	})
	if err != nil {
		t.Fatal(err)
	}

	err = d.serve(context.Background(), make(chan os.Signal, 1))
	if !errors.Is(err, ErrUnpaired) {
		t.Fatalf("serve = %v, want ErrUnpaired", err)
	}
	if cfg.DeviceID != "" {
		t.Errorf("the device id survived a revocation: %q", cfg.DeviceID)
	}
	if cfg.OrganizationID != "" {
		t.Errorf("the organization survived a revocation: %q", cfg.OrganizationID)
	}
	// The key is deliberately kept: the identity is not the problem, the
	// approval is.
	if _, err := keys.Load("11111111-2222-3333-4444-555555555555"); err != nil {
		t.Errorf("the device key was thrown away: %v", err)
	}
	// And the unpaired state reached the file, so a restart does not try again.
	path, err := config.Path()
	if err != nil {
		t.Fatal(err)
	}
	reloaded, err := config.LoadFrom(path)
	if err != nil {
		t.Fatalf("the configuration could not be re-read: %v", err)
	}
	if reloaded.DeviceID != "" || reloaded.IsPaired() {
		t.Errorf("the unpaired state was not persisted: %+v", reloaded)
	}
}

func TestATerminalRefusalOtherThanRevocationIsReported(t *testing.T) {
	runner := newFakeRunner(fmt.Errorf("closed: %w", agent.ErrUnsupportedVersion))
	d := newTestDaemon(t, pairedConfig(t), mediaOnlyResult(), runner)
	err := d.serve(context.Background(), make(chan os.Signal, 1))
	if !errors.Is(err, agent.ErrUnsupportedVersion) {
		t.Fatalf("serve = %v, want the version error", err)
	}
	if errors.Is(err, ErrUnpaired) {
		t.Error("an unsupported version must not un-pair the machine")
	}
}

// ---- scratch ----

func TestSweepScratchRemovesOnlyOldDirectories(t *testing.T) {
	cfg := pairedConfig(t)
	root := t.TempDir()
	cfg.ScratchRoot = root
	old := filepath.Join(root, "old-job")
	fresh := filepath.Join(root, "fresh-job")
	for _, dir := range []string{old, fresh} {
		if err := os.MkdirAll(dir, 0o700); err != nil {
			t.Fatal(err)
		}
	}
	past := time.Now().Add(-48 * time.Hour)
	if err := os.Chtimes(old, past, past); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "not-a-directory"), []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}

	d := newTestDaemon(t, cfg, mediaOnlyResult(), newFakeRunner(nil))
	d.sweepScratch()

	if _, err := os.Stat(old); !errors.Is(err, os.ErrNotExist) {
		t.Error("a job directory from an earlier run survived the sweep")
	}
	if _, err := os.Stat(fresh); err != nil {
		t.Errorf("the sweep removed a directory that may belong to a live job: %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "not-a-directory")); err != nil {
		t.Errorf("the sweep removed a file: %v", err)
	}
}

func TestRunSweepsTheScratchRootBeforeConnecting(t *testing.T) {
	cfg := pairedConfig(t)
	root := t.TempDir()
	cfg.ScratchRoot = root
	old := filepath.Join(root, "stale")
	if err := os.MkdirAll(old, 0o700); err != nil {
		t.Fatal(err)
	}
	past := time.Now().Add(-48 * time.Hour)
	if err := os.Chtimes(old, past, past); err != nil {
		t.Fatal(err)
	}
	runner := newFakeRunner(nil)
	d := newTestDaemon(t, cfg, mediaOnlyResult(), runner)
	sigCh := make(chan os.Signal, 2)
	done := make(chan error, 1)
	go func() { done <- d.serve(context.Background(), sigCh) }()
	<-runner.started
	if _, err := os.Stat(old); !errors.Is(err, os.ErrNotExist) {
		t.Error("the daemon connected without sweeping a stale job directory")
	}
	sigCh <- syscall.SIGTERM
	<-done
}

// ---- dispatch ----

func TestSessionsRefuseAnUnknownJobType(t *testing.T) {
	s := sessions{}
	_, err := s.New(context.Background(), &agent.Job{JobType: "not.a.job"})
	var jobErr *agent.JobError
	if !errors.As(err, &jobErr) {
		t.Fatalf("err = %v, want a JobError", err)
	}
	if jobErr.Code != protocol.ErrCodeUnsupportedOp {
		t.Errorf("code = %q, want %q", jobErr.Code, protocol.ErrCodeUnsupportedOp)
	}
}

func TestSessionsRefuseRemotionWithoutARuntime(t *testing.T) {
	s := sessions{}
	_, err := s.New(context.Background(), &agent.Job{JobType: protocol.JobRemotionWorkspace})
	var jobErr *agent.JobError
	if !errors.As(err, &jobErr) {
		t.Fatalf("err = %v, want a JobError", err)
	}
	if jobErr.Code != protocol.ErrCodeContainerUnavailable {
		t.Errorf("code = %q", jobErr.Code)
	}
	if !jobErr.Retryable {
		t.Error("a missing runtime is a retryable runner failure, not a permanent one")
	}
}

func TestSessionsRefuseMediaWithoutFFmpeg(t *testing.T) {
	s := sessions{}
	_, err := s.New(context.Background(), &agent.Job{JobType: protocol.JobVideoCompile})
	var jobErr *agent.JobError
	if !errors.As(err, &jobErr) {
		t.Fatalf("err = %v, want a JobError", err)
	}
	if jobErr.Code != protocol.ErrCodeUnsupportedOp {
		t.Errorf("code = %q", jobErr.Code)
	}
}

// ---- capability plumbing ----

func TestRemotionIsAdvertisedOnlyWhenTheRuntimeIsVerified(t *testing.T) {
	cfg := pairedConfig(t)
	digest := "sha256:" + "c"
	caps := capabilitiesWith(protocol.JobRemotionWorkspace, protocol.JobVideoCompile)
	caps.Container = protocol.ContainerDocker
	caps.RuntimeImageDigest = &digest
	probe := ProbeResult{
		Capabilities: caps,
		Image:        imageSource("repo@sha256:cc"),
		Runtime: &runtime.Report{
			Candidate:     runtime.Candidate{Kind: runtime.KindDocker, Binary: "docker"},
			Usable:        true,
			RemotionReady: true,
			Version:       "27.0",
		},
	}
	dir := t.TempDir()
	// An in-memory keyring: a test must not file its key in the developer's
	// own OS keyring, where the next run's "no stored key" test would find it.
	keys := identity.NewWithRing(dir, &memRing{}, nil)
	if err := keys.Save(cfg.DeviceID, testPrivateKey(t)); err != nil {
		t.Fatal(err)
	}
	d, err := New(Options{
		Config: cfg,
		Keys:   keys,
		Prober: &fakeProber{result: probe},
	})
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	if !d.ProbeResult().RemotionReady() {
		t.Fatal("RemotionReady = false with a verified runtime and the job type advertised")
	}
	if d.sessions.remotion == nil {
		t.Error("no workspace executor was built although the runtime is Remotion-ready")
	}
}

func TestRemotionReadyIsFalseWhenTheJobTypeIsMissing(t *testing.T) {
	probe := ProbeResult{
		Capabilities: capabilitiesWith(protocol.JobVideoCompile),
		Runtime: &runtime.Report{
			Candidate:     runtime.Candidate{Kind: runtime.KindDocker, Binary: "docker"},
			Usable:        true,
			RemotionReady: true,
		},
	}
	if probe.RemotionReady() {
		t.Fatal("RemotionReady must require the job type to be advertised, not the runtime alone")
	}
}

func TestReasonNotReadyExplainsTheFailure(t *testing.T) {
	if got := reasonNotReady(&runtime.Report{Failure: runtime.FailImageMissing, Detail: "no such image"}); got != "no_image_configured" && got != "image_missing: no such image" {
		// The exact constant is asserted to keep the mapping honest.
		if got != runtime.FailImageMissing+": no such image" {
			t.Errorf("reasonNotReady = %q", got)
		}
	}
	if got := reasonNotReady(nil); got != "no runtime" {
		t.Errorf("reasonNotReady(nil) = %q", got)
	}
}
