// Package daemon is the headless runner: the process around the worker agent
// library.
//
// It owns everything the library deliberately does not — where the configuration
// lives, where the device key comes from, which runtime image jobs execute in,
// what happens when the process is asked to stop, and what a service manager
// sees when it does. The library owns the protocol: connect, handshake,
// capability reporting, heartbeat lease renewal, job claim, op dispatch, drain,
// bye and the reconnect backoff.
//
// Two properties are worth stating because they are the reason this is a package
// rather than a main():
//
//   - The daemon listens on no port and holds no credential. It dials out, it
//     signs with a device key that never leaves the machine, and a job's ops
//     arrive over that one socket.
//   - Stopping is orderly. SIGTERM sends drain (the gateway stops offering work
//     and requeues anything in flight), waits the configured grace, then sends
//     bye and closes — the sequence the specification defines, not an abrupt
//     exit that would leave the gateway to time the runner out.
package daemon

import (
	"context"
	"crypto/ed25519"
	"errors"
	"fmt"
	"log"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent/media"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent/srccache"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/governor"
	"github.com/vecchiotom/reelbolt/runner/internal/identity"
	"github.com/vecchiotom/reelbolt/runner/internal/llmrelay"
	"github.com/vecchiotom/reelbolt/runner/internal/runtime"
	"github.com/vecchiotom/reelbolt/runner/internal/update"
)

// Errors a caller maps to an exit message.
var (
	// ErrNotPaired means this machine has no device identity, so there is
	// nothing to run: it must be paired first.
	ErrNotPaired = errors.New("daemon: this machine is not paired")
	// ErrUnpaired is returned when the gateway refused the device. The device id
	// has been wiped and is recorded nowhere, the device key is kept, and the
	// machine must be paired again.
	ErrUnpaired = errors.New("daemon: the gateway refused this device; pair this machine again")
)

// Defaults owned by the daemon.
const (
	// defaultShutdownTimeout bounds the bye-and-close that ends a run.
	defaultShutdownTimeout = 15 * time.Second
	// probeTimeout bounds the startup capability probe: it creates and removes a
	// throwaway container, and a runtime that never answers must not hang startup.
	probeTimeout = 5 * time.Minute

	// scratchRetention is how long an abandoned job directory survives. A job
	// directory left behind by a killed process is removed on the next start
	// once it is this old; anything younger may belong to a job another
	// instance is running.
	scratchRetention = 24 * time.Hour
)

// Runner is the worker agent, as the daemon uses it. *agent.Agent implements it;
// the daemon's tests substitute a fake to assert the stop sequence.
type Runner interface {
	Run(ctx context.Context) error
	Drain(reason string, deadline time.Duration) error
	Shutdown(ctx context.Context) error
}

// Options are the daemon's inputs.
type Options struct {
	// Config is the loaded runner.json. Required.
	Config *config.Config
	// Keys is the device key store. Required.
	Keys *identity.Store
	// Logger receives the daemon's lines. Nil means stderr.
	Logger *log.Logger
	// Version is this build's version, advertised in capabilities.
	Version string
	// FontsetVersion is the bundled font set's tag. Empty is legal: the gateway
	// simply will not match a job that requires a specific one.
	FontsetVersion string
	// Signals are turned into drain-and-shutdown. Nil means SIGINT and SIGTERM.
	Signals []os.Signal
	// Prober surveys the machine. Nil means the real one.
	Prober Prober
	// Agent overrides the worker agent, for tests.
	Agent Runner
	// AgentFactory rebuilds the worker agent after a pause. Nil means the
	// daemon rebuilds it from the device key it already loaded. It takes
	// precedence over Agent when both are set.
	AgentFactory func() (Runner, error)
	// Dialer overrides the WebSocket dialer, for tests. Nil installs the
	// daemon's own dialer, which is the WebSocket dialer wrapped to watch for
	// the gateway's control messages.
	Dialer agent.Dialer
	// Machine overrides the idle/power reader, for tests.
	Machine governor.Signals
	// Now overrides the clock, for tests.
	Now func() time.Time
	// Interval overrides the governor's re-evaluation period, for tests.
	Interval time.Duration
	// ControlDir overrides where the governor's control and live documents
	// live. Empty means the runner's cache directory.
	ControlDir string
	// OnJob receives the daemon's job notifications: a session starting, a
	// session reporting progress, and a session closing. Nil means nothing is
	// reported, which is every headless run. It exists for the desktop shell's
	// job list (E6) and is documented in jobevents.go.
	OnJob JobObserver
	// OnJobFailure receives a job this runner could not start, with the
	// protocol's own class and code and no message. Nil means nothing is
	// reported. It exists for E11's opt-in crash reporting, which is the only
	// caller, and is documented in jobfailures.go.
	OnJobFailure JobFailureObserver
	// Updater enables the signed auto-update (E9). Nil means the daemon never
	// checks for a release; the "update" command still works by hand.
	Updater Updater
	// UpdateInterval is how long the daemon waits between update checks. Zero
	// means DefaultUpdateInterval.
	UpdateInterval time.Duration
	// MediaCacheDir overrides where the downloaded-media cache lives. Empty
	// means config.MediaCacheDir(). Whether there is a cache at all, and its
	// cap, come from runner.json's mediaCache block.
	MediaCacheDir string
}

