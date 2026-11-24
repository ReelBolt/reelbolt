package main

import (
	"context"
	"errors"
	"fmt"
	"log"
	"strings"
	"sync"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

// This file is F11's half of the platform kill switches: the runner gateway's
// view of platform_settings.
//
// The table is owned by the Inference API's EF migration and is read here by raw
// SQL, never migrated (the same ruling the rest of the gateway follows for
// runner_devices, runner_jobs and runner_sessions). Its shape is fixed:
//
//	key           text        NOT NULL PRIMARY KEY,
//	paused        boolean     NOT NULL DEFAULT false,
//	reason        text        NULL,
//	pool_slot_cap integer     NULL,
//	updated_at    timestamptz NOT NULL DEFAULT now(),
//	updated_by    text        NULL
//
// A key of 'compute' with paused=true means platform compute is paused. A key of
// 'org:<lowercase-guid>' with paused=true means that organisation is disabled,
// and its pool_slot_cap (when present and greater than zero) overrides that
// organisation's CloudPool concurrency cap. A key with no row is enabled with no
// cap.
//
// THE POINT OF ALL OF THIS IS THAT A KILL SWITCH MUST NOT FAIL OPEN, which makes
// three of the rules here deliberately asymmetric:
//
//   - A read is cached for switchCacheTTL. Inside the TTL the cached value is
//     served; past it the store is re-read, and a read that FAILS is treated as
//     PAUSED. A stale "not paused" is never served, so a flipped switch takes
//     effect within the TTL and a settings outage pauses the platform rather
//     than releasing it.
//   - An unreadable store means no job is assigned at all. It is logged at ERROR
//     on the transition into the unreadable state (so a sustained outage is one
//     line rather than one per claim) and counted per failed read, so an
//     operator can alert on it.
//   - An unreadable store does NOT refuse a hello. 4403 is TERMINAL in
//     pkg/agent (retryableClose returns false), so terminating every desktop
//     runner's reconnect loop because the settings table was briefly unreadable
//     would be a worse bug than the switch it protects. The organisation check
//     therefore applies only to a snapshot a store actually produced; the runner
//     that slips through is still handed nothing, because the rule above already
//     refuses it work.

// switchCacheTTL is how long one read of platform_settings is served. F11 fixes
// the number ("reads a platform_settings row, cached 10 s") and it is also the
// upper bound on how long a flipped switch can take to take effect — in both
// directions, since a failed read is cached for the same window.
const switchCacheTTL = 10 * time.Second

// The platform_settings keys this gateway acts on.
//
// The 'inference' key is deliberately ignored: the jobs this gateway assigns are
// compute (Remotion sessions and ffmpeg work), never an inference call, so
// pausing inference is the engine's admission decision and not this service's.
// Ignoring a key can only ever leave more work assignable than the operator
// asked for if the key meant something here, and this one does not.
const (
	settingsKeyCompute   = "compute"
	settingsKeyOrgPrefix = "org:"
)

// errSwitchesUnavailable is what a gateway with no settings facade at all
// answers. It is a read failure like any other, so every caller treats it the
// same way: paused, and no work assigned.
var errSwitchesUnavailable = errors.New("gateway: platform switches are not configured")

// SwitchSnapshot is the platform's kill-switch state at the moment a store
// answered, together with the decisions the gateway derives from it.
//
// The fields are unexported and every accessor is written so that the ZERO VALUE
// means "paused". That is the whole reason this is not three plain fields: a
// snapshot nobody loaded — the zero value, or the one a failed read returns —
// must never be mistaken for a platform that is running normally, and
// ComputePaused is the one boolean that decides whether any work at all is
// assigned.
type SwitchSnapshot struct {
	// loaded records that a store actually answered. Nothing else in this struct
	// means anything without it.
	loaded bool
	// computePaused is the 'compute' row's paused column: platform compute is
	// paused.
	computePaused bool
	// disabledOrgs holds the lowercased organisation ids whose 'org:<guid>' row
	// says paused=true.
	disabledOrgs map[string]struct{}
	// poolSlotCaps holds the per-organisation CloudPool slot-cap overrides. Only a
	// cap greater than zero is kept: the column is nullable and an explicit 0 or a
	// negative value is not a cap any claim could satisfy, so both read as "no
	// override" rather than as "cap this organisation at zero", which would be a
	// permanent stop that the operator did not ask for.
	poolSlotCaps map[string]int
}

// ComputePaused reports whether platform compute is paused, which is the one
// decision that settles whether any job may be assigned.
//
// It answers TRUE for a snapshot no store produced, so the unreadable case and
// the paused case take the same path through every caller: a kill switch that
// cannot be read is engaged, never released. There is deliberately no
// ComputeEnabled counterpart to reach for by mistake.
func (s SwitchSnapshot) ComputePaused() bool {
	return !s.loaded || s.computePaused
}

// OrganizationDisabled reports whether the platform has disabled an
// organisation.
//
// An unreadable snapshot answers false, and this is the ONE place the platform
// deliberately fails open: 4403 is terminal in pkg/agent, so a settings outage
// must not terminate a desktop runner's reconnect loop. Nothing is assigned to
// the runner that gets through, because ComputePaused is true for the very same
// snapshot — the handshake accepts a connection it cannot judge, and the claim
// path refuses it work it cannot justify.
func (s SwitchSnapshot) OrganizationDisabled(orgID string) bool {
	if !s.loaded || orgID == "" {
		return false
	}
	_, disabled := s.disabledOrgs[normalizeOrgID(orgID)]
	return disabled
}

// PoolSlotCap returns an organisation's CloudPool slot-cap override and whether
// the platform carries one for it at all.
func (s SwitchSnapshot) PoolSlotCap(orgID string) (int, bool) {
	if !s.loaded || orgID == "" {
		return 0, false
	}
	cap, ok := s.poolSlotCaps[normalizeOrgID(orgID)]
	return cap, ok
}

// ClaimGates is the per-candidate half of the switches, as ClaimJobs applies it:
// which organisations are disabled, and which carry a slot-cap override.
//
// It is produced from a snapshot by ClaimGates and is deliberately NOT itself
// fail-closed: an empty ClaimGates reads as "no organisation is disabled", which
// is only a truthful answer for a snapshot a store produced. The authority that
// a read happened lives one level up, in the gateway's claim gate, which refuses
// to call ClaimJobs at all without a loaded snapshot — so an unreadable store
// assigns nothing before this type is ever consulted.
type ClaimGates struct {
	// The maps are shared with the snapshot that produced them, which is safe
	// because a refresh replaces them wholesale and never mutates them in place;
	// treat them as read-only.
	disabledOrgs map[string]struct{}
	poolSlotCaps map[string]int
}

// ClaimGates projects the snapshot onto the two per-candidate decisions.
func (s SwitchSnapshot) ClaimGates() ClaimGates {
	if !s.loaded {
		return ClaimGates{}
	}
	return ClaimGates{disabledOrgs: s.disabledOrgs, poolSlotCaps: s.poolSlotCaps}
}

// Disabled reports whether an organisation is disabled in this decision.
func (g ClaimGates) Disabled(orgID string) bool {
	_, disabled := g.disabledOrgs[normalizeOrgID(orgID)]
	return disabled
}

// PoolCap returns an organisation's slot-cap override, if the platform carries
// one.
func (g ClaimGates) PoolCap(orgID string) (int, bool) {
	cap, ok := g.poolSlotCaps[normalizeOrgID(orgID)]
	return cap, ok
}

// normalizeOrgID folds an organisation id to the form a settings key uses. The
// platform writes the guid lowercased, but a hand-written row need not be, and
// two spellings of one guid are one organisation: both sides go through this
// before they are compared.
func normalizeOrgID(orgID string) string {
	return strings.ToLower(strings.TrimSpace(orgID))
}

// switchRow is one platform_settings row, in the shape the store reads it.
type switchRow struct {
	Key         string
	Paused      bool
	PoolSlotCap *int
}

// foldSwitchRows turns the rows of platform_settings into the decision they
// imply.
//
// It is pure and separate from the query so the row semantics — which keys mean
// what, and how an override column is read — are testable without a database,
// and so the SQL has no policy in it.
func foldSwitchRows(rows []switchRow) SwitchSnapshot {
	snapshot := SwitchSnapshot{
		loaded:       true,
		disabledOrgs: map[string]struct{}{},
		poolSlotCaps: map[string]int{},
	}
	for _, row := range rows {
		switch {
		case row.Key == settingsKeyCompute:
			snapshot.computePaused = row.Paused
		case strings.HasPrefix(row.Key, settingsKeyOrgPrefix):
			orgID := normalizeOrgID(strings.TrimPrefix(row.Key, settingsKeyOrgPrefix))
			if orgID == "" {
				// An 'org:' key with nothing after it names no organisation, so it
				// disables nothing and caps nothing. Failing the whole read instead
				// would let one junk row pause the platform.
				continue
			}
			if row.Paused {
				snapshot.disabledOrgs[orgID] = struct{}{}
			}
			if row.PoolSlotCap != nil && *row.PoolSlotCap > 0 {
				snapshot.poolSlotCaps[orgID] = *row.PoolSlotCap
			}
		default:
			// A key this gateway does not act on ('inference', or one a later WP
			// adds). It is not an error: settings keys are an open set.
		}
	}
	return snapshot
}

// SwitchStore reads the platform settings. It is an interface so the switch
// logic and the claim gate can be tested without a database.
type SwitchStore interface {
	// ReadPlatformSettings reads every platform_settings row in ONE query and
	// returns the decision they imply.
	//
	// A snapshot it returns is always loaded, so a caller that got a snapshot and
	// no error is entitled to read it as the platform's real state. An error means
	// the state is unknown — including "this database has no platform_settings
	// table", which is the shape a deployment that has not run the migration
	// presents.
	ReadPlatformSettings(ctx context.Context) (SwitchSnapshot, error)
}

// PostgresSwitchStore reads platform_settings through pgx. It is a third pool
// over the same DATABASE_URL the device registry and the queue already use,
// which is how those two are wired today.
type PostgresSwitchStore struct {
	pool *pgxpool.Pool
}

// NewPostgresSwitchStore connects and verifies the connection. maxConns is
// DATABASE_MAX_CONNS (zero = unset; see pool.go for the precedence).
func NewPostgresSwitchStore(ctx context.Context, databaseURL string, maxConns int32) (*PostgresSwitchStore, error) {
	pool, err := newPgxPool(ctx, databaseURL, maxConns, 1, poolSwitches)
	if err != nil {
		return nil, err
	}
	store := &PostgresSwitchStore{pool: pool}
	if err := pool.Ping(ctx); err != nil {
		pool.Close()
		return nil, fmt.Errorf("ping postgres: %w", err)
	}
	return store, nil
}

// Close releases the connection pool.
func (s *PostgresSwitchStore) Close() { s.pool.Close() }

// ReadPlatformSettings reads the whole table. It is one query with no WHERE
// clause: the table is a handful of rows by construction, and a query that could
// return a subset would let a missing row read as "enabled".
func (s *PostgresSwitchStore) ReadPlatformSettings(ctx context.Context) (SwitchSnapshot, error) {
	rows, err := s.pool.Query(ctx, "SELECT key, paused, pool_slot_cap FROM platform_settings")
	if err != nil {
		return SwitchSnapshot{}, fmt.Errorf("read platform_settings: %w", err)
	}
	defer rows.Close()
	var parsed []switchRow
	for rows.Next() {
		var row switchRow
		if err := rows.Scan(&row.Key, &row.Paused, &row.PoolSlotCap); err != nil {
			return SwitchSnapshot{}, fmt.Errorf("scan platform_settings: %w", err)
		}
		parsed = append(parsed, row)
	}
	if err := rows.Err(); err != nil {
		return SwitchSnapshot{}, fmt.Errorf("read platform_settings: %w", err)
	}
	return foldSwitchRows(parsed), nil
}

// platformSwitches is the gateway's cached view of platform_settings.
//
// The cache exists because every claim round and every ready message would
// otherwise ask the database, and a deploy that reconnects a fleet at once would
// turn a table of a few rows into a hot spot. One read serves every caller for
// switchCacheTTL, and concurrent callers that find the cache cold share a SINGLE
// in-flight read rather than each starting one.
//
// A failed read is cached for the same window: it is what the gateway serves
// until the next attempt, so an outage costs one query per TTL instead of one
// per claim.
type platformSwitches struct {
	store   SwitchStore
	metrics *gatewayMetrics
	ttl     time.Duration
	// now is the clock, so a test can age the cache without sleeping. It is the
	// same seam the gateway itself uses (gateway.now).
	now func() time.Time

	mu       sync.Mutex
	snapshot SwitchSnapshot
	// loadedAt is when the cached read happened. The zero time means "nothing has
	// been read yet", the one state in which nothing at all is served and every
	// caller joins the first refresh.
	loadedAt time.Time
	// lastErr is the error the cached read failed with, remembered so that
	// followers share the leader's answer exactly.
	lastErr error
	// inflight is the one refresh a cold cache is allowed to have running.
	inflight *switchRefresh
	// unreadable records the previous read's outcome so that the ERROR line marks
	// the transition into the unreadable state instead of repeating on every
	// claim. It is observability only: it never decides anything, which is why it
	// is allowed to start as false.
	unreadable bool
}

// switchRefresh is one read in flight. Its fields are written by the leader
// before done is closed and read by every follower afterwards, which is what
// makes the handoff race-free.
type switchRefresh struct {
	done     chan struct{}
	snapshot SwitchSnapshot
	err      error
}

// newPlatformSwitches builds the facade over a store. Every gateway has one
// except in the tests that deliberately leave it out.
func newPlatformSwitches(store SwitchStore, metrics *gatewayMetrics) *platformSwitches {
	return &platformSwitches{store: store, metrics: metrics, ttl: switchCacheTTL, now: time.Now}
}

// prime reads the switches once, before the gateway serves anything, so that a
// startup with an unreadable store says so at startup rather than on the first
// claim.
//
// It cannot fail the process, and must not: F11's rule is that an unreadable
// store assigns nothing, not that it stops the gateway. The error is logged by
// Snapshot, once, on the transition into the unreadable state — which the first
// failed read always is.
func (p *platformSwitches) prime(ctx context.Context) { _, _ = p.Snapshot(ctx) }

// Snapshot returns the platform's kill-switch state, re-reading the store when
// the cache has aged out.
//
// A read failure is returned as an error AND is what the returned snapshot says:
// its ComputePaused is true, so a caller that ignores the error still refuses
// work rather than assigning it.
//
// A nil facade — a gateway started without one — fails closed the same way. That
// is the direction a missing dependency must fall: "no switches configured" can
// never mean "no gating".
func (p *platformSwitches) Snapshot(ctx context.Context) (SwitchSnapshot, error) {
	if p == nil {
		return SwitchSnapshot{}, errSwitchesUnavailable
	}
	p.mu.Lock()
	if p.freshLocked() {
		snapshot, err := p.snapshot, p.lastErr
		p.mu.Unlock()
		return snapshot, err
	}
	call := p.inflight
	if call == nil {
		// This caller leads the refresh. The lock is released first so the store
		// call cannot block every other reader.
		call = &switchRefresh{done: make(chan struct{})}
		p.inflight = call
		p.mu.Unlock()
		p.runRefresh(ctx, call)
	} else {
		p.mu.Unlock()
	}
	// A follower waits for the leader's answer. Its own cancellation gives up and
	// fails closed rather than guessing, and — because the leader's context is
	// what the store sees — a leader cancelled past its deadline fails the shared
	// read for everyone, which is the safe direction: the alternative is handing
	// back a value nobody could read.
	select {
	case <-call.done:
	case <-ctx.Done():
		return SwitchSnapshot{}, ctx.Err()
	}
	return call.snapshot, call.err
}

// freshLocked reports whether the cached answer may still be served, whether it
// was a snapshot or a failure.
func (p *platformSwitches) freshLocked() bool {
	return !p.loadedAt.IsZero() && p.now().Sub(p.loadedAt) < p.ttl
}

// runRefresh performs the one read a cold cache is allowed, publishes it, and
// releases the followers.
func (p *platformSwitches) runRefresh(ctx context.Context, call *switchRefresh) {
	snapshot, err := p.store.ReadPlatformSettings(ctx)
	if err != nil {
		// The read failed, so the platform's state is UNKNOWN, and unknown is
		// paused. The zero snapshot is exactly that: a value whose every accessor
		// answers "refuse".
		snapshot = SwitchSnapshot{}
	}

	p.mu.Lock()
	p.snapshot = snapshot
	p.lastErr = err
	p.loadedAt = p.now()
	enteredUnreadable := err != nil && !p.unreadable
	leftUnreadable := err == nil && p.unreadable
	p.unreadable = err != nil
	p.inflight = nil
	p.mu.Unlock()

	if err != nil {
		// Counted per failed READ, not per decision: the counter answers "how
		// often could this gateway not read the switches", and the decisions it
		// cost are counted separately by
		// reelbolt_runner_gateway_claims_deferred_total.
		p.metrics.platformSwitchesUnreadable.Inc()
		p.metrics.setPlatformPaused(switchGaugeSettings, true)
		p.metrics.setPlatformPaused(switchGaugeCompute, true)
		if enteredUnreadable {
			log.Printf("ERROR platform switches are unreadable: %v; no runner work will be assigned until "+
				"platform_settings can be read", err)
		}
	} else {
		p.metrics.setPlatformPaused(switchGaugeSettings, false)
		p.metrics.setPlatformPaused(switchGaugeCompute, snapshot.ComputePaused())
		if leftUnreadable {
			state := "running"
			if snapshot.ComputePaused() {
				state = "paused"
			}
			log.Printf("platform switches are readable again: compute=%s", state)
		}
	}

	call.snapshot = snapshot
	call.err = err
	close(call.done)
}
