package main

import (
	"encoding/json"
	"errors"
	"io"
	"log"
	"mime"
	"net"
	"net/http"
	"sync"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/telemetrywire"
)

// The public crash-report collector: POST /telemetry/v1/reports.
//
// A desktop runner whose user turned crash reporting ON posts one small JSON
// document per crash or per job it could not start (runner/internal/telemetry).
// This is where it lands, and it is the least a collector can be:
//
//   - PUBLIC AND UNAUTHENTICATED, because the runner deliberately sends nothing
//     that identifies the machine, the device or the workspace, and so has
//     nothing to authenticate with. It is therefore treated as hostile input:
//     the body is capped, the media type is checked, the document is decoded
//     strictly (telemetrywire.Decode refuses unknown fields and anything outside
//     the runner's own allowlist), and each source address is rate limited.
//   - NOTHING IS STORED. An accepted report becomes ONE structured log line,
//     "telemetry_report {json}", and a counter by kind. The line carries the
//     validated document only: not the source address, not the user agent, not
//     a header. Aggregation is the log pipeline's job.
//   - 202 ACCEPTED, with no body worth reading: the runner treats any 2xx as
//     sent and never retries, so there is nothing to negotiate.
//
// nginx (runner-server.conf.template) proxies exactly this path on the runner
// hostname, with its own body cap and rate limit in front of these.

// TelemetryPath is the collector's path. The runner's release builds are stamped
// with https://runner.<domain> + this path (runner/internal/telemetry).
const TelemetryPath = "/telemetry/v1/reports"

// Rate limits, per source address. A runner sends a handful of reports in a bad
// hour; these bound what a flood can cost to one log line per report.
const (
	telemetryBurst      = 10
	telemetryRefillEach = 6 * time.Second // ten a minute, sustained
	// telemetryMaxSources bounds the limiter's memory. When it is full of
	// addresses that are all still within their window, a NEW address is refused
	// rather than an old one forgotten, which would reset its budget.
	telemetryMaxSources = 50_000
	// telemetryIdle is how long an address's bucket is kept after it last
	// posted: past this it is full again anyway, so it is dropped.
	telemetryIdle = 10 * time.Minute
)

// telemetryLimiter is a token bucket per source address.
type telemetryLimiter struct {
	mu      sync.Mutex
	buckets map[string]*telemetryBucket
	now     func() time.Time
	swept   time.Time
}

type telemetryBucket struct {
	tokens float64
	last   time.Time
}

func newTelemetryLimiter(now func() time.Time) *telemetryLimiter {
	return &telemetryLimiter{buckets: map[string]*telemetryBucket{}, now: now}
}

// allow takes one token from the address's bucket.
func (l *telemetryLimiter) allow(addr string) bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := l.now()
	if now.Sub(l.swept) > time.Minute {
		for k, b := range l.buckets {
			if now.Sub(b.last) > telemetryIdle {
				delete(l.buckets, k)
			}
		}
		l.swept = now
	}
	b, ok := l.buckets[addr]
	if !ok {
		if len(l.buckets) >= telemetryMaxSources {
			return false
		}
		b = &telemetryBucket{tokens: telemetryBurst, last: now}
		l.buckets[addr] = b
	}
	b.tokens += now.Sub(b.last).Seconds() / telemetryRefillEach.Seconds()
	if b.tokens > telemetryBurst {
		b.tokens = telemetryBurst
	}
	b.last = now
	if b.tokens < 1 {
		return false
	}
	b.tokens--
	return true
}

// telemetrySource is the address a report is rate limited by. Behind nginx the
// socket's peer is the proxy, so the proxy's X-Real-IP is used - but only when
// the peer is itself a private or loopback address, which is what the proxy is.
// A client that reaches the gateway directly from a public address cannot pick
// its own bucket by sending the header.
func telemetrySource(r *http.Request) string {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		host = r.RemoteAddr
	}
	peer := net.ParseIP(host)
	if peer != nil && (peer.IsLoopback() || peer.IsPrivate()) {
		if real := net.ParseIP(r.Header.Get("X-Real-IP")); real != nil {
			return real.String()
		}
	}
	return host
}

// handleTelemetry ingests one report.
func (g *gateway) handleTelemetry(w http.ResponseWriter, r *http.Request) {
	if !g.telemetry.allow(telemetrySource(r)) {
		g.metrics.telemetryRejected.WithLabelValues("rate_limited").Inc()
		w.Header().Set("Retry-After", "60")
		http.Error(w, "too many reports", http.StatusTooManyRequests)
		return
	}
	media, _, err := mime.ParseMediaType(r.Header.Get("Content-Type"))
	if err != nil || media != "application/json" {
		g.metrics.telemetryRejected.WithLabelValues("media_type").Inc()
		http.Error(w, "send application/json", http.StatusUnsupportedMediaType)
		return
	}
	body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, telemetrywire.MaxBodyBytes))
	if err != nil {
		var tooLarge *http.MaxBytesError
		if errors.As(err, &tooLarge) {
			g.metrics.telemetryRejected.WithLabelValues("too_large").Inc()
			http.Error(w, "report too large", http.StatusRequestEntityTooLarge)
			return
		}
		g.metrics.telemetryRejected.WithLabelValues("invalid").Inc()
		http.Error(w, "unreadable body", http.StatusBadRequest)
		return
	}
	report, err := telemetrywire.Decode(body)
	if err != nil {
		// The refusal names no field: a public endpoint that explains which
		// check failed is a tool for finding the one that does not.
		g.metrics.telemetryRejected.WithLabelValues("invalid").Inc()
		http.Error(w, "not a reelbolt.telemetry/1 report", http.StatusBadRequest)
		return
	}
	// Re-marshalled from the validated struct, never echoed from the body, so
	// the line can only ever hold allowlisted fields.
	line, err := json.Marshal(report)
	if err != nil {
		g.metrics.telemetryRejected.WithLabelValues("invalid").Inc()
		http.Error(w, "not a reelbolt.telemetry/1 report", http.StatusBadRequest)
		return
	}
	log.Printf("telemetry_report %s", line)
	g.metrics.telemetryReports.WithLabelValues(report.Kind).Inc()
	w.WriteHeader(http.StatusAccepted)
}
