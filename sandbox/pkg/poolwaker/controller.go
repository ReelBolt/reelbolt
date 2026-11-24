package poolwaker

import (
	"context"
	"errors"
	"fmt"
	"log"
	"math"
	"strconv"
	"strings"
	"time"
)

// NamePrefix is the start of every node name this waker creates or touches.
const NamePrefix = "rb-pool-"

// Node lifecycle: starting -> connected -> idle -> draining -> (deleted).
type nodeState string

const (
	stStarting  nodeState = "starting"
	stConnected nodeState = "connected"
	stIdle      nodeState = "idle"
	stDraining  nodeState = "draining"
)

type tracked struct {
	Name       string
	ID         string
	State      nodeState
	ServerType string
	Hourly     float64
	CreatedAt  time.Time
	ReadyAt    time.Time
	IdleSince  time.Time
	GoneSince  time.Time
	BilledHrs  int
	EndReason  string
	EverListed bool
}

// Controller is the pool waker's control loop. It is single-goroutine: Tick
// must not be called concurrently.
type Controller struct {
	cfg    Config
	prov   Provider
	gw     Gateway
	ledger *Ledger
	usage  *UsageWriter
	clock  Clock
	logf   func(format string, args ...any)

	nodes        map[string]*tracked
	lastScaleUp  time.Time
	bootFailures int
	breakerUntil time.Time
	provErrs     int
	seq          int
	metrics      *metricSet
	lastHB       Heartbeat
	// verified holds the ids List returned this tick that passed the ownership
	// check; owned holds ids of servers this controller created. Delete is only
	// ever issued for an id in one of them.
	verified map[string]bool
	owned    map[string]bool
	// refused maps a server size the provider refused to the time it may be tried again.
	refused map[string]time.Time
}

// NewController builds a controller. usage may be nil; clock nil means wall clock.
func NewController(cfg Config, prov Provider, gw Gateway, ledger *Ledger, usage *UsageWriter, clock Clock) (*Controller, error) {
	if err := cfg.Normalize(); err != nil {
		return nil, err
	}
	if prov == nil || gw == nil || ledger == nil {
		return nil, fmt.Errorf("provider, gateway and ledger are required")
	}
	if clock == nil {
		clock = RealClock{}
	}
	return &Controller{
		cfg: cfg, prov: prov, gw: gw, ledger: ledger, usage: usage, clock: clock,
		logf: log.Printf, nodes: map[string]*tracked{}, metrics: newMetrics(),
		verified: map[string]bool{}, owned: map[string]bool{}, refused: map[string]time.Time{},
	}, nil
}

// SetLogf replaces the logger (tests).
func (c *Controller) SetLogf(f func(string, ...any)) { c.logf = f }

// Metrics returns the Prometheus text.
func (c *Controller) Metrics() string { return c.metrics.Text() }

// LastHeartbeat returns the heartbeat sent (or computed in dry-run) by the last Tick.
func (c *Controller) LastHeartbeat() Heartbeat { return c.lastHB }

func (c *Controller) newName(now time.Time) string {
	c.seq++
	return NamePrefix + strconv.FormatInt(now.Unix(), 36) + "-" + strconv.Itoa(c.seq)
}

