package main

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"
)

// These tests cover the F11 switch logic that needs no database: the row
// semantics, the TTL cache, the single in-flight refresh and the claim gate.
// Everything that needs SQL — a real platform_settings table, the per-candidate
// skip and the slot-cap override — is in queue_integration_test.go.

// fakeSwitchStore is a platform_settings store with no database behind it: it
// serves a snapshot a test sets, can be made to fail, and counts its reads so a
// test can prove the cache went back to the store exactly when it should.
type fakeSwitchStore struct {
	mu       sync.Mutex
	snapshot SwitchSnapshot
	err      error
	calls    int

	// hold, when non-nil, is a gate every read waits on before answering, so the
	// stampede test can make reads overlap rather than complete one at a time.
	hold chan struct{}
	// entered, when non-nil, receives one value as each read enters the store, so
	// a test can wait until a read is really in flight.
	entered chan struct{}
}

func newFakeSwitchStore() *fakeSwitchStore {
	return &fakeSwitchStore{snapshot: switchesFrom()}
}

func (f *fakeSwitchStore) ReadPlatformSettings(ctx context.Context) (SwitchSnapshot, error) {
	f.mu.Lock()
	f.calls++
	snapshot, err := f.snapshot, f.err
	hold, entered := f.hold, f.entered
	f.mu.Unlock()

	if entered != nil {
		entered <- struct{}{}
	}
	if hold != nil {
		select {
		case <-hold:
		case <-ctx.Done():
			return SwitchSnapshot{}, ctx.Err()
		}
	}
	return snapshot, err
}

// serve is the store's answer for the next read: this state, no error.
func (f *fakeSwitchStore) serve(snapshot SwitchSnapshot) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.snapshot, f.err = snapshot, nil
}

// fail makes every read fail, which is a platform whose switches cannot be read.
func (f *fakeSwitchStore) fail(err error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.err = err
}

func (f *fakeSwitchStore) reads() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.calls
}

// switchesFrom builds a loaded snapshot the way a store does — by folding rows —
// so no test hand-assembles a struct that production only ever parses into.
func switchesFrom(rows ...switchRow) SwitchSnapshot { return foldSwitchRows(rows) }

// pausedComputeSwitches is platform_settings with 'compute' paused.
func pausedComputeSwitches() SwitchSnapshot {
	return switchesFrom(switchRow{Key: settingsKeyCompute, Paused: true})
}

// testClock is a now func a test drives by hand, so a cache TTL is exercised by
// moving the clock rather than by sleeping. It is the same seam the gateway
// exposes as gateway.now.
type testClock struct {
	mu  sync.Mutex
	now time.Time
}

func newTestClock() *testClock {
	return &testClock{now: time.Date(2026, 10, 8, 9, 0, 0, 0, time.UTC)}
}

func (c *testClock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.now
}

func (c *testClock) advance(d time.Duration) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.now = c.now.Add(d)
}

// metricsValue scrapes a registry the way /metrics is scraped and returns the
// value on one series line. A series nothing has touched reads as 0, which is
// what an untouched Prometheus counter reports.
func metricsValue(t *testing.T, m *gatewayMetrics, line string) float64 {
	t.Helper()
	recorder := httptest.NewRecorder()
	m.handler.ServeHTTP(recorder, httptest.NewRequestWithContext(context.Background(), http.MethodGet, "/metrics", nil))
	value, ok := parseMetricLine(recorder.Body.String(), line)
	if !ok {
		return 0
	}
	return value
}

// parseMetricLine finds one series in the prometheus text format.
func parseMetricLine(body, line string) (float64, bool) {
	for _, l := range strings.Split(body, "\n") {
		if !strings.HasPrefix(l, line) {
			continue
		}
		fields := strings.Fields(l)
		if len(fields) < 2 {
			continue
		}
		value, err := strconv.ParseFloat(fields[len(fields)-1], 64)
		if err != nil {
			continue
		}
		return value, true
	}
	return 0, false
}

// ── the decision ────────────────────────────────────────────────────────────

