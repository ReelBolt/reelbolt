package foldersync

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/deviceapi"
)

// ---- a fake device API ----
//
// It is the server as the engine needs to see it: the upload ticket lifecycle, a
// complete-manifest reconciliation with the mass-delete guard, and a delete
// call. Every canned failure is per path so one test can make exactly one file
// fail.

type fakeAPI struct {
	mu sync.Mutex

	// files is the project's content: relative path -> sha256.
	files map[string]string

	// seedFiles are the rows the server already holds at the start.
	seedFiles map[string]string

	storage *deviceapi.Storage
	// reconciled is every path the manifest diff told the server to delete.
	// The SERVER deletes them: the client's delete route is for a live removal
	// it observed itself.
	reconciled []string

	// guardPause makes the server apply the mass-delete guard: a final manifest
	// that is empty (or deletes more than half of >= 20 files) comes back
	// paused.
	guardPause bool
	// pauseReason is what a pauseOnCall refusal means: "guard" or "user".
	pauseReason string

	// Link failures.
	errManifest        error
	failManifestFirstN int
	manifestCalls      int

	// Per-path canned answers.
	errUpload    map[string]error
	unchangedOn  map[string]bool
	failPath     map[string]int // path -> remaining forced failures
	occupied     map[string]bool
	pauseOnCall  bool
	unauthorized bool

	// Recorded calls.
	manifests   []deviceapi.ManifestBatch
	uploads     []deviceapi.UploadRequest
	deletes     [][]string
	parts       []int
	completeCnt int
	abortCnt    int
	// partAttempts counts PUTs per part number, so a retry is observable.
	partAttempts map[int]int
	// open is uploadId -> the request that opened it, so a completion knows
	// which path it finished.
	open map[string]deviceapi.UploadRequest
	// partBytes is the most recent upload's part number -> bytes.
	partBytes map[int][]byte

	putObjects []putObjectCall

	// onPut, when set, is called with a single PUT's path once its body has
	// been read, outside the lock: the moment a test rewrites a file "while it
	// uploads".
	onPut func(path string)
}

type putObjectCall struct {
	url  string
	data []byte
}

func newFakeAPI() *fakeAPI {
	return &fakeAPI{
		files:       map[string]string{},
		seedFiles:   map[string]string{},
		errUpload:   map[string]error{},
		unchangedOn: map[string]bool{},
		failPath:    map[string]int{},
		occupied:    map[string]bool{},
		open:        map[string]deviceapi.UploadRequest{},
		partBytes:   map[int][]byte{},
	}
}

// serverFiles is what the project holds, seed plus everything uploaded.
func (f *fakeAPI) serverFiles() map[string]string {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := map[string]string{}
	for p, s := range f.seedFiles {
		out[p] = s
	}
	for p, s := range f.files {
		out[p] = s
	}
	return out
}

func (f *fakeAPI) setGuardPause(on bool) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.guardPause = on
}

func (f *fakeAPI) setSeed(path, sha string) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.seedFiles[path] = sha
}

func (f *fakeAPI) Manifest(_ context.Context, linkID string, b deviceapi.ManifestBatch) (deviceapi.ManifestResult, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.manifestCalls++
	if f.errManifest != nil {
		return deviceapi.ManifestResult{}, f.errManifest
	}
	if f.failManifestFirstN > 0 {
		f.failManifestFirstN--
		return deviceapi.ManifestResult{}, &deviceapi.Error{Method: "POST", Route: "/manifest", Status: 500, Code: "internal"}
	}
	f.manifests = append(f.manifests, b)
	if !b.Final {
		return deviceapi.ManifestResult{Accepted: len(b.Files)}, nil
	}

	current := map[string]string{}
	for p, s := range f.seedFiles {
		current[p] = s
	}
	for p, s := range f.files {
		current[p] = s
	}
	manifest := map[string]string{}
	for _, mf := range b.Files {
		manifest[mf.Path] = mf.SHA256
	}
	var upload, del []string
	for p, sha := range manifest {
		if current[p] != sha {
			upload = append(upload, p)
		}
	}
	for p := range current {
		if _, ok := manifest[p]; !ok {
			del = append(del, p)
		}
	}
	sort.Strings(upload)
	sort.Strings(del)

	f.reconciled = append(f.reconciled, del...)
	out := deviceapi.ManifestResult{Upload: upload, Delete: del, Storage: f.storage}
	if f.guardPause && len(manifest) == 0 && len(current) > 0 {
		out.Paused = true
		out.PausedReason = deviceapi.PausedByGuard
		out.LastError = "The folder on this PC looks empty or mostly gone."
		// The guard holds the deletions back: they are NOT applied.
		out.Delete = nil
		return out, nil
	}
	// The real server applies its own diff, so the project really does lose
	// these paths. Doing the same here is what makes "the file is gone from the
	// project" a fact rather than a claim.
	for _, p := range del {
		delete(f.files, p)
		delete(f.seedFiles, p)
	}
	return out, nil
}

// GuardPaused implements the engine's guardReporter: this fake only ever pauses
// via the guard, which is what the guardPause tests exercise.
func (f *fakeAPI) GuardPaused(string) bool {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.guardPause || f.pauseReason == "guard"
}

func (f *fakeAPI) CreateUpload(_ context.Context, _ string, r deviceapi.UploadRequest) (deviceapi.Ticket, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.unauthorized {
		return deviceapi.Ticket{}, &deviceapi.Error{Method: "POST", Route: "/uploads", Status: 401, Code: "device_unauthorized"}
	}
	if f.pauseOnCall {
		return deviceapi.Ticket{}, &deviceapi.Error{Method: "POST", Route: "/uploads", Status: 409, Code: "link_paused"}
	}
	if f.occupied[r.Path] {
		return deviceapi.Ticket{}, &deviceapi.Error{Method: "POST", Route: "/uploads", Status: 409, Code: "occupied"}
	}
	if e := f.errUpload[r.Path]; e != nil {
		return deviceapi.Ticket{}, e
	}
	if f.failPath[r.Path] > 0 {
		f.failPath[r.Path]--
		return deviceapi.Ticket{}, &deviceapi.Error{Method: "POST", Route: "/uploads", Status: 503, Code: "unavailable"}
	}
	if f.unchangedOn[r.Path] {
		return deviceapi.Ticket{}, &deviceapi.Error{Method: "POST", Route: "/uploads", Status: 409, Code: "unchanged"}
	}
	f.uploads = append(f.uploads, r)

	if r.Size > 0 && r.Size >= 256 {
		// A multipart upload: parts of 256 bytes each.
		partSize := int64(256)
		count := int((r.Size + partSize - 1) / partSize)
		t := deviceapi.Ticket{
			Mode: deviceapi.ModeMultipart, FileID: "file-1", SizeBytes: r.Size,
			UploadID: "upload-1", PartSizeBytes: partSize, PartCount: count,
		}
		f.open["upload-1"] = r
		return t, nil
	}
	return deviceapi.Ticket{
		Mode: deviceapi.ModeSinglePut, FileID: "file-1", SizeBytes: r.Size,
		URL: "https://storage.test/put/" + r.Path + "?sig=secret", Headers: map[string]string{"x-amz-checksum-sha256": r.SHA256},
	}, nil
}

func (f *fakeAPI) PresignParts(_ context.Context, _ string, _ deviceapi.Ticket, parts []int) ([]deviceapi.PartURL, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([]deviceapi.PartURL, 0, len(parts))
	for _, n := range parts {
		f.parts = append(f.parts, n)
		out = append(out, deviceapi.PartURL{PartNumber: n, URL: fmt.Sprintf("https://storage.test/part/%d?sig=secret", n)})
	}
	return out, nil
}

