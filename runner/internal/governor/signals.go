package governor

import "time"

// Signals is what the governor needs to know about the machine, and nothing
// else. Both methods report whether the answer is KNOWN: a platform that cannot
// tell is a degraded gate, never a closed one.
type Signals interface {
	// IdleFor reports how long the user's input has been idle. ok=false means
	// this machine cannot say.
	IdleFor() (d time.Duration, ok bool)
	// OnAC reports whether the machine is on mains power. ok=false means this
	// machine cannot say — a desktop with no battery reports ok=true.
	OnAC() (onAC bool, ok bool)
	// Describe names the source, for "status" and "doctor".
	Describe() string
}

// SystemSignals reads the real machine. It is the only part of the governor
// that touches the platform, and every failure in it degrades to "unknown".
type SystemSignals struct{}

// IdleFor implements Signals.
func (SystemSignals) IdleFor() (time.Duration, bool) { return readIdleFor() }

// OnAC implements Signals.
func (SystemSignals) OnAC() (bool, bool) { return readOnAC() }

// Describe implements Signals.
func (SystemSignals) Describe() string { return describeSignals() }
