package ui

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent/srccache"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/daemon"
	"github.com/vecchiotom/reelbolt/runner/internal/deviceapi"
)

// ---- jobs: names, outcomes and the dashboard link ----

func TestTheLedgerKeepsNamesOutcomesAndTheRunLink(t *testing.T) {
	base := time.Date(2026, 10, 8, 12, 0, 0, 0, time.UTC)
	l := NewLedger(func() time.Time { return base })
	l.ObserveJob(daemon.JobEvent{
		Kind: daemon.JobStarted, JobID: "j1", JobType: protocol.JobVideoAnalyzeEx, At: base,
		WorkspaceName: "Acme Studio", ProjectName: "Launch film", WorkflowName: "Derush",
		ProjectID: "p 1", WorkflowID: "w1", ExecutionID: "e1",
	})
	// A progress event with no names must not blank the ones the row has.
	l.ObserveJob(daemon.JobEvent{Kind: daemon.JobProgress, JobID: "j1", At: base.Add(time.Second)})
	running := jobView(l.Jobs()[0], base.Add(5*time.Second), "https://app.reelbolt.ai/")
	if running.Outcome != "" || running.EndedAt != "" {
		t.Errorf("a running job has an outcome: %+v", running)
	}
	l.ObserveJob(daemon.JobEvent{
		Kind: daemon.JobFinished, JobID: "j1", At: base.Add(time.Minute),
		Outcome: daemon.OutcomeTimedOut, OutcomeDetail: "This part ran longer than ReelBolt allows.",
	})
	v := jobView(l.Jobs()[0], base.Add(2*time.Minute), "https://app.reelbolt.ai/")
	if v.Title != "Analyzing footage" {
		t.Errorf("title = %q", v.Title)
	}
	if v.WorkspaceName != "Acme Studio" || v.ProjectName != "Launch film" || v.WorkflowName != "Derush" {
		t.Errorf("names = %+v", v)
	}
	if v.Outcome != "timed-out" || v.OutcomeDetail == "" || v.EndedAt != "2026-10-08T12:01:00Z" || v.Running {
		t.Errorf("ending = %+v", v)
	}
	if want := "https://app.reelbolt.ai/app/projects/p%201/workflows/w1/executions/e1"; v.DashboardURL != want {
		t.Errorf("dashboard url = %q, want %q", v.DashboardURL, want)
	}
	// Without all three ids there is no link.
	if u := RunURL("https://app.reelbolt.ai", Job{ProjectID: "p", WorkflowID: "w"}); u != "" {
		t.Errorf("a link from two ids: %q", u)
	}
	raw, _ := json.Marshal(v)
	for _, k := range []string{`"projectName"`, `"workflowName"`, `"workspaceName"`, `"endedAt"`, `"outcome"`, `"outcomeDetail"`, `"dashboardUrl"`} {
		if !strings.Contains(string(raw), k) {
			t.Errorf("the job row has no %s: %s", k, raw)
		}
	}
}

func TestOpenJobOpensTheRunOrTheDashboard(t *testing.T) {
	var opened []string
	s, cleanup := newTestShell(t, func(o *Options) { o.DashboardURL = "https://dash.example" })
	defer cleanup()
	a := NewApp(s, AppOptions{
		OpenURL:      func(u string) error { opened = append(opened, u); return nil },
		Autostart:    &fakeAutostart{},
		DashboardURL: "https://app.reelbolt.ai",
	})
	s.Ledger().ObserveJob(daemon.JobEvent{Kind: daemon.JobStarted, JobID: "j1", JobType: protocol.JobVideoCompile,
		ProjectID: "p1", WorkflowID: "w1", ExecutionID: "e1"})
	s.Ledger().ObserveJob(daemon.JobEvent{Kind: daemon.JobStarted, JobID: "j2", JobType: protocol.JobVideoCompile})
	for _, id := range []string{"j1", "j2", "nobody"} {
		if _, err := a.Do(ActionOpenJob, id); err != nil {
			t.Fatalf("open-job %s: %v", id, err)
		}
	}
	want := []string{"https://dash.example/app/projects/p1/workflows/w1/executions/e1", "https://app.reelbolt.ai", "https://app.reelbolt.ai"}
	if strings.Join(opened, " ") != strings.Join(want, " ") {
		t.Fatalf("opened %v, want %v", opened, want)
	}
	if got := s.View().Jobs[1].DashboardURL; got != want[0] {
		t.Errorf("view link = %q", got)
	}
}

// ---- the media cache ----