func (f *fakeAPI) CompleteUpload(_ context.Context, _ string, t deviceapi.Ticket, _ []deviceapi.CompletedPart) (deviceapi.Completed, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.completeCnt++
	req, ok := f.open[t.UploadID]
	if !ok {
		return deviceapi.Completed{}, &deviceapi.Error{Method: "POST", Route: "/uploads/complete", Status: 404, Code: "upload_not_found"}
	}
	// The bytes were recorded by PutObject; commit them as this path's content.
	var data []byte
	for n := 1; n <= len(f.partBytes); n++ {
		data = append(data, f.partBytes[n]...)
	}
	f.files[req.Path] = sha256Hex(data)
	return deviceapi.Completed{FileID: "file-1", Path: req.Path}, nil
}

func (f *fakeAPI) AbortUpload(context.Context, string, deviceapi.Ticket) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.abortCnt++
	return nil
}

func (f *fakeAPI) DeletePaths(_ context.Context, _ string, paths []string) (int, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.deletes = append(f.deletes, append([]string(nil), paths...))
	for _, p := range paths {
		delete(f.files, p)
		delete(f.seedFiles, p)
	}
	return len(paths), nil
}

// PutObject records the bytes and, for a part URL, fails the attempts the test
// asked for.
func (f *fakeAPI) PutObject(_ context.Context, rawURL string, _ map[string]string, body io.Reader, size int64) (string, error) {
	data, err := io.ReadAll(body)
	if err != nil {
		return "", err
	}
	// Registered before the unlock below, so it runs after it.
	var putPath string
	defer func() {
		f.mu.Lock()
		hook := f.onPut
		f.mu.Unlock()
		if hook != nil && putPath != "" {
			hook(putPath)
		}
	}()
	f.mu.Lock()
	defer f.mu.Unlock()
	f.putObjects = append(f.putObjects, putObjectCall{url: rawURL, data: data})

	if n, ok := partNumber(rawURL); ok {
		if f.partAttempts == nil {
			f.partAttempts = map[int]int{}
		}
		f.partAttempts[n]++
		// A canned failure is CONSUMED: a part that fails once then succeeds
		// must not be failed again on every attempt.
		if key := "part:" + strconv.Itoa(n); f.failPath[key] > 0 {
			f.failPath[key]--
			return "", &deviceapi.StorageError{Status: 500, Host: "storage.test"}
		}
		if f.partBytes == nil {
			f.partBytes = map[int][]byte{}
		}
		f.partBytes[n] = append([]byte(nil), data...)
		return `"etag-` + strconv.Itoa(n) + `"`, nil
	}
	// A single PUT has no separate completion step: the object is there.
	if i := strings.Index(rawURL, "/put/"); i >= 0 {
		rest := rawURL[i+len("/put/"):]
		if j := strings.IndexByte(rest, '?'); j >= 0 {
			rest = rest[:j]
		}
		f.files[rest] = sha256Hex(data)
		putPath = rest
	}
	return `"etag-` + strconv.Itoa(len(data)) + `"`, nil
}

func partNumber(rawURL string) (int, bool) {
	i := strings.Index(rawURL, "/part/")
	if i < 0 {
		return 0, false
	}
	rest := rawURL[i+len("/part/"):]
	if j := strings.IndexByte(rest, '?'); j >= 0 {
		rest = rest[:j]
	}
	n, err := strconv.Atoi(rest)
	return n, err == nil
}

// putBytes is the object as storage would assemble it: the LAST attempt at each
// part, in part order — not every attempt that was ever made.
func (f *fakeAPI) putBytes() []byte {
	f.mu.Lock()
	defer f.mu.Unlock()
	var all []byte
	for n := 1; n <= len(f.partBytes); n++ {
		all = append(all, f.partBytes[n]...)
	}
	return all
}

// allPutBytes is every byte any PUT ever carried, retries included.
func (f *fakeAPI) allPutBytes() []byte {
	f.mu.Lock()
	defer f.mu.Unlock()
	var all []byte
	for _, c := range f.putObjects {
		all = append(all, c.data...)
	}
	return all
}

func (f *fakeAPI) manifestBatches() []deviceapi.ManifestBatch {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]deviceapi.ManifestBatch(nil), f.manifests...)
}

func (f *fakeAPI) uploadedPaths() []string {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([]string, 0, len(f.uploads))
	for _, u := range f.uploads {
		out = append(out, u.Path)
	}
	return out
}

// reconciledPaths is what the manifest diff asked the server to delete.
func (f *fakeAPI) reconciledPaths() []string {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := append([]string(nil), f.reconciled...)
	sort.Strings(out)
	return out
}

func (f *fakeAPI) deletedPaths() []string {
	f.mu.Lock()
	defer f.mu.Unlock()
	var out []string
	for _, d := range f.deletes {
		out = append(out, d...)
	}
	sort.Strings(out)
	return out
}

func (f *fakeAPI) putURLs() []string {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([]string, 0, len(f.putObjects))
	for _, c := range f.putObjects {
		out = append(out, c.url)
	}
	return out
}

// ---- fixtures ----

func sha256Hex(b []byte) string {
	sum := sha256.Sum256(b)
	return hex.EncodeToString(sum[:])
}

// testTiming is fast, and still lets a real fsnotify event be observed, debounced
// and a file be seen to stop changing.
func testTiming() Timing {
	return Timing{
		Quiet:           120 * time.Millisecond,
		Stable:          250 * time.Millisecond,
		Tick:            40 * time.Millisecond,
		Reconcile:       10 * time.Minute,
		PollEvery:       time.Minute,
		MaxWatchDirs:    500,
		StoreCheck:      time.Hour,
		RetryBase:       time.Millisecond,
		RetryMax:        time.Millisecond,
		Recheck:         time.Millisecond,
		PartBackoff:     time.Millisecond,
		PartRetries:     3,
		PartConcurrency: 3,
	}
}

type fixture struct {
	t      *testing.T
	root   string // the synced local folder
	dir    string // every other file (store, index)
	store  *LinkStore
	api    *fakeAPI
	engine *Engine
	ctx    context.Context
	cancel context.CancelFunc
	linkID string
}

func newFixture(t *testing.T, api *fakeAPI, mutate func(*Options, *Timing)) *fixture {
	t.Helper()
	dir := t.TempDir()
	root := filepath.Join(dir, "Footage")
	if err := os.MkdirAll(root, 0o755); err != nil {
		t.Fatal(err)
	}
	store := OpenStore(filepath.Join(dir, "folders.json"))
	link := Link{LinkID: "link-1", LocalPath: root, ProjectID: "proj-1", ProjectName: "Launch", TargetFolder: "Footage"}
	if err := store.Add(link); err != nil {
		t.Fatal(err)
	}
	timing := testTiming()
	opts := Options{
		Store:      store,
		API:        api,
		IndexDir:   filepath.Join(dir, "index"),
		StatusPath: filepath.Join(dir, "folder-sync.status.json"),
		Logger:     testLogger{t},
		Timing:     timing,
	}
	if mutate != nil {
		mutate(&opts, &timing)
	}
	opts.Timing = timing
	e, err := New(opts)
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		defer close(done)
		_ = e.Run(ctx)
	}()
	f := &fixture{t: t, root: root, dir: dir, store: store, api: api, engine: e, ctx: ctx, cancel: cancel, linkID: "link-1"}
	// The engine must be fully stopped before the test's temp directory is
	// removed: a watcher still delivering an event would be writing the index
	// into a directory that no longer exists.
	t.Cleanup(func() {
		cancel()
		select {
		case <-done:
		case <-time.After(10 * time.Second):
			t.Errorf("the folder-sync engine did not stop")
		}
	})
	return f
}

