package srccache

import (
	"context"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// objectStore is a presigned-GET object store with ETags: it answers 304 to a
// matching If-None-Match exactly as S3, R2 and Garage do, and records what it
// was asked.
type objectStore struct {
	mu           sync.Mutex
	body         []byte
	etag         string
	ignoreINM    bool
	noETag       bool
	requests     int
	conditionals []string
	queries      []string
	bodiesSent   int
}

func (s *objectStore) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.requests++
	s.queries = append(s.queries, r.URL.RawQuery)
	inm := r.Header.Get("If-None-Match")
	s.conditionals = append(s.conditionals, inm)
	if !s.noETag {
		w.Header().Set("ETag", s.etag)
	}
	if inm != "" && inm == s.etag && !s.ignoreINM {
		w.WriteHeader(http.StatusNotModified)
		return
	}
	s.bodiesSent++
	_, _ = w.Write(s.body)
}

func (s *objectStore) set(body, etag string) {
	s.mu.Lock()
	s.body, s.etag = []byte(body), etag
	s.mu.Unlock()
}

func (s *objectStore) snapshot() (int, []string, int) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.requests, append([]string(nil), s.conditionals...), s.bodiesSent
}

type fetchFixture struct {
	store  *objectStore
	server *httptest.Server
	cache  *Cache
	fs     *agent.RootFS
}

func newFetchFixture(t *testing.T) *fetchFixture {
	t.Helper()
	store := &objectStore{body: []byte("source clip bytes"), etag: `"v1"`}
	server := httptest.NewServer(store)
	t.Cleanup(server.Close)
	fs, err := agent.OpenRootFS(t.TempDir(), 0)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = fs.Close() })
	return &fetchFixture{store: store, server: server, cache: fresh(t, t.TempDir(), MinMaxBytes), fs: fs}
}

// url is a presigned GET of one object: the signature differs every time, as
// it does for every op the engine sends.
func (f *fetchFixture) url(sig string) string {
	return f.server.URL + "/bucket/projects/p/clip.mp4?X-Amz-Signature=" + sig
}

func (f *fetchFixture) stage(t *testing.T, sig, path, want string) agent.Transfer {
	t.Helper()
	tr, opErr := agent.FetchToFSCached(context.Background(), f.server.Client(), f.cache, f.fs, f.url(sig), path, 0, want)
	if opErr != nil {
		t.Fatalf("stage %s: %v", path, opErr)
	}
	got, err := f.fs.ReadFile(path, 1<<20)
	if err != nil {
		t.Fatal(err)
	}
	f.store.mu.Lock()
	body := string(f.store.body)
	f.store.mu.Unlock()
	if string(got) != body {
		t.Fatalf("%s holds %q, want %q", path, got, body)
	}
	return tr
}

func TestASecondStageOfTheSameObjectIsRevalidatedNotDownloaded(t *testing.T) {
	f := newFetchFixture(t)
	first := f.stage(t, "aaa", "media/a.mp4", "")
	second := f.stage(t, "bbb", "media/b.mp4", "")

	requests, conditionals, bodies := f.store.snapshot()
	if requests != 2 || bodies != 1 {
		t.Fatalf("requests=%d bodies=%d, want 2 requests and one download", requests, bodies)
	}
	if conditionals[0] != "" || conditionals[1] != `"v1"` {
		t.Fatalf("If-None-Match sent = %q", conditionals)
	}
	if first != second {
		t.Fatalf("the hit reported %+v, the download %+v", second, first)
	}
	if st := f.cache.Stats(); st.Hits != 1 || st.Misses != 1 || st.SavedBytes != int64(len("source clip bytes")) {
		t.Fatalf("stats = %+v", st)
	}
	// Every request carried its own signature: the cache never replaces the
	// store's authorization.
	if f.store.queries[1] != "X-Amz-Signature=bbb" {
		t.Fatalf("the revalidation was not signed with the op's own URL: %q", f.store.queries[1])
	}
}

func TestAChangedObjectReplacesTheEntry(t *testing.T) {
	f := newFetchFixture(t)
	f.stage(t, "a", "x.mp4", "")
	f.store.set("a different take", `"v2"`)
	f.stage(t, "b", "y.mp4", "")
	h := f.cache.Acquire(agent.SourceCacheKeyOf(f.url("z")))
	if h == nil || h.ETag() != `"v2"` {
		t.Fatalf("entry = %v, want the new version", h)
	}
	h.Release()
	f.stage(t, "c", "z.mp4", "")
	if _, _, bodies := f.store.snapshot(); bodies != 2 {
		t.Fatalf("downloads = %d, want 2 (the original and the changed object)", bodies)
	}
}