// Tick runs one iteration of the loop.
func (c *Controller) Tick(ctx context.Context) {
	now := c.clock.Now()

	listed, listErr := c.prov.List(ctx, c.cfg.Instance)
	if listErr != nil {
		c.provErrs++
		c.metrics.add("provider_errors_total", 1)
		c.logf("poolwaker: provider list failed (%d in a row): %v", c.provErrs, listErr)
		c.heartbeat(ctx, now, len(c.nodes), false, "")
		return
	}
	c.provErrs = 0

	runners, runErr := c.gw.Runners(ctx)
	demand, demErr := c.gw.Demand(ctx)
	gwOK := runErr == nil && demErr == nil
	if !gwOK {
		c.logf("poolwaker: gateway unreadable (runners: %v, demand: %v)", runErr, demErr)
	}
	byName := map[string]Runner{}
	for _, r := range runners {
		byName[r.Name] = r
	}

	c.verified = map[string]bool{}
	present := map[string]bool{}
	listedNames := map[string]bool{}
	for _, n := range listed {
		// The provider contract says List is scoped to our labels; verify anyway,
		// because this may be a project that holds real servers.
		if n.ID == "" || n.Instance != c.cfg.Instance || !strings.HasPrefix(n.Name, NamePrefix) {
			c.logf("WARN poolwaker: provider listed server %q that is not ours; ignoring it", n.ID)
			continue
		}
		c.verified[n.ID] = true
		listedNames[n.Name] = true
		present[n.Name] = true
		st := c.nodes[n.Name]
		if st == nil {
			age := now.Sub(n.CreatedAt)
			_, connected := byName[n.Name]
			switch {
			case gwOK && connected && age <= c.cfg.MaxLifetime:
				// Restart: a working node is adopted, never reaped.
				hourly := n.HourlyEUR
				if hourly <= 0 {
					hourly, _ = c.prov.HourlyEUR(ctx, n.ServerType)
				}
				hrs := int(math.Ceil(age.Seconds() / 3600))
				if hrs < 1 {
					hrs = 1
				}
				c.nodes[n.Name] = &tracked{
					Name: n.Name, ID: n.ID, State: stConnected, ServerType: n.ServerType, Hourly: hourly,
					CreatedAt: n.CreatedAt, ReadyAt: n.CreatedAt, BilledHrs: hrs, EverListed: true,
				}
				c.metrics.add("nodes_adopted_total", 1)
				c.logf("poolwaker: adopted connected node %s", n.Name)
				continue
			case age <= c.cfg.MaxLifetime && (!gwOK || age <= c.cfg.BootDeadline):
				// Possibly still booting (or the gateway cannot tell): leave it, it counts against the cap.
				continue
			}
			// Orphan: carries our labels, unknown to this controller, not working.
			c.metrics.add("orphans_found_total", 1)
			c.logf("poolwaker: orphan node %s (%s), deleting", n.Name, n.ID)
			if c.deleteID(ctx, n.ID) {
				delete(present, n.Name)
				c.writeUsage(n.Name, n.ServerType, n.CreatedAt, time.Time{}, n.HourlyEUR, now, "orphan")
			}
			continue
		}
		st.ID = n.ID
		st.EverListed = true
	}
	// Bookkeeping nodes the provider no longer lists.
	for name, st := range c.nodes {
		if listedNames[name] {
			continue
		}
		if !st.EverListed && now.Sub(st.CreatedAt) < c.cfg.createLagGrace {
			present[name] = true
			continue
		}
		c.logf("poolwaker: node %s is gone from the provider", name)
		c.finish(ctx, st, now, "vanished")
	}

	// Per-node lifecycle.
	for name, st := range c.nodes {
		if !present[name] {
			continue
		}
		if st.State == stDraining {
			c.end(ctx, st, now, st.EndReason, present)
			continue
		}
		if now.Sub(st.CreatedAt) > c.cfg.MaxLifetime {
			c.end(ctx, st, now, "max_lifetime", present)
			continue
		}
		if !c.cfg.DryRun && c.hourGuard(ctx, now, st, byName, gwOK, demand, present) {
			continue
		}
		if !gwOK {
			continue
		}
		r, connected := byName[name]
		switch {
		case connected:
			st.GoneSince = time.Time{}
			if st.ReadyAt.IsZero() {
				st.ReadyAt = now
				c.bootFailures = 0
			}
			if r.Running > 0 {
				st.State, st.IdleSince = stConnected, time.Time{}
			} else {
				if st.State != stIdle {
					st.State, st.IdleSince = stIdle, now
				}
				switch {
				case demand.Queued != 0:
					// Work is waiting: an idle node is exactly what it needs.
				case c.cfg.IdleWindow > 0 && now.Sub(st.IdleSince) >= c.cfg.IdleWindow:
					// Optional earlier end (POOL_IDLE_SECONDS); off by default.
					c.end(ctx, st, now, "idle", present)
				case !now.Before(c.hourEnd(st)):
					// The hour already paid is nearly over: stop before the next one starts.
					c.end(ctx, st, now, "hour_end", present)
				}
			}
		case st.ReadyAt.IsZero():
			if now.Sub(st.CreatedAt) > c.cfg.BootDeadline {
				c.metrics.add("boot_failures_total", 1)
				c.bootFailures++
				if c.bootFailures >= c.cfg.BreakerFailures {
					c.breakerUntil = now.Add(c.cfg.BreakerBackoff)
					c.bootFailures = 0
					c.metrics.add("breaker_opened_total", 1)
					c.logf("poolwaker: circuit breaker open until %s after repeated boot failures", c.breakerUntil.Format(time.RFC3339))
				}
				c.end(ctx, st, now, "boot_deadline", present)
			}
		default:
			if st.GoneSince.IsZero() {
				st.GoneSince = now
			}
			if now.Sub(st.GoneSince) > c.cfg.DeadGrace {
				c.end(ctx, st, now, "worker_gone", present)
			}
		}
	}

	// Spend state.
	sel := c.selectType(ctx, now)
	priceKnown, capped := sel.priceKnown, sel.capped
	breakerOpen := now.Before(c.breakerUntil)

	// Scale up.
	if gwOK {
		c.scaleUp(ctx, now, demand, byName, present, &capped, breakerOpen)
	}
	// Scaling up may have changed the picture (a size refused, the cap used up): report that one.
	sel = c.selectType(ctx, now)
	priceKnown = sel.priceKnown
	capped = capped || sel.capped

	c.metrics.set("nodes", float64(len(present)))
	c.metrics.set("max_nodes", float64(c.cfg.MaxNodes))
	c.metrics.set("spend_today_eur", c.ledger.Spent(now))
	c.metrics.set("spend_cap_eur", c.cfg.DailyCapEUR)
	c.metrics.set("capped", b2f(capped))
	c.metrics.set("breaker_open", b2f(breakerOpen))

	reason := ""
	switch {
	case !gwOK:
		reason = "gateway_unreadable"
	case c.provErrs >= c.cfg.ProviderErrors:
		reason = "provider_errors"
	case breakerOpen:
		reason = "breaker_open"
	case sel.allRefused:
		reason = "size_unavailable"
	case !priceKnown:
		reason = "price_unknown"
	case capped:
		reason = "daily_cap_reached"
	}
	c.heartbeat(ctx, now, len(present), capped, reason)
}