// writeBeforeStart writes files into the fixture's folder from newFixture's
// mutate hook, so they are there before the engine starts. A test about what
// the start-up walk finds has to do this: a file written after Run began races
// that walk, and one the walk misses arrives as a live event instead. It
// returns the folder, for anything else the test wants to put there.
func writeBeforeStart(t *testing.T, o *Options, files map[string]string) string {
	t.Helper()
	links, err := o.Store.Load()
	if err != nil || len(links) != 1 {
		t.Fatalf("load the fixture's link: %v (%d links)", err, len(links))
	}
	root := links[0].LocalPath
	for rel, content := range files {
		full := filepath.Join(root, filepath.FromSlash(rel))
		if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(full, []byte(content), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	return root
}

// testLogger keeps the test output readable but still fails the test on an
// unexpected line, so a swallowed error cannot pass for a green run.
type testLogger struct{ t *testing.T }

func (l testLogger) Printf(format string, args ...any) {
	l.t.Logf("[engine] "+format, args...)
}

func (f *fixture) write(rel string, content string) string {
	f.t.Helper()
	full := filepath.Join(f.root, filepath.FromSlash(rel))
	if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
		f.t.Fatal(err)
	}
	if err := os.WriteFile(full, []byte(content), 0o644); err != nil {
		f.t.Fatal(err)
	}
	return full
}

func (f *fixture) remove(rel string) {
	f.t.Helper()
	if err := os.Remove(filepath.Join(f.root, filepath.FromSlash(rel))); err != nil {
		f.t.Fatal(err)
	}
}

// find returns the link's status row, and whether there is one yet. A freshly
// started engine has not read the store on its first tick, so "no row" is a
// normal thing to see while waiting rather than a failure.
func (f *fixture) find() (LinkStatus, bool) {
	for _, l := range f.engine.Status().Links {
		if l.LinkID == f.linkID {
			return l, true
		}
	}
	return LinkStatus{}, false
}

// link returns the link's status row, failing the test when there is none.
func (f *fixture) link() LinkStatus {
	l, ok := f.find()
	if !ok {
		f.t.Fatalf("no status row for %s", f.linkID)
	}
	return l
}

// waitFor polls until cond is true, or fails the test.
func (f *fixture) waitFor(what string, cond func() bool) {
	f.t.Helper()
	deadline := time.Now().Add(8 * time.Second)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(20 * time.Millisecond)
	}
	s := f.status()
	rows := make([]string, 0, len(s.Links))
	for _, l := range s.Links {
		rows = append(rows, fmt.Sprintf("%s=%s/%s(%s) fileCount=%d reason=%q", l.LinkID, l.State, l.Headline, l.PausedBy, l.FileCount, l.Reason))
	}
	f.t.Fatalf("timed out waiting for %s; problem=%q links=[%s] uploads=%v",
		what, s.Problem, strings.Join(rows, " "), f.api.uploadedPaths())
}

// waitUploaded waits until the project holds content for a path.
func (f *fixture) waitUploaded(path string) {
	f.t.Helper()
	f.waitFor("upload of "+path, func() bool {
		_, ok := f.api.serverFiles()[path]
		return ok
	})
}

// waitDeleted waits until the project no longer holds a path.
func (f *fixture) waitDeleted(path string) {
	f.t.Helper()
	f.waitFor("deletion of "+path, func() bool {
		_, ok := f.api.serverFiles()[path]
		return !ok
	})
}

// waitManifests waits until the server has received at least n manifest
// batches. The first one marks the end of the start-up walk: the watches are
// installed before it, and anything written after it reaches the engine only
// as a live event.
func (f *fixture) waitManifests(n int) {
	f.t.Helper()
	f.waitFor(fmt.Sprintf("%d manifest batches", n), func() bool {
		return len(f.api.manifestBatches()) >= n
	})
}

// waitStarted waits for the start-up walk's manifest, so that what the test
// writes next reaches the engine as a live event. Written any earlier, a file
// races the engine's start: the walk, the watcher or both may see it, and
// which one did changes from run to run.
func (f *fixture) waitStarted() {
	f.t.Helper()
	f.waitManifests(1)
}

// waitSettled waits until the link reads as up to date with no pending work.
func (f *fixture) waitSettled() {
	f.t.Helper()
	f.waitFor("the link to settle", func() bool {
		l, ok := f.find()
		return ok && l.State == StateUpToDate
	})
}

func (f *fixture) status() Status { return f.engine.Status() }

// pausedLink pauses the stored link directly, as "folders pause" does.
func (f *fixture) pause() {
	f.t.Helper()
	if err := f.store.Modify(f.linkID, func(l *Link) { l.Paused = true }); err != nil {
		f.t.Fatal(err)
	}
	f.engine.Reload()
}

func (f *fixture) resume(confirm bool) {
	f.t.Helper()
	if err := f.engine.Resume(f.linkID, confirm); err != nil {
		f.t.Fatal(err)
	}
}

// ---- pure rules that do not need a folder ----

func TestNameReasonCoversWhatIsSkipped(t *testing.T) {
	cases := []struct {
		name string
		want string
	}{
		{".DS_Store", ReasonSystem},
		{"Thumbs.db", ReasonSystem},
		{"desktop.ini", ReasonSystem},
		{".hidden", ReasonHidden},
		{".git", ReasonHidden},
		{"~$report.docx", ReasonTemporary},
		{"clip.tmp", ReasonTemporary},
		{"clip.part", ReasonTemporary},
		{"clip.crdownload", ReasonTemporary},
		{"movie.MP4", ""},
		{"notes.txt", ""},
	}
	for _, c := range cases {
		if got := nameReason(c.name); got != c.want {
			t.Errorf("nameReason(%q) = %q, want %q", c.name, got, c.want)
		}
	}
}

func TestValidRelPath(t *testing.T) {
	good := []string{"a.txt", "sub/dir/b.bin", "Ünïcode.txt", strings.Repeat("a", 1024)}
	for _, p := range good {
		if !validRelPath(p) {
			t.Errorf("validRelPath(%q) = false", p)
		}
	}
	bad := []string{"", "a\\b", "..", "a/../b", "a//b", "./a", strings.Repeat("a", 1025), "a\x01b"}
	for _, p := range bad {
		if validRelPath(p) {
			t.Errorf("validRelPath(%q) = true", p)
		}
	}
}

// TestWindowsPlaceholderRule is the online-only rule, exercised on every
// platform: the constants are the ones winnt.h defines, and reading such a file
// is what would make iCloud or OneDrive download it.
func TestWindowsPlaceholderRule(t *testing.T) {
	const (
		recallOnDataAccess = 0x00400000
		recallOnOpen       = 0x00040000
		offline            = 0x00001000
		hidden             = 0x00000002
	)
	for _, attrs := range []uint32{recallOnDataAccess, recallOnOpen, offline} {
		if got := windowsAttrReason(attrs, false); got != ReasonOnlineOnly {
			t.Errorf("attributes %#x: %q, want the online-only reason", attrs, got)
		}
	}
	if got := windowsAttrReason(recallOnDataAccess, true); got != "" {
		t.Errorf("a placeholder folder was skipped: %q", got)
	}
	if got := windowsAttrReason(hidden, false); got != ReasonHidden {
		t.Errorf("hidden attribute: %q", got)
	}
	if got := windowsAttrReason(0, false); got != "" {
		t.Errorf("a plain file was skipped: %q", got)
	}
}

func TestChunkManifestBatches(t *testing.T) {
	// Nothing at all is still one final batch.
	if got := chunkManifest(nil); len(got) != 1 || len(got[0]) != 0 {
		t.Fatalf("an empty manifest = %d batches", len(got))
	}
	files := make([]deviceapi.ManifestFile, 2501)
	for i := range files {
		files[i] = deviceapi.ManifestFile{Path: fmt.Sprintf("f%04d", i)}
	}
	batches := chunkManifest(files)
	if len(batches) != 2 {
		t.Fatalf("2501 files = %d batches, want 2", len(batches))
	}
	if len(batches[0]) != deviceapi.MaxManifestFiles || len(batches[1]) != 501 {
		t.Fatalf("batch sizes = %d, %d", len(batches[0]), len(batches[1]))
	}
}

