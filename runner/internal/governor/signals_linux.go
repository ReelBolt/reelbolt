//go:build linux

package governor

import (
	"os"
	"strings"
	"time"

	"github.com/godbus/dbus/v5"
)

// logind, over the SYSTEM bus. Read through the manager's own idle hint rather
// than a session's: "is anyone using this machine" is a property of the machine,
// and the manager is the object that always exists.
const (
	login1Service = "org.freedesktop.login1"
	login1Path    = dbus.ObjectPath("/org/freedesktop/login1")
	login1Manager = "org.freedesktop.login1.Manager"
)

// powerSupplyRoot is where the kernel reports power supplies. It is a variable
// so a test can point it at a fixture directory: /sys is not writable, and a
// unit test must not depend on the machine it happens to run on.
var powerSupplyRoot = "/sys/class/power_supply"

// loginSession is one entry of Manager.ListSessions, whose D-Bus signature is
// a(susso): id, uid, user, seat, object path.
type loginSession struct {
	ID       string
	UserID   uint32
	UserName string
	SeatID   string
	Path     dbus.ObjectPath
}

// readIdleFor reads logind's system idle hint.
//
// IdleSinceHint is a microsecond timestamp, and ZERO means the hint is OFF —
// that is, logind says somebody is at the machine. That reading is only
// trustworthy when a local session exists that could go idle in the first
// place, so a machine with no seat-bound session at all reports "unknown" and
// the gate degrades instead of stopping the runner for ever.
//
// This is the "best effort" the plan labels it as: a desktop session sets the
// hint (typically when the screen blanks), so it is a LOWER bound on input
// idleness. A machine whose session never sets it reports "unknown".
func readIdleFor() (time.Duration, bool) {
	conn, err := dbus.SystemBus()
	if err != nil {
		return 0, false
	}
	obj := conn.Object(login1Service, login1Path)
	v, err := obj.GetProperty(login1Manager + ".IdleSinceHint")
	if err != nil {
		return 0, false
	}
	micros, ok := v.Value().(uint64)
	if !ok {
		return 0, false
	}
	if micros == 0 {
		if !hasLocalSession(conn) {
			return 0, false
		}
		return 0, true
	}
	since := time.Unix(0, int64(micros)*int64(time.Microsecond))
	d := time.Since(since)
	if d < 0 {
		d = 0
	}
	return d, true
}

// hasLocalSession reports whether any logind session is bound to a seat, which
// is what distinguishes "the user is at the machine" from "nothing here tracks
// idleness".
func hasLocalSession(conn *dbus.Conn) bool {
	var sessions []loginSession
	if err := conn.Object(login1Service, login1Path).
		Call(login1Manager+".ListSessions", 0).Store(&sessions); err != nil {
		return false
	}
	for _, s := range sessions {
		if s.SeatID != "" {
			return true
		}
	}
	return false
}

// readOnAC reads the kernel's own mains-power state.
//
// /sys/class/power_supply holds one directory per supply. A "Mains" supply
// reports online; a machine with a battery and no readable mains adapter is
// judged by whether the battery is discharging. A machine with NEITHER is a
// desktop and is always on mains, which is a known answer rather than an
// unknown one.
func readOnAC() (bool, bool) { return onACFromFS(powerSupplyRoot) }

// onACFromFS is readOnAC with the sysfs root handed in, so a test can describe
// a laptop, a desktop and an unreadable tree without one.
func onACFromFS(root string) (bool, bool) {
	entries, err := os.ReadDir(root)
	if err != nil {
		return false, false
	}
	if len(entries) == 0 {
		// A machine with no power supply of any kind cannot be on battery.
		// This is the ordinary desktop and the ordinary container.
		return true, true
	}
	sawSupply := false
	sawBattery := false
	batteryDischarging := false
	for _, e := range entries {
		dir := root + "/" + e.Name()
		switch readTrimmed(dir + "/type") {
		case "Mains", "USB", "USB_C":
			online, ok := readOnline(dir + "/online")
			if !ok {
				continue
			}
			sawSupply = true
			if online {
				return true, true
			}
		case "Battery":
			sawSupply = true
			sawBattery = true
			if readTrimmed(dir+"/status") == "Discharging" {
				batteryDischarging = true
			}
		}
	}
	switch {
	case !sawSupply:
		// Nothing readable at all: do not guess about a machine we cannot see.
		return false, false
	case sawBattery:
		// A battery that is not discharging is charging or full; either way
		// the machine is plugged in.
		return !batteryDischarging, true
	default:
		// A mains supply that reported offline.
		return false, true
	}
}

func readOnline(path string) (bool, bool) {
	switch readTrimmed(path) {
	case "1":
		return true, true
	case "0":
		return false, true
	}
	return false, false
}

func readTrimmed(path string) string {
	raw, err := os.ReadFile(path)
	if err != nil {
		return ""
	}
	return strings.TrimSpace(string(raw))
}

func describeSignals() string {
	return "Checks whether you're using this computer and whether it's plugged in."
}
