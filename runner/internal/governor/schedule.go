package governor

import (
	"fmt"
	"strings"
	"time"
)

// Mode is when a runner is willing to take work.
//
// The names are what a user writes in runner.json. D26 dropped macOS, so there
// is no macOS-only mode here; the two modes that need the machine's state are
// implemented for Windows x64 and Linux x64/arm64.
type Mode string

const (
	// ModeAlways takes work whenever the machine is on and unpaused.
	ModeAlways Mode = "always"
	// ModeIdle takes work only after the user has been away for IdleMinutes.
	// On Linux this reads logind's idle hint, which a desktop sets when the
	// session goes idle — it is a LOWER bound on input idleness, and the mode is
	// documented as best-effort there.
	ModeIdle Mode = "idle"
	// ModeAC takes work only while the machine is on mains power.
	ModeAC Mode = "ac"
	// ModeWindows takes work only inside the configured time windows.
	ModeWindows Mode = "windows"
)

// modeAliases lets a user write the name they expect. An unknown name is
// refused by Validate (loudly, at load time) and treated as ModeAlways by
// Evaluate (quietly, at run time) — see the package invariant.
var modeAliases = map[string]Mode{
	"always":           ModeAlways,
	"any":              ModeAlways,
	"idle":             ModeIdle,
	"only-when-idle":   ModeIdle,
	"when-idle":        ModeIdle,
	"ac":               ModeAC,
	"power":            ModeAC,
	"only-on-ac-power": ModeAC,
	"only-on-ac":       ModeAC,
	"windows":          ModeWindows,
	"window":           ModeWindows,
	"time-windows":     ModeWindows,
	"schedule":         ModeWindows,
}

// Battery policy values.
const (
	// BatteryAllow keeps working on battery. It is the default: a runner that
	// pauses on battery without being asked strands its workspace's queue.
	BatteryAllow = "allow"
	// BatteryPause stops taking work while the machine is on battery.
	BatteryPause = "pause"
)

// DefaultIdleMinutes is how long the user must be away before an idle-mode
// runner starts. Five minutes is long enough that reading a document does not
// start a render and short enough that a coffee break does.
const DefaultIdleMinutes = 5

// Window is one period work is allowed in.
//
// Days are "sun".."sat" (three-letter or full, case-insensitive); an empty list
// means every day. Start and End are "HH:MM" in local time. An End earlier than
// its Start wraps past midnight and belongs to the day it STARTS on, so
// {days:[mon], start:"23:00", end:"06:00"} covers Monday night into Tuesday
// morning.
type Window struct {
	Days  []string `json:"days,omitempty"`
	Start string   `json:"start"`
	End   string   `json:"end"`
}

// Schedule is the [schedule] block of runner.json.
type Schedule struct {
	// Mode is the primary gate. Empty means always.
	Mode Mode `json:"mode,omitempty"`
	// IdleMinutes is how long the user must have been away in ModeIdle.
	IdleMinutes int `json:"idleMinutes,omitempty"`
	// Windows is an independent gate applied in EVERY mode, including always:
	// "always, but only between 09:00 and 18:00 on weekdays" is expressible
	// without a separate mode. ModeWindows additionally REQUIRES at least one.
	Windows []Window `json:"windows,omitempty"`
	// OnBattery is "allow" (default) or "pause", and applies in every mode.
	OnBattery string `json:"onBattery,omitempty"`
}

// ApplyDefaults fills in what a user left out. It never invents a restriction:
// an empty schedule means "always".
func (s *Schedule) ApplyDefaults() {
	s.Mode = normalizeMode(string(s.Mode))
	if s.IdleMinutes <= 0 {
		s.IdleMinutes = DefaultIdleMinutes
	}
	if s.OnBattery == "" {
		s.OnBattery = BatteryAllow
	}
	s.OnBattery = strings.ToLower(strings.TrimSpace(s.OnBattery))
}

// normalizeMode maps a written mode onto a known one. An unknown name becomes
// ModeAlways: the caller that wants a refusal asks Validate first.
func normalizeMode(raw string) Mode {
	name := strings.ToLower(strings.TrimSpace(raw))
	if name == "" {
		return ModeAlways
	}
	if m, ok := modeAliases[name]; ok {
		return m
	}
	return Mode(raw)
}

