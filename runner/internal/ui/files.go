package ui

import (
	"os"
	"path/filepath"
)

// writeFileAtomic writes a small file by rename, so a reader never sees half of
// it. It is the same discipline runner.json and the governor's documents are
// written with, and it matters here for the same reason: a login item that is
// half written is a login item the session will try to run.
func writeFileAtomic(path string, raw []byte, mode os.FileMode) error {
	dir := filepath.Dir(path)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	tmp, err := os.CreateTemp(dir, filepath.Base(path)+".tmp*")
	if err != nil {
		return err
	}
	name := tmp.Name()
	defer func() { _ = os.Remove(name) }()
	if err := tmp.Chmod(mode); err != nil {
		_ = tmp.Close()
		return err
	}
	if _, err := tmp.Write(raw); err != nil {
		_ = tmp.Close()
		return err
	}
	if err := tmp.Close(); err != nil {
		return err
	}
	return os.Rename(name, path)
}
