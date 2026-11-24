package config

import (
	"strings"
	"testing"
	"time"
)

func envOf(m map[string]string) func(string) string {
	return func(k string) string { return m[k] }
}

func TestParseDBPoolDefaults(t *testing.T) {
	pool, err := parseDBPool(envOf(nil))
	if err != nil {
		t.Fatalf("parseDBPool: %v", err)
	}
	want := DBPool{MaxOpenConns: 8, MaxIdleConns: 4, ConnMaxLifetime: 30 * time.Minute}
	if pool != want {
		t.Fatalf("pool = %+v, want %+v", pool, want)
	}
}

func TestParseDBPoolEmptyMeansDefault(t *testing.T) {
	pool, err := parseDBPool(envOf(map[string]string{
		"DB_MAX_OPEN_CONNS": "", "DB_MAX_IDLE_CONNS": "  ", "DB_CONN_MAX_LIFETIME": "",
	}))
	if err != nil {
		t.Fatalf("parseDBPool: %v", err)
	}
	if pool.MaxOpenConns != DefaultDBMaxOpenConns || pool.MaxIdleConns != DefaultDBMaxIdleConns ||
		pool.ConnMaxLifetime != DefaultDBConnMaxLifetime {
		t.Fatalf("pool = %+v, want the defaults", pool)
	}
}

func TestParseDBPoolExplicit(t *testing.T) {
	pool, err := parseDBPool(envOf(map[string]string{
		"DB_MAX_OPEN_CONNS": "25", "DB_MAX_IDLE_CONNS": "8", "DB_CONN_MAX_LIFETIME": "1h15m",
	}))
	if err != nil {
		t.Fatalf("parseDBPool: %v", err)
	}
	want := DBPool{MaxOpenConns: 25, MaxIdleConns: 8, ConnMaxLifetime: 75 * time.Minute}
	if pool != want {
		t.Fatalf("pool = %+v, want %+v", pool, want)
	}
}

func TestParseDBPoolInvalidNamesTheVariable(t *testing.T) {
	cases := []struct{ key, value string }{
		{"DB_MAX_OPEN_CONNS", "0"},
		{"DB_MAX_OPEN_CONNS", "-3"},
		{"DB_MAX_OPEN_CONNS", "ten"},
		{"DB_MAX_IDLE_CONNS", "0"},
		{"DB_MAX_IDLE_CONNS", "1.5"},
		{"DB_CONN_MAX_LIFETIME", "30"},
		{"DB_CONN_MAX_LIFETIME", "0s"},
		{"DB_CONN_MAX_LIFETIME", "-5m"},
		{"DB_CONN_MAX_LIFETIME", "forever"},
	}
	for _, c := range cases {
		_, err := parseDBPool(envOf(map[string]string{c.key: c.value}))
		if err == nil {
			t.Errorf("%s=%q: want an error", c.key, c.value)
			continue
		}
		if !strings.Contains(err.Error(), c.key) {
			t.Errorf("%s=%q: error %q does not name the variable", c.key, c.value, err)
		}
	}
}

func TestParseDBPoolClampsIdleToOpen(t *testing.T) {
	pool, err := parseDBPool(envOf(map[string]string{"DB_MAX_OPEN_CONNS": "3", "DB_MAX_IDLE_CONNS": "9"}))
	if err != nil {
		t.Fatalf("parseDBPool: %v", err)
	}
	if pool.MaxOpenConns != 3 || pool.MaxIdleConns != 3 {
		t.Fatalf("pool = %+v, want open 3 and idle clamped to 3", pool)
	}

	// The default idle (4) above an explicit open (2) is clamped too.
	pool, err = parseDBPool(envOf(map[string]string{"DB_MAX_OPEN_CONNS": "2"}))
	if err != nil {
		t.Fatalf("parseDBPool: %v", err)
	}
	if pool.MaxIdleConns != 2 {
		t.Fatalf("MaxIdleConns = %d, want 2", pool.MaxIdleConns)
	}
}

// ATTRIBUTION_URL_CAPTURE_ENABLED is a privacy switch (docs/design/new-user-funnel.md section 7):
// off unless the controller turns it on, and anything that is not a boolean is "off", never "on".
func TestAttributionURLCaptureIsOffUnlessTurnedOn(t *testing.T) {
	old := Cfg
	t.Cleanup(func() { Cfg = old })
	t.Setenv("JWT_SIGNING_KEY", "0123456789abcdef0123456789abcdef")
	t.Setenv("TENANCY_MODE", "Cloud")

	for value, want := range map[string]bool{"": false, "false": false, "0": false, "yes please": false, "true": true, "1": true} {
		t.Setenv("ATTRIBUTION_URL_CAPTURE_ENABLED", value)
		Load()
		if Cfg.AttributionURLCaptureEnabled != want {
			t.Fatalf("ATTRIBUTION_URL_CAPTURE_ENABLED=%q: got %v, want %v", value, Cfg.AttributionURLCaptureEnabled, want)
		}
	}
}
