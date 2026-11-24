// Package srccache is a runner's downloaded-media cache: the source clips,
// images and audio a stage op fetched, kept on disk so the next job that stages
// the same object revalidates it with the object store instead of downloading
// it again. The agent's side of it - how a stage uses an entry, and why that is
// safe - is agent.FetchToFSCached; this package is the store.
//
// The store is deliberately simple and deliberately on disk:
//
//   - One entry per object key, as two files: <id>.json (the sidecar: key,
//     ETag, size, SHA-256, last use) and the bytes, <id>-<n>.bin. The sidecar
//     is the index; Open rebuilds the in-memory view by reading them, so the
//     cache survives a restart with its least-recently-used order intact.
//   - Every write is atomic: bytes and sidecar are written to a temporary name
//     and renamed into place, so a crash leaves either the old entry, the new
//     one, or a temporary file the next Open deletes - never a half-written
//     entry that looks whole.
//   - Eviction is least-recently-used down to MaxBytes, and never touches an
//     entry a job is reading (Acquire pins it). A pinned entry that is replaced,
//     invalidated or cleared is removed from disk when its last reader lets go.
//   - Safe for concurrent jobs in one process. Open returns ONE Cache per root
//     per process, so a desktop shell and the daemon it hosts share the same
//     counters and the same index. Another process sharing the root (a headless
//     runner started by hand) is tolerated rather than coordinated: an entry
//     that disappears under one process is treated as a miss.
package srccache

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
)

// MinMaxBytes is the smallest cap that makes a cache worth having: below it a
// single 1080p source would not fit.
const MinMaxBytes int64 = 256 << 20

// Stats is the cache at one moment. Hits, Misses and SavedBytes count since this
// process started; Bytes and Entries are what is on disk now.
type Stats struct {
	Root       string `json:"root"`
	Bytes      int64  `json:"bytes"`
	Entries    int64  `json:"entries"`
	MaxBytes   int64  `json:"maxBytes"`
	Hits       int64  `json:"hits"`
	Misses     int64  `json:"misses"`
	SavedBytes int64  `json:"savedBytes"`
}

// Cache is the store. Create it with Open.
type Cache struct {
	root string

	mu       sync.Mutex
	maxBytes int64
	entries  map[string]*entry // by key
	bytes    int64
	now      func() time.Time

	hits       atomic.Int64
	misses     atomic.Int64
	savedBytes atomic.Int64
}

// entry is one object. pins counts the readers holding it; doomed means it was
// replaced, invalidated or cleared and goes when the last reader lets go.
type entry struct {
	meta   sidecar
	pins   int
	doomed bool
}

// sidecar is <id>.json.
type sidecar struct {
	Key      string `json:"key"`
	File     string `json:"file"`
	ETag     string `json:"etag"`
	Size     int64  `json:"size"`
	SHA256   string `json:"sha256"`
	LastUsed int64  `json:"lastUsed"` // unix nanoseconds
}

var (
	registryMu sync.Mutex
	registry   = map[string]*Cache{}
)

// Open returns the process's cache at root, creating the directory and reading
// what is already there on first use. A later Open of the same root returns the
// same *Cache and applies maxBytes to it (the last caller's cap wins, which is
// what a settings change wants).
//
// maxBytes below MinMaxBytes is raised to it; use SetMaxBytes(0) to stop
// keeping anything.
func Open(root string, maxBytes int64) (*Cache, error) {
	if strings.TrimSpace(root) == "" {
		return nil, errors.New("srccache: a root directory is required")
	}
	abs, err := filepath.Abs(root)
	if err != nil {
		return nil, fmt.Errorf("srccache: %w", err)
	}
	if maxBytes < MinMaxBytes {
		maxBytes = MinMaxBytes
	}
	registryMu.Lock()
	defer registryMu.Unlock()
	if c, ok := registry[abs]; ok {
		c.SetMaxBytes(maxBytes)
		return c, nil
	}
	c, err := openFresh(abs, maxBytes)
	if err != nil {
		return nil, err
	}
	registry[abs] = c
	return c, nil
}

