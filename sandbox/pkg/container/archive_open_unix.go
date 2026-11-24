//go:build unix

package container

import (
	"os"
	"syscall"
)

// nonBlockingOpenFlag makes an open of a FIFO (or a device) return at once
// instead of waiting for a peer. Workspace paths are chosen by hostile code,
// and a blocking open would hang the control plane's goroutine forever.
const nonBlockingOpenFlag = syscall.O_NONBLOCK

// setBlocking clears O_NONBLOCK on a handle already verified to be a regular
// file or directory, so later reads behave normally.
func setBlocking(f *os.File) error {
	rc, err := f.SyscallConn()
	if err != nil {
		return err
	}
	var setErr error
	if err := rc.Control(func(fd uintptr) {
		setErr = syscall.SetNonblock(int(fd), false)
	}); err != nil {
		return err
	}
	return setErr
}
