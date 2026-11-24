//go:build !linux && !windows

package ui

import (
	"errors"
	"os"
	"runtime"
)

// lockFile is not implemented off Windows and Linux.
//
// D26 ships no macOS runner, so there is nothing to lock there. Refusing beats
// falling back to a lock that does not lock: a desktop shell that silently runs
// twice is worse than one that says why it will not start.
func lockFile(*os.File) (bool, error) {
	return false, errors.New("the ReelBolt desktop app runs on Windows and Linux only (decision D26); there is no lock on " + runtime.GOOS)
}

func unlockFile(*os.File) {}
