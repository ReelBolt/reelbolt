package governor

import (
	"strings"
	"testing"
	"time"
)

func schedAt(t *testing.T, mode Mode, windows []Window, battery string) Schedule {
	t.Helper()
	s := Schedule{Mode: mode, Windows: windows, OnBattery: battery}
	s.ApplyDefaults()
	if err := s.Validate(); err != nil {
		t.Fatalf("the fixture schedule is invalid: %v", err)
	}
	return s
}

func at(hour, min int, day time.Weekday) time.Time {
	// A Monday at 00:00, so a weekday fixture is a fixed date.
	base := time.Date(2026, time.January, 5, 0, 0, 0, 0, time.Local)
	return base.AddDate(0, 0, int(day-time.Monday)).Add(time.Duration(hour)*time.Hour + time.Duration(min)*time.Minute)
}

func TestAlwaysAllows(t *testing.T) {
	s := Schedule{}
	s.ApplyDefaults()
	v := s.Evaluate(at(3, 0, time.Sunday), Machine{})
	if !v.Allow || v.Reason != ReasonAllowed {
		t.Fatalf("an unconfigured schedule must allow: %+v", v)
	}
	if v.Degraded {
		t.Errorf("an unconfigured schedule needs no machine state: %+v", v)
	}
}

func TestIdleModeWaitsForTheUserToLeave(t *testing.T) {
	s := schedAt(t, ModeIdle, nil, BatteryAllow)
	s.IdleMinutes = 5

	cases := []struct {
		name   string
		m      Machine
		allow  bool
		reason string
	}{
		{"the user is typing", Machine{IdleFor: 30 * time.Second, IdleKnown: true}, false, ReasonUserActive},
		{"exactly at the threshold", Machine{IdleFor: 5 * time.Minute, IdleKnown: true}, true, ReasonAllowed},
		{"the user is away", Machine{IdleFor: 20 * time.Minute, IdleKnown: true}, true, ReasonAllowed},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			v := s.Evaluate(at(10, 0, time.Monday), tc.m)
			if v.Allow != tc.allow || v.Reason != tc.reason {
				t.Fatalf("verdict = %+v, want allow=%t reason=%s", v, tc.allow, tc.reason)
			}
		})
	}
}

// The invariant this package exists to keep: a machine that cannot report its
// idle state allows work and says so. The alternative — treating "unknown" as
// "not idle" — is a runner that stops for good the first time a probe breaks,
// which is exactly the failure mode a governor must not have.
func TestAnUnknownIdleStateAllowsWorkAndSaysSo(t *testing.T) {
	s := schedAt(t, ModeIdle, nil, BatteryAllow)
	v := s.Evaluate(at(10, 0, time.Monday), Machine{IdleKnown: false})
	if !v.Allow {
		t.Fatalf("an unreadable idle state must not stop the machine: %+v", v)
	}
	if !v.Degraded || !strings.Contains(v.Detail, "when you're away") {
		t.Fatalf("the degradation must be visible: %+v", v)
	}
	// The result is not the same as a plain "allowed": one is a decision, the
	// other is an absence of information.
	if v.Reason != ReasonAllowed || v.Detail == "" {
		t.Fatalf("verdict = %+v", v)
	}
}

func TestACModeAndTheBatteryGate(t *testing.T) {
	cases := []struct {
		name  string
		sched Schedule
		m     Machine
		allow bool
	}{
		{"ac mode on battery", schedAt(t, ModeAC, nil, BatteryAllow), Machine{OnAC: false, PowerKnown: true}, false},
		{"ac mode on mains", schedAt(t, ModeAC, nil, BatteryAllow), Machine{OnAC: true, PowerKnown: true}, true},
		{"battery gate on always", schedAt(t, ModeAlways, nil, BatteryPause), Machine{OnAC: false, PowerKnown: true}, false},
		{"battery gate on mains", schedAt(t, ModeAlways, nil, BatteryPause), Machine{OnAC: true, PowerKnown: true}, true},
		{"power unreadable", schedAt(t, ModeAC, nil, BatteryAllow), Machine{}, true},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			v := tc.sched.Evaluate(at(10, 0, time.Monday), tc.m)
			if v.Allow != tc.allow {
				t.Fatalf("verdict = %+v, want allow=%t", v, tc.allow)
			}
		})
	}
}

