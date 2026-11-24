//go:build linux

package ui

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// The XDG entry is exercised for real here: it is a plain file, so a test can
// write it, read it back and assert what a session would actually run. The
// Windows manager cannot be tested this way (it is a registry key), which is
// why it is compile- and vet-only, stated in TESTING.md.
func TestAutostartIsOffByDefaultAndWritesARealEntry(t *testing.T) {
	dir := t.TempDir()
	a := LinuxAutostartFor(filepath.Join(dir, "autostart"))
	on, err := a.Enabled()
	if err != nil {
		t.Fatalf("Enabled: %v", err)
	}
	if on {
		t.Fatal("a fresh install has a login item")
	}
	// A path with a space in it is the normal case on a Linux desktop.
	argv := []string{filepath.Join(dir, "my runner"), "--background"}
	if err := a.Enable(argv); err != nil {
		t.Fatalf("Enable: %v", err)
	}
	on, err = a.Enabled()
	if err != nil || !on {
		t.Fatalf("Enabled after Enable = (%v, %v)", on, err)
	}
	body, err := os.ReadFile(a.Describe())
	if err != nil {
		t.Fatalf("read the entry: %v", err)
	}
	text := string(body)
	for _, want := range []string{"[Desktop Entry]", "Type=Application", "Exec=", "--background"} {
		if !strings.Contains(text, want) {
			t.Errorf("the entry does not contain %q", want)
		}
	}
	if strings.Contains(text, "Exec="+argv[0]) {
		t.Errorf("the Exec line is unquoted, so a path with a space breaks it:\n%s", text)
	}
	if err := a.Disable(); err != nil {
		t.Fatalf("Disable: %v", err)
	}
	if on, _ := a.Enabled(); on {
		t.Fatal("Disable left the entry in place")
	}
	// Disabling what is already gone is not an error: the toggle may be
	// pressed twice.
	if err := a.Disable(); err != nil {
		t.Fatalf("second Disable: %v", err)
	}
}

func TestDesktopEntryQuote(t *testing.T) {
	quote := string(rune(34))
	if got := DesktopEntryQuote("/usr/bin/runner"); got != "/usr/bin/runner" {
		t.Errorf("a plain path was quoted: %q", got)
	}
	want := quote + "/opt/my runner/bin" + quote
	if got := DesktopEntryQuote("/opt/my runner/bin"); got != want {
		t.Errorf("a path with a space = %q, want %q", got, want)
	}
}

// TestSupportedPlatforms is the D26 check: this WP ships for Windows and Linux,
// and the shell says so rather than half-building macOS.
func TestSupportedOnLinux(t *testing.T) {
	if err := Supported(); err != nil {
		t.Fatalf("Supported() on linux = %v", err)
	}
}
