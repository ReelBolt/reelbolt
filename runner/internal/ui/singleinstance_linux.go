package ui

import (
	"errors"
	"os"
	"syscall"
)

// lockFile takes an exclusive, non-blocking flock on the open file.
//
// flock, not a pid file: the kernel drops it when the process ends, so a crash
// cannot leave the lock held. LOCK_NB is what turns "someone else has it" into
// an answer instead of a hang.
func lockFile(f *os.File) (bool, error) {
	err := syscall.Flock(int(f.Fd()), syscall.LOCK_EX|syscall.LOCK_NB)
	switch {
	case err == nil:
		return true, nil
	case errors.Is(err, syscall.EWOULDBLOCK):
		return false, nil
	default:
		return false, err
	}
}

// unlockFile releases the flock. The close that follows drops it anyway; this is
// belt to the pair of braces.
func unlockFile(f *os.File) { _ = syscall.Flock(int(f.Fd()), syscall.LOCK_UN) }
