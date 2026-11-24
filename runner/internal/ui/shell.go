package ui

import (
	"context"
	"errors"
	"io"
	"log"
	"os"
	"path/filepath"
	"reflect"
	goruntime "runtime"
	"sync"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent/srccache"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/daemon"
	"github.com/vecchiotom/reelbolt/runner/internal/deviceapi"
	"github.com/vecchiotom/reelbolt/runner/internal/governor"
	"github.com/vecchiotom/reelbolt/runner/internal/identity"
	"github.com/vecchiotom/reelbolt/runner/internal/runtime"
	"github.com/vecchiotom/reelbolt/runner/internal/runtimeimage"
	"github.com/vecchiotom/reelbolt/runner/internal/telemetry"
	"github.com/vecchiotom/reelbolt/runner/internal/update"
)

// watchInterval is how often the shell recomputes its view. A second is what
// makes the tray feel alive without being a busy loop; the governor's own
// Changed channel wakes it sooner when the verdict moves.
const watchInterval = time.Second

// Options configure a Shell.
type Options struct {
	// Config is the loaded runner.json. Required.
	Config *config.Config
	// Keys is the device key store. Required.
	Keys *identity.Store
	// Logger receives the shell's lines. Nil means stderr.
	Logger *log.Logger
	// Version is this build's version.
	Version string
	// FontsetVersion is the bundled font set's tag.
	FontsetVersion string
	// ControlDir overrides where the governor's documents live. Empty means
	// the runner's cache directory, which is where "reelbolt-runner run" and
	// "reelbolt-runner status" already look.
	ControlDir string

	// Prober, Agent, AgentFactory, Now and Interval are the daemon's own test
	// seams, passed through unchanged.
	Prober       daemon.Prober
	Agent        daemon.Runner
	AgentFactory func() (daemon.Runner, error)
	Now          func() time.Time
	Interval     time.Duration

	// Autostart is the login-item manager. Nil means the platform's. It is used
	// by the App, which owns the actions; the shell only reports it.
	Autostart Autostart
	// PairingBaseURL overrides the origin the pairing routes are reached at.
	PairingBaseURL string
	// ScratchRoot and WorkspaceRoot override where the cache screen measures.
	// Empty means what the daemon uses.
	ScratchRoot string
	// UpdateCheck, when set, is what the release check reports. The shell does
	// not check on its own: the daemon already does, on its own interval, and
	// a second checker would be a second truth.
	ReleaseCheck func(ctx context.Context) (UpdateView, error)

	// Telemetry is the crash reporter (E11). Nil means the shell builds its
	// own, with the switch read from the configuration and NO collector unless
	// one was compiled in - which is what every build made from this source
	// tree gets.
	Telemetry *telemetry.Reporter
	// LogDir is where the rotating log is written when the shell owns logging
	// (see New). Empty means the runner's usual cache directory.
	LogDir string
	// DiagnosticsDir is where "Export diagnostics" writes. Empty means the
	// runner's usual cache directory; a test sets it so an export does not
	// litter the machine.
	DiagnosticsDir string
	// ConfigPath is the runner.json the shell writes to when a setting
	// changes. Empty means the user's own configuration directory, which is
	// what a real run uses; a test sets it so a write never lands in the
	// developer's own file.
	ConfigPath string

	// Folders is the host's folder-sync operations, or nil for a build with
	// none. The Folders screen shows "not available in this window" then,
	// rather than an empty list that looks like a working feature.
	Folders FolderOps
	// FoldersStatusPath is the engine's published status file, shown for a bug
	// report. Empty means the shell does not name one.
	FoldersStatusPath string
	// PickerAvailable overrides whether the host can show a folder chooser.
	// Nil means "ask by trying", which is what a real host wants.
	PickerAvailable func() bool

	// Commands runs the container CLI for the video toolkit download and the
	// "is it downloaded" check. Nil means the real one; a test scripts it.
	Commands CommandRunner
	// Whoami overrides the device API's "who am I" call, which names the
	// workspace. Nil means the signed device client built from the
	// configuration; a test answers it directly.
	Whoami func(ctx context.Context) (deviceapi.Me, error)
	// HostFacts reports total memory and free disk at a directory. Nil means
	// caps.SystemFacts.
	HostFacts func(dir string) (memTotal, diskFree uint64)
	// ForgetMe overrides the device API's "remove me" call that Unpair makes.
	// Nil means the signed device client; a test answers it directly.
	ForgetMe func(ctx context.Context) error
	// MediaCacheDir overrides where the downloaded-media cache lives. Empty
	// means config.MediaCacheDir(); a test sets it.
	MediaCacheDir string
	// DashboardURL is the ReelBolt origin job links point at when the device
	// has no recorded API origin. Empty means DefaultOrigin().
	DashboardURL string
}

