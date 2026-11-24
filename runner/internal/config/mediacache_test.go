package config

import (
	"encoding/json"
	"strings"
	"testing"
)

func TestMediaCacheDefaultsToTenGiBAndZeroTurnsItOff(t *testing.T) {
	var m MediaCache
	if m.EffectiveMaxBytes() != DefaultMediaCacheBytes || !m.Enabled() {
		t.Fatalf("absent = %d", m.EffectiveMaxBytes())
	}
	m.SetMaxBytes(0)
	if m.Enabled() || m.EffectiveMaxBytes() != 0 {
		t.Fatal("0 did not turn the cache off")
	}
	if err := m.Validate(); err != nil {
		t.Fatalf("0 is a valid answer: %v", err)
	}
}

func TestMediaCacheValidateRefusesCapsTheCacheCannotHonour(t *testing.T) {
	for _, n := range []int64{-1, 1, MinMediaCacheBytes - 1} {
		m := MediaCache{}
		m.SetMaxBytes(n)
		if err := m.Validate(); err == nil {
			t.Errorf("%d was accepted", n)
		}
	}
	for _, n := range []int64{MinMediaCacheBytes, DefaultMediaCacheBytes, 1 << 40} {
		m := MediaCache{}
		m.SetMaxBytes(n)
		if err := m.Validate(); err != nil {
			t.Errorf("%d was refused: %v", n, err)
		}
	}
}

// The block round-trips through runner.json, and Config.Validate checks it.
func TestMediaCacheIsPartOfRunnerJSON(t *testing.T) {
	var c Config
	if err := json.Unmarshal([]byte(`{"mediaCache":{"maxBytes":1024}}`), &c); err != nil {
		t.Fatal(err)
	}
	c.ApplyDefaults()
	if err := c.Validate(); err == nil || !strings.Contains(err.Error(), "mediaCache") {
		t.Fatalf("Validate = %v, want the mediaCache refusal", err)
	}
	c.MediaCache.SetMaxBytes(0)
	raw, err := json.Marshal(c)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(raw), `"mediaCache":{"maxBytes":0}`) {
		t.Fatalf("an explicit off was not written: %s", raw)
	}
	var old Config
	if err := json.Unmarshal([]byte(`{}`), &old); err != nil {
		t.Fatal(err)
	}
	if old.MediaCache.EffectiveMaxBytes() != DefaultMediaCacheBytes {
		t.Fatal("a runner.json written before the cache existed does not get the default")
	}
}
