package config

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"
)

// The single most important property of E11 is that the default is OFF, so it
// is the first thing tested - including for a file written before the feature
// existed, which is what every runner.json on every machine is today.
func TestTelemetryIsOffUnlessTheUserTurnsItOn(t *testing.T) {
	var zero Config
	if zero.Telemetry.On() {
		t.Error("the zero configuration has telemetry on")
	}
	if zero.Telemetry.Asked() {
		t.Error("the zero configuration claims the wizard has asked")
	}

	// A file written by a build that predates E11.
	raw := `{"deviceId":"dev-1","gatewayUrl":"wss://runner.reelbolt.ai/v1/connect","slots":1}`
	path := filepath.Join(t.TempDir(), "runner.json")
	if err := os.WriteFile(path, []byte(raw), 0o600); err != nil {
		t.Fatalf("write: %v", err)
	}
	cfg, err := LoadFrom(path)
	if err != nil {
		t.Fatalf("LoadFrom: %v", err)
	}
	if cfg.Telemetry.On() {
		t.Error("a pre-E11 runner.json turned telemetry on")
	}
}

// An explicit "off" is recorded rather than omitted, and an explicit "on" needs
// the disclosure version that was accepted.
func TestTelemetrySetRecordsTheAnswerAndTheDisclosure(t *testing.T) {
	at := time.Date(2026, 10, 8, 9, 14, 12, 0, time.UTC)
	var tel Telemetry
	tel.Set(true, at, "2026-10-08")
	if !tel.On() {
		t.Fatal("Set(true) did not turn it on")
	}
	if !tel.Asked() || tel.AskedAt != "2026-10-08T09:14:12Z" {
		t.Errorf("AskedAt = %q", tel.AskedAt)
	}
	if tel.ConsentVersion != "2026-10-08" {
		t.Errorf("ConsentVersion = %q", tel.ConsentVersion)
	}

	// Declining withdraws the consent, so a later build cannot read an old
	// "yes" out of a "no".
	tel.Set(false, at, "2026-10-08")
	if tel.On() {
		t.Error("Set(false) left it on")
	}
	if tel.ConsentVersion != "" {
		t.Errorf("ConsentVersion = %q after declining, want empty", tel.ConsentVersion)
	}
	if !tel.Asked() {
		t.Error("declining was not recorded as an answer, so the wizard would ask again")
	}

	// A later turn-on keeps the original askedAt rather than moving it.
	first := tel.AskedAt
	tel.Set(true, at.Add(time.Hour), "2026-10-08")
	if tel.AskedAt != first {
		t.Errorf("AskedAt moved from %q to %q", first, tel.AskedAt)
	}
}

// The answer survives a round trip through the file: this is what the Settings
// screen writes and the daemon reads.
func TestTelemetryRoundTripsThroughRunnerJSON(t *testing.T) {
	path := filepath.Join(t.TempDir(), "runner.json")
	cfg := &Config{Slots: 1, Telemetry: Telemetry{Enabled: boolPtr(true), AskedAt: "2026-10-08T09:14:12Z", ConsentVersion: "2026-10-08"}}
	cfg.ApplyDefaults()
	if err := cfg.SaveTo(path); err != nil {
		t.Fatalf("SaveTo: %v", err)
	}
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	var doc map[string]any
	if err := json.Unmarshal(raw, &doc); err != nil {
		t.Fatalf("parse: %v", err)
	}
	if _, ok := doc["telemetry"]; !ok {
		t.Errorf("the telemetry block is not written at all: %s", raw)
	}
	back, err := LoadFrom(path)
	if err != nil {
		t.Fatalf("LoadFrom: %v", err)
	}
	if !back.Telemetry.On() || back.Telemetry.ConsentVersion != "2026-10-08" {
		t.Errorf("telemetry did not survive the round trip: %+v", back.Telemetry)
	}
}

// A timestamp the daemon cannot read would make "has the wizard asked?"
// unanswerable, which is a configuration the runner must refuse.
func TestTelemetryRejectsAnUnreadableTimestamp(t *testing.T) {
	cfg := &Config{Slots: 1}
	cfg.ApplyDefaults()
	cfg.Telemetry.AskedAt = "last Tuesday"
	if err := cfg.Validate(); err == nil {
		t.Fatal("Validate accepted an unreadable askedAt")
	}
}

func boolPtr(v bool) *bool { return &v }