// Shell is the desktop shell's runtime.
//
// It owns the daemon when it hosts one, and it observes it when something else
// does; both funnel into one View, because a person looking at this machine
// does not care which process started the runner.
type Shell struct {
	opts   Options
	log    *log.Logger
	ledger *Ledger
	lock   *InstanceLock

	// telemetry is the crash reporter; logWriter is the rotating log the
	// diagnostics export attaches. Both are closed by Release.
	telemetry *telemetry.Reporter
	logWriter *telemetry.RotatingWriter

	mu        sync.Mutex
	probe     *daemon.ProbeResult
	probing   bool
	probeErr  string
	daemon    *daemon.Daemon
	runCtx    context.CancelFunc
	running   bool
	runErr    string
	revoked   bool
	startedAt time.Time
	update    *UpdateView
	// rootCtx is the context Start was given, so "Start the runner" from the
	// menu has a parent to hang off.
	rootCtx context.Context
	// pairing is the first-run wizard's state, and pairCancel abandons an open
	// request.
	pairing    *PairingView
	pairCancel context.CancelFunc
	// runDone is closed when the in-process runner's goroutine has finished,
	// so a restart can wait for the old runner before starting the new one.
	runDone chan struct{}
	// restarting is true while the in-process runner is being restarted to
	// pick up new settings, and restartPending while a restart waits for the
	// job in flight to finish.
	restarting     bool
	restartPending bool
	// userStopped is true after a person pressed Stop, so nothing starts the
	// runner behind their back (a finished download, for one).
	userStopped bool
	// toolkit is the video toolkit's download and presence.
	toolkit toolkitState
	// info is the device API's answer about this device (the workspace name).
	info deviceInfo
	// host is the cached machine facts the Settings screen shows.
	host hostCache
	// media is the downloaded-media cache once opened, and mediaFailed
	// records that opening it failed, so the view does not retry every second.
	media       *srccache.Cache
	mediaFailed bool

	subs   map[int]func(View)
	nextID int
	last   View
	now    func() time.Time
}

// New builds a shell. It does not probe or connect; Start does that.
func New(opts Options) (*Shell, error) {
	if opts.Config == nil {
		return nil, errors.New("ui: a configuration is required")
	}
	if opts.Keys == nil {
		return nil, errors.New("ui: a key store is required")
	}
	logger := opts.Logger
	var logWriter *telemetry.RotatingWriter
	if logger == nil {
		// The shell owns logging when nobody handed it a logger - which is the
		// case for the desktop host only if it fails to open one itself. The
		// rotating file is what the diagnostics export attaches, so a shell
		// that logged nowhere would export a bundle with no log in it.
		dir := opts.LogDir
		if dir == "" {
			dir, _ = telemetry.LogsDir()
		}
		if dir != "" {
			if w, err := telemetry.OpenRotating(dir, "runner", 0, 0); err == nil {
				logWriter = w
				logger = log.New(io.MultiWriter(os.Stderr, w), "", log.LstdFlags|log.LUTC)
			}
		}
	}
	if logger == nil {
		logger = log.New(os.Stderr, "", log.LstdFlags|log.LUTC)
	}
	now := opts.Now
	if now == nil {
		now = time.Now
	}
	s := &Shell{
		opts:      opts,
		log:       logger,
		ledger:    NewLedger(now),
		now:       now,
		subs:      map[int]func(View){},
		logWriter: logWriter,
	}
	// The reporter is built here rather than passed in, so the switch it reads
	// is this shell's live configuration: turning telemetry on from the window
	// takes effect on the next report, with nothing to rebuild.
	s.telemetry = opts.Telemetry
	if s.telemetry == nil {
		s.telemetry = telemetry.New(telemetry.Options{
			Enabled:  func() bool { return s.config().Telemetry.On() },
			Endpoint: telemetry.Endpoint(),
			Env:      s.telemetryEnv,
			Log:      func(format string, args ...any) { s.log.Printf(format, args...) },
		})
	}
	return s, nil
}

// TakeLock takes the single-instance lock, or reports the process that holds
// it. The desktop shell calls it before anything else: two trays would fight
// over one autostart entry and one daemon.
func (s *Shell) TakeLock(dir string) error {
	lock, err := TakeInstanceLock(filepath.Join(dir, "desktop.lock"))
	if err != nil {
		return err
	}
	s.lock = lock
	return nil
}

// Release releases the single-instance lock, if one was taken, and closes the
// telemetry reporter and the rotating log. It is what Quit calls and what a
// deferred call in a test calls, so a run never leaves a file descriptor or a
// sender goroutine behind.
func (s *Shell) Release() {
	if s.lock != nil {
		s.lock.Release()
		s.lock = nil
	}
	s.mu.Lock()
	rep := s.telemetry
	w := s.logWriter
	s.mu.Unlock()
	if rep != nil {
		rep.Close()
	}
	if w != nil {
		_ = w.Close()
	}
}

