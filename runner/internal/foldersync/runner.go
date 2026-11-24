package foldersync

import (
	"context"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"sync"
	"time"

	"github.com/fsnotify/fsnotify"

	"github.com/vecchiotom/reelbolt/runner/internal/deviceapi"
)

// runner syncs one link. It owns its folder's watcher, its local index and the
// state the status document reads.
//
// One runner per link, never two: two watchers on one folder would race each
// other into the project, which is why LinkStore.Add refuses a second link for
// the same path.
type runner struct {
	e    *Engine
	link Link
	ix   *Index

	ctx    context.Context
	cancel context.CancelFunc
	wg     sync.WaitGroup
	// watchMu guards the watcher and the set of watched directories. It is
	// separate from mu because the fsnotify goroutine takes it while the
	// status path may be holding mu.
	watchMu sync.Mutex
	watcher *fsnotify.Watcher
	dirs    map[string]struct{}

	// mu guards everything below.
	mu sync.Mutex
	// pending is a path that moved, and when it last moved. A path is looked at
	// once it has been quiet for Timing.Quiet.
	pending map[string]time.Time
	// waiting is a file seen at a path, holding the mtime it must keep for
	// Timing.Stable before it is uploaded. A file still being written is never
	// uploaded half-done.
	waiting map[string]int64
	// due is when the waiting files may next be re-checked.
	due time.Time

	syncing    bool
	lastSync   time.Time
	lastChange time.Time
	polling    bool
	note       string
	// problem is what "Needs attention" shows. It is cleared when the link
	// makes progress again.
	problem string
	// pausedByGuard is true when the mass-delete guard paused this link on the
	// server; Resume then confirms the deletions.
	pausedByGuard bool
	// pausedByDashboard is true when a person paused this link from the
	// ReelBolt dashboard. It is not this computer's pause and it is not the
	// guard's: the resume happens in the dashboard, so the window offers no
	// button for it.
	pausedByDashboard bool
	// skipped is reason -> paths.
	skipped map[string]map[string]struct{}
	// storage is the last storage answer the server gave.
	storage *deviceapi.Storage

	// scanNow asks for a full rescan (start, wake, reconnect).
	scanNow chan struct{}
}

func newRunner(e *Engine, l Link) *runner {
	return &runner{
		e:       e,
		link:    l,
		ix:      OpenIndex(IndexPath(e.opts.IndexDir, l.LinkID)),
		pending: map[string]time.Time{},
		waiting: map[string]int64{},
		skipped: map[string]map[string]struct{}{},
		dirs:    map[string]struct{}{},
		scanNow: make(chan struct{}, 1),
	}
}

// start launches the runner's goroutine. ctx is the engine's context.
func (r *runner) start(ctx context.Context) {
	ctx, cancel := context.WithCancel(ctx)
	r.ctx = ctx
	r.cancel = cancel
	r.wg.Add(1)
	go func() {
		defer r.wg.Done()
		r.run(ctx)
	}()
}

// stop ends the runner and waits for every goroutine it started. A second call
// is a no-op, and it is safe to call from any goroutine except the runner's own.
func (r *runner) stop() {
	if r.cancel != nil {
		r.cancel()
	}
	r.wg.Wait()
	r.saveIndex()
}

// adopt replaces the link this runner is following with a newer copy from the
// store. It is used for a change that does not need a restart, so the next
// manifest sees it (a confirmation the person just gave, most of all).
func (r *runner) adopt(l Link) {
	r.mu.Lock()
	r.link.ProjectName = l.ProjectName
	r.link.TargetFolder = l.TargetFolder
	r.link.Paused = l.Paused
	r.link.ConfirmLargeDelete = l.ConfirmLargeDelete
	r.mu.Unlock()
}

// syncNow asks for a full rescan at the next loop turn.
func (r *runner) syncNow() {
	select {
	case r.scanNow <- struct{}{}:
	default:
	}
}

// run is the runner's whole life: watch, debounce, rescan on a timer.
func (r *runner) run(ctx context.Context) {
	defer r.saveIndex()
	r.watchStart(ctx)
	r.reconcileNow(ctx)

	quiet := time.NewTicker(r.e.t.Tick)
	defer quiet.Stop()
	reconcile := time.NewTicker(r.e.t.Reconcile)
	defer reconcile.Stop()
	poll := time.NewTicker(r.e.t.PollEvery)
	defer poll.Stop()

	for {
		select {
		case <-ctx.Done():
			return
		case <-r.scanNow:
			r.reconcileNow(ctx)
		case <-quiet.C:
			r.tick(ctx)
		case <-reconcile.C:
			r.reconcileNow(ctx)
		case <-poll.C:
			if r.isPolling() {
				r.reconcileNow(ctx)
			}
		}
	}
}

// tick is one turn of the debounce loop: look at every quiet path, then at
// every file that has been still long enough.
func (r *runner) tick(ctx context.Context) {
	ready := r.takeQuiet()
	if len(ready) > 0 {
		r.syncingOn()
		for _, rel := range ready {
			r.apply(ctx, rel)
		}
		r.syncOff()
	}
	r.checkWaiting(ctx)
	_ = r.ix.Save()
}