// TestSendManifestMarksOnlyTheLastBatchFinal checks the contract the server
// relies on: every batch carries the same manifest id and its own number, and
// only the last one is final.
func TestSendManifestMarksOnlyTheLastBatchFinal(t *testing.T) {
	api := newFakeAPI()
	files := make([]deviceapi.ManifestFile, deviceapi.MaxManifestFiles+1)
	for i := range files {
		files[i] = deviceapi.ManifestFile{Path: fmt.Sprintf("f%04d", i), SHA256: "x"}
	}
	out, err := sendManifest(context.Background(), api, "link-1", "m-1", files, true)
	if err != nil {
		t.Fatal(err)
	}
	_ = out
	got := api.manifestBatches()
	if len(got) != 2 {
		t.Fatalf("%d batches", len(got))
	}
	for i, b := range got {
		if b.ManifestID != "m-1" || b.Batch != i {
			t.Errorf("batch %d: id=%q number=%d", i, b.ManifestID, b.Batch)
		}
		if wantFinal := i == len(got)-1; b.Final != wantFinal {
			t.Errorf("batch %d: final=%v, want %v", i, b.Final, wantFinal)
		}
	}
	if !got[1].ConfirmLargeDelete {
		t.Error("the confirmation did not reach the final batch")
	}
	if got[0].ConfirmLargeDelete {
		t.Error("the confirmation was sent on a non-final batch")
	}
}

// TestIndexAvoidsRehashingUnchangedFiles is the index's whole reason to exist.
func TestIndexAvoidsRehashingUnchangedFiles(t *testing.T) {
	api := newFakeAPI()
	f := newFixture(t, api, nil)
	f.waitStarted()
	f.write("a.txt", "hello")
	f.waitUploaded("a.txt")
	f.write("sub/b.txt", "world")
	f.waitUploaded("sub/b.txt")
	f.waitSettled()

	before := f.engine.Hashes()
	if before == 0 {
		t.Fatal("the first pass read no file to hash")
	}
	// A full rescan with nothing changed must not read a file again. The
	// rescan hashes before it sends its manifest, so once the manifest is in,
	// the count is final.
	seen := len(f.api.manifestBatches())
	f.engine.Sync(f.linkID)
	f.waitManifests(seen + 1)
	if after := f.engine.Hashes(); after != before {
		t.Fatalf("a rescan re-hashed %d files (was %d)", after-before, before)
	}
}

// TestSkipRulesWalkARealTree covers what a walk skips: hidden names, system
// files, temporary names, symlinks and non-regular files.
//
// The tree is built before the engine starts. Only a walk reports a skipped
// name (the watcher drops events for hidden, system and temporary names before
// they become work), so a file written after Run began that the start-up walk
// missed was never reported at all.
func TestSkipRulesWalkARealTree(t *testing.T) {
	api := newFakeAPI()
	f := newFixture(t, api, func(o *Options, _ *Timing) {
		root := writeBeforeStart(t, o, map[string]string{
			"keep.txt":           "keep",
			".DS_Store":          "junk",
			"Thumbs.db":          "junk",
			".hidden/inside.txt": "junk",
			"~$draft.docx":       "junk",
			"half.part":          "junk",
			"notes.tmp":          "junk",
		})
		if err := os.Symlink(filepath.Join(root, "keep.txt"), filepath.Join(root, "alias.txt")); err != nil {
			t.Skipf("symlinks are not available here: %v", err)
		}
	})

	f.waitUploaded("keep.txt")
	// Give the walk a chance to report everything it skipped.
	f.waitFor("the skips to be reported", func() bool {
		l, ok := f.find()
		if !ok {
			return false
		}
		var total int
		for _, g := range l.Skipped {
			total += g.Count
		}
		return total >= 6
	})
	groups := map[string]int{}
	for _, g := range f.link().Skipped {
		groups[g.Reason] = g.Count
	}
	if groups[ReasonSystem] != 2 {
		t.Errorf("system files skipped = %d, want 2", groups[ReasonSystem])
	}
	if groups[ReasonHidden] == 0 {
		t.Error("hidden files were not reported")
	}
	if groups[ReasonTemporary] != 3 {
		t.Errorf("temporary files skipped = %d, want 3", groups[ReasonTemporary])
	}
	if groups[ReasonSymlink] != 1 {
		t.Errorf("symlinks skipped = %d, want 1", groups[ReasonSymlink])
	}
	for _, p := range f.api.uploadedPaths() {
		for _, banned := range []string{".DS_Store", "Thumbs.db", ".hidden", "~$", ".part", ".tmp", "alias.txt"} {
			if strings.Contains(p, banned) {
				t.Errorf("%q was uploaded", p)
			}
		}
	}
}

// TestZeroByteFileIsAFile: the lesson carried over from the cloud-sync work. A
// zero-byte file is a file; it is uploaded, and the empty body is sent with an
// explicit zero length.
func TestZeroByteFileIsAFile(t *testing.T) {
	api := newFakeAPI()
	f := newFixture(t, api, nil)
	f.waitStarted()
	f.write("empty.txt", "")
	f.waitUploaded("empty.txt")

	ups := f.api.uploadedPaths()
	found := false
	for _, u := range ups {
		if u == "empty.txt" {
			found = true
		}
	}
	if !found {
		t.Fatalf("a zero-byte file was not uploaded: %v", ups)
	}
	if len(f.api.allPutBytes()) != 0 {
		t.Fatalf("the zero-byte upload sent %d bytes", len(f.api.allPutBytes()))
	}
	if f.api.completeCnt != 0 {
		t.Fatalf("a single PUT called the multipart completion route %d times", f.api.completeCnt)
	}
	if got := f.api.serverFiles()["empty.txt"]; got != sha256Hex(nil) {
		t.Fatalf("the project holds %q for the empty file", got)
	}
}

func TestCreateModifyDeleteAndRename(t *testing.T) {
	api := newFakeAPI()
	f := newFixture(t, api, nil)
	f.waitStarted()

	// Create.
	f.write("a.txt", "one")
	f.waitUploaded("a.txt")
	if f.api.serverFiles()["a.txt"] != sha256Hex([]byte("one")) {
		t.Fatalf("a.txt content = %q", f.api.serverFiles()["a.txt"])
	}

	// Modify in place: the content changes, the path does not.
	f.waitFor("a.txt to settle", func() bool { l, ok := f.find(); return ok && l.State == StateUpToDate })
	f.write("a.txt", "two")
	f.waitFor("a.txt to be rewritten", func() bool {
		return f.api.serverFiles()["a.txt"] == sha256Hex([]byte("two"))
	})

	// Rename: the new name arrives and the old one leaves.
	f.waitFor("a.txt to settle again", func() bool {
		return f.api.serverFiles()["a.txt"] == sha256Hex([]byte("two"))
	})
	time.Sleep(300 * time.Millisecond)
	f.write("b.txt", "two")
	f.waitUploaded("b.txt")
	f.remove("a.txt")
	f.waitDeleted("a.txt")

	// Delete.
	f.remove("b.txt")
	f.waitDeleted("b.txt")
}

// TestDebounceCoalescesBursts: twenty rapid writes to one path must reach the
// project as one upload, not twenty.
//
// It is about the watcher's debounce, so the burst starts only once the
// start-up walk has sent its manifest: the watches are installed before that,
// and the burst reaches the engine through live events alone. Started any
// earlier, the burst raced the engine's start-up, and which path saw the file
// (the walk, the watcher, or both) changed from run to run. A walk that lands
// in the middle of a write is TestStartCatchUpWaitsForAStableWrite's subject.
func TestDebounceCoalescesBursts(t *testing.T) {
	api := newFakeAPI()
	f := newFixture(t, api, nil)
	f.waitManifests(1)
	for i := 0; i < 20; i++ {
		f.write("burst.txt", fmt.Sprintf("write %d", i))
	}
	f.waitUploaded("burst.txt")
	f.waitSettled()
	time.Sleep(400 * time.Millisecond)

	ups := f.api.uploadedPaths()
	var n int
	for _, u := range ups {
		if u == "burst.txt" {
			n++
		}
	}
	if n != 1 {
		t.Fatalf("a burst of 20 writes produced %d uploads (uploads: %v)", n, ups)
	}
	if got := f.api.serverFiles()["burst.txt"]; got != sha256Hex([]byte("write 19")) {
		t.Fatalf("the project holds %q, not the final write", got)
	}
}

