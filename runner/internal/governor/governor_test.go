package governor

import (
	"sync/atomic"
	"testing"
	"time"
)

// fakeSignals is a machine a test describes.
type fakeSignals struct {
	idle    time.Duration
	idleOK  bool
	onAC    bool
	powerOK bool

	idleCalls  int32
	powerCalls int32
}

func (f *fakeSignals) IdleFor() (time.Duration, bool) {
	atomic.AddInt32(&f.idleCalls, 1)
	return f.idle, f.idleOK
}

func (f *fakeSignals) OnAC() (bool, bool) {
	atomic.AddInt32(&f.powerCalls, 1)
	return f.onAC, f.powerOK
}

func (f *fakeSignals) Describe() string { return "a machine a test described" }

func TestPauseAlwaysWinsAndResumeAlwaysReleases(t *testing.T) {
	sigs := &fakeSignals{idleOK: true, powerOK: true}
	g := New(Config{Signals: sigs})
	if g.Allowed() != true {
		t.Fatal("a governor with no schedule must start working")
	}

	g.Pause(SourceUser, "the user asked")
	if g.Allowed() {
		t.Fatal("a paused governor must not allow work")
	}
	st := g.Status()
	if !st.Held || st.HeldBy != SourceUser || st.Reason != ReasonPaused {
		t.Fatalf("status = %+v", st)
	}
	if st.Offline() != true {
		t.Fatal("a local pause must take the runner off the gateway")
	}

	if !g.Resume() {
		t.Fatal("Resume must report that a hold was in force")
	}
	if !g.Allowed() {
		t.Fatal("a resumed governor must allow work again")
	}
	if g.Resume() {
		t.Fatal("a second Resume must report that nothing was held")
	}
}

// The one asymmetry the daemon depends on: a hold the GATEWAY placed does not
// take the runner offline, because the runner has to stay reachable for the
// gateway to resume it.
func TestARemoteHoldKeepsTheConnection(t *testing.T) {
	g := New(Config{Signals: &fakeSignals{}})
	g.Pause(SourceRemote, "paused from the dashboard")
	st := g.Status()
	if st.Offline() {
		t.Fatalf("a remote hold must not take the runner offline: %+v", st)
	}

	// And a schedule gate does the opposite: a machine that is not working
	// should not be holding a socket at all.
	s := Schedule{Mode: ModeAC}
	s.ApplyDefaults()
	g2 := New(Config{Schedule: s, Signals: &fakeSignals{powerOK: true, onAC: false}})
	if st := g2.Status(); !st.Offline() {
		t.Fatalf("a schedule gate must take the runner offline: %+v", st)
	}
}

func TestResumeReleasesWhateverHeldIt(t *testing.T) {
	// A governor stopped by its schedule is not "held", so Resume has nothing
	// to release — but it must never make things worse by pretending to.
	s := Schedule{Mode: ModeIdle}
	s.ApplyDefaults()
	sigs := &fakeSignals{idleOK: true, idle: 0}
	g := New(Config{Schedule: s, Signals: sigs})
	if g.Allowed() {
		t.Fatal("the user is at the machine, so an idle-mode runner must wait")
	}
	if g.Resume() {
		t.Fatal("Resume must not claim to have released a schedule gate")
	}
	if g.Allowed() {
		t.Fatal("Resume must not override the schedule")
	}
}

func TestTheDefaultScheduleNeverTouchesTheMachine(t *testing.T) {
	sigs := &fakeSignals{}
	var s Schedule
	s.ApplyDefaults()
	g := New(Config{Schedule: s, Signals: sigs})
	g.Refresh()
	if atomic.LoadInt32(&sigs.idleCalls) != 0 || atomic.LoadInt32(&sigs.powerCalls) != 0 {
		t.Fatalf("an unconfigured schedule must read nothing: idle=%d power=%d",
			sigs.idleCalls, sigs.powerCalls)
	}
	if !g.Allowed() {
		t.Fatal("the default governor must allow work")
	}
}

func TestChangedFiresOncePerTransition(t *testing.T) {
	g := New(Config{Signals: &fakeSignals{}})
	changed := g.Changed()

	g.Pause(SourceUser, "asked")
	select {
	case <-changed:
	default:
		t.Fatal("Pause must wake a waiter")
	}
	// The channel is replaced, so a caller that keeps the old one waits for
	// ever. That is the contract: call Changed again after it fires, and the
	// new channel is open (a pending change has not happened yet).
	select {
	case <-g.Changed():
		t.Fatal("Changed must hand out a fresh, unfired channel after a transition")
	default:
	}
	// An identical transition is not a change.
	again := g.Changed()
	g.Pause(SourceUser, "asked")
	select {
	case <-again:
		t.Fatal("a repeat of the same state must not wake a waiter")
	default:
	}
}

func TestARefreshThatChangesTheVerdictWakesA_Waiter(t *testing.T) {
	s := Schedule{Mode: ModeIdle}
	s.ApplyDefaults()
	s.IdleMinutes = 5
	sigs := &fakeSignals{idleOK: true, idle: 0}
	g := New(Config{Schedule: s, Signals: sigs})
	if g.Allowed() {
		t.Fatal("the user is typing")
	}
	changed := g.Changed()
	sigs.idle = 30 * time.Minute
	g.Refresh()
	select {
	case <-changed:
	default:
		t.Fatal("the user walked away and nobody was told")
	}
	if !g.Allowed() {
		t.Fatal("idle past the threshold must allow work")
	}
}

func TestStatusExplainsADegradedGate(t *testing.T) {
	s := Schedule{Mode: ModeIdle}
	s.ApplyDefaults()
	g := New(Config{Schedule: s, Signals: &fakeSignals{idleOK: false}})
	st := g.Status()
	if !st.Allowed || !st.Degraded {
		t.Fatalf("status = %+v, want a degraded allow", st)
	}
	if st.Idle != "unknown" {
		t.Errorf("idle = %q, want unknown", st.Idle)
	}
	if st.Signals == "" || st.Schedule == "" {
		t.Errorf("status must name the source and the policy: %+v", st)
	}
}

func TestSetScheduleReevaluates(t *testing.T) {
	g := New(Config{Signals: &fakeSignals{}})
	always := Schedule{}
	always.ApplyDefaults()
	g.SetSchedule(always)
	if !g.Allowed() {
		t.Fatal("always must allow")
	}
	windows := Schedule{Mode: ModeWindows, Windows: []Window{{Start: "00:00", End: "00:01"}}}
	windows.ApplyDefaults()
	g.SetSchedule(windows)
	if g.Status().Signals == "" {
		t.Error("status must still describe the machine reader")
	}
}
