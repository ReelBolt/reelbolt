package config

import "testing"

// The values the production deployment hands out and the shipped runner is stamped with
// (the production runner readiness checks). These hold in every build; the release-only refusals
// are in prodpolicy_release_test.go.
func TestProdGatewayValues(t *testing.T) {
	accepted := []string{
		"wss://reelbolt.ai/runner/v1/connect",
		"wss://runner.reelbolt.ai/v1/connect",
	}
	for _, u := range accepted {
		if err := CheckGateway(u, nil); err != nil {
			t.Errorf("CheckGateway(%q) = %v, want acceptance", u, err)
		}
	}
	refused := []string{
		"ws://reelbolt.ai/runner/v1/connect",
		"ws://runner.reelbolt.ai/v1/connect",
		"wss://reelbolt.ai.evil.com/runner/v1/connect",
		"wss://evilreelbolt.ai/runner/v1/connect",
		"wss://evil.com/runner/v1/connect",
		"wss://reelbolt.ai@evil.com/runner/v1/connect",
	}
	for _, u := range refused {
		if err := CheckGateway(u, nil); err == nil {
			t.Errorf("CheckGateway(%q) accepted a gateway it must refuse", u)
		}
	}
}

func TestDefaultServiceOriginIsProd(t *testing.T) {
	if ServiceOrigin != "https://reelbolt.ai" {
		t.Skipf("ServiceOrigin stamped to %q", ServiceOrigin)
	}
	if got := Origin(); got != "https://reelbolt.ai" {
		t.Fatalf("Origin() = %q", got)
	}
	if got := CollectorEndpoint(); got != "https://reelbolt.ai/runner/telemetry/v1/reports" {
		t.Fatalf("CollectorEndpoint() = %q", got)
	}
}
