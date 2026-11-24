package telemetry

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
	"sync"
	"testing"
	"time"
)

// fakeTransport records what it was asked to send. It is the whole reason the
// Transport seam exists: the property that matters most about this package is
// "nothing was sent", and a test cannot assert on a network it does not own.
type fakeTransport struct {
	mu        sync.Mutex
	endpoints []string
	bodies    []string
	err       error
}

func (f *fakeTransport) Send(_ context.Context, endpoint string, body []byte) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.endpoints = append(f.endpoints, endpoint)
	f.bodies = append(f.bodies, string(body))
	return f.err
}

func (f *fakeTransport) count() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return len(f.bodies)
}

func (f *fakeTransport) all() string {
	f.mu.Lock()
	defer f.mu.Unlock()
	return strings.Join(f.bodies, "\n")
}

// testEnv is a plausible machine, with values that are all safe to send.
func testEnv() Env {
	return Env{
		Version:     "1.4.2",
		OS:          "linux",
		Arch:        "amd64",
		GoVersion:   "go1.26.0",
		RuntimeKind: "docker",
		RuntimeMode: "rootless",
	}
}

func newTestReporter(t *testing.T, on bool, endpoint string, tr Transport) *Reporter {
	t.Helper()
	return New(Options{
		Enabled:   func() bool { return on },
		Endpoint:  endpoint,
		Env:       testEnv,
		Transport: tr,
		Now:       func() time.Time { return time.Date(2026, 10, 8, 9, 14, 12, 0, time.UTC) },
		Rand:      strings.NewReader(strings.Repeat("a", 1024)),
	})
}

// The acceptance criterion: with the switch off, a fake transport sees ZERO
// calls. Not "few", not "redacted" - zero, from every entry point this package
// offers, including the crash path.
func TestNothingIsSentWhileTheSwitchIsOff(t *testing.T) {
	tr := &fakeTransport{}
	rep := newTestReporter(t, false, "https://collector.example/v1/events", tr)

	rep.Report(NewCrash(testEnv(), "id", time.Now(), "run", errors.New("boom"), []byte("x")))
	_ = rep.ReportSync(context.Background(), NewCrash(testEnv(), "id", time.Now(), "run", "boom", nil))
	rep.ReportJobFailure("video.compile", "runner", "stage_failed")
	ReportPanic(rep, "run", errors.New("boom"), []byte("goroutine 1 [running]:\n"))
	if err := rep.Flush(context.Background()); err != nil {
		t.Fatalf("Flush: %v", err)
	}

	if got := tr.count(); got != 0 {
		t.Fatalf("the transport was called %d times with the switch off:\n%s", got, tr.all())
	}
	if s := rep.Stats(); s.Sent != 0 || s.Unsent != 0 || s.Failed != 0 {
		t.Errorf("stats = %+v, want all zeros", s)
	}
}

// Turning it on changes exactly one thing: reports now reach the transport.
func TestReportsReachTheTransportWhenTheSwitchIsOn(t *testing.T) {
	tr := &fakeTransport{}
	rep := newTestReporter(t, true, "https://collector.example/v1/events", tr)
	rep.ReportJobFailure("video.compile", "runner", "stage_failed")
	if err := rep.Flush(context.Background()); err != nil {
		t.Fatalf("Flush: %v", err)
	}
	if tr.count() != 1 {
		t.Fatalf("the transport was called %d times, want 1", tr.count())
	}
	var ev Event
	if err := json.Unmarshal([]byte(tr.bodies[0]), &ev); err != nil {
		t.Fatalf("the payload is not JSON: %v", err)
	}
	if ev.Kind != KindJobFailure || ev.JobType != "video.compile" ||
		ev.FailureClass != "runner" || ev.FailureCode != "stage_failed" {
		t.Errorf("event = %+v", ev)
	}
	if ev.Schema != Schema || ev.ID == "" || ev.At == "" {
		t.Errorf("event is missing its envelope: %+v", ev)
	}
	if rep.Stats().Sent != 1 {
		t.Errorf("stats = %+v, want one sent", rep.Stats())
	}
}

