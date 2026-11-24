package poolwaker

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

type fakeClock struct{ t time.Time }

func (c *fakeClock) Now() time.Time          { return c.t }
func (c *fakeClock) Advance(d time.Duration) { c.t = c.t.Add(d) }

type rig struct {
	t     *testing.T
	clk   *fakeClock
	prov  *Fake
	gw    *FakeGateway
	c     *Controller
	dir   string
	logs  []string
	usage string
}

func newRig(t *testing.T, mut func(*Config)) *rig {
	t.Helper()
	dir := t.TempDir()
	clk := &fakeClock{t: time.Date(2026, 3, 1, 10, 0, 0, 0, time.UTC)}
	r := &rig{t: t, clk: clk, prov: NewFake(clk), gw: NewFakeGateway(), dir: dir, usage: filepath.Join(dir, "usage.jsonl")}
	cfg := Config{Instance: "staging", ServerType: "cpx42", NodeGatewayURL: "wss://gw.example/ws", MaxNodes: 2}
	if mut != nil {
		mut(&cfg)
	}
	r.c = r.build(cfg)
	return r
}

func (r *rig) build(cfg Config) *Controller {
	led, err := OpenLedger(filepath.Join(r.dir, "ledger.json"), r.clk.Now())
	if err != nil {
		r.t.Fatal(err)
	}
	c, err := NewController(cfg, r.prov, r.gw, led, NewUsageWriter(r.usage), r.clk)
	if err != nil {
		r.t.Fatal(err)
	}
	c.SetLogf(func(f string, a ...any) { r.logs = append(r.logs, strings.TrimSpace(sprintf(f, a...))) })
	return c
}

func (r *rig) tick() { r.c.Tick(context.Background()) }

func (r *rig) names() []string {
	var out []string
	l, _ := r.prov.List(context.Background(), "staging")
	for _, n := range l {
		out = append(out, n.Name)
	}
	return out
}

func (r *rig) logged(sub string) int {
	n := 0
	for _, l := range r.logs {
		if strings.Contains(l, sub) {
			n++
		}
	}
	return n
}

func TestScaleUpAndCooldown(t *testing.T) {
	r := newRig(t, nil)
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	if r.prov.Count() != 1 {
		t.Fatalf("want 1 node, got %d", r.prov.Count())
	}
	r.gw.DemandVal = Demand{DesiredNodes: 2, Queued: 2}
	r.clk.Advance(30 * time.Second)
	r.tick()
	if r.prov.Count() != 1 {
		t.Fatalf("cooldown must block second create, got %d", r.prov.Count())
	}
	r.clk.Advance(31 * time.Second)
	r.tick()
	if r.prov.Count() != 2 {
		t.Fatalf("after cooldown want 2, got %d", r.prov.Count())
	}
	if len(r.gw.Minted) != 2 {
		t.Fatalf("each node mints a credential, got %v", r.gw.Minted)
	}
}

func TestIdleOverrideEndsEarly(t *testing.T) {
	r := newRig(t, func(c *Config) { c.IdleWindow = 300 * time.Second })
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	name := r.names()[0]
	r.prov.SetReady(name)
	r.gw.Connect(name, 1)
	r.clk.Advance(10 * time.Second)
	r.tick()
	r.gw.DemandVal = Demand{}
	r.gw.Connect(name, 0)
	r.clk.Advance(10 * time.Second)
	r.tick() // idle starts
	r.clk.Advance(200 * time.Second)
	r.tick()
	if r.prov.Count() != 1 {
		t.Fatal("must not delete before idle window")
	}
	r.clk.Advance(101 * time.Second)
	r.tick()
	if r.prov.Count() != 0 {
		t.Fatal("idle node must be deleted after the window")
	}
	if len(r.gw.Revoked) == 0 || r.gw.Revoked[0] != name {
		t.Fatalf("credential must be revoked: %v", r.gw.Revoked)
	}
	b, _ := os.ReadFile(r.usage)
	for _, want := range []string{`"node":"` + name + `"`, `"reason":"idle"`, `"provider":"fake"`, `"jobSeconds":null`, `"billedHours":1`} {
		if !strings.Contains(string(b), want) {
			t.Fatalf("usage record lacks %s: %s", want, b)
		}
	}
}

func TestBusyNodeNeverDeleted(t *testing.T) {
	r := newRig(t, nil)
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	name := r.names()[0]
	r.gw.Connect(name, 1)
	r.gw.DemandVal = Demand{}
	for i := 0; i < 10; i++ {
		r.clk.Advance(5 * time.Minute)
		r.tick()
	}
	if r.prov.Count() != 1 {
		t.Fatal("a node running a job must never be deleted by idle (max lifetime aside)")
	}
}

func TestHardCapUnderOrphansAndRace(t *testing.T) {
	r := newRig(t, nil)
	r.prov.DelErr = errors.New("api down")
	r.prov.AddOrphan("rb-pool-x1", "staging", r.clk.Now())
	r.prov.AddOrphan("rb-pool-x2", "staging", r.clk.Now())
	r.gw.DemandVal = Demand{DesiredNodes: 5, Queued: 5}
	r.tick()
	if r.prov.Count() != 2 {
		t.Fatalf("provider-listed orphans count against the cap, got %d", r.prov.Count())
	}
	r.prov.DelErr = nil
	r.prov2Clean(t)
}

