package daemon

import (
	"context"
	"errors"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/governor"
	"github.com/vecchiotom/reelbolt/runner/internal/identity"
)

// How often the daemon looks for a control request, and how often it publishes
// its own state. A second is the difference between "the pause button worked"
// and "the pause button worked eventually"; five is short enough that "status"
// is never meaningfully stale.
const (
	controlPollInterval = time.Second
	liveInterval        = 5 * time.Second
)

// Capabilities is the document this runner advertises. It is fixed at
// construction: the probe ran once and the hello carries its result on every
// reconnect, because re-probing would start containers and the gateway's routes
// were computed from the first answer.
func (d *Daemon) Capabilities() protocol.Capabilities { return d.probe.Capabilities }

// ProbeResult is everything the probe found, for `doctor` and `status`.
func (d *Daemon) ProbeResult() ProbeResult { return d.probe }

// GatewayURL is the endpoint dialled.
func (d *Daemon) GatewayURL() string { return d.cfg.GatewayURL }

// ScratchRoot is where job scratch directories live.
func (d *Daemon) ScratchRoot() string { return d.scratchRoot }

// WorkspaceRoot is where workspace containers' bind-mounted directories live.
func (d *Daemon) WorkspaceRoot() string { return d.workspaceRoot }

// Limits are the resolved per-job resource caps.
func (d *Daemon) Limits() governor.Limits { return d.limits }

// Governor is the schedule, the pause state and the machine reader. The desktop
// shell drives Pause and Resume through it rather than through a control file.
func (d *Daemon) Governor() *governor.Governor { return d.gov }

// Pause stops this runner taking work. It is the in-process trigger the tray
// uses; the command line writes a control request that reaches the same place.
func (d *Daemon) Pause(reason string) { d.gov.Pause(governor.SourceUser, reason) }

// Resume releases a pause and reports whether one was in force.
func (d *Daemon) Resume() bool { return d.gov.Resume() }

// Run serves until the context ends, a signal asks the daemon to stop, or the
// gateway refuses this identity for good.
//
// A signal does not end the run abruptly: it drains first (the gateway stops
// offering work and requeues what is in flight), waits the configured grace, and
// only then sends bye and closes. That is the difference between a service
// restart the gateway handles cleanly and one it has to time out at 45 seconds.
func (d *Daemon) Run(ctx context.Context) error {
	sigCh := make(chan os.Signal, 2)
	signal.Notify(sigCh, d.signalSet()...)
	defer signal.Stop(sigCh)
	return d.run(ctx, sigCh)
}

// run is Run with the signal source handed in, so the stop sequence and the
// pause/resume cycle are deterministic tests rather than real signals to the
// test process.
func (d *Daemon) run(ctx context.Context, sigCh <-chan os.Signal) error {
	lifeCtx, stopBackground := context.WithCancel(context.Background())
	defer stopBackground()

	go d.gov.Start(lifeCtx)
	go d.watchControl(lifeCtx)
	go d.publishLive(lifeCtx)
	defer func() { _ = d.control.RemoveLive() }()

	return d.serve(ctx, sigCh)
}

// serve keeps one worker agent connected while the governor allows work, and
// takes it away when it does not.
//
// The loop exists because the worker agent cannot be un-drained: once it has
// stopped accepting assignments — because the gateway said so, because the
// version check said so, or because the daemon asked it to — the only way back
// is a new agent on a new connection. Resuming therefore means reconnecting,
// and this is where that happens.
func (d *Daemon) serve(ctx context.Context, sigCh <-chan os.Signal) error {
	d.sweepScratch()
	d.logf("connecting to %s", d.cfg.GatewayURL)

	if d.updater != nil {
		d.logf("checking for updates every %s (an update is applied only while no job is running)",
			d.updateInterval())
		updateCtx, cancelUpdates := context.WithCancel(ctx)
		defer cancelUpdates()
		go d.updateLoop(updateCtx, d.updateInterval())
	}

	d.primeLocalLLM(ctx)
	go d.llmLoop(ctx)

	for {
		if !d.gov.Allowed() {
			st := d.gov.Status()
			d.logf("not taking work: %s (%s)", st.Reason, st.Detail)
			if !d.waitForWork(ctx, sigCh) {
				return nil
			}
		}
		runner, err := d.newRunner()
		if err != nil {
			return err
		}
		again, err := d.serveOne(ctx, sigCh, runner)
		if err != nil {
			return err
		}
		if !again {
			return nil
		}
	}
}