// TestSwitchSnapshotZeroValueMeansPaused pins the property the whole file is
// built around: a snapshot nobody loaded refuses. A struct that is all zero
// values is easy to produce by accident — a forgotten assignment, an error path
// that returns the zero value — and reading one as "the platform is running
// normally" is exactly the fail-open a kill switch exists to prevent.
func TestSwitchSnapshotZeroValueMeansPaused(t *testing.T) {
	var none SwitchSnapshot
	if !none.ComputePaused() {
		t.Error("the zero snapshot was read as an enabled platform")
	}
	// The organisation and cap accessors answer "no": an unreadable store must
	// never invent a disabled organisation (see the handshake note in
	// switches.go), and the refusal it does produce is the compute one above.
	if none.OrganizationDisabled(testOrgID) {
		t.Error("the zero snapshot disabled an organisation")
	}
	if cap, ok := none.PoolSlotCap(testOrgID); ok || cap != 0 {
		t.Errorf("the zero snapshot produced a slot cap of %d, %v", cap, ok)
	}
	// ClaimGates is the documented exception: it is not itself fail-closed,
	// because the gate that produces it is.
	if none.ClaimGates().Disabled(testOrgID) {
		t.Error("an empty ClaimGates disabled an organisation")
	}
}

// TestFoldSwitchRowsReadsTheFrozenSchema pins the row semantics the frozen
// platform_settings schema defines, without a database.
func TestFoldSwitchRowsReadsTheFrozenSchema(t *testing.T) {
	const orgC = "5e2b1c90-7a3d-4f68-8b21-0c9d4e6f1a24"

	// An empty table is a platform with every switch released: "No row for a key
	// means enabled / no cap".
	empty := foldSwitchRows(nil)
	if empty.ComputePaused() {
		t.Error("an empty platform_settings paused compute")
	}
	if empty.OrganizationDisabled(testOrgA) {
		t.Error("an empty platform_settings disabled an organisation")
	}

	snapshot := foldSwitchRows([]switchRow{
		{Key: settingsKeyCompute},
		// Not this gateway's switch: the engine enforces it.
		{Key: "inference", Paused: true},
		// The key is upper-cased here on purpose: the platform writes lowercase
		// guids and a hand-written row may not, and one organisation must not
		// depend on which spelling a row happens to use.
		{Key: settingsKeyOrgPrefix + strings.ToUpper(testOrgA), Paused: true, PoolSlotCap: intPtr(3)},
		{Key: settingsKeyOrgPrefix + testOrgB},
		// A zero cap is not a cap: it would stop the organisation permanently,
		// which is not something a nullable column can be asking for.
		{Key: settingsKeyOrgPrefix + orgC, PoolSlotCap: intPtr(0)},
		{Key: settingsKeyOrgPrefix, Paused: true},
		{Key: "future.key", Paused: true},
	})
	if snapshot.ComputePaused() {
		t.Error("an explicit paused=false row paused compute")
	}
	if !snapshot.OrganizationDisabled(testOrgA) {
		t.Error("a paused org row did not disable that organisation")
	}
	if snapshot.OrganizationDisabled(testOrgB) {
		t.Error("an org row with no paused flag disabled the organisation")
	}
	if cap, ok := snapshot.PoolSlotCap(testOrgA); !ok || cap != 3 {
		t.Errorf("PoolSlotCap(orgA) = %d, %v; want 3, true", cap, ok)
	}
	if cap, ok := snapshot.PoolSlotCap(testOrgB); ok || cap != 0 {
		t.Errorf("PoolSlotCap(orgB) = %d, %v; want no override", cap, ok)
	}
	if cap, ok := snapshot.PoolSlotCap(orgC); ok || cap != 0 {
		t.Errorf("PoolSlotCap(orgC) = %d, %v; a zero cap must read as no override", cap, ok)
	}
	// 'org:' with nothing after it names no organisation, and a key this gateway
	// does not act on is not an error: settings keys are an open set.
	if snapshot.OrganizationDisabled("") {
		t.Error("an empty organisation id was reported as disabled")
	}

	if !foldSwitchRows([]switchRow{{Key: settingsKeyCompute, Paused: true}}).ComputePaused() {
		t.Error("a paused compute row did not pause the platform")
	}
}

