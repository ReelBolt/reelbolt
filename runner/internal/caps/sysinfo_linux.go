//go:build linux

package caps

import (
	"bufio"
	"os"
	"strconv"
	"strings"
	"syscall"
)

// SystemFacts returns total memory and free disk space at scratchDir.
func SystemFacts(scratchDir string) (memTotal, diskFree uint64) {
	if f, err := os.Open("/proc/meminfo"); err == nil {
		defer func() { _ = f.Close() }()
		sc := bufio.NewScanner(f)
		for sc.Scan() {
			if rest, ok := strings.CutPrefix(sc.Text(), "MemTotal:"); ok {
				fields := strings.Fields(rest)
				if len(fields) > 0 {
					if kb, err := strconv.ParseUint(fields[0], 10, 64); err == nil {
						memTotal = kb * 1024
					}
				}
				break
			}
		}
	}
	var st syscall.Statfs_t
	// Bsize is signed on some platforms; a non-positive block size is not a
	// usable free-space figure, so the conversion only runs on a positive value.
	if err := syscall.Statfs(scratchDir, &st); err == nil && st.Bsize > 0 {
		diskFree = st.Bavail * uint64(st.Bsize)
	}
	return memTotal, diskFree
}
