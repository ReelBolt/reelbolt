package telemetry

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
)

// The log the diagnostics export attaches, and the numbers the plan fixes.
//
// This file computes its own directory rather than asking package config, which
// would be a cycle: runner.json holds the telemetry switch, so config cannot
// import this package. The path is the one E5 established for the cache
// directory, with "logs" under it.
const (
	// DirName is the same directory name the configuration and cache use.
	DirName = "ReelBolt"
	// LogsDirName is the directory under the cache root.
	LogsDirName = "logs"
	// DefaultMaxLogBytes is how large one log file may grow before it rotates.
	DefaultMaxLogBytes int64 = 5 << 20
	// DefaultLogFiles is how many log files are kept, the current one included,
	// so the log costs at most 25 MB of the user's disk.
	DefaultLogFiles = 5
	// MaxLogEntryBytes caps one write, so a single enormous line cannot blow
	// past the rotation ceiling before the check runs.
	MaxLogEntryBytes = 1 << 20
)

// LogsDir returns <UserCacheDir>/ReelBolt/logs.
func LogsDir() (string, error) {
	base, err := os.UserCacheDir()
	if err != nil {
		return "", fmt.Errorf("telemetry: user cache directory: %w", err)
	}
	return filepath.Join(base, DirName, LogsDirName), nil
}

// RotatingWriter is an io.Writer that keeps a bounded number of bounded files.
//
// It is deliberately simple: no compression, no goroutine, no timers. A
// runner's log exists so that a failure which already happened can be read
// afterwards, and a rotation mechanism that itself needs diagnosing would
// defeat that.
type RotatingWriter struct {
	mu    sync.Mutex
	dir   string
	name  string
	max   int64
	files int

	f    *os.File
	size int64
}

// ListLogs returns every *.log file in a directory, oldest first by
// modification time.
//
// It exists because the export must attach the log that actually exists: the
// desktop host writes its own rotating file (desktop.log) and hands the shell a
// logger rather than a writer, so "the shell's writer" is not always the file
// to include. A directory that cannot be read yields nothing rather than an
// error: an export with no log is still worth having.
func ListLogs(dir string) []string {
	if dir == "" {
		return nil
	}
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil
	}
	type entry struct {
		path string
		mod  int64
	}
	var found []entry
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".log") {
			continue
		}
		info, err := e.Info()
		if err != nil {
			continue
		}
		found = append(found, entry{path: filepath.Join(dir, e.Name()), mod: info.ModTime().UnixNano()})
	}
	sort.Slice(found, func(i, j int) bool { return found[i].mod < found[j].mod })
	out := make([]string, 0, len(found))
	for _, e := range found {
		out = append(out, e.path)
	}
	return out
}

// OpenRotating opens (or creates) <dir>/<name>.log for appending. maxBytes and
// files of zero mean the defaults above.
func OpenRotating(dir, name string, maxBytes int64, files int) (*RotatingWriter, error) {
	if maxBytes <= 0 {
		maxBytes = DefaultMaxLogBytes
	}
	if files <= 0 {
		files = DefaultLogFiles
	}
	if files < 1 {
		files = 1
	}
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return nil, fmt.Errorf("telemetry: create %s: %w", dir, err)
	}
	w := &RotatingWriter{dir: dir, name: name, max: maxBytes, files: files}
	if err := w.open(); err != nil {
		return nil, err
	}
	return w, nil
}

// Path is the file new lines are appended to.
func (w *RotatingWriter) Path() string { return filepath.Join(w.dir, w.name+".log") }

// Files lists every log file this writer owns, oldest first. A file that
// vanished between the glob and the stat is skipped rather than reported: the
// diagnostics export must not fail because rotation ran under it.
func (w *RotatingWriter) Files() []string {
	entries, err := os.ReadDir(w.dir)
	if err != nil {
		return nil
	}
	type entry struct {
		index int
		path  string
	}
	var found []entry
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		if n, ok := w.logIndex(e.Name()); ok {
			found = append(found, entry{index: n, path: filepath.Join(w.dir, e.Name())})
		}
	}
	// Highest index first, so the file about to be deleted leads the bundle and
	// the live log closes it: a support engineer reads history, then the end.
	sort.Slice(found, func(i, j int) bool { return found[i].index > found[j].index })
	out := make([]string, 0, len(found))
	for _, e := range found {
		out = append(out, e.path)
	}
	return out
}

// logIndex reports the rotation index of a directory entry: 0 for the live
// file, 1..N for the rotated ones.
func (w *RotatingWriter) logIndex(name string) (int, bool) {
	if name == w.name+".log" {
		return 0, true
	}
	rest, ok := strings.CutPrefix(name, w.name+".")
	if !ok {
		return 0, false
	}
	rest, ok = strings.CutSuffix(rest, ".log")
	if !ok {
		return 0, false
	}
	n, err := strconv.Atoi(rest)
	if err != nil || n < 1 {
		return 0, false
	}
	return n, true
}

// Write appends, rotating first when the entry would cross the ceiling.
func (w *RotatingWriter) Write(p []byte) (int, error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.f == nil {
		return 0, errors.New("telemetry: the log file is closed")
	}
	if len(p) > MaxLogEntryBytes {
		p = p[:MaxLogEntryBytes]
	}
	if w.size+int64(len(p)) > w.max && w.size > 0 {
		if err := w.rotate(); err != nil {
			return 0, err
		}
	}
	n, err := w.f.Write(p)
	w.size += int64(n)
	return n, err
}

// Close closes the current file. It is idempotent.
func (w *RotatingWriter) Close() error {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.f == nil {
		return nil
	}
	err := w.f.Close()
	w.f = nil
	return err
}

// open opens the live file for appending and records its size.
func (w *RotatingWriter) open() error {
	f, err := os.OpenFile(w.Path(), os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		return fmt.Errorf("telemetry: open %s: %w", w.Path(), err)
	}
	info, err := f.Stat()
	if err != nil {
		_ = f.Close()
		return fmt.Errorf("telemetry: stat %s: %w", w.Path(), err)
	}
	w.f, w.size = f, info.Size()
	return nil
}

// rotate shifts name.N.log to name.N+1.log, drops the oldest, and starts a new
// live file.
func (w *RotatingWriter) rotate() error {
	if err := w.f.Close(); err != nil {
		// A close failure leaves a file descriptor this process cannot use; the
		// run continues with a new one rather than failing the job.
		w.f = nil
	}
	// The oldest file is the one at index files-1; anything at or beyond it is
	// removed so a crash loop cannot grow the directory without bound.
	for n := w.files - 1; n >= 1; n-- {
		from := w.path(n)
		if n == w.files-1 {
			_ = os.Remove(from)
			continue
		}
		if _, err := os.Stat(from); err != nil {
			continue
		}
		if err := os.Rename(from, w.path(n+1)); err != nil {
			_ = os.Remove(from)
		}
	}
	if _, err := os.Stat(w.Path()); err == nil {
		if err := os.Rename(w.Path(), w.path(1)); err != nil {
			_ = os.Remove(w.Path())
		}
	}
	return w.open()
}

func (w *RotatingWriter) path(n int) string {
	return filepath.Join(w.dir, fmt.Sprintf("%s.%d.log", w.name, n))
}
