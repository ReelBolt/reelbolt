package main

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/telemetrywire"
)

const telemetryCrash = `{"schema":"reelbolt.telemetry/1","kind":"crash","id":"0123456789abcdef0123456789abcdef",` +
	`"at":"2026-10-11T09:00:00Z","runnerVersion":"1.4.2","os":"linux","arch":"amd64","go":"go1.27.0",` +
	`"stage":"run","panicType":"runtime.boundsError","frames":[{"func":"main.main","file":"main.go","line":12}]}`

// postTelemetry sends one report and returns the status code. The body is closed
// here, so callers never hold the response.
func postTelemetry(t *testing.T, tg *testGateway, body, contentType, realIP string) int {
	t.Helper()
	req, err := http.NewRequestWithContext(context.Background(), http.MethodPost, tg.server.URL+TelemetryPath, strings.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	if contentType != "" {
		req.Header.Set("Content-Type", contentType)
	}
	if realIP != "" {
		req.Header.Set("X-Real-IP", realIP)
	}
	resp, err := tg.server.Client().Do(req)
	if err != nil {
		t.Fatal(err)
	}
	_ = resp.Body.Close()
	return resp.StatusCode
}

func TestAValidReportIsAcceptedAndCountedByKind(t *testing.T) {
	tg := newTestGateway(t, nil)
	if status := postTelemetry(t, tg, telemetryCrash, "application/json; charset=utf-8", ""); status != http.StatusAccepted {
		t.Fatalf("status = %d, want 202", status)
	}
	if v := tg.metricValue(t, `reelbolt_runner_gateway_telemetry_reports_total{kind="crash"}`); v != 1 {
		t.Fatalf("crash reports counted = %v, want 1", v)
	}
}

func TestReportsOutsideTheContractAreRefused(t *testing.T) {
	tg := newTestGateway(t, nil)
	cases := []struct {
		name, body, contentType string
		want                    int
	}{
		{"not JSON", "stage=run", "application/x-www-form-urlencoded", http.StatusUnsupportedMediaType},
		{"no media type", telemetryCrash, "", http.StatusUnsupportedMediaType},
		{"an extra field", strings.Replace(telemetryCrash, `"stage"`, `"message":"/home/sam","stage"`, 1), "application/json", http.StatusBadRequest},
		{"too large", `{"schema":"` + strings.Repeat("a", telemetrywire.MaxBodyBytes) + `"}`, "application/json", http.StatusRequestEntityTooLarge},
	}
	for i, tc := range cases {
		// A distinct address per case, so the rate limit is not what answers.
		ip := "203.0.113." + string(rune('1'+i))
		if status := postTelemetry(t, tg, tc.body, tc.contentType, ip); status != tc.want {
			t.Errorf("%s: status = %d, want %d", tc.name, status, tc.want)
		}
	}
	if v := tg.metricValue(t, `reelbolt_runner_gateway_telemetry_reports_total{kind="crash"}`); v != 0 {
		t.Fatalf("a refused report was counted as accepted: %v", v)
	}
}

func TestTheIngestIsRateLimitedPerSourceAddress(t *testing.T) {
	tg := newTestGateway(t, nil)
	// The test server's peer is loopback, which is what the proxy is, so the
	// forwarded address decides the bucket.
	for i := 0; i < telemetryBurst; i++ {
		if status := postTelemetry(t, tg, telemetryCrash, "application/json", "198.51.100.7"); status != http.StatusAccepted {
			t.Fatalf("report %d: status = %d", i, status)
		}
	}
	if status := postTelemetry(t, tg, telemetryCrash, "application/json", "198.51.100.7"); status != http.StatusTooManyRequests {
		t.Fatalf("report past the burst: status = %d, want 429", status)
	}
	// Another address has its own budget.
	if status := postTelemetry(t, tg, telemetryCrash, "application/json", "198.51.100.8"); status != http.StatusAccepted {
		t.Fatalf("another address: status = %d, want 202", status)
	}
}

func TestTheLimiterRefillsAndForgetsIdleAddresses(t *testing.T) {
	now := time.Date(2026, 10, 11, 9, 0, 0, 0, time.UTC)
	l := newTelemetryLimiter(func() time.Time { return now })
	for i := 0; i < telemetryBurst; i++ {
		if !l.allow("a") {
			t.Fatalf("report %d refused inside the burst", i)
		}
	}
	if l.allow("a") {
		t.Fatal("the burst was exceeded")
	}
	now = now.Add(telemetryRefillEach)
	if !l.allow("a") {
		t.Fatal("one refill period did not return a token")
	}
	now = now.Add(telemetryIdle + 2*time.Minute)
	l.allow("b")
	l.mu.Lock()
	_, kept := l.buckets["a"]
	l.mu.Unlock()
	if kept {
		t.Fatal("an idle address was kept past its window")
	}
}

// A client that reaches the gateway from a public address cannot choose its
// own bucket by sending the proxy's header.
func TestTheForwardedAddressIsOnlyTrustedFromAProxy(t *testing.T) {
	r := httptest.NewRequestWithContext(context.Background(), http.MethodPost, TelemetryPath, nil)
	r.RemoteAddr = "203.0.113.9:4444"
	r.Header.Set("X-Real-IP", "198.51.100.1")
	if got := telemetrySource(r); got != "203.0.113.9" {
		t.Fatalf("source = %q, want the public peer", got)
	}
	r.RemoteAddr = "10.0.3.2:4444"
	if got := telemetrySource(r); got != "198.51.100.1" {
		t.Fatalf("source = %q, want the forwarded address", got)
	}
}
