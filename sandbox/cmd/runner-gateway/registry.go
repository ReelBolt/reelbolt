package main

import (
	"context"
	"fmt"
	"sync"
	"sync/atomic"
	"time"

	"github.com/coder/websocket"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// conn is one live runner connection: the gateway's whole view of a socket.
//
// Its immutable identity is set once at handshake. The mutable state (free
// slots, the jobs the runner says it holds, last seen, draining) is written by
// the connection's own goroutine and read by the revocation sweeper and by
// metrics scrapes, so it is mutex-guarded.
//
// D5b claims work against freeSlots and assigns through send; D5c correlates op
// responses here.
type conn struct {
	runnerID string
	kind     string
	orgID    string
	name     string
	remote   string
	host     string
	caps     protocol.Capabilities
	logTag   string
	// protocolVersion is the version negotiated at hello: the highest minor both
	// ends speak, echoed in welcome.protocolVersion.
	protocolVersion string

	ws *websocket.Conn

	// writeMu serializes writes: coder/websocket permits one concurrent reader
	// and one concurrent writer, and the sweeper closes a connection while the
	// connection's own loop may be replying to a heartbeat.
	writeMu sync.Mutex

	// revoking marks that this connection is being torn down as revoked. The
	// periodic re-check and the RunnerDeviceRevoked event can both see the same
	// socket before either has removed it, and a revocation must run once:
	// cancelling a device's jobs twice is wasted work that races two
	// transactions over the same rows, and the second one can close the socket
	// before the first has committed the jobs' new state.
	revoking atomic.Bool

	mu        sync.Mutex
	freeSlots int
	running   map[string]int64
	lastSeen  time.Time
	lastTouch time.Time
	draining  bool
	connected time.Time

	// offers are assignments sent to this runner that it has not answered yet
	// (jobID -> leaseEpoch). They are counted against freeSlots so a runner that
	// updates its slot count only on its next heartbeat is not handed the same
	// slot twice, and they are dropped when the runner answers or the accept
	// watchdog fires.
	offers map[string]int64
	// rejected remembers the jobs this runner declined, so the gateway does not
	// immediately re-offer one to the same runner ("the gateway may remember the
	// rejection"). It is per connection and never persisted: a runner that
	// reconnects may legitimately get another shot at a job that was requeued
	// while it was away.
	rejected map[string]time.Time
	// sinceHeartbeat holds the jobs this runner accepted since its last
	// heartbeat. freeSlots is the runner's own count and only becomes aware of
	// them at that heartbeat, so a slot would be promised twice if they were not
	// subtracted from it until then. A heartbeat's running list is authoritative
	// and clears the set.
	sinceHeartbeat map[string]struct{}
}

// send writes one message. The context bounds the write, not the connection.
func (c *conn) send(ctx context.Context, env protocol.Envelope) error {
	raw, err := env.Encode()
	if err != nil {
		return err
	}
	c.writeMu.Lock()
	defer c.writeMu.Unlock()
	return c.ws.Write(ctx, websocket.MessageText, raw)
}

// sendPayload builds and sends a connection-scoped message, logging (never
// failing the caller on) a marshal error.
func (c *conn) sendPayload(ctx context.Context, t protocol.MessageType, payload any) error {
	env, err := protocol.New(t, payload)
	if err != nil {
		return err
	}
	return c.send(ctx, env)
}

// closeWith closes the socket with a protocol close code. It is idempotent: a
// connection already being closed keeps its first code.
func (c *conn) closeWith(code protocol.CloseCode, reason string) {
	_ = c.ws.Close(websocket.StatusCode(code), reason)
}

// markSeen records liveness and returns the previous timestamp, so the caller
// can decide whether the registry's last_seen_at is due for a refresh.
func (c *conn) markSeen(now time.Time) time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	previous := c.lastSeen
	c.lastSeen = now
	return previous
}

// lastSeenAt is when the last message arrived on this connection.
func (c *conn) lastSeenAt() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.lastSeen
}

// setFreeSlots records the slots a heartbeat or ready announced.
func (c *conn) setFreeSlots(n int) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.freeSlots = n
}

// addOffer records an assignment that is awaiting an answer.
func (c *conn) addOffer(jobID string, epoch int64) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.offers == nil {
		c.offers = map[string]int64{}
	}
	c.offers[jobID] = epoch
}

// removeOffer drops an assignment the runner has answered (or that the accept
// watchdog released).
func (c *conn) removeOffer(jobID string) {
	c.mu.Lock()
	defer c.mu.Unlock()
	delete(c.offers, jobID)
}

// addSinceHeartbeat records that a job this runner accepted has not yet shown up
// in the runner's own slot count.
func (c *conn) addSinceHeartbeat(jobID string) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.sinceHeartbeat == nil {
		c.sinceHeartbeat = map[string]struct{}{}
	}
	c.sinceHeartbeat[jobID] = struct{}{}
}

// clearSinceHeartbeat drops the jobs accepted since the last heartbeat: the
// heartbeat that just arrived reports them in its running list.
func (c *conn) clearSinceHeartbeat() {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.sinceHeartbeat = map[string]struct{}{}
}