// IsKnown reports whether the mode is one this build evaluates.
func (m Mode) IsKnown() bool {
	switch m {
	case ModeAlways, ModeIdle, ModeAC, ModeWindows:
		return true
	}
	return false
}

// Modes lists the modes Validate accepts.
func Modes() []string {
	return []string{string(ModeAlways), string(ModeIdle), string(ModeAC), string(ModeWindows)}
}

// Validate rejects a schedule the governor must not act on. It is called on
// every config load, so an unknown mode is a startup refusal that names the
// field — never a silent pause.
func (s *Schedule) Validate() error {
	if !s.Mode.IsKnown() {
		return fmt.Errorf("mode %q is not one of %s", s.Mode, strings.Join(Modes(), ", "))
	}
	if s.IdleMinutes < 1 {
		return fmt.Errorf("idleMinutes must be at least 1, got %d", s.IdleMinutes)
	}
	if s.Mode == ModeWindows && len(s.Windows) == 0 {
		return fmt.Errorf("mode %q needs at least one entry in windows", ModeWindows)
	}
	switch s.OnBattery {
	case BatteryAllow, BatteryPause:
	default:
		return fmt.Errorf("onBattery must be %q or %q, got %q", BatteryAllow, BatteryPause, s.OnBattery)
	}
	for i, w := range s.Windows {
		if _, _, err := w.bounds(); err != nil {
			return fmt.Errorf("windows[%d]: %w", i, err)
		}
		for _, d := range w.Days {
			if _, ok := dayIndex(d); !ok {
				return fmt.Errorf("windows[%d]: %q is not a day name (sun..sat)", i, d)
			}
		}
	}
	return nil
}

// IsDefault reports whether the schedule restricts nothing, which is what
// makes it safe to skip reading the machine's idle and power state entirely.
func (s Schedule) IsDefault() bool {
	return s.Mode == ModeAlways && len(s.Windows) == 0 && s.OnBattery == BatteryAllow
}

// NeedsIdle reports whether the verdict depends on the user's idle time.
func (s Schedule) NeedsIdle() bool { return s.Mode == ModeIdle }

// NeedsPower reports whether the verdict depends on the power source.
func (s Schedule) NeedsPower() bool {
	return s.Mode == ModeAC || s.OnBattery == BatteryPause
}

// Describe is the schedule in one plain sentence. "status" prints it and the
// desktop window shows it, so it is written for the person who set it.
func (s Schedule) Describe() string {
	sched := s
	sched.ApplyDefaults()
	var line string
	switch sched.Mode {
	case ModeIdle:
		line = "Works when you've been away for " + minutesPhrase(sched.IdleMinutes)
	case ModeAC:
		line = "Works only when plugged in"
	case ModeWindows:
		line = "Works only at the hours you chose"
	default:
		line = "Works any time"
	}
	if len(sched.Windows) > 0 {
		ws := make([]string, 0, len(sched.Windows))
		for _, w := range sched.Windows {
			ws = append(ws, w.String())
		}
		line += ", " + strings.Join(ws, "; ")
	}
	line += "."
	if sched.OnBattery == BatteryPause && sched.Mode != ModeAC {
		line += " Pauses on battery."
	}
	return line
}

// String renders one window: "Mon, Tue 09:00-18:00", or "every day 22:00-06:00".
func (w Window) String() string {
	days := "every day"
	if len(w.Days) > 0 {
		names := make([]string, 0, len(w.Days))
		for _, d := range w.Days {
			names = append(names, shortDayName(d))
		}
		days = strings.Join(names, ", ")
	}
	return days + " " + w.Start + "-" + w.End
}

// shortDayName turns "mon" or "Monday" into "Mon".
func shortDayName(d string) string {
	n := strings.ToLower(strings.TrimSpace(d))
	if len(n) > 3 {
		n = n[:3]
	}
	if n == "" {
		return n
	}
	return strings.ToUpper(n[:1]) + n[1:]
}

// minutesPhrase is "1 minute" or "5 minutes".
func minutesPhrase(n int) string {
	if n == 1 {
		return "1 minute"
	}
	return fmt.Sprintf("%d minutes", n)
}

