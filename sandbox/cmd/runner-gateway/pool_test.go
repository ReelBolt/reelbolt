package main

import (
	"strings"
	"testing"
)

const poolTestURL = "postgres://u:p@127.0.0.1:5432/db?sslmode=disable"

func TestParseDatabaseMaxConns(t *testing.T) {
	for _, raw := range []string{"", "   "} {
		n, err := parseDatabaseMaxConns(raw)
		if err != nil || n != 0 {
			t.Errorf("%q: got %d, %v; want 0 (unset), nil", raw, n, err)
		}
	}
	for raw, want := range map[string]int32{"2": 2, "4": 4, "12": 12, "\t3\n": 3} {
		n, err := parseDatabaseMaxConns(raw)
		if err != nil || n != want {
			t.Errorf("%q: got %d, %v; want %d", raw, n, err, want)
		}
	}
	for _, raw := range []string{"0", "-1", "abc", "2.5", "99999999999"} {
		if _, err := parseDatabaseMaxConns(raw); err == nil || !strings.Contains(err.Error(), "DATABASE_MAX_CONNS") {
			t.Errorf("%q: want an error naming DATABASE_MAX_CONNS, got %v", raw, err)
		}
	}
	_, err := parseDatabaseMaxConns("1")
	if err == nil || !strings.Contains(err.Error(), "DATABASE_MAX_CONNS") || !strings.Contains(err.Error(), "LISTEN") {
		t.Errorf("1: want an error naming DATABASE_MAX_CONNS and the LISTEN connection, got %v", err)
	}
}

func TestLoadConfigDatabaseMaxConns(t *testing.T) {
	t.Setenv("DATABASE_MAX_CONNS", "")
	cfg, err := loadConfig()
	if err != nil || cfg.DatabaseMaxConns != 0 {
		t.Fatalf("unset: got %d, %v; want 0, nil", cfg.DatabaseMaxConns, err)
	}
	t.Setenv("DATABASE_MAX_CONNS", "6")
	if cfg, err = loadConfig(); err != nil || cfg.DatabaseMaxConns != 6 {
		t.Fatalf("6: got %d, %v; want 6, nil", cfg.DatabaseMaxConns, err)
	}
	t.Setenv("DATABASE_MAX_CONNS", "1")
	if _, err = loadConfig(); err == nil {
		t.Fatal("1: want a startup error")
	}
	t.Setenv("DATABASE_MAX_CONNS", "lots")
	if _, err = loadConfig(); err == nil {
		t.Fatal("lots: want a startup error")
	}
}

func TestPoolConfigPrecedence(t *testing.T) {
	cases := []struct {
		name     string
		url      string
		maxConns int32
		want     int32
	}{
		{"default", poolTestURL, 0, defaultDatabaseMaxConns},
		{"url wins over default", poolTestURL + "&pool_max_conns=9", 0, 9},
		{"keyword/value url wins over default", "host=127.0.0.1 user=u dbname=db pool_max_conns=7", 0, 7},
		{"env wins over url", poolTestURL + "&pool_max_conns=9", 3, 3},
		{"env wins over default", poolTestURL, 5, 5},
	}
	for _, c := range cases {
		cfg, err := poolConfig(c.url, c.maxConns, minJobsPoolConns, poolJobs)
		if err != nil {
			t.Errorf("%s: %v", c.name, err)
			continue
		}
		if cfg.MaxConns != c.want {
			t.Errorf("%s: MaxConns = %d, want %d", c.name, cfg.MaxConns, c.want)
		}
	}
}

func TestPoolConfigJobsPoolFloor(t *testing.T) {
	url := poolTestURL + "&pool_max_conns=1"
	if _, err := poolConfig(url, 0, minJobsPoolConns, poolJobs); err == nil || !strings.Contains(err.Error(), "jobs") {
		t.Fatalf("jobs pool with pool_max_conns=1: want an error naming the pool, got %v", err)
	}
	// The other pools hold no LISTEN connection, so an operator's 1 stands there.
	cfg, err := poolConfig(url, 0, 1, poolDevices)
	if err != nil || cfg.MaxConns != 1 {
		t.Fatalf("devices pool with pool_max_conns=1: got %v, %v", cfg, err)
	}
	// An explicit env value overrides a too-small URL value.
	if cfg, err = poolConfig(url, 2, minJobsPoolConns, poolJobs); err != nil || cfg.MaxConns != 2 {
		t.Fatalf("env 2 over url 1: got %v, %v", cfg, err)
	}
}

func TestPoolConfigBadURL(t *testing.T) {
	if _, err := poolConfig("postgres://u:p@127.0.0.1/db?pool_max_conns=zero", 0, 1, poolDevices); err == nil {
		t.Fatal("unparsable pool_max_conns: want an error")
	}
}

func TestDBPoolMetrics(t *testing.T) {
	m := newGatewayMetrics()
	m.observePools(map[string]func() poolStats{
		poolDevices:  func() poolStats { return poolStats{acquired: 1, idle: 2, max: 4} },
		poolJobs:     func() poolStats { return poolStats{acquired: 3, idle: 0, max: 5} },
		poolSwitches: func() poolStats { return poolStats{acquired: 0, idle: 1, max: 6} },
	})
	families, err := m.registry.Gather()
	if err != nil {
		t.Fatalf("gather: %v", err)
	}
	got := map[string]float64{}
	for _, f := range families {
		if !strings.HasPrefix(f.GetName(), "reelbolt_runner_gateway_db_pool_") {
			continue
		}
		for _, metric := range f.GetMetric() {
			pool := ""
			for _, l := range metric.GetLabel() {
				if l.GetName() == "pool" {
					pool = l.GetValue()
				}
			}
			got[strings.TrimPrefix(f.GetName(), "reelbolt_runner_gateway_db_pool_")+"/"+pool] = metric.GetGauge().GetValue()
		}
	}
	want := map[string]float64{
		"acquired_connections/devices": 1, "idle_connections/devices": 2, "max_connections/devices": 4,
		"acquired_connections/jobs": 3, "idle_connections/jobs": 0, "max_connections/jobs": 5,
		"acquired_connections/switches": 0, "idle_connections/switches": 1, "max_connections/switches": 6,
	}
	if len(got) != len(want) {
		t.Fatalf("series = %v, want %v", got, want)
	}
	for k, v := range want {
		if g, ok := got[k]; !ok || g != v {
			t.Errorf("%s = %v (present %v), want %v", k, g, ok, v)
		}
	}
}
