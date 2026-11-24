package ui

import (
	"strings"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/governor"
)

// menuIDs is the enabled row ids, which is what a click can reach.
func menuIDs(v View) []string {
	var out []string
	for _, item := range Menu(v) {
		if item.ID != "" && item.Enabled {
			out = append(out, item.ID)
		}
	}
	return out
}

func has(list []string, want string) bool {
	for _, s := range list {
		if s == want {
			return true
		}
	}
	return false
}

func TestMenuOffersExactlyWhatTheStateAllows(t *testing.T) {
	paused := base()
	paused.Runner = running(governor.Status{
		Allowed: false, Reason: "paused", Held: true,
		HeldBy: governor.SourceUser, HeldReason: "installing a driver",
	})
	remote := base()
	remote.Runner = running(governor.Status{
		Allowed: false, Reason: "paused", Held: true,
		HeldBy: governor.SourceRemote, HeldReason: "an administrator paused this device",
	})
	unpaired := base()
	unpaired.Paired = false
	stopped := base()
	stopped.Runner = RunnerState{}

	cases := []struct {
		name      string
		view      View
		want      []string
		notWanted []string
	}{
		{
			name: "idle",
			view: Build(base()),
			want: []string{ActionPause, ActionOpenWindow, ActionOpenDashboard, ActionQuit},
		},
		{
			name:      "locally paused",
			view:      Build(paused),
			want:      []string{ActionResume, ActionQuit},
			notWanted: []string{ActionPause},
		},
		{
			name:      "paused by the gateway",
			view:      Build(remote),
			notWanted: []string{ActionPause, ActionResume},
		},
		{
			name:      "unpaired",
			view:      Build(unpaired),
			want:      []string{ActionPair, ActionQuit},
			notWanted: []string{ActionPause},
		},
		{
			name:      "nothing running",
			view:      Build(stopped),
			want:      []string{ActionStartRunner, ActionQuit},
			notWanted: []string{ActionPause, ActionStopRunner},
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got := menuIDs(tc.view)
			for _, w := range tc.want {
				if !has(got, w) {
					t.Errorf("menu %v does not offer %q", got, w)
				}
			}
			for _, w := range tc.notWanted {
				if has(got, w) {
					t.Errorf("menu %v offers %q, which this state must not", got, w)
				}
			}
		})
	}
}

// TestMenuNeverOffersAnUnhandledAction walks every action the menu can reach and
// checks it against Actions(), which App.Do dispatches.
func TestMenuNeverOffersAnUnhandledAction(t *testing.T) {
	known := map[string]bool{}
	for _, a := range Actions() {
		known[a] = true
	}
	in := base()
	in.Runner = RunnerState{}
	views := []View{Build(base()), Build(in)}
	au := base()
	au.Runner = RunnerState{}
	au.Paired = false
	views = append(views, Build(au))
	for _, v := range views {
		for _, item := range Menu(v) {
			if item.ID == "" {
				continue
			}
			if !known[item.ID] {
				t.Fatalf("the menu offers %q, which is not in Actions()", item.ID)
			}
		}
	}
}

// TestMenuShape: the first row is a status line a person reads without clicking,
// Quit is last and always enabled, and the window is always reachable.
func TestMenuShape(t *testing.T) {
	items := Menu(Build(base()))
	if len(items) < 4 {
		t.Fatalf("menu has %d rows", len(items))
	}
	if items[0].Enabled || items[0].ID != "" {
		t.Errorf("the first row %+v is clickable; it is the status line", items[0])
	}
	if items[0].Label != "Ready for work" {
		t.Errorf("status line = %q", items[0].Label)
	}
	last := items[len(items)-1]
	if last.ID != ActionQuit || !last.Enabled {
		t.Errorf("the last row is %+v; Quit must be last and always enabled", last)
	}
	if !has(menuIDs(Build(base())), ActionOpenWindow) {
		t.Error("the window is not reachable from the tray")
	}
	// The unpaired case still needs the window: it is where pairing lives.
	in := base()
	in.Paired = false
	if !has(menuIDs(Build(in)), ActionOpenWindow) {
		t.Error("an unpaired machine cannot reach the pairing window")
	}
}

// TestMenuNamesTheCurrentJob: "working" with no noun is the least useful thing a
// tray can say.
func TestMenuNamesTheCurrentJob(t *testing.T) {
	in := base()
	p := 12
	in.Current = &Job{ID: "j1", Type: protocol.JobVideoCompile, Started: in.Now.Add(-70 * time.Second), Percent: &p}
	items := Menu(Build(in))
	found := false
	for _, item := range items {
		if item.ID == ActionOpenJobs {
			found = true
			if !strings.Contains(item.Label, "Putting the video together") || !strings.Contains(item.Label, "12%") {
				t.Errorf("the current-job row is %q", item.Label)
			}
		}
	}
	if !found {
		t.Fatal("the menu does not name the job in flight")
	}
}

func TestTooltipCarriesTheReasonAndTheRemedy(t *testing.T) {
	in := base()
	in.Runner = running(governor.Status{
		Allowed: false, Reason: "paused", Held: true,
		HeldBy: governor.SourceUser, HeldReason: "installing a driver",
	})
	tip := Tooltip(Build(in))
	for _, want := range []string{"ReelBolt", "Paused", "installing a driver", "Resume"} {
		if !strings.Contains(tip, want) {
			t.Errorf("tooltip %q does not contain %q", tip, want)
		}
	}
}

func TestShortDuration(t *testing.T) {
	cases := map[time.Duration]string{
		0:                           "0s",
		45 * time.Second:            "45s",
		70 * time.Second:            "1m10s",
		3*time.Hour + 5*time.Minute: "3h05m",
		-time.Second:                "0s",
	}
	for d, want := range cases {
		if got := shortDuration(d); got != want {
			t.Errorf("shortDuration(%s) = %q, want %q", d, got, want)
		}
	}
}

// TestIconFallsBackToTheBrandMark: a host with no per-state art must still get a
// real icon, and a host that has one must be believed.
func TestIconFallsBackToTheBrandMark(t *testing.T) {
	for _, size := range []int{16, 22, 24, 32} {
		for _, dark := range []bool{false, true} {
			if len(Mark(size, dark)) == 0 {
				t.Fatalf("no brand mark at %d px (dark=%v)", size, dark)
			}
		}
	}
	if string(Mark(16, false)) == string(Mark(16, true)) {
		t.Error("the light and dark marks are the same bytes")
	}
	set := fakeIconSet{}
	if string(Icon(set, StateWorking, 16, false)) != "per-state" {
		t.Error("a host-supplied icon was not used")
	}
	if len(Icon(nil, StateIdle, 16, false)) == 0 {
		t.Error("no icon when the host supplies none")
	}
}

type fakeIconSet struct{}

func (fakeIconSet) Icon(state TrayState, size int, dark bool) ([]byte, bool) {
	if state == StateWorking {
		return []byte("per-state"), true
	}
	return nil, false
}
