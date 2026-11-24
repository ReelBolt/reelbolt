package ui

import (
	"archive/zip"
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/daemon"
	"github.com/vecchiotom/reelbolt/runner/internal/telemetry"
)

// collector is a LOCAL FAKE crash-ingest endpoint.
//
// This repository has no collector and no vendor credential, so the only honest
// way to test "a report was sent" - and, far more importantly, "nothing was
// sent" - is against a server this test owns. It also means the test exercises
// the real HTTP transport rather than a stub of it: the shell builds its own
// reporter, reads the endpoint the way a development build reads it, and posts
// a real request.
type collector struct {
	srv *httptest.Server

	mu     sync.Mutex
	bodies []string
}

func newCollector(t *testing.T) *collector {
	t.Helper()
	c := &collector{}
	c.srv = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(io.LimitReader(r.Body, 1<<20))
		c.mu.Lock()
		c.bodies = append(c.bodies, string(body))
		c.mu.Unlock()
		w.WriteHeader(http.StatusAccepted)
	}))
	t.Cleanup(c.srv.Close)
	// The loopback address is what telemetry.Endpoint() accepts from a
	// development build, which is exactly the seam the fake needs.
	t.Setenv(telemetry.DevEndpointEnv, c.srv.URL)
	return c
}

func (c *collector) count() int {
	c.mu.Lock()
	defer c.mu.Unlock()
	return len(c.bodies)
}

func (c *collector) all() string {
	c.mu.Lock()
	defer c.mu.Unlock()
	return strings.Join(c.bodies, "\n")
}

// waitForReports waits for the reporter's background sender.
func (c *collector) waitForReports(t *testing.T, want int) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		if c.count() >= want {
			return
		}
		time.Sleep(5 * time.Millisecond)
	}
}

func telemetryShell(t *testing.T) (*Shell, *collector, func()) {
	t.Helper()
	c := newCollector(t)
	s, cleanup := newTestShell(t, func(o *Options) {
		o.DiagnosticsDir = t.TempDir()
		// The log lives in the log directory, not in the diagnostics directory,
		// and the export falls back to whatever is there.
		o.LogDir = t.TempDir()
	})
	if s.View().Settings.Telemetry.Endpoint == "" {
		cleanup()
		t.Fatal("the shell did not pick up the fake collector")
	}
	return s, c, cleanup
}

// The property E11 exists for: with the switch off, a reachable collector
// receives nothing at all - not a redacted report, not an anonymous ping.
func TestTheShellSendsNothingUntilTheUserTurnsItOn(t *testing.T) {
	s, c, cleanup := telemetryShell(t)
	defer cleanup()

	if v := s.View().Settings.Telemetry; v.Enabled {
		t.Fatalf("telemetry is on in a fresh shell: %+v", v)
	}

	// A job the runner could not start is exactly what this feature reports.
	refusal := daemon.JobFailure{
		JobID: "job-1", JobType: "video.compile", OrgID: "org-secret", Class: "runner", Code: "stage_failed",
	}
	s.observeJobFailure(refusal)
	if err := s.TelemetryReporter().Flush(context.Background()); err != nil {
		t.Fatalf("Flush: %v", err)
	}
	if c.count() != 0 {
		t.Fatalf("the collector received %d reports with the switch off:\n%s", c.count(), c.all())
	}

	// On, and the same refusal reports - once.
	if err := s.SetTelemetry(true); err != nil {
		t.Fatalf("SetTelemetry: %v", err)
	}
	s.observeJobFailure(refusal)
	c.waitForReports(t, 1)
	if err := s.TelemetryReporter().Flush(context.Background()); err != nil {
		t.Fatalf("Flush: %v", err)
	}
	if c.count() != 1 {
		t.Fatalf("the collector received %d reports, want 1", c.count())
	}
	// The organisation id is on the event the daemon produced and must NOT be
	// on the wire: no workspace may be identifiable from a report.
	if strings.Contains(c.all(), "org-secret") {
		t.Errorf("the workspace id reached the collector:\n%s", c.all())
	}
	if !strings.Contains(c.all(), "\"failureCode\":\"stage_failed\"") {
		t.Errorf("the report lost its failure code:\n%s", c.all())
	}

	// Off again, and it stops immediately.
	if err := s.SetTelemetry(false); err != nil {
		t.Fatalf("SetTelemetry(false): %v", err)
	}
	s.observeJobFailure(refusal)
	if err := s.TelemetryReporter().Flush(context.Background()); err != nil {
		t.Fatalf("Flush: %v", err)
	}
	if c.count() != 1 {
		t.Fatalf("the collector received %d reports after turning it off, want 1", c.count())
	}
}