// The switch can be on while this build has no collector at all, which is the
// state of EVERY build made from this source tree. The runner must record that
// honestly and still send nothing.
func TestNoCollectorMeansNothingLeavesEvenWhenTheSwitchIsOn(t *testing.T) {
	tr := &fakeTransport{}
	var logged []string
	rep := New(Options{
		Enabled:   func() bool { return true },
		Endpoint:  "",
		Env:       testEnv,
		Transport: tr,
		Log:       func(format string, args ...any) { logged = append(logged, format) },
	})
	rep.ReportJobFailure("video.compile", "runner", "stage_failed")
	if err := rep.Flush(context.Background()); err != nil {
		t.Fatalf("Flush: %v", err)
	}
	if tr.count() != 0 {
		t.Fatalf("the transport was called %d times with no endpoint", tr.count())
	}
	if got := rep.Stats().Unsent; got != 1 {
		t.Errorf("Unsent = %d, want 1", got)
	}
	if len(logged) != 1 {
		t.Errorf("the reason was logged %d times, want exactly once: %v", len(logged), logged)
	}
}

// The report this package exists for: a crash whose VALUE, MESSAGE and STACK
// are full of the user's own data. None of it may reach the payload.
func TestACrashPayloadCarriesNothingThatIdentifiesTheUser(t *testing.T) {
	tr := &fakeTransport{}
	rep := newTestReporter(t, true, "https://collector.example/v1/events", tr)

	// Every one of these is something a real panic on a real machine carries.
	const deviceID = "6f1a2c34-9b7e-4c11-8a2d-6c1f0b9d4e77"
	const workspaceID = "0d2f8a11-3c4b-4a55-9e01-2b7c8d9f0a12"
	panicValue := errors.New("could not stage /home/alice/ReelBolt/secret-project/final-cut.mp4" +
		"?X-Amz-Signature=9f8c1d2e3b4a5c6d7e8f9012345678abcdef0123456789" +
		" for alice@example.com on studio-laptop (" + deviceID + ", " + workspaceID + ")")
	stack := []byte("goroutine 7 [running]:\n" +
		"github.com/vecchiotom/reelbolt/runner/internal/ui.(*Shell).startRunner.func1()\n" +
		"\t/home/alice/ReelBolt/runner/internal/ui/shell.go:271 +0x1a2\n" +
		"runtime.gopanic(...)\n" +
		"\t/usr/local/go/src/runtime/panic.go:720 +0x2c\n" +
		"created by main.main in goroutine 1\n" +
		"\t/home/alice/ReelBolt/runner/cmd/reelbolt-runner-desktop/main.go:230 +0x40\n")

	ReportPanic(rep, "run", panicValue, stack)

	if tr.count() != 1 {
		t.Fatalf("the transport was called %d times, want 1", tr.count())
	}
	payload := tr.bodies[0]
	for _, secret := range []string{
		"/home/alice", "alice", "studio-laptop", "secret-project", "final-cut.mp4", ".mp4",
		"X-Amz-Signature", "9f8c1d2e3b4a5c6d", "@", "example.com", deviceID, workspaceID,
		"ReelBolt/runner/internal",
	} {
		if strings.Contains(payload, secret) {
			t.Errorf("%q reached the payload:\n%s", secret, payload)
		}
	}
	// The useful half survives: the shape of the failure.
	for _, want := range []string{
		"\"kind\":\"crash\"", "\"stage\":\"run\"", "\"panicType\":\"error\"",
		"\"file\":\"shell.go\"", "\"file\":\"panic.go\"", "\"line\":271",
		"\"os\":\"linux\"", "\"runtime\":\"docker\"", "\"runtimeMode\":\"rootless\"",
	} {
		if !strings.Contains(payload, want) {
			t.Errorf("%q is missing, which makes the report useless:\n%s", want, payload)
		}
	}
}

