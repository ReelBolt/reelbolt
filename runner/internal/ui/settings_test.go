package ui

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/daemon"
	"github.com/vecchiotom/reelbolt/runner/internal/deviceapi"
	"github.com/vecchiotom/reelbolt/runner/internal/governor"
)

func settingsApp(t *testing.T, mutate func(*Options)) (*App, func()) {
	t.Helper()
	s, cleanup := newTestShell(t, mutate)
	return NewApp(s, AppOptions{OpenURL: func(string) error { return nil }, Autostart: &fakeAutostart{}}), cleanup
}

func TestSettingsMergeALimitsPatchAndRestartTheRunner(t *testing.T) {
	app, cleanup := settingsApp(t, nil)
	defer cleanup()
	s := app.Shell()
	before := s.config().Limits

	msg, err := app.SaveSettings(SettingsPatch{Limits: &governor.LimitsConfig{Memory: governor.ByteSize(2 << 30), CPUs: 1}})
	if err != nil {
		t.Fatalf("SaveSettings: %v", err)
	}
	if msg != savedMessage {
		t.Errorf("message = %q, want %q (nothing was running)", msg, savedMessage)
	}
	got := s.config().Limits
	if got.Memory != governor.ByteSize(2<<30) || got.CPUs != 1 {
		t.Fatalf("limits = %+v", got)
	}
	// The fields the patch left out keep their values.
	if got.PIDs != before.PIDs || got.Priority != before.Priority || got.DiskQuotaBytes != before.DiskQuotaBytes {
		t.Errorf("the patch changed fields it did not name: before %+v, after %+v", before, got)
	}
	// It reached the file.
	onDisk, err := config.LoadFrom(s.ConfigPath())
	if err != nil {
		t.Fatalf("reload: %v", err)
	}
	if onDisk.Limits != got {
		t.Errorf("file limits = %+v, want %+v", onDisk.Limits, got)
	}
	// And the in-process runner was restarted with them.
	waitFor(t, 15*time.Second, func() bool {
		d := s.currentDaemon()
		return d != nil && d.Limits().MemoryBytes == 2<<30 && s.View().Runner.Running
	})
	if v := s.View(); v.Settings.Limits.Memory != governor.ByteSize(2<<30) {
		t.Errorf("view limits = %+v", v.Settings.Limits)
	}
}

func TestSettingsWaitForTheJobInFlight(t *testing.T) {
	app, cleanup := settingsApp(t, nil)
	defer cleanup()
	s := app.Shell()
	first := s.currentDaemon()
	s.Ledger().ObserveJob(daemon.JobEvent{Kind: daemon.JobStarted, JobID: "j1", JobType: protocol.JobVideoCompile})

	msg, err := app.SaveSettings(SettingsPatch{Limits: &governor.LimitsConfig{CPUs: 1}})
	if err != nil {
		t.Fatalf("SaveSettings: %v", err)
	}
	if msg != "Saved. It applies to the next job." {
		t.Fatalf("message = %q", msg)
	}
	time.Sleep(300 * time.Millisecond)
	if s.currentDaemon() != first {
		t.Fatal("the runner was restarted under a running job")
	}
	s.Ledger().ObserveJob(daemon.JobEvent{Kind: daemon.JobFinished, JobID: "j1"})
	waitFor(t, 20*time.Second, func() bool {
		d := s.currentDaemon()
		return d != nil && d != first && d.Limits().CPUs == 1
	})
}

func TestSettingsApplyTheScheduleToTheRunningGovernor(t *testing.T) {
	app, cleanup := settingsApp(t, nil)
	defer cleanup()
	s := app.Shell()
	sched := governor.Schedule{Mode: "windows", Windows: []governor.Window{{Days: []string{"mon", "tue"}, Start: "09:00", End: "18:00"}}, OnBattery: "pause"}
	msg, err := app.SaveSettings(SettingsPatch{Schedule: &sched})
	if err != nil {
		t.Fatalf("SaveSettings: %v", err)
	}
	if msg != savedMessage {
		t.Errorf("message = %q", msg)
	}
	got := s.currentDaemon().Governor().Schedule()
	if got.Mode != governor.ModeWindows || len(got.Windows) != 1 || got.OnBattery != governor.BatteryPause || got.IdleMinutes != governor.DefaultIdleMinutes {
		t.Fatalf("governor schedule = %+v", got)
	}
	v := s.View()
	if v.Settings.Schedule.Mode != governor.ModeWindows {
		t.Errorf("view schedule = %+v", v.Settings.Schedule)
	}
	if want := "Works only at the hours you chose, Mon, Tue 09:00-18:00. Pauses on battery."; v.Runner.ScheduleSummary != want {
		t.Errorf("schedule summary = %q, want %q", v.Runner.ScheduleSummary, want)
	}
}