// touch records that a path moved now.
func (r *runner) touch(rel string) {
	r.mu.Lock()
	r.pending[rel] = time.Now()
	r.mu.Unlock()
}

// takeQuiet returns and clears every path quiet for long enough.
func (r *runner) takeQuiet() []string {
	now := time.Now()
	r.mu.Lock()
	defer r.mu.Unlock()
	var ready []string
	for p, at := range r.pending {
		if now.Sub(at) >= r.e.t.Quiet {
			ready = append(ready, p)
			delete(r.pending, p)
		}
	}
	sort.Strings(ready)
	return ready
}

// apply handles one quiet path: a file is queued until it is stable; a
// directory is walked and its files queued; a path that no longer exists
// deletes its subtree from the project.
//
// It stats the path itself rather than scanning it, because scanTree's start is
// a DIRECTORY: walking a file would report no files and read exactly like a
// deletion.
func (r *runner) apply(ctx context.Context, rel string) {
	full := filepath.Join(r.link.LocalPath, filepath.FromSlash(rel))
	info, err := os.Lstat(full)
	if err != nil {
		// Gone. If this computer synced it, it leaves the project.
		if tracked := r.ix.Under(rel); len(tracked) > 0 {
			r.removePaths(ctx, tracked)
		}
		return
	}
	if reason := entryReason(filepath.Base(rel), info); reason != "" {
		r.recordSkip(reason, rel)
		return
	}
	if !info.IsDir() {
		r.queueStable(scannedFile{Path: rel, Size: info.Size(), MTime: info.ModTime().UnixNano()})
		return
	}
	res, err := scanTree(r.link.LocalPath, rel)
	if err != nil {
		return
	}
	r.recordSkipped(res)
	if len(res.Dirs) > r.e.t.MaxWatchDirs {
		r.setPolling(true)
	}
	for _, d := range res.Dirs {
		r.addWatch(d)
	}
	for _, f := range res.Files {
		r.queueStable(f)
	}
}

// queueStable waits until a file's size and mtime have not moved for
// Timing.Stable, then uploads it.
func (r *runner) queueStable(f scannedFile) {
	r.mu.Lock()
	r.waiting[f.Path] = f.MTime
	// Re-arm the deadline on EVERY change: a file that keeps being written must
	// push its own deadline out, or the second change would be looked at at the
	// first change's deadline and uploaded mid-write.
	r.due = time.Now().Add(r.e.t.Stable)
	r.mu.Unlock()
}

// checkWaiting uploads every waiting file whose mtime has not moved for
// Timing.Stable, and re-arms the deadline while anything is still moving.
func (r *runner) checkWaiting(ctx context.Context) {
	r.mu.Lock()
	if len(r.waiting) == 0 {
		r.mu.Unlock()
		return
	}
	if r.due.IsZero() {
		r.due = time.Now().Add(r.e.t.Stable)
	}
	if time.Now().Before(r.due) {
		r.mu.Unlock()
		return
	}
	waiting := make(map[string]int64, len(r.waiting))
	for k, v := range r.waiting {
		waiting[k] = v
	}
	r.due = time.Time{}
	r.mu.Unlock()

	moved := false
	for path, mtime := range waiting {
		full := filepath.Join(r.link.LocalPath, filepath.FromSlash(path))
		info, err := os.Lstat(full)
		if errors.Is(err, fs.ErrNotExist) || (err == nil && info.IsDir()) {
			// Gone (deleted, or renamed away), or no longer a file. It
			// leaves the wait, or the link would read "Syncing" until the
			// runner restarts. A file that vanished before it was ever
			// uploaded has nothing to delete; one that was, leaves the
			// project through its watcher event or the next rescan, and a
			// file that comes back is queued again by either.
			r.forgetWaiting(path, full)
			continue
		}
		if err != nil {
			// Present but unreadable for now: look again next time.
			continue
		}
		if info.ModTime().UnixNano() != mtime {
			// Still moving. Give it another quiet period.
			r.mu.Lock()
			r.waiting[path] = info.ModTime().UnixNano()
			r.mu.Unlock()
			moved = true
			continue
		}
		r.mu.Lock()
		delete(r.waiting, path)
		r.mu.Unlock()
		r.syncingOn()
		r.uploadOne(ctx, path)
		r.syncOff()
	}
	if moved {
		r.mu.Lock()
		// Never move the deadline backwards: a change that arrived while this
		// loop was running has already pushed it further out.
		if next := time.Now().Add(r.e.t.Stable); next.After(r.due) {
			r.due = next
		}
		r.mu.Unlock()
	}
}

// forgetWaiting drops a path from the stability wait once it is no longer a
// file there. It looks again under the lock, which queueStable also takes: a
// file that came back between checkWaiting's look and this one keeps its place,
// and one that comes back after this is queued afresh.
func (r *runner) forgetWaiting(path, full string) {
	r.mu.Lock()
	defer r.mu.Unlock()
	info, err := os.Lstat(full)
	if errors.Is(err, fs.ErrNotExist) || (err == nil && info.IsDir()) {
		delete(r.waiting, path)
	}
}