// Start probes the machine and, when it is paired, starts the runner.
//
// The probe runs in the background on purpose: it starts and removes a
// container, so it can take seconds, and a tray that shows nothing for five
// seconds looks broken. Until it answers, the view says it is checking.
func (s *Shell) Start(ctx context.Context) {
	s.mu.Lock()
	s.rootCtx = ctx
	if s.probing || s.probe != nil {
		s.mu.Unlock()
		return
	}
	s.probing = true
	s.mu.Unlock()

	go func() {
		// E11: the probe starts a container, so it is the least predictable
		// thing this process does. A panic is reported and then re-raised.
		defer telemetry.Recover(s.telemetry, "probe")
		res, err := s.probeMachine(ctx)
		s.mu.Lock()
		s.probing = false
		if err != nil {
			s.probeErr = err.Error()
			s.log.Printf("probe: %v", err)
		} else {
			s.probe = &res
		}
		s.mu.Unlock()
		s.notify()
		if err == nil {
			s.startRunner(ctx)
			s.checkImage(ctx)
			s.notify()
		}
	}()
	s.refreshDeviceInfo(false)
}

// reprobe checks the machine again: after the video toolkit was downloaded, or
// when a person presses Check again on the setup screen. The previous answer
// stays on screen while the new one is worked out.
//
// When the new answer changes what this computer can do, the in-process runner
// is restarted (once it is idle) so it tells ReelBolt; when no runner was
// running because the first check failed, it is started now.
func (s *Shell) reprobe(ctx context.Context) {
	s.mu.Lock()
	if s.probing {
		s.mu.Unlock()
		return
	}
	s.probing = true
	wasReady := s.probe != nil && s.probe.RemotionReady()
	s.mu.Unlock()
	s.notify()

	res, err := s.probeMachine(ctx)
	s.mu.Lock()
	s.probing = false
	if err != nil {
		s.probeErr = err.Error()
		s.log.Printf("probe: %v", err)
	} else {
		s.probe = &res
		s.probeErr = ""
	}
	running := s.running
	s.mu.Unlock()
	if err == nil {
		s.checkImage(ctx)
	}
	s.notify()
	if err != nil {
		return
	}
	switch {
	case !running:
		s.mu.Lock()
		stopped := s.userStopped
		s.mu.Unlock()
		if !stopped {
			s.startRunner(s.parentCtx())
		}
	case res.RemotionReady() != wasReady:
		s.RestartRunnerWhenIdle()
	}
}

// Recheck runs the machine check again in the background. It is the setup
// screen's Check again.
func (s *Shell) Recheck() {
	go func() {
		defer telemetry.Recover(s.telemetry, "probe")
		s.reprobe(s.parentCtx())
	}()
}

// parentCtx is the context Start was given, or Background before Start.
func (s *Shell) parentCtx() context.Context {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.rootCtx != nil {
		return s.rootCtx
	}
	return context.Background()
}

// probeMachine surveys the machine once, and replays the answer to the daemon
// so it does not probe a second time.
func (s *Shell) probeMachine(ctx context.Context) (daemon.ProbeResult, error) {
	prober := s.opts.Prober
	if prober == nil {
		prober = &daemon.SystemProber{Log: func(f string, a ...any) { s.log.Printf(f, a...) }}
	}
	scratch := s.opts.ScratchRoot
	if scratch == "" {
		scratch = s.scratchRoot()
	}
	return prober.Probe(ctx, daemon.ProbeRequest{
		Version:        s.opts.Version,
		FontsetVersion: s.opts.FontsetVersion,
		Slots:          s.opts.Config.Slots,
		Image:          s.opts.Config.RuntimeImage,
		ContainerCLI:   s.opts.Config.ContainerCLI,
		FfmpegPath:     daemon.DefaultFfmpegPath,
		ScratchRoot:    scratch,
	})
}

// replayProber hands the daemon the probe the shell already ran. Probing starts
// a container, so a second one is not free.
//
// The one fact it takes from the request is the number of jobs at once: that is
// a setting, not something the probe measured, and a restart after the person
// changed it must advertise the new number.
type replayProber struct{ res daemon.ProbeResult }

func (p replayProber) Probe(_ context.Context, req daemon.ProbeRequest) (daemon.ProbeResult, error) {
	res := p.res
	if req.Slots > 0 {
		res.Capabilities.Slots = req.Slots
	}
	return res, nil
}