// ── the cache ───────────────────────────────────────────────────────────────

func TestSwitchCacheServesTheValueInsideTheTTL(t *testing.T) {
	store := newFakeSwitchStore()
	clock := newTestClock()
	switches := newPlatformSwitches(store, newGatewayMetrics())
	switches.now = clock.Now
	ctx := context.Background()

	snapshot, err := switches.Snapshot(ctx)
	if err != nil || snapshot.ComputePaused() {
		t.Fatalf("first read paused=%v, err=%v; want an enabled platform", snapshot.ComputePaused(), err)
	}

	// The operator pauses compute. Inside the TTL the cached answer still stands:
	// that is what "cached 10 s" buys, and it is why a claim round does not go
	// back to the database every five seconds.
	clock.advance(switchCacheTTL - time.Millisecond)
	store.serve(pausedComputeSwitches())
	snapshot, err = switches.Snapshot(ctx)
	if err != nil {
		t.Fatalf("cached read: %v", err)
	}
	if snapshot.ComputePaused() {
		t.Error("the cache was not served inside the TTL")
	}
	if got := store.reads(); got != 1 {
		t.Errorf("the store was read %d times inside the TTL, want 1", got)
	}

	// Past it the switch is read, so flipping one always takes effect within the
	// TTL rather than never.
	clock.advance(2 * time.Millisecond)
	snapshot, err = switches.Snapshot(ctx)
	if err != nil {
		t.Fatalf("re-read: %v", err)
	}
	if !snapshot.ComputePaused() {
		t.Error("a switch flipped in the store was not read past the TTL")
	}
	if got := store.reads(); got != 2 {
		t.Errorf("reads = %d, want 2", got)
	}
}

// TestSwitchCacheNeverServesAStaleValueFromAFailedRead is the safety property of
// rule 1: once the TTL is up, a store that cannot be read means PAUSED. The one
// thing a kill switch must never do is keep answering "not paused" from a value
// nobody can confirm.
func TestSwitchCacheNeverServesAStaleValueFromAFailedRead(t *testing.T) {
	store := newFakeSwitchStore()
	metrics := newGatewayMetrics()
	clock := newTestClock()
	switches := newPlatformSwitches(store, metrics)
	switches.now = clock.Now
	ctx := context.Background()

	if _, err := switches.Snapshot(ctx); err != nil {
		t.Fatalf("first read: %v", err)
	}
	clock.advance(switchCacheTTL)
	store.fail(errors.New("no such table: platform_settings"))

	snapshot, err := switches.Snapshot(ctx)
	if err == nil {
		t.Fatal("a failed re-read was reported as a successful one")
	}
	if !snapshot.ComputePaused() {
		t.Fatal("a stale 'not paused' was served after the store became unreadable")
	}
	// The failure is cached for the TTL as well, so an outage costs one query per
	// TTL instead of one per claim.
	if _, err := switches.Snapshot(ctx); err == nil {
		t.Error("the cached failure was not remembered")
	}
	if got := store.reads(); got != 2 {
		t.Errorf("reads = %d, want 2 (the failure is cached for the TTL too)", got)
	}
	if got := metricsValue(t, metrics, "reelbolt_platform_switches_unreadable_total"); got != 1 {
		t.Errorf("reelbolt_platform_switches_unreadable_total = %v, want 1", got)
	}
	if got := metricsValue(t, metrics, "reelbolt_runner_gateway_platform_paused{switch=\"compute\"}"); got != 1 {
		t.Errorf("platform_paused{switch=compute} = %v, want 1: an unreadable store is not a released switch", got)
	}
	if got := metricsValue(t, metrics, "reelbolt_runner_gateway_platform_paused{switch=\"settings\"}"); got != 1 {
		t.Errorf("platform_paused{switch=settings} = %v, want 1", got)
	}

	// Recovery is published too, and the counter is not reset by it: how often
	// the switches were unreadable is a fact about the past.
	clock.advance(switchCacheTTL)
	store.serve(switchesFrom())
	if _, err := switches.Snapshot(ctx); err != nil {
		t.Fatalf("recovery read: %v", err)
	}
	if got := metricsValue(t, metrics, "reelbolt_runner_gateway_platform_paused{switch=\"settings\"}"); got != 0 {
		t.Errorf("platform_paused{switch=settings} = %v after recovery, want 0", got)
	}
	if got := metricsValue(t, metrics, "reelbolt_runner_gateway_platform_paused{switch=\"compute\"}"); got != 0 {
		t.Errorf("platform_paused{switch=compute} = %v after recovery, want 0", got)
	}
	if got := metricsValue(t, metrics, "reelbolt_platform_switches_unreadable_total"); got != 1 {
		t.Errorf("reelbolt_platform_switches_unreadable_total = %v after recovery, want 1", got)
	}
}

