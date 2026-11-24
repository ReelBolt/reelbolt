//go:build windows

package governor

import "syscall"

var (
	kernel32Priority     = syscall.NewLazyDLL("kernel32.dll")
	procOpenProcess      = kernel32Priority.NewProc("OpenProcess")
	procCloseHandle      = kernel32Priority.NewProc("CloseHandle")
	procSetPriorityClass = kernel32Priority.NewProc("SetPriorityClass")
)

// PROCESS_SET_INFORMATION is what SetPriorityClass needs; nothing else about
// the job's process handle is requested.
const processSetInformation = 0x0200

// Priority classes, from winbase.h.
const (
	normalPriorityClass      = 0x00000020
	belowNormalPriorityClass = 0x00004000
	idlePriorityClass        = 0x00000040
)

// LowerPriority gives a host process this runner's priority class. It is the
// Windows shape of the same idea as nice: a render must not make the desktop
// unusable.
func LowerPriority(pid int, p Priority) error {
	var class uintptr
	switch p {
	case PriorityLow:
		class = idlePriorityClass
	case PriorityBelowNormal:
		class = belowNormalPriorityClass
	default:
		return nil
	}
	handle, _, err := procOpenProcess.Call(processSetInformation, 0, uintptr(uint32(pid)))
	if handle == 0 {
		return err
	}
	defer procCloseHandle.Call(handle)
	if r, _, callErr := procSetPriorityClass.Call(handle, class); r == 0 {
		return callErr
	}
	return nil
}
