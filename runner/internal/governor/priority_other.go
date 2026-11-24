//go:build !unix && !windows

package governor

// LowerPriority does nothing here. D26 scopes the runner to Windows and Linux;
// anywhere else a job runs at the default priority, which is a missing
// optimisation and never a failure.
func LowerPriority(pid int, p Priority) error { return nil }