// uploadOne hashes a file (reusing the index when its size and mtime have not
// moved) and uploads it when the project does not already hold that content.
func (r *runner) uploadOne(ctx context.Context, rel string) {
	r.uploadIfHeld(ctx, rel, "")
}

// uploadIfHeld is uploadOne with a hash the caller already promised the project
// is missing.
//
// A manifest is built from hashes taken before it is sent, and the file can move
// in between. This re-checks the hash immediately before sending, so the bytes
// that go up are the bytes that were hashed: a file that changed is left to its
// own event rather than uploaded as a torn intermediate state.
//
// The path may come from the server's answer to a manifest, so it gets the
// rules the walk applies to what it finds: a path the server would not accept,
// or one under a folder the walk does not enter, is never opened, and the file
// is looked at and read through the linked folder's os.Root, so a folder on the
// way that is a symlink out of it leads nowhere.
func (r *runner) uploadIfHeld(ctx context.Context, rel, mustBe string) {
	if !validRelPath(rel) {
		r.recordSkip(ReasonName, rel)
		return
	}
	if hiddenAncestor(rel) {
		return
	}
	full := filepath.Join(r.link.LocalPath, filepath.FromSlash(rel))
	info, err := lstatInFolder(r.link.LocalPath, rel)
	if err != nil {
		return
	}
	if reason := entryReason(filepath.Base(rel), info); reason != "" {
		r.recordSkip(reason, rel)
		return
	}
	sum, err := r.hashIfSame(rel, full, info, mustBe)
	if err != nil {
		if errors.Is(err, errChanged) {
			r.queueStable(scannedFile{Path: rel, Size: info.Size(), MTime: info.ModTime().UnixNano()})
			return
		}
		r.recordSkip(ReasonUnreadable, rel)
		return
	}
	if e, ok := r.ix.Get(rel); ok && e.Synced == sum {
		return
	}
	if r.e.opts.API == nil {
		return
	}
	r.sendFile(ctx, rel, full, sum, info.Size())
}

// hashIfSame is hash, but a file whose hash is not mustBe answers errChanged:
// the caller's view of the file is stale, so sending it would upload bytes
// nobody hashed.
func (r *runner) hashIfSame(rel, full string, info fs.FileInfo, mustBe string) (string, error) {
	sum, err := r.hash(rel, full, info)
	if err != nil {
		return "", err
	}
	if mustBe != "" && sum != mustBe {
		return "", errChanged
	}
	return sum, nil
}

// hash returns a file's SHA-256, reusing an index entry whose size and mtime
// still match. That reuse is why the index exists: an unchanged file is never
// read again.
func (r *runner) hash(rel, full string, info fs.FileInfo) (string, error) {
	mtime := info.ModTime().UnixNano()
	if e, ok := r.ix.Get(rel); ok && e.Size == info.Size() && e.MTime == mtime && e.SHA256 != "" {
		return e.SHA256, nil
	}
	sum, size, mtime, err := hashFile(r.link.LocalPath, rel)
	if err != nil {
		return "", err
	}
	r.e.hashes.Add(1)
	r.ix.Put(rel, IndexEntry{Size: size, MTime: mtime, SHA256: sum, Synced: ""})
	return sum, nil
}

// sendFile opens an upload and pushes the bytes: one PUT, or parts in parallel.
func (r *runner) sendFile(ctx context.Context, rel, full, sum string, size int64) {
	t, err := r.e.opts.API.CreateUpload(ctx, r.link.LinkID, deviceapi.UploadRequest{
		Path: rel, Size: size, SHA256: sum,
	})
	switch {
	case err == nil:
	case errors.Is(err, deviceapi.ErrUnchanged):
		r.markSynced(rel, sum, size)
		return
	case errors.Is(err, deviceapi.ErrLinkPaused):
		r.pausedByServer("")
		return
	case errors.Is(err, deviceapi.ErrOccupied):
		r.recordSkip(ReasonOccupied, rel)
		return
	case errors.Is(err, deviceapi.ErrInvalidPath):
		r.recordSkip(ReasonName, rel)
		return
	case errors.Is(err, deviceapi.ErrStorageFull):
		r.storageFull(deviceapi.StorageSentence(err))
		return
	case errors.Is(err, deviceapi.ErrNotConnected):
		r.e.disconnect()
		return
	default:
		r.fail(err)
		return
	}

	var parts []deviceapi.CompletedPart
	if t.Mode == deviceapi.ModeMultipart {
		parts, err = r.putParts(ctx, rel, t, size)
	} else {
		err = r.putSingle(ctx, rel, t, size)
	}
	if err != nil {
		// Abort with a context that is not cancelled: the whole point is to
		// tell the server to drop what it already opened.
		abortCtx, cancel := context.WithTimeout(context.WithoutCancel(ctx), 15*time.Second)
		_ = r.e.opts.API.AbortUpload(abortCtx, r.link.LinkID, t)
		cancel()
		if errors.Is(err, deviceapi.ErrNotConnected) {
			r.e.disconnect()
			return
		}
		r.fail(err)
		return
	}

	// A single PUT is complete the moment storage answers with 2xx: there is no
	// multipart upload to close, and the server's "complete" route is for a
	// multipart one. Only the parts flow calls it.
	if t.Mode == deviceapi.ModeMultipart {
		if _, err := r.e.opts.API.CompleteUpload(ctx, r.link.LinkID, t, parts); err != nil {
			switch {
			case errors.Is(err, deviceapi.ErrNotConnected):
				r.e.disconnect()
			case errors.Is(err, deviceapi.ErrLinkPaused):
				r.pausedByServer("")
			default:
				r.fail(err)
			}
			return
		}
	}
	r.markSynced(rel, sum, size)
}