// selection is the outcome of choosing a server size for the next node.
type selection struct {
	serverType string
	price      float64
	ok         bool // a size can be created now
	// priceKnown: at least one size that is not backing off has a known price.
	priceKnown bool
	// capped: priced sizes exist but none fits under the daily cap.
	capped bool
	// allRefused: every configured size is backing off after a provider refusal.
	allRefused bool
}

// selectType walks the configured sizes in order and returns the first one that is not backing off
// after a refusal, has a known price (fail closed otherwise) and fits under the daily cap. A
// cheaper size further down the list is used when the preferred one no longer fits the cap.
func (c *Controller) selectType(ctx context.Context, now time.Time) selection {
	var sel selection
	tried := 0
	for _, t := range c.cfg.ServerTypes {
		if until, ok := c.refused[t]; ok && now.Before(until) {
			continue
		}
		tried++
		price, known := c.prov.HourlyEUR(ctx, t)
		if !known || price <= 0 {
			continue
		}
		sel.priceKnown = true
		if !c.ledger.CanSpend(now, price, c.cfg.DailyCapEUR) {
			continue
		}
		sel.serverType, sel.price, sel.ok = t, price, true
		return sel
	}
	sel.capped = sel.priceKnown
	sel.allRefused = tried == 0
	return sel
}