// whenPhrase is when the schedule next opens, as a person says it: "at 08:00"
// today, "tomorrow at 08:00", or "on Monday at 08:00".
func whenPhrase(now, next time.Time) string {
	clock := next.Format("15:04")
	y1, m1, d1 := now.Date()
	y2, m2, d2 := next.Date()
	switch {
	case y1 == y2 && m1 == m2 && d1 == d2:
		return "at " + clock
	case now.AddDate(0, 0, 1).Format("2006-01-02") == next.Format("2006-01-02"):
		return "tomorrow at " + clock
	default:
		return "on " + next.Format("Monday") + " at " + clock
	}
}

// bounds parses a window's start and end into minutes past local midnight.
func (w Window) bounds() (int, int, error) {
	start, err := ParseClock(w.Start)
	if err != nil {
		return 0, 0, fmt.Errorf("start %q: %w", w.Start, err)
	}
	end, err := ParseClock(w.End)
	if err != nil {
		return 0, 0, fmt.Errorf("end %q: %w", w.End, err)
	}
	return start, end, nil
}

// ParseClock reads "HH:MM" (or "H:MM") as minutes past midnight.
func ParseClock(raw string) (int, error) {
	s := strings.TrimSpace(raw)
	hh, mm, ok := strings.Cut(s, ":")
	if !ok {
		return 0, fmt.Errorf("%q is not HH:MM", raw)
	}
	h, err := atoiStrict(hh)
	if err != nil || h < 0 || h > 23 {
		return 0, fmt.Errorf("%q is not HH:MM", raw)
	}
	m, err := atoiStrict(mm)
	if err != nil || m < 0 || m > 59 {
		return 0, fmt.Errorf("%q is not HH:MM", raw)
	}
	return h*60 + m, nil
}

func atoiStrict(s string) (int, error) {
	if s == "" {
		return 0, fmt.Errorf("empty")
	}
	n := 0
	for _, c := range s {
		if c < '0' || c > '9' {
			return 0, fmt.Errorf("not a number")
		}
		n = n*10 + int(c-'0')
	}
	return n, nil
}

var weekdays = []string{"sun", "mon", "tue", "wed", "thu", "fri", "sat"}

// dayIndex maps a day name onto time.Weekday.
func dayIndex(name string) (time.Weekday, bool) {
	n := strings.ToLower(strings.TrimSpace(name))
	if n == "" {
		return 0, false
	}
	if len(n) > 3 {
		n = n[:3]
	}
	for i, d := range weekdays {
		if d == n {
			return time.Weekday(i), true
		}
	}
	return 0, false
}

// covers reports whether now falls inside the window, and whether the window
// was usable at all. An unusable window never denies (see the package
// invariant): Validate refuses one at load time, and evaluation must not turn a
// value the governor cannot read into a stopped machine.
func (w Window) covers(now time.Time) (covered, usable bool) {
	start, end, err := w.bounds()
	if err != nil {
		return false, false
	}
	for _, d := range w.Days {
		if _, ok := dayIndex(d); !ok {
			return false, false
		}
	}
	mins := now.Hour()*60 + now.Minute()
	switch {
	case start < end:
		return mins >= start && mins < end && w.coversDay(now.Weekday()), true
	case start > end:
		// Wraps past midnight; the tail belongs to the day after the start day.
		if mins >= start && w.coversDay(now.Weekday()) {
			return true, true
		}
		return mins < end && w.coversDay(now.AddDate(0, 0, -1).Weekday()), true
	default:
		// start == end reads as the whole day, which is what "00:00-00:00"
		// looks like it means.
		return w.coversDay(now.Weekday()), true
	}
}

func (w Window) coversDay(day time.Weekday) bool {
	if len(w.Days) == 0 {
		return true
	}
	for _, d := range w.Days {
		if got, ok := dayIndex(d); ok && got == day {
			return true
		}
	}
	return false
}

// Verdict is the governor's answer, and the reason for it.
type Verdict struct {
	// Allow is whether this machine may take a job right now.
	Allow bool `json:"allow"`
	// Reason is a stable machine-readable token: allowed, user-active,
	// on-battery, outside-window or unknown-mode.
	Reason string `json:"reason"`
	// Detail is the sentence a person reads.
	Detail string `json:"detail,omitempty"`
	// Degraded records that a gate was SKIPPED because the machine cannot
	// report what it needs. It is how "we allowed this because we could not
	// tell" stays visible in status instead of looking like a plain "allowed".
	Degraded bool `json:"degraded,omitempty"`
}