// markSynced records that the project now holds sum for rel.
//
// The entry keeps the size and mtime the file had when sum was HASHED, never
// the ones it has now: the file can be written again while it uploads, and an
// entry pairing that newer mtime with the older hash would vouch for content
// nobody read. The newer write's own event would then find its size and mtime
// "unchanged", reuse the old hash, see it already synced, and never upload the
// file again. When the hashed state is not in the index the mtime is left
// unknown, so the next look reads the file.
func (r *runner) markSynced(rel, sum string, size int64) {
	e, ok := r.ix.Get(rel)
	if !ok || e.SHA256 != sum {
		e = IndexEntry{Size: size, SHA256: sum}
	}
	e.Synced = sum
	r.ix.Put(rel, e)
	r.markChange()
	r.clearProblem()
}

func (r *runner) putSingle(ctx context.Context, rel string, t deviceapi.Ticket, size int64) error {
	if t.URL == "" {
		return errors.New("the server opened a single upload with no address")
	}
	headers := t.Headers
	if headers == nil {
		headers = map[string]string{}
	}
	f, err := openInFolder(r.link.LocalPath, rel)
	if err != nil {
		return err
	}
	defer func() { _ = f.Close() }()
	_, err = r.e.opts.API.PutObject(ctx, t.URL, headers, io.LimitReader(f, size), size)
	return err
}

// putParts uploads every part, at most PartConcurrency at a time, each retried
// up to PartRetries times with backoff. It returns the completed-parts list.
func (r *runner) putParts(ctx context.Context, rel string, t deviceapi.Ticket, size int64) ([]deviceapi.CompletedPart, error) {
	count := t.PartCount
	if count <= 0 {
		count = partsFor(size, t.PartSizeBytes)
	}
	numbers := make([]int, 0, count)
	for i := 1; i <= count; i++ {
		numbers = append(numbers, i)
	}
	urls, err := r.presign(ctx, t, numbers)
	if err != nil {
		return nil, err
	}
	f, err := openInFolder(r.link.LocalPath, rel)
	if err != nil {
		return nil, err
	}
	defer func() { _ = f.Close() }()

	etags := make([]string, count+1)
	errs := make([]error, count+1)
	sem := make(chan struct{}, r.e.t.PartConcurrency)
	var wg sync.WaitGroup
	for i := 1; i <= count; i++ {
		n := i
		wg.Add(1)
		go func() {
			defer wg.Done()
			sem <- struct{}{}
			defer func() { <-sem }()
			etag, err := r.putPart(ctx, f, n, t, size, urls)
			if err != nil {
				errs[n] = err
				return
			}
			etags[n] = etag
		}()
	}
	wg.Wait()
	for i := 1; i <= count; i++ {
		if errs[i] != nil {
			return nil, fmt.Errorf("part %d: %w", i, errs[i])
		}
	}
	parts := make([]deviceapi.CompletedPart, 0, count)
	for i := 1; i <= count; i++ {
		parts = append(parts, deviceapi.CompletedPart{PartNumber: i, ETag: etags[i]})
	}
	return parts, nil
}

// putPart uploads one part, retrying with backoff and re-presigning when the
// URL it holds is missing or the server refused it.
func (r *runner) putPart(ctx context.Context, f *os.File, n int, t deviceapi.Ticket, size int64, urls map[int]string) (string, error) {
	partSize := t.PartSizeBytes
	if partSize <= 0 {
		return "", errors.New("the server opened a multipart upload with no part size")
	}
	offset := int64(n-1) * partSize
	length := partSize
	if remaining := size - offset; remaining < length {
		length = remaining
	}
	if length < 0 {
		length = 0
	}
	var last error
	for attempt := 0; attempt <= r.e.t.PartRetries; attempt++ {
		if attempt > 0 {
			wait := r.e.t.PartBackoff * time.Duration(1<<(attempt-1))
			select {
			case <-ctx.Done():
				return "", ctx.Err()
			case <-time.After(wait):
			}
		}
		url := urls[n]
		if url == "" {
			fresh, err := r.presign(ctx, t, []int{n})
			if err != nil {
				last = err
				continue
			}
			url = fresh[n]
			if url == "" {
				last = fmt.Errorf("the server presigned no URL for part %d", n)
				continue
			}
		}
		section := io.NewSectionReader(f, offset, length)
		etag, err := r.e.opts.API.PutObject(ctx, url, nil, section, length)
		if err == nil {
			return etag, nil
		}
		last = err
	}
	return "", last
}