// TestSwitchCacheReadsOnceUnderConcurrentReaders is the stampede guard: a cold
// cache with N readers waiting on it is ONE query, not N. Without it a deploy
// that reconnects a fleet at once would hit the settings table once per runner.
func TestSwitchCacheReadsOnceUnderConcurrentReaders(t *testing.T) {
	store := newFakeSwitchStore()
	store.hold = make(chan struct{})
	store.entered = make(chan struct{}, 64)
	switches := newPlatformSwitches(store, newGatewayMetrics())

	const readers = 20
	start := make(chan struct{})
	errs := make(chan error, readers)
	var wg sync.WaitGroup
	for i := 0; i < readers; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			<-start
			if _, err := switches.Snapshot(context.Background()); err != nil {
				errs <- err
			}
		}()
	}
	close(start)
	// Wait for the leader to be inside the store, then let the rest pile up
	// behind it before the read is allowed to answer.
	<-store.entered
	time.Sleep(50 * time.Millisecond)
	close(store.hold)
	wg.Wait()
	close(errs)
	for err := range errs {
		t.Errorf("Snapshot: %v", err)
	}
	if got := store.reads(); got != 1 {
		t.Errorf("the store was read %d times, want 1: concurrent readers stampeded the database", got)
	}
}

// TestUnreadableSwitchesProduceNoOrganizationDecision is rule 3's other half: a
// store nobody can read must not fabricate the one decision that is terminal for
// a runner.
func TestUnreadableSwitchesProduceNoOrganizationDecision(t *testing.T) {
	store := newFakeSwitchStore()
	store.fail(errors.New("no such table: platform_settings"))
	switches := newPlatformSwitches(store, newGatewayMetrics())

	snapshot, err := switches.Snapshot(context.Background())
	if err == nil {
		t.Fatal("an unreadable store reported success")
	}
	if snapshot.OrganizationDisabled(testOrgID) {
		t.Error("an unreadable store decided an organisation was disabled")
	}
	if cap, ok := snapshot.PoolSlotCap(testOrgID); ok || cap != 0 {
		t.Errorf("an unreadable store produced a slot cap of %d, %v", cap, ok)
	}
	if !snapshot.ComputePaused() {
		t.Error("an unreadable store did not pause compute")
	}
}

// TestNilPlatformSwitchesFailsClosed covers a gateway started without a facade.
// The absent dependency must mean "assign nothing", never "skip the gate".
func TestNilPlatformSwitchesFailsClosed(t *testing.T) {
	var switches *platformSwitches
	snapshot, err := switches.Snapshot(context.Background())
	if !errors.Is(err, errSwitchesUnavailable) {
		t.Errorf("Snapshot error = %v, want errSwitchesUnavailable", err)
	}
	if !snapshot.ComputePaused() {
		t.Error("a gateway with no switches facade read as an enabled platform")
	}
	// The startup prime must survive it rather than panicking on the nil receiver.
	switches.prime(context.Background())
}