// writeLog is every state a test's writer left a file in: the content's hash
// and the mtime the filesystem stamped on it. It is how a stability test tells
// an engine that uploaded a file mid-write from a writer that simply stalled.
//
// A test that asserts "nothing was uploaded while the writer was still going"
// is really asserting that the writer never paused for Timing.Quiet plus
// Timing.Stable, and a loaded CI machine can stall a goroutine that long. The
// engine is right to upload a file that sat still for Timing.Stable, however
// that came about. So the assertion is made against the filesystem instead:
// a state the engine uploaded must have stayed on disk, by its own mtimes, for
// at least Timing.Stable before the next write replaced it. A state that was
// replaced 30 or 60 ms later, as the writer's normal pace replaces them, was
// uploaded while the file was still moving; a stall makes the gap long, and
// the upload legitimate, rather than failing the test.
type writeLog struct {
	mu      sync.Mutex
	content []byte
	states  []fileState
}

type fileState struct {
	sha   string
	mtime time.Time
}

// write appends chunk to fh and records the state it leaves the file in. It
// returns an error rather than failing the test, so a writer goroutine can use
// it.
func (w *writeLog) write(fh *os.File, chunk string) error {
	if _, err := fh.WriteString(chunk); err != nil {
		return err
	}
	if err := fh.Sync(); err != nil {
		return err
	}
	// Only this writer touches the file, so this is the mtime of the state
	// just written, however long after the write the Stat runs.
	info, err := fh.Stat()
	if err != nil {
		return err
	}
	w.mu.Lock()
	defer w.mu.Unlock()
	w.content = append(w.content, chunk...)
	w.states = append(w.states, fileState{sha: sha256Hex(w.content), mtime: info.ModTime()})
	return nil
}

// final is the content the writer left the file with.
func (w *writeLog) final() []byte {
	w.mu.Lock()
	defer w.mu.Unlock()
	return append([]byte(nil), w.content...)
}

// mtimeSlack allows for timestamp granularity: the filesystem stamps a write
// from a coarse clock that can trail the engine's own clock by a tick.
const mtimeSlack = 20 * time.Millisecond

// assertOnlyStillStatesUploaded fails the test when the project was sent a
// version of path that the writer replaced less than Timing.Stable after
// writing it, or one the writer never left the file in at all (a torn read).
func (w *writeLog) assertOnlyStillStatesUploaded(t *testing.T, api *fakeAPI, path string) {
	t.Helper()
	w.mu.Lock()
	states := append([]fileState(nil), w.states...)
	w.mu.Unlock()
	api.mu.Lock()
	uploads := append([]deviceapi.UploadRequest(nil), api.uploads...)
	api.mu.Unlock()
	stable := testTiming().Stable
	for _, u := range uploads {
		if u.Path != path {
			continue
		}
		i := -1
		for j, s := range states {
			if s.sha == u.SHA256 {
				i = j
			}
		}
		if i < 0 {
			t.Fatalf("%s was uploaded with content the writer never left it in", path)
		}
		if i == len(states)-1 {
			continue // the finished file
		}
		if still := states[i+1].mtime.Sub(states[i].mtime); still < stable-mtimeSlack {
			t.Fatalf("%s was uploaded as write %d of %d, which the next write replaced %s later: "+
				"the file was still being written (it must be still for %s first)",
				path, i+1, len(states), still, stable)
		}
	}
}

// TestWaitsForAStableWrite: a file that is still growing must not be uploaded
// until it stops. The test writes, keeps writing past the quiet window, and
// asserts that nothing it wrote while it was still moving was uploaded.
func TestWaitsForAStableWrite(t *testing.T) {
	api := newFakeAPI()
	f := newFixture(t, api, nil)
	full := filepath.Join(f.root, "growing.bin")
	// Let the engine's own start-up catch-up finish first, so this test is
	// about a file that MOVES, not about one that appears during the first scan
	// (TestStartCatchUpWaitsForAStableWrite covers that one).
	f.waitManifests(1)
	fh, err := os.Create(full)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = fh.Close() }()
	var w writeLog
	if err := w.write(fh, "part one"); err != nil {
		t.Fatal(err)
	}
	// Keep moving the file for longer than one quiet window.
	for i := 0; i < 4; i++ {
		time.Sleep(60 * time.Millisecond)
		if err := w.write(fh, "-more"); err != nil {
			t.Fatal(err)
		}
	}
	// Now let it settle.
	if err := w.write(fh, "-done"); err != nil {
		t.Fatal(err)
	}
	if err := fh.Close(); err != nil {
		t.Fatal(err)
	}
	if want := []byte("part one-more-more-more-more-done"); string(w.final()) != string(want) {
		t.Fatalf("the writer left %q", w.final())
	}
	f.waitFor("the finished file to be uploaded", func() bool {
		return api.serverFiles()["growing.bin"] == sha256Hex(w.final())
	})
	w.assertOnlyStillStatesUploaded(t, api, "growing.bin")
}

// TestStartCatchUpWaitsForAStableWrite: a file still being written when the
// engine starts is found by the start-up walk, and the server asks for it, but
// it is not uploaded as the half-written version the walk happened to hash:
// it goes through the same stability wait as a file the watcher reported.
func TestStartCatchUpWaitsForAStableWrite(t *testing.T) {
	api := newFakeAPI()
	var w writeLog
	stop := make(chan struct{})
	stopWriter := sync.OnceFunc(func() { close(stop) })
	writerDone := make(chan error, 1)
	writerExited := make(chan struct{})
	f := newFixture(t, api, func(o *Options, _ *Timing) {
		root := writeBeforeStart(t, o, nil)
		fh, err := os.Create(filepath.Join(root, "growing.bin"))
		if err != nil {
			t.Fatal(err)
		}
		if err := w.write(fh, "part one"); err != nil {
			t.Fatal(err)
		}
		// A test that fails before it stops the writer still stops it, and
		// waits for it to close the file. Registered here, after newFixture
		// made the temp directory, it runs before that directory is removed:
		// cleanups run last-registered first, and a writer still appending
		// would race the removal.
		t.Cleanup(func() {
			stopWriter()
			<-writerExited
		})
		// The writer starts before the engine does, so the walk always finds
		// the file moving, however long the engine takes to get to it.
		go func() {
			defer close(writerExited)
			tick := time.NewTicker(30 * time.Millisecond)
			defer tick.Stop()
			for {
				select {
				case <-stop:
					err := w.write(fh, "-done")
					if cerr := fh.Close(); err == nil {
						err = cerr
					}
					writerDone <- err
					return
				case <-tick.C:
					if err := w.write(fh, "-more"); err != nil {
						_ = fh.Close()
						writerDone <- err
						return
					}
				}
			}
		}()
	})
	// The walk has hashed the file and the server has asked for it.
	f.waitManifests(1)
	// Keep the file moving for two stability windows past that point: an
	// upload straight off the manifest happens well inside them.
	time.Sleep(2 * testTiming().Stable)
	stopWriter()
	if err := <-writerDone; err != nil {
		t.Fatal(err)
	}
	f.waitFor("the finished file to be uploaded", func() bool {
		return api.serverFiles()["growing.bin"] == sha256Hex(w.final())
	})
	f.waitSettled()
	w.assertOnlyStillStatesUploaded(t, api, "growing.bin")
}

// runner returns the link's runner, failing the test when there is none.
func (f *fixture) runner() *runner {
	f.t.Helper()
	f.engine.mu.Lock()
	r := f.engine.runners[f.linkID]
	f.engine.mu.Unlock()
	if r == nil {
		f.t.Fatal("the link has no runner")
	}
	return r
}

// waitWaiting waits until a path is in the runner's stability wait: seen, and
// not yet uploaded because it has not been still for Timing.Stable.
func (f *fixture) waitWaiting(path string) {
	f.t.Helper()
	f.waitFor(path+" to wait for a stable write", func() bool {
		f.engine.mu.Lock()
		r := f.engine.runners[f.linkID]
		f.engine.mu.Unlock()
		if r == nil {
			return false
		}
		r.mu.Lock()
		defer r.mu.Unlock()
		_, ok := r.waiting[path]
		return ok
	})
}