func (r *rig) prov2Clean(t *testing.T) {
	t.Helper()
	r.clk.Advance(2 * time.Minute)
	r.tick() // orphans deleted, create allowed up to the cap in the same tick
	if r.prov.Count() > 2 {
		t.Fatalf("cap exceeded: %d", r.prov.Count())
	}
	r.gw.DemandVal = Demand{DesiredNodes: 9}
	for i := 0; i < 5; i++ {
		r.clk.Advance(2 * time.Minute)
		r.tick()
		if r.prov.Count() > 2 {
			t.Fatalf("cap exceeded: %d", r.prov.Count())
		}
	}
}

func TestSpendCapAndAlert(t *testing.T) {
	r := newRig(t, func(c *Config) { c.MaxNodes = 20 })
	r.prov.Prices["cpx42"] = 1.0
	r.gw.DemandVal = Demand{DesiredNodes: 20, Queued: 20}
	r.tick()
	if r.prov.Count() != 5 {
		t.Fatalf("EUR 5 cap at EUR 1/h allows 5 nodes, got %d", r.prov.Count())
	}
	if got := r.logged("WARN poolwaker: daily spend reached 80%"); got != 1 {
		t.Fatalf("alert must fire exactly once, fired %d", got)
	}
	hb := r.gw.Last()
	if !hb.Capped || hb.Healthy || hb.Reason != "daily_cap_reached" {
		t.Fatalf("heartbeat: %+v", hb)
	}
	r.clk.Advance(2 * time.Minute)
	r.tick()
	if r.prov.Count() != 5 || r.logged("WARN poolwaker: daily spend reached") != 1 {
		t.Fatal("no creates and no repeat alert while capped")
	}
	// Existing nodes are still drained while capped.
	r.gw.DemandVal = Demand{}
	for _, n := range r.names() {
		r.gw.Connect(n, 0)
	}
	r.clk.Advance(time.Second)
	r.tick()
	r.clk.Advance(55 * time.Minute) // the end of the paid hour, minus the margin
	for _, n := range r.names() {
		r.gw.Connect(n, 0)
	}
	r.tick()
	if r.prov.Count() != 0 {
		t.Fatalf("capped waker must still drain, %d left", r.prov.Count())
	}
	// Next UTC day: spend resets.
	r.clk.t = time.Date(2026, 3, 2, 0, 1, 0, 0, time.UTC)
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	if r.prov.Count() != 1 || r.gw.Last().Capped {
		t.Fatal("day boundary must reset the cap")
	}
}

func TestSpendPersistsAcrossRestart(t *testing.T) {
	r := newRig(t, nil)
	r.prov.Prices["cpx42"] = 2.0
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	r.clk.Advance(time.Minute)
	// "Restart": a fresh controller on the same ledger file.
	cfg := Config{Instance: "staging", ServerType: "cpx42", NodeGatewayURL: "wss://gw.example/ws", MaxNodes: 5}
	c2 := r.build(cfg)
	if got := c2.ledger.Spent(r.clk.Now()); got != 2.0 {
		t.Fatalf("ledger lost on restart: %v", got)
	}
	r.gw.DemandVal = Demand{DesiredNodes: 5, Queued: 5}
	c2.Tick(context.Background())
	// 2 spent, 3 more fit under 5; the restarted controller also reaps the old node as an orphan.
	if got := c2.ledger.Spent(r.clk.Now()); got > 5.0001 {
		t.Fatalf("spend over cap after restart: %v", got)
	}
}

func TestPriceUnknownFailsClosed(t *testing.T) {
	r := newRig(t, nil)
	delete(r.prov.Prices, "cpx42")
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	if r.prov.Count() != 0 || len(r.gw.Minted) != 0 {
		t.Fatal("must not create or mint when the price is unknown")
	}
	if hb := r.gw.Last(); hb.Healthy || hb.Reason != "price_unknown" {
		t.Fatalf("heartbeat: %+v", hb)
	}
}

func TestOrphanSweepOnlyLabelled(t *testing.T) {
	r := newRig(t, nil)
	r.prov.Foreign["foreign-1"] = true
	id := r.prov.AddOrphan("rb-pool-old", "staging", r.clk.Now().Add(-time.Hour))
	other := r.prov.AddOrphan("rb-pool-other", "production", r.clk.Now())
	r.tick()
	for _, a := range r.prov.Asked {
		if a == "foreign-1" || a == other {
			t.Fatalf("asked about a server that is not ours: %s", a)
		}
	}
	if len(r.prov.Asked) != 1 || r.prov.Asked[0] != id {
		t.Fatalf("expected only the orphan to be deleted, asked %v", r.prov.Asked)
	}
}

func TestMaxLifetimeSweep(t *testing.T) {
	r := newRig(t, nil)
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	name := r.names()[0]
	r.gw.Connect(name, 1)
	r.clk.Advance(6*time.Hour + time.Minute)
	r.gw.DemandVal = Demand{}
	r.tick()
	if r.prov.Count() != 0 {
		t.Fatal("node older than max lifetime must be deleted even if busy")
	}
}

