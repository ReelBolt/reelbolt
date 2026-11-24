package foldersync

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"log"
	"os"
	"path/filepath"
	"sync"
	"sync/atomic"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/deviceapi"
)

// API is the slice of the device API the engine uses; *deviceapi.Client
// implements it.
type API interface {
	Manifest(ctx context.Context, linkID string, b deviceapi.ManifestBatch) (deviceapi.ManifestResult, error)
	CreateUpload(ctx context.Context, linkID string, r deviceapi.UploadRequest) (deviceapi.Ticket, error)
	PresignParts(ctx context.Context, linkID string, t deviceapi.Ticket, parts []int) ([]deviceapi.PartURL, error)
	CompleteUpload(ctx context.Context, linkID string, t deviceapi.Ticket, parts []deviceapi.CompletedPart) (deviceapi.Completed, error)
	AbortUpload(ctx context.Context, linkID string, t deviceapi.Ticket) error
	DeletePaths(ctx context.Context, linkID string, paths []string) (int, error)
	PutObject(ctx context.Context, url string, headers map[string]string, body io.Reader, size int64) (string, error)
}

// Logger is the one method the engine needs.
type Logger interface {
	Printf(format string, args ...any)
}

// Timing is every interval the engine uses. Tests shrink them; nothing else
// should.
type Timing struct {
	// Quiet is how long a path must see no events before it is looked at.
	Quiet time.Duration
	// Stable is how long a file's size and mtime must not change before it is
	// uploaded (a file still being written is not uploaded half-done).
	Stable time.Duration
	// Tick is the debounce loop's resolution.
	Tick time.Duration
	// Reconcile is the safety-net full rescan.
	Reconcile time.Duration
	// PollEvery is the rescan interval when a folder has too many sub-folders
	// to watch.
	PollEvery time.Duration
	// MaxWatchDirs is that limit.
	MaxWatchDirs int
	// StoreCheck is how often folders.json is re-read (another process — the
	// command line — may have changed it).
	StoreCheck time.Duration
	// RetryBase and RetryMax bound the backoff after a failure.
	RetryBase, RetryMax time.Duration
	// Recheck is how often a link paused on the server, or a computer the
	// server refused, is asked again.
	Recheck time.Duration
	// PartBackoff is the first wait before retrying an upload part.
	PartBackoff time.Duration
	// PartRetries is how many times a part is retried after its first try.
	PartRetries int
	// PartConcurrency is how many parts upload at once.
	PartConcurrency int
}

// DefaultTiming is the design's numbers.
func DefaultTiming() Timing {
	return Timing{
		Quiet:           2 * time.Second,
		Stable:          2 * time.Second,
		Tick:            500 * time.Millisecond,
		Reconcile:       15 * time.Minute,
		PollEvery:       2 * time.Minute,
		MaxWatchDirs:    20000,
		StoreCheck:      3 * time.Second,
		RetryBase:       30 * time.Second,
		RetryMax:        10 * time.Minute,
		Recheck:         5 * time.Minute,
		PartBackoff:     time.Second,
		PartRetries:     3,
		PartConcurrency: 3,
	}
}

func (t Timing) withDefaults() Timing {
	d := DefaultTiming()
	if t.Quiet <= 0 {
		t.Quiet = d.Quiet
	}
	if t.Stable <= 0 {
		t.Stable = d.Stable
	}
	if t.Tick <= 0 {
		t.Tick = d.Tick
	}
	if t.Reconcile <= 0 {
		t.Reconcile = d.Reconcile
	}
	if t.PollEvery <= 0 {
		t.PollEvery = d.PollEvery
	}
	if t.MaxWatchDirs <= 0 {
		t.MaxWatchDirs = d.MaxWatchDirs
	}
	if t.StoreCheck <= 0 {
		t.StoreCheck = d.StoreCheck
	}
	if t.RetryBase <= 0 {
		t.RetryBase = d.RetryBase
	}
	if t.RetryMax <= 0 {
		t.RetryMax = d.RetryMax
	}
	if t.Recheck <= 0 {
		t.Recheck = d.Recheck
	}
	if t.PartBackoff <= 0 {
		t.PartBackoff = d.PartBackoff
	}
	if t.PartRetries < 0 {
		t.PartRetries = d.PartRetries
	}
	if t.PartConcurrency <= 0 {
		t.PartConcurrency = d.PartConcurrency
	}
	return t
}

