package daemon

import (
	"context"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/update"
)

// Auto-update timings.
const (
	// firstUpdateDelay is how long the daemon serves before its first update
	// check. A runner that was just started is usually being watched, and a
	// check on the first millisecond would put an update in the middle of a
	// user's first render.
	firstUpdateDelay = 90 * time.Second
	// DefaultUpdateInterval is how often the check runs when the configuration
	// names no interval.
	DefaultUpdateInterval = 6 * time.Hour
)

// Updater is the E9 auto-update as the daemon uses it. *update.Updater
// implements it; a test substitutes a fake to assert the deferral rules.
type Updater interface {
	// RunOnce performs one complete attempt: check, download, verify, apply.
	RunOnce(ctx context.Context) (update.Outcome, error)
	// SetGate installs the job-activity gate, so the update refuses to replace
	// the binary while a job is running even if the job started after the
	// daemon's own check.
	SetGate(gate update.Gate)
}

// updateLoop checks for a release on a timer until the daemon stops.
//
// Failure is never fatal here. An update host that is unreachable, a manifest
// whose signature does not verify, a device outside the rollout, a build with
// no release key compiled in: all of them are logged and the runner carries on
// serving the jobs it has. The alternative — a runner that stops working
// because a download server is down — is worse than a runner that is one
// release behind.
func (d *Daemon) updateLoop(ctx context.Context, interval time.Duration) {
	if interval <= 0 {
		interval = DefaultUpdateInterval
	}
	timer := time.NewTimer(firstUpdateDelay)
	defer timer.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-timer.C:
		}
		if d.shouldStop() {
			return
		}
		d.tryUpdate(ctx)
		timer.Reset(interval)
	}
}

// tryUpdate performs one attempt.
//
// The order is deliberate:
//
//  1. a job in flight ends the attempt before anything is downloaded — an
//     update must never take a slot or a byte of bandwidth from a render;
//  2. at most one update is applied per process run, so a helper waiting for
//     this process to exit cannot be spawned twice;
//  3. after a successful apply the daemon stops, because an update that is
//     installed but never run is a lie: on Linux the new file is in place but
//     this process keeps running the old image, and on Windows the swap only
//     happens once this process has exited.
func (d *Daemon) tryUpdate(ctx context.Context) {
	if d.updater == nil || d.updated.Load() {
		return
	}
	if d.Busy() {
		d.logf("a job is running; deferring the update check")
		return
	}
	out, err := d.updater.RunOnce(ctx)
	switch {
	case err != nil:
		// update.ErrJobRunning arrives if a job started between the check above
		// and the download; it is the same answer as the busy case, not a
		// failure.
		d.logf("update: %v", err)
		return
	case !out.Applied:
		if out.Reason != "" {
			d.logf("update: %s", out.Reason)
		}
		if out.NotifyOnly && out.Notify != "" {
			d.logf("%s", out.Notify)
		}
		return
	}
	d.updated.Store(true)
	d.logf("update: %s", out.Reason)
	d.requestStop()
}

// requestStop asks serve to take the same path a SIGTERM takes: drain, then
// bye and close. It never interrupts a job — the drain waits for in-flight work
// exactly as it does on a signal.
func (d *Daemon) requestStop() {
	select {
	case d.stopRequest <- struct{}{}:
	default:
	}
}

// shouldStop reports whether a stop has already been requested.
func (d *Daemon) shouldStop() bool {
	select {
	case <-d.stopRequest:
		return true
	default:
		return false
	}
}