// Verdict reasons.
const (
	ReasonAllowed       = "allowed"
	ReasonUserActive    = "user-active"
	ReasonOnBattery     = "on-battery"
	ReasonOutsideWindow = "outside-window"
	ReasonUnknownMode   = "unknown-mode"
)

// Machine is the part of the machine's state the schedule reads.
type Machine struct {
	IdleFor    time.Duration
	IdleKnown  bool
	OnAC       bool
	PowerKnown bool
}

// Evaluate applies the schedule to one moment and one machine state.
//
// It is pure: no clock, no filesystem, no D-Bus. Every ordering decision is
// therefore a table test rather than a description.
func (s Schedule) Evaluate(now time.Time, m Machine) Verdict {
	mode := s.Mode
	degraded := false

	if !mode.IsKnown() {
		// A mode this build does not know is an unknown case, never a parse
		// error: keep working and say why.
		degraded = true
		mode = ModeAlways
	}

	switch mode {
	case ModeIdle:
		if !m.IdleKnown {
			degraded = true
			break
		}
		need := time.Duration(s.IdleMinutes) * time.Minute
		if m.IdleFor < need {
			return Verdict{
				Allow:  false,
				Reason: ReasonUserActive,
				Detail: fmt.Sprintf("You're using this computer. It starts working once you've been away for %s.",
					minutesPhrase(s.IdleMinutes)),
			}
		}
	case ModeAC:
		if !m.PowerKnown {
			degraded = true
			break
		}
		if !m.OnAC {
			return Verdict{
				Allow:  false,
				Reason: ReasonOnBattery,
				Detail: "This computer is on battery. It starts working again when you plug it in.",
			}
		}
	}

	if s.OnBattery == BatteryPause {
		if !m.PowerKnown {
			degraded = true
		} else if !m.OnAC {
			return Verdict{
				Allow:  false,
				Reason: ReasonOnBattery,
				Detail: "This computer is on battery. It starts working again when you plug it in.",
			}
		}
	}

	if len(s.Windows) > 0 {
		inWindow, usable := inAnyWindow(s.Windows, now)
		if !usable {
			degraded = true
		} else if !inWindow {
			v := Verdict{
				Allow:  false,
				Reason: ReasonOutsideWindow,
				Detail: "It's outside the hours you chose for this computer to work.",
			}
			if next, ok := s.NextChange(now); ok {
				v.Detail += " It resumes " + whenPhrase(now, next) + "."
			}
			return v
		}
	}

	v := Verdict{Allow: true, Reason: ReasonAllowed, Degraded: degraded}
	switch {
	case degraded && mode == ModeIdle:
		v.Detail = "This computer can't tell when you're away, so it works whenever it's on."
	case degraded && mode == ModeAC:
		v.Detail = "This computer can't tell whether it's plugged in, so it works whenever it's on."
	case degraded:
		v.Detail = "Part of your schedule couldn't be checked on this computer, so it works whenever it's on."
	}
	return v
}

// inAnyWindow reports whether now is inside one of the windows, and whether the
// set was usable. A set with no usable entry is not a denial.
func inAnyWindow(windows []Window, now time.Time) (inside, usable bool) {
	usable = false
	for _, w := range windows {
		covered, ok := w.covers(now)
		if !ok {
			continue
		}
		usable = true
		if covered {
			return true, true
		}
	}
	return false, usable
}

// NextChange returns the next moment the verdict changes, looking forward at
// most eight days. It exists so "status" can tell a user when their machine
// will start working again instead of leaving them to work it out.
func (s Schedule) NextChange(now time.Time) (time.Time, bool) {
	if len(s.Windows) == 0 {
		return time.Time{}, false
	}
	inside, usable := inAnyWindow(s.Windows, now)
	if !usable {
		return time.Time{}, false
	}
	t := now.Truncate(time.Minute).Add(time.Minute)
	limit := now.Add(8 * 24 * time.Hour)
	for t.Before(limit) {
		next, ok := inAnyWindow(s.Windows, t)
		if ok && next != inside {
			return t, true
		}
		t = t.Add(time.Minute)
	}
	return time.Time{}, false
}