func TestBootDeadlineAndBreaker(t *testing.T) {
	r := newRig(t, func(c *Config) { c.MaxNodes = 1 })
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	for i := 0; i < 3; i++ {
		r.tick() // creates (or keeps) the one allowed node
		r.clk.Advance(301 * time.Second)
		r.tick() // boot deadline passed -> deleted, counted as a failure
		r.clk.Advance(61 * time.Second)
	}
	// Breaker is open now: demand but no creates, unhealthy.
	r.clk.Advance(2 * time.Minute)
	r.tick()
	if r.prov.Count() != 0 {
		t.Fatalf("breaker must stop creates, have %d", r.prov.Count())
	}
	if hb := r.gw.Last(); hb.Healthy || hb.Reason != "breaker_open" {
		t.Fatalf("heartbeat: %+v", hb)
	}
	r.clk.Advance(16 * time.Minute)
	r.tick()
	if r.prov.Count() != 1 {
		t.Fatal("breaker must close after the backoff")
	}
}

func TestDeadWorkerDeleted(t *testing.T) {
	r := newRig(t, nil)
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	name := r.names()[0]
	r.gw.Connect(name, 0)
	r.clk.Advance(5 * time.Second)
	r.tick()
	r.gw.Disconnect(name)
	r.clk.Advance(60 * time.Second)
	r.tick()
	if r.prov.Count() != 1 {
		t.Fatal("within grace the node stays")
	}
	r.clk.Advance(130 * time.Second)
	r.gw.DemandVal = Demand{}
	r.tick()
	if r.prov.Count() != 0 {
		t.Fatal("node whose worker is gone beyond the grace must be deleted")
	}
}

func TestHeartbeatStates(t *testing.T) {
	r := newRig(t, nil)
	r.tick()
	if hb := r.gw.Last(); !hb.Healthy || hb.Reason != "ok" || hb.MaxNodes != 2 || hb.Instance != "staging" {
		t.Fatalf("ok: %+v", hb)
	}
	r.gw.DemandErr = errors.New("503")
	r.tick()
	if hb := r.gw.Last(); hb.Healthy || hb.Reason != "gateway_unreadable" {
		t.Fatalf("gateway: %+v", hb)
	}
	r.gw.DemandErr = nil
	r.prov.ListErr = errors.New("boom")
	r.tick()
	if !r.gw.Last().Healthy {
		t.Fatal("one provider error is transient")
	}
	r.tick()
	r.tick()
	if hb := r.gw.Last(); hb.Healthy || hb.Reason != "provider_errors" {
		t.Fatalf("provider: %+v", hb)
	}
	r.c.Shutdown(context.Background())
	if hb := r.gw.Last(); hb.Healthy || hb.Reason != "shutdown" {
		t.Fatalf("shutdown: %+v", hb)
	}
}

func TestDryRunChangesNothing(t *testing.T) {
	r := newRig(t, func(c *Config) { c.DryRun = true })
	r.gw.DemandVal = Demand{DesiredNodes: 2, Queued: 2}
	r.prov.AddOrphan("rb-pool-o", "staging", r.clk.Now())
	r.tick()
	if r.prov.Count() != 1 || len(r.gw.Minted) != 0 || len(r.gw.Heartbeats) != 0 {
		t.Fatal("dry-run must not create, delete, mint or heartbeat")
	}
}

func TestUserDataHasOnlyNodeToken(t *testing.T) {
	ud, err := RenderUserData(UserDataParams{GatewayURL: "wss://g/ws", Name: "rb-pool-a", Token: "rbn1.abc.def", CPULimit: "4"})
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{"REELBOLT_GATEWAY_URL=wss://g/ws", "REELBOLT_WORKER_NAME=rb-pool-a", "RUNNER_POOL_CREDENTIAL=rbn1.abc.def", "SANDBOX_CPU_LIMIT=4"} {
		if !strings.Contains(ud, want) {
			t.Fatalf("missing %s", want)
		}
	}
	for _, want := range []string{"reelbolt-worker.service", "OnBootSec=6h", "shutdown -h"} {
		if !strings.Contains(ud, want) {
			t.Fatalf("user-data lacks %s", want)
		}
	}
	if strings.Contains(ud, "${") {
		t.Fatal("unreplaced placeholder")
	}
	if strings.Contains(strings.ToUpper(ud), "DATABASE") || strings.Contains(ud, "INTERNAL_TOKEN") {
		t.Fatal("user-data must carry no other secret")
	}
	if _, err := RenderUserData(UserDataParams{GatewayURL: "wss://g\nevil", Name: "n", Token: "t", CPULimit: "4"}); err == nil {
		t.Fatal("newline injection must be refused")
	}
}

func TestMetricsText(t *testing.T) {
	r := newRig(t, nil)
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	m := r.c.Metrics()
	if !strings.Contains(m, "reelbolt_poolwaker_nodes_created_total 1") || !strings.Contains(m, "reelbolt_poolwaker_spend_today_eur") {
		t.Fatalf("metrics: %s", m)
	}
}

