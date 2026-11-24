//go:build release

package config

import "testing"

// A release build accepts the production gateways and refuses every development gateway,
// including loopback, even when the caller declares it.
func TestReleaseRefusesDevGateways(t *testing.T) {
	for _, u := range []string{"ws://127.0.0.1:8090/v1/connect", "ws://localhost:8090/v1/connect"} {
		if err := CheckGateway(u, nil); err == nil {
			t.Errorf("release build accepted %q", u)
		}
	}
	if err := CheckGateway("wss://gw.dev.internal/v1/connect", []string{"gw.dev.internal"}); err == nil {
		t.Error("release build accepted a declared development gateway")
	}
	for _, u := range []string{"wss://reelbolt.ai/runner/v1/connect", "wss://runner.reelbolt.ai/v1/connect"} {
		if err := CheckGateway(u, nil); err != nil {
			t.Errorf("release build refused %q: %v", u, err)
		}
	}
}
