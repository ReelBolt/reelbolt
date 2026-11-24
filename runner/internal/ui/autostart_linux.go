package ui

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// The XDG autostart entry.
//
// A .desktop file in $XDG_CONFIG_HOME/autostart is the freedesktop convention
// every GNOME, KDE and XFCE session honours without a helper, a daemon or root.
// It is a plain text file the user can read and delete, which is the property
// that matters: the Settings toggle and deleting that file are the same action,
// and a user who does not trust the toggle can always do the second.
const (
	desktopFileName     = "reelbolt-runner.desktop"
	desktopStartupClass = "ReelBoltRunner"
)

type linuxAutostart struct {
	// Dir overrides the autostart directory. Empty means the XDG one.
	Dir string
}

// LinuxAutostartFor builds a manager writing into dir, for a test.
func LinuxAutostartFor(dir string) Autostart { return linuxAutostart{Dir: dir} }

// PlatformAutostart returns this platform's login-item manager.
func PlatformAutostart() Autostart { return linuxAutostart{} }

func (l linuxAutostart) dir() (string, error) {
	if l.Dir != "" {
		return l.Dir, nil
	}
	base := os.Getenv("XDG_CONFIG_HOME")
	if base == "" {
		home, err := os.UserHomeDir()
		if err != nil {
			return "", fmt.Errorf("find the autostart directory: %w", err)
		}
		base = filepath.Join(home, ".config")
	}
	return filepath.Join(base, "autostart"), nil
}

func (l linuxAutostart) path() (string, error) {
	dir, err := l.dir()
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, desktopFileName), nil
}

// Describe names the file, which is what the Settings screen shows so a user
// can go and look at it.
func (l linuxAutostart) Describe() string {
	p, err := l.path()
	if err != nil {
		return "the XDG autostart directory"
	}
	return p
}

func (l linuxAutostart) Enabled() (bool, error) {
	p, err := l.path()
	if err != nil {
		return false, err
	}
	switch _, err := os.Stat(p); {
	case err == nil:
		return true, nil
	case errors.Is(err, os.ErrNotExist):
		return false, nil
	default:
		return false, err
	}
}

func (l linuxAutostart) Enable(command []string) error {
	if len(command) == 0 {
		return errors.New("nothing to run at login")
	}
	p, err := l.path()
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		return fmt.Errorf("create the autostart directory: %w", err)
	}
	exec := ""
	for i, a := range command {
		if i > 0 {
			exec += " "
		}
		exec += DesktopEntryQuote(a)
	}
	body := strings.Join([]string{
		"[Desktop Entry]",
		"Type=Application",
		"Name=ReelBolt Runner",
		"Comment=Run ReelBolt video jobs on this machine",
		"Exec=" + exec,
		"Terminal=false",
		"X-GNOME-Autostart-enabled=true",
		"StartupNotify=false",
		"StartupWMClass=" + desktopStartupClass,
		"",
	}, "\n")
	return writeFileAtomic(p, []byte(body), 0o644)
}

func (l linuxAutostart) Disable() error {
	p, err := l.path()
	if err != nil {
		return err
	}
	if err := os.Remove(p); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	return nil
}

// DesktopEntryQuote applies the Desktop Entry specification's reserved
// character rule: a value containing a space or a reserved character is quoted,
// and a backslash inside it is doubled. A path with a space in it is the normal
// case on a Linux desktop, so this is not theoretical.
func DesktopEntryQuote(s string) string {
	if !strings.ContainsAny(s, " \t\n\"'\\><~|&;$*?#()`") {
		return s
	}
	s = strings.ReplaceAll(s, "\\", "\\\\")
	s = strings.ReplaceAll(s, "\"", "\\\"")
	return "\"" + s + "\""
}