// startRunner builds and runs the daemon in this process.
func (s *Shell) startRunner(parent context.Context) {
	s.mu.Lock()
	if s.running {
		s.mu.Unlock()
		return
	}
	cfg := s.opts.Config
	if !cfg.IsPaired() {
		s.mu.Unlock()
		s.log.Printf("not starting the runner: this machine is not paired")
		return
	}
	probe := s.probe
	s.mu.Unlock()

	opts := daemon.Options{
		Config:         cfg,
		Keys:           s.opts.Keys,
		Logger:         s.log,
		Version:        s.opts.Version,
		FontsetVersion: s.opts.FontsetVersion,
		Prober:         replayProber{res: *probe},
		Agent:          s.opts.Agent,
		AgentFactory:   s.opts.AgentFactory,
		Now:            s.opts.Now,
		Interval:       s.opts.Interval,
		ControlDir:     s.opts.ControlDir,
		MediaCacheDir:  s.opts.MediaCacheDir,
		OnJob:          s.ledger,
		// E11's stream, and its only consumer. The ledger above is untouched by
		// it: a job this runner refused never entered the machine, so it must
		// not appear in a list of the machine's work.
		OnJobFailure:   daemon.JobFailureObserverFunc(s.observeJobFailure),
		UpdateInterval: cfg.UpdateInterval(),
	}
	// The updater is deliberately NOT installed here. The daemon's background
	// update stops the process to swap the binary, which is right for a service
	// and wrong for a shell a person is looking at: the tray window would
	// vanish. The Settings screen checks and applies by hand instead.
	d, err := daemon.New(opts)
	if err != nil {
		if errors.Is(err, daemon.ErrNotPaired) {
			return
		}
		s.mu.Lock()
		s.runErr = err.Error()
		s.mu.Unlock()
		s.notify()
		return
	}

	runCtx, cancel := context.WithCancel(parent)
	done := make(chan struct{})
	s.mu.Lock()
	if s.running {
		// Another start won the race while this one was building its daemon.
		s.mu.Unlock()
		cancel()
		return
	}
	s.daemon = d
	s.runCtx = cancel
	s.runDone = done
	s.running = true
	s.startedAt = s.now()
	s.runErr = ""
	s.mu.Unlock()
	s.notify()

	go func() {
		defer close(done)
		defer telemetry.Recover(s.telemetry, "run")
		err := d.Run(runCtx)
		cancel()
		s.mu.Lock()
		s.running = false
		s.daemon = nil
		s.runCtx = nil
		switch {
		case err == nil:
		case errors.Is(err, daemon.ErrUnpaired):
			s.revoked = true
			s.opts.Config.DeviceID = ""
			s.opts.Config.OrganizationID = ""
		default:
			s.runErr = err.Error()
			s.log.Printf("the runner stopped: %v", err)
		}
		s.mu.Unlock()
		s.notify()
	}()
}

// WaitStopped waits until the in-process runner has finished stopping, or the
// timeout passes. It reports whether it stopped.
func (s *Shell) WaitStopped(timeout time.Duration) bool {
	s.mu.Lock()
	done := s.runDone
	s.mu.Unlock()
	if done == nil {
		return true
	}
	select {
	case <-done:
		return true
	case <-time.After(timeout):
		return false
	}
}

// restartStopTimeout bounds how long a restart waits for the old runner. The
// daemon's own stop waits for work in flight, and a restart only starts when
// there is none, so this is a backstop rather than an expected wait.
const restartStopTimeout = 2 * time.Minute

// RestartRunnerWhenIdle restarts the in-process runner so it picks up new
// settings (the per-job limits, the number of jobs at once) or a newly
// downloaded video toolkit.
//
// It never interrupts a job. With nothing in flight it restarts now, in the
// background; with a job running it waits for that job to end, so the change
// applies to the next job. It reports whether a job is holding it back. A
// runner started by another process is left alone: that process reads the
// file when it starts.
func (s *Shell) RestartRunnerWhenIdle() (waiting bool) {
	s.mu.Lock()
	if !s.running || s.daemon == nil {
		s.mu.Unlock()
		return false
	}
	if s.restartPending || s.restarting {
		s.mu.Unlock()
		return s.busy()
	}
	s.restartPending = true
	s.mu.Unlock()

	waiting = s.busy()
	go func() {
		defer telemetry.Recover(s.telemetry, "restart")
		ctx := s.parentCtx()
		for s.busy() {
			select {
			case <-ctx.Done():
				s.mu.Lock()
				s.restartPending = false
				s.mu.Unlock()
				return
			case <-time.After(2 * time.Second):
			}
		}
		s.restartNow(ctx)
	}()
	return waiting
}

// busy reports whether a job is in flight on this computer.
func (s *Shell) busy() bool {
	return s.ledger.Current() != nil || s.runnerBusy()
}

// restartNow stops the in-process runner, waits for it, and starts it again
// with the current configuration.
func (s *Shell) restartNow(ctx context.Context) {
	s.mu.Lock()
	s.restartPending = false
	if !s.running {
		s.mu.Unlock()
		return
	}
	s.restarting = true
	cancel := s.runCtx
	s.mu.Unlock()
	s.notify()
	s.log.Printf("restarting the runner to apply new settings")

	if cancel != nil {
		cancel()
	}
	stopped := s.WaitStopped(restartStopTimeout)
	s.mu.Lock()
	s.restarting = false
	s.mu.Unlock()
	if !stopped {
		s.log.Printf("the runner did not stop in time; the new settings apply when it next starts")
		s.notify()
		return
	}
	if ctx.Err() != nil {
		s.notify()
		return
	}
	s.startRunner(ctx)
	s.notify()
}

