package ui

import (
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/governor"
)

// capable is a machine that can run something, which most of these cases need
// so that the runtime check does not decide the answer for them.
func capable() RuntimeView {
	return RuntimeView{
		JobTypes:      []string{protocol.JobVideoCompile},
		FfmpegVersion: "7.1",
		Slots:         1,
	}
}

func running(g governor.Status) RunnerState {
	return RunnerState{Running: true, PID: 42, StartedAt: "2026-10-08T10:00:00Z", Governor: g, GovernorKnown: true}
}

func base() Inputs {
	return Inputs{
		Version:    "1.2.3",
		Paired:     true,
		DeviceID:   "dev-1",
		DeviceName: "studio-laptop",
		Workspace:  "org-1",
		GatewayURL: "wss://runner.reelbolt.ai/v1/connect",
		Runtime:    capable(),
		Runner:     running(governor.Status{Allowed: true, Reason: "schedule allows work now"}),
		Now:        time.Date(2026, 10, 8, 12, 0, 0, 0, time.UTC),
	}
}

func TestBuildClassifiesEveryState(t *testing.T) {
	cases := []struct {
		name         string
		mutate       func(*Inputs)
		wantState    TrayState
		wantRemedy   RemedyKind
		wantHeadline string
	}{
		{
			name:         "a machine that was never paired",
			mutate:       func(in *Inputs) { in.Paired = false; in.DeviceID = ""; in.GatewayURL = "" },
			wantState:    StateUnpaired,
			wantRemedy:   RemedyPair,
			wantHeadline: "Not connected to ReelBolt",
		},
		{
			name:         "a device the gateway revoked",
			mutate:       func(in *Inputs) { in.Revoked = true; in.Paired = false },
			wantState:    StateUnpaired,
			wantRemedy:   RemedyPair,
			wantHeadline: "This computer was removed from ReelBolt",
		},
		{
			name: "a machine with no container runtime and no ffmpeg",
			mutate: func(in *Inputs) {
				in.Runtime = RuntimeView{Failure: "Video rendering needs Docker or Podman, and neither is installed."}
			},
			wantState:    StateError,
			wantRemedy:   RemedyRuntime,
			wantHeadline: "This computer can't make videos yet",
		},
		{
			name:         "a runner that exited with an error",
			mutate:       func(in *Inputs) { in.Runner = RunnerState{}; in.DaemonErr = "probe: docker returned 125" },
			wantState:    StateError,
			wantRemedy:   RemedyStart,
			wantHeadline: "ReelBolt stopped because of a problem",
		},
		{
			name:         "nothing running",
			mutate:       func(in *Inputs) { in.Runner = RunnerState{} },
			wantState:    StateStopped,
			wantRemedy:   RemedyStart,
			wantHeadline: "Not running",
		},
		{
			name: "paused by the user",
			mutate: func(in *Inputs) {
				in.Runner = running(governor.Status{Allowed: false, Reason: "paused", Detail: "installing a GPU driver",
					Held: true, HeldBy: governor.SourceUser, HeldReason: "installing a GPU driver"})
			},
			wantState:    StatePaused,
			wantRemedy:   RemedyResume,
			wantHeadline: "Paused",
		},
		{
			name: "paused by the gateway",
			mutate: func(in *Inputs) {
				in.Runner = running(governor.Status{Allowed: false, Reason: "paused",
					Held: true, HeldBy: governor.SourceRemote, HeldReason: "an administrator paused this device"})
			},
			wantState:    StatePaused,
			wantRemedy:   RemedyNone,
			wantHeadline: "Paused by ReelBolt",
		},
		{
			name: "held by the machine schedule, not by a pause",
			mutate: func(in *Inputs) {
				in.Runner = running(governor.Status{Allowed: false, Reason: "outside the schedule window",
					Detail: "outside the schedule window", NextChange: "2026-10-08T08:00:00Z"})
			},
			wantState:    StatePaused,
			wantRemedy:   RemedySchedule,
			wantHeadline: "Waiting for your schedule",
		},
		{
			name: "a job in flight",
			mutate: func(in *Inputs) {
				p := 42
				in.Current = &Job{ID: "j1", Type: protocol.JobRemotionWorkspace, Started: in.Now.Add(-90 * time.Second), Percent: &p}
			},
			wantState:    StateWorking,
			wantRemedy:   RemedyNone,
			wantHeadline: "Working: Rendering a video (42%)",
		},
		{
			name:         "ready and idle",
			wantState:    StateIdle,
			wantRemedy:   RemedyNone,
			wantHeadline: "Ready for work",
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			in := base()
			if tc.mutate != nil {
				tc.mutate(&in)
			}
			v := Build(in)
			if v.State != tc.wantState {
				t.Fatalf("state = %q, want %q (headline %q)", v.State, tc.wantState, v.Headline)
			}
			if tc.wantHeadline != "" && v.Headline != tc.wantHeadline {
				t.Errorf("headline = %q, want %q", v.Headline, tc.wantHeadline)
			}
			got := RemedyNone
			if v.Remedy != nil {
				got = v.Remedy.Kind
			}
			if got != tc.wantRemedy {
				t.Errorf("remedy = %q, want %q", got, tc.wantRemedy)
			}
		})
	}
}