func TestLedgerCorruptIsAnError(t *testing.T) {
	p := filepath.Join(t.TempDir(), "l.json")
	_ = os.WriteFile(p, []byte("{not json"), 0o600)
	if _, err := OpenLedger(p, time.Now()); err == nil {
		t.Fatal("a corrupt ledger must not silently reset the cap")
	}
}

func sprintf(f string, a ...any) string { return fmt.Sprintf(f, a...) }

func TestRestartAdoptsConnectedNodes(t *testing.T) {
	r := newRig(t, nil)
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	name := r.names()[0]
	r.gw.Connect(name, 1)
	r.clk.Advance(20 * time.Minute)
	c2 := r.build(Config{Instance: "staging", ServerType: "cpx42", NodeGatewayURL: "wss://g/ws", MaxNodes: 2})
	c2.Tick(context.Background())
	if len(r.prov.Asked) != 0 || r.prov.Count() != 1 {
		t.Fatalf("restart must not reap a connected node, asked %v", r.prov.Asked)
	}
	st := c2.nodes[name]
	if st == nil || st.ReadyAt.IsZero() {
		t.Fatal("node must be adopted as ready")
	}
	// Adopted node is managed afterwards: idle until the end of its paid hour minus the margin.
	r.gw.DemandVal = Demand{}
	r.gw.Connect(name, 0)
	c2.Tick(context.Background())
	r.clk.Advance(36 * time.Minute) // created 56 minutes ago
	c2.Tick(context.Background())
	if r.prov.Count() != 0 {
		t.Fatal("adopted node must follow normal idle handling")
	}
}

func TestRestartOrphanRules(t *testing.T) {
	r := newRig(t, nil)
	now := r.clk.Now()
	young := r.prov.AddOrphan("rb-pool-young", "staging", now.Add(-60*time.Second))
	old := r.prov.AddOrphan("rb-pool-old", "staging", now.Add(-10*time.Minute))
	ancient := r.prov.AddOrphan("rb-pool-ancient", "staging", now.Add(-7*time.Hour))
	r.gw.Connect("rb-pool-ancient", 1)
	r.tick()
	got := map[string]bool{}
	for _, a := range r.prov.Asked {
		got[a] = true
	}
	if got[young] || !got[old] || !got[ancient] || len(got) != 2 {
		t.Fatalf("young unconnected kept, old unconnected and over-lifetime deleted; asked %v", r.prov.Asked)
	}
}

func TestStrictSpendCapAtHourBoundary(t *testing.T) {
	r := newRig(t, func(c *Config) { c.MaxNodes = 1; c.DailyCapEUR = 5 })
	r.prov.Prices["cpx42"] = 2.0
	r.addForeign()
	defer r.assertForeignSafe()
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	name := r.names()[0]
	r.gw.Connect(name, 1)
	start := r.clk.Now()
	spent := func() float64 { return r.c.ledger.Spent(r.clk.Now()) }
	at := func(d time.Duration) {
		r.clk.t = start.Add(d)
		r.gw.Connect(name, 1)
		r.tick()
		if spent() > 5.0001 {
			t.Fatalf("ledger %v exceeds the cap at +%s", spent(), d)
		}
	}
	at(30 * time.Minute)
	if spent() != 2 {
		t.Fatalf("no top-up yet: %v", spent())
	}
	at(59 * time.Minute) // reserves hour 2: 4 = 80%
	if spent() != 4 || r.logged("WARN poolwaker: daily spend reached 80%") != 1 {
		t.Fatalf("spent %v alerts %d", spent(), r.logged("WARN poolwaker: daily spend reached 80%"))
	}
	at(119*time.Minute + 10*time.Second) // hour 3 does not fit, but the job may keep running
	if r.prov.Count() != 1 {
		t.Fatal("a busy node is kept until the hard stop")
	}
	at(119*time.Minute + 40*time.Second) // inside the hard stop
	if r.prov.Count() != 0 {
		t.Fatal("busy node must be deleted before the hour it cannot pay for")
	}
	if spent() != 4 {
		t.Fatalf("ledger %v", spent())
	}
}

func TestStrictSpendCapIdleNodeDeletedAtOnce(t *testing.T) {
	r := newRig(t, func(c *Config) { c.MaxNodes = 1; c.DailyCapEUR = 3; c.IdleWindow = 4 * time.Hour })
	r.prov.Prices["cpx42"] = 2.0
	r.addForeign()
	defer r.assertForeignSafe()
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	name := r.names()[0]
	r.gw.Connect(name, 0)
	r.clk.Advance(59 * time.Minute)
	r.tick()
	if r.prov.Count() != 0 || r.c.ledger.Spent(r.clk.Now()) != 2 {
		t.Fatalf("idle node that cannot afford the next hour is deleted now; count %d", r.prov.Count())
	}
}