// StartRunner starts the runner in this process.
//
// It is idempotent and safe to call before the probe has answered: the start is
// deferred until the probe is in, because building the daemon without a
// capability document is what the daemon refuses to do.
func (s *Shell) StartRunner() {
	s.mu.Lock()
	s.userStopped = false
	if s.running {
		s.mu.Unlock()
		return
	}
	ctx := s.rootCtx
	if ctx == nil {
		ctx = context.Background()
	}
	probing, probed := s.probing, s.probe != nil
	s.mu.Unlock()
	if probing || !probed {
		go s.Start(ctx)
		return
	}
	s.startRunner(ctx)
}

// setUpdate records what the release check said.
func (s *Shell) setUpdate(v UpdateView) {
	s.mu.Lock()
	s.update = &v
	s.mu.Unlock()
	s.notify()
}

// StopRunner asks the runner to stop, letting a job in flight finish first.
// It is what the tray's Stop and a clean Quit call.
func (s *Shell) StopRunner() {
	s.mu.Lock()
	cancel := s.runCtx
	s.userStopped = true
	s.mu.Unlock()
	if cancel != nil {
		cancel()
	}
}

// Pause stops this machine taking work, from this process.
//
// It pauses the IN-PROCESS governor. When the runner is another process the
// only lever is the control file, which is what "reelbolt-runner pause" writes
// — and the tray uses exactly that rather than inventing a second one.
func (s *Shell) Pause(reason string) error {
	if reason == "" {
		reason = "You paused this computer."
	}
	s.mu.Lock()
	d := s.daemon
	s.mu.Unlock()
	if d != nil {
		d.Pause(reason)
		s.notify()
		return nil
	}
	return s.requestControl(governor.ActionPause, reason)
}

// Resume releases a hold.
func (s *Shell) Resume() error {
	s.mu.Lock()
	d := s.daemon
	s.mu.Unlock()
	if d != nil {
		if !d.Resume() {
			return say("This computer wasn't paused.")
		}
		s.notify()
		return nil
	}
	return s.requestControl(governor.ActionResume, "")
}

// requestControl writes a pause or resume request for whichever process is
// running the runner, and waits for it to answer.
func (s *Shell) requestControl(action, reason string) error {
	c := s.control()
	if !c.Enabled() {
		return say("ReelBolt isn't running on this computer, so there is nothing to pause or resume.")
	}
	if _, ok := s.liveRunner(); !ok {
		return say("ReelBolt isn't running on this computer, so there is nothing to pause or resume.")
	}
	if err := c.Request(action, "the ReelBolt window", reason); err != nil {
		s.log.Printf("control request: %v", err)
		return sayWrap("Couldn't reach ReelBolt on this computer. Try again in a moment.", err)
	}
	deadline := s.now().Add(6 * time.Second)
	for {
		if live, ok := s.liveRunner(); ok {
			if (action == governor.ActionResume && !live.Status.Held) ||
				(action == governor.ActionPause && live.Status.Held) {
				s.notify()
				return nil
			}
		}
		if s.now().After(deadline) {
			return say("ReelBolt didn't respond. Try again in a moment.")
		}
		time.Sleep(200 * time.Millisecond)
	}
}

// control locates the governor's control documents, the same way the command
// line does.
func (s *Shell) control() *governor.Control {
	dir := s.opts.ControlDir
	if dir == "" {
		cache, err := config.CacheDir()
		if err != nil {
			return &governor.Control{}
		}
		dir = cache
	}
	return &governor.Control{
		RequestPath: filepath.Join(dir, "governor.json"),
		LivePath:    filepath.Join(dir, "runner.live.json"),
	}
}

// liveRunner reads the live document, when one is fresh. It returns the
// document and whether a runner other than this process wrote it.
func (s *Shell) liveRunner() (governor.Live, bool) {
	c := s.control()
	live, err := c.ReadLive()
	if err != nil {
		return governor.Live{}, false
	}
	if !live.Fresh(s.now()) {
		return governor.Live{}, false
	}
	return live, true
}

// View is the current document.
func (s *Shell) View() View {
	v := Build(s.snapshot())
	v.FolderSummary = s.folderSummary()
	return v
}

// folderSummary is the tray's one line about folder sync. It is read from the
// same document the Folders screen shows, on the same cheap call, so the two
// cannot disagree. A build with no folder support says nothing at all: the
// absence of a feature is not a one-line status.
func (s *Shell) folderSummary() string {
	if s.opts.Folders == nil {
		return ""
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	return s.FoldersDocument(ctx).Summary
}

// Ledger is the job ledger, so a host (and a test) can see what the daemon
// reported without going through the view.
func (s *Shell) Ledger() *Ledger { return s.ledger }

// Subscribe registers a redraw callback and returns a function that removes it.
func (s *Shell) Subscribe(fn func(View)) func() {
	s.mu.Lock()
	id := s.nextID
	s.nextID++
	s.subs[id] = fn
	s.mu.Unlock()
	return func() {
		s.mu.Lock()
		delete(s.subs, id)
		s.mu.Unlock()
	}
}

// Watch recomputes the view on a ticker and on every governor change, and
// notifies subscribers when it actually changed. It blocks until ctx ends.
func (s *Shell) Watch(ctx context.Context) {
	t := time.NewTicker(watchInterval)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			s.Refresh()
		}
	}
}