// TestEveryStateIsNamedAndRanked is the property the tray exists for: a state a
// person cannot read is a state they cannot act on.
func TestEveryStateIsNamedAndRanked(t *testing.T) {
	for _, s := range States() {
		if !s.Known() {
			t.Fatalf("%q is not Known", s)
		}
	}
	if TrayState("green").Known() {
		t.Fatal("an unknown state reported as known")
	}
	if len(States()) != 6 {
		t.Fatalf("States() = %d, want 6", len(States()))
	}
}

// TestARemedyAlwaysHasAnAction is the "no dead buttons" rule: every remedy the
// classifier can produce maps to an action the shell handles, except the one
// that deliberately says there is nothing to do.
func TestARemedyAlwaysHasAnAction(t *testing.T) {
	kinds := []RemedyKind{RemedyNone, RemedyPair, RemedyStart, RemedyResume, RemedySchedule, RemedyRuntime, RemedyUpdate}
	known := map[string]bool{}
	for _, a := range Actions() {
		known[a] = true
	}
	for _, k := range kinds {
		a := RemedyAction(k)
		if k == RemedyNone {
			if a != "" {
				t.Errorf("RemedyNone maps to %q; it must offer nothing", a)
			}
			continue
		}
		if a == "" {
			t.Errorf("remedy %q has no action", k)
			continue
		}
		if !known[a] {
			t.Errorf("remedy %q maps to %q, which is not in Actions()", k, a)
		}
	}
}

// TestAGatewayHoldNeverOffersResume: only the gateway can release its own hold,
// and the runner keeps the connection open to hear that. A Resume button here
// would be a button that does nothing, which is worse than no button.
func TestAGatewayHoldNeverOffersResume(t *testing.T) {
	in := base()
	in.Runner = running(governor.Status{
		Allowed: false, Reason: "paused", Held: true,
		HeldBy: governor.SourceRemote, HeldReason: "an administrator paused this device",
	})
	v := Build(in)
	if v.Remedy == nil || v.Remedy.Kind != RemedyNone {
		t.Fatalf("remedy = %+v, want none", v.Remedy)
	}
	for _, item := range Menu(v) {
		if item.ID == ActionResume || item.ID == ActionPause {
			t.Fatalf("the menu offers %q for a gateway hold", item.ID)
		}
	}
}

// TestAScheduleHoldDoesNotClaimResumeWouldFixIt: governor.Resume clears a hold
// and nothing else, so offering it for a schedule gate would contradict what
// "reelbolt-runner resume" actually does.
func TestAScheduleHoldDoesNotClaimResumeWouldFixIt(t *testing.T) {
	in := base()
	in.Runner = running(governor.Status{
		Allowed: false, Reason: "outside the schedule window", Detail: "outside the schedule window",
		NextChange: "2026-10-08T08:00:00Z",
	})
	v := Build(in)
	if v.Remedy == nil {
		t.Fatal("no remedy for a machine the schedule is holding")
	}
	if v.Remedy.Kind != RemedySchedule {
		t.Fatalf("remedy = %q, want %q", v.Remedy.Kind, RemedySchedule)
	}
	if RemedyAction(v.Remedy.Kind) == ActionResume {
		t.Fatal("the schedule remedy is a Resume, which cannot clear a schedule gate")
	}
}

// TestRebuildWithoutChangeIsStable keeps the pure function honest: Build must
// not depend on a clock it was not given.
func TestRebuildWithoutChangeIsStable(t *testing.T) {
	in := base()
	in.Jobs = []Job{{ID: "j1", Type: protocol.JobVideoCompile, Started: in.Now.Add(-time.Minute), Ended: in.Now.Add(-30 * time.Second)}}
	a := Build(in)
	b := Build(in)
	if !sameView(a, b) {
		t.Fatal("Build is not deterministic for the same Inputs")
	}
}
