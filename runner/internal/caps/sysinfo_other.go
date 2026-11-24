//go:build !linux && !windows

package caps

// SystemFacts is a compile-only stub on platforms the runner does not ship on
// (decision D26: no macOS runner for now). Zero means "unknown".
func SystemFacts(_ string) (memTotal, diskFree uint64) { return 0, 0 }