// Options configure an Engine.
type Options struct {
	// Store is folders.json. Required.
	Store *LinkStore
	// API is the signed device client. Nil means folder sync cannot run on
	// this computer, and Unavailable says why in plain words.
	API         API
	Unavailable string
	// IndexDir is where per-link indexes live
	// (os.UserCacheDir()/ReelBolt/folder-index). Required.
	IndexDir string
	// StatusPath, when set, receives the published status (see ReadStatus).
	StatusPath string
	// Logger receives progress lines. Nil means log.Default().
	Logger Logger
	// Timing overrides the intervals. Zero fields take the defaults.
	Timing Timing
	// OnChange is called (from the engine's goroutine) when the status
	// changes, so a window can redraw.
	OnChange func()
}

// Engine syncs every link in the store. It runs independently of the job
// runner and the gateway connection: folder sync talks to the API over signed
// HTTP, so it works whether or not the compute fabric is up.
type Engine struct {
	opts Options
	t    Timing
	log  Logger

	mu           sync.Mutex
	links        []Link
	runners      map[string]*runner
	disconnected string
	retryAt      time.Time
	loadErr      string
	last         []byte
	// problems is what a link's runner learned from the server about the link
	// itself, when the runner has since stopped — a link removed in ReelBolt
	// (404 link_not_found). It is cleared when the link is gone from the store,
	// which is exactly when there is nothing left to say about it.
	problems map[string]string
	// held is why a link's runner stopped without anything being wrong with the
	// link: the dashboard paused it, or the mass-delete guard did. It is kept
	// so the status can still say WHY after the runner is gone, and retryAt is
	// when that link is tried again, so a resume made anywhere — here or in the
	// dashboard — is picked up without a restart of this process.
	held      map[string]string
	heldUntil map[string]time.Time

	reloadCh chan struct{}
	// hashes counts files actually read for hashing; the index exists to keep
	// it low, and a test asserts it.
	hashes atomic.Int64
}

// New builds an engine. It does nothing until Run.
func New(opts Options) (*Engine, error) {
	if opts.Store == nil {
		return nil, errors.New("foldersync: a link store is required")
	}
	if opts.IndexDir == "" {
		return nil, errors.New("foldersync: an index directory is required")
	}
	l := opts.Logger
	if l == nil {
		l = log.Default()
	}
	return &Engine{
		opts:      opts,
		t:         opts.Timing.withDefaults(),
		log:       l,
		runners:   map[string]*runner{},
		problems:  map[string]string{},
		held:      map[string]string{},
		heldUntil: map[string]time.Time{},
		reloadCh:  make(chan struct{}, 1),
	}, nil
}

// DefaultIndexDir is os.UserCacheDir()/ReelBolt/folder-index.
func DefaultIndexDir() (string, error) {
	base, err := os.UserCacheDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(base, "ReelBolt", "folder-index"), nil
}

// Hashes is how many files the engine has read to hash them.
func (e *Engine) Hashes() int64 { return e.hashes.Load() }

// Reload re-reads the store now rather than at the next check.
func (e *Engine) Reload() {
	select {
	case e.reloadCh <- struct{}{}:
	default:
	}
}

// Sync asks one link for a full rescan now: the same catch-up that runs at
// start and every Reconcile interval. It is how "folders sync" and a retry
// after a failure reach a runner that is otherwise waiting for an event.
func (e *Engine) Sync(linkID string) {
	e.mu.Lock()
	r := e.runners[linkID]
	e.mu.Unlock()
	if r != nil {
		r.syncNow()
	}
}

// Run syncs until ctx ends.
func (e *Engine) Run(ctx context.Context) error {
	e.reload(ctx)
	e.publish()
	status := time.NewTicker(time.Second)
	defer status.Stop()
	store := time.NewTicker(e.t.StoreCheck)
	defer store.Stop()
	for {
		select {
		case <-ctx.Done():
			e.drain()
			e.publish()
			return nil
		case <-e.reloadCh:
			e.reload(ctx)
			e.publish()
		case <-store.C:
			e.reload(ctx)
		case <-status.C:
			e.publish()
		}
	}
}