// A machine with no power source to read is degraded, not stopped.
func TestAnUnknownPowerSourceAllowsWork(t *testing.T) {
	s := schedAt(t, ModeAC, nil, BatteryAllow)
	v := s.Evaluate(at(10, 0, time.Monday), Machine{PowerKnown: false})
	if !v.Allow || !v.Degraded {
		t.Fatalf("verdict = %+v, want a degraded allow", v)
	}
}

func TestTimeWindows(t *testing.T) {
	weekday := []Window{{Days: []string{"mon", "tue", "wed", "thu", "fri"}, Start: "09:00", End: "18:00"}}
	s := schedAt(t, ModeAlways, weekday, BatteryAllow)

	cases := []struct {
		name  string
		when  time.Time
		allow bool
	}{
		{"inside, on a monday", at(10, 0, time.Monday), true},
		{"the first minute counts", at(9, 0, time.Monday), true},
		{"the last minute does not", at(18, 0, time.Monday), false},
		{"before", at(8, 59, time.Monday), false},
		{"the weekend", at(10, 0, time.Saturday), false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			v := s.Evaluate(tc.when, Machine{})
			if v.Allow != tc.allow {
				t.Fatalf("verdict = %+v, want allow=%t", v, tc.allow)
			}
		})
	}
	if v := s.Evaluate(at(10, 0, time.Saturday), Machine{}); !strings.Contains(v.Detail, "It resumes") {
		t.Errorf("a refusal must say when work resumes: %+v", v)
	}
}

// A window whose end is before its start crosses midnight and belongs to the
// day it starts on.
func TestAWindowMayCrossMidnight(t *testing.T) {
	s := schedAt(t, ModeAlways, []Window{{Days: []string{"mon"}, Start: "23:00", End: "06:00"}}, BatteryAllow)
	cases := []struct {
		name  string
		when  time.Time
		allow bool
	}{
		{"monday night", at(23, 30, time.Monday), true},
		{"tuesday small hours", at(2, 0, time.Tuesday), true},
		{"tuesday morning", at(6, 0, time.Tuesday), false},
		{"tuesday night", at(23, 30, time.Tuesday), false},
		{"monday evening", at(22, 59, time.Monday), false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			v := s.Evaluate(tc.when, Machine{})
			if v.Allow != tc.allow {
				t.Fatalf("verdict = %+v, want allow=%t", v, tc.allow)
			}
		})
	}
}

// Validate refuses a schedule it cannot evaluate; Evaluate must still allow
// work if one reaches it anyway. Both halves matter: the first is what a user
// sees at startup, the second is what a machine sees when the first was
// bypassed.
func TestAnUnknownModeIsRefusedAtLoadAndAllowedAtRunTime(t *testing.T) {
	bad := Schedule{Mode: Mode("only-when-bored")}
	bad.ApplyDefaults()
	if err := bad.Validate(); err == nil {
		t.Fatal("Validate accepted a mode this build does not know")
	}
	v := bad.Evaluate(at(10, 0, time.Monday), Machine{})
	if !v.Allow || !v.Degraded {
		t.Fatalf("an unknown mode must not stop the machine: %+v", v)
	}
}

func TestAnUnusableWindowDoesNotDeny(t *testing.T) {
	s := Schedule{Mode: ModeAlways, Windows: []Window{{Start: "not-a-time", End: "18:00"}}}
	v := s.Evaluate(at(10, 0, time.Monday), Machine{})
	if !v.Allow || !v.Degraded {
		t.Fatalf("verdict = %+v, want a degraded allow", v)
	}
}

func TestValidateRejectsWhatItCannotEvaluate(t *testing.T) {
	cases := []struct {
		name string
		s    Schedule
	}{
		{"unknown mode", Schedule{Mode: Mode("someday")}},
		{"windows mode with no windows", Schedule{Mode: ModeWindows}},
		{"unparsable start", Schedule{Mode: ModeAlways, Windows: []Window{{Start: "9am", End: "10:00"}}}},
		{"impossible hour", Schedule{Mode: ModeAlways, Windows: []Window{{Start: "25:00", End: "10:00"}}}},
		{"unknown day", Schedule{Mode: ModeAlways, Windows: []Window{{Days: []string{"funday"}, Start: "09:00", End: "10:00"}}}},
		{"unknown battery policy", Schedule{Mode: ModeAlways, OnBattery: "maybe"}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			s := tc.s
			s.ApplyDefaults()
			if tc.name == "windows mode with no windows" {
				s.Windows = nil
				s.Mode = ModeWindows
			}
			if err := s.Validate(); err == nil {
				t.Fatalf("Validate accepted %+v", s)
			}
		})
	}
}

