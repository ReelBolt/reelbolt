package main

import (
	"strings"
	"testing"
	"time"
)

func TestLoopRefusesWhenDisabled(t *testing.T) {
	t.Setenv("POOL_WAKER_ENABLED", "")
	err := run([]string{"loop", "-once"})
	if err == nil || !strings.Contains(err.Error(), "POOL_WAKER_ENABLED") {
		t.Fatalf("loop must refuse without the flag, got %v", err)
	}
}

func TestLoopUnknownProvider(t *testing.T) {
	t.Setenv("POOL_WAKER_ENABLED", "true")
	t.Setenv("RUNNER_GATEWAY_URL", "http://g")
	t.Setenv("RUNNER_INTERNAL_TOKEN", "x")
	t.Setenv("POOL_NODE_GATEWAY_URL", "wss://g/ws")
	t.Setenv("POOL_PROVIDER", "nope")
	if err := run([]string{"loop", "-once"}); err == nil || !strings.Contains(err.Error(), "not available") {
		t.Fatalf("got %v", err)
	}
}

func TestHetznerFactoryFailsClearlyWithoutLeaking(t *testing.T) {
	home := t.TempDir()
	env := func(m map[string]string) func(string) string {
		return func(k string) string { return m[k] }
	}
	if _, err := hetznerFactory(env(map[string]string{"HOME": home})); err == nil || !strings.Contains(err.Error(), "HCLOUD_TOKEN") {
		t.Fatalf("no token: %v", err)
	}
	const secret = "s3cr3t-token-value"
	_, err := hetznerFactory(env(map[string]string{"HOME": home, "HCLOUD_TOKEN": secret, "POOL_INSTANCE": "staging"}))
	if err == nil || !strings.Contains(err.Error(), "HCLOUD_SNAPSHOT_ID") || strings.Contains(err.Error(), secret) {
		t.Fatalf("no snapshot: %v", err)
	}
	if _, err := hetznerFactory(env(map[string]string{"HOME": home, "HCLOUD_TOKEN": secret, "HCLOUD_SNAPSHOT_ID": "123", "POOL_INSTANCE": "staging"})); err == nil || !strings.Contains(err.Error(), "HCLOUD_FIREWALL") {
		t.Fatalf("no firewall must be refused: %v", err)
	}
	p, err := hetznerFactory(env(map[string]string{"HOME": home, "HCLOUD_TOKEN": secret, "HCLOUD_SNAPSHOT_ID": "123", "HCLOUD_FIREWALL": "9", "POOL_INSTANCE": "staging"}))
	if err != nil || p.Name() != "hetzner" {
		t.Fatalf("valid config: %v", err)
	}
}

func TestLoopRequiresInstanceForHetzner(t *testing.T) {
	t.Setenv("POOL_WAKER_ENABLED", "true")
	t.Setenv("RUNNER_GATEWAY_URL", "http://g")
	t.Setenv("RUNNER_INTERNAL_TOKEN", "x")
	t.Setenv("POOL_NODE_GATEWAY_URL", "wss://g/ws")
	t.Setenv("POOL_PROVIDER", "hetzner")
	t.Setenv("POOL_INSTANCE", "")
	t.Setenv("HCLOUD_TOKEN", "tok-secret")
	t.Setenv("HCLOUD_SNAPSHOT_ID", "1")
	err := run([]string{"loop", "-once"})
	if err == nil || !strings.Contains(err.Error(), "POOL_INSTANCE") || strings.Contains(err.Error(), "tok-secret") {
		t.Fatalf("got %v", err)
	}
}

func TestServerTypesFromEnv(t *testing.T) {
	env := func(m map[string]string) func(string) string { return func(k string) string { return m[k] } }
	if got := strings.Join(serverTypesFromEnv(env(nil)), ","); got != "ccx33,ccx23,ccx13" {
		t.Fatalf("default list: %s", got)
	}
	if got := strings.Join(serverTypesFromEnv(env(map[string]string{"HCLOUD_SERVER_TYPES": " ccx23 , ccx13 ,", "HCLOUD_SERVER_TYPE": "cpx42"})), ","); got != "ccx23,ccx13" {
		t.Fatalf("the list wins: %s", got)
	}
	if got := strings.Join(serverTypesFromEnv(env(map[string]string{"HCLOUD_SERVER_TYPE": "cpx42"})), ","); got != "cpx42" {
		t.Fatalf("the single type still works: %s", got)
	}
}