// A panic whose value is a plain string is the commonest case of all, and the
// one where the message is most likely to BE the sensitive data.
func TestAStringPanicReportsItsTypeAndNotItsText(t *testing.T) {
	tr := &fakeTransport{}
	rep := newTestReporter(t, true, "https://collector.example/v1/events", tr)
	ReportPanic(rep, "pairing", "device "+strings.Repeat("9", 64)+" http://alice@example.com", nil)
	if tr.count() != 1 {
		t.Fatalf("the transport was called %d times, want 1", tr.count())
	}
	payload := tr.bodies[0]
	if strings.Contains(payload, "example.com") || strings.Contains(payload, strings.Repeat("9", 64)) {
		t.Errorf("the panic's text reached the payload:\n%s", payload)
	}
	if !strings.Contains(payload, "\"panicType\":\"string\"") {
		t.Errorf("the panic's type is missing:\n%s", payload)
	}
}

// The frame parser keeps symbols and bare file names, and refuses everything it
// does not recognise rather than forwarding it.
func TestFramesKeepSymbolsAndDropPaths(t *testing.T) {
	stack := []byte("goroutine 1 [running]:\n" +
		"main.boom()\n" +
		"\t/home/alice/x/main.go:12 +0x20\n" +
		"a symbol with a ~ in it()\n" +
		"\t/home/alice/x/other.go:9 +0x10\n" +
		"runtime.gopanic(...)\n" +
		"\t/usr/local/go/src/runtime/panic.go:720 +0x2c\n")
	frames, dropped := ParseFrames(stack, 16)
	if len(frames) != 2 {
		t.Fatalf("frames = %+v, want 2 (the third symbol has an illegal character)", frames)
	}
	if dropped != 1 {
		t.Errorf("dropped = %d, want 1", dropped)
	}
	for _, f := range frames {
		if strings.Contains(f.File, "/") {
			t.Errorf("frame carries a path: %+v", f)
		}
	}
	if frames[0].Func != "main.boom()" || frames[0].File != "main.go" || frames[0].Line != 12 {
		t.Errorf("frames[0] = %+v", frames[0])
	}
}

// A stack longer than the cap is truncated and the truncation is reported,
// rather than the document growing with the recursion.
func TestFramesAreCapped(t *testing.T) {
	var b strings.Builder
	b.WriteString("goroutine 1 [running]:\n")
	for i := 0; i < 40; i++ {
		b.WriteString("main.f()\n\t/home/alice/x/main.go:12 +0x20\n")
	}
	frames, dropped := ParseFrames([]byte(b.String()), 16)
	if len(frames) != 16 || dropped != 24 {
		t.Errorf("frames = %d, dropped = %d; want 16 and 24", len(frames), dropped)
	}
}

// Recover adds reporting and NOTHING ELSE: the panic still unwinds.
func TestRecoverReportsAndStillPanics(t *testing.T) {
	tr := &fakeTransport{}
	rep := newTestReporter(t, true, "https://collector.example/v1/events", tr)

	var recovered any
	func() {
		defer func() { recovered = recover() }()
		func() {
			defer Recover(rep, "run")
			panic(errors.New("boom"))
		}()
	}()

	if recovered == nil {
		t.Fatal("Recover swallowed the panic, which would leave a half-dead runner")
	}
	if tr.count() != 1 {
		t.Errorf("the transport was called %d times, want 1", tr.count())
	}
}

// A full queue drops events instead of blocking the caller. The reporter is
// called from a job's own goroutine, so blocking there would stall real work in
// order to talk about a diagnostic.
func TestAFullQueueDropsRatherThanBlocks(t *testing.T) {
	blocked := make(chan struct{})
	tr := &slowTransport{release: blocked}
	rep := New(Options{
		Enabled:   func() bool { return true },
		Endpoint:  "https://collector.example/v1/events",
		Env:       testEnv,
		Transport: tr,
		Queue:     1,
	})
	defer close(blocked)

	done := make(chan struct{})
	go func() {
		defer close(done)
		for i := 0; i < 50; i++ {
			rep.ReportJobFailure("video.compile", "runner", "stage_failed")
		}
	}()
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		t.Fatal("Report blocked on a full queue")
	}
	if rep.Stats().Dropped == 0 {
		t.Errorf("nothing was counted as dropped: %+v", rep.Stats())
	}
}