// addForeign adds the four kinds of unrelated server a real project holds.
func (r *rig) addForeign() {
	r.prov.AddUnrelated("control", "")                      // other label / none
	r.prov.AddUnrelated("media", "")                        // unlabelled
	r.prov.AddUnrelated("rb-pool-lookalike", "")            // similar name, no labels
	r.prov.AddUnrelated("rb-pool-other-inst", "production") // pool label, other instance
}

func (r *rig) assertForeignSafe() {
	r.t.Helper()
	if len(r.prov.ForeignTouched) != 0 || r.prov.UnrelatedCount() != 4 {
		r.t.Fatalf("unrelated server touched: %v (left %d)", r.prov.ForeignTouched, r.prov.UnrelatedCount())
	}
	for _, id := range r.prov.Asked {
		if strings.HasPrefix(id, "foreign-") {
			r.t.Fatalf("Delete asked for unrelated %s", id)
		}
	}
}

func TestUnrelatedServersNeverTouched(t *testing.T) {
	r := newRig(t, func(c *Config) { c.MaxNodes = 1; c.DailyCapEUR = 100 })
	r.prov.Prices["cpx42"] = 2.0
	r.addForeign()
	now := r.clk.Now()
	r.prov.AddOrphan("rb-pool-old", "staging", now.Add(-time.Hour)) // orphan cleanup
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()                         // orphan deleted, node created
	r.clk.Advance(301 * time.Second) // boot deadline
	r.tick()
	r.clk.Advance(61 * time.Second)
	r.tick() // recreated
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.clk.Advance(4 * time.Hour)
	r.tick()                                 // fresh node
	r.clk.Advance(6*time.Hour + time.Minute) // max lifetime
	r.gw.DemandVal = Demand{}
	r.tick()
	// restart adoption and shutdown
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.c.ledger.st.SpentEUR = 0
	r.tick()
	for _, n := range r.names() {
		r.gw.Connect(n, 1)
	}
	c2 := r.build(Config{Instance: "staging", ServerType: "cpx42", NodeGatewayURL: "wss://g/ws", MaxNodes: 1})
	c2.Tick(context.Background())
	c2.Shutdown(context.Background())
	r.c.Shutdown(context.Background())
	r.assertForeignSafe()
}

func TestDeleteGuardRefusesUnverifiedID(t *testing.T) {
	r := newRig(t, nil)
	r.addForeign()
	r.tick()
	for _, n := range r.prov.Unrelated() {
		if r.c.deleteID(context.Background(), n.ID) {
			t.Fatal("buggy path deleted a foreign id")
		}
	}
	if len(r.prov.Asked) != 0 {
		t.Fatalf("provider must not even be asked: %v", r.prov.Asked)
	}
}

type leakyProvider struct{ *Fake }

// List wrongly includes the unrelated servers.
func (l leakyProvider) List(ctx context.Context, instance string) ([]Node, error) {
	n, err := l.Fake.List(ctx, instance)
	if err != nil {
		return nil, err
	}
	n = append(n, l.Unrelated()...)
	n = append(n, Node{ID: "leak-nolabel", Name: "rb-pool-x", Instance: "", CreatedAt: l.Fake.Clock.Now().Add(-9 * time.Hour)})
	return n, nil
}

func TestAdversarialProviderListIsNotTrusted(t *testing.T) {
	r := newRig(t, nil)
	r.addForeign()
	led, _ := OpenLedger(filepath.Join(r.dir, "l2.json"), r.clk.Now())
	c, err := NewController(Config{Instance: "staging", ServerType: "cpx42", NodeGatewayURL: "wss://g/ws"}, leakyProvider{r.prov}, r.gw, led, nil, r.clk)
	if err != nil {
		t.Fatal(err)
	}
	c.SetLogf(func(f string, a ...any) {})
	for i := 0; i < 3; i++ {
		c.Tick(context.Background())
		r.clk.Advance(10 * time.Hour)
	}
	r.assertForeignSafe()
	for _, id := range r.prov.Asked {
		if id == "leak-nolabel" {
			t.Fatal("deleted a server with no instance label")
		}
	}
}

func TestInstanceCharsetAndNames(t *testing.T) {
	cfg := Config{Instance: "Prod_1", ServerType: "cpx42"}
	if err := cfg.Normalize(); err == nil {
		t.Fatal("instance charset must be enforced")
	}
	r := newRig(t, nil)
	r.gw.DemandVal = Demand{DesiredNodes: 2, Queued: 2}
	r.tick()
	for _, n := range r.names() {
		if !strings.HasPrefix(n, NamePrefix) {
			t.Fatalf("name %s", n)
		}
	}
}

// hourRig creates one node, connects it and leaves it idle with an empty queue.
func hourRig(t *testing.T, mut func(*Config)) (*rig, string, time.Time) {
	t.Helper()
	r := newRig(t, mut)
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	name := r.names()[0]
	start := r.clk.Now()
	r.prov.SetReady(name)
	r.gw.Connect(name, 1)
	r.clk.Advance(10 * time.Second)
	r.tick()
	r.gw.DemandVal = Demand{}
	r.gw.Connect(name, 0)
	return r, name, start
}

func (r *rig) at(start time.Time, d time.Duration, running int, name string) {
	r.clk.t = start.Add(d)
	r.gw.Connect(name, running)
	r.tick()
}

