//go:build !linux && !windows

package governor

import "time"

// readIdleFor is unavailable here. D26 scopes the runner to Windows and Linux;
// anything else reports "unknown" for both signals, which degrades every
// schedule gate to "allowed" rather than inventing an answer.
func readIdleFor() (time.Duration, bool) { return 0, false }

// readOnAC is unavailable here.
func readOnAC() (bool, bool) { return false, false }

func describeSignals() string {
	return "Can't check whether you're using this computer or whether it's plugged in."
}
