package ui

import (
	"errors"
	"os"

	"golang.org/x/sys/windows"
)

// lockOffset is where the one locked byte sits: far past the pid text. A Windows
// byte-range lock is mandatory, so locking byte 0 would make the pid unreadable
// to every other handle (the second shell's "held by pid N" message, and this
// process's own Holder()). Locking a byte beyond EOF is allowed and excludes
// other lockers all the same.
const lockOffset = 1 << 30

// lockFile takes an exclusive, non-blocking lock on the open file.
//
// LockFileEx with LOCKFILE_FAIL_IMMEDIATELY is the Windows equivalent of
// LOCK_EX|LOCK_NB, and it has the same property that makes this design work:
// the lock belongs to the HANDLE, so the kernel releases it when the process
// ends and a crash cannot leave the shell locked out. A pid file would.
//
// It goes through golang.org/x/sys/windows rather than the standard syscall
// package because syscall does not export LockFileEx at all - the raw kernel32
// call is reachable from there only by going through a LazyDLL, and x/sys is
// already a dependency of this module (the device key store uses it).
func lockFile(f *os.File) (bool, error) {
	ov := windows.Overlapped{Offset: lockOffset}
	err := windows.LockFileEx(windows.Handle(f.Fd()),
		windows.LOCKFILE_EXCLUSIVE_LOCK|windows.LOCKFILE_FAIL_IMMEDIATELY,
		0, 1, 0, &ov)
	switch {
	case err == nil:
		return true, nil
	case errors.Is(err, windows.ERROR_LOCK_VIOLATION):
		// Someone else holds it. That is an ANSWER, not a failure.
		return false, nil
	default:
		return false, err
	}
}

// unlockFile releases the lock. Closing the handle does this too; the explicit
// unlock keeps the pair symmetrical with the Unix build.
func unlockFile(f *os.File) {
	ov := windows.Overlapped{Offset: lockOffset}
	_ = windows.UnlockFileEx(windows.Handle(f.Fd()), 0, 1, 0, &ov)
}
