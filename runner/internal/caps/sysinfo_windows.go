//go:build windows

package caps

import (
	"syscall"
	"unsafe"
)

var (
	kernel32             = syscall.NewLazyDLL("kernel32.dll")
	procGlobalMemory     = kernel32.NewProc("GlobalMemoryStatusEx")
	procGetDiskFreeSpace = kernel32.NewProc("GetDiskFreeSpaceExW")
)

type memoryStatusEx struct {
	Length               uint32
	MemoryLoad           uint32
	TotalPhys            uint64
	AvailPhys            uint64
	TotalPageFile        uint64
	AvailPageFile        uint64
	TotalVirtual         uint64
	AvailVirtual         uint64
	AvailExtendedVirtual uint64
}

// SystemFacts returns total memory and free disk space at scratchDir.
func SystemFacts(scratchDir string) (memTotal, diskFree uint64) {
	ms := memoryStatusEx{Length: uint32(unsafe.Sizeof(memoryStatusEx{}))}
	if r, _, _ := procGlobalMemory.Call(uintptr(unsafe.Pointer(&ms))); r != 0 {
		memTotal = ms.TotalPhys
	}
	if p, err := syscall.UTF16PtrFromString(scratchDir); err == nil {
		var free, total, totalFree uint64
		r, _, _ := procGetDiskFreeSpace.Call(
			uintptr(unsafe.Pointer(p)),
			uintptr(unsafe.Pointer(&free)),
			uintptr(unsafe.Pointer(&total)),
			uintptr(unsafe.Pointer(&totalFree)),
		)
		if r != 0 {
			diskFree = free
		}
	}
	return memTotal, diskFree
}