// assertNeverUploaded fails the test when the project was ever sent a path.
func (f *fixture) assertNeverUploaded(path string) {
	f.t.Helper()
	for _, u := range f.api.uploadedPaths() {
		if u == path {
			f.t.Fatalf("%s was uploaded after it was deleted", path)
		}
	}
	if _, ok := f.api.serverFiles()[path]; ok {
		f.t.Fatalf("the project holds %s after it was deleted", path)
	}
}

// slowStable gives a test a stability window long enough to act inside it,
// however loaded the machine is: a file is uploaded once it has been still for
// this long, so a deletion that must land first needs room.
func slowStable(_ *Options, t *Timing) { t.Stable = 2 * time.Second }

// TestFileDeletedWhileDeferredAtStartSettles: a file the start-up walk finds
// still moving waits for a stable write rather than being uploaded off the
// manifest. Deleted during that wait, it must leave the wait too: the link
// reads "Up to date", not "Syncing 1 file" until the runner restarts, and
// nothing is uploaded.
func TestFileDeletedWhileDeferredAtStartSettles(t *testing.T) {
	api := newFakeAPI()
	f := newFixture(t, api, func(o *Options, timing *Timing) {
		slowStable(o, timing)
		root := writeBeforeStart(t, o, map[string]string{"fresh.bin": "fresh"})
		// An mtime in the future always reads as modified less than
		// Timing.Stable ago, so the walk defers the file however long the
		// engine takes to get to it.
		ahead := time.Now().Add(time.Hour)
		if err := os.Chtimes(filepath.Join(root, "fresh.bin"), ahead, ahead); err != nil {
			t.Fatal(err)
		}
	})
	f.waitManifests(1)
	f.waitWaiting("fresh.bin")
	f.remove("fresh.bin")
	f.waitSettled()
	f.assertNeverUploaded("fresh.bin")
}

// TestFileDeletedInsideTheStabilityWindowSettles: a file the watcher reported,
// deleted before it has been still for Timing.Stable, leaves the stability
// wait, so the link settles to "Up to date" with nothing uploaded. Written
// again later, it is picked up as any new file is.
func TestFileDeletedInsideTheStabilityWindowSettles(t *testing.T) {
	api := newFakeAPI()
	f := newFixture(t, api, slowStable)
	// Let the start-up walk finish, so the file reaches the engine as a live
	// event.
	f.waitManifests(1)
	f.write("brief.txt", "first draft")
	f.waitWaiting("brief.txt")
	f.remove("brief.txt")
	f.waitSettled()
	f.assertNeverUploaded("brief.txt")

	f.write("brief.txt", "second draft")
	f.waitFor("the rewritten file to be uploaded", func() bool {
		return api.serverFiles()["brief.txt"] == sha256Hex([]byte("second draft"))
	})
	f.waitSettled()
}

// TestRescanWatchesAFolderTheWatcherMissed: a folder that has no watch (one
// created between the start-up walk and its parent's watch produced no event)
// is watched again after the next full rescan, so a change inside it is
// noticed live rather than at the rescan after that.
func TestRescanWatchesAFolderTheWatcherMissed(t *testing.T) {
	api := newFakeAPI()
	f := newFixture(t, api, func(o *Options, _ *Timing) {
		writeBeforeStart(t, o, map[string]string{"sub/first.txt": "first"})
	})
	f.waitUploaded("sub/first.txt")

	r := f.runner()
	// The window that loses a watch is microseconds wide; taking the watch
	// away leaves the runner in exactly the state it produces.
	r.dropWatch("sub")

	seen := len(api.manifestBatches())
	f.engine.Sync(f.linkID)
	f.waitManifests(seen + 1)

	// The rescan is over and the next one is minutes away: only a watch on
	// sub/ can bring this file in.
	f.write("sub/late.txt", "late")
	f.waitUploaded("sub/late.txt")
}

// TestRewriteDuringUploadIsUploadedToo: a file written again, at the same size,
// while its previous content was uploading reaches the project once it settles.
// The index has to remember the state that was HASHED: an entry that took the
// mtime seen after the upload would call the new content already synced, and
// the rewrite would never go up.
func TestRewriteDuringUploadIsUploadedToo(t *testing.T) {
	api := newFakeAPI()
	f := newFixture(t, api, nil)
	f.waitManifests(1)
	var once sync.Once
	api.mu.Lock()
	api.onPut = func(path string) {
		if path != "doc.txt" {
			return
		}
		once.Do(func() {
			full := filepath.Join(f.root, "doc.txt")
			if err := os.WriteFile(full, []byte("second"), 0o644); err != nil {
				t.Error(err)
				return
			}
			// A new mtime, so the change is one the index can see on any
			// filesystem, however coarse its timestamps.
			later := time.Now().Add(time.Second)
			if err := os.Chtimes(full, later, later); err != nil {
				t.Error(err)
			}
		})
	}
	api.mu.Unlock()

	f.write("doc.txt", "first!") // the same size as "second"
	f.waitFor("the rewrite to reach the project", func() bool {
		return api.serverFiles()["doc.txt"] == sha256Hex([]byte("second"))
	})
}

// TestStartCatchesUp: files that were already there when the engine started
// reach the project through the manifest and upload path, not through an event.
func TestStartCatchesUp(t *testing.T) {
	api := newFakeAPI()
	api.setSeed("old.txt", sha256Hex([]byte("old")))
	// The files are written before the engine starts, as the test's premise
	// says. Written after, they race the initial scan: a write the scan misses
	// arrives as a live event and is uploaded, which is not what this pins.
	f := newFixture(t, api, func(o *Options, _ *Timing) {
		writeBeforeStart(t, o, map[string]string{
			"old.txt":     "old", // the project already has this content, at this path
			"sub/new.txt": "new",
		})
	})
	f.waitUploaded("sub/new.txt")
	f.waitSettled()

	for _, p := range api.uploadedPaths() {
		if p == "old.txt" {
			t.Fatal("a file the project already had was uploaded again")
		}
	}
}

// TestManifestReconcilesDeletions: a file removed while the app was off leaves
// the project when the app starts, because the manifest diff says so.
//
// The SERVER applies that deletion: the client's delete route exists for a
// removal the watch observed live, so what is asserted here is the manifest
// answer itself.
func TestManifestReconcilesDeletions(t *testing.T) {
	api := newFakeAPI()
	api.setSeed("gone.txt", sha256Hex([]byte("gone")))
	f := newFixture(t, api, nil)
	f.waitFor("the manifest to ask for the deletion", func() bool {
		for _, p := range api.reconciledPaths() {
			if p == "gone.txt" {
				return true
			}
		}
		return false
	})
	f.waitSettled()
}

// TestMassDeleteGuardPausesAndResumeConfirms walks the guard's whole story:
// an empty manifest pauses the link, the status says so and offers the
// confirmation, and Resume sends confirmLargeDelete once.
func TestMassDeleteGuardPausesAndResumeConfirms(t *testing.T) {
	api := newFakeAPI()
	api.guardPause = true
	api.setSeed("one.txt", sha256Hex([]byte("one")))
	api.setSeed("two.txt", sha256Hex([]byte("two")))
	f := newFixture(t, api, nil)

	f.waitFor("the guard to pause the link", func() bool {
		l, ok := f.find()
		return ok && l.State == StatePaused && l.PausedBy == PausedByGuard
	})
	l := f.link()
	if !l.CanConfirmDelete {
		t.Fatal("the guard's pause did not offer the confirmation")
	}
	if !strings.Contains(strings.ToLower(l.Reason), "resume") {
		t.Fatalf("the guard's reason does not say what to do: %q", l.Reason)
	}
	if got := api.deletedPaths(); len(got) != 0 {
		t.Fatalf("the guard deleted %v", got)
	}

	// Resume with the confirmation. The guard is standing down at the same time
	// (in ReelBolt it is the same act: the link is resumed, and the person says
	// the deletions are real), so the next final manifest carries the flag —
	// once — and the deletions apply.
	api.setGuardPause(false)
	f.resume(true)
	f.waitFor("the deletions to apply after the confirmation", func() bool {
		_, a := api.serverFiles()["one.txt"]
		_, b := api.serverFiles()["two.txt"]
		return !a && !b
	})
	seen := false
	for _, b := range api.manifestBatches() {
		if b.ConfirmLargeDelete {
			if seen {
				t.Fatal("confirmLargeDelete was sent more than once")
			}
			seen = true
			if !b.Final {
				t.Fatal("confirmLargeDelete arrived on a non-final batch")
			}
		}
	}
	if links, err := f.store.Load(); err != nil {
		t.Fatal(err)
	} else if len(links) != 1 || links[0].ConfirmLargeDelete {
		t.Fatalf("the confirmation flag was not cleared from the store: %+v", links)
	}
}