// Daemon is a configured, probed, ready-to-run runner.
type Daemon struct {
	cfg     *config.Config
	keys    *identity.Store
	log     *log.Logger
	version string
	fontset string
	signals []os.Signal

	prober       Prober
	probe        ProbeResult
	runner       Runner
	agentFactory func() (Runner, error)
	sessions     sessions
	dialer       agent.Dialer
	limits       governor.Limits
	gov          *governor.Governor
	control      *governor.Control
	reconnect    chan struct{}
	now          func() time.Time
	startedAt    time.Time
	liveWarned   bool

	scratchRoot   string
	workspaceRoot string

	// mediaCacheDir and mediaCache are the downloaded-media cache: nil when
	// runner.json turns it off or it could not be opened.
	mediaCacheDir string
	mediaCache    *srccache.Cache

	shutdownTimeout time.Duration

	// llm is the local-model relay (always present; it serves nothing unless the
	// user turned sharing on). advertisedLLM is the localLlm block of the last
	// hello, so a changed model list can be told apart from an unchanged one.
	llm           *llmrelay.Relay
	llmMu         sync.Mutex
	advertisedLLM string

	// onJob is the job observer, when one was supplied.
	onJob JobObserver
	// onJobFailure is the refusal observer (E11), when one was supplied.
	onJobFailure JobFailureObserver
	// act counts the jobs in flight, so the auto-update can refuse to replace
	// the binary underneath a running render (E9).
	act *activity
	// updater is the signed auto-update, when one is configured.
	updater     Updater
	updateEvery time.Duration
	stopRequest chan struct{}
	updated     atomic.Bool
}

// New builds the daemon: it loads the device key, probes the machine and wires
// the executors. It does not connect — Run does that — so a caller (the desktop
// shell, or `doctor`) can build the daemon for its capabilities alone.
func New(opts Options) (*Daemon, error) {
	if opts.Config == nil {
		return nil, errors.New("daemon: a configuration is required")
	}
	if opts.Keys == nil {
		return nil, errors.New("daemon: a key store is required")
	}
	cfg := opts.Config
	if !cfg.IsPaired() {
		return nil, ErrNotPaired
	}
	if err := cfg.Validate(); err != nil {
		return nil, fmt.Errorf("daemon: %w", err)
	}
	logger := opts.Logger
	if logger == nil {
		logger = log.New(os.Stderr, "", log.LstdFlags|log.LUTC)
	}
	d := &Daemon{
		cfg:             cfg,
		keys:            opts.Keys,
		log:             logger,
		version:         opts.Version,
		fontset:         opts.FontsetVersion,
		signals:         opts.Signals,
		prober:          opts.Prober,
		reconnect:       make(chan struct{}, 1),
		now:             time.Now,
		startedAt:       time.Now(),
		shutdownTimeout: defaultShutdownTimeout,
		act:             &activity{},
		onJob:           opts.OnJob,
		onJobFailure:    opts.OnJobFailure,
		updater:         opts.Updater,
		updateEvery:     opts.UpdateInterval,
		stopRequest:     make(chan struct{}, 1),
		mediaCacheDir:   opts.MediaCacheDir,
	}
	if d.updater != nil {
		d.updater.SetGate(update.GateFunc(d.Busy))
	}
	if d.prober == nil {
		d.prober = &SystemProber{Log: func(format string, args ...any) { d.logf(format, args...) }}
	}
	scratch, err := scratchRoot(cfg)
	if err != nil {
		return nil, err
	}
	d.scratchRoot = scratch
	d.workspaceRoot = filepath.Join(filepath.Dir(scratch), "workspaces")

	// The probe starts real containers, so it is bounded: a container runtime that
	// never answers must not hang the daemon's startup forever.
	probeCtx, cancelProbe := context.WithTimeout(context.Background(), probeTimeout)
	defer cancelProbe()
	if err := d.probeMachine(probeCtx, opts.Agent == nil); err != nil {
		return nil, err
	}
	d.configureGovernor(opts)
	// The governor's verdict gates the relay too: a paused runner answers
	// "disabled" rather than serving the user's model.
	d.llm = llmrelay.New(llmrelay.Options{Settings: cfg.LocalLLM, Allowed: d.gov.Allowed, Now: opts.Now})

	// The dialer is wrapped so the daemon can see the gateway's control
	// messages; a test that supplies its own keeps it unwrapped.
	dialer := opts.Dialer
	if dialer == nil {
		dialer = &controlDialer{inner: &agent.WebSocketDialer{}, onDrain: d.onGatewayControl}
	}
	d.dialer = dialer

	if opts.Agent != nil || opts.AgentFactory != nil {
		d.runner = opts.Agent
		d.agentFactory = opts.AgentFactory
		return d, nil
	}
	key, err := d.keys.Load(cfg.DeviceID)
	if err != nil {
		return nil, fmt.Errorf("daemon: load the device key: %w", err)
	}
	d.runner, err = d.buildAgent(key, dialer)
	if err != nil {
		return nil, err
	}
	// The key is already in hand, so a rebuild after a pause costs nothing but
	// the agent itself.
	d.agentFactory = func() (Runner, error) { return d.buildAgent(key, dialer) }
	return d, nil
}

