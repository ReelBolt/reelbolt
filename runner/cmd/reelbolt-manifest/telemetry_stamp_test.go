package main

import (
	"strings"
	"testing"

	"github.com/vecchiotom/reelbolt/runner/internal/telemetry"
)

func TestLdflagsStampsTheProductionCollectorByDefault(t *testing.T) {
	t.Setenv(TelemetryStampEnv, "")
	line := mustRun(t, "ldflags", "-version", "1.4.2", "-no-release-key")
	if !strings.Contains(line, telemetryVar+"="+telemetry.ProductionCollectorEndpoint) {
		t.Fatalf("the default build line does not stamp the production collector:\n%s", line)
	}
}

func TestTheEnvironmentOverridesOrTurnsOffTheCollectorStamp(t *testing.T) {
	t.Setenv(TelemetryStampEnv, "https://runner.reelbolt.ai/telemetry/v1/reports")
	line := mustRun(t, "ldflags", "-version", "1.4.2", "-no-release-key")
	if !strings.Contains(line, telemetryVar+"=https://runner.reelbolt.ai/telemetry/v1/reports") {
		t.Fatalf("the environment's collector was not stamped:\n%s", line)
	}
	t.Setenv(TelemetryStampEnv, "none")
	if line := mustRun(t, "ldflags", "-version", "1.4.2", "-no-release-key"); strings.Contains(line, telemetryVar) {
		t.Fatalf("\"none\" still stamped a collector:\n%s", line)
	}
	// The flag wins over the environment.
	line = mustRun(t, "ldflags", "-version", "1.4.2", "-no-release-key", "-telemetry-endpoint", "http://127.0.0.1:9/r")
	if !strings.Contains(line, telemetryVar+"=http://127.0.0.1:9/r") {
		t.Fatalf("the flag did not win:\n%s", line)
	}
}

func TestACollectorStampMustBeHTTPSAndPlain(t *testing.T) {
	for _, bad := range []string{
		"http://collector.example.com/r", // clear text off this machine
		"ftp://collector.example.com/r",
		"collector.example.com/r",
		"https://collector.example.com/r?token=x",
		"https://user:pw@collector.example.com/r",
		"https://collector.example.com/r a",
	} {
		if _, err := runCmd(t, "ldflags", "-version", "1.4.2", "-no-release-key", "-telemetry-endpoint", bad); err == nil {
			t.Errorf("%q was accepted as a collector", bad)
		}
	}
}