// TestUnauthorizedStopsWithTheReconnectMessage: a 401 stops syncing and says
// pair it again, rather than retrying forever.
func TestUnauthorizedStopsWithTheReconnectMessage(t *testing.T) {
	api := newFakeAPI()
	api.unauthorized = true
	f := newFixture(t, api, nil)
	f.waitStarted()
	f.write("a.txt", "x")

	f.waitFor("the engine to report the refused device", func() bool {
		s := f.status()
		return s.Problem != "" && strings.Contains(s.Problem, "Pair it again")
	})
	// The link must stop, not spin.
	time.Sleep(200 * time.Millisecond)
	before := len(api.manifestBatches())
	time.Sleep(200 * time.Millisecond)
	if after := len(api.manifestBatches()); after != before {
		t.Fatalf("the engine kept asking after a 401: %d -> %d manifests", before, after)
	}
	if got := f.status().Summary(); !strings.Contains(got, "Pair it again") {
		t.Fatalf("the tray line does not say to pair again: %q", got)
	}
}

// TestStorageFullIsReported: a 413 pauses the file with the design's sentence.
func TestStorageFullIsReported(t *testing.T) {
	api := newFakeAPI()
	api.errUpload["big.bin"] = &deviceapi.Error{
		Method: "POST", Route: "/uploads", Status: 413, Code: "STORAGE_QUOTA_EXCEEDED",
		UsedBytes: int64p(15000000000), LimitBytes: int64p(15000000000),
	}
	f := newFixture(t, api, nil)
	f.waitStarted()
	f.write("big.bin", strings.Repeat("x", 64))

	f.waitFor("the storage-full sentence", func() bool {
		l, ok := f.find()
		return ok && strings.Contains(l.Reason, "Storage is full (15.0 GB of 15.0 GB used)")
	})
	if f.link().State != StateAttention {
		t.Fatalf("state = %s, want attention", f.link().State)
	}
}

// TestLinkPausedOnTheServerIsReportedAsSuch: a 409 link_paused is the
// dashboard's pause, and the message says to resume it there.
func TestLinkPausedOnTheServerIsReportedAsSuch(t *testing.T) {
	api := newFakeAPI()
	api.pauseOnCall = true
	api.pauseReason = "user"
	f := newFixture(t, api, nil)
	f.waitStarted()
	f.write("a.txt", "x")

	f.waitFor("the dashboard pause", func() bool {
		l, ok := f.find()
		return ok && l.State == StatePaused && l.PausedBy == PausedByDashboard
	})
	if l := f.link(); l.CanConfirmDelete {
		t.Fatal("a dashboard pause offered a Resume this computer cannot perform")
	}
	if got := f.link().Reason; !strings.Contains(got, "Resume it there") {
		t.Fatalf("reason = %q", got)
	}
}

// TestLocalPauseStopsTheWatcherAndResumeRestartsIt: pause is local, and nothing
// reaches the project while it is paused.
func TestLocalPauseStopsTheWatcherAndResumeRestartsIt(t *testing.T) {
	api := newFakeAPI()
	f := newFixture(t, api, nil)
	f.waitStarted()
	f.write("first.txt", "one")
	f.waitUploaded("first.txt")
	f.waitSettled()
	time.Sleep(300 * time.Millisecond)

	f.pause()
	f.waitFor("the link to read as paused", func() bool {
		l, ok := f.find()
		return ok && l.State == StatePaused
	})
	f.write("while-paused.txt", "two")
	time.Sleep(500 * time.Millisecond)
	if _, ok := api.serverFiles()["while-paused.txt"]; ok {
		t.Fatal("a file was uploaded while the link was paused")
	}

	// Resume starts a new runner, whose start-up walk would race the write.
	seen := len(api.manifestBatches())
	f.resume(false)
	f.waitManifests(seen + 1)
	f.write("after-resume.txt", "three")
	f.waitUploaded("after-resume.txt")
}

// TestUploadFailureIsReportedAndRecovered: a file that fails once is reported,
// and the next reconciliation still sends it.
func TestUploadFailureIsReportedAndRecovered(t *testing.T) {
	api := newFakeAPI()
	api.failPath["flaky.txt"] = 1
	f := newFixture(t, api, nil)
	f.waitStarted()
	f.write("flaky.txt", "content")

	f.waitFor("the failure to be reported", func() bool {
		l, ok := f.find()
		return ok && l.State == StateAttention && l.Reason != ""
	})
	// The periodic reconciliation retries it; nudge it the way a wake would.
	f.engine.Sync(f.linkID)
	f.waitUploaded("flaky.txt")
	f.waitSettled()
}

// TestUnchangedAnswerStillMarksSynced: a 409 unchanged means the project
// already has the content, so the file is not sent and the index records it.
func TestUnchangedAnswerStillMarksSynced(t *testing.T) {
	api := newFakeAPI()
	api.unchangedOn["same.txt"] = true
	f := newFixture(t, api, nil)
	f.waitStarted()
	f.write("same.txt", "identical")
	f.waitFor("the unchanged answer to settle the file", func() bool {
		l, ok := f.find()
		return ok && l.State == StateUpToDate && l.FileCount == 1
	})
	// Nothing was PUT: the server said it already had the content.
	for _, u := range api.putURLs() {
		if strings.Contains(u, "same.txt") {
			t.Fatal("the content was PUT after the server said unchanged")
		}
	}
}

func int64p(n int64) *int64 { return &n }

// TestMultipartUploadRetriesAFailedPart: a part refused by storage is retried
// with backoff, and the completed-parts list is what the server is told.
//
// The engine is deliberately NOT started here: a test of the part mechanics
// must not race a running sync of the same folder for the same file.
func TestMultipartUploadRetriesAFailedPart(t *testing.T) {
	api := newFakeAPI()
	api.failPath["part:2"] = 1

	dir := t.TempDir()
	store := OpenStore(filepath.Join(dir, "folders.json"))
	link := Link{LinkID: "link-1", LocalPath: dir, ProjectID: "proj-1", TargetFolder: "Footage"}
	if err := store.Add(link); err != nil {
		t.Fatal(err)
	}
	timing := testTiming()
	timing.PartBackoff = time.Millisecond
	timing.PartRetries = 3
	e, err := New(Options{Store: store, API: api, IndexDir: filepath.Join(dir, "index"), Logger: testLogger{t}, Timing: timing})
	if err != nil {
		t.Fatal(err)
	}
	r := newRunner(e, link)

	content := strings.Repeat("A", 250) + strings.Repeat("B", 250) + strings.Repeat("C", 250) + strings.Repeat("D", 250)
	full := filepath.Join(dir, "big.bin")
	if err := os.WriteFile(full, []byte(content), 0o644); err != nil {
		t.Fatal(err)
	}
	api.mu.Lock()
	api.open["u-1"] = deviceapi.UploadRequest{Path: "big.bin", Size: int64(len(content)), SHA256: sha256Hex([]byte(content))}
	api.mu.Unlock()

	ticket := deviceapi.Ticket{
		Mode: deviceapi.ModeMultipart, UploadID: "u-1", SizeBytes: int64(len(content)),
		PartSizeBytes: 250, PartCount: 4,
	}
	parts, err := r.putParts(context.Background(), "big.bin", ticket, int64(len(content)))
	if err != nil {
		t.Fatalf("putParts: %v", err)
	}
	if len(parts) != 4 {
		t.Fatalf("%d completed parts, want 4", len(parts))
	}
	for i, p := range parts {
		if p.PartNumber != i+1 || p.ETag == "" {
			t.Fatalf("part %d = %+v", i, p)
		}
	}
	api.mu.Lock()
	attempts := api.partAttempts[2]
	remaining := api.failPath["part:2"]
	api.mu.Unlock()
	if attempts != 2 {
		t.Fatalf("part 2 was attempted %d times, want 2 (one refusal, one retry)", attempts)
	}
	if remaining != 0 {
		t.Fatalf("the canned refusal was not consumed: %d left", remaining)
	}
	if got := api.putBytes(); string(got) != content {
		t.Fatalf("the parts do not reassemble the file: %d bytes, want %d", len(got), len(content))
	}
}

