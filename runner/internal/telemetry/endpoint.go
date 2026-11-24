package telemetry

import (
	"net/url"
	"os"
	"strings"
)

// CollectorEndpoint is the crash collector's address, stamped into the binary
// by the release pipeline exactly as update's release keys are:
//
//	go build -tags release -ldflags '-X .../telemetry.CollectorEndpoint=https://...'
//
// The collector is the runner gateway's public POST /telemetry/v1/reports
// (sandbox/cmd/runner-gateway/telemetry.go), which logs each validated report
// and stores nothing. "reelbolt-manifest ldflags" prints the stamp, defaulting
// to ProductionCollectorEndpoint and overridden by REELBOLT_TELEMETRY_ENDPOINT_STAMP
// ("none" stamps nothing). A plain "go build" from this tree stamps nothing, so
// it stays empty there: telemetry can be switched on, is recorded as on, and
// sends nothing - which the Settings screen says in words rather than leaving
// the user to wonder why the counter stays at zero.
var CollectorEndpoint = ""

// ProductionCollectorEndpoint is the address a release build is stamped with
// unless the build says otherwise: the runner gateway's collector, published
// under the one ReelBolt host (config.ServiceOrigin) at /runner/telemetry/v1/reports.
// It is spelled out rather than derived because this package cannot import config.
const ProductionCollectorEndpoint = "https://reelbolt.ai/runner/telemetry/v1/reports"

// DevEndpointEnv names a collector a NON-RELEASE build may post to.
//
// It exists because a crash path cannot be developed against a collector that
// does not exist yet, and a local fake is how this WP was tested. The value is
// accepted only when it is loopback (see devEndpoint): a development build can
// point at a fake on this machine and nowhere else, so this is not a way to
// redirect a build's telemetry at a host of someone's choosing. A release build
// (-tags release) ignores the variable entirely.
const DevEndpointEnv = "REELBOLT_TELEMETRY_ENDPOINT"

// Endpoint returns the collector this build may send to, or "".
//
// It fails closed at every step: an unparseable stamped address, one with no
// host, or one that is not http(s) yields no endpoint at all, because a report
// posted to a URL this program cannot reason about is worse than no report.
func Endpoint() string {
	if raw := strings.TrimSpace(CollectorEndpoint); raw != "" {
		if u, err := url.Parse(raw); err == nil && u.Host != "" && isHTTP(u.Scheme) {
			return raw
		}
		return ""
	}
	if !devEndpointAllowed {
		return ""
	}
	return devEndpoint(os.Getenv(DevEndpointEnv))
}

// devEndpoint accepts a loopback http(s) URL and nothing else.
func devEndpoint(raw string) string {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return ""
	}
	u, err := url.Parse(raw)
	if err != nil || u.Host == "" || !isHTTP(u.Scheme) {
		return ""
	}
	switch strings.ToLower(u.Hostname()) {
	case "localhost", "127.0.0.1", "::1":
		return raw
	}
	return ""
}

func isHTTP(scheme string) bool { return scheme == "http" || scheme == "https" }
