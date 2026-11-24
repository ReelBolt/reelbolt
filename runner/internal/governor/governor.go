package governor

import (
	"context"
	"sync"
	"time"
)

// Who asked for a pause.
const (
	// SourceUser is the person at this machine (the CLI, or the tray).
	SourceUser = "user"
	// SourceRemote is the gateway. It is the one source the runner keeps a
	// connection for: the gateway must be able to resume what it paused, and a
	// runner that hung up could never hear it.
	SourceRemote = "gateway"
)

// ReasonPaused is the verdict reason while a hold is in force.
const ReasonPaused = "paused"

// DefaultInterval is how often the governor re-reads the machine. It bounds how
// long a runner keeps working after the user comes back, and how long a paused
// machine waits to notice the schedule has opened.
const DefaultInterval = 5 * time.Second

// Config builds a Governor.
type Config struct {
	// Schedule is the policy. The zero value restricts nothing.
	Schedule Schedule
	// Signals reads the machine. Nil means SystemSignals.
	Signals Signals
	// Now is the clock. Nil means time.Now.
	Now func() time.Time
	// Interval is the re-evaluation period. Zero means DefaultInterval.
	Interval time.Duration
	// Log receives every state change. Nil logs nothing.
	Log func(format string, args ...any)
}

// Hold is an explicit pause: a person's instruction, or the gateway's.
type Hold struct {
	By     string    `json:"by"`
	Reason string    `json:"reason,omitempty"`
	At     time.Time `json:"at"`
}

// Status is the governor's whole state, as "status", the tray and the live
// document see it.
type Status struct {
	Allowed  bool   `json:"allowed"`
	Reason   string `json:"reason"`
	Detail   string `json:"detail,omitempty"`
	Degraded bool   `json:"degraded,omitempty"`

	Held       bool   `json:"held"`
	HeldBy     string `json:"heldBy,omitempty"`
	HeldReason string `json:"heldReason,omitempty"`

	Mode     Mode   `json:"mode"`
	Schedule string `json:"schedule"`
	Signals  string `json:"signals,omitempty"`
	Idle     string `json:"idle,omitempty"`
	Power    string `json:"power,omitempty"`
	// NextChange is when a windowed schedule next changes its mind, RFC3339.
	NextChange string `json:"nextChange,omitempty"`
}

// Offline reports whether this runner should stop holding a connection at all.
//
// A local hold, and every schedule gate, take the runner off the gateway: a
// machine that is not working should not be holding a socket, a heartbeat timer
// and a lease. A REMOTE hold does not — the worker agent stops accepting
// assignments by itself when the gateway tells it to drain, and the runner has
// to stay connected to hear the gateway resume it. That asymmetry is the
// library's, not a choice made here: the worker agent exposes no way to clear
// its own draining state, so resuming from a remote hold means reconnecting.
func (s Status) Offline() bool { return !s.Allowed && s.HeldBy != SourceRemote }

// Governor holds the schedule, the machine's state and any explicit pause, and
// broadcasts the verdict.
//
// It is safe for concurrent use. Every method that can change the verdict
// recomputes and, when the verdict actually changed, closes the channel
// Changed returned — so a caller waits on a channel rather than polling.
type Governor struct {
	sigs     Signals
	now      func() time.Time
	log      func(format string, args ...any)
	interval time.Duration

	mu      sync.Mutex
	sched   Schedule
	machine Machine
	hold    *Hold
	verdict Verdict
	changed chan struct{}
}

// New builds a governor and evaluates once, so a caller can read Status
// immediately.
func New(cfg Config) *Governor {
	g := &Governor{
		sched:    cfg.Schedule,
		sigs:     cfg.Signals,
		now:      cfg.Now,
		log:      cfg.Log,
		interval: cfg.Interval,
		changed:  make(chan struct{}),
	}
	if g.sigs == nil {
		g.sigs = SystemSignals{}
	}
	if g.now == nil {
		g.now = time.Now
	}
	if g.interval <= 0 {
		g.interval = DefaultInterval
	}
	g.mu.Lock()
	g.refreshLocked()
	g.mu.Unlock()
	return g
}

// Start re-evaluates on a ticker until the context ends.
func (g *Governor) Start(ctx context.Context) {
	t := time.NewTicker(g.interval)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			g.Refresh()
		}
	}
}