func TestSettingsRefuseWithASentence(t *testing.T) {
	app, cleanup := settingsApp(t, nil)
	defer cleanup()
	before := *app.Shell().config()
	bad := []struct {
		name  string
		patch SettingsPatch
		want  string
	}{
		{"nothing", SettingsPatch{}, "Nothing to save."},
		{"slots", SettingsPatch{Slots: ptr(99)}, "Jobs at once must be between 1 and 16."},
		{"memory", SettingsPatch{Limits: &governor.LimitsConfig{Memory: 1 << 20}}, "Memory per job must be at least 256.0 MB."},
		{"priority", SettingsPatch{Limits: &governor.LimitsConfig{Priority: "realtime"}}, "Choose Normal, Below normal or Low for how jobs share this computer."},
		{"mode", SettingsPatch{Schedule: &governor.Schedule{Mode: "sometimes"}}, "Choose when this computer works: any time, when you're away, when plugged in, or at set hours."},
		{"no windows", SettingsPatch{Schedule: &governor.Schedule{Mode: "windows"}}, "Add at least one time range, or choose another option."},
		{"bad time", SettingsPatch{Schedule: &governor.Schedule{Windows: []governor.Window{{Start: "9am", End: "18:00"}}}}, `"9am" isn't a time. Use the 24-hour clock, like 09:00.`},
		{"bad day", SettingsPatch{Schedule: &governor.Schedule{Windows: []governor.Window{{Days: []string{"someday"}, Start: "09:00", End: "18:00"}}}}, `"someday" isn't a day. Use names like Mon or Tuesday.`},
		{"cache", SettingsPatch{MediaCacheMaxBytes: ptr64(1024)}, "The media cache must be at least 256.0 MB, or 0 to turn it off."},
	}
	for _, tc := range bad {
		t.Run(tc.name, func(t *testing.T) {
			_, err := app.SaveSettings(tc.patch)
			if err == nil {
				t.Fatal("accepted")
			}
			if err.Error() != tc.want {
				t.Fatalf("error = %q, want %q", err.Error(), tc.want)
			}
		})
	}
	after := *app.Shell().config()
	if after.Slots != before.Slots || after.Limits != before.Limits || after.Schedule.Mode != before.Schedule.Mode {
		t.Fatal("a refused patch changed the configuration")
	}
}

func TestSettingsCarryTheMediaCacheCapAndTheHost(t *testing.T) {
	app, cleanup := settingsApp(t, func(o *Options) {
		o.HostFacts = func(string) (uint64, uint64) { return 16 << 30, 100 << 30 }
	})
	defer cleanup()
	if _, err := app.SaveSettings(SettingsPatch{MediaCacheMaxBytes: ptr64(10 << 30)}); err != nil {
		t.Fatalf("SaveSettings: %v", err)
	}
	v := app.View()
	if v.Settings.MediaCacheMaxBytes == nil || *v.Settings.MediaCacheMaxBytes != 10<<30 {
		t.Errorf("media cache cap = %v", v.Settings.MediaCacheMaxBytes)
	}
	h := v.Settings.Host
	if h == nil || h.CPUs < 1 || h.MemoryBytes != 16<<30 || h.DiskFreeBytes != 100<<30 {
		t.Fatalf("host = %+v", h)
	}
}

// TestSettingsJSONMatchesTheContract pins the field names types.ts reads.
func TestSettingsJSONMatchesTheContract(t *testing.T) {
	app, cleanup := settingsApp(t, func(o *Options) {
		o.HostFacts = func(string) (uint64, uint64) { return 8 << 30, 0 }
	})
	defer cleanup()
	raw, err := json.Marshal(app.View().Settings)
	if err != nil {
		t.Fatal(err)
	}
	var doc map[string]any
	if err := json.Unmarshal(raw, &doc); err != nil {
		t.Fatal(err)
	}
	limits, _ := doc["limits"].(map[string]any)
	for _, k := range []string{"memory", "cpus", "pids", "ffmpegThreads", "priority", "diskQuotaBytes"} {
		if _, ok := limits[k]; !ok {
			t.Errorf("settings.limits has no %q: %v", k, limits)
		}
	}
	if _, ok := limits["memory"].(float64); !ok {
		t.Errorf("settings.limits.memory is %T, want a number", limits["memory"])
	}
	host, _ := doc["host"].(map[string]any)
	if _, ok := host["cpus"]; !ok {
		t.Errorf("settings.host = %v", host)
	}
	if _, ok := host["diskFreeBytes"]; ok {
		t.Error("an unreadable free-space figure was sent as a number")
	}
}

