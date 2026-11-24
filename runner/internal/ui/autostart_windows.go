package ui

import (
	"errors"
	"fmt"

	"golang.org/x/sys/windows/registry"
)

// The Windows login item: one value under HKCU\...\Run.
//
// HKCU, not HKLM, and no elevation: a per-user login item is the whole point of
// a per-user install, and E10b installs under %LOCALAPPDATA% precisely so that
// nobody needs an administrator to run this. The value is the binary plus
// --background, quoted, because the install path has spaces in it.
//
// The key is read from CURRENT_USER on every call rather than cached: a user who
// removes the entry with regedit should see the toggle go off, not a shell that
// insists it is still on.
const (
	runKeyPath   = "Software\\Microsoft\\Windows\\CurrentVersion\\Run"
	runValueName = "ReelBolt Runner"
)

type windowsAutostart struct {
	// Exe overrides the command, for a test that cannot call os.Executable
	// for the product binary.
	Exe string
}

// WindowsAutostartFor builds a manager for exe, for a test.
func WindowsAutostartFor(exe string) Autostart { return windowsAutostart{Exe: exe} }

// PlatformAutostart returns this platform's login-item manager.
func PlatformAutostart() Autostart { return windowsAutostart{} }

func (w windowsAutostart) Describe() string {
	return "HKCU\\" + runKeyPath
}

func (w windowsAutostart) Enabled() (bool, error) {
	k, err := registry.OpenKey(registry.CURRENT_USER, runKeyPath, registry.QUERY_VALUE)
	if err != nil {
		return false, fmt.Errorf("read the Windows Run key: %w", err)
	}
	defer func() { _ = k.Close() }()
	_, _, err = k.GetStringValue(runValueName)
	switch {
	case err == nil:
		return true, nil
	case errors.Is(err, registry.ErrNotExist):
		return false, nil
	default:
		return false, fmt.Errorf("read the Windows Run value: %w", err)
	}
}

func (w windowsAutostart) Enable(command []string) error {
	if len(command) == 0 {
		return errors.New("nothing to run at login")
	}
	k, err := registry.OpenKey(registry.CURRENT_USER, runKeyPath, registry.SET_VALUE)
	if err != nil {
		return fmt.Errorf("open the Windows Run key for writing: %w", err)
	}
	defer func() { _ = k.Close() }()
	if err := k.SetStringValue(runValueName, QuoteWindowsCommand(command)); err != nil {
		return fmt.Errorf("write the Windows Run value: %w", err)
	}
	return nil
}

func (w windowsAutostart) Disable() error {
	k, err := registry.OpenKey(registry.CURRENT_USER, runKeyPath, registry.SET_VALUE)
	if err != nil {
		if errors.Is(err, registry.ErrNotExist) {
			return nil
		}
		return fmt.Errorf("open the Windows Run key for writing: %w", err)
	}
	defer func() { _ = k.Close() }()
	if err := k.DeleteValue(runValueName); err != nil && !errors.Is(err, registry.ErrNotExist) {
		return fmt.Errorf("remove the Windows Run value: %w", err)
	}
	return nil
}
