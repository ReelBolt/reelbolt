package config

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestLoadMissingFileYieldsDefaults(t *testing.T) {
	path := filepath.Join(t.TempDir(), FileName)
	cfg, err := LoadFrom(path)
	if err != nil {
		t.Fatalf("LoadFrom: %v", err)
	}
	if cfg.Slots != DefaultSlots {
		t.Errorf("Slots = %d, want %d", cfg.Slots, DefaultSlots)
	}
	if cfg.DrainSeconds != DefaultDrainSeconds {
		t.Errorf("DrainSeconds = %d, want %d", cfg.DrainSeconds, DefaultDrainSeconds)
	}
	if cfg.IsPaired() {
		t.Error("a missing file must not look paired")
	}
	if cfg.DeviceName == "" {
		t.Error("DeviceName should default to the host name")
	}
}

func TestSaveLoadRoundTripIsPrivate(t *testing.T) {
	path := filepath.Join(t.TempDir(), "nested", FileName)
	in := &Config{
		GatewayURL:     "wss://runner.reelbolt.ai/v1/connect",
		DeviceID:       "11111111-2222-3333-4444-555555555555",
		DeviceName:     "studio-imac",
		OrganizationID: "99999999-2222-3333-4444-555555555555",
		Slots:          3,
		RuntimeImage:   "ghcr.io/reelbolt/reelbolt-sandbox-runtime@sha256:" + strings.Repeat("a", 64),
	}
	if err := in.SaveTo(path); err != nil {
		t.Fatalf("SaveTo: %v", err)
	}
	info, err := os.Stat(path)
	if err != nil {
		t.Fatalf("stat: %v", err)
	}
	if perm := info.Mode().Perm(); perm != 0o600 {
		t.Errorf("config mode = %o, want 600", perm)
	}
	out, err := LoadFrom(path)
	if err != nil {
		t.Fatalf("LoadFrom: %v", err)
	}
	if out.DeviceID != in.DeviceID || out.Slots != in.Slots || out.GatewayURL != in.GatewayURL {
		t.Errorf("round trip lost data: %+v", out)
	}
	if !out.IsPaired() {
		t.Error("a config with a device id and gateway must be paired")
	}
}

func TestLoadRejectsCorruptFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), FileName)
	if err := os.WriteFile(path, []byte("{not json"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := LoadFrom(path); err == nil {
		t.Fatal("a corrupt config must be an error, not a silent default")
	}
}

func TestValidate(t *testing.T) {
	base := func() *Config {
		c := &Config{GatewayURL: "wss://runner.reelbolt.ai/v1/connect", DeviceID: "d", Slots: 1}
		c.ApplyDefaults()
		return c
	}
	cases := []struct {
		name string
		mut  func(*Config)
		ok   bool
	}{
		{"default", func(*Config) {}, true},
		{"slots too high", func(c *Config) { c.Slots = MaxSlots + 1 }, false},
		{"slots zero", func(c *Config) { c.Slots = 0 }, false},
		{"relative scratch root", func(c *Config) { c.ScratchRoot = "relative/dir" }, false},
		{"absolute scratch root", func(c *Config) { c.ScratchRoot = filepath.Join(string(filepath.Separator), "var", "lib") }, true},
		{"unknown cli", func(c *Config) { c.ContainerCLI = "containerd" }, false},
		{"podman", func(c *Config) { c.ContainerCLI = "podman" }, true},
		{"paired without a gateway", func(c *Config) { c.GatewayURL = "" }, false},
		{"nul byte in name", func(c *Config) { c.DeviceName = "a\x00b" }, false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			c := base()
			tc.mut(c)
			err := c.Validate()
			if tc.ok && err != nil {
				t.Fatalf("Validate: %v", err)
			}
			if !tc.ok && err == nil {
				t.Fatal("Validate accepted a configuration it must refuse")
			}
		})
	}
}

func TestCheckGatewayAllowlist(t *testing.T) {
	cases := []struct {
		name     string
		url      string
		dev      []string
		accepted bool
	}{
		{"product domain", "wss://runner.reelbolt.ai/v1/connect", nil, true},
		{"apex", "wss://reelbolt.ai/v1/connect", nil, true},
		{"deep subdomain", "wss://a.b.reelbolt.ai/v1/connect", nil, true},
		{"suffix lookalike", "wss://reelbolt.ai.example.com/v1/connect", nil, false},
		{"prefix lookalike", "wss://notreelbolt.ai/v1/connect", nil, false},
		{"other host", "wss://evil.example/v1/connect", nil, false},
		{"insecure remote", "ws://evil.example/v1/connect", nil, false},
		{"plaintext product domain", "ws://runner.reelbolt.ai/v1/connect", nil, false},
		{"loopback dev", "ws://127.0.0.1:8090/v1/connect", nil, true},
		{"loopback localhost", "ws://localhost:8090/v1/connect", nil, true},
		{"unsupported scheme", "http://runner.reelbolt.ai/v1/connect", nil, false},
		{"no host", "wss:///v1/connect", nil, false},
		{"dev gateway declared", "wss://gw.dev.internal/v1/connect", []string{"gw.dev.internal"}, true},
		{"dev gateway with port", "ws://127.0.0.1:9000/v1/connect", []string{"127.0.0.1:9000"}, true},
		{"dev gateway not declared", "wss://gw.dev.internal/v1/connect", []string{"other.internal"}, false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			err := CheckGateway(tc.url, tc.dev)
			if tc.accepted && err != nil {
				t.Fatalf("CheckGateway(%q) = %v, want acceptance", tc.url, err)
			}
			if !tc.accepted && err == nil {
				t.Fatalf("CheckGateway(%q) accepted a gateway it must refuse", tc.url)
			}
		})
	}
}