func TestSettingsOverHTTP(t *testing.T) {
	app, cleanup := settingsApp(t, nil)
	defer cleanup()
	h := Handler(app)
	post := func(body string) ActionResult {
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, httptest.NewRequestWithContext(context.Background(), http.MethodPost, "/settings", strings.NewReader(body)))
		if rec.Code != http.StatusOK {
			t.Fatalf("status = %d", rec.Code)
		}
		var res ActionResult
		if err := json.Unmarshal(rec.Body.Bytes(), &res); err != nil {
			t.Fatalf("decode: %v", err)
		}
		return res
	}
	res := post(`{"limits":{"memory":"3g","priority":"low"},"schedule":{"mode":"idle","idleMinutes":10}}`)
	if !res.OK || res.Message != savedMessage {
		t.Fatalf("result = %+v", res)
	}
	if res.View.Settings.Limits.Memory != governor.ByteSize(3<<30) || res.View.Settings.Limits.Priority != governor.PriorityLow {
		t.Errorf("limits = %+v", res.View.Settings.Limits)
	}
	if res.View.Settings.Schedule.Mode != governor.ModeIdle || res.View.Settings.Schedule.IdleMinutes != 10 {
		t.Errorf("schedule = %+v", res.View.Settings.Schedule)
	}
	res = post(`{"slots":0}`)
	if res.OK || res.Message != "Jobs at once must be between 1 and 16." {
		t.Fatalf("refused result = %+v", res)
	}
	res = post(`{"limits":{"memory":true}}`)
	if res.OK || !strings.Contains(res.Message, "couldn't be read") {
		t.Fatalf("unreadable result = %+v", res)
	}
}

// ---- the workspace name ----

func TestTheViewNamesTheWorkspace(t *testing.T) {
	var calls atomic.Int32
	s, cleanup := newTestShell(t, func(o *Options) {
		o.Whoami = func(context.Context) (deviceapi.Me, error) {
			calls.Add(1)
			return deviceapi.Me{OrganizationName: "Acme Studio", PairedByEmail: "ann@acme.test", DeviceID: "dev-1"}, nil
		}
	})
	defer cleanup()
	waitFor(t, 5*time.Second, func() bool { return s.View().Device.WorkspaceName == "Acme Studio" })
	v := s.View()
	if v.Device.PairedByEmail != "ann@acme.test" || v.Device.Workspace != "org-1" {
		t.Errorf("device = %+v", v.Device)
	}
	// Cached: rebuilding the view does not ask again.
	for i := 0; i < 20; i++ {
		_ = s.View()
	}
	if n := calls.Load(); n != 1 {
		t.Errorf("whoami ran %d times, want once", n)
	}
}

func TestAFailedWhoamiNeverBlocksTheView(t *testing.T) {
	block := make(chan struct{})
	defer close(block)
	s, cleanup := newTestShell(t, func(o *Options) {
		o.Whoami = func(ctx context.Context) (deviceapi.Me, error) {
			select {
			case <-block:
			case <-ctx.Done():
			}
			return deviceapi.Me{}, errors.New("offline")
		}
	})
	defer cleanup()
	start := time.Now()
	v := s.View()
	if time.Since(start) > time.Second {
		t.Fatal("the view waited for the device API")
	}
	if v.Device.WorkspaceName != "" {
		t.Errorf("workspace name = %q before any answer", v.Device.WorkspaceName)
	}
}

// ---- wording ----

func TestTheScheduleRemedyOpensSettings(t *testing.T) {
	if got := RemedyAction(RemedySchedule); got != ActionOpenSettings {
		t.Fatalf("RemedyAction(schedule) = %q, want %q", got, ActionOpenSettings)
	}
	in := base()
	in.Runner = running(governor.Status{Allowed: false, Reason: governor.ReasonOutsideWindow,
		Detail: "Thu 07:00 is outside the configured hours", NextChange: "2026-10-08T08:00:00Z"})
	v := Build(in)
	if v.Remedy == nil || v.Remedy.Label != "Change schedule" {
		t.Fatalf("remedy = %+v", v.Remedy)
	}
	if strings.Contains(v.Detail, "configured hours") || !strings.Contains(v.Detail, "hours you chose") {
		t.Errorf("detail = %q; it should be the plain sentence, not the governor's", v.Detail)
	}
}

func TestGovernorReasonsInPlainWords(t *testing.T) {
	sched := governor.Schedule{Mode: governor.ModeIdle, IdleMinutes: 5}
	cases := map[string]governor.Status{
		"You're using this computer. It starts working once you've been away for 5 minutes.": {Reason: governor.ReasonUserActive, Detail: "you were active 1m0s ago and this runner waits for 5m0s"},
		"This computer is on battery. It starts working again when you plug it in.":          {Reason: governor.ReasonOnBattery, Detail: "this runner only works on mains power"},
	}
	for want, st := range cases {
		if got := describeGovernor(st, sched).Detail; got != want {
			t.Errorf("describeGovernor(%s) = %q, want %q", st.Reason, got, want)
		}
	}
	if got := describeGovernor(governor.Status{Allowed: true, Degraded: true, Mode: governor.ModeIdle}, sched).Detail; !strings.Contains(got, "can't tell when you're away") {
		t.Errorf("degraded detail = %q", got)
	}
}