// presign returns part number → presigned URL, asking in batches of the
// server's maximum.
func (r *runner) presign(ctx context.Context, t deviceapi.Ticket, numbers []int) (map[int]string, error) {
	out := map[int]string{}
	for start := 0; start < len(numbers); start += deviceapi.MaxPartsPerPresign {
		end := start + deviceapi.MaxPartsPerPresign
		if end > len(numbers) {
			end = len(numbers)
		}
		urls, err := r.e.opts.API.PresignParts(ctx, r.link.LinkID, t, numbers[start:end])
		if err != nil {
			return nil, err
		}
		for _, u := range urls {
			out[u.PartNumber] = u.URL
		}
	}
	return out, nil
}

func partsFor(size, partSize int64) int {
	if partSize <= 0 {
		return 1
	}
	n := int((size + partSize - 1) / partSize)
	if n < 1 {
		n = 1
	}
	return n
}

// ---- manifests ----

// reconcileNow walks the whole folder and posts a complete manifest, so
// everything that happened while the app was off (or the link was paused)
// reaches the project and everything deleted here leaves it.
func (r *runner) reconcileNow(ctx context.Context) {
	if r.e.opts.API == nil {
		return
	}
	res, err := scanTree(r.link.LocalPath, "")
	if err != nil {
		r.fail(err)
		return
	}
	r.recordSkipped(res)
	if len(res.Dirs) > r.e.t.MaxWatchDirs {
		r.setPolling(true)
	} else {
		// Every folder this walk found is watched from here on. The start-up
		// watch is installed from an EARLIER walk, so a folder created between
		// that walk and its parent's watch produced no event and is in no
		// watch list; without this, changes inside it would wait for the next
		// full rescan for as long as the process runs. addWatch is a no-op for
		// a folder already watched, and when there is no watcher at all.
		for _, d := range res.Dirs {
			r.addWatch(d)
		}
	}

	manifestID := r.link.LinkID + "-" + strconv.FormatInt(time.Now().UnixNano(), 10)
	confirmed := r.takeConfirmLargeDelete()
	r.syncingOn()

	// manifestSHA is the hash each path was described with. The upload after
	// the manifest re-checks it, so the bytes sent are the bytes hashed.
	manifestSHA := make(map[string]string, len(res.Files))
	var files []deviceapi.ManifestFile
	for _, f := range res.Files {
		full := filepath.Join(r.link.LocalPath, filepath.FromSlash(f.Path))
		info, err := os.Lstat(full)
		if err != nil {
			continue
		}
		sum, err := r.hash(f.Path, full, info)
		if err != nil {
			if errors.Is(err, errChanged) {
				r.queueStable(f)
			} else {
				r.recordSkip(ReasonUnreadable, f.Path)
			}
			// A file we cannot hash right now still has to be in the manifest
			// with the hash the index last knew, or leaving it out would ask
			// the server to delete it.
			if e, ok := r.ix.Get(f.Path); ok && e.SHA256 != "" {
				files = append(files, deviceapi.ManifestFile{Path: f.Path, Size: e.Size, SHA256: e.SHA256})
				manifestSHA[f.Path] = e.SHA256
			}
			continue
		}
		files = append(files, deviceapi.ManifestFile{Path: f.Path, Size: f.Size, SHA256: sum})
		manifestSHA[f.Path] = sum
	}
	// A file that was synced before it became an online-only placeholder stays
	// in the manifest: it is still in the project, and leaving it out would ask
	// the server to delete it.
	for _, f := range res.OnlineOnly {
		if e, ok := r.ix.Get(f.Path); ok && e.SHA256 != "" {
			files = append(files, deviceapi.ManifestFile{Path: f.Path, Size: e.Size, SHA256: e.SHA256})
			manifestSHA[f.Path] = e.SHA256
		}
	}

	out, err := sendManifest(ctx, r.e.opts.API, r.link.LinkID, manifestID, files, confirmed)
	if err != nil {
		r.syncOff()
		r.manifestError(err)
		return
	}
	if confirmed {
		_ = r.e.opts.Store.Modify(r.link.LinkID, func(l *Link) { l.ConfirmLargeDelete = false })
	}
	r.setStorage(out.Storage)
	r.recordSkippedPaths(out.Skipped)
	r.clearProblem()

	if out.Paused {
		r.syncOff()
		r.pausedByServer(out.LastError)
		return
	}
	for _, path := range out.Delete {
		r.ix.Delete(path)
	}

	uploads := append([]string(nil), out.Upload...)
	sort.Strings(uploads)
	for _, path := range uploads {
		// A file modified less than Timing.Stable ago may still be being
		// written: the walk can land in the middle of a copy, or of a burst of
		// saves. It goes through the same stability wait as a file the watcher
		// reported, so the walk never uploads a half-written version that the
		// watcher's events then upload again. Its manifest row stays as it is:
		// the file is still in the folder.
		//
		// Only a path this manifest described is uploaded. The answer comes
		// from the server, and a path the walk did not report is one the walk
		// skipped or one that is not in the folder at all.
		sum, described := manifestSHA[path]
		if !described {
			continue
		}
		if info, err := lstatInFolder(r.link.LocalPath, path); err == nil && !info.IsDir() && time.Since(info.ModTime()) < r.e.t.Stable {
			r.queueStable(scannedFile{Path: path, Size: info.Size(), MTime: info.ModTime().UnixNano()})
			continue
		}
		r.uploadIfHeld(ctx, path, sum)
	}
	r.syncOff()
}

