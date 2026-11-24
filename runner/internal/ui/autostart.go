package ui

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// Autostart is the login item: "start the runner when I log in".
//
// It is OFF by default and offered once, at first run, because a desktop shell
// that installs itself into someone's login items without asking is the
// behaviour people uninstall software over. Disable must always work: a user
// who cannot turn it off has lost control of their own machine.
//
// The mechanisms are the plan's (HKCU\...\Run on Windows, an XDG autostart
// .desktop on Linux) and deliberately NOT a macOS LaunchAgent: D26 ships no
// macOS runner, so this refuses there rather than writing a login item that
// starts a binary that does not exist.
type Autostart interface {
	// Enabled reports whether the login item is present.
	Enabled() (bool, error)
	// Enable writes the login item, running the command at login.
	Enable(command []string) error
	// Disable removes it. It is not an error for it to be absent.
	Disable() error
	// Describe names the file or key, for the Settings screen.
	Describe() string
}

// ErrAutostartUnsupported is returned on a platform E6 does not ship for.
var ErrAutostartUnsupported = errors.New("autostart is not supported on this platform")

// PlatformAutostart returns this platform's manager. Each platform file
// defines it, so a build cannot reference a manager it does not have.

// AutostartCommand is the command a login item runs: this binary with
// --background, so logging in starts the runner and the tray without throwing a
// window in the user's face.
//
// It is built from the RUNNING executable rather than a configured path, so the
// entry follows the binary the user actually has - including a per-user install
// that E10b has not produced yet.
func AutostartCommand(exe string) ([]string, error) {
	if exe == "" {
		var err error
		exe, err = os.Executable()
		if err != nil {
			return nil, fmt.Errorf("find this program's own path: %w", err)
		}
	}
	if !filepath.IsAbs(exe) {
		abs, err := filepath.Abs(exe)
		if err != nil {
			return nil, err
		}
		exe = abs
	}
	if _, err := os.Stat(exe); err != nil {
		// A login item pointing at a deleted binary is worse than no login
		// item: the OS silently does nothing and the user believes the runner
		// is set to start.
		return nil, fmt.Errorf("the login item would point at %s, which is not there: %w", exe, err)
	}
	return []string{exe, "--background"}, nil
}

// QuoteWindowsCommand renders argv as a command line for a registry Run value.
// Every argument is quoted, which is what CreateProcess needs for a path with
// spaces in it - and "%LOCALAPPDATA%\Programs\ReelBolt Runner" has spaces.
func QuoteWindowsCommand(argv []string) string {
	parts := make([]string, 0, len(argv))
	for _, a := range argv {
		parts = append(parts, "\""+strings.ReplaceAll(a, "\"", "\\\"")+"\"")
	}
	return strings.Join(parts, " ")
}

// DescribeCommand is argv as a person would type it.
func DescribeCommand(argv []string) string {
	parts := make([]string, 0, len(argv))
	for _, a := range argv {
		if strings.ContainsAny(a, " \t") {
			parts = append(parts, "\""+a+"\"")
			continue
		}
		parts = append(parts, a)
	}
	return strings.Join(parts, " ")
}