// What the switch writes is a consent record, not a flag. This is the file a
// support engineer reads when a user says "I never agreed to that".
func TestTheSwitchWritesAConsentRecord(t *testing.T) {
	s, _, cleanup := telemetryShell(t)
	defer cleanup()

	if err := s.SetTelemetry(true); err != nil {
		t.Fatalf("SetTelemetry: %v", err)
	}
	path := s.ConfigPath()
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read %s: %v", path, err)
	}
	var doc map[string]any
	if err := json.Unmarshal(raw, &doc); err != nil {
		t.Fatalf("parse %s: %v", path, err)
	}
	tel, ok := doc["telemetry"].(map[string]any)
	if !ok {
		t.Fatalf("runner.json carries no telemetry block:\n%s", raw)
	}
	if on, _ := tel["enabled"].(bool); !on {
		t.Errorf("enabled is not written as true:\n%s", raw)
	}
	if asked, _ := tel["askedAt"].(string); asked == "" {
		t.Errorf("askedAt is not recorded, so the wizard would ask again:\n%s", raw)
	}
	if cv, _ := tel["consentVersion"].(string); cv != telemetry.ConsentVersion {
		t.Errorf("consentVersion = %q, want %q", cv, telemetry.ConsentVersion)
	}
	if !s.View().Settings.Telemetry.Enabled {
		t.Error("the view did not follow the switch")
	}

	// Asking again changes nothing: that is what "asked once" means.
	if err := s.MarkTelemetryAsked(); err != nil {
		t.Fatalf("MarkTelemetryAsked: %v", err)
	}
	after, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("re-read: %v", err)
	}
	if string(after) != string(raw) {
		t.Errorf("asking again rewrote the file:\nbefore:\n%s\nafter:\n%s", raw, after)
	}
}

// The preview is what the Settings screen shows when a user asks what would be
// sent. It is built from THIS machine, and it explains itself.
func TestTheShellPreviewDescribesThisMachine(t *testing.T) {
	s, _, cleanup := telemetryShell(t)
	defer cleanup()
	if err := s.SetTelemetry(true); err != nil {
		t.Fatalf("SetTelemetry: %v", err)
	}

	p := s.TelemetryPreview()
	if p.Schema != telemetry.Schema {
		t.Errorf("schema = %q", p.Schema)
	}
	if !p.Enabled {
		t.Error("the preview says telemetry is off after it was turned on")
	}
	if !p.Available {
		t.Error("the preview says no collector is configured, but this shell has one")
	}
	if p.Crash.OS == "" || p.Crash.Arch == "" {
		t.Errorf("the preview does not carry this machine's facts: %+v", p.Crash)
	}
	if len(p.NeverSent) == 0 || len(p.Fields) == 0 || len(p.Notes) == 0 {
		t.Error("the preview does not explain what is and is not sent")
	}
	raw, err := json.Marshal(p)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	if strings.Contains(string(raw), "/home/") {
		t.Errorf("the preview shows a path:\n%s", raw)
	}
	if p.Crash.PanicType != telemetry.PlaceholderPanicType ||
		p.JobFailure.FailureCode != telemetry.PlaceholderJobCode {
		t.Errorf("the preview does not mark the fields that vary per report: %+v / %+v",
			p.Crash.PanicType, p.JobFailure.FailureCode)
	}
}

// The export writes a zip the user reads before sending it, into the directory
// the shell was given, with its free text redacted.
func TestExportDiagnosticsWritesAScrubbedBundle(t *testing.T) {
	s, _, cleanup := telemetryShell(t)
	defer cleanup()

	logPath := filepath.Join(s.opts.LogDir, "desktop.log")
	if err := os.WriteFile(logPath, []byte("staging for alice@example.com from /home/bob/renders/x.mp4\n"), 0o600); err != nil {
		t.Fatalf("write log: %v", err)
	}

	res, err := s.ExportDiagnostics()
	if err != nil {
		t.Fatalf("ExportDiagnostics: %v", err)
	}
	if res.Entries == 0 {
		t.Fatal("the bundle is empty")
	}
	r, err := zip.OpenReader(res.Path)
	if err != nil {
		t.Fatalf("open %s: %v", res.Path, err)
	}
	defer func() { _ = r.Close() }()

	var names []string
	var whole strings.Builder
	for _, f := range r.File {
		names = append(names, f.Name)
		rc, err := f.Open()
		if err != nil {
			t.Fatalf("open entry: %v", err)
		}
		body, _ := io.ReadAll(rc)
		_ = rc.Close()
		whole.Write(body)
		whole.WriteString("\n")
	}
	for _, want := range []string{"README.txt", "summary.json", "logs/desktop.log"} {
		if !hasString(names, want) {
			t.Errorf("%s is missing from the bundle: %v", want, names)
		}
	}
	if strings.Contains(whole.String(), "/home/bob") {
		t.Errorf("the bundle kept another user's home directory:\n%s", whole.String())
	}
	if strings.Contains(whole.String(), "alice@example.com") {
		t.Errorf("the bundle kept an e-mail address:\n%s", whole.String())
	}
	if !strings.Contains(whole.String(), "<email>") {
		t.Errorf("the log was not redacted:\n%s", whole.String())
	}
	if !strings.Contains(whole.String(), "generatedBy") || !strings.Contains(whole.String(), "reelbolt-runner-desktop") {
		t.Errorf("the summary is not the runner's own:\n%s", whole.String())
	}
	if !strings.Contains(whole.String(), "has not been") {
		t.Errorf("the readme does not say the bundle is local:\n%s", whole.String())
	}
}