func TestHetznerFactoryAcceptsLatest(t *testing.T) {
	home := t.TempDir()
	env := map[string]string{"HOME": home, "HCLOUD_TOKEN": "tok", "HCLOUD_SNAPSHOT_ID": "latest", "HCLOUD_FIREWALL": "9", "POOL_INSTANCE": "staging"}
	if p, err := hetznerFactory(func(k string) string { return env[k] }); err != nil || p.Name() != "hetzner" {
		t.Fatalf("latest: %v", err)
	}
}

func TestLoopConfigIdleAndHourEndDefaults(t *testing.T) {
	t.Setenv("POOL_IDLE_SECONDS", "")
	t.Setenv("POOL_HOUR_END_MARGIN_SECONDS", "")
	lc := loadLoopConfig()
	if lc.IdleWindow != 0 || lc.HourEndMargin != 5*time.Minute {
		t.Fatalf("defaults: idle override %s (want off), hour end margin %s (want 5m)", lc.IdleWindow, lc.HourEndMargin)
	}
	t.Setenv("POOL_IDLE_SECONDS", "600")
	t.Setenv("POOL_HOUR_END_MARGIN_SECONDS", "120")
	lc = loadLoopConfig()
	if lc.IdleWindow != 10*time.Minute || lc.HourEndMargin != 2*time.Minute {
		t.Fatalf("overrides: idle %s, margin %s", lc.IdleWindow, lc.HourEndMargin)
	}
}

func TestValidateLoopConfigAcceptsServerTypes(t *testing.T) {
	t.Setenv("POOL_INSTANCE", "staging")
	for _, types := range []string{"", "ccx23,ccx13"} {
		t.Setenv("HCLOUD_SERVER_TYPES", types)
		t.Setenv("HCLOUD_SERVER_TYPE", "")
		lc := loadLoopConfig()
		if err := validateLoopConfig(lc); err != nil {
			t.Fatalf("HCLOUD_SERVER_TYPES=%q: %v", types, err)
		}
		if len(lc.ServerTypes) == 0 {
			t.Fatalf("validation must not change the loaded list")
		}
	}
}

func TestHetznerFactoryNetworkNeedsSubnet(t *testing.T) {
	env := map[string]string{"HCLOUD_TOKEN": "t", "POOL_INSTANCE": "stg", "HCLOUD_SNAPSHOT_ID": "1", "HCLOUD_FIREWALL": "9", "HCLOUD_NETWORK": "55"}
	get := func(k string) string { return env[k] }
	if _, err := hetznerFactory(get); err == nil || !strings.Contains(err.Error(), "HCLOUD_NETWORK_SUBNET") {
		t.Fatalf("err %v", err)
	}
	env["HCLOUD_NETWORK_SUBNET"] = "8.8.8.0/24"
	if _, err := hetznerFactory(get); err == nil {
		t.Fatal("public subnet accepted")
	}
	env["HCLOUD_NETWORK_SUBNET"] = "10.0.2.0/24"
	if _, err := hetznerFactory(get); err != nil {
		t.Fatal(err)
	}
}

func loopEnv(t *testing.T, gatewayURL string) {
	t.Helper()
	t.Setenv("POOL_WAKER_ENABLED", "true")
	t.Setenv("RUNNER_GATEWAY_URL", gatewayURL)
	t.Setenv("RUNNER_INTERNAL_TOKEN", "fake-internal-token")
	t.Setenv("POOL_NODE_GATEWAY_URL", "wss://g/ws")
	t.Setenv("POOL_INSTANCE", "staging")
	t.Setenv("HCLOUD_TOKEN", "")
	t.Setenv("HCLOUD_TOKEN_FILE", "")
	t.Setenv("REELBOLT_ROLE", "")
	t.Setenv("POOL_WAKER_ALLOW_ON_MEDIA_HOST", "")
	t.Setenv("RUNNER_GATEWAY_ALLOW_PLAIN_HTTP", "")
	t.Setenv("RUNNER_GATEWAY_CA_FILE", "")
	t.Setenv("RUNNER_GATEWAY_CLIENT_CERT_FILE", "")
	t.Setenv("RUNNER_GATEWAY_CLIENT_KEY_FILE", "")
}

