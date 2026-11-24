package daemon

import (
	"errors"
	"os"
	"path/filepath"
	"testing"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/identity"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent/srccache"
)

// memRing is an in-memory secret store, so these tests never write the
// developer's own OS keyring.
type memRing struct{ m map[string]string }

func (r *memRing) Set(service, user, secret string) error {
	if r.m == nil {
		r.m = map[string]string{}
	}
	r.m[service+"/"+user] = secret
	return nil
}

func (r *memRing) Get(service, user string) (string, error) {
	v, ok := r.m[service+"/"+user]
	if !ok {
		return "", errors.New("not found")
	}
	return v, nil
}

func (r *memRing) Delete(service, user string) error {
	delete(r.m, service+"/"+user)
	return nil
}

// newCacheDaemon builds a real daemon (executors and all) with the cache at dir.
func newCacheDaemon(t *testing.T, cfg *config.Config, dir string) *Daemon {
	t.Helper()
	keys := identity.NewWithRing(t.TempDir(), &memRing{}, nil)
	if err := keys.Save(cfg.DeviceID, testPrivateKey(t)); err != nil {
		t.Fatal(err)
	}
	d, err := New(Options{
		Config:        cfg,
		Keys:          keys,
		Prober:        &fakeProber{result: mediaOnlyResult()},
		MediaCacheDir: dir,
		ControlDir:    t.TempDir(),
	})
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	return d
}

func TestTheDaemonOpensTheMediaCacheRunnerJSONAsksFor(t *testing.T) {
	cfg := pairedConfig(t)
	cfg.MediaCache = config.MediaCache{} // absent: the default cap
	dir := filepath.Join(t.TempDir(), "media")
	d := newCacheDaemon(t, cfg, dir)

	cache := d.MediaCache()
	if cache == nil {
		t.Fatal("no media cache with the default configuration")
	}
	st := cache.Stats()
	if st.Root != dir || st.MaxBytes != config.DefaultMediaCacheBytes {
		t.Fatalf("stats = %+v", st)
	}
	if _, err := os.Stat(dir); err != nil {
		t.Fatalf("the cache directory was not created: %v", err)
	}
	// The executors stage through it.
	if d.sourceCache() == nil {
		t.Fatal("the executors were given no cache")
	}
	// The shell opening the same directory gets the same cache, counters and all.
	shared, err := srccache.Open(dir, config.DefaultMediaCacheBytes)
	if err != nil {
		t.Fatal(err)
	}
	if shared != cache {
		t.Fatal("the shell and the daemon would read two different caches")
	}
}

func TestAMediaCacheTurnedOffIsNotOpened(t *testing.T) {
	cfg := pairedConfig(t) // maxBytes 0
	dir := filepath.Join(t.TempDir(), "media")
	d := newCacheDaemon(t, cfg, dir)
	if d.MediaCache() != nil {
		t.Fatal("a cache was opened although runner.json turns it off")
	}
	// A nil cache must reach the executors as a nil interface, or every stage
	// would call through a nil pointer.
	if d.sourceCache() != nil {
		t.Fatal("the executors were handed a non-nil interface holding no cache")
	}
	if _, err := os.Stat(dir); !os.IsNotExist(err) {
		t.Fatalf("the cache directory was created although the cache is off: %v", err)
	}
}
