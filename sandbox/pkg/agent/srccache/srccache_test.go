package srccache

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

const mib = int64(1 << 20)

func digest(b []byte) string {
	sum := sha256.Sum256(b)
	return hex.EncodeToString(sum[:])
}

// put writes one entry through the same path a download does.
func put(t *testing.T, c *Cache, key string, body []byte) {
	t.Helper()
	w, err := c.Create(key)
	if err != nil {
		t.Fatalf("Create(%s): %v", key, err)
	}
	if _, err := w.Write(body); err != nil {
		t.Fatalf("Write: %v", err)
	}
	if err := w.Commit(`"`+digest(body)[:8]+`"`, int64(len(body)), digest(body)); err != nil {
		t.Fatalf("Commit: %v", err)
	}
}

// clock is a controllable time source, so least-recently-used is deterministic.
type clock struct {
	mu  sync.Mutex
	now time.Time
}

func (c *clock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.now = c.now.Add(time.Second)
	return c.now
}

func fresh(t *testing.T, root string, max int64) *Cache {
	t.Helper()
	c, err := openFresh(root, max)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	c.now = (&clock{now: time.Date(2026, 10, 11, 9, 0, 0, 0, time.UTC)}).Now
	return c
}

func blob(n int64, fill byte) []byte { return []byte(strings.Repeat(string(fill), int(n))) }

func TestAnEntryIsReadBackWithItsValidator(t *testing.T) {
	c := fresh(t, t.TempDir(), MinMaxBytes)
	body := []byte("the source clip")
	put(t, c, "https://s3.example/bucket/clip.mp4", body)

	h := c.Acquire("https://s3.example/bucket/clip.mp4")
	if h == nil {
		t.Fatal("no entry after a commit")
	}
	defer h.Release()
	if h.Size() != int64(len(body)) || h.SHA256() != digest(body) || h.ETag() == "" {
		t.Fatalf("entry = %d %s %s", h.Size(), h.SHA256(), h.ETag())
	}
	f, err := h.Open()
	if err != nil {
		t.Fatal(err)
	}
	got, _ := io.ReadAll(f)
	_ = f.Close()
	if string(got) != string(body) {
		t.Fatalf("read %q", got)
	}
	if c.Acquire("https://s3.example/bucket/other.mp4") != nil {
		t.Fatal("an unknown key returned an entry")
	}
}

func TestEvictionIsLeastRecentlyUsedDownToTheCap(t *testing.T) {
	c := fresh(t, t.TempDir(), MinMaxBytes)
	part := MinMaxBytes / 4
	put(t, c, "k/a", blob(part, 'a'))
	put(t, c, "k/b", blob(part, 'b'))
	put(t, c, "k/c", blob(part, 'c'))
	// a is used, so b is now the oldest.
	c.Acquire("k/a").Release()
	put(t, c, "k/d", blob(part, 'd'))
	put(t, c, "k/e", blob(part, 'e')) // past the cap: the oldest goes

	if c.Acquire("k/b") != nil {
		t.Fatal("the least recently used entry survived")
	}
	for _, k := range []string{"k/a", "k/c", "k/d", "k/e"} {
		h := c.Acquire(k)
		if h == nil {
			t.Fatalf("%s was evicted", k)
		}
		h.Release()
	}
	if st := c.Stats(); st.Bytes > st.MaxBytes || st.Entries != 4 {
		t.Fatalf("stats = %+v", st)
	}
}

func TestAPinnedEntryIsNeverEvictedOrDeletedUnderItsReader(t *testing.T) {
	root := t.TempDir()
	c := fresh(t, root, MinMaxBytes)
	put(t, c, "k/pinned", blob(MinMaxBytes/2, 'p'))
	h := c.Acquire("k/pinned")
	f, err := h.Open()
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = f.Close() }()

	// Shrinking to nothing, clearing and replacing must all leave the reader's
	// bytes in place until it lets go.
	c.SetMaxBytes(0)
	if freed, err := c.Clear(); err != nil || freed != 0 {
		t.Fatalf("Clear = %d, %v; a pinned entry is not freed now", freed, err)
	}
	if _, err := os.Stat(filepath.Join(root, h.(*handle).e.meta.File)); err != nil {
		t.Fatalf("the pinned bytes were deleted under their reader: %v", err)
	}
	if c.Acquire("k/pinned") != nil {
		t.Fatal("a cleared entry is still offered")
	}
	h.Release()
	if _, err := os.Stat(filepath.Join(root, h.(*handle).e.meta.File)); !os.IsNotExist(err) {
		t.Fatalf("the released bytes of a cleared entry were kept: %v", err)
	}
	if st := c.Stats(); st.Bytes != 0 || st.Entries != 0 {
		t.Fatalf("stats after clear = %+v", st)
	}
}