// Refresh recomputes and notifies.
func (s *Shell) Refresh() { s.notify() }

// notify recomputes the view and calls every subscriber when it differs from
// the last one. Comparing first is what keeps a tray redraw per second from
// being a rebuild of the whole menu per second.
func (s *Shell) notify() {
	v := s.View()
	s.mu.Lock()
	changed := !sameView(s.last, v)
	if changed {
		s.last = v
	}
	subs := make([]func(View), 0, len(s.subs))
	for _, fn := range s.subs {
		subs = append(subs, fn)
	}
	s.mu.Unlock()
	if !changed {
		return
	}
	for _, fn := range subs {
		fn(v)
	}
}

// sameView compares the parts that the tray and the window actually render.
// Everything here is a comparable value except Jobs, which is compared by
// length and first row: a list whose head is unchanged has nothing new at the
// top, and the rows below it are history.
func sameView(a, b View) bool {
	if a.State != b.State || a.Headline != b.Headline || a.Detail != b.Detail {
		return false
	}
	if !reflect.DeepEqual(a.Remedy, b.Remedy) {
		return false
	}
	if a.Runner != b.Runner || a.Governor != b.Governor || a.Device != b.Device {
		return false
	}
	if !reflect.DeepEqual(a.Warnings, b.Warnings) {
		return false
	}
	// RuntimeView and GovernorView carry slices, so they are compared by value
	// rather than by ==.
	if !reflect.DeepEqual(a.Runtime, b.Runtime) {
		return false
	}
	if !reflect.DeepEqual(a.Settings, b.Settings) || !reflect.DeepEqual(a.Cache, b.Cache) {
		return false
	}
	if a.FolderSummary != b.FolderSummary {
		return false
	}
	if a.Cache.Bytes != b.Cache.Bytes || a.Cache.Jobs != b.Cache.Jobs {
		return false
	}
	if len(a.Jobs) != len(b.Jobs) {
		return false
	}
	return sameCurrent(a.Current, b.Current)
}

// sameCurrent compares the current job by what the tray renders. The elapsed
// time in the row is deliberately NOT compared: it changes every second, and a
// menu that rebuilds itself every second is a menu that flickers.
func sameCurrent(a, b *JobView) bool {
	if (a == nil) != (b == nil) {
		return false
	}
	if a == nil {
		return true
	}
	if a.ID != b.ID || a.Title != b.Title || a.Stage != b.Stage || a.Running != b.Running {
		return false
	}
	if (a.Percent == nil) != (b.Percent == nil) {
		return false
	}
	return a.Percent == nil || *a.Percent == *b.Percent
}