// sendManifest posts a complete manifest in batches of the server's maximum and
// returns the final batch's answer. Every batch carries the same manifest id and
// its own 0-based number; only the last one is final, and only it asks the server
// to work out uploads and deletions.
//
// It is a free function rather than a method so the batching can be tested
// without a folder, a watcher or a clock.
func sendManifest(ctx context.Context, api API, linkID, manifestID string, files []deviceapi.ManifestFile, confirmLargeDelete bool) (deviceapi.ManifestResult, error) {
	batches := chunkManifest(files)
	for i, chunk := range batches {
		batch := deviceapi.ManifestBatch{
			ManifestID: manifestID,
			Batch:      i,
			Final:      i == len(batches)-1,
			Files:      chunk,
		}
		if batch.Final {
			batch.ConfirmLargeDelete = confirmLargeDelete
		}
		out, err := api.Manifest(ctx, linkID, batch)
		if err != nil {
			return deviceapi.ManifestResult{}, err
		}
		if batch.Final {
			return out, nil
		}
	}
	// No files at all still has to be one final, empty batch: an empty manifest
	// is the loudest thing this client can say (the mass-delete guard is
	// listening for it).
	return api.Manifest(ctx, linkID, deviceapi.ManifestBatch{
		ManifestID: manifestID, Batch: 0, Final: true, ConfirmLargeDelete: confirmLargeDelete,
		Files: []deviceapi.ManifestFile{},
	})
}

// chunkManifest splits a manifest into batches of at most MaxManifestFiles
// files. A manifest with no files is one empty batch, never zero batches.
func chunkManifest(files []deviceapi.ManifestFile) [][]deviceapi.ManifestFile {
	if len(files) == 0 {
		return [][]deviceapi.ManifestFile{{}}
	}
	var out [][]deviceapi.ManifestFile
	for start := 0; start < len(files); start += deviceapi.MaxManifestFiles {
		end := start + deviceapi.MaxManifestFiles
		if end > len(files) {
			end = len(files)
		}
		out = append(out, files[start:end])
	}
	return out
}

func (r *runner) manifestError(err error) {
	switch {
	case errors.Is(err, deviceapi.ErrNotConnected):
		r.e.disconnect()
	case errors.Is(err, deviceapi.ErrLinkPaused):
		r.pausedByServer("")
	case errors.Is(err, deviceapi.ErrStorageFull):
		r.storageFull(deviceapi.StorageSentence(err))
	case errors.Is(err, deviceapi.ErrLinkNotFound):
		r.e.handled(r.link.LinkID, unlinkedSentence)
	default:
		r.fail(err)
	}
}

// takeConfirmLargeDelete reports whether a confirmation is waiting and spends
// it: it is sent exactly once, because a confirmation is an answer about the
// deletions that were held back at that moment, not a standing permission.
func (r *runner) takeConfirmLargeDelete() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if !r.link.ConfirmLargeDelete {
		return false
	}
	r.link.ConfirmLargeDelete = false
	return true
}

// removePaths deletes this link's files for the given paths from the project.
func (r *runner) removePaths(ctx context.Context, paths []string) {
	if len(paths) == 0 || r.e.opts.API == nil {
		return
	}
	sort.Strings(paths)
	for start := 0; start < len(paths); start += deviceapi.MaxDeletePaths {
		end := start + deviceapi.MaxDeletePaths
		if end > len(paths) {
			end = len(paths)
		}
		if _, err := r.e.opts.API.DeletePaths(ctx, r.link.LinkID, paths[start:end]); err != nil {
			switch {
			case errors.Is(err, deviceapi.ErrNotConnected):
				r.e.disconnect()
			case errors.Is(err, deviceapi.ErrLinkPaused):
				r.pausedByServer("")
			case errors.Is(err, deviceapi.ErrLinkNotFound):
				r.e.handled(r.link.LinkID, unlinkedSentence)
			default:
				r.fail(err)
			}
			return
		}
	}
	for _, p := range paths {
		r.ix.Delete(p)
	}
	r.markChange()
	r.clearProblem()
	_ = r.ix.Save()
}

// ---- state ----

func (r *runner) fail(err error) {
	if err == nil {
		return
	}
	r.e.log.Printf("folder sync: link %s: %v", r.link.LinkID, err)
	r.mu.Lock()
	r.problem = ProblemSentence(err)
	r.mu.Unlock()
	// Nothing is retried on a timer: the file stays in the folder, its hash
	// stays in the index, and the next reconciliation (or the next event on it)
	// tries again. That is what makes a failure recoverable without a retry
	// loop that could hammer a server that is already unhappy.
	r.changed()
}

func (r *runner) clearProblem() {
	r.mu.Lock()
	changed := r.problem != ""
	r.problem = ""
	r.pausedByGuard = false
	r.pausedByDashboard = false
	r.mu.Unlock()
	if changed {
		r.changed()
	}
}

func (r *runner) storageFull(sentence string) {
	r.mu.Lock()
	r.problem = sentence
	r.mu.Unlock()
	r.changed()
}