func TestIdleNodeSurvivesFiveMinutesAndIsDeletedAtHourMinusMargin(t *testing.T) {
	r, name, start := hourRig(t, nil)
	r.at(start, 20*time.Second, 0, name)
	r.at(start, 6*time.Minute, 0, name) // far past the old 5 minute idle window
	r.at(start, 30*time.Minute, 0, name)
	r.at(start, 54*time.Minute+30*time.Second, 0, name)
	if r.prov.Count() != 1 {
		b, _ := os.ReadFile(r.usage)
		t.Fatalf("an idle node must stay connected until the end of its paid hour: %s %v", b, r.logs)
	}
	r.at(start, 55*time.Minute, 0, name)
	if r.prov.Count() != 0 {
		t.Fatal("idle node must be deleted at hour minus margin")
	}
	b, _ := os.ReadFile(r.usage)
	if !strings.Contains(string(b), `"reason":"hour_end"`) || !strings.Contains(string(b), `"billedHours":1`) {
		t.Fatalf("usage record: %s", b)
	}
	if got := r.c.ledger.Spent(r.clk.Now()); got != r.prov.Prices["cpx42"] {
		t.Fatalf("only the first hour is charged, spent %v", got)
	}
}

func TestBusyNodeAtHourMarginIsKeptAndRenewedOnce(t *testing.T) {
	r, name, start := hourRig(t, nil)
	price := r.prov.Prices["cpx42"]
	r.at(start, 55*time.Minute, 1, name)
	r.at(start, 58*time.Minute, 1, name)
	if r.prov.Count() != 1 || r.c.ledger.Spent(r.clk.Now()) != price {
		t.Fatalf("busy node kept, not charged yet; count %d spent %v", r.prov.Count(), r.c.ledger.Spent(r.clk.Now()))
	}
	r.at(start, 59*time.Minute, 1, name) // inside the lookahead: next hour is reserved
	r.at(start, 59*time.Minute+30*time.Second, 1, name)
	r.at(start, 61*time.Minute, 1, name)
	if got := r.c.ledger.Spent(r.clk.Now()); got != 2*price {
		t.Fatalf("renewal must be charged exactly once, spent %v want %v", got, 2*price)
	}
	// Job over: the node idles to the end of the second hour, not the first.
	r.at(start, 62*time.Minute, 0, name)
	r.at(start, 114*time.Minute, 0, name)
	if r.prov.Count() != 1 {
		t.Fatal("renewed node stays until minute 55 of its second hour")
	}
	r.at(start, 115*time.Minute, 0, name)
	if r.prov.Count() != 0 || r.c.ledger.Spent(r.clk.Now()) != 2*price {
		t.Fatalf("count %d spent %v", r.prov.Count(), r.c.ledger.Spent(r.clk.Now()))
	}
}

func TestQueuedDemandAtTheBoundaryRenewsAnIdleNode(t *testing.T) {
	r, name, start := hourRig(t, nil)
	price := r.prov.Prices["cpx42"]
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.at(start, 55*time.Minute, 0, name)
	r.at(start, 59*time.Minute, 0, name)
	if r.prov.Count() != 1 || r.c.ledger.Spent(r.clk.Now()) != 2*price {
		t.Fatalf("queued demand renews into the next hour; count %d spent %v", r.prov.Count(), r.c.ledger.Spent(r.clk.Now()))
	}
	if r.prov.Count() != 1 || len(r.prov.Asked) != 0 {
		t.Fatal("no second server for a job the idle node can take")
	}
}

func TestIdleNodeNotRenewedWhenNothingWaits(t *testing.T) {
	// Margin shorter than the lookahead: the guard, not the idle check, ends it, and charges nothing.
	r, name, start := hourRig(t, func(c *Config) { c.HourEndMargin = 30 * time.Second })
	r.at(start, 59*time.Minute, 0, name)
	if r.prov.Count() != 0 || r.c.ledger.Spent(r.clk.Now()) != r.prov.Prices["cpx42"] {
		t.Fatalf("count %d spent %v", r.prov.Count(), r.c.ledger.Spent(r.clk.Now()))
	}
}

func TestQueuedJobReusesIdleNodeNoSecondServer(t *testing.T) {
	r, name, start := hourRig(t, nil)
	r.at(start, 20*time.Minute, 0, name)
	// A job is queued; the gateway's own slot formula (queued + running + buffer) asks for two.
	r.gw.DemandVal = Demand{DesiredNodes: 2, Queued: 1}
	r.at(start, 21*time.Minute, 0, name)
	r.at(start, 23*time.Minute, 0, name)
	if r.prov.Count() != 1 || r.logged("fit on") == 0 {
		t.Fatalf("an idle connected node takes the job; servers %d", r.prov.Count())
	}
	// Same job now running, nothing queued: still no new server for the buffer alone.
	r.gw.DemandVal = Demand{DesiredNodes: 2, Running: 1}
	r.at(start, 25*time.Minute, 1, name)
	if r.prov.Count() != 1 {
		t.Fatal("a buffer alone must not start a server")
	}
	// A second job queues while the first runs: that one needs a new server.
	r.gw.DemandVal = Demand{DesiredNodes: 2, Running: 1, Queued: 1}
	r.at(start, 27*time.Minute, 1, name)
	if r.prov.Count() != 2 {
		t.Fatalf("a job with no free node needs a server; servers %d", r.prov.Count())
	}
	// Two queued jobs, the new node still booting counts as free capacity: no third server.
	r.gw.DemandVal = Demand{DesiredNodes: 3, Running: 1, Queued: 1}
	r.at(start, 29*time.Minute, 1, name)
	if r.prov.Count() != 2 {
		t.Fatalf("a booting node will take the queued job; servers %d", r.prov.Count())
	}
}