func TestLoopRefusesProviderTokenOnMediaHost(t *testing.T) {
	loopEnv(t, "http://runner-gateway:8090")
	t.Setenv("POOL_PROVIDER", "hetzner")
	t.Setenv("REELBOLT_ROLE", "media")
	// A path that does not exist: the guard must refuse before anything tries to read the token.
	t.Setenv("HCLOUD_TOKEN_FILE", "/nonexistent/fake-hcloud-token")
	err := run([]string{"loop", "-once"})
	if err == nil || !strings.Contains(err.Error(), "REELBOLT_ROLE=media") || !strings.Contains(err.Error(), "POOL_WAKER_ALLOW_ON_MEDIA_HOST") {
		t.Fatalf("the waker must refuse a provider token on the media host, got %v", err)
	}
}

func TestRefuseOnMediaHost(t *testing.T) {
	env := func(m map[string]string) func(string) string {
		return func(k string) string { return m[k] }
	}
	cases := []struct {
		name     string
		env      map[string]string
		provider string
		refused  bool
	}{
		{"control host", map[string]string{"REELBOLT_ROLE": "control", "HCLOUD_TOKEN_FILE": "/run/secrets/hcloud_token"}, "hetzner", false},
		{"no role set", map[string]string{"HCLOUD_TOKEN_FILE": "/run/secrets/hcloud_token"}, "hetzner", false},
		{"media with token file", map[string]string{"REELBOLT_ROLE": "media", "HCLOUD_TOKEN_FILE": "/run/secrets/hcloud_token"}, "hetzner", true},
		{"media, role in capitals", map[string]string{"REELBOLT_ROLE": " Media ", "HCLOUD_TOKEN": "fake-token"}, "hetzner", true},
		{"media with hetzner and the home token file", map[string]string{"REELBOLT_ROLE": "media"}, "hetzner", true},
		{"media with a token and the fake provider", map[string]string{"REELBOLT_ROLE": "media", "HCLOUD_TOKEN": "fake-token"}, "fake", true},
		{"media, fake provider, no token", map[string]string{"REELBOLT_ROLE": "media"}, "fake", false},
		{"media with the override", map[string]string{"REELBOLT_ROLE": "media", "HCLOUD_TOKEN_FILE": "/run/secrets/hcloud_token", "POOL_WAKER_ALLOW_ON_MEDIA_HOST": "true"}, "hetzner", false},
	}
	for _, c := range cases {
		err := refuseOnMediaHost(env(c.env), c.provider)
		if (err != nil) != c.refused {
			t.Errorf("%s: refused=%t, got %v", c.name, c.refused, err)
		}
		if err != nil && strings.Contains(err.Error(), "fake-token") {
			t.Errorf("%s: the error quotes the token", c.name)
		}
	}
}

func TestLoopRefusesPlainGatewayOnAnotherHost(t *testing.T) {
	loopEnv(t, "http://10.0.0.3:8090")
	t.Setenv("POOL_PROVIDER", "fake")
	err := run([]string{"loop", "-once", "-dry-run"})
	if err == nil || !strings.Contains(err.Error(), "RUNNER_GATEWAY_ALLOW_PLAIN_HTTP") {
		t.Fatalf("a plain http gateway on another host must be refused, got %v", err)
	}
}

func TestLoopRefusesHalfATLSClientIdentity(t *testing.T) {
	loopEnv(t, "https://10.0.0.3:8443")
	t.Setenv("POOL_PROVIDER", "fake")
	t.Setenv("RUNNER_GATEWAY_CLIENT_CERT_FILE", "/run/gateway-tls/client.pem")
	err := run([]string{"loop", "-once", "-dry-run"})
	if err == nil || !strings.Contains(err.Error(), "together") {
		t.Fatalf("a certificate without its key must be refused, got %v", err)
	}
}

func TestScalerRefusesPlainGatewayOnAnotherHost(t *testing.T) {
	loopEnv(t, "http://10.0.0.3:8090")
	err := run([]string{"-no-metrics"})
	if err == nil || !strings.Contains(err.Error(), "RUNNER_GATEWAY_ALLOW_PLAIN_HTTP") {
		t.Fatalf("a plain http gateway on another host must be refused, got %v", err)
	}
}