// configureGovernor builds the governor and the control channel.
//
// Neither can stop the runner: a machine whose cache directory is unreadable
// loses the command-line pause button and keeps working, which is the right way
// round for a component whose whole job is deciding that a machine does NOT
// work.
func (d *Daemon) configureGovernor(opts Options) {
	d.gov = governor.New(governor.Config{
		Schedule: d.cfg.Schedule,
		Signals:  opts.Machine,
		Now:      opts.Now,
		Interval: opts.Interval,
		Log:      func(format string, args ...any) { d.logf(format, args...) },
	})
	if opts.Now != nil {
		d.now = opts.Now
	}
	d.control = controlFiles(opts.ControlDir)
	if !d.control.Enabled() {
		d.logf("no control channel: the cache directory is unavailable, so pause and resume are in-process only")
	}
	if st := d.gov.Status(); !st.Allowed {
		d.logf("governor: %s (%s)", st.Reason, st.Detail)
	}
	if st := d.gov.Status(); st.Degraded {
		d.logf("governor: %s", st.Detail)
	}
}

// controlFiles locates the governor's control and live documents.
func controlFiles(explicit string) *governor.Control {
	dir := explicit
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

// scratchRoot is where job scratch directories are created: the configured root,
// or <UserCacheDir>/ReelBolt/jobs.
func scratchRoot(cfg *config.Config) (string, error) {
	if cfg.ScratchRoot != "" {
		return cfg.ScratchRoot, nil
	}
	cache, err := config.CacheDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(cache, "jobs"), nil
}

// probeMachine runs the capability probe and builds the executors.
func (d *Daemon) probeMachine(ctx context.Context, buildExecutors bool) error {
	image := d.cfg.RuntimeImage
	res, err := d.prober.Probe(ctx, ProbeRequest{
		Version:        d.version,
		FontsetVersion: d.fontset,
		Slots:          d.cfg.Slots,
		Image:          image,
		ContainerCLI:   d.cfg.ContainerCLI,
		FfmpegPath:     DefaultFfmpegPath,
		ScratchRoot:    d.scratchRoot,
	})
	if err != nil {
		return fmt.Errorf("daemon: probe: %w", err)
	}
	d.probe = res
	d.resolveLimits()
	d.logf("capabilities: %s", strings.Join(res.Capabilities.JobTypes, ", "))
	if res.Image.Origin == "development default" {
		d.logf("warning: no runtime image is pinned and none is configured; using %s. "+
			"A released runner pins a digest, and a job that requires one will not be routed here.",
			res.Image.Ref)
	}
	if !buildExecutors {
		return nil
	}
	return d.buildExecutors()
}

// resolveLimits turns the [limits] block into the caps this host can honour.
// It runs before any executor is built, because an executor is built WITH its
// limits: there is no path that creates a container without one.
func (d *Daemon) resolveLimits() {
	d.limits = d.cfg.Limits.Resolve(governor.Host{
		NumCPU:        d.probe.Inventory.CPUCores,
		MemTotalBytes: d.probe.Inventory.MemTotal,
	})
	d.logf("limits: %s", d.limits.Describe())
	for _, note := range d.limits.Notes {
		d.logf("limits: %s", note)
	}
}

// openMediaCache opens the downloaded-media cache runner.json asks for. A cache
// that cannot be opened is logged and left out: every stage then downloads, as
// it always did, which is a slower runner and not a broken one.
func (d *Daemon) openMediaCache() {
	max := d.cfg.MediaCache.EffectiveMaxBytes()
	if max <= 0 {
		d.logf("media cache: off (mediaCache.maxBytes is 0)")
		return
	}
	dir := d.mediaCacheDir
	if dir == "" {
		var err error
		if dir, err = config.MediaCacheDir(); err != nil {
			d.logf("media cache: off: %v", err)
			return
		}
	}
	cache, err := srccache.Open(dir, max)
	if err != nil {
		d.logf("media cache: off: %v", err)
		return
	}
	d.mediaCache = cache
	st := cache.Stats()
	d.logf("media cache: %s (%d entries, %d of %d bytes)", st.Root, st.Entries, st.Bytes, st.MaxBytes)
}

// MediaCache returns the downloaded-media cache this daemon stages through, or
// nil when there is none (runner.json turned it off, or it could not be opened).
// The desktop shell reads its Stats and calls Clear and SetMaxBytes on it; the
// same *srccache.Cache is returned by srccache.Open for the same directory, so
// a shell that opened it before the daemon existed sees the same counters.
func (d *Daemon) MediaCache() *srccache.Cache { return d.mediaCache }

// sourceCache adapts the cache to the executors' optional field. A nil
// *srccache.Cache must become a nil interface, not an interface holding nil.
func (d *Daemon) sourceCache() agent.SourceCache {
	if d.mediaCache == nil {
		return nil
	}
	return d.mediaCache
}

// buildExecutors wires the two executors from the probe's findings.
func (d *Daemon) buildExecutors() error {
	d.openMediaCache()
	var remotionFactory agent.Factory
	if d.probe.RemotionReady() {
		ex, err := newRemotionExecutor(workspaceConfig{
			Image:   d.probe.Image.Ref,
			Root:    d.workspaceRoot,
			Slots:   d.cfg.Slots,
			Network: "none",
			Limits:  d.limits,
			Cache:   d.sourceCache(),
		}, d.probe.Runtime.Candidate)
		if err != nil {
			return err
		}
		remotionFactory = ex
		d.logf("remotion executor ready (image %s)", d.probe.Image.Ref)
	} else if d.probe.Runtime != nil && d.probe.Runtime.Usable {
		d.logf("the container runtime is usable but Remotion is not ready (%s); "+
			"this runner accepts media jobs only", reasonNotReady(d.probe.Runtime))
	}

	var mediaFactory agent.Factory
	if d.probe.Capabilities.FfmpegVersion != "" {
		// The media executor runs ffmpeg inside the runtime image when one was
		// verified, and as a host process otherwise.
		var cand *runtime.Candidate
		if d.probe.Runtime != nil {
			cand = &d.probe.Runtime.Candidate
		}
		ex, err := newMediaExecutor(d.probe.Image.Ref, cand, d.limits, media.Config{
			FFmpegPath:  DefaultFfmpegPath,
			FFprobePath: DefaultFFprobePath,
			SourceCache: d.sourceCache(),
		})
		if err != nil {
			return err
		}
		mediaFactory = ex
	} else {
		d.logf("no usable ffmpeg was found; this runner advertises no job types")
	}

	if remotionFactory == nil && mediaFactory == nil {
		// The agent refuses to start with a capability document it cannot
		// honour, and there is nothing to do without one.
		return errors.New("daemon: this machine can run no job type: no usable ffmpeg and no verified container runtime")
	}
	d.sessions = sessions{
		remotion: remotionFactory,
		media:    mediaFactory,
		act:      d.act,
		observe:  d.onJob,
		failures: d.onJobFailure,
	}
	return nil
}

// reasonNotReady explains why a usable runtime is not Remotion-ready, which is
// the difference between "ffmpeg-only runner" and "broken install".
func reasonNotReady(r *runtime.Report) string {
	if r == nil {
		return "no runtime"
	}
	if r.Failure != "" {
		if r.Detail != "" {
			return r.Failure + ": " + r.Detail
		}
		return r.Failure
	}
	return "unknown reason"
}

// buildAgent builds the worker agent around the resolved identity.
func (d *Daemon) buildAgent(key ed25519.PrivateKey, dialer agent.Dialer) (*agent.Agent, error) {
	if d.sessions.remotion == nil && d.sessions.media == nil {
		return nil, errors.New("daemon: no executor is configured")
	}
	cfg := agent.Config{
		GatewayURL:   d.cfg.GatewayURL,
		Identity:     agent.Identity{DeviceID: d.cfg.DeviceID, PrivateKey: key},
		Capabilities: d.probe.Capabilities,
		// Rebuilt at every hello, so a reconnect advertises the current model list.
		CapabilitiesFunc: d.helloCapabilities,
		LLM:              d.llm,
		Sessions:         d.sessions,
		ScratchRoot:      d.scratchRoot,
		// The scratch quota is a resource limit like any other, so it comes
		// from the same place rather than from the library's own default.
		DiskQuotaBytes: d.limits.DiskQuotaBytes,
		Logger:         d.log,
		Dialer:         dialer,
	}
	return agent.New(cfg)
}