// serveOne runs one worker agent until something changes: the run ends, a
// signal arrives, or the governor moves. It reports whether the daemon should
// connect again.
//
// The governor's channel is taken BEFORE its state is read, on every pass. Read
// the other way round and a change landing between the two reads would be
// waited for on a channel that had already fired — the machine would sit idle
// believing it was still watching.
func (d *Daemon) serveOne(ctx context.Context, sigCh <-chan os.Signal, runner Runner) (bool, error) {
	runCtx, cancelRun := context.WithCancel(context.Background())
	defer cancelRun()
	done := make(chan error, 1)
	go func() { done <- runner.Run(runCtx) }()

	lastReason := ""
	for {
		changed := d.gov.Changed()
		st := d.gov.Status()
		if !st.Allowed && st.Reason != lastReason {
			lastReason = st.Reason
			if st.Offline() {
				d.logf("pausing: %s", st.Detail)
				d.detach(runner, done, cancelRun, st.Detail)
				return true, nil
			}
			// A remote hold is enforced by the worker agent, which the gateway
			// itself told to drain, so this runner stays connected and simply
			// refuses assignments until the gateway resumes it.
			d.logf("paused by the gateway: holding the connection but taking no work")
		}
		if st.Allowed && lastReason != "" {
			lastReason = ""
		}

		select {
		case err := <-done:
			cancelRun()
			return false, d.afterRun(err)

		case <-ctx.Done():
			d.logf("shutting down: %v", ctx.Err())
			return false, d.stop(sigCh, done, cancelRun, runner)

		case sig := <-sigCh:
			d.logf("received %s: draining", sig)
			return false, d.stop(sigCh, done, cancelRun, runner)

		case <-d.stopRequest:
			// The auto-update installed a release. The daemon stops so the new
			// binary takes over: on Linux the file was replaced in place, and this
			// process would otherwise keep running the old image indefinitely; on
			// Windows the swap waits for this process to exit.
			d.logf("stopping to finish the update; start the runner again to run the new version")
			return false, d.stop(sigCh, done, cancelRun, runner)

		case <-d.reconnect:
			d.logf("reconnecting after a remote resume")
			d.detach(runner, done, cancelRun, "the gateway resumed this runner")
			return true, nil

		case <-changed:
			// Re-read the verdict on the next pass.
		}
	}
}

// waitForWork blocks until the governor allows work again, and reports whether
// the daemon should keep running at all. A signal while paused is a stop: there
// is no connection to drain and no work to wait for.
func (d *Daemon) waitForWork(ctx context.Context, sigCh <-chan os.Signal) bool {
	for {
		changed := d.gov.Changed()
		if d.gov.Allowed() {
			d.logf("taking work again")
			return true
		}
		select {
		case <-changed:
		case <-d.reconnect:
			// A resume that arrived while this runner held no connection. It
			// is dropped rather than carried forward: the connection this loop
			// is about to make is already the thing it asked for, and keeping
			// the token would tear that fresh connection straight back down.
		case <-ctx.Done():
			d.logf("shutting down while paused: %v", ctx.Err())
			return false
		case sig := <-sigCh:
			d.logf("received %s while paused: stopping", sig)
			return false
		}
	}
}

// detach takes the worker agent off the gateway and waits for it to stop.
//
// The drain is sent first so the gateway learns promptly rather than waiting
// for the socket to time out, and its reason is the protocol's own
// "paused_by_user": a runner coming off the gateway because its user (or its
// schedule) said so is not a runner that is shutting down.
func (d *Daemon) detach(runner Runner, done <-chan error, cancelRun context.CancelFunc, reason string) {
	deadline := time.Duration(d.cfg.DrainSeconds) * time.Second
	if deadline <= 0 {
		deadline = config.DefaultDrainSeconds * time.Second
	}
	if err := runner.Drain(ControlPauseReason, deadline); err != nil {
		d.logf("drain did not reach the gateway: %v", err)
	} else {
		d.logf("drained (%s)", reason)
	}
	shCtx, cancelShutdown := context.WithTimeout(context.Background(), d.shutdownTimeout)
	defer cancelShutdown()
	if err := runner.Shutdown(shCtx); err != nil {
		d.logf("shutdown: %v", err)
	}
	cancelRun()
	select {
	case <-done:
	case <-time.After(d.shutdownTimeout):
		d.logf("the agent did not stop within %s", d.shutdownTimeout)
	}
}