// forgetJob drops every per-job trace of a job that has ended.
func (c *conn) forgetJob(jobID string) {
	c.mu.Lock()
	defer c.mu.Unlock()
	delete(c.offers, jobID)
	delete(c.sinceHeartbeat, jobID)
}

// markRejected remembers that this runner declined a job.
func (c *conn) markRejected(jobID string, now time.Time) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.rejected == nil {
		c.rejected = map[string]time.Time{}
	}
	c.rejected[jobID] = now
}

// rejectedJobs lists the jobs this runner declined inside the memory window.
func (c *conn) rejectedJobs(now time.Time, within time.Duration) []string {
	c.mu.Lock()
	defer c.mu.Unlock()
	out := make([]string, 0, len(c.rejected))
	for jobID, at := range c.rejected {
		if now.Sub(at) > within {
			delete(c.rejected, jobID)
			continue
		}
		out = append(out, jobID)
	}
	return out
}

// setRunning records the jobs a heartbeat listed with their epochs.
func (c *conn) setRunning(running []protocol.JobLease) {
	held := make(map[string]int64, len(running))
	for _, j := range running {
		held[j.JobID] = j.LeaseEpoch
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	c.running = held
}

// beginRevoke claims the one revocation of this connection, so the periodic
// re-check and the revocation event cannot both tear it down.
func (c *conn) beginRevoke() bool { return c.revoking.CompareAndSwap(false, true) }

// markDraining records that the runner will take no new work.
func (c *conn) markDraining() {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.draining = true
}

// touchDue reports whether last_seen_at is due for a refresh and, when it is,
// records that the refresh is happening.
func (c *conn) touchDue(now time.Time, interval time.Duration) bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	if !c.lastTouch.IsZero() && now.Sub(c.lastTouch) < interval {
		return false
	}
	c.lastTouch = now
	return true
}

// connSnapshot is a point-in-time view for metrics and, in D5b, the internal
// "is a runner online?" API.
type connSnapshot struct {
	RunnerID  string
	Kind      string
	OrgID     string
	Name      string
	FreeSlots int
	Running   map[string]int64
	Offers    map[string]int64
	// SinceHeartbeat counts the jobs accepted since the last heartbeat, which the
	// runner's freeSlots does not know about yet.
	SinceHeartbeat int
	Draining       bool
	Connected      time.Time
	LastSeen       time.Time
	Caps           protocol.Capabilities
}

func (c *conn) snapshot() connSnapshot {
	c.mu.Lock()
	defer c.mu.Unlock()
	running := make(map[string]int64, len(c.running))
	for id, epoch := range c.running {
		running[id] = epoch
	}
	offers := make(map[string]int64, len(c.offers))
	for id, epoch := range c.offers {
		offers[id] = epoch
	}
	return connSnapshot{
		RunnerID: c.runnerID, Kind: c.kind, OrgID: c.orgID, Name: c.name,
		FreeSlots: c.freeSlots, Running: running, Offers: offers,
		SinceHeartbeat: len(c.sinceHeartbeat), Draining: c.draining,
		Connected: c.connected, LastSeen: c.lastSeen, Caps: c.caps,
	}
}

// registry is the in-memory index of live runners on this gateway instance. It
// is deliberately not shared between replicas: D5c routes an op to the replica
// that owns a session through runner_sessions.gateway_instance, and D5b's
// "is a runner online?" answer is per instance.
type registry struct {
	mu    sync.Mutex
	conns map[string]*conn // runner id (runner_devices.id) -> connection
}

func newRegistry() *registry {
	return &registry{conns: map[string]*conn{}}
}

// add registers a connection and returns the connection it superseded, if any.
// The specification: when the same device connects while a previous connection
// is open, the new connection wins and the old one is closed with 4409.
func (r *registry) add(c *conn) (superseded *conn) {
	r.mu.Lock()
	defer r.mu.Unlock()
	previous := r.conns[c.runnerID]
	r.conns[c.runnerID] = c
	if previous == c {
		return nil
	}
	return previous
}

// remove drops a connection, but only if it is still the current one: a
// superseded connection closing itself must not evict its replacement.
func (r *registry) remove(c *conn) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.conns[c.runnerID] == c {
		delete(r.conns, c.runnerID)
	}
}

// get returns the live connection for a runner id, or nil.
func (r *registry) get(runnerID string) *conn {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.conns[runnerID]
}

// snapshot returns the live connections.
func (r *registry) snapshot() []*conn {
	r.mu.Lock()
	defer r.mu.Unlock()
	out := make([]*conn, 0, len(r.conns))
	for _, c := range r.conns {
		out = append(out, c)
	}
	return out
}

func (r *registry) len() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.conns)
}

// shortID truncates an id for a log tag: enough to correlate, never a secret.
func shortID(id string) string {
	if len(id) <= 8 {
		return id
	}
	return id[:8]
}

func connLogTag(kind, runnerID, remote string) string {
	return fmt.Sprintf("runner=%s/%s remote=%s", kind, shortID(runnerID), remote)
}