// Every action the shell advertises is handled, including E11's - and the
// diagnostics pair does something useful with no network at all.
func TestTelemetryActionsDoWhatTheySay(t *testing.T) {
	var opened []string
	s, c, cleanup := telemetryShell(t)
	defer cleanup()
	a := NewApp(s, AppOptions{
		OpenURL:   func(u string) error { opened = append(opened, u); return nil },
		Autostart: &fakeAutostart{},
	})

	res, err := a.Do(ActionTelemetryOn, "")
	if err != nil {
		t.Fatalf("%s: %v", ActionTelemetryOn, err)
	}
	if !res.View.Settings.Telemetry.Enabled {
		t.Error("turning it on left the view saying it is off")
	}
	if res.Message == "" {
		t.Error("turning it on said nothing")
	}
	// The sentence must not claim reports are being sent by a build whose
	// collector is not configured, which is every real build today.
	if !res.View.Settings.Telemetry.Available && strings.Contains(res.Message, "will send a short report") {
		t.Errorf("the message overstates what happens: %q", res.Message)
	}

	if _, err := a.Do(ActionTelemetryOff, ""); err != nil {
		t.Fatalf("%s: %v", ActionTelemetryOff, err)
	}
	if s.View().Settings.Telemetry.Enabled {
		t.Error("turning it off left the view saying it is on")
	}

	res, err = a.Do(ActionExportDiagnostics, "")
	if err != nil {
		t.Fatalf("%s: %v", ActionExportDiagnostics, err)
	}
	if !strings.Contains(res.Message, "Nothing was uploaded") {
		t.Errorf("the export message does not say the bundle stayed local: %q", res.Message)
	}

	if _, err := a.Do(ActionOpenDiagnosticsDir, ""); err != nil {
		t.Fatalf("%s: %v", ActionOpenDiagnosticsDir, err)
	}
	if len(opened) == 0 {
		t.Error("open-diagnostics opened nothing")
	}

	if _, err := a.Do(ActionTelemetryAsked, ""); err != nil {
		t.Fatalf("%s: %v", ActionTelemetryAsked, err)
	}
	if !s.View().Settings.Telemetry.Asked {
		t.Error("telemetry-asked did not record that the wizard asked")
	}
	if c.count() != 0 {
		t.Errorf("an action sent %d reports", c.count())
	}
}

// The shell hands the reporter facts from the probe and nothing else. This is
// the join between "what the runner knows" and "what may leave", so it is worth
// its own test.
func TestTheShellReportsOnlyTheRuntimesKindAndMode(t *testing.T) {
	s, cleanup := newTestShell(t, func(o *Options) {
		o.Prober = fakeProber{res: capableProbe()}
	})
	defer cleanup()

	env := s.telemetryEnv()
	if env.Version != "1.2.3" {
		t.Errorf("version = %q", env.Version)
	}
	if env.OS == "" || env.Arch == "" || env.GoVersion == "" {
		t.Errorf("the environment is incomplete: %+v", env)
	}
	// The shared fake probe has no runtime at all, which must read as "this
	// machine has none" rather than as an empty string with a meaning.
	if env.RuntimeKind != "" || env.RuntimeMode != "" {
		t.Errorf("a machine with no probed runtime reported %+v", env)
	}
}

// A machine with no container runtime is exactly the machine whose refusals are
// worth hearing about, so its report must still be well formed.
func TestAReportFromAMachineWithNoRuntimeIsStillValid(t *testing.T) {
	s, c, cleanup := telemetryShell(t)
	defer cleanup()
	if err := s.SetTelemetry(true); err != nil {
		t.Fatalf("SetTelemetry: %v", err)
	}
	s.observeJobFailure(daemon.JobFailure{JobType: "video.compile", Class: "runner", Code: "container_unavailable"})
	c.waitForReports(t, 1)
	if err := s.TelemetryReporter().Flush(context.Background()); err != nil {
		t.Fatalf("Flush: %v", err)
	}
	if c.count() != 1 {
		t.Fatalf("the collector received %d reports, want 1", c.count())
	}
	var ev telemetry.Event
	if err := json.Unmarshal([]byte(strings.TrimSpace(c.all())), &ev); err != nil {
		t.Fatalf("the payload is not an event: %v\n%s", err, c.all())
	}
	if ev.Kind != telemetry.KindJobFailure || ev.FailureCode != "container_unavailable" {
		t.Errorf("event = %+v", ev)
	}
	if ev.ID == "" || ev.At == "" {
		t.Errorf("the event has no envelope: %+v", ev)
	}
}