func TestModeAliasesAreAccepted(t *testing.T) {
	for written, want := range map[string]Mode{
		"only-when-idle":   ModeIdle,
		"Only-On-AC-Power": ModeAC,
		"window":           ModeWindows,
		"":                 ModeAlways,
	} {
		s := Schedule{Mode: Mode(written)}
		if want == ModeWindows {
			// A windows-mode schedule without windows is refused by design.
			s.Windows = []Window{{Start: "09:00", End: "18:00"}}
		}
		s.ApplyDefaults()
		if s.Mode != want {
			t.Errorf("mode %q normalized to %q, want %q", written, s.Mode, want)
		}
		if err := s.Validate(); err != nil {
			t.Errorf("mode %q: %v", written, err)
		}
	}
}

func TestDefaultScheduleNeedsNoMachineState(t *testing.T) {
	var s Schedule
	s.ApplyDefaults()
	if !s.IsDefault() {
		t.Fatalf("an unconfigured schedule must be the default: %+v", s)
	}
	if s.NeedsIdle() || s.NeedsPower() {
		t.Error("the default schedule must not make the governor read the machine")
	}
}

func TestNextChange(t *testing.T) {
	s := schedAt(t, ModeAlways, []Window{{Start: "09:00", End: "18:00"}}, BatteryAllow)
	next, ok := s.NextChange(at(10, 0, time.Monday))
	if !ok {
		t.Fatal("a windowed schedule must know when it changes")
	}
	if next.Hour() != 18 || next.Minute() != 0 {
		t.Fatalf("next change = %s, want 18:00", next)
	}
	next, ok = s.NextChange(at(20, 0, time.Monday))
	if !ok || next.Hour() != 9 {
		t.Fatalf("next change = %s (%t), want the next morning", next, ok)
	}
	var none Schedule
	none.ApplyDefaults()
	if _, ok := none.NextChange(at(10, 0, time.Monday)); ok {
		t.Fatal("a schedule with no windows has no next change")
	}
}

func TestDescribeIsAPlainSentence(t *testing.T) {
	cases := map[string]Schedule{
		"Works any time.": {},
		"Works when you've been away for 1 minute.":                            {Mode: ModeIdle, IdleMinutes: 1},
		"Works only when plugged in.":                                          {Mode: ModeAC, OnBattery: BatteryPause},
		"Works any time, every day 22:00-06:00. Pauses on battery.":            {Windows: []Window{{Start: "22:00", End: "06:00"}}, OnBattery: BatteryPause},
		"Works only at the hours you chose, Mon, Wed 09:00-17:00.":             {Mode: ModeWindows, Windows: []Window{{Days: []string{"mon", "WEDNESDAY"}, Start: "09:00", End: "17:00"}}},
		"Works only at the hours you chose, Sat 10:00-12:00; Sun 10:00-12:00.": {Mode: ModeWindows, Windows: []Window{{Days: []string{"sat"}, Start: "10:00", End: "12:00"}, {Days: []string{"sun"}, Start: "10:00", End: "12:00"}}},
	}
	for want, s := range cases {
		if got := s.Describe(); got != want {
			t.Errorf("Describe(%+v) = %q, want %q", s, got, want)
		}
	}
}

func TestWhenPhrase(t *testing.T) {
	now := time.Date(2026, 10, 8, 7, 0, 0, 0, time.Local) // a Thursday
	cases := map[string]time.Time{
		"at 08:00":             now.Add(time.Hour),
		"tomorrow at 08:00":    now.Add(25 * time.Hour),
		"on Saturday at 08:00": now.Add(49 * time.Hour),
	}
	for want, next := range cases {
		if got := whenPhrase(now, next); got != want {
			t.Errorf("whenPhrase(%s) = %q, want %q", next, got, want)
		}
	}
}