// slowTransport holds the sender until it is released.
type slowTransport struct{ release <-chan struct{} }

func (s *slowTransport) Send(ctx context.Context, _ string, _ []byte) error {
	select {
	case <-s.release:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

// The preview is not a mock-up: it is built from this machine's values, and it
// is itself free of anything the package promises not to send.
func TestThePreviewShowsRealValuesAndNoUserData(t *testing.T) {
	rep := New(Options{Enabled: func() bool { return false }, Env: testEnv})
	p := rep.Preview()
	if p.Schema != Schema || p.ConsentVersion != ConsentVersion {
		t.Errorf("preview envelope = %+v", p)
	}
	if p.Crash.OS != "linux" || p.Crash.Runtime != "docker" || p.Crash.RuntimeMode != "rootless" {
		t.Errorf("the preview does not carry this machine's facts: %+v", p.Crash)
	}
	if p.Crash.PanicType != PlaceholderPanicType || p.JobFailure.FailureCode != PlaceholderJobCode {
		t.Errorf("the varying fields are not marked as placeholders: %+v", p)
	}
	if len(p.Fields) == 0 || len(p.NeverSent) == 0 || len(p.Notes) == 0 {
		t.Error("the preview does not explain itself")
	}
	raw, err := json.Marshal(p)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	// The frame fixtures in the preview use a bare file name and a line number,
	// so the document contains no path even as an example.
	if strings.Contains(string(raw), "/home/") {
		t.Errorf("the preview shows a path:\n%s", raw)
	}
	if p.Enabled {
		t.Error("the preview claims telemetry is on when it is off")
	}
}

// The disclosure the user reads and the list of what is never sent are part of
// the API, not of the screen: a screen that rendered its own promise could
// drift from the payload.
func TestTheDisclosureNamesTheThingsThatAreNeverSent(t *testing.T) {
	joined := strings.Join(NeverSent, " ")
	for _, want := range []string{"names of your files", "where they are", "ReelBolt id", "project", "what a job was asked", "download links", "error message"} {
		if !strings.Contains(strings.ToLower(joined), strings.ToLower(want)) {
			t.Errorf("the disclosure does not mention %q: %s", want, joined)
		}
	}
	if !strings.Contains(Disclosure, "turn this on") {
		t.Errorf("the disclosure does not state the switch: %s", Disclosure)
	}
}

// The endpoint the runner may post to. This is the one place a build could be
// pointed somewhere it should not be, so it is tested directly.
func TestEndpointIsLoopbackOnlyInADevelopmentBuild(t *testing.T) {
	t.Setenv(DevEndpointEnv, "")
	if got := Endpoint(); got != "" {
		t.Errorf("Endpoint with nothing configured = %q, want empty", got)
	}
	t.Setenv(DevEndpointEnv, "http://127.0.0.1:8088/ingest")
	if got := Endpoint(); got != "http://127.0.0.1:8088/ingest" {
		t.Errorf("a loopback collector was refused: %q", got)
	}
	for _, host := range []string{
		"https://collector.example.com/ingest",
		"http://10.0.0.5:8088/ingest",
		"ftp://127.0.0.1/ingest",
		"not a url",
	} {
		t.Setenv(DevEndpointEnv, host)
		if got := Endpoint(); got != "" {
			t.Errorf("Endpoint accepted %q as %q", host, got)
		}
	}
}

// A nil reporter is what a headless command and a test get, and it must behave
// like a switch that is off rather than panicking.
func TestANilReporterSendsNothingAndPanicsNowhere(t *testing.T) {
	var rep *Reporter
	rep.Report(Event{})
	rep.ReportJobFailure("video.compile", "runner", "x")
	if err := rep.Flush(context.Background()); err != nil {
		t.Errorf("Flush: %v", err)
	}
	rep.Close()
	if rep.Enabled() || rep.Available() || rep.Endpoint() != "" {
		t.Error("a nil reporter claims to be usable")
	}
	if s := rep.Stats(); s.Schema != Schema || s.Sent != 0 {
		t.Errorf("stats = %+v", s)
	}
	ReportPanic(rep, "run", errors.New("boom"), nil)
}
