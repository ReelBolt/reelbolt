package ui

import (
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
)

// cacheView measures what the runner owns on this machine.
//
// Two directories, and both are the daemon's own: job scratch (created per job,
// deleted when the job ends) and workspace directories (bind-mounted into the
// workspace container). Anything found in them at rest is a job that was killed,
// which is exactly what a user opens this screen to clear.
func (s *Shell) cacheView() CacheView {
	scratch := s.scratchRoot()
	workspaces := s.WorkspaceRoot()
	v := CacheView{ScratchRoot: scratch, WorkspaceRoot: workspaces}
	v.Media = s.mediaCacheView()
	v.Capped = v.Media != nil
	v.Note = "Leftover working files are from jobs that were interrupted, and are safe to clear. " +
		"The media cache keeps footage and music this computer downloaded, so the next video " +
		"that uses them starts sooner."
	if v.Media == nil {
		v.Note = "Leftover working files are from jobs that were interrupted, and are safe to clear. " +
			"The media cache is off, so footage is downloaded again for every job."
	}
	if scratch == "" {
		return v
	}
	var bytes int64
	var entries int
	failed := false
	for _, root := range []string{scratch, workspaces} {
		b, n, err := DirUsage(root)
		if err != nil {
			failed = true
			continue
		}
		bytes += b
		entries += n
	}
	v.Bytes, v.Jobs, v.Known = bytes, entries, !failed
	return v
}

// DirUsage sums the size of everything under root, and counts its top-level
// entries. A root that does not exist is zero, not an error: an empty cache is
// not a fault.
func DirUsage(root string) (int64, int, error) {
	if root == "" {
		return 0, 0, nil
	}
	if _, err := os.Stat(root); errors.Is(err, os.ErrNotExist) {
		return 0, 0, nil
	}
	var bytes int64
	entries := 0
	err := filepath.WalkDir(root, func(path string, d fs.DirEntry, err error) error {
		if err != nil {
			// A directory that vanished mid-walk (the daemon deleting a
			// finished job's scratch) is not a failure of the walk.
			if errors.Is(err, os.ErrNotExist) {
				return nil
			}
			return err
		}
		if path == root {
			return nil
		}
		if d.IsDir() {
			if filepath.Dir(path) == root {
				entries++
			}
			return nil
		}
		info, err := d.Info()
		if err != nil {
			if errors.Is(err, os.ErrNotExist) {
				return nil
			}
			return err
		}
		bytes += info.Size()
		return nil
	})
	return bytes, entries, err
}

// ErrBusy is returned when an action is refused because a job is running.
var ErrBusy = errors.New("a job is running on this machine")

// ClearCache removes the leftover scratch and workspace directories.
//
// It REFUSES while a job is in flight. Deleting a running job's scratch
// directory does not free anything useful and does break the job. The refusal
// is an error rather than a silent no-op so the window can say why.
func (s *Shell) ClearCache() (int64, error) {
	if s.ledger.Current() != nil || s.runnerBusy() {
		return 0, sayWrap("A job is running and still needs its files. Try again when it's done.", ErrBusy)
	}
	var freed int64
	var errs []error
	for _, root := range []string{s.scratchRoot(), s.WorkspaceRoot()} {
		if root == "" {
			continue
		}
		entries, err := os.ReadDir(root)
		if err != nil {
			if errors.Is(err, os.ErrNotExist) {
				continue
			}
			errs = append(errs, err)
			continue
		}
		for _, e := range entries {
			path := filepath.Join(root, e.Name())
			b, _, _ := DirUsage(path)
			if err := os.RemoveAll(path); err != nil {
				errs = append(errs, err)
				continue
			}
			freed += b
		}
	}
	s.notify()
	if err := errors.Join(errs...); err != nil {
		s.log.Printf("clear cache: %v", err)
		return freed, sayWrap(fmt.Sprintf("Freed %s, but some files couldn't be removed. Close any program using them and try again.", humanBytes(freed)), err)
	}
	return freed, nil
}

// runnerBusy reports whether the daemon this shell hosts has a job in flight.
func (s *Shell) runnerBusy() bool {
	d := s.currentDaemon()
	return d != nil && d.Busy()
}
