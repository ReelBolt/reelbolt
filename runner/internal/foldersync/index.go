package foldersync

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
)

// IndexEntry is what this computer knows about one file: the size and
// modification time it was hashed at, the hash, and the hash the project last
// accepted. A file whose size and mtime still match is not hashed again.
type IndexEntry struct {
	Size   int64  `json:"size"`
	MTime  int64  `json:"mtime"` // UnixNano
	SHA256 string `json:"sha256"`
	Synced string `json:"syncedSha256,omitempty"`
}

// Index is one link's local index:
// os.UserCacheDir()/ReelBolt/folder-index/<linkId>.json. It is a cache: losing
// it costs a re-hash, never a wrong upload, because the server answers
// "unchanged" for content it already has.
type Index struct {
	path string

	mu      sync.Mutex
	entries map[string]IndexEntry
	dirty   bool
}

type indexFile struct {
	Version int                   `json:"version"`
	Files   map[string]IndexEntry `json:"files"`
}

// OpenIndex loads an index, or starts an empty one when the file is missing or
// unreadable (a corrupt cache is not worth refusing to sync over).
func OpenIndex(path string) *Index {
	ix := &Index{path: path, entries: map[string]IndexEntry{}}
	raw, err := os.ReadFile(path)
	if err != nil {
		return ix
	}
	var f indexFile
	if json.Unmarshal(raw, &f) == nil && f.Files != nil {
		ix.entries = f.Files
	}
	return ix
}

// IndexPath is where a link's index lives under a cache directory.
func IndexPath(dir, linkID string) string {
	return filepath.Join(dir, safeFileName(linkID)+".json")
}

func safeFileName(s string) string {
	return strings.Map(func(r rune) rune {
		if r == '-' || r == '_' || (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') {
			return r
		}
		return '_'
	}, s)
}

// Get returns one entry.
func (ix *Index) Get(path string) (IndexEntry, bool) {
	ix.mu.Lock()
	defer ix.mu.Unlock()
	e, ok := ix.entries[path]
	return e, ok
}

// Put records one entry.
func (ix *Index) Put(path string, e IndexEntry) {
	ix.mu.Lock()
	defer ix.mu.Unlock()
	if old, ok := ix.entries[path]; ok && old == e {
		return
	}
	ix.entries[path] = e
	ix.dirty = true
}

// MarkSynced records that the project holds sha for path.
func (ix *Index) MarkSynced(path, sha string) {
	ix.mu.Lock()
	defer ix.mu.Unlock()
	e := ix.entries[path]
	if e.Synced == sha {
		return
	}
	e.Synced = sha
	ix.entries[path] = e
	ix.dirty = true
}

// Delete forgets one path.
func (ix *Index) Delete(path string) {
	ix.mu.Lock()
	defer ix.mu.Unlock()
	if _, ok := ix.entries[path]; ok {
		delete(ix.entries, path)
		ix.dirty = true
	}
}

// Under lists the indexed paths equal to p or inside the folder p, sorted.
func (ix *Index) Under(p string) []string {
	ix.mu.Lock()
	defer ix.mu.Unlock()
	var out []string
	for k := range ix.entries {
		if p == "" || k == p || strings.HasPrefix(k, p+"/") {
			out = append(out, k)
		}
	}
	sort.Strings(out)
	return out
}

// Paths lists every indexed path.
func (ix *Index) Paths() []string { return ix.Under("") }

// Len is the number of entries.
func (ix *Index) Len() int {
	ix.mu.Lock()
	defer ix.mu.Unlock()
	return len(ix.entries)
}

// Save writes the index if it changed.
func (ix *Index) Save() error {
	ix.mu.Lock()
	if !ix.dirty {
		ix.mu.Unlock()
		return nil
	}
	raw, err := json.Marshal(indexFile{Version: 1, Files: ix.entries})
	ix.dirty = false
	ix.mu.Unlock()
	if err != nil {
		return err
	}
	if err := writeFileAtomic(ix.path, raw); err != nil {
		ix.mu.Lock()
		ix.dirty = true
		ix.mu.Unlock()
		return fmt.Errorf("foldersync: write %s: %w", ix.path, err)
	}
	return nil
}

// Remove deletes the index file (a link was removed).
func (ix *Index) Remove() error {
	err := os.Remove(ix.path)
	if err == nil || errors.Is(err, os.ErrNotExist) {
		return nil
	}
	return err
}