// reload starts, restarts and stops runners to match the store.
func (e *Engine) reload(ctx context.Context) {
	links, err := e.opts.Store.Load()
	e.mu.Lock()
	if err != nil {
		e.loadErr = err.Error()
		e.mu.Unlock()
		e.log.Printf("folder sync: read the folder list: %v", err)
		return
	}
	e.loadErr = ""
	e.links = links
	if e.disconnected != "" && time.Now().After(e.retryAt) {
		// Ask again: a clock that was far off, or a server that was briefly
		// confused, should not need a re-pair.
		e.disconnected = ""
	}
	canRun := e.opts.API != nil && e.disconnected == ""
	now := time.Now()
	want := map[string]Link{}
	if canRun {
		for _, l := range links {
			if l.Paused {
				// A pause made on THIS computer is the person's; nothing is
				// retried until they resume it.
				delete(e.held, l.LinkID)
				continue
			}
			// A link the server paused is asked again at its own interval,
			// because the answer that unpauses it can arrive from the
			// dashboard, where this process cannot see it happen.
			if _, ok := e.held[l.LinkID]; ok {
				if at, ok := e.heldUntil[l.LinkID]; ok && now.Before(at) {
					continue
				}
				delete(e.held, l.LinkID)
				delete(e.heldUntil, l.LinkID)
			}
			want[l.LinkID] = l
		}
	}
	var stop []*runner
	var adopt []*runner
	for id, r := range e.runners {
		l, ok := want[id]
		if !ok || restartNeeded(r.link, l) {
			stop = append(stop, r)
			delete(e.runners, id)
			continue
		}
		if r.link != l {
			// The stored link changed in a way that does not need a new watch —
			// clearing a spent ConfirmLargeDelete, or a name being filled in.
			// The RUNNING runner still has to see it: the next manifest is what
			// carries the confirmation, and it reads it from here.
			adopt = append(adopt, r)
		}
	}
	e.mu.Unlock()

	for _, r := range adopt {
		r.adopt(want[r.link.LinkID])
	}
	for _, r := range stop {
		r.stop()
	}

	e.mu.Lock()
	defer e.mu.Unlock()
	for id, l := range want {
		if _, ok := e.runners[id]; ok {
			continue
		}
		r := newRunner(e, l)
		e.runners[id] = r
		r.start(ctx)
	}
}

// restartNeeded compares a running link with its stored form. Clearing the
// confirm flag (which the runner itself does after using it) is not a reason
// to restart; setting it is.
func restartNeeded(old, cur Link) bool {
	a, b := old, cur
	a.ConfirmLargeDelete, b.ConfirmLargeDelete = false, false
	a.ProjectName, b.ProjectName = "", ""
	a.AddedAt, b.AddedAt = "", ""
	if a != b {
		return true
	}
	return cur.ConfirmLargeDelete && !old.ConfirmLargeDelete
}

// drain stops every runner and waits for it to finish. It is synchronous on
// purpose: a Runner that returned while its goroutine was still writing the
// index would leave a folder that is not quiet yet, and "the engine stopped"
// has to mean exactly that.
func (e *Engine) drain() {
	e.mu.Lock()
	rs := make([]*runner, 0, len(e.runners))
	for id, r := range e.runners {
		rs = append(rs, r)
		delete(e.runners, id)
	}
	e.mu.Unlock()
	for _, r := range rs {
		r.stop()
	}
}

// disconnect stops every link: the server refused this computer (401). It is
// asked again after Recheck.
func (e *Engine) disconnect() {
	e.mu.Lock()
	if e.disconnected != "" {
		e.mu.Unlock()
		return
	}
	e.disconnected = disconnectedSentence
	e.retryAt = time.Now().Add(e.t.Recheck)
	e.mu.Unlock()
	e.log.Printf("folder sync: the server no longer accepts this computer; syncing stopped (asking again in %s)", e.t.Recheck)
	// Synchronous, because the caller may BE one of these runners: a goroutine
	// that stopped itself would be the one goroutine whose exit nobody waits
	// for, and the process could still be writing while everything else has
	// moved on.
	e.drain()
	e.Reload()
}

// hold records that a link stopped for a reason that lives on the server (the
// dashboard paused it, or the mass-delete guard did) and when it should be
// asked again. It is separate from problems because it is not an error: the
// link is fine and the runner is fine, and the person's next move may be made
// in the dashboard, which this process cannot observe.
func (e *Engine) hold(linkID, reason string) {
	if linkID == "" {
		return
	}
	e.mu.Lock()
	if e.held == nil {
		e.held = map[string]string{}
	}
	if e.heldUntil == nil {
		e.heldUntil = map[string]time.Time{}
	}
	e.held[linkID] = reason
	e.heldUntil[linkID] = time.Now().Add(e.t.Recheck)
	e.mu.Unlock()
	// The runner ends itself the moment it sets this; the reload that follows
	// sees the hold and leaves the link stopped until the retry is due.
	e.Reload()
}

