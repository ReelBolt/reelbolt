package ui

import (
	"fmt"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/governor"
)

// TrayState is what the tray icon, its tooltip and the menu's first line say.
//
// The list is deliberately short and ordered by what matters to a person: a
// machine that cannot work at all outranks one that is merely idle, and a
// machine whose user paused it outranks one that is busy.
type TrayState string

const (
	// StateUnpaired means this machine has no device identity: it has never
	// been paired, or the gateway refused it and the daemon wiped the id. The
	// machine cannot take work and no amount of waiting changes that.
	StateUnpaired TrayState = "unpaired"
	// StateError means the runner cannot work for a reason that is not policy
	// and not a pause: no container runtime and no ffmpeg, an unreadable
	// configuration, or a runner that exited with an error.
	StateError TrayState = "error"
	// StateStopped means nothing is running on this machine: the shell is open
	// but its runner is not, and no other runner is either.
	StateStopped TrayState = "stopped"
	// StatePaused means a runner is running and choosing not to take work, and
	// the governor can say why.
	StatePaused TrayState = "paused"
	// StateWorking means at least one job is in flight.
	StateWorking TrayState = "working"
	// StateIdle means a runner is connected and willing, with nothing to do.
	StateIdle TrayState = "idle"
)

// States lists every state, in the order a person would rank them.
func States() []TrayState {
	return []TrayState{StateUnpaired, StateError, StateStopped, StatePaused, StateWorking, StateIdle}
}

// Known reports whether s is one of the states this build knows.
func (s TrayState) Known() bool {
	for _, k := range States() {
		if s == k {
			return true
		}
	}
	return false
}

// RemedyKind is what a person does about a state. It is a closed set so the UI
// cannot offer a button the shell has no action for: every kind maps to an
// Action in tray.go, and a test asserts that mapping.
type RemedyKind string

const (
	// RemedyNone means there is nothing to do, and the tray should not pretend
	// otherwise - an idle machine has no button-shaped problem.
	RemedyNone RemedyKind = "none"
	// RemedyPair means run the pairing flow.
	RemedyPair RemedyKind = "pair"
	// RemedyStart means start the runner.
	RemedyStart RemedyKind = "start"
	// RemedyResume means clear the pause. It is only ever offered when a hold
	// is actually in force, because governor.Resume clears a hold and nothing
	// else (see the long note on Resume in internal/governor/governor.go).
	RemedyResume RemedyKind = "resume"
	// RemedySchedule means the machine's own schedule is what is holding it.
	// The remedy is to change the schedule, never to "resume": a schedule gate
	// is not a hold, so Resume would do nothing and the button would be a lie.
	RemedySchedule RemedyKind = "schedule"
	// RemedyRuntime means the machine cannot run any job type: it needs a
	// container runtime (for Remotion) or ffmpeg.
	RemedyRuntime RemedyKind = "runtime"
	// RemedyUpdate means this build is older than the release its channel
	// offers, or below the version the gateway still supports.
	RemedyUpdate RemedyKind = "update"
)

// Remedy is the answer to "so what do I do?".
type Remedy struct {
	Kind RemedyKind `json:"kind"`
	// Label is the button, in the user's words.
	Label string `json:"label"`
	// Detail is one sentence of why, shown under the label and in the
	// tooltip.
	Detail string `json:"detail,omitempty"`
}

// RemedyAction is the action a remedy's button performs. It is a function so
// the mapping lives in one place and a test can walk every kind.
func RemedyAction(kind RemedyKind) string {
	switch kind {
	case RemedyPair:
		return ActionPair
	case RemedyStart:
		return ActionStartRunner
	case RemedyResume:
		return ActionResume
	case RemedySchedule:
		// The schedule is edited on the Settings screen, so the remedy goes
		// there. It is never Resume: a schedule gate is not a hold.
		return ActionOpenSettings
	case RemedyRuntime:
		return ActionSetupRuntime
	case RemedyUpdate:
		return ActionCheckForUpdate
	default:
		return ""
	}
}