func TestReplacingAnEntryKeepsOneCopy(t *testing.T) {
	root := t.TempDir()
	c := fresh(t, root, MinMaxBytes)
	put(t, c, "k/x", []byte("version one"))
	put(t, c, "k/x", []byte("version two!"))
	h := c.Acquire("k/x")
	defer h.Release()
	if h.SHA256() != digest([]byte("version two!")) {
		t.Fatal("the replacement is not the entry")
	}
	bins, _ := filepath.Glob(filepath.Join(root, "*.bin"))
	if len(bins) != 1 {
		t.Fatalf("%d data files on disk, want 1: %v", len(bins), bins)
	}
	if st := c.Stats(); st.Bytes != int64(len("version two!")) {
		t.Fatalf("bytes = %d", st.Bytes)
	}
}

func TestTheIndexSurvivesARestartAndDebrisIsSwept(t *testing.T) {
	root := t.TempDir()
	c := fresh(t, root, MinMaxBytes)
	put(t, c, "k/old", []byte("old"))
	put(t, c, "k/new", []byte("new"))
	c.Acquire("k/old").Release() // old is now the most recent use

	// What a crash leaves: a temporary file, bytes with no sidecar, a sidecar
	// whose bytes are gone, and a sidecar pointing outside the directory.
	mustWrite(t, filepath.Join(root, ".tmp-123.bin"), "partial")
	mustWrite(t, filepath.Join(root, "deadbeef-00.bin"), "orphan")
	mustWrite(t, filepath.Join(root, id("k/gone")+".json"), fmt.Sprintf(`{"key":"k/gone","file":"%s-1.bin","etag":"x","size":3,"sha256":"x"}`, id("k/gone")))
	mustWrite(t, filepath.Join(root, id("k/evil")+".json"), `{"key":"k/evil","file":"../../etc/passwd.bin","etag":"x","size":3,"sha256":"x"}`)

	again := fresh(t, root, MinMaxBytes)
	if st := again.Stats(); st.Entries != 2 || st.Bytes != 6 {
		t.Fatalf("restored stats = %+v", st)
	}
	for _, gone := range []string{".tmp-123.bin", "deadbeef-00.bin", id("k/gone") + ".json", id("k/evil") + ".json"} {
		if _, err := os.Stat(filepath.Join(root, gone)); !os.IsNotExist(err) {
			t.Errorf("%s survived the restart", gone)
		}
	}
	// The use before the restart decides which goes first after it.
	again.mu.Lock()
	oldUse := again.entries["k/old"].meta.LastUsed
	newUse := again.entries["k/new"].meta.LastUsed
	again.mu.Unlock()
	if oldUse <= newUse {
		t.Fatal("the least-recently-used order did not survive the restart")
	}
}

func TestAnEntryLargerThanTheCacheIsNotKept(t *testing.T) {
	c := fresh(t, t.TempDir(), MinMaxBytes)
	put(t, c, "k/huge", blob(MinMaxBytes+1, 'h'))
	if c.Acquire("k/huge") != nil {
		t.Fatal("an entry larger than the cache was kept")
	}
}

func TestATurnedOffCacheTakesNothing(t *testing.T) {
	c := fresh(t, t.TempDir(), MinMaxBytes)
	c.SetMaxBytes(0)
	if _, err := c.Create("k"); err == nil {
		t.Fatal("a cache turned off accepted an entry")
	}
	c.SetMaxBytes(1) // a positive cap below the floor is raised to it
	if st := c.Stats(); st.MaxBytes != MinMaxBytes {
		t.Fatalf("max = %d, want the floor", st.MaxBytes)
	}
}

