package foldersync

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"strings"
)

// errChanged means a file changed while it was being hashed; it is tried again
// once it is quiet.
var errChanged = errors.New("the file changed while it was being read")

// scannedFile is one syncable file found by a walk.
type scannedFile struct {
	Path  string // relative, slash-separated
	Size  int64
	MTime int64
}

// scanResult is a full walk of a link's folder.
type scanResult struct {
	Files []scannedFile
	// OnlineOnly are files that exist only as cloud placeholders. They are
	// never read, but a file that was synced before it was made online-only
	// stays in the manifest (from the index), so freeing space on this PC does
	// not delete it from the project.
	OnlineOnly []scannedFile
	Dirs       []string // relative folders to watch ("" is the root)
	Skipped    map[string][]string
}

func (s *scanResult) skip(reason, path string) {
	if s.Skipped == nil {
		s.Skipped = map[string][]string{}
	}
	s.Skipped[reason] = append(s.Skipped[reason], path)
}

// relPath turns a path under root into the slash-separated relative path the
// server uses. It returns "" for the root itself and for anything outside it.
func relPath(root, full string) string {
	rel, err := filepath.Rel(root, full)
	if err != nil || rel == "." || rel == ".." || strings.HasPrefix(rel, ".."+string(filepath.Separator)) {
		return ""
	}
	return filepath.ToSlash(rel)
}

// hiddenAncestor reports whether any folder above rel is skipped by name, so
// an event deep inside a hidden folder is ignored.
func hiddenAncestor(rel string) bool {
	segs := strings.Split(rel, "/")
	for _, s := range segs[:len(segs)-1] {
		if nameReason(s) != "" {
			return true
		}
	}
	return false
}

// scanTree walks the folder under root starting at the relative folder start
// ("" for the whole folder). Symlinks are not followed (WalkDir never does),
// skipped folders are not entered, and an entry that cannot be read is counted
// rather than failing the walk.
func scanTree(root, start string) (scanResult, error) {
	var res scanResult
	top := root
	if start != "" {
		top = filepath.Join(root, filepath.FromSlash(start))
	}
	err := filepath.WalkDir(top, func(full string, d fs.DirEntry, walkErr error) error {
		rel := relPath(root, full)
		if walkErr != nil {
			if full == top {
				return walkErr
			}
			res.skip(ReasonUnreadable, rel)
			if d != nil && d.IsDir() {
				return fs.SkipDir
			}
			return nil
		}
		if full == top {
			// The caller has already decided the starting folder is synced.
			res.Dirs = append(res.Dirs, start)
			return nil
		}
		info, err := d.Info()
		if err != nil {
			if errors.Is(err, fs.ErrNotExist) {
				return nil // vanished during the walk
			}
			res.skip(ReasonUnreadable, rel)
			return nil
		}
		if reason := entryReason(d.Name(), info); reason != "" {
			res.skip(reason, rel)
			if info.IsDir() {
				return fs.SkipDir
			}
			if reason == ReasonOnlineOnly {
				res.OnlineOnly = append(res.OnlineOnly, scannedFile{Path: rel, Size: info.Size(), MTime: info.ModTime().UnixNano()})
			}
			return nil
		}
		if !validRelPath(rel) {
			res.skip(ReasonName, rel)
			if info.IsDir() {
				return fs.SkipDir
			}
			return nil
		}
		if info.IsDir() {
			res.Dirs = append(res.Dirs, rel)
			return nil
		}
		res.Files = append(res.Files, scannedFile{Path: rel, Size: info.Size(), MTime: info.ModTime().UnixNano()})
		return nil
	})
	return res, err
}

// hashFile hashes a file and returns the size and mtime it was hashed at. A
// file whose size or mtime moved during the read is errChanged.
//
// The file is opened and looked at through the linked folder's os.Root, so a
// folder on the way to it that is a symlink out of the linked folder is an
// error rather than a way to read what lies behind it.
func hashFile(dir, rel string) (sha string, size, mtime int64, err error) {
	f, err := openInFolder(dir, rel)
	if err != nil {
		return "", 0, 0, err
	}
	defer func() { _ = f.Close() }()
	before, err := f.Stat()
	if err != nil {
		return "", 0, 0, err
	}
	h := sha256.New()
	n, err := io.Copy(h, f)
	if err != nil {
		return "", 0, 0, fmt.Errorf("read: %w", err)
	}
	after, err := lstatInFolder(dir, rel)
	if err != nil {
		return "", 0, 0, err
	}
	if n != before.Size() || after.Size() != before.Size() || !after.ModTime().Equal(before.ModTime()) {
		return "", 0, 0, errChanged
	}
	return hex.EncodeToString(h.Sum(nil)), before.Size(), before.ModTime().UnixNano(), nil
}

// openInFolder opens the file at the relative, slash-separated path rel for
// reading, confined to the linked folder dir: os.Root refuses a path that
// leaves it, through a symlink included.
func openInFolder(dir, rel string) (*os.File, error) {
	root, err := os.OpenRoot(dir)
	if err != nil {
		return nil, err
	}
	defer func() { _ = root.Close() }()
	return root.Open(filepath.FromSlash(rel))
}

// lstatInFolder is os.Lstat confined to the linked folder dir, like
// openInFolder.
func lstatInFolder(dir, rel string) (fs.FileInfo, error) {
	root, err := os.OpenRoot(dir)
	if err != nil {
		return nil, err
	}
	defer func() { _ = root.Close() }()
	return root.Lstat(filepath.FromSlash(rel))
}