// snapshot assembles the Inputs from the shell's own state, the configuration
// and the live document.
func (s *Shell) snapshot() Inputs {
	s.refreshDeviceInfo(false)

	s.mu.Lock()
	cfg := s.opts.Config
	probe := s.probe
	probing := s.probing
	probeErr := s.probeErr
	running := s.running
	runErr := s.runErr
	revoked := s.revoked
	startedAt := s.startedAt
	updateView := s.update
	restarting := s.restarting || (s.restartPending && !running)
	info := s.info
	toolkit := s.toolkit
	var download *RuntimeDownload
	if toolkit.download != nil {
		d := *toolkit.download
		if d.Percent != nil {
			p := *d.Percent
			d.Percent = &p
		}
		download = &d
	}
	s.mu.Unlock()
	mediaCacheMax := cfg.MediaCache.EffectiveMaxBytes()

	now := s.now()
	in := Inputs{
		Version:       s.opts.Version,
		Paired:        cfg.IsPaired(),
		DeviceID:      cfg.DeviceID,
		DeviceName:    cfg.DeviceName,
		Workspace:     cfg.OrganizationID,
		GatewayURL:    cfg.GatewayURL,
		KeyStore:      s.opts.Keys.Label(),
		Revoked:       revoked,
		DaemonErr:     runErr,
		Restarting:    restarting,
		Schedule:      cfg.Schedule,
		Limits:        cfg.Limits.Resolve(governor.Host{}),
		Now:           now,
		Update:        updateView,
		DashboardURL:  s.dashboardURL(),
		Jobs:          s.ledger.Jobs(),
		Current:       s.ledger.Current(),
		WorkspaceName: info.workspaceName,
		PairedByEmail: info.pairedByEmail,
		Settings: SettingsView{
			Slots:              cfg.Slots,
			Limits:             cfg.Limits,
			Schedule:           cfg.Schedule,
			Host:               s.hostView(),
			MediaCacheMaxBytes: &mediaCacheMax,
			UpdateChannel:      cfg.UpdateChannelOr(),
			AutoUpdate:         cfg.UpdatesEnabled(),
			ConfigPath:         s.configPath(),
			Telemetry:          s.telemetryView(),
		},
		Cache: s.cacheView(),
	}
	if !in.Paired {
		in.WorkspaceName, in.PairedByEmail = "", ""
	}
	if keys, err := update.TrustedKeys(); err == nil {
		in.Settings.TrustedKeys = update.DescribeKeys(keys)
	} else {
		in.Settings.TrustedKeys = "none (this version cannot install updates by itself)"
	}
	if as := s.opts.Autostart; as != nil {
		on, err := as.Enabled()
		in.Settings.Autostart = on
		in.Settings.AutostartKnown = err == nil
	}

	// The runtime facts. The probe has answered, failed, or not answered yet;
	// a re-check keeps the previous answer on screen while it runs.
	switch {
	case probe != nil:
		in.Runtime = runtimeView(*probe, cfg.Slots)
		in.Runtime.Probing = probing
	case probing:
		in.Runtime = RuntimeView{Image: runtimeimage.Resolve(cfg.RuntimeImage, "").Ref, Probing: true}
	case probeErr != "":
		in.Runtime = RuntimeView{
			Known:      true,
			Image:      runtimeimage.Resolve(cfg.RuntimeImage, "").Ref,
			Failure:    "Couldn't check what this computer can do. Press Check again, or restart ReelBolt.",
			ProbeError: "Couldn't check what this computer can do.",
		}
	}
	in.Runtime.ImagePresent = toolkit.imagePresent
	in.Runtime.ImageDownloadable = imageDownloadable(in.Runtime.Image)
	if toolkit.imagePresent != nil && *toolkit.imagePresent {
		in.Runtime.ImageBytes = toolkit.imageBytes
	}
	in.Runtime.Download = download

	// The governor. This process's own daemon is authoritative for itself;
	// otherwise the live document another process published is.
	if running {
		if d := s.currentDaemon(); d != nil {
			in.Runner = RunnerState{
				Running: true, InProcess: true, PID: os.Getpid(),
				StartedAt: startedAt.Format(time.RFC3339),
				Governor:  d.Governor().Status(), GovernorKnown: true,
			}
		}
	}
	if !in.Runner.Running {
		if live, ok := s.liveRunner(); ok {
			other := live.PID != os.Getpid()
			in.Runner = RunnerState{
				Running: true, InProcess: !other, PID: live.PID, StartedAt: live.StartedAt,
				Governor: live.Status, GovernorKnown: true,
			}
		}
	}
	if in.Runner.Running && !in.Runner.GovernorKnown {
		// A runner is running but its verdict is unreadable. Saying "no reason"
		// would be a claim; saying so is not.
		in.Warnings = append(in.Warnings, "ReelBolt is running, but this window can't read what it is doing.")
	}

	// Limits need the host's core count and memory, which only a probe gives.
	if probe != nil {
		in.Limits = cfg.Limits.Resolve(governor.Host{
			NumCPU:        probe.Inventory.CPUCores,
			MemTotalBytes: probe.Inventory.MemTotal,
		})
	}
	return in
}

func (s *Shell) currentDaemon() *daemon.Daemon {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.daemon
}

// runtimeView turns the probe into the screen's facts.
//
// Failure is the sentence a person reads about why videos cannot be rendered
// (the window shows it verbatim), and Candidates says in plain words what each
// container runtime that was tried is doing. The probe's own failure codes and
// command output stay in the log and the diagnostics export.
//
// A missing video toolkit is deliberately NOT a Failure: ImagePresent says it,
// and the download panel is the answer, so a warning saying the same thing
// would only repeat it.
func runtimeView(res daemon.ProbeResult, slots int) RuntimeView {
	v := RuntimeView{
		Known:         true,
		Image:         res.Image.Ref,
		Origin:        res.Image.Origin,
		ContainerCLI:  res.ContainerCLI,
		FfmpegVersion: res.Capabilities.FfmpegVersion,
		JobTypes:      append([]string(nil), res.Capabilities.JobTypes...),
		RemotionReady: res.RemotionReady(),
		Slots:         slots,
	}
	for _, r := range res.Reports {
		v.Candidates = append(v.Candidates, runtimeName(r.Candidate.Kind)+": "+candidateState(r))
	}
	if v.RemotionReady {
		return v
	}
	switch {
	case res.Runtime != nil && res.Runtime.Failure == runtime.FailImageMissing:
	case res.Runtime != nil && res.Runtime.Failure != "":
		v.Failure = runtimeFailureSentence(res.Runtime.Candidate.Kind, res.Runtime.Failure)
	case res.Runtime == nil && len(res.Reports) > 0:
		first := res.Reports[0]
		v.Failure = runtimeFailureSentence(first.Candidate.Kind, first.Failure)
	case res.Runtime == nil:
		v.Failure = "Video rendering needs Docker or Podman, and neither is installed."
	}
	return v
}