func TestAnAbortedWriteLeavesNothing(t *testing.T) {
	root := t.TempDir()
	c := fresh(t, root, MinMaxBytes)
	w, err := c.Create("k/aborted")
	if err != nil {
		t.Fatal(err)
	}
	_, _ = w.Write([]byte("half"))
	w.Abort()
	names, _ := os.ReadDir(root)
	if len(names) != 0 {
		t.Fatalf("an aborted write left %d files", len(names))
	}
	// A commit whose size disagrees with what was written is refused.
	w, _ = c.Create("k/short")
	_, _ = w.Write([]byte("abc"))
	if err := w.Commit(`"e"`, 10, digest([]byte("abc"))); err == nil {
		t.Fatal("a short write was committed")
	}
}

func TestInvalidateDropsAnEntry(t *testing.T) {
	c := fresh(t, t.TempDir(), MinMaxBytes)
	put(t, c, "k/bad", []byte("bytes"))
	h := c.Acquire("k/bad")
	h.Invalidate()
	h.Release() // a second end is a no-op
	if c.Acquire("k/bad") != nil {
		t.Fatal("an invalidated entry is still offered")
	}
}

func TestOpenSharesOneCachePerRoot(t *testing.T) {
	root := t.TempDir()
	a, err := Open(root, MinMaxBytes)
	if err != nil {
		t.Fatal(err)
	}
	b, err := Open(filepath.Join(root, "."), 2*MinMaxBytes)
	if err != nil {
		t.Fatal(err)
	}
	if a != b {
		t.Fatal("two Opens of one root built two caches")
	}
	if a.Stats().MaxBytes != 2*MinMaxBytes {
		t.Fatal("the later Open's cap was not applied")
	}
	a.RecordHit(10)
	a.RecordMiss()
	if st := b.Stats(); st.Hits != 1 || st.Misses != 1 || st.SavedBytes != 10 {
		t.Fatalf("shared counters = %+v", st)
	}
	if _, err := Open("", MinMaxBytes); err == nil {
		t.Fatal("an empty root was accepted")
	}
}

// Concurrent jobs filling, reading, evicting and clearing one cache: the race
// detector is the assertion, plus the invariant that the books balance.
func TestConcurrentUseKeepsTheBooksStraight(t *testing.T) {
	root := t.TempDir()
	c := fresh(t, root, MinMaxBytes)
	var wg sync.WaitGroup
	for g := 0; g < 8; g++ {
		wg.Add(1)
		go func(g int) {
			defer wg.Done()
			for i := 0; i < 40; i++ {
				key := fmt.Sprintf("k/%d", (g+i)%6)
				if h := c.Acquire(key); h != nil {
					if f, err := h.Open(); err == nil {
						_, _ = io.Copy(io.Discard, f)
						_ = f.Close()
					}
					if i%7 == 0 {
						h.Invalidate()
					} else {
						h.Release()
					}
					continue
				}
				w, err := c.Create(key)
				if err != nil {
					continue
				}
				body := blob(mib*int64(1+i%5), byte('a'+g))
				_, _ = w.Write(body)
				_ = w.Commit(`"x"`, int64(len(body)), digest(body))
				if i%13 == 0 {
					_, _ = c.Clear()
				}
			}
		}(g)
	}
	wg.Wait()
	st := c.Stats()
	var onDisk int64
	bins, _ := filepath.Glob(filepath.Join(root, "*.bin"))
	for _, b := range bins {
		if info, err := os.Stat(b); err == nil {
			onDisk += info.Size()
		}
	}
	if st.Bytes != onDisk {
		t.Fatalf("the index says %d bytes, the disk holds %d", st.Bytes, onDisk)
	}
	if st.Bytes > st.MaxBytes {
		t.Fatalf("the cache is over its cap: %+v", st)
	}
}

func mustWrite(t *testing.T, path, body string) {
	t.Helper()
	if err := os.WriteFile(path, []byte(body), 0o600); err != nil {
		t.Fatal(err)
	}
}
