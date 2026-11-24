package poolwaker

import (
	"context"
	"errors"
	"strings"
	"testing"
	"time"
)

func ccxRig(t *testing.T, mut func(*Config)) *rig {
	t.Helper()
	r := newRig(t, func(c *Config) {
		c.ServerType = ""
		c.ServerTypes = []string{"ccx33", "ccx23", "ccx13"}
		c.DailyCapEUR = 5
		if mut != nil {
			mut(c)
		}
	})
	r.prov.Prices = map[string]float64{"ccx33": 0.222, "ccx23": 0.138, "ccx13": 0.069}
	return r
}

func TestConfigSizeList(t *testing.T) {
	c := Config{Instance: "x", ServerType: "cpx42"}
	if err := c.Normalize(); err != nil || len(c.ServerTypes) != 1 || c.ServerTypes[0] != "cpx42" {
		t.Fatalf("single type kept as a one-entry list: %+v %v", c.ServerTypes, err)
	}
	c = Config{Instance: "x", ServerTypes: []string{" ccx33 ", "", "ccx23", "ccx33"}}
	if err := c.Normalize(); err != nil || strings.Join(c.ServerTypes, ",") != "ccx33,ccx23" || c.ServerType != "ccx33" {
		t.Fatalf("trimmed, de-duplicated, first wins: %+v %v", c.ServerTypes, err)
	}
	if err := (&Config{Instance: "x"}).Normalize(); err == nil {
		t.Fatal("no size at all must be refused")
	}
	c = Config{Instance: "x", ServerType: "ccx33"}
	_ = c.Normalize()
	if c.CPULimit != "all" || c.SizeRefusalBackoff != time.Hour {
		t.Fatalf("defaults: cpu %q backoff %v", c.CPULimit, c.SizeRefusalBackoff)
	}
}

func TestFirstSizeInTheListIsUsed(t *testing.T) {
	r := ccxRig(t, nil)
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	nodes, _ := r.prov.List(context.Background(), "staging")
	if len(nodes) != 1 || nodes[0].ServerType != "ccx33" {
		t.Fatalf("want one ccx33 node, got %+v", nodes)
	}
	if got := r.c.Metrics(); !strings.Contains(got, "spend_today_eur 0.222") {
		t.Fatalf("the ledger must be charged the ccx33 price:\n%s", got)
	}
}

func TestCoreLimitRefusalFallsBackAndDoesNotStorm(t *testing.T) {
	r := ccxRig(t, nil)
	var asked []string
	r.prov.CreateFn = func(spec NodeSpec) error {
		asked = append(asked, spec.ServerType)
		if spec.ServerType == "ccx33" {
			return &SizeRefusedError{ServerType: "ccx33", Reason: "resource_limit_exceeded: dedicated core limit exceeded"}
		}
		return nil
	}
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	if strings.Join(asked, ",") != "ccx33,ccx23" {
		t.Fatalf("refused size must be followed by the next one in the same tick: %v", asked)
	}
	nodes, _ := r.prov.List(context.Background(), "staging")
	if len(nodes) != 1 || nodes[0].ServerType != "ccx23" {
		t.Fatalf("fallback node: %+v", nodes)
	}
	if r.c.provErrs != 0 || r.c.bootFailures != 0 {
		t.Fatal("a size refusal is neither a provider outage nor a boot failure")
	}
	if got := r.c.Metrics(); !strings.Contains(got, "size_refused_total 1") || !strings.Contains(got, "spend_today_eur 0.138") {
		t.Fatalf("refusal counted, only the created node charged:\n%s", got)
	}
	// The second node of the next wave must not ask for ccx33 again within the backoff.
	asked = nil
	r.gw.DemandVal = Demand{DesiredNodes: 2, Queued: 2}
	r.clk.Advance(2 * time.Minute)
	r.tick()
	if strings.Join(asked, ",") != "ccx23" {
		t.Fatalf("no retry of the refused size inside the backoff: %v", asked)
	}
	// After the backoff the preferred size is tried again.
	asked = nil
	r.prov.Delete(context.Background(), nodes[0].ID)
	r.c.nodes = map[string]*tracked{}
	r.clk.Advance(2 * time.Hour)
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	if len(asked) == 0 || asked[0] != "ccx33" {
		t.Fatalf("after the backoff ccx33 is tried again: %v", asked)
	}
}