func TestIdleNodeWithQueuedWorkIsNeverEndedByMargin(t *testing.T) {
	r, name, start := hourRig(t, func(c *Config) { c.IdleWindow = time.Minute })
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.at(start, 10*time.Minute, 0, name)
	if r.prov.Count() != 1 {
		t.Fatal("queued work keeps an idle node, even past the idle override")
	}
}

func TestClockEdgeCases(t *testing.T) {
	t.Run("clock steps back", func(t *testing.T) {
		r, name, start := hourRig(t, nil)
		r.at(start, -2*time.Minute, 0, name)
		r.at(start, 10*time.Minute, 0, name)
		if r.prov.Count() != 1 {
			t.Fatal("a clock that stepped back must not end the node")
		}
	})
	t.Run("long gap while idle charges nothing", func(t *testing.T) {
		r, name, start := hourRig(t, nil)
		r.at(start, 3*time.Hour+10*time.Second, 0, name)
		if r.prov.Count() != 0 || r.c.ledger.Spent(r.clk.Now()) != r.prov.Prices["cpx42"] {
			t.Fatalf("count %d spent %v", r.prov.Count(), r.c.ledger.Spent(r.clk.Now()))
		}
	})
	t.Run("margin at or above an hour is refused", func(t *testing.T) {
		c := Config{ServerType: "cpx42", HourEndMargin: time.Hour}
		if err := c.Normalize(); err == nil {
			t.Fatal("expected an error")
		}
	})
	t.Run("defaults", func(t *testing.T) {
		c := Config{ServerType: "cpx42"}
		if err := c.Normalize(); err != nil || c.HourEndMargin != 5*time.Minute || c.IdleWindow != 0 {
			t.Fatalf("err %v margin %s idle %s", err, c.HourEndMargin, c.IdleWindow)
		}
	})
	t.Run("adopted late in its hour", func(t *testing.T) {
		r, name, start := hourRig(t, nil)
		r.clk.t = start.Add(57 * time.Minute)
		c2 := r.build(Config{Instance: "staging", ServerType: "cpx42", NodeGatewayURL: "wss://g/ws", MaxNodes: 2})
		r.gw.Connect(name, 0)
		c2.Tick(context.Background())
		c2.Tick(context.Background())
		if r.prov.Count() != 0 {
			t.Fatal("an idle node adopted inside the margin is let go, not renewed")
		}
	})
}

func TestIdleOverrideNeverEndsABusyNode(t *testing.T) {
	r, name, start := hourRig(t, func(c *Config) { c.IdleWindow = time.Minute })
	r.at(start, 30*time.Minute, 1, name)
	r.at(start, 55*time.Minute, 1, name)
	if r.prov.Count() != 1 {
		t.Fatal("a node with a running job is never deleted by the idle rules")
	}
}

func TestUserDataFirewallPlaceholders(t *testing.T) {
	for _, tc := range []struct{ url, host, port string }{
		{"ws://10.0.1.5:8080/v1/connect", "10.0.1.5", "8080"},
		{"ws://10.0.1.5/v1/connect", "10.0.1.5", "80"},
		{"wss://runner.example.com/v1/connect", "runner.example.com", "443"},
		{"wss://runner.example.com:8443/v1/connect", "runner.example.com", "8443"},
	} {
		ud, err := RenderUserData(UserDataParams{GatewayURL: tc.url, Name: "rb-pool-a", Token: "t", CPULimit: "all"})
		if err != nil {
			t.Fatalf("%s: %v", tc.url, err)
		}
		if strings.Contains(ud, "${") {
			t.Fatalf("%s: unreplaced placeholder", tc.url)
		}
		for _, want := range []string{"GW_HOST=" + tc.host + "\n", "GW_PORT=" + tc.port + "\n", "reelbolt-node-firewall.service", "DOCKER-USER", "UseGateway=false", "Destination=10.0.0.0/16", "Gateway=10.0.0.1", "GatewayOnLink=yes", "-s $ROUTER -p udp --sport 67 --dport 68", "RB-NODE-OUT", "RB-NODE-FWD", "172.16.0.0/12", "192.168.0.0/16", "enp7s0", "ens10", "REELBOLT_GATEWAY_URL=" + tc.url} {
			if !strings.Contains(ud, want) {
				t.Fatalf("%s: user-data lacks %q", tc.url, want)
			}
		}
	}
}

