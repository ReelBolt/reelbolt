package main

import (
	"errors"
	"path/filepath"
	"testing"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
)

func TestHostOf(t *testing.T) {
	cases := map[string]string{
		"https://gw.example/v1/connect": "gw.example",
		"ws://127.0.0.1:8090":           "127.0.0.1:8090",
		"gw.example":                    "gw.example",
		"gw.example:9000":               "gw.example:9000",
		"gw.example/path":               "",
	}
	for in, want := range cases {
		if got := hostOf(in); got != want {
			t.Errorf("hostOf(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestApplyDevGateway(t *testing.T) {
	var cfg config.Config
	if err := applyDevGateway(&cfg, "wss://gw.dev.internal/v1/connect"); err != nil {
		t.Fatalf("applyDevGateway: %v", err)
	}
	if !cfg.DevGateway || len(cfg.DevGateways) != 1 || cfg.DevGateways[0] != "gw.dev.internal" {
		t.Fatalf("cfg = %+v", cfg)
	}
	// Applying twice must not duplicate the entry.
	if err := applyDevGateway(&cfg, "gw.dev.internal"); err != nil {
		t.Fatal(err)
	}
	if len(cfg.DevGateways) != 1 {
		t.Errorf("DevGateways = %v, want one entry", cfg.DevGateways)
	}
	if err := applyDevGateway(&cfg, "not a url/with/path"); err == nil {
		t.Error("a value that is neither a URL nor a host must be refused")
	}
}

func TestExitFor(t *testing.T) {
	if got := exitFor(errNotPaired); got != exitNotPaired {
		t.Errorf("exitFor(errNotPaired) = %d, want %d", got, exitNotPaired)
	}
	if got := exitFor(errors.New("boom")); got != exitFailure {
		t.Errorf("exitFor(other) = %d, want %d", got, exitFailure)
	}
}

func TestLoadEnvUsesTheConfigDirectoryAndRejectsBadOverrides(t *testing.T) {
	dir := t.TempDir()
	t.Setenv("XDG_CONFIG_HOME", dir)
	t.Setenv("AppData", dir)
	t.Setenv("HOME", dir)

	e, err := loadEnv(nil)
	if err != nil {
		t.Fatalf("loadEnv: %v", err)
	}
	want := filepath.Join(dir, config.DirName, config.FileName)
	if e.cfgPath != want {
		t.Errorf("config path = %q, want %q", e.cfgPath, want)
	}
	if e.cfg.IsPaired() {
		t.Error("a fresh configuration must not look paired")
	}

	// An override that cannot be honoured fails the load rather than being
	// silently ignored.
	if _, err := loadEnv(func(c *config.Config) { c.Slots = config.MaxSlots + 1 }); err == nil {
		t.Error("loadEnv accepted an impossible slot count")
	}
}

func TestLoadEnvRefusesAGatewayOutsideTheAllowlist(t *testing.T) {
	dir := t.TempDir()
	t.Setenv("XDG_CONFIG_HOME", dir)
	t.Setenv("AppData", dir)
	t.Setenv("HOME", dir)
	if _, err := loadEnv(func(c *config.Config) { c.GatewayURL = "wss://evil.example/v1/connect" }); err == nil {
		t.Fatal("loadEnv accepted a gateway outside the allowlist")
	}
}

// TestCommandsDeclareTheirFlags pins the CLI surface: the flag constructors are
// what the commands parse, so a flag that disappears from one disappears for
// users — and the E13 harness scripts against these names.
func TestCommandsDeclareTheirFlags(t *testing.T) {
	run := &runFlags{}
	fs := newRunFlags(run)
	for _, name := range []string{
		"headless", "gateway", "dev-gateway", "slots", "runtime-image", "container-cli", "log-file",
	} {
		if fs.Lookup(name) == nil {
			t.Errorf("run lost its -%s flag", name)
		}
	}
	status := &statusFlags{}
	if fs := newStatusFlags(status); fs.Lookup("json") == nil {
		t.Error("status lost its -json flag")
	}
	doc := &doctorFlags{}
	if fs := newDoctorFlags(doc); fs.Lookup("json") == nil || fs.Lookup("slots") == nil {
		t.Error("doctor lost a flag")
	}
	pair := &pairFlags{}
	fs = newPairFlags(pair)
	for _, name := range []string{"api", "name", "dev-gateway", "no-browser", "gateway"} {
		if fs.Lookup(name) == nil {
			t.Errorf("pair lost its -%s flag", name)
		}
	}
	unpair := &unpairFlags{}
	if fs := newUnpairFlags(unpair); fs.Lookup("forget-key") == nil {
		t.Error("unpair lost its -forget-key flag")
	}
}

func TestUnpairClearsTheDeviceAndKeepsTheKeyByDefault(t *testing.T) {
	dir := t.TempDir()
	t.Setenv("XDG_CONFIG_HOME", dir)
	t.Setenv("AppData", dir)
	t.Setenv("HOME", dir)

	cfg := &config.Config{
		GatewayURL: "wss://runner.reelbolt.ai/v1/connect",
		DeviceID:   "11111111-2222-3333-4444-555555555555",
		Slots:      1,
	}
	cfg.ApplyDefaults()
	path, err := config.Path()
	if err != nil {
		t.Fatal(err)
	}
	if err := cfg.SaveTo(path); err != nil {
		t.Fatal(err)
	}
	if err := cmdUnpair(nil); err != nil {
		t.Fatalf("cmdUnpair: %v", err)
	}
	reloaded, err := config.LoadFrom(path)
	if err != nil {
		t.Fatal(err)
	}
	if reloaded.DeviceID != "" {
		t.Errorf("DeviceID = %q, want it cleared", reloaded.DeviceID)
	}
	if reloaded.GatewayURL == "" {
		t.Error("unpairing should leave the gateway, not blank a working configuration")
	}
}

func TestRunRefusesTheDesktopMode(t *testing.T) {
	// This binary has no UI; asking for one is a usage error rather than a silent
	// headless run.
	if err := cmdRun([]string{"-headless=false"}); err == nil {
		t.Fatal("run accepted -headless=false")
	}
}