func (c *Controller) scaleUp(ctx context.Context, now time.Time, d Demand, byName map[string]Runner,
	present map[string]bool, capped *bool, breakerOpen bool) {
	desired := d.DesiredNodes
	if desired > c.cfg.MaxNodes {
		desired = c.cfg.MaxNodes
	}
	active := 0
	for name, st := range c.nodes {
		if present[name] && st.State != stDraining {
			active++
		}
	}
	want := desired - active
	// A node that is connected and free, or still booting, will take a waiting job itself: only the
	// jobs beyond what those nodes can hold need a new server. This is also why an idle node kept
	// until the end of its paid hour is never followed by a second one for the same job.
	if free := c.spareSlots(present, byName); int64(free) >= d.Queued {
		if want > 0 {
			if d.Queued == 0 {
				c.logf("poolwaker: no job is waiting for a pool worker (running=%d); not creating", d.Running)
			} else {
				c.logf("poolwaker: %d job(s) waiting for a worker fit on %d free slot(s) of existing nodes; not creating", d.Queued, free)
			}
		}
		return
	} else if need := int((d.Queued - int64(free) + int64(c.cfg.SlotsPerNode) - 1) / int64(c.cfg.SlotsPerNode)); want > need {
		want = need
	}
	if want <= 0 {
		return
	}
	if breakerOpen || c.provErrs >= c.cfg.ProviderErrors {
		return
	}
	if !c.lastScaleUp.IsZero() && now.Sub(c.lastScaleUp) < c.cfg.Cooldown {
		return
	}
	for i := 0; i < want; i++ {
		// Hard cap counts everything the provider shows plus pending, not our own bookkeeping alone.
		if len(present) >= c.cfg.MaxNodes {
			c.logf("poolwaker: node cap %d reached", c.cfg.MaxNodes)
			return
		}
		sel := c.selectType(ctx, now)
		switch {
		case sel.allRefused:
			c.logf("poolwaker: every configured size is backing off after a provider refusal; not creating")
			return
		case !sel.priceKnown:
			c.logf("poolwaker: price for %s unknown; refusing to create (fail closed)", strings.Join(c.cfg.ServerTypes, ","))
			return
		case !sel.ok:
			*capped = true
			c.logf("poolwaker: daily spend cap EUR %.2f reached; not creating", c.cfg.DailyCapEUR)
			return
		}
		price := sel.price
		if c.cfg.DryRun {
			c.logf("poolwaker: dry-run: would create a %s node (EUR %.4f/h)", sel.serverType, price)
			return
		}
		name := c.newName(now)
		if !strings.HasPrefix(name, NamePrefix) {
			c.logf("poolwaker: refusing to create a node named outside %s*", NamePrefix)
			return
		}
		tok, err := c.gw.MintNode(ctx, name, c.cfg.TokenTTL)
		if err != nil {
			c.logf("poolwaker: mint node credential failed: %v", err)
			return
		}
		ud, err := RenderUserData(UserDataParams{
			GatewayURL: c.cfg.NodeGatewayURL, Name: name, Token: tok.Token, CPULimit: c.cfg.CPULimit,
		})
		if err != nil {
			_ = c.gw.RevokeNode(ctx, name)
			c.logf("poolwaker: render user-data failed: %v", err)
			return
		}
		node, err := c.prov.Create(ctx, NodeSpec{
			Name: name, Instance: c.cfg.Instance, ServerType: sel.serverType, UserData: ud,
		})
		if err != nil {
			_ = c.gw.RevokeNode(ctx, name)
			if errors.Is(err, ErrSizeRefused) {
				// Not an outage and not a boot failure: leave this size alone for a while and try the
				// next one in the list. Nothing was created, so nothing is charged.
				c.refused[sel.serverType] = now.Add(c.cfg.SizeRefusalBackoff)
				c.metrics.add("size_refused_total", 1)
				c.logf("poolwaker: %s refused (%v); not retrying it for %s", sel.serverType, err, c.cfg.SizeRefusalBackoff)
				i--
				continue
			}
			c.provErrs++
			c.metrics.add("provider_errors_total", 1)
			c.logf("poolwaker: create %s failed: %v", name, err)
			return
		}
		c.provErrs = 0
		if node.Name != name || node.ID == "" {
			c.logf("WARN poolwaker: provider created server %q with an unexpected name; not tracking it", node.ID)
			return
		}
		c.owned[node.ID] = true
		c.charge(now, price)
		c.nodes[name] = &tracked{
			Name: name, ID: node.ID, State: stStarting, ServerType: sel.serverType,
			Hourly: price, CreatedAt: now, BilledHrs: 1,
		}
		present[name] = true
		c.lastScaleUp = now
		c.metrics.add("nodes_created_total", 1)
		c.logf("poolwaker: created node %s (%s)", name, sel.serverType)
	}
}