// verdictText is the headline and the reason, in the user's words.
type verdictText struct {
	Headline string
	Detail   string
}

// describeGovernor renders the governor's verdict without re-deriving it.
//
// governor.Status already carries the reason, and a second opinion computed
// here is exactly the second truth this package's doc comment promises not to
// have. All this does is pick the plain sentence for the reason that is
// already there. The governor's own Detail is written for "reelbolt-runner
// status" and the log, so it is used only for a reason this file does not know.
func describeGovernor(st governor.Status, sched governor.Schedule) verdictText {
	if st.Allowed {
		if st.Degraded {
			return verdictText{Headline: "Ready for work", Detail: degradedSentence(st.Mode)}
		}
		return verdictText{Headline: "Ready for work"}
	}
	// A hold is the governor's own vocabulary for "a person (or ReelBolt)
	// said stop", and it is the one case Resume releases.
	if st.Held {
		detail := st.HeldReason
		if detail == "" {
			detail = "This computer is paused."
		}
		if st.HeldBy == governor.SourceRemote {
			detail = "Someone paused this computer from ReelBolt."
		}
		return verdictText{Headline: "Paused", Detail: detail}
	}
	var detail string
	switch st.Reason {
	case governor.ReasonUserActive:
		minutes := sched.IdleMinutes
		if minutes <= 0 {
			minutes = governor.DefaultIdleMinutes
		}
		detail = fmt.Sprintf("You're using this computer. It starts working once you've been away for %s.", plural(minutes, "minute"))
	case governor.ReasonOnBattery:
		detail = "This computer is on battery. It starts working again when you plug it in."
	case governor.ReasonOutsideWindow:
		detail = "It's outside the hours you chose for this computer to work."
		if st.NextChange != "" {
			detail += " It starts again at " + localTime(st.NextChange) + "."
		}
		return verdictText{Headline: "Waiting for your schedule", Detail: detail}
	case governor.ReasonUnknownMode:
		detail = "The schedule in your settings isn't one this version understands. Choose it again in Settings."
	default:
		detail = st.Detail
		if detail == "" {
			detail = "This computer isn't taking work right now."
		}
	}
	if st.NextChange != "" {
		detail += " It starts again at " + localTime(st.NextChange) + "."
	}
	return verdictText{Headline: "Waiting for your schedule", Detail: detail}
}

// degradedSentence explains a schedule the governor could only half read: it
// is working, and this says why it is working anyway.
func degradedSentence(mode governor.Mode) string {
	switch mode {
	case governor.ModeIdle:
		return "This computer can't tell when you're away, so it works whenever it's on."
	case governor.ModeAC:
		return "This computer can't tell whether it's plugged in, so it works whenever it's on."
	default:
		return "Part of your schedule couldn't be checked on this computer, so it works whenever it's on."
	}
}

// describeSchedule is the schedule in one sentence, for the window. It is the
// governor's own description, so "status" and the window say the same thing.
func describeSchedule(s governor.Schedule) string { return s.Describe() }

// describeSignals says what the computer checks to follow the schedule, in
// plain words, or nothing when the schedule needs nothing checked.
func describeSignals(s governor.Schedule) string {
	sched := s
	sched.ApplyDefaults()
	switch {
	case sched.NeedsIdle() && sched.NeedsPower():
		return "Checks whether you're using this computer and whether it's plugged in."
	case sched.NeedsIdle():
		return "Checks whether you're using this computer."
	case sched.NeedsPower():
		return "Checks whether this computer is plugged in."
	default:
		return ""
	}
}

// plural renders "1 minute" / "5 minutes".
func plural(n int, unit string) string {
	if n == 1 {
		return "1 " + unit
	}
	return fmt.Sprintf("%d %ss", n, unit)
}

// localTime renders an RFC3339 stamp in local time, falling back to the raw
// string rather than dropping it: a stamp a person cannot read still beats a
// blank line where the answer was.
func localTime(rfc3339 string) string {
	t, err := time.Parse(time.RFC3339, rfc3339)
	if err != nil {
		return rfc3339
	}
	return t.Local().Format("15:04")
}