func TestDescribeSchedule(t *testing.T) {
	cases := map[string]governor.Schedule{
		"Works any time.": {},
		"Works when you've been away for 1 minute.":                            {Mode: governor.ModeIdle, IdleMinutes: 1},
		"Works only when plugged in.":                                          {Mode: governor.ModeAC, OnBattery: "pause"},
		"Works any time, every day 22:00-06:00. Pauses on battery.":            {Windows: []governor.Window{{Start: "22:00", End: "06:00"}}, OnBattery: "pause"},
		"Works only at the hours you chose, Sat 10:00-12:00; Sun 10:00-12:00.": {Mode: governor.ModeWindows, Windows: []governor.Window{{Days: []string{"sat"}, Start: "10:00", End: "12:00"}, {Days: []string{"SUNDAY"}, Start: "10:00", End: "12:00"}}},
	}
	for want, sched := range cases {
		if got := describeSchedule(sched); got != want {
			t.Errorf("describeSchedule(%+v) = %q, want %q", sched, got, want)
		}
	}
}

// TestNoBackendWordsReachThePerson walks every state the classifier can
// produce and every remedy, and checks the sentences for the words a creator
// should never have to read.
func TestNoBackendWordsReachThePerson(t *testing.T) {
	banned := []string{"runner.json", "gateway", "container runtime", "runtime image", "remotion", "ffmpeg",
		"device key", "keyring", "slot", "probe", "capability", "protocol", "hkcu", "xdg", "d26", "pid",
		"lease", "session", "workspace id", "drain", "daemon", "—"}
	var views []View
	mutations := []func(*Inputs){
		nil,
		func(in *Inputs) { in.Paired = false },
		func(in *Inputs) { in.Revoked = true; in.Paired = false },
		func(in *Inputs) {
			in.Runtime = RuntimeView{Known: true, Failure: runtimeFailureSentence("docker", "unreachable")}
		},
		func(in *Inputs) { in.Runtime = RuntimeView{Known: true} },
		func(in *Inputs) { in.Runner = RunnerState{}; in.DaemonErr = "daemon: probe: exit 125" },
		func(in *Inputs) { in.Runner = RunnerState{} },
		func(in *Inputs) { in.Runner = RunnerState{}; in.Restarting = true },
		func(in *Inputs) {
			in.Runner = running(governor.Status{Allowed: false, Reason: "paused", Held: true, HeldBy: governor.SourceRemote})
		},
		func(in *Inputs) {
			in.Runner = running(governor.Status{Allowed: false, Reason: "paused", Held: true, HeldBy: governor.SourceUser})
		},
		func(in *Inputs) {
			in.Runner = running(governor.Status{Allowed: false, Reason: governor.ReasonOnBattery, Detail: "this runner pauses on battery"})
		},
		func(in *Inputs) {
			in.Runtime.Known = true
			in.Runtime.ContainerCLI = "docker"
			in.Runtime.ImagePresent = new(bool)
		},
		func(in *Inputs) {
			p := 3
			in.Current = &Job{ID: "j", Type: protocol.JobRemotionWorkspace, Started: in.Now, Percent: &p}
		},
	}
	for _, m := range mutations {
		in := base()
		if m != nil {
			m(&in)
		}
		views = append(views, Build(in))
	}
	for _, v := range views {
		texts := []string{v.Headline, v.Detail, v.Runner.ReasonDetail, v.Runner.ScheduleSummary, Tooltip(v)}
		if v.Remedy != nil {
			texts = append(texts, v.Remedy.Label, v.Remedy.Detail)
		}
		for _, item := range Menu(v) {
			texts = append(texts, item.Label)
		}
		for _, text := range texts {
			lower := strings.ToLower(text)
			for _, word := range banned {
				if strings.Contains(lower, word) {
					t.Errorf("%q (state %s) says %q", text, v.State, word)
				}
			}
		}
	}
	for _, code := range []string{"unreachable", "version_too_old", "info_failed", "internal_network_unsupported",
		"no_image_configured", "image_missing", "arch_mismatch", "image_run_failed", "egress_possible", "x"} {
		lower := strings.ToLower(runtimeFailureSentence("podman", code))
		for _, word := range banned {
			if strings.Contains(lower, word) {
				t.Errorf("runtime sentence for %s says %q", code, word)
			}
		}
	}
}

func ptr(n int) *int       { return &n }
func ptr64(n int64) *int64 { return &n }