// paidUntil is the end of the last hour charged for this node. Hetzner bills every started hour
// in full, counted from the server's created time, so an idle node costs nothing more until then.
func (st *tracked) paidUntil() time.Time {
	return st.CreatedAt.Add(time.Duration(st.BilledHrs) * time.Hour)
}

// hourEnd is when an idle node is let go: the end of its paid hour minus the configured margin.
func (c *Controller) hourEnd(st *tracked) time.Time {
	return st.paidUntil().Add(-c.cfg.HourEndMargin)
}

// spareSlots counts the job slots the live nodes can still take now: connected nodes minus what
// they run, plus every node that is still booting and has not yet shown it is gone.
func (c *Controller) spareSlots(present map[string]bool, byName map[string]Runner) int {
	free := 0
	for name, st := range c.nodes {
		if !present[name] || st.State == stDraining {
			continue
		}
		if r, connected := byName[name]; connected {
			if n := c.cfg.SlotsPerNode - r.Running; n > 0 {
				free += n
			}
		} else if st.ReadyAt.IsZero() {
			free += c.cfg.SlotsPerNode
		}
	}
	return free
}

// hourGuard keeps the ledger strictly under the cap as a node enters each new
// billed hour. Shortly before the boundary it reserves the next hour; when that
// does not fit, an idle node is deleted at once and a busy one is deleted just
// before the boundary. It reports whether the node was ended (or is being retried).
func (c *Controller) hourGuard(ctx context.Context, now time.Time, st *tracked, byName map[string]Runner, gwOK bool, demand Demand, present map[string]bool) bool {
	for {
		boundary := st.paidUntil()
		if now.Add(c.cfg.HourLookahead).Before(boundary) {
			return false
		}
		if r, connected := byName[st.Name]; gwOK && connected && r.Running == 0 && demand.Queued == 0 {
			// Idle with nothing waiting: let the paid hour run out instead of buying another.
			c.end(ctx, st, now, "hour_end", present)
			return true
		}
		if c.ledger.CanSpend(now, st.Hourly, c.cfg.DailyCapEUR) {
			c.charge(now, st.Hourly)
			st.BilledHrs++
			continue
		}
		r, connected := byName[st.Name]
		busy := !gwOK || (connected && r.Running > 0)
		if !busy || !now.Add(c.cfg.HourHardStop).Before(boundary) {
			c.end(ctx, st, now, "spend_cap", present)
			return true
		}
		return false
	}
}

func (c *Controller) charge(now time.Time, cost float64) {
	alert, err := c.ledger.Charge(now, cost, c.cfg.DailyCapEUR, c.cfg.AlertFraction)
	if err != nil {
		c.logf("poolwaker: ledger write failed: %v", err)
	}
	if alert {
		c.metrics.add("spend_alerts_total", 1)
		c.logf("WARN poolwaker: daily spend reached %.0f%% of the EUR %.2f cap", c.cfg.AlertFraction*100, c.cfg.DailyCapEUR)
	}
}

// end drains (revokes the credential) and deletes a tracked node.
func (c *Controller) end(ctx context.Context, st *tracked, now time.Time, reason string, present map[string]bool) {
	st.State, st.EndReason = stDraining, reason
	if c.cfg.DryRun {
		c.logf("poolwaker: dry-run: would delete %s (%s)", st.Name, reason)
		return
	}
	if err := c.gw.RevokeNode(ctx, st.Name); err != nil {
		c.logf("poolwaker: revoke %s failed: %v", st.Name, err)
	}
	if st.ID == "" || !c.deleteID(ctx, st.ID) {
		return
	}
	delete(present, st.Name)
	c.finish(ctx, st, now, reason)
}

// finish writes the usage record and drops the bookkeeping.
func (c *Controller) finish(ctx context.Context, st *tracked, now time.Time, reason string) {
	if reason == "vanished" {
		_ = c.gw.RevokeNode(ctx, st.Name)
	}
	c.writeUsage(st.Name, st.ServerType, st.CreatedAt, st.ReadyAt, st.Hourly, now, reason)
	delete(c.nodes, st.Name)
}