// ── the claim gate ──────────────────────────────────────────────────────────

// TestClaimGateRefusesWorkOnAPlatformItCannotJudge walks every way of not being
// allowed to assign. Each one has to answer "no", because the alternative —
// assigning on a switch the gateway could not read — is the bug F11 exists to
// prevent.
func TestClaimGateRefusesWorkOnAPlatformItCannotJudge(t *testing.T) {
	cases := []struct {
		name       string
		switches   func() *platformSwitches
		wantAllow  bool
		wantReason string
	}{
		{
			name:       "no switches facade at all",
			switches:   func() *platformSwitches { return nil },
			wantReason: deferReasonSwitchesUnreadable,
		},
		{
			name: "a store that cannot be read",
			switches: func() *platformSwitches {
				store := newFakeSwitchStore()
				store.fail(errors.New("platform_settings is unreachable"))
				return newPlatformSwitches(store, newGatewayMetrics())
			},
			wantReason: deferReasonSwitchesUnreadable,
		},
		{
			name: "compute paused",
			switches: func() *platformSwitches {
				store := newFakeSwitchStore()
				store.serve(pausedComputeSwitches())
				return newPlatformSwitches(store, newGatewayMetrics())
			},
			wantReason: deferReasonComputePaused,
		},
		{
			name:      "compute running",
			switches:  func() *platformSwitches { return newPlatformSwitches(newFakeSwitchStore(), newGatewayMetrics()) },
			wantAllow: true,
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			metrics := newGatewayMetrics()
			g := &gateway{metrics: metrics, now: time.Now, switches: tc.switches()}
			gates, allowed := g.claimGate(context.Background())
			if allowed != tc.wantAllow {
				t.Fatalf("claimGate allowed = %v, want %v", allowed, tc.wantAllow)
			}
			if tc.wantReason != "" {
				line := "reelbolt_runner_gateway_claims_deferred_total{reason=\"" + tc.wantReason + "\"}"
				if got := metricsValue(t, metrics, line); got != 1 {
					t.Errorf("%s = %v, want 1", line, got)
				}
			} else if got := metricsValue(t, metrics, "reelbolt_runner_gateway_claims_deferred_total"); got != 0 {
				t.Errorf("a permitted claim was counted as deferred (%v)", got)
			}
			// A refusal never carries decisions: nothing downstream may act on a
			// switch the gate would not stand behind.
			if !tc.wantAllow && gates.Disabled(testOrgA) {
				t.Error("a refused claim still carried a per-organisation decision")
			}
		})
	}
}

// TestClaimGateCarriesThePerCandidateDecisions: once the gate allows a claim, it
// hands ClaimJobs the organisation-level half of the switches.
func TestClaimGateCarriesThePerCandidateDecisions(t *testing.T) {
	store := newFakeSwitchStore()
	store.serve(switchesFrom(
		switchRow{Key: settingsKeyOrgPrefix + testOrgA, Paused: true},
		switchRow{Key: settingsKeyOrgPrefix + testOrgB, PoolSlotCap: intPtr(2)},
	))
	g := &gateway{
		metrics:  newGatewayMetrics(),
		now:      time.Now,
		switches: newPlatformSwitches(store, newGatewayMetrics()),
	}

	gates, allowed := g.claimGate(context.Background())
	if !allowed {
		t.Fatal("an enabled platform refused a claim")
	}
	if !gates.Disabled(testOrgA) {
		t.Error("the disabled organisation was not carried into the claim")
	}
	if gates.Disabled(testOrgB) {
		t.Error("an enabled organisation was carried into the claim as disabled")
	}
	if cap, ok := gates.PoolCap(testOrgB); !ok || cap != 2 {
		t.Errorf("PoolCap(orgB) = %d, %v; want 2, true", cap, ok)
	}
	if cap, ok := gates.PoolCap(testOrgA); ok || cap != 0 {
		t.Errorf("PoolCap(orgA) = %d, %v; want no override", cap, ok)
	}
}