func TestUserDataFirewallShape(t *testing.T) {
	ud, err := RenderUserData(UserDataParams{GatewayURL: "ws://10.0.0.3:8091/v1/connect", Name: "rb-pool-a", Token: "t", CPULimit: "all"})
	if err != nil {
		t.Fatal(err)
	}
	// DHCP renewal is allowed before the private-range drops, and the drops come after the gateway exception.
	dhcp := strings.Index(ud, "RB-NODE-OUT -d $ROUTER -p udp --sport 68 --dport 67 -j ACCEPT")
	gw := strings.Index(ud, `RB-NODE-OUT -d "$GW_HOST" -p tcp`)
	drop := strings.Index(ud, "RB-NODE-OUT -d $NET -j DROP")
	if dhcp < 0 || gw < dhcp || drop < gw {
		t.Fatalf("OUTPUT order wrong: dhcp=%d gw=%d drop=%d", dhcp, gw, drop)
	}
	// The default route and DNS still must not come from the private NIC.
	for _, want := range []string{"UseRoutes=false", "UseDNS=false", "UseGateway=false", "Wants=docker.service", "exit $rc", "rebuild RB-NODE-IN"} {
		if !strings.Contains(ud, want) {
			t.Fatalf("user-data lacks %q", want)
		}
	}
	meta := strings.Index(ud, "RB-NODE-FWD -d 169.254.169.254 -j DROP")
	est := strings.Index(ud, "RB-NODE-FWD -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT")
	fdrop := strings.Index(ud, "RB-NODE-FWD -d $NET -j DROP")
	if meta < 0 || est < meta || fdrop < est {
		t.Fatalf("forward chain order wrong: metadata=%d established=%d drop=%d", meta, est, fdrop)
	}
	if strings.Contains(ud, "DOCKER-USER -d \"$GW_HOST\"") || strings.Contains(ud, "add DOCKER-USER") {
		t.Fatal("containers must get no gateway exception")
	}
}

func TestGatewayEndpointRules(t *testing.T) {
	for _, bad := range []string{"ws://runner.example.com/x", "ws://8.8.8.8/x", "http://10.0.0.1/x", "ws://[fd00::1]/x", "10.0.0.1", ""} {
		if _, _, err := GatewayEndpoint(bad); err == nil {
			t.Fatalf("%q must be refused", bad)
		}
	}
	for _, ok := range []string{"ws://10.0.2.4:9000/v1/connect", "ws://192.168.1.1/x", "ws://172.16.0.1/x", "ws://127.0.0.1:1/x", "wss://anything.example/x"} {
		if _, _, err := GatewayEndpoint(ok); err != nil {
			t.Fatalf("%q: %v", ok, err)
		}
	}
}

// A job that was assigned before and is now reused or retried (attempt 1 or more) reaches the waker as
// waiting demand, however the gateway came to count it: Demand.Queued is the gateway's awaitingWorker,
// which includes a job leased to a vanished worker. These tests pin what the loop does with it.
func TestReusedJobWithNoWorkerWakesOneNode(t *testing.T) {
	r := newRig(t, nil)
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1, Running: 1}
	r.tick()
	if r.prov.Count() != 1 {
		t.Fatalf("a reused job waiting for a worker must wake a node, got %d", r.prov.Count())
	}
	for i := 0; i < 3; i++ {
		r.clk.Advance(2 * time.Minute)
		r.tick()
	}
	if r.prov.Count() != 1 {
		t.Fatalf("one job must never buy two nodes, got %d", r.prov.Count())
	}
	name := r.names()[0]
	r.prov.SetReady(name)
	r.gw.Connect(name, 0)
	r.clk.Advance(time.Minute)
	r.tick()
	if r.prov.Count() != 1 {
		t.Fatalf("a connected free node takes the job; got %d nodes", r.prov.Count())
	}
}

func TestJobsRunningOnWorkersAreNotDemand(t *testing.T) {
	r := newRig(t, nil)
	r.gw.DemandVal = Demand{DesiredNodes: 1, Running: 1}
	r.tick()
	if r.prov.Count() != 0 {
		t.Fatalf("a job a worker already holds must not wake a node, got %d", r.prov.Count())
	}
	if r.logged("no job is waiting for a pool worker") == 0 {
		t.Fatalf("the refusal must be logged, got %v", r.logs)
	}
}

func TestReusedJobDemandStillObeysTheCaps(t *testing.T) {
	r := newRig(t, func(c *Config) { c.MaxNodes = 1; c.DailyCapEUR = 1 })
	r.prov.Prices["cpx42"] = 2.0
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1, Running: 1}
	r.tick()
	if r.prov.Count() != 0 {
		t.Fatalf("the daily cap must still win over demand, got %d", r.prov.Count())
	}

	r2 := newRig(t, func(c *Config) { c.MaxNodes = 2 })
	r2.gw.DemandVal = Demand{DesiredNodes: 5, Queued: 5, Running: 5}
	for i := 0; i < 6; i++ {
		r2.clk.Advance(2 * time.Minute)
		r2.tick()
	}
	if r2.prov.Count() != 2 {
		t.Fatalf("the server cap (2) must still win, got %d", r2.prov.Count())
	}
}