// pausedByServer records that the server paused this link.
//
// The two pauses are not the same thing. A person's dashboard pause is a state
// the dashboard owns: this computer keeps the link in its list, reports it, and
// offers no Resume button, because resuming there is the only thing that works.
// The mass-delete guard's pause is answered by THIS computer: the local link is
// marked paused (so it stays that way across a restart) and Resume confirms the
// deletions.
func (r *runner) pausedByServer(message string) {
	if reason, ok := r.e.opts.API.(guardReporter); ok && reason.GuardPaused(r.link.LinkID) {
		r.mu.Lock()
		r.pausedByGuard = true
		r.pausedByDashboard = false
		r.problem = ""
		r.note = message
		r.mu.Unlock()
		// The engine holds the link and asks again at the recheck interval: the
		// answer that releases a guard pause is the person pressing Resume
		// HERE, or the guard's condition no longer holding, and neither is
		// visible to a runner that simply stopped.
		r.e.hold(r.link.LinkID, guardSentence)
		r.changed()
		return
	}
	r.mu.Lock()
	r.pausedByGuard = false
	r.pausedByDashboard = true
	r.problem = ""
	r.note = message
	r.mu.Unlock()
	r.e.hold(r.link.LinkID, dashboardPauseSentence)
	r.changed()
}

func (r *runner) setStorage(s *deviceapi.Storage) {
	if s == nil {
		return
	}
	r.mu.Lock()
	r.storage = s
	r.mu.Unlock()
}

func (r *runner) syncingOn() {
	r.mu.Lock()
	r.syncing = true
	r.mu.Unlock()
}

func (r *runner) syncOff() {
	r.mu.Lock()
	r.syncing = false
	r.lastSync = time.Now()
	r.mu.Unlock()
}

func (r *runner) markChange() {
	r.mu.Lock()
	r.lastChange = time.Now()
	r.mu.Unlock()
}

func (r *runner) setNote(msg string) {
	if msg == "" {
		return
	}
	r.mu.Lock()
	r.note = msg
	r.mu.Unlock()
	r.changed()
}

func (r *runner) isPolling() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.polling
}

func (r *runner) setPolling(p bool) {
	r.mu.Lock()
	changed := r.polling != p
	r.polling = p
	r.mu.Unlock()
	if changed {
		r.changed()
	}
}

func (r *runner) changed() {
	if r.e.opts.OnChange != nil {
		r.e.opts.OnChange()
	}
}

// recordSkipped folds a scan's skips into the link's skipped groups.
func (r *runner) recordSkipped(res scanResult) {
	for reason, paths := range res.Skipped {
		for _, p := range paths {
			r.recordSkip(reason, p)
		}
	}
}

func (r *runner) recordSkippedPaths(skipped []deviceapi.SkippedPath) {
	for _, s := range skipped {
		r.recordSkip(serverReason(s.Reason), s.Path)
	}
}

func (r *runner) recordSkip(reason, path string) {
	if reason == "" || path == "" {
		return
	}
	r.mu.Lock()
	if r.skipped[reason] == nil {
		r.skipped[reason] = map[string]struct{}{}
	}
	r.skipped[reason][path] = struct{}{}
	r.mu.Unlock()
}

// status builds the link's status row.
func (r *runner) status(base LinkStatus) LinkStatus {
	r.mu.Lock()
	syncing := r.syncing
	lastSync := r.lastSync
	lastChange := r.lastChange
	polling := r.polling
	problem := r.problem
	pausedByGuard := r.pausedByGuard
	pausedByDashboard := r.pausedByDashboard
	note := r.note
	waiting := len(r.waiting)
	queued := 0
	for _, at := range r.pending {
		if time.Since(at) < r.e.t.Quiet {
			queued++
		}
	}
	skipped := make(map[string]map[string]struct{}, len(r.skipped))
	for reason, paths := range r.skipped {
		cp := make(map[string]struct{}, len(paths))
		for p := range paths {
			cp[p] = struct{}{}
		}
		skipped[reason] = cp
	}
	r.mu.Unlock()

	base.FileCount = r.ix.Len()
	if !lastChange.IsZero() {
		t := lastChange.UTC()
		base.LastChangeAt = &t
	}
	if !lastSync.IsZero() {
		t := lastSync.UTC()
		base.LastSyncAt = &t
	}
	base.Polling = polling
	if note != "" {
		base.Note = note
	}
	base.Skipped = skipGroups(skipped)

	pending := waiting + queued
	switch {
	case pausedByGuard:
		base.State = StatePaused
		base.PausedBy = PausedByGuard
		base.CanConfirmDelete = true
		base.Headline = headline(StatePaused, 0, false)
		base.Reason = guardSentence
	case pausedByDashboard:
		base.State = StatePaused
		base.PausedBy = PausedByDashboard
		base.Headline = headline(StatePaused, 0, false)
		base.Reason = dashboardPauseSentence
	case problem != "":
		base.State = StateAttention
		base.Headline = headline(StateAttention, 0, false)
		base.Reason = problem
	case syncing || pending > 0:
		base.State = StateSyncing
		base.Pending = pending
		base.Headline = headline(StateSyncing, pending, pending == 0)
	case base.FileCount > 0 && lastSync.IsZero():
		base.State = StateSyncing
		base.Headline = headline(StateSyncing, 0, true)
	default:
		base.State = StateUpToDate
		base.Headline = headline(StateUpToDate, 0, false)
	}
	return base
}