func TestEverySizeRefusedStopsAndReportsWithoutRetryStorm(t *testing.T) {
	r := ccxRig(t, nil)
	calls := 0
	r.prov.CreateFn = func(spec NodeSpec) error {
		calls++
		return &SizeRefusedError{ServerType: spec.ServerType, Reason: "dedicated core limit exceeded"}
	}
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	if calls != 3 || r.prov.Count() != 0 {
		t.Fatalf("each size asked once, nothing created: calls=%d nodes=%d", calls, r.prov.Count())
	}
	if hb := r.gw.Last(); hb.Healthy || hb.Reason != "size_unavailable" {
		t.Fatalf("heartbeat must say size_unavailable so the engine runs Local: %+v", hb)
	}
	for i := 0; i < 5; i++ {
		r.clk.Advance(20 * time.Second)
		r.tick()
	}
	if calls != 3 {
		t.Fatalf("no further create calls while every size backs off, got %d", calls)
	}
	if len(r.gw.Minted) != len(r.gw.Revoked) {
		t.Fatalf("every minted credential of a refused create is revoked: %v / %v", r.gw.Minted, r.gw.Revoked)
	}
}

func TestAnOrdinaryCreateErrorStillCountsAsProviderError(t *testing.T) {
	r := ccxRig(t, nil)
	r.prov.CreateFn = func(NodeSpec) error { return errors.New("boom") }
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	if r.c.provErrs != 1 || len(r.c.refused) != 0 {
		t.Fatalf("plain errors keep the old behaviour: provErrs=%d refused=%v", r.c.provErrs, r.c.refused)
	}
}

func TestCapStaysFailClosedAndFallsDownTheListWithCcxPrices(t *testing.T) {
	r := ccxRig(t, func(c *Config) { c.DailyCapEUR = 0.30; c.MaxNodes = 2; c.Cooldown = time.Second })
	r.gw.DemandVal = Demand{DesiredNodes: 2, Queued: 2}
	r.tick()
	nodes, _ := r.prov.List(context.Background(), "staging")
	// First node ccx33 (0.222). 0.30 - 0.222 = 0.078: ccx33 and ccx23 no longer fit, ccx13 (0.069) does.
	types := map[string]bool{}
	for _, n := range nodes {
		types[n.ServerType] = true
	}
	if len(nodes) != 2 || !types["ccx33"] || !types["ccx13"] {
		t.Fatalf("expected a ccx33 then, under the cap, a ccx13: %+v", nodes)
	}
	// Now nothing fits: fail closed.
	r.gw.DemandVal = Demand{DesiredNodes: 2, Queued: 3}
	r.c.cfg.MaxNodes = 3
	r.clk.Advance(5 * time.Second)
	r.tick()
	if r.prov.Count() != 2 {
		t.Fatalf("cap reached, no third node: %d", r.prov.Count())
	}
	if hb := r.gw.Last(); !hb.Capped {
		t.Fatalf("heartbeat must report capped: %+v", hb)
	}
}

func TestUnknownPriceOfOneSizeSkipsItAndUnknownOfAllFailsClosed(t *testing.T) {
	r := ccxRig(t, nil)
	delete(r.prov.Prices, "ccx33")
	r.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r.tick()
	nodes, _ := r.prov.List(context.Background(), "staging")
	if len(nodes) != 1 || nodes[0].ServerType != "ccx23" {
		t.Fatalf("a size with no known price is skipped, never guessed: %+v", nodes)
	}
	r2 := ccxRig(t, nil)
	r2.prov.Prices = map[string]float64{}
	r2.gw.DemandVal = Demand{DesiredNodes: 1, Queued: 1}
	r2.tick()
	if r2.prov.Count() != 0 || r2.gw.Last().Reason != "price_unknown" {
		t.Fatalf("no price at all must fail closed: %d %+v", r2.prov.Count(), r2.gw.Last())
	}
}

// A node is never told how many jobs to take: it derives one slot from the "all" cpu limit.
// An explicit slot count in its environment would break the one-job-at-a-time rule.
func TestNodeUserDataSetsNoSlotOverride(t *testing.T) {
	ud, err := RenderUserData(UserDataParams{GatewayURL: "wss://g/ws", Name: "rb-pool-x", Token: "t", CPULimit: "all"})
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(ud, "REELBOLT_WORKER_SLOTS") {
		t.Fatal("cloud-init must not set REELBOLT_WORKER_SLOTS")
	}
	if !strings.Contains(ud, "SANDBOX_CPU_LIMIT=all") {
		t.Fatal("cloud-init must pass the cpu limit")
	}
	c := Config{ServerType: "cpx42"}
	if err := c.Normalize(); err != nil || c.CPULimit != "all" {
		t.Fatalf("default cpu limit %q (%v)", c.CPULimit, err)
	}
}
