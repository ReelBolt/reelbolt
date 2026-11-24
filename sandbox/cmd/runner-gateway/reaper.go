package main

import (
	"context"
	"errors"
	"log"
	"time"

	"github.com/jackc/pgx/v5/pgconn"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// This file is the reaper: the loop that takes expired leases back, cancels jobs
// whose execution has ended, and enforces the grace window of a cancellation.
//
// It runs every 10 seconds ("Lease expiry (reaper)" in docs/runner-protocol.md).
// Everything it decides is committed in one transaction per job, and every
// transition is fenced by the epoch, so a reaper on each of several replicas
// cannot double-act: the row lock decides, and the losers find nothing to do.

// pendingCancel is a cancellation awaiting its grace window.
type pendingCancel struct {
	jobID    string
	epoch    int64
	reason   string
	deadline time.Time
}

// runReaper sweeps every ReaperInterval until the context is cancelled.
func (g *gateway) runReaper(ctx context.Context) {
	interval := g.cfg.ReaperInterval
	if interval <= 0 {
		return
	}
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			g.reapOnce(ctx)
		}
	}
}

// reapOnce is one pass of the reaper.
func (g *gateway) reapOnce(ctx context.Context) {
	if g.jobs == nil {
		return
	}
	now := g.now().UTC()
	workCtx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()

	g.expireLeases(workCtx, now)
	g.cancelEndedExecutions(workCtx)
	g.sweepPendingCancels(workCtx)
	g.refreshQueueMetrics(workCtx)
}

// expireLeases takes back every lease that has run out.
func (g *gateway) expireLeases(ctx context.Context, now time.Time) {
	expired, err := g.jobs.ExpireLeases(ctx, now)
	if err != nil {
		log.Printf("reaper: expiring leases failed: %v", err)
		return
	}
	for _, job := range expired {
		g.leaseEventFor(job)
		log.Printf("reaper: job %s (type=%s target=%s attempt=%d) lease expired -> %s",
			shortID(job.JobID), job.JobType, job.Target, job.Attempt, job.Status)
		// A session job is Expired, so nothing will answer an op that was in flight
		// on it: the engine is told session_lost now rather than being left to time
		// out on a socket that belongs to a runner the gateway no longer trusts.
		g.ops.abortJob(job.JobID, "lease_expired", true)
		// The runner may still be running the job; tell it to stop. Its next
		// heartbeat would answer ok:false for the same lease, so this only makes
		// the news arrive sooner.
		if c := g.registry.get(job.RunnerID); c != nil {
			if env, err := protocol.NewJob(protocol.TypeCancel, job.JobID, "",
				protocol.CancelPayload{Reason: protocol.CancelLeaseLost, GraceSec: int(g.cfg.cancelGrace() / time.Second)}); err == nil {
				if err := c.send(ctx, env); err != nil {
					log.Printf("%s: sending cancel{lease_lost} for %s failed: %v", c.logTag, shortID(job.JobID), err)
				} else {
					g.metrics.countMessage(string(protocol.TypeCancel), false)
				}
			}
		}
	}
	if len(expired) > 0 {
		// A requeued job may be claimable right now.
		g.pokeAssigners()
	}
}

// cancelEndedExecutions cancels jobs whose workflow execution is no longer
// Running: nobody will read their ops, so they must not hold a runner slot.
func (g *gateway) cancelEndedExecutions(ctx context.Context) {
	jobs, err := g.jobs.JobsForEndedExecutions(ctx, 100)
	if err != nil {
		if isMissingTable(err) {
			// A deployment whose engine owns no such table (the self-host profile
			// runs Compute:Mode=Local and has no runner work) simply has no jobs
			// to cancel here. Reported once per sweep at most, never as an error.
			g.metrics.executionSweepUnavailable.Inc()
			return
		}
		log.Printf("reaper: reading jobs of ended executions failed: %v", err)
		return
	}
	for _, job := range jobs {
		log.Printf("reaper: job %s belongs to an execution that is no longer running, cancelling",
			shortID(job.ID))
		g.cancelJob(ctx, job, protocol.CancelExecutionEnded)
	}
}

// sweepPendingCancels is the safety net for the grace window: the per-job timer
// normally fires first, and this catches a timer that was lost (a clock jump, a
// job whose row was updated by another replica in the meantime is simply not
// cancelled twice).
func (g *gateway) sweepPendingCancels(ctx context.Context) {
	for _, pending := range g.overduePendingCancels(g.now().UTC()) {
		g.expirePendingCancel(ctx, pending.jobID, pending.epoch)
	}
}