// watchControl consumes pause and resume requests written by the command line.
//
// The request file is cleared before the loop starts, so a request left behind
// by an earlier process can never pause a fresh one. From then on a request is
// deleted before it is parsed (see Control.Take), so an unusable one is logged
// and dropped rather than re-read for ever.
func (d *Daemon) watchControl(ctx context.Context) {
	if !d.control.Enabled() {
		return
	}
	if err := d.control.Clear(); err != nil {
		d.logf("could not clear a stale control request: %v", err)
	}
	t := time.NewTicker(controlPollInterval)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			req, ok, err := d.control.Take()
			if err != nil {
				d.logf("ignoring an unusable control request: %v", err)
				continue
			}
			if !ok {
				continue
			}
			switch req.Action {
			case governor.ActionPause:
				reason := req.Reason
				if reason == "" {
					reason = "paused from the command line"
				}
				d.logf("pause requested by %s", owner(req.By))
				d.gov.Pause(governor.SourceUser, reason)
			case governor.ActionResume:
				if d.gov.Resume() {
					d.logf("resume requested by %s", owner(req.By))
				} else {
					d.logf("resume requested by %s, but this runner was not paused", owner(req.By))
				}
			}
		}
	}
}

func owner(by string) string {
	if by == "" {
		return "the command line"
	}
	return by
}

// publishLive writes the document "status" and the desktop shell read.
func (d *Daemon) publishLive(ctx context.Context) {
	if !d.control.Enabled() {
		return
	}
	d.publishOnce()
	t := time.NewTicker(liveInterval)
	defer t.Stop()
	for {
		changed := d.gov.Changed()
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			d.publishOnce()
		case <-changed:
			d.publishOnce()
		}
	}
}

func (d *Daemon) publishOnce() {
	live := governor.Live{
		PID:         os.Getpid(),
		StartedAt:   d.startedAt.Format(time.RFC3339),
		HeartbeatAt: d.now().Format(time.RFC3339),
		Status:      d.gov.Status(),
	}
	if err := d.control.Publish(live); err != nil && !d.liveWarned {
		d.liveWarned = true
		d.logf("could not publish the runner's live state: %v", err)
	}
}

// Drain stops accepting work and tells the gateway so. It is exported because
// the desktop shell's Pause button does the same thing, and because a caller
// that wants to stop without exiting needs it.
func (d *Daemon) Drain(reason string, deadline time.Duration) error {
	return d.runner.Drain(reason, deadline)
}

// Shutdown ends the connection: bye, then close.
func (d *Daemon) Shutdown(ctx context.Context) error { return d.runner.Shutdown(ctx) }

// drainGrace is how long a stop waits before it trusts "nothing is in flight".
// An assignment the agent accepted just before the drain may still be creating
// its session, and that session is only counted once it exists.
const drainGrace = 250 * time.Millisecond

// drainPoll is how often a stop re-checks whether the work in flight is done.
const drainPoll = 50 * time.Millisecond

// waitForInFlight waits, after a drain, until no job is in flight, the
// deadline passes, or a second signal says not to wait. It used to wait the
// whole deadline unconditionally, which made every stop of an idle runner (and
// every settings restart in the desktop app) take DrainSeconds for nothing.
func (d *Daemon) waitForInFlight(sigCh <-chan os.Signal, deadline time.Duration) {
	timer := time.NewTimer(deadline)
	defer timer.Stop()
	grace := time.NewTimer(min(drainGrace, deadline))
	defer grace.Stop()
	select {
	case <-timer.C:
		return
	case sig := <-sigCh:
		d.logf("received a second %s: not waiting any longer", sig)
		return
	case <-grace.C:
	}
	tick := time.NewTicker(drainPoll)
	defer tick.Stop()
	for d.Busy() {
		select {
		case <-timer.C:
			return
		case sig := <-sigCh:
			d.logf("received a second %s: not waiting any longer", sig)
			return
		case <-tick.C:
		}
	}
}

// stop performs the drain-and-shutdown sequence one signal started.
func (d *Daemon) stop(sigCh <-chan os.Signal, done <-chan error, cancelRun context.CancelFunc, runner Runner) error {
	deadline := time.Duration(d.cfg.DrainSeconds) * time.Second
	if deadline <= 0 {
		deadline = config.DefaultDrainSeconds * time.Second
	}
	if err := runner.Drain(protocol.CancelShutdown, deadline); err != nil {
		// A drain that could not be sent is not a reason to skip the shutdown:
		// the gateway's own dead-window covers a socket that has gone away.
		d.logf("drain did not reach the gateway: %v", err)
	} else {
		d.logf("drained; waiting up to %s for in-flight work", deadline)
	}
	d.waitForInFlight(sigCh, deadline)

	shCtx, cancelShutdown := context.WithTimeout(context.Background(), d.shutdownTimeout)
	defer cancelShutdown()
	if err := runner.Shutdown(shCtx); err != nil {
		d.logf("shutdown: %v", err)
	}
	// Belt to the pair of braces: Run returns once Shutdown has recorded the
	// stop, and cancelling the context makes that certain even if the agent was
	// between connections when the signal arrived.
	cancelRun()

	select {
	case err := <-done:
		return d.afterRun(err)
	case <-time.After(d.shutdownTimeout):
		d.logf("the agent did not stop within %s; exiting anyway", d.shutdownTimeout)
		return nil
	}
}