// candidateState is one container runtime's state in a few words.
func candidateState(r runtime.Report) string {
	switch {
	case r.Usable && r.RemotionReady:
		return "ready"
	case r.Usable && r.Failure == runtime.FailImageMissing:
		return "working, video toolkit not downloaded"
	case r.Usable:
		return "working, video rendering not ready"
	case r.Failure == runtime.FailUnreachable:
		return "not running"
	case r.Failure == runtime.FailVersionTooOld:
		return "too old"
	default:
		return "not working"
	}
}

// runtimeFailureSentence is what a probe failure means for a person, and what
// to do about it.
func runtimeFailureSentence(kind runtime.Kind, code string) string {
	name := runtimeName(kind)
	switch code {
	case runtime.FailUnreachable:
		return notRunningSentence(kind)
	case runtime.FailVersionTooOld:
		return "Your version of " + name + " is too old for video rendering. Update it, then check again."
	case runtime.FailInfo:
		return name + " didn't answer properly. Restart it, then check again."
	case runtime.FailInternalNetwork:
		return name + " can't set up the private network videos are rendered in. Update it, then check again."
	case runtime.FailNoImage:
		return "This version of ReelBolt doesn't say which video toolkit to use. Update the app."
	case runtime.FailImageMissing:
		return "The video toolkit isn't downloaded yet."
	case runtime.FailArchMismatch:
		return "The video toolkit doesn't match this computer's processor. Update the app."
	case runtime.FailImageRun:
		return "The video toolkit is downloaded but won't start. Restart " + name + " and check again."
	case runtime.FailEgressPossible:
		return name + " lets the video toolkit reach the internet, which ReelBolt doesn't allow. The setup guide shows how to fix it."
	case "":
		return "Video rendering isn't ready on this computer."
	default:
		return name + " isn't working on this computer. The setup guide shows how to fix it."
	}
}

// dashboardURL is the ReelBolt origin job links point at: the origin this
// computer was paired at (the dashboard and the device API share it), else the
// configured one, else the product's.
func (s *Shell) dashboardURL() string {
	if cfg := s.config(); cfg.APIBaseURL != "" {
		if origin, err := config.CheckAPIBase(cfg.APIBaseURL, cfg.DevGateways); err == nil {
			return origin
		}
	}
	if s.opts.DashboardURL != "" {
		return s.opts.DashboardURL
	}
	return DefaultOrigin()
}

// ConfigPath is where runner.json lives, so a screen can name it.
func (s *Shell) ConfigPath() string { return s.configPath() }

func (s *Shell) configPath() string {
	if s.opts.ConfigPath != "" {
		return s.opts.ConfigPath
	}
	p, err := config.Path()
	if err != nil {
		return ""
	}
	return p
}

// saveConfig writes a configuration to the path this shell reads, and is the
// ONLY place a screen's change reaches the disk. Every writer goes through it,
// so "the file and the running daemon agree after this returns" is one rule
// rather than one rule per setting.
func (s *Shell) saveConfig(next *config.Config) error {
	path := s.ConfigPath()
	if path == "" {
		return errors.New("this machine has no configuration file to write")
	}
	return next.SaveTo(path)
}

func (s *Shell) scratchRoot() string {
	if s.opts.ScratchRoot != "" {
		return s.opts.ScratchRoot
	}
	if s.opts.Config.ScratchRoot != "" {
		return s.opts.Config.ScratchRoot
	}
	cache, err := config.CacheDir()
	if err != nil {
		return ""
	}
	return filepath.Join(cache, "jobs")
}

// WorkspaceRoot is where workspace containers' directories live.
func (s *Shell) WorkspaceRoot() string {
	return filepath.Join(filepath.Dir(s.scratchRoot()), "workspaces")
}

// Supported reports whether this platform is one the desktop app ships for:
// Windows x64 and Linux x64/arm64 (decision D26). There is no macOS desktop
// runner, so this refuses rather than pretending — a half-built macOS shell
// that fails at launch is worse than one that says why it is not here.
func Supported() error {
	switch goruntime.GOOS {
	case "windows", "linux":
		return nil
	default:
		return say("ReelBolt for desktop runs on Windows and Linux. There is no Mac version yet.")
	}
}

// JobTypes is the capability list, for the window's runtime panel.
func (s *Shell) JobTypes() []string {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.probe == nil {
		return nil
	}
	return append([]string(nil), s.probe.Capabilities.JobTypes...)
}

// Capabilities exposes the probe's capability document for diagnostics.
func (s *Shell) Capabilities() (protocol.Capabilities, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.probe == nil {
		return protocol.Capabilities{}, false
	}
	return s.probe.Capabilities, true
}
