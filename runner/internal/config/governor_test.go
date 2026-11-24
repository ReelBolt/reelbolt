package config

import (
	"encoding/json"
	"os"
	"strings"
	"testing"

	"github.com/vecchiotom/reelbolt/runner/internal/governor"
)

// The [limits] and [schedule] blocks are part of runner.json now, so they are
// validated on every load like everything else: a typo must be a startup
// refusal that names the field, never a machine that quietly stops working.
func TestValidateRefusesBadLimitsAndSchedule(t *testing.T) {
	base := func() *Config {
		c := &Config{GatewayURL: "wss://runner.reelbolt.ai/v1/connect", DeviceID: "d", Slots: 1}
		c.ApplyDefaults()
		return c
	}
	cases := []struct {
		name string
		mut  func(*Config)
		want string
	}{
		{"memory too small", func(c *Config) { c.Limits.Memory = 1 << 20 }, "limits: memory"},
		{"unknown priority", func(c *Config) { c.Limits.Priority = "urgent" }, "limits: priority"},
		{"unknown mode", func(c *Config) { c.Schedule.Mode = "whenever" }, "schedule: mode"},
		{"bad window", func(c *Config) {
			c.Schedule.Mode = governor.ModeWindows
			c.Schedule.Windows = []governor.Window{{Start: "9am", End: "5pm"}}
		}, "schedule: windows"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			c := base()
			tc.mut(c)
			err := c.Validate()
			if err == nil {
				t.Fatalf("Validate accepted %+v", c)
			}
			if !strings.Contains(err.Error(), tc.want) {
				t.Fatalf("error %q does not name the field %q", err, tc.want)
			}
		})
	}
	if err := base().Validate(); err != nil {
		t.Fatalf("the defaults must validate: %v", err)
	}
}

// Defaults are written into the file, so a user opening runner.json sees the
// knobs they can turn instead of having to read the source.
func TestDefaultsAreWrittenToTheFile(t *testing.T) {
	c := &Config{GatewayURL: "wss://runner.reelbolt.ai/v1/connect", DeviceID: "d", Slots: 1}
	c.ApplyDefaults()
	raw, err := json.MarshalIndent(c, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{"limits", "schedule", "memory", "ffmpegThreads", "onBattery"} {
		if !strings.Contains(string(raw), want) {
			t.Errorf("runner.json does not show %q: %s", want, raw)
		}
	}
}

// A schedule written as a string with a unit, as a person would write it.
func TestLimitsAndScheduleRoundTripThroughTheFile(t *testing.T) {
	path := t.TempDir() + "/" + FileName
	body := `{
  "gatewayUrl": "wss://runner.reelbolt.ai/v1/connect",
  "deviceId": "11111111-2222-3333-4444-555555555555",
  "slots": 1,
  "limits": {"memory": "3g", "cpus": 1.5, "pids": 256, "priority": "low"},
  "schedule": {"mode": "only-when-idle", "idleMinutes": 10, "onBattery": "pause"}
}`
	if err := os.WriteFile(path, []byte(body), 0o600); err != nil {
		t.Fatal(err)
	}
	c, err := LoadFrom(path)
	if err != nil {
		t.Fatalf("LoadFrom: %v", err)
	}
	if c.Limits.Memory.Int64() != 3<<30 {
		t.Errorf("memory = %s, want 3g", c.Limits.Memory)
	}
	if c.Limits.CPUs != 1.5 || c.Limits.Priority != governor.PriorityLow {
		t.Errorf("limits = %+v", c.Limits)
	}
	if c.Schedule.Mode != governor.ModeIdle {
		t.Errorf("mode = %q, want idle (the alias must be normalized)", c.Schedule.Mode)
	}
	if c.Schedule.IdleMinutes != 10 || c.Schedule.OnBattery != governor.BatteryPause {
		t.Errorf("schedule = %+v", c.Schedule)
	}
}