// afterRun maps the way a run ended onto what the caller should see.
func (d *Daemon) afterRun(err error) error {
	switch {
	case err == nil, errors.Is(err, agent.ErrStopped), errors.Is(err, context.Canceled):
		d.logf("stopped")
		return nil
	case errors.Is(err, agent.ErrUnauthenticated):
		// The device was revoked (or is unknown to this gateway). The device id
		// is worthless now and is dropped; the key is kept, because re-pairing
		// this machine should not have to generate a new identity — the user may
		// still approve a new device record for it.
		d.wipeDeviceID()
		d.logf("the gateway refused this device (%v); this machine is now unpaired", err)
		return ErrUnpaired
	default:
		return err
	}
}

// newRunner hands out the worker agent, rebuilding it after a pause.
func (d *Daemon) newRunner() (Runner, error) {
	if d.agentFactory != nil {
		return d.agentFactory()
	}
	if d.runner != nil {
		return d.runner, nil
	}
	return nil, errors.New("daemon: no worker agent is configured")
}

// wipeDeviceID clears the paired identity from runner.json, keeping the key and
// the gateway.
func (d *Daemon) wipeDeviceID() {
	d.cfg.DeviceID = ""
	d.cfg.OrganizationID = ""
	if err := d.cfg.Save(); err != nil {
		d.logf("could not record the unpaired state: %v", err)
		return
	}
	d.logf("cleared the device id from the configuration; the device key was kept")
}

// signalSet is the signals that start a drain: SIGTERM (what systemd, launchd
// and Windows services send on stop or logoff) and SIGINT (Ctrl-C).
func (d *Daemon) signalSet() []os.Signal {
	if len(d.signals) > 0 {
		return d.signals
	}
	return []os.Signal{os.Interrupt, syscall.SIGTERM}
}

// sweepScratch removes job directories left behind by a process that was killed.
//
// A job's scratch directory is deleted when the job ends, so anything still on
// disk at startup belongs to a previous process. Directories younger than the
// retention window are left alone: another daemon instance may be running them
// (the desktop shell and a foreground run can coexist), and deleting a live
// job's scratch directory is worse than keeping a stale one for a day.
func (d *Daemon) sweepScratch() {
	entries, err := os.ReadDir(d.scratchRoot)
	if err != nil {
		if !errors.Is(err, os.ErrNotExist) {
			d.logf("could not read the scratch root %s: %v", d.scratchRoot, err)
		}
		return
	}
	cutoff := time.Now().Add(-scratchRetention)
	removed := 0
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		info, err := e.Info()
		if err != nil || info.ModTime().After(cutoff) {
			continue
		}
		path := d.scratchRoot + string(os.PathSeparator) + e.Name()
		if err := os.RemoveAll(path); err != nil {
			d.logf("could not remove the stale scratch directory %s: %v", path, err)
			continue
		}
		removed++
	}
	if removed > 0 {
		d.logf("removed %d stale scratch director%s from an earlier run", removed, plural(removed))
	}
}

func plural(n int) string {
	if n == 1 {
		return "y"
	}
	return "ies"
}

func (d *Daemon) logf(format string, args ...any) {
	if d.log != nil {
		d.log.Printf(format, args...)
	}
}

// Keystore returns the device key store, so `doctor` can report where the key
// is kept without reaching for the file itself.
func (d *Daemon) Keystore() *identity.Store { return d.keys }

// updateInterval is how long the daemon waits between update checks.
func (d *Daemon) updateInterval() time.Duration {
	if d.updateEvery > 0 {
		return d.updateEvery
	}
	return DefaultUpdateInterval
}

// Busy reports whether a job is running right now.
//
// The auto-update asks before it replaces the binary: an update that landed
// mid-render would kill the render, and the gateway would see a runner that
// died holding a lease. A runner with no job in flight is always safe to update
// (nothing is mounted, and the scratch directory of the last job is gone).
func (d *Daemon) Busy() bool { return d.act.busy() }