// ---- SBX-06: a path the server names is held to the walk's own rules ----

// namingAPI is the fake server answering a manifest with extra upload paths the
// manifest never described, which is what a hostile or compromised server can do.
type namingAPI struct {
	*fakeAPI
	extra []string
	// during runs inside the Manifest call, after the walk and before the
	// runner acts on the answer.
	during func()
}

func (n *namingAPI) Manifest(ctx context.Context, linkID string, b deviceapi.ManifestBatch) (deviceapi.ManifestResult, error) {
	if n.during != nil {
		n.during()
	}
	out, err := n.fakeAPI.Manifest(ctx, linkID, b)
	out.Upload = append(out.Upload, n.extra...)
	return out, err
}

// unstartedRunner is a runner over a fresh linked folder whose engine is not
// running, so nothing but the call under test touches the folder.
func unstartedRunner(t *testing.T, api API) (*runner, string) {
	t.Helper()
	dir := t.TempDir()
	root := filepath.Join(dir, "Footage")
	if err := os.MkdirAll(root, 0o755); err != nil {
		t.Fatal(err)
	}
	store := OpenStore(filepath.Join(dir, "folders.json"))
	link := Link{LinkID: "link-1", LocalPath: root, ProjectID: "proj-1", TargetFolder: "Footage"}
	if err := store.Add(link); err != nil {
		t.Fatal(err)
	}
	e, err := New(Options{Store: store, API: api, IndexDir: filepath.Join(dir, "index"), Logger: testLogger{t}, Timing: testTiming()})
	if err != nil {
		t.Fatal(err)
	}
	return newRunner(e, link), root
}

// writeSettled writes a file whose mtime is old enough to be past the stability
// wait, so an upload decision is made now rather than deferred.
func writeSettled(t *testing.T, full, body string) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(full, []byte(body), 0o644); err != nil {
		t.Fatal(err)
	}
	old := time.Now().Add(-time.Hour)
	if err := os.Chtimes(full, old, old); err != nil {
		t.Fatal(err)
	}
}

// TestManifestAnswerUploadsOnlyDescribedPaths: the server's upload list is
// honoured only for paths the manifest itself described. A file in a hidden
// folder, which the walk never reports, is not uploaded because the server
// asked for it.
func TestManifestAnswerUploadsOnlyDescribedPaths(t *testing.T) {
	fake := newFakeAPI()
	api := &namingAPI{fakeAPI: fake, extra: []string{".git/config", "undescribed.txt"}}
	r, root := unstartedRunner(t, api)
	writeSettled(t, filepath.Join(root, "clip.txt"), "clip")
	writeSettled(t, filepath.Join(root, ".git", "config"), "secret")

	r.reconcileNow(context.Background())

	got := fake.uploadedPaths()
	if len(got) != 1 || got[0] != "clip.txt" {
		t.Fatalf("uploaded %v, want only clip.txt", got)
	}
}

// TestUploadRefusesAPathUnderAHiddenFolder: the upload applies the walk's rule
// for folders it does not enter, whoever named the path.
func TestUploadRefusesAPathUnderAHiddenFolder(t *testing.T) {
	fake := newFakeAPI()
	r, root := unstartedRunner(t, fake)
	writeSettled(t, filepath.Join(root, ".ssh", "id_rsa"), "key")
	writeSettled(t, filepath.Join(root, "sub", "ok.txt"), "ok")

	r.uploadIfHeld(context.Background(), ".ssh/id_rsa", "")
	r.uploadIfHeld(context.Background(), "sub/ok.txt", "")

	got := fake.uploadedPaths()
	if len(got) != 1 || got[0] != "sub/ok.txt" {
		t.Fatalf("uploaded %v, want only sub/ok.txt", got)
	}
}

// TestUploadDoesNotFollowASymlinkedFolderOutOfTheLink: a folder inside the
// linked folder that is a symlink to somewhere else is not a way to upload what
// is behind it.
func TestUploadDoesNotFollowASymlinkedFolderOutOfTheLink(t *testing.T) {
	fake := newFakeAPI()
	r, root := unstartedRunner(t, fake)
	outside := t.TempDir()
	writeSettled(t, filepath.Join(outside, "secret.txt"), "secret")
	if err := os.Symlink(outside, filepath.Join(root, "escape")); err != nil {
		t.Skipf("symlinks are not available here: %v", err)
	}

	r.uploadIfHeld(context.Background(), "escape/secret.txt", "")

	if got := fake.uploadedPaths(); len(got) != 0 {
		t.Fatalf("uploaded %v through a symlinked folder", got)
	}
	if _, _, _, err := hashFile(root, "escape/secret.txt"); err == nil {
		t.Fatal("hashFile read a file through a symlinked folder out of the link")
	}
	if f, err := openInFolder(root, "escape/secret.txt"); err == nil {
		_ = f.Close()
		t.Fatal("openInFolder opened a file through a symlinked folder out of the link")
	}
}

// TestManifestAnswerSkipsAFileCreatedAfterTheWalk: a settled, visible file
// that appears while the manifest is in flight is on disk when the answer
// names it, but the manifest did not describe it, so it is not uploaded.
func TestManifestAnswerSkipsAFileCreatedAfterTheWalk(t *testing.T) {
	fake := newFakeAPI()
	api := &namingAPI{fakeAPI: fake, extra: []string{"late.txt"}}
	r, root := unstartedRunner(t, api)
	writeSettled(t, filepath.Join(root, "clip.txt"), "clip")
	api.during = func() { writeSettled(t, filepath.Join(root, "late.txt"), "late") }

	r.reconcileNow(context.Background())

	got := fake.uploadedPaths()
	if len(got) != 1 || got[0] != "clip.txt" {
		t.Fatalf("uploaded %v, want only clip.txt", got)
	}
}

// TestReconcileStableCheckStaysInsideTheFolder: a folder the walk described is
// swapped for a symlink out of the link while the manifest is in flight. The
// stability check looks through the folder's os.Root, so the fresh file behind
// the link is neither queued nor uploaded.
func TestReconcileStableCheckStaysInsideTheFolder(t *testing.T) {
	fake := newFakeAPI()
	api := &namingAPI{fakeAPI: fake}
	r, root := unstartedRunner(t, api)
	writeSettled(t, filepath.Join(root, "sub", "clip.txt"), "clip")
	outside := t.TempDir()
	if err := os.WriteFile(filepath.Join(outside, "clip.txt"), []byte("outside"), 0o644); err != nil {
		t.Fatal(err)
	}
	api.during = func() {
		if err := os.Rename(filepath.Join(root, "sub"), filepath.Join(t.TempDir(), "sub")); err != nil {
			t.Fatal(err)
		}
		if err := os.Symlink(outside, filepath.Join(root, "sub")); err != nil {
			t.Skipf("symlinks are not available here: %v", err)
		}
	}

	r.reconcileNow(context.Background())

	r.mu.Lock()
	_, queued := r.waiting["sub/clip.txt"]
	r.mu.Unlock()
	if queued {
		t.Fatal("the stability check followed a symlinked folder out of the link")
	}
	if got := fake.uploadedPaths(); len(got) != 0 {
		t.Fatalf("uploaded %v through a symlinked folder", got)
	}
}
