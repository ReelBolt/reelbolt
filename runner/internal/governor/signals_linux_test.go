//go:build linux

package governor

import (
	"os"
	"path/filepath"
	"testing"
)

// NL is one newline: the kernel's sysfs attribute files end with one, and the
// reader is expected to tolerate it.
const NL = "\n"

// fixtureTree writes a /sys/class/power_supply-shaped directory. The real one
// cannot be written to, and a unit test must not depend on whether the machine
// running it happens to be a laptop.
func fixtureTree(t *testing.T, files map[string]string) string {
	t.Helper()
	root := t.TempDir()
	for name, body := range files {
		path := filepath.Join(root, name)
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	return root
}

func TestOnACFromSysfs(t *testing.T) {
	cases := []struct {
		name  string
		files map[string]string
		onAC  bool
		known bool
	}{
		{
			name: "a laptop on mains",
			files: map[string]string{
				"AC/type":     "Mains" + NL,
				"AC/online":   "1" + NL,
				"BAT0/type":   "Battery" + NL,
				"BAT0/status": "Charging" + NL,
			},
			onAC: true, known: true,
		},
		{
			name: "a laptop on battery",
			files: map[string]string{
				"AC/type":     "Mains" + NL,
				"AC/online":   "0" + NL,
				"BAT0/type":   "Battery" + NL,
				"BAT0/status": "Discharging" + NL,
			},
			onAC: false, known: true,
		},
		{
			name: "a battery with no readable adapter",
			files: map[string]string{
				"BAT0/type":   "Battery" + NL,
				"BAT0/status": "Discharging" + NL,
			},
			onAC: false, known: true,
		},
		{
			// A desktop: no supply at all is not "unknown", it is a machine
			// that cannot be on battery.
			name:  "a desktop",
			files: map[string]string{},
			onAC:  true, known: true,
		},
		{
			// Entries we cannot make sense of are unknown: this is the case
			// where guessing either way could stop a real machine.
			name:  "an unrecognisable tree",
			files: map[string]string{"README": "nothing here" + NL},
			onAC:  false, known: false,
		},
		{
			name: "an adapter whose online flag cannot be read",
			files: map[string]string{
				"AC/type":   "Mains" + NL,
				"AC/online": "garbage" + NL,
			},
			onAC: false, known: false,
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			root := fixtureTree(t, tc.files)
			onAC, known := onACFromFS(root)
			if known != tc.known || (known && onAC != tc.onAC) {
				t.Fatalf("onACFromFS = %t, %t; want %t, %t", onAC, known, tc.onAC, tc.known)
			}
		})
	}
}

func TestAnUnreadablePowerTreeIsUnknown(t *testing.T) {
	if _, known := onACFromFS(filepath.Join(t.TempDir(), "does-not-exist")); known {
		t.Fatal("a tree that cannot be read must be unknown, never a guess")
	}
}

// The real reader must survive whatever this machine is, including a container
// with no /sys and a session bus that is not there: every failure is "unknown",
// which degrades the schedule gate rather than stopping the runner.
func TestTheRealSignalsNeverPanic(t *testing.T) {
	var s SystemSignals
	_, _ = s.IdleFor()
	_, _ = s.OnAC()
	if s.Describe() == "" {
		t.Error("Describe must say something")
	}
}