// openFresh builds a Cache over abs without consulting the process registry.
func openFresh(abs string, maxBytes int64) (*Cache, error) {
	if err := os.MkdirAll(abs, 0o700); err != nil {
		return nil, fmt.Errorf("srccache: create %s: %w", abs, err)
	}
	c := &Cache{root: abs, maxBytes: maxBytes, entries: map[string]*entry{}, now: time.Now}
	if err := c.load(); err != nil {
		return nil, err
	}
	c.mu.Lock()
	c.evictLocked()
	c.mu.Unlock()
	return c, nil
}

// Root is the directory the cache lives in.
func (c *Cache) Root() string { return c.root }

// load rebuilds the index from the sidecars and deletes what no sidecar names:
// temporary files a crash left, and bytes whose sidecar never made it.
func (c *Cache) load() error {
	names, err := os.ReadDir(c.root)
	if err != nil {
		return fmt.Errorf("srccache: read %s: %w", c.root, err)
	}
	referenced := map[string]bool{}
	for _, de := range names {
		name := de.Name()
		if de.IsDir() || !strings.HasSuffix(name, ".json") || strings.HasPrefix(name, ".") {
			continue
		}
		raw, err := os.ReadFile(filepath.Join(c.root, name))
		var meta sidecar
		if err != nil || json.Unmarshal(raw, &meta) != nil || meta.Key == "" ||
			sidecarName(meta.Key) != name || !validDataName(meta.File) {
			_ = os.Remove(filepath.Join(c.root, name))
			continue
		}
		info, err := os.Stat(filepath.Join(c.root, meta.File))
		if err != nil || !info.Mode().IsRegular() || info.Size() != meta.Size {
			_ = os.Remove(filepath.Join(c.root, name))
			continue
		}
		referenced[meta.File] = true
		c.entries[meta.Key] = &entry{meta: meta}
		c.bytes += meta.Size
	}
	for _, de := range names {
		name := de.Name()
		if de.IsDir() || strings.HasSuffix(name, ".json") || referenced[name] {
			continue
		}
		if strings.HasSuffix(name, ".bin") || strings.HasPrefix(name, ".tmp-") {
			_ = os.Remove(filepath.Join(c.root, name))
		}
	}
	return nil
}

// id is the stable on-disk name of a key: the key itself may hold any byte.
func id(key string) string {
	sum := sha256.Sum256([]byte(key))
	return hex.EncodeToString(sum[:16])
}

func sidecarName(key string) string { return id(key) + ".json" }