// handled records something the server said about a link itself rather than
// about one file, and stops trying that link until the store changes. It is how
// "this folder was unlinked in ReelBolt" (a 404) reaches "folders list" without
// dying silently; the engine re-reads the store, so a link the command line
// removed really does stop.
func (e *Engine) handled(linkID, message string) {
	e.mu.Lock()
	if e.problems == nil {
		e.problems = map[string]string{}
	}
	e.problems[linkID] = message
	e.mu.Unlock()
	e.log.Printf("folder sync: link %s: %s", linkID, message)
	e.Reload()
}

// Status is the current status of every link.
func (e *Engine) Status() Status {
	e.mu.Lock()
	links := append([]Link(nil), e.links...)
	runners := make(map[string]*runner, len(e.runners))
	for k, v := range e.runners {
		runners[k] = v
	}
	held := make(map[string]string, len(e.held))
	for k, v := range e.held {
		held[k] = v
	}
	problem := e.disconnected
	if e.opts.API == nil {
		problem = e.opts.Unavailable
		if problem == "" {
			problem = "Folder sync is not available on this computer."
		}
	}
	if e.loadErr != "" && problem == "" {
		problem = "The list of synced folders on this computer couldn't be read. Restart ReelBolt; if it keeps happening, add the folders again."
	}
	perLink := make(map[string]string, len(e.problems))
	for k, v := range e.problems {
		perLink[k] = v
	}
	e.mu.Unlock()

	st := Status{UpdatedAt: time.Now().UTC(), PID: os.Getpid(), Problem: problem, Links: []LinkStatus{}}
	for _, l := range links {
		base := LinkStatus{
			LinkID: l.LinkID, LocalPath: l.LocalPath, ProjectID: l.ProjectID,
			ProjectName: l.ProjectName, TargetFolder: l.TargetFolder,
		}
		switch r, ok := runners[l.LinkID]; {
		case l.Paused:
			base.State, base.PausedBy = StatePaused, PausedOnComputer
			base.Headline = headline(StatePaused, 0, false)
			base.Reason = "Paused on this computer."
		case ok:
			base = r.status(base)
		case perLink[l.LinkID] != "":
			base.State = StateAttention
			base.Headline = headline(StateAttention, 0, false)
			base.Reason = perLink[l.LinkID]
		case held[l.LinkID] != "":
			base.State = StatePaused
			base.Headline = headline(StatePaused, 0, false)
			base.Reason = held[l.LinkID]
			// Who paused it decides whether the window offers a Resume, so it
			// is recovered from the sentence the runner recorded.
			if held[l.LinkID] == guardSentence {
				base.PausedBy = PausedByGuard
				base.CanConfirmDelete = true
			} else {
				base.PausedBy = PausedByDashboard
			}
		case problem != "":
			base.State = StateAttention
			base.Headline = headline(StateAttention, 0, false)
			base.Reason = problem
		default:
			base.State = StateSyncing
			base.Headline = "Starting"
		}
		st.Links = append(st.Links, base)
	}
	return st
}

// publish writes the status file and calls OnChange when the status moved.
func (e *Engine) publish() {
	st := e.Status()
	cmp := st
	cmp.UpdatedAt = time.Time{}
	raw, _ := json.Marshal(cmp)
	e.mu.Lock()
	changed := !bytes.Equal(raw, e.last)
	e.last = raw
	e.mu.Unlock()
	if e.opts.StatusPath != "" {
		if doc, err := json.MarshalIndent(st, "", "  "); err == nil {
			_ = writeFileAtomic(e.opts.StatusPath, append(doc, '\n'))
		}
	}
	if changed && e.opts.OnChange != nil {
		e.opts.OnChange()
	}
}

// Pause pauses a link on this computer.
func (e *Engine) Pause(linkID string) error {
	err := e.opts.Store.Modify(linkID, func(l *Link) { l.Paused = true; l.ConfirmLargeDelete = false })
	e.Reload()
	return err
}

// Resume resumes a link. confirmDelete is the person's answer to the
// mass-delete guard: the next manifest tells the server to apply the
// deletions it held back.
func (e *Engine) Resume(linkID string, confirmDelete bool) error {
	err := e.opts.Store.Modify(linkID, func(l *Link) {
		l.Paused = false
		if confirmDelete {
			l.ConfirmLargeDelete = true
		}
	})
	e.Reload()
	return err
}
