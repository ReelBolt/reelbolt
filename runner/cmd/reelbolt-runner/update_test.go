package main

import (
	"errors"
	"testing"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/update"
)

// isolateConfig points config.Path() at a temporary directory, so a test never
// reads or writes the developer own runner.json.
func isolateConfig(t *testing.T) {
	t.Helper()
	dir := t.TempDir()
	t.Setenv("XDG_CONFIG_HOME", dir)
	t.Setenv("AppData", dir)
	t.Setenv("HOME", dir)
}

func TestUpdateFlagsParse(t *testing.T) {
	var o updateFlags
	fs := newUpdateFlags(&o)
	if err := fs.Parse([]string{"--check", "--channel", "beta", "--json"}); err != nil {
		t.Fatalf("Parse: %v", err)
	}
	if !o.check || o.channel != "beta" || !o.asJSON {
		t.Fatalf("flags were not bound: %+v", o)
	}
}

func TestUpdateRefusesContradictoryFlags(t *testing.T) {
	isolateConfig(t)
	if err := cmdUpdate([]string{"--check", "--apply"}); err == nil {
		t.Fatal("--check --apply was accepted")
	}
}

func TestUpdateRefusesToRunWithoutAReleaseKey(t *testing.T) {
	// This is the honest state of the tree: the repository holds no release
	// key, so the command refuses BEFORE it contacts the update host rather
	// than fetching a manifest it cannot verify. The negative control is that
	// pointing it at an unreachable host still fails on the key, not on the
	// network.
	isolateConfig(t)
	t.Setenv(update.DevTrustKeyEnv, "")
	err := cmdUpdate([]string{"--check", "--update-base-url", "https://127.0.0.1:1/runner"})
	if !errors.Is(err, update.ErrNoTrustedKey) {
		t.Fatalf("cmdUpdate = %v, want ErrNoTrustedKey", err)
	}
}

func TestUpdateRejectsAnUnknownChannel(t *testing.T) {
	isolateConfig(t)
	if err := cmdUpdate([]string{"--check", "--channel", "nightly"}); err == nil {
		t.Fatal("an unknown channel was accepted")
	}
}

func TestApplyDevUpdateHost(t *testing.T) {
	var cfg config.Config
	if err := applyDevUpdateHost(&cfg, "http://127.0.0.1:8099/runner"); err != nil {
		t.Fatalf("applyDevUpdateHost: %v", err)
	}
	if len(cfg.DevUpdateHosts) != 1 || cfg.DevUpdateHosts[0] != "127.0.0.1:8099" {
		t.Fatalf("DevUpdateHosts = %v", cfg.DevUpdateHosts)
	}
	// The same host twice is one entry, not two.
	if err := applyDevUpdateHost(&cfg, "http://127.0.0.1:8099/runner"); err != nil {
		t.Fatal(err)
	}
	if len(cfg.DevUpdateHosts) != 1 {
		t.Fatalf("DevUpdateHosts = %v, want one entry", cfg.DevUpdateHosts)
	}
	if err := applyDevUpdateHost(&cfg, "not a url/with/a/slash"); err == nil {
		t.Fatal("a path was accepted as an update host")
	}
}

func TestStatusNamesTheTrustRoot(t *testing.T) {
	// A user asking why their runner is not updating gets the answer from
	// status: no release key is compiled in, so nothing can be verified.
	isolateConfig(t)
	t.Setenv(update.DevTrustKeyEnv, "")
	if got := trustedKeyNames(); got == "" {
		t.Fatal("trustedKeyNames returned nothing")
	}
	if onOff(true) != "on" || onOff(false) != "off" {
		t.Fatal("onOff is not an on/off renderer")
	}
}