func TestTheMediaCacheIsShownCappedAndClearable(t *testing.T) {
	app, cleanup := settingsApp(t, nil)
	defer cleanup()
	s := app.Shell()
	v := s.View()
	m := v.Cache.Media
	if m == nil || !v.Cache.Capped {
		t.Fatalf("cache = %+v, want the media cache on by default", v.Cache)
	}
	if m.MaxBytes != config.DefaultMediaCacheBytes || deref64(v.Settings.MediaCacheMaxBytes) != config.DefaultMediaCacheBytes {
		t.Errorf("max = %d / settings %d", m.MaxBytes, deref64(v.Settings.MediaCacheMaxBytes))
	}

	// Put a file in it through the cache's own API, as a job would.
	c, err := srccache.Open(s.mediaCacheDir(), config.DefaultMediaCacheBytes)
	if err != nil {
		t.Fatal(err)
	}
	if c != s.mediaCache() {
		t.Fatal("the shell and srccache.Open disagree about which cache this is")
	}
	w, err := c.Create("projects/p/userFiles/clip.mp4")
	if err != nil {
		t.Fatal(err)
	}
	data := make([]byte, 4096)
	if _, err := w.Write(data); err != nil {
		t.Fatal(err)
	}
	if err := w.Commit("etag", int64(len(data)), ""); err != nil {
		t.Fatal(err)
	}
	if got := s.View().Cache.Media; got.Bytes != 4096 || got.Entries != 1 {
		t.Fatalf("after a download: %+v", got)
	}

	res, err := app.Do(ActionClearMediaCache, "")
	if err != nil {
		t.Fatalf("clear-media-cache: %v", err)
	}
	if res.Message != "Freed 4.0 KB. Footage will be downloaded again the next time a job needs it." {
		t.Errorf("message = %q", res.Message)
	}
	if got := res.View.Cache.Media; got.Bytes != 0 || got.Entries != 0 {
		t.Errorf("after clear: %+v", got)
	}
	if res, _ := app.Do(ActionClearMediaCache, ""); res.Message != "The media cache was already empty." {
		t.Errorf("second clear = %q", res.Message)
	}
}

func TestTheMediaCacheCapIsSavedAndApplied(t *testing.T) {
	app, cleanup := settingsApp(t, nil)
	defer cleanup()
	s := app.Shell()
	if _, err := app.SaveSettings(SettingsPatch{MediaCacheMaxBytes: ptr64(1 << 30)}); err != nil {
		t.Fatalf("SaveSettings: %v", err)
	}
	onDisk, err := config.LoadFrom(s.ConfigPath())
	if err != nil {
		t.Fatal(err)
	}
	if onDisk.MediaCache.EffectiveMaxBytes() != 1<<30 {
		t.Errorf("file cap = %d", onDisk.MediaCache.EffectiveMaxBytes())
	}
	if got := s.View().Cache.Media.MaxBytes; got != 1<<30 {
		t.Errorf("live cap = %d", got)
	}

	// Off: no media section, not capped, and clearing says so.
	if _, err := app.SaveSettings(SettingsPatch{MediaCacheMaxBytes: ptr64(0)}); err != nil {
		t.Fatalf("SaveSettings(0): %v", err)
	}
	v := s.View()
	if v.Cache.Media != nil || v.Cache.Capped || deref64(v.Settings.MediaCacheMaxBytes) != 0 {
		t.Fatalf("cache after turning it off = %+v (settings %d)", v.Cache, deref64(v.Settings.MediaCacheMaxBytes))
	}
	if _, err := app.Do(ActionClearMediaCache, ""); err == nil || !strings.Contains(err.Error(), "turned off") {
		t.Errorf("clearing an off cache = %v", err)
	}
	// On again.
	if _, err := app.SaveSettings(SettingsPatch{MediaCacheMaxBytes: ptr64(2 << 30)}); err != nil {
		t.Fatalf("SaveSettings(on): %v", err)
	}
	if m := s.View().Cache.Media; m == nil || m.MaxBytes != 2<<30 {
		t.Fatalf("cache after turning it on = %+v", m)
	}
	waitFor(t, 15*time.Second, func() bool { return s.View().Runner.Running })
}

// ---- unpair ----

func TestUnpairAsksReelBoltToForgetTheDevice(t *testing.T) {
	cases := []struct {
		name    string
		answer  error
		removed bool
	}{
		{"removed", nil, true},
		{"already removed", deviceapi.ErrNotConnected, true},
		{"older server", deviceapi.ErrNotFound, false},
		{"offline", errors.New("dial tcp: i/o timeout"), false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			called := false
			s, cleanup := newTestShell(t, func(o *Options) {
				o.ForgetMe = func(context.Context) error {
					called = true
					return tc.answer
				}
			})
			defer cleanup()
			a := NewApp(s, AppOptions{OpenURL: func(string) error { return nil }, Autostart: &fakeAutostart{}})
			res, err := a.Do(ActionUnpair, "")
			if err != nil {
				t.Fatalf("unpair: %v", err)
			}
			if !called {
				t.Fatal("ReelBolt was not asked to forget the device")
			}
			want := UnpairResult{Removed: tc.removed}.Message()
			if res.Message != want {
				t.Errorf("message = %q, want %q", res.Message, want)
			}
			if tc.removed == strings.Contains(res.Message, "may still be listed") {
				t.Errorf("message %q does not match removed=%v", res.Message, tc.removed)
			}
			// The local identity is gone either way, and in the test's own file.
			onDisk, err := config.LoadFrom(s.ConfigPath())
			if err != nil {
				t.Fatal(err)
			}
			if onDisk.DeviceID != "" || res.View.State != StateUnpaired {
				t.Errorf("device id %q, state %q after unpair", onDisk.DeviceID, res.View.State)
			}
		})
	}
}

func deref64(p *int64) int64 {
	if p == nil {
		return -1
	}
	return *p
}