// validDataName accepts only names this package writes, so a tampered sidecar
// cannot point the cache at a file outside its own directory.
func validDataName(name string) bool {
	if !strings.HasSuffix(name, ".bin") || strings.ContainsAny(name, `/\`) || name != filepath.Base(name) {
		return false
	}
	return len(name) > len(".bin")
}

// Acquire implements agent.SourceCache.
func (c *Cache) Acquire(key string) agent.SourceEntry {
	c.mu.Lock()
	defer c.mu.Unlock()
	e, ok := c.entries[key]
	if !ok || e.doomed {
		return nil
	}
	e.pins++
	return &handle{c: c, e: e}
}

// Create implements agent.SourceCache.
func (c *Cache) Create(key string) (agent.SourceWriter, error) {
	c.mu.Lock()
	limit := c.maxBytes
	c.mu.Unlock()
	if limit <= 0 {
		return nil, errors.New("srccache: the cache is turned off")
	}
	f, err := os.CreateTemp(c.root, ".tmp-*.bin")
	if err != nil {
		return nil, err
	}
	return &writer{c: c, key: key, f: f}, nil
}

// RecordHit implements agent.SourceCache.
func (c *Cache) RecordHit(saved int64) {
	c.hits.Add(1)
	c.savedBytes.Add(saved)
}

// RecordMiss implements agent.SourceCache.
func (c *Cache) RecordMiss() { c.misses.Add(1) }

// Stats reports the cache now.
func (c *Cache) Stats() Stats {
	c.mu.Lock()
	defer c.mu.Unlock()
	var n int64
	for _, e := range c.entries {
		if !e.doomed {
			n++
		}
	}
	return Stats{
		Root:       c.root,
		Bytes:      c.bytes,
		Entries:    n,
		MaxBytes:   c.maxBytes,
		Hits:       c.hits.Load(),
		Misses:     c.misses.Load(),
		SavedBytes: c.savedBytes.Load(),
	}
}

// SetMaxBytes changes the cap and evicts down to it at once. A value of zero
// or less stops the cache keeping anything new and evicts everything no job is
// reading; a positive value below MinMaxBytes is raised to it.
func (c *Cache) SetMaxBytes(n int64) {
	if n > 0 && n < MinMaxBytes {
		n = MinMaxBytes
	}
	if n < 0 {
		n = 0
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	c.maxBytes = n
	c.evictLocked()
}

// Clear deletes every entry and reports the bytes it freed now. An entry a job
// is reading is removed when the job lets go of it, and is not counted.
func (c *Cache) Clear() (int64, error) {
	c.mu.Lock()
	var freed int64
	var firstErr error
	for key, e := range c.entries {
		if e.pins > 0 {
			c.doomLocked(key, e)
			continue
		}
		if err := c.removeLocked(key, e); err != nil {
			if firstErr == nil {
				firstErr = err
			}
			continue
		}
		freed += e.meta.Size
	}
	c.mu.Unlock()
	// Leftover temporary files of writes that are no longer in flight are
	// swept as well; an in-flight one is open and is left alone on Windows,
	// and removed harmlessly (its writer still holds it) elsewhere.
	if names, err := os.ReadDir(c.root); err == nil {
		for _, de := range names {
			if strings.HasPrefix(de.Name(), ".tmp-") {
				if info, err := de.Info(); err == nil && time.Since(info.ModTime()) > time.Hour {
					_ = os.Remove(filepath.Join(c.root, de.Name()))
				}
			}
		}
	}
	return freed, firstErr
}

// evictLocked removes least-recently-used, unpinned entries until the total is
// within the cap.
func (c *Cache) evictLocked() {
	if c.bytes <= c.maxBytes {
		return
	}
	candidates := make([]*entry, 0, len(c.entries))
	for _, e := range c.entries {
		if e.pins == 0 && !e.doomed {
			candidates = append(candidates, e)
		}
	}
	sort.Slice(candidates, func(i, j int) bool { return candidates[i].meta.LastUsed < candidates[j].meta.LastUsed })
	for _, e := range candidates {
		if c.bytes <= c.maxBytes {
			return
		}
		_ = c.removeLocked(e.meta.Key, e)
	}
}

// removeLocked deletes an unpinned entry from disk and from the index.
func (c *Cache) removeLocked(key string, e *entry) error {
	if cur, ok := c.entries[key]; ok && cur == e {
		delete(c.entries, key)
		c.bytes -= e.meta.Size
		if err := os.Remove(filepath.Join(c.root, sidecarName(key))); err != nil && !errors.Is(err, os.ErrNotExist) {
			return err
		}
	}
	if err := os.Remove(filepath.Join(c.root, e.meta.File)); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	return nil
}

// doomLocked takes a pinned entry out of the index now and leaves its bytes for
// the last reader to remove.
func (c *Cache) doomLocked(key string, e *entry) {
	if e.doomed {
		return
	}
	e.doomed = true
	if cur, ok := c.entries[key]; ok && cur == e {
		delete(c.entries, key)
		c.bytes -= e.meta.Size
		_ = os.Remove(filepath.Join(c.root, sidecarName(key)))
	}
}

// release unpins, and finishes a doomed entry's removal.
func (c *Cache) release(e *entry, touch bool) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if e.pins > 0 {
		e.pins--
	}
	if e.doomed {
		if e.pins == 0 {
			_ = os.Remove(filepath.Join(c.root, e.meta.File))
		}
		return
	}
	if touch {
		e.meta.LastUsed = c.now().UnixNano()
		// Best effort: a lost touch only makes this entry look older after a
		// restart.
		_ = c.writeSidecar(e.meta)
	}
	c.evictLocked()
}

// writeSidecar writes <id>.json atomically.
func (c *Cache) writeSidecar(meta sidecar) error {
	raw, err := json.Marshal(meta)
	if err != nil {
		return err
	}
	tmp, err := os.CreateTemp(c.root, ".tmp-*.json")
	if err != nil {
		return err
	}
	if _, err := tmp.Write(raw); err != nil {
		_ = tmp.Close()
		_ = os.Remove(tmp.Name())
		return err
	}
	if err := tmp.Close(); err != nil {
		_ = os.Remove(tmp.Name())
		return err
	}
	if err := os.Rename(tmp.Name(), filepath.Join(c.root, sidecarName(meta.Key))); err != nil {
		_ = os.Remove(tmp.Name())
		return err
	}
	return nil
}

// commit installs a finished download as the entry for its key.
func (c *Cache) commit(key, tmpPath, etag string, size int64, digest string) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.maxBytes <= 0 || size > c.maxBytes {
		_ = os.Remove(tmpPath)
		return nil
	}
	file := fmt.Sprintf("%s-%s.bin", id(key), randomSuffix())
	if err := os.Rename(tmpPath, filepath.Join(c.root, file)); err != nil {
		_ = os.Remove(tmpPath)
		return err
	}
	meta := sidecar{Key: key, File: file, ETag: etag, Size: size, SHA256: strings.ToLower(digest), LastUsed: c.now().UnixNano()}
	if err := c.writeSidecar(meta); err != nil {
		_ = os.Remove(filepath.Join(c.root, file))
		return err
	}
	if old, ok := c.entries[key]; ok {
		if old.pins > 0 {
			old.doomed = true
			c.bytes -= old.meta.Size
			delete(c.entries, key)
		} else {
			delete(c.entries, key)
			c.bytes -= old.meta.Size
			_ = os.Remove(filepath.Join(c.root, old.meta.File))
		}
	}
	c.entries[key] = &entry{meta: meta}
	c.bytes += size
	c.evictLocked()
	return nil
}

func randomSuffix() string {
	var b [6]byte
	if _, err := io.ReadFull(rand.Reader, b[:]); err != nil {
		return fmt.Sprintf("%x", time.Now().UnixNano())
	}
	return hex.EncodeToString(b[:])
}

// handle is a pinned entry handed to one stage.
type handle struct {
	c    *Cache
	e    *entry
	once sync.Once
}

func (h *handle) ETag() string   { return h.e.meta.ETag }
func (h *handle) Size() int64    { return h.e.meta.Size }
func (h *handle) SHA256() string { return h.e.meta.SHA256 }

// Open implements agent.SourceEntry.
func (h *handle) Open() (*os.File, error) {
	return os.Open(filepath.Join(h.c.root, h.e.meta.File))
}

// Release implements agent.SourceEntry: a use, which counts as the entry's most
// recent one.
func (h *handle) Release() {
	h.once.Do(func() { h.c.release(h.e, true) })
}

// Invalidate implements agent.SourceEntry.
func (h *handle) Invalidate() {
	h.once.Do(func() {
		h.c.mu.Lock()
		h.c.doomLocked(h.e.meta.Key, h.e)
		h.c.mu.Unlock()
		h.c.release(h.e, false)
	})
}

// writer is one entry being downloaded.
type writer struct {
	c    *Cache
	key  string
	f    *os.File
	done bool
}

func (w *writer) Write(p []byte) (int, error) { return w.f.Write(p) }

// Commit implements agent.SourceWriter.
func (w *writer) Commit(etag string, size int64, digest string) error {
	if w.done {
		return errors.New("srccache: entry already finished")
	}
	w.done = true
	name := w.f.Name()
	if err := w.f.Close(); err != nil {
		_ = os.Remove(name)
		return err
	}
	info, err := os.Stat(name)
	if err != nil || info.Size() != size {
		_ = os.Remove(name)
		return fmt.Errorf("srccache: the cached copy is not the size the download reported")
	}
	return w.c.commit(w.key, name, etag, size, digest)
}

// Abort implements agent.SourceWriter.
func (w *writer) Abort() {
	if w.done {
		return
	}
	w.done = true
	name := w.f.Name()
	_ = w.f.Close()
	_ = os.Remove(name)
}