// refreshQueueMetrics republishes the queue-depth gauge.
func (g *gateway) refreshQueueMetrics(ctx context.Context) {
	depth, err := g.jobs.QueueDepth(ctx)
	if err != nil {
		log.Printf("reaper: reading the queue depth failed: %v", err)
		return
	}
	g.metrics.setQueueDepth(depth)
}

// --- in-memory cancellation bookkeeping -------------------------------------

// addPendingCancel records a cancellation waiting for its grace window.
func (g *gateway) addPendingCancel(pending pendingCancel) {
	g.cancelMu.Lock()
	defer g.cancelMu.Unlock()
	if g.pendingCancels == nil {
		g.pendingCancels = map[string]pendingCancel{}
	}
	g.pendingCancels[pending.jobID] = pending
}

// takePendingCancel removes and reports a pending cancellation, so exactly one
// of the timer and the sweep acts on it.
func (g *gateway) takePendingCancel(jobID string) bool {
	g.cancelMu.Lock()
	defer g.cancelMu.Unlock()
	if _, ok := g.pendingCancels[jobID]; !ok {
		return false
	}
	delete(g.pendingCancels, jobID)
	return true
}

// clearPendingCancel drops a pending cancellation because the job reached a
// terminal state on its own.
func (g *gateway) clearPendingCancel(jobID string) {
	g.cancelMu.Lock()
	defer g.cancelMu.Unlock()
	delete(g.pendingCancels, jobID)
}

// overduePendingCancels lists the cancellations whose window has closed.
func (g *gateway) overduePendingCancels(now time.Time) []pendingCancel {
	g.cancelMu.Lock()
	defer g.cancelMu.Unlock()
	var out []pendingCancel
	for _, pending := range g.pendingCancels {
		if !pending.deadline.After(now) {
			out = append(out, pending)
		}
	}
	return out
}

// --- assignment loop --------------------------------------------------------

// runAssigner re-examines every connected runner with free slots, on a timer and
// whenever the queue announces a new job or a lease is released.
func (g *gateway) runAssigner(ctx context.Context) {
	interval := g.cfg.AssignInterval
	if interval <= 0 {
		return
	}
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		case <-g.assignWake:
		}
		g.assignAll(ctx)
	}
}

// assignAll runs one assignment attempt per connected runner.
func (g *gateway) assignAll(ctx context.Context) {
	if g.jobs == nil {
		return
	}
	for _, c := range g.registry.snapshot() {
		g.assignWork(ctx, c)
	}
}

// pokeAssigners asks the assignment loop to run now: a job was created, a lease
// was released, or the reaper requeued something.
func (g *gateway) pokeAssigners() {
	select {
	case g.assignWake <- struct{}{}:
	default:
		// A wakeup is already pending: one pass sees the same queue.
	}
}

// --- queue notifications ----------------------------------------------------

// runJobListener keeps a LISTEN on runner_jobs_new open and pokes the assigner
// whenever the queue announces work. The channel is what makes a job created for
// an organisation whose runner is already idle start immediately instead of
// waiting for that runner's next heartbeat.
//
// A failure is never fatal: the listener reconnects with backoff, and the
// assignment ticker is the backstop that makes the gateway correct without it.
func (g *gateway) runJobListener(ctx context.Context) {
	if g.jobs == nil {
		return
	}
	notifier, ok := g.jobs.(JobNotifier)
	if !ok {
		log.Printf("queue: this store cannot listen for new jobs, so the assignment ticker is the only wakeup")
		return
	}
	backoff := 500 * time.Millisecond
	for ctx.Err() == nil {
		err := notifier.Listen(ctx, func() {
			g.metrics.notifications.Inc()
			g.pokeAssigners()
		})
		if ctx.Err() != nil {
			return
		}
		g.metrics.notifyReconnects.Inc()
		log.Printf("queue listener: %v, retrying in %s", err, backoff)
		select {
		case <-ctx.Done():
			return
		case <-time.After(backoff):
		}
		if backoff < 30*time.Second {
			backoff *= 2
		}
	}
}

// isMissingTable reports whether an error is Postgres' undefined_table (SQLSTATE
// 42P01): a table this deployment does not have is "nothing to do", not a fault.
func isMissingTable(err error) bool {
	var pgErr *pgconn.PgError
	if errors.As(err, &pgErr) {
		return pgErr.Code == "42P01"
	}
	return false
}

// pendingCancelCount reports how many cancellations are waiting; used by tests
// and by the health output.
func (g *gateway) pendingCancelCount() int {
	g.cancelMu.Lock()
	defer g.cancelMu.Unlock()
	return len(g.pendingCancels)
}
