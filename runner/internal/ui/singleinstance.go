package ui

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

// ErrAlreadyRunning is returned when another desktop shell holds the lock.
var ErrAlreadyRunning = errors.New("the ReelBolt desktop shell is already running")

// InstanceLock is the single-instance lock.
//
// Two shells on one machine would fight over one login item, one tray icon and
// one runner, and the second one would silently lose the race. So the lock is
// taken before anything else and held for the life of the process.
//
// It is an OS file lock, not the existence of a file. That distinction is the
// whole design: a lock file that merely exists survives a crash and locks the
// user out of their own shell for ever, while a flock is released by the
// kernel the instant the process dies, however it dies. The pid inside the
// file is only there so a person can see WHO holds it.
type InstanceLock struct {
	f    *os.File
	path string
}

// TakeInstanceLock takes the lock at path, or reports the holder.
func TakeInstanceLock(path string) (*InstanceLock, error) {
	if path == "" {
		return nil, errors.New("ui: no lock path")
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return nil, fmt.Errorf("create the lock directory: %w", err)
	}
	f, err := os.OpenFile(path, os.O_CREATE|os.O_RDWR, 0o600)
	if err != nil {
		return nil, fmt.Errorf("open the lock file: %w", err)
	}
	ok, err := lockFile(f)
	if err != nil {
		_ = f.Close()
		return nil, fmt.Errorf("lock %s: %w", path, err)
	}
	if !ok {
		_ = f.Close()
		return nil, fmt.Errorf("%w (lock %s held by pid %s)", ErrAlreadyRunning, path, readHolder(path))
	}
	// The pid is diagnostic only: nothing decides anything from it, because a
	// stale pid in a reused slot is exactly the bug a file lock exists to avoid.
	_ = f.Truncate(0)
	_, _ = f.WriteAt([]byte(strconv.Itoa(os.Getpid())+"\n"), 0)
	_ = f.Sync()
	return &InstanceLock{f: f, path: path}, nil
}

// Path is where the lock lives.
func (l *InstanceLock) Path() string {
	if l == nil {
		return ""
	}
	return l.path
}

// Holder is the pid recorded in the file, or 0.
func (l *InstanceLock) Holder() int {
	if l == nil {
		return 0
	}
	n, _ := strconv.Atoi(strings.TrimSpace(readHolder(l.path)))
	return n
}

// Release drops the lock. It is safe to call twice and safe on a nil lock.
func (l *InstanceLock) Release() {
	if l == nil || l.f == nil {
		return
	}
	unlockFile(l.f)
	_ = l.f.Close()
	l.f = nil
	// Removing the file is safe only because the lock is held exclusively: no
	// other process can be locked on it. Leaving it behind would be harmless
	// too, but a stale file confuses whoever goes looking.
	_ = os.Remove(l.path)
}

func readHolder(path string) string {
	raw, err := os.ReadFile(path)
	if err != nil {
		return "unknown"
	}
	s := strings.TrimSpace(string(raw))
	if s == "" {
		return "unknown"
	}
	return s
}
