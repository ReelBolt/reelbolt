//go:build windows

package governor

import (
	"syscall"
	"time"
	"unsafe"
)

// The two Windows readings the plan names: GetLastInputInfo for input idleness
// and GetSystemPowerStatus for the power source. Both are plain user32/kernel32
// calls, so no cgo and no new dependency.
var (
	user32                   = syscall.NewLazyDLL("user32.dll")
	procGetLastInputInfo     = user32.NewProc("GetLastInputInfo")
	kernel32                 = syscall.NewLazyDLL("kernel32.dll")
	procGetTickCount64       = kernel32.NewProc("GetTickCount64")
	procGetSystemPowerStatus = kernel32.NewProc("GetSystemPowerStatus")
)

// lastInputInfo mirrors LASTINPUTINFO.
type lastInputInfo struct {
	cbSize uint32
	dwTime uint32
}

// systemPowerStatus mirrors SYSTEM_POWER_STATUS.
type systemPowerStatus struct {
	ACLineStatus        byte
	BatteryFlag         byte
	BatteryLifePercent  byte
	SystemStatusFlag    byte
	BatteryLifeTime     uint32
	BatteryFullLifeTime uint32
}

// readIdleFor returns the time since the last input event, of any kind, in any
// session the calling process can see.
//
// dwTime is a 32-bit tick count while GetTickCount64 is 64-bit, so the
// subtraction is done in 32-bit arithmetic: that is exact modulo the tick
// wrap, and the alternative (comparing the two widths) reports a negative idle
// time once every 49 days.
func readIdleFor() (time.Duration, bool) {
	var lii lastInputInfo
	lii.cbSize = uint32(unsafe.Sizeof(lii))
	if r, _, _ := procGetLastInputInfo.Call(uintptr(unsafe.Pointer(&lii))); r == 0 {
		return 0, false
	}
	ticks, _, _ := procGetTickCount64.Call()
	delta := uint32(uint64(ticks)) - lii.dwTime
	return time.Duration(delta) * time.Millisecond, true
}

// readOnAC reports whether the machine is on mains power. ACLineStatus 255 is
// "unknown", which is a degraded gate rather than a guess.
func readOnAC() (bool, bool) {
	var s systemPowerStatus
	if r, _, _ := procGetSystemPowerStatus.Call(uintptr(unsafe.Pointer(&s))); r == 0 {
		return false, false
	}
	switch s.ACLineStatus {
	case 0:
		return false, true
	case 1:
		return true, true
	}
	return false, false
}

func describeSignals() string {
	return "Checks whether you're using this computer and whether it's plugged in."
}