func (c *Controller) deleteID(ctx context.Context, id string) bool {
	if !c.verified[id] && !c.owned[id] {
		c.logf("WARN poolwaker: refusing to delete server %q: not verified as ours", id)
		c.metrics.add("delete_refused_total", 1)
		return false
	}
	if c.cfg.DryRun {
		c.logf("poolwaker: dry-run: would delete server %s", id)
		return false
	}
	if err := c.prov.Delete(ctx, id); err != nil {
		c.provErrs++
		c.metrics.add("provider_errors_total", 1)
		c.logf("poolwaker: delete %s failed: %v", id, err)
		return false
	}
	delete(c.owned, id)
	delete(c.verified, id)
	c.metrics.add("nodes_deleted_total", 1)
	return true
}

func (c *Controller) writeUsage(name, serverType string, created, ready time.Time, hourly float64, ended time.Time, reason string) {
	secs := int64(ended.Sub(created).Seconds())
	if secs < 0 {
		secs = 0
	}
	if !ready.IsZero() {
		secs = int64(ended.Sub(ready).Seconds())
	}
	hours := int(math.Ceil(ended.Sub(created).Seconds() / 3600))
	if hours < 1 {
		hours = 1
	}
	rec := UsageRecord{
		Node: name, Provider: c.prov.Name(), ServerType: serverType, Instance: c.cfg.Instance,
		CreatedAt: created.UTC(), EndedAt: ended.UTC(), NodeSeconds: secs, BilledHours: hours,
		EstimatedEUR: math.Round(float64(hours)*hourly*1e6) / 1e6, Reason: reason,
	}
	if !ready.IsZero() {
		r := ready.UTC()
		rec.ReadyAt = &r
	}
	c.metrics.add("node_seconds_total", float64(secs))
	if err := c.usage.Write(rec); err != nil {
		c.logf("poolwaker: usage record write failed: %v", err)
	}
}

func (c *Controller) heartbeat(ctx context.Context, now time.Time, nodes int, capped bool, reason string) {
	spent := c.ledger.Spent(now)
	if reason == "" && c.provErrs >= c.cfg.ProviderErrors {
		reason = "provider_errors"
	}
	healthy := reason == ""
	if healthy {
		reason = "ok"
		if spent >= c.cfg.DailyCapEUR*c.cfg.AlertFraction {
			reason = "spend_alert"
		}
	}
	hb := Heartbeat{
		Healthy: healthy, Reason: reason, Nodes: nodes, MaxNodes: c.cfg.MaxNodes,
		Capped: capped, SpendTodayEUR: math.Round(spent*1e4) / 1e4, Instance: c.cfg.Instance,
	}
	c.lastHB = hb
	if c.cfg.DryRun {
		return
	}
	if err := c.gw.Heartbeat(ctx, hb); err != nil {
		c.logf("poolwaker: heartbeat failed: %v", err)
	}
}

// Shutdown tells the gateway the waker is going away (healthy=false).
func (c *Controller) Shutdown(ctx context.Context) {
	if c.cfg.DryRun {
		return
	}
	now := c.clock.Now()
	hb := Heartbeat{
		Healthy: false, Reason: "shutdown", Nodes: len(c.nodes), MaxNodes: c.cfg.MaxNodes,
		SpendTodayEUR: c.ledger.Spent(now), Instance: c.cfg.Instance,
	}
	c.lastHB = hb
	if err := c.gw.Heartbeat(ctx, hb); err != nil {
		c.logf("poolwaker: shutdown heartbeat failed: %v", err)
	}
}

// Run ticks every interval until ctx ends, then sends the shutdown heartbeat.
func (c *Controller) Run(ctx context.Context, interval time.Duration) {
	t := time.NewTicker(interval)
	defer t.Stop()
	for {
		c.Tick(ctx)
		select {
		case <-ctx.Done():
			sctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
			c.Shutdown(sctx)
			cancel()
			return
		case <-t.C:
		}
	}
}

func b2f(b bool) float64 {
	if b {
		return 1
	}
	return 0
}
