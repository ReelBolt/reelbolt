//go:build !linux && !windows

package ui

import (
	"fmt"
	"runtime"
)

// unsupportedAutostart refuses with a reason, which is what D26 asks for: say
// macOS is out of scope rather than half-building it.
type unsupportedAutostart struct{ goos string }

func (u unsupportedAutostart) Enabled() (bool, error) {
	return false, fmt.Errorf("%w (%s)", ErrAutostartUnsupported, u.goos)
}

func (u unsupportedAutostart) Enable([]string) error {
	return fmt.Errorf("%w (%s)", ErrAutostartUnsupported, u.goos)
}

func (u unsupportedAutostart) Disable() error {
	return fmt.Errorf("%w (%s)", ErrAutostartUnsupported, u.goos)
}

func (u unsupportedAutostart) Describe() string { return "not available on " + u.goos }

// PlatformAutostart refuses off Windows and Linux.
//
// D26 ships no macOS runner, so there is no login item to write there.
// Refusing beats writing a LaunchAgent that starts a binary which does not
// exist: the failure would be silent and the user would believe the runner was
// set to start.
func PlatformAutostart() Autostart { return unsupportedAutostart{runtime.GOOS} }