// Changed returns a channel that is closed the next time the verdict changes.
// The channel is replaced on every change, so a waiter must call Changed again
// after it fires.
func (g *Governor) Changed() <-chan struct{} {
	g.mu.Lock()
	defer g.mu.Unlock()
	return g.changed
}

// Schedule returns the policy in force.
func (g *Governor) Schedule() Schedule {
	g.mu.Lock()
	defer g.mu.Unlock()
	return g.sched
}

// SetSchedule replaces the policy and re-evaluates.
func (g *Governor) SetSchedule(s Schedule) {
	g.mu.Lock()
	g.sched = s
	g.refreshLocked()
	g.mu.Unlock()
}

// Signals returns the machine reader, for diagnostics.
func (g *Governor) Signals() Signals { return g.sigs }

// Pause holds this runner. by is one of SourceUser and SourceRemote.
func (g *Governor) Pause(by, reason string) {
	if by == "" {
		by = SourceUser
	}
	g.mu.Lock()
	g.hold = &Hold{By: by, Reason: reason, At: g.now()}
	g.refreshLocked()
	g.mu.Unlock()
}

// Resume clears any hold. It reports whether one was in force.
//
// It is idempotent and unconditional on purpose: whatever put this runner on
// hold, one command releases it. A governor whose only exit is editing a file
// by hand is a governor that stops a machine for good.
func (g *Governor) Resume() bool {
	g.mu.Lock()
	was := g.hold != nil
	g.hold = nil
	g.refreshLocked()
	g.mu.Unlock()
	return was
}

// Hold returns the pause in force, if any.
func (g *Governor) Hold() (Hold, bool) {
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.hold == nil {
		return Hold{}, false
	}
	return *g.hold, true
}

// Allowed reports whether this machine may take a job right now.
func (g *Governor) Allowed() bool {
	g.mu.Lock()
	defer g.mu.Unlock()
	return g.verdict.Allow
}

// Status returns the whole state.
func (g *Governor) Status() Status {
	g.mu.Lock()
	defer g.mu.Unlock()
	st := Status{
		Allowed:  g.verdict.Allow,
		Reason:   g.verdict.Reason,
		Detail:   g.verdict.Detail,
		Degraded: g.verdict.Degraded,
		Mode:     g.sched.Mode,
		Schedule: g.sched.Describe(),
		Signals:  g.sigs.Describe(),
	}
	if g.hold != nil {
		st.Held, st.HeldBy, st.HeldReason = true, g.hold.By, g.hold.Reason
	}
	if g.sched.NeedsIdle() {
		st.Idle = "unknown"
		if g.machine.IdleKnown {
			st.Idle = g.machine.IdleFor.Round(time.Second).String()
		}
	}
	if g.sched.NeedsPower() {
		switch {
		case !g.machine.PowerKnown:
			st.Power = "unknown"
		case g.machine.OnAC:
			st.Power = "ac"
		default:
			st.Power = "battery"
		}
	}
	if next, ok := g.sched.NextChange(g.now()); ok {
		st.NextChange = next.Format(time.RFC3339)
	}
	return st
}

// Refresh re-reads the machine and re-evaluates.
func (g *Governor) Refresh() {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.refreshLocked()
}

// refreshLocked recomputes the verdict and broadcasts a change. The caller
// holds the lock.
func (g *Governor) refreshLocked() {
	m := Machine{}
	if g.sched.NeedsIdle() {
		if d, ok := g.sigs.IdleFor(); ok {
			m.IdleFor, m.IdleKnown = d, true
		}
	}
	if g.sched.NeedsPower() {
		if on, ok := g.sigs.OnAC(); ok {
			m.OnAC, m.PowerKnown = on, true
		}
	}
	g.machine = m

	v := g.sched.Evaluate(g.now(), m)
	if g.hold != nil {
		v = Verdict{Allow: false, Reason: ReasonPaused, Detail: g.hold.Reason}
	}
	if v == g.verdict {
		return
	}
	first := g.verdict == Verdict{}
	g.verdict = v
	// Close the previous channel and publish a fresh one: everyone waiting on
	// the old one wakes, and everyone who reads Changed after this gets the one
	// that will fire next.
	close(g.changed)
	g.changed = make(chan struct{})
	if g.log != nil && !first {
		if v.Detail == "" {
			g.log("governor: %s", v.Reason)
		} else {
			g.log("governor: %s (%s)", v.Reason, v.Detail)
		}
	}
}