func (r *runner) saveIndex() {
	if err := r.ix.Save(); err != nil {
		r.e.log.Printf("folder sync: %s: writing the local index failed: %v", r.link.LinkID, err)
	}
}

// guardSentence is the mass-delete guard in the words a person reads.
const guardSentence = "The folder looks empty or mostly gone, so sync is paused and nothing is deleted. If you really removed those files, resume it."

// dashboardPauseSentence is a pause made in ReelBolt, where the resume happens.
const dashboardPauseSentence = "Paused in ReelBolt. Resume it there."

// guardReporter is the optional API method that says whether a pause was the
// mass-delete guard's doing. *deviceapi.Client answers it from the last
// manifest; an API that does not gets the dashboard wording, which is the safe
// default because it offers the user nothing to press here.
type guardReporter interface {
	GuardPaused(linkID string) bool
}

// ---- the watcher ----

// watchStart walks the folder and installs one watch per directory, or decides
// the folder has too many sub-folders to watch and falls back to polling.
func (r *runner) watchStart(ctx context.Context) {
	res, err := scanTree(r.link.LocalPath, "")
	if err != nil {
		r.fail(err)
		return
	}
	if len(res.Dirs) > r.e.t.MaxWatchDirs {
		r.setPolling(true)
		r.setNote(fmt.Sprintf("This folder has more sub-folders than can be watched at once, so it is checked every %s instead.", r.e.t.PollEvery))
		return
	}
	w, err := fsnotify.NewWatcher()
	if err != nil {
		r.setPolling(true)
		r.setNote(fmt.Sprintf("This computer could not watch the folder, so it is checked every %s instead.", r.e.t.PollEvery))
		return
	}
	r.watchMu.Lock()
	r.watcher = w
	r.watchMu.Unlock()
	for _, d := range res.Dirs {
		r.addWatch(d)
	}
	r.wg.Add(1)
	go func() {
		defer r.wg.Done()
		r.watchLoop(ctx, w)
	}()
}

// addWatch watches one directory, remembering it so an event can be turned back
// into a relative path.
func (r *runner) addWatch(rel string) {
	r.watchMu.Lock()
	w := r.watcher
	if w == nil {
		r.watchMu.Unlock()
		return
	}
	if _, ok := r.dirs[rel]; ok {
		r.watchMu.Unlock()
		return
	}
	r.dirs[rel] = struct{}{}
	r.watchMu.Unlock()

	full := r.link.LocalPath
	if rel != "" {
		full = filepath.Join(r.link.LocalPath, filepath.FromSlash(rel))
	}
	if err := w.Add(full); err != nil {
		// The directory may have vanished between the walk and the watch.
		// Failing to watch one directory is not a failure of the link: the
		// periodic rescan still covers it.
		r.watchMu.Lock()
		delete(r.dirs, rel)
		r.watchMu.Unlock()
	}
}

func (r *runner) dropWatch(rel string) {
	r.watchMu.Lock()
	w := r.watcher
	delete(r.dirs, rel)
	r.watchMu.Unlock()
	if w != nil {
		full := r.link.LocalPath
		if rel != "" {
			full = filepath.Join(r.link.LocalPath, filepath.FromSlash(rel))
		}
		_ = w.Remove(full)
	}
}

// watchLoop turns fsnotify events into "this path moved" marks.
func (r *runner) watchLoop(ctx context.Context, w *fsnotify.Watcher) {
	defer func() { _ = w.Close() }()
	for {
		select {
		case <-ctx.Done():
			return
		case ev, ok := <-w.Events:
			if !ok {
				return
			}
			r.onEvent(ev)
		case _, ok := <-w.Errors:
			if !ok {
				return
			}
			// A watch error is not fatal: the periodic rescan covers whatever
			// the watcher missed.
		}
	}
}

// onEvent records one filesystem event, adding a watch when a directory
// appears.
func (r *runner) onEvent(ev fsnotify.Event) {
	full := filepath.Clean(ev.Name)
	rel := relPath(r.link.LocalPath, full)
	if rel == "" {
		return
	}
	if hiddenAncestor(rel) || nameReason(filepath.Base(rel)) != "" {
		return
	}
	if ev.Op&fsnotify.Create != 0 {
		if info, err := os.Lstat(full); err == nil && info.IsDir() {
			if sub, err := scanTree(r.link.LocalPath, rel); err == nil {
				for _, d := range sub.Dirs {
					r.addWatch(d)
				}
			}
		}
	}
	// A watch is only ever dropped for a directory: fsnotify watches
	// directories, so removing a file has no watch to drop, and the directory
	// event carries the deletion anyway.
	if ev.Op&(fsnotify.Remove|fsnotify.Rename) != 0 {
		if info, err := os.Lstat(full); err != nil || !info.IsDir() {
			r.watchMu.Lock()
			_, isDir := r.dirs[rel]
			r.watchMu.Unlock()
			if isDir {
				r.dropWatch(rel)
			}
		}
	}
	r.touch(rel)
}