// A store that ignores the precondition answers 200; that is a normal download.
func TestAStoreThatIgnoresThePreconditionStillWorks(t *testing.T) {
	f := newFetchFixture(t)
	f.store.ignoreINM = true
	f.stage(t, "a", "x.mp4", "")
	f.stage(t, "b", "y.mp4", "")
	if _, _, bodies := f.store.snapshot(); bodies != 2 {
		t.Fatalf("downloads = %d, want 2", bodies)
	}
}

// Cached bytes that no longer hash to what the store sent are never staged.
func TestACorruptedEntryIsDetectedAndFetchedAgain(t *testing.T) {
	f := newFetchFixture(t)
	f.stage(t, "a", "x.mp4", "")
	h := f.cache.Acquire(agent.SourceCacheKeyOf(f.url("a")))
	path := filepath.Join(f.cache.Root(), h.(*handle).e.meta.File)
	h.Release()
	if err := os.WriteFile(path, []byte("tampered clip bytes"[:len("source clip bytes")]), 0o600); err != nil {
		t.Fatal(err)
	}
	f.stage(t, "b", "y.mp4", "") // asserts the staged bytes are the store's
	if _, _, bodies := f.store.snapshot(); bodies != 2 {
		t.Fatalf("downloads = %d, want the corrupted entry fetched again", bodies)
	}
	if st := f.cache.Stats(); st.Hits != 0 {
		t.Fatalf("a corrupted entry was counted as a hit: %+v", st)
	}
}

// When the engine supplies a digest the entry does not hold, the entry is not
// consulted at all.
func TestADigestMismatchNeverTrustsTheCache(t *testing.T) {
	f := newFetchFixture(t)
	f.stage(t, "a", "x.mp4", "")
	_, opErr := agent.FetchToFSCached(context.Background(), f.server.Client(), f.cache, f.fs, f.url("b"), "y.mp4", 0,
		strings.Repeat("0", 64))
	if opErr == nil || opErr.Code != protocol.ErrCodeStageFailed {
		t.Fatalf("a stage whose digest the object does not have = %v, want stage_failed", opErr)
	}
	_, conditionals, _ := f.store.snapshot()
	if conditionals[1] != "" {
		t.Fatal("the cache was consulted for a digest it does not hold")
	}
	if ok, _, _ := f.fs.Stat("y.mp4"); ok {
		t.Fatal("a mismatched download was kept")
	}
	// With the right digest, the entry is used.
	f.stage(t, "c", "z.mp4", digest([]byte("source clip bytes")))
	if st := f.cache.Stats(); st.Hits != 1 {
		t.Fatalf("hits = %d, want 1", st.Hits)
	}
}

// The op's size cap applies to a hit exactly as to a download.
func TestTheSizeCapAppliesToAHit(t *testing.T) {
	f := newFetchFixture(t)
	f.stage(t, "a", "x.mp4", "")
	_, opErr := agent.FetchToFSCached(context.Background(), f.server.Client(), f.cache, f.fs, f.url("b"), "y.mp4", 4, "")
	if opErr == nil || opErr.Code != protocol.ErrCodeFileTooLarge {
		t.Fatalf("a hit past the op's cap = %v, want file_too_large", opErr)
	}
}

// An object served without a validator cannot be revalidated, so it is not kept.
func TestAnObjectWithoutAnETagIsNotCached(t *testing.T) {
	f := newFetchFixture(t)
	f.store.noETag = true
	f.stage(t, "a", "x.mp4", "")
	if st := f.cache.Stats(); st.Entries != 0 {
		t.Fatalf("stats = %+v, want nothing cached", st)
	}
}

// With no cache, the call is FetchToFS: no conditional header, ever.
func TestANilCacheIsThePlainDownload(t *testing.T) {
	f := newFetchFixture(t)
	for i, sig := range []string{"a", "b"} {
		if _, opErr := agent.FetchToFSCached(context.Background(), f.server.Client(), nil, f.fs, f.url(sig), "x.mp4", 0, ""); opErr != nil {
			t.Fatalf("stage %d: %v", i, opErr)
		}
	}
	_, conditionals, bodies := f.store.snapshot()
	if bodies != 2 || conditionals[0] != "" || conditionals[1] != "" {
		t.Fatalf("bodies=%d conditionals=%q", bodies, conditionals)
	}
}

// The cache key is the object, never its signature.
func TestTheCacheKeyIsTheObjectNotTheSignature(t *testing.T) {
	a := agent.SourceCacheKeyOf("https://S3.Example.com/bucket/Clip.mp4?X-Amz-Signature=1&X-Amz-Date=2")
	b := agent.SourceCacheKeyOf("https://s3.example.com/bucket/Clip.mp4?X-Amz-Signature=3")
	if a != b || a != "https://s3.example.com/bucket/Clip.mp4" {
		t.Fatalf("keys %q and %q", a, b)
	}
	if agent.SourceCacheKeyOf("https://s3.example.com/bucket/clip.mp4") == a {
		t.Fatal("the object path was case-folded; S3 keys are case-sensitive")
	}
}