func TestValidateRefusesGatewayOutsideTheAllowlist(t *testing.T) {
	c := &Config{GatewayURL: "wss://evil.example/v1/connect", Slots: 1}
	c.ApplyDefaults()
	if err := c.Validate(); err == nil {
		t.Fatal("a config naming an unknown gateway must not validate")
	}
}

func TestConfigFileCarriesNoSecret(t *testing.T) {
	// The device key belongs in the keystore, never in runner.json. This is the
	// regression guard for someone adding a convenient field later.
	c := &Config{GatewayURL: "wss://runner.reelbolt.ai/v1/connect", DeviceID: "d", Slots: 1}
	c.ApplyDefaults()
	raw, err := json.Marshal(c)
	if err != nil {
		t.Fatal(err)
	}
	body := strings.ToLower(string(raw))
	for _, banned := range []string{"privatekey", "private_key", "secret", "token", "password"} {
		if strings.Contains(body, banned) {
			t.Errorf("runner.json contains %q: %s", banned, body)
		}
	}
}

// ---- update settings (E9) ----

func TestUpdateDefaults(t *testing.T) {
	var c Config
	c.ApplyDefaults()
	if !c.UpdatesEnabled() {
		t.Error("updates are off by default; the released runner updates itself unless told not to")
	}
	if c.UpdateChannelOr() != DefaultUpdateChannel {
		t.Errorf("UpdateChannelOr = %q, want %q", c.UpdateChannelOr(), DefaultUpdateChannel)
	}
	if c.UpdateInterval() != DefaultUpdateIntervalHours*time.Hour {
		t.Errorf("UpdateInterval = %s, want %dh", c.UpdateInterval(), DefaultUpdateIntervalHours)
	}
}

func TestAutoUpdateCanBeTurnedOffExplicitly(t *testing.T) {
	off := false
	c := Config{AutoUpdate: &off}
	if c.UpdatesEnabled() {
		t.Error("autoUpdate: false did not turn updates off")
	}
	on := true
	c.AutoUpdate = &on
	if !c.UpdatesEnabled() {
		t.Error("autoUpdate: true did not turn updates on")
	}
	// Absent is not the same as false: a configuration written before this key
	// existed keeps updating.
	c.AutoUpdate = nil
	if !c.UpdatesEnabled() {
		t.Error("an absent autoUpdate key turned updates off")
	}
}

func TestUpdateSettingsAreValidated(t *testing.T) {
	// Every fixture is completed with its defaults before it is validated,
	// because that is what a loader does: LoadFrom calls ApplyDefaults and only
	// then Validate. Validating a raw struct instead would make each case below
	// fail on the ABSENT limits block rather than on the rule it names, so a
	// regression in the rule under test could hide behind an unrelated error.
	valid := func(c Config) error {
		c.ApplyDefaults()
		return c.Validate()
	}

	if err := valid(Config{Slots: 1, UpdateChannel: "nightly"}); err == nil {
		t.Error("an unknown update channel was accepted")
	}
	if err := valid(Config{Slots: 1, UpdateChannel: "beta"}); err != nil {
		t.Errorf("the beta channel was refused: %v", err)
	}
	if err := valid(Config{Slots: 1, UpdateIntervalHours: -1}); err == nil {
		t.Error("a negative update interval was accepted")
	}
	if err := valid(Config{Slots: 1, UpdateIntervalHours: MaxUpdateIntervalHours + 1}); err == nil {
		t.Error("an absurd update interval was accepted")
	}
	if err := valid(Config{Slots: 1, UpdateBaseURL: strings.Repeat("x", 5000)}); err == nil {
		t.Error("an unbounded updateBaseUrl was accepted")
	}
}

func TestUpdateSettingsRoundTrip(t *testing.T) {
	off := false
	path := filepath.Join(t.TempDir(), FileName)
	c := Config{Slots: 2, UpdateChannel: "beta", AutoUpdate: &off, UpdateBaseURL: "https://staging.example/runner", UpdateIntervalHours: 24, DevUpdateHosts: []string{"staging.example"}}
	if err := c.SaveTo(path); err != nil {
		t.Fatalf("SaveTo: %v", err)
	}
	got, err := LoadFrom(path)
	if err != nil {
		t.Fatalf("LoadFrom: %v", err)
	}
	if got.UpdateChannelOr() != "beta" || got.UpdatesEnabled() || got.UpdateInterval() != 24*time.Hour {
		t.Fatalf("the update settings did not round-trip: %+v", got)
	}
	if got.UpdateBaseURL != "https://staging.example/runner" || len(got.DevUpdateHosts) != 1 {
		t.Fatalf("the update host did not round-trip: %+v", got)
	}
}
