//go:build unix

package governor

import "syscall"

// LowerPriority gives a host process this runner's priority.
//
// The call happens after the process starts, so the first few milliseconds of
// an encode run at the default priority. That is deliberate: the alternative is
// wrapping every command in "nice", which spawns a second process per encode
// and does not exist on Windows at all.
func LowerPriority(pid int, p Priority) error {
	if pid <= 0 || p == PriorityNormal || p == "" {
		return nil
	}
	return syscall.Setpriority(syscall.PRIO_PROCESS, pid, p.UnixNice())
}
