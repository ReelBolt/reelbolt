package main

import (
	"context"
	"fmt"
	"strconv"
	"strings"

	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
)

// The gateway opens three pgx pools over DATABASE_URL: devices (the registry),
// jobs (the queue) and switches (platform_settings). Each is capped at
// DATABASE_MAX_CONNS, so the gateway as a whole holds at most three times that
// against the shared Postgres, and that product is part of the deployment's
// connection budget. pgxpool's own default (the greater of 4 and the CPU count)
// grows with the machine, which is exactly what a budget cannot allow.
//
// Precedence for each pool's cap:
//  1. DATABASE_MAX_CONNS, when set (an integer >= 2, else startup fails);
//  2. pool_max_conns already present in DATABASE_URL, which is left alone;
//  3. defaultDatabaseMaxConns.
//
// The floor is 2 because the jobs pool PERMANENTLY holds one connection for
// LISTEN runner_jobs_new (PostgresJobStore.Listen): with a cap of 1 every claim,
// lease renewal and job write would queue forever behind the listener. The
// value applies to every pool, so DATABASE_MAX_CONNS=1 is refused outright, and
// a DATABASE_URL whose pool_max_conns=1 is refused when the jobs pool opens.
const (
	defaultDatabaseMaxConns = 4
	minJobsPoolConns        = 2
)

// Pool names: the label value of the pool metrics, and the subject of errors.
const (
	poolDevices  = "devices"
	poolJobs     = "jobs"
	poolSwitches = "switches"
)

// parseDatabaseMaxConns reads DATABASE_MAX_CONNS. Empty means unset (zero), so
// the URL or the default decides; anything else must be an integer of at least
// minJobsPoolConns.
func parseDatabaseMaxConns(raw string) (int32, error) {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return 0, nil
	}
	n, err := strconv.ParseInt(raw, 10, 32)
	if err != nil || n <= 0 {
		return 0, fmt.Errorf("DATABASE_MAX_CONNS must be a positive integer (got %q)", raw)
	}
	if n < minJobsPoolConns {
		return 0, fmt.Errorf("DATABASE_MAX_CONNS must be at least %d (got %d): the jobs pool keeps one "+
			"connection open for LISTEN runner_jobs_new, so a cap of 1 leaves none for the queue itself",
			minJobsPoolConns, n)
	}
	return int32(n), nil
}

// poolConfig parses databaseURL and applies the cap by the precedence above.
// maxConns is the parsed DATABASE_MAX_CONNS (zero = unset); minConns is the
// smallest cap this pool can work with.
func poolConfig(databaseURL string, maxConns, minConns int32, name string) (*pgxpool.Config, error) {
	cfg, err := pgxpool.ParseConfig(databaseURL)
	if err != nil {
		return nil, fmt.Errorf("parse DATABASE_URL: %w", err)
	}
	switch {
	case maxConns > 0:
		cfg.MaxConns = maxConns
	case urlSetsPoolMaxConns(databaseURL):
		// The operator chose this pool size in the URL; keep it.
	default:
		cfg.MaxConns = defaultDatabaseMaxConns
	}
	if cfg.MaxConns < minConns {
		return nil, fmt.Errorf("the %s pool needs at least %d connections (got %d from pool_max_conns "+
			"in DATABASE_URL): it keeps one open for LISTEN", name, minConns, cfg.MaxConns)
	}
	return cfg, nil
}

// urlSetsPoolMaxConns reports whether DATABASE_URL itself carries
// pool_max_conns. pgxpool.ParseConfig consumes the key without saying whether
// it was there, so the connection string is parsed once more by pgconn, which
// leaves unknown keys (the pool_* ones among them) in RuntimeParams. Both the
// URL and the keyword/value forms are covered that way.
func urlSetsPoolMaxConns(databaseURL string) bool {
	cc, err := pgconn.ParseConfig(databaseURL)
	if err != nil {
		return false
	}
	_, ok := cc.RuntimeParams["pool_max_conns"]
	return ok
}

// newPgxPool opens one of the gateway's pools with its cap applied. It does not
// ping: each store verifies its own connection the way it always has.
func newPgxPool(ctx context.Context, databaseURL string, maxConns, minConns int32, name string) (*pgxpool.Pool, error) {
	cfg, err := poolConfig(databaseURL, maxConns, minConns, name)
	if err != nil {
		return nil, err
	}
	pool, err := pgxpool.NewWithConfig(ctx, cfg)
	if err != nil {
		return nil, fmt.Errorf("connect to postgres: %w", err)
	}
	return pool, nil
}
