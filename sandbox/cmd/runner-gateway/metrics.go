package main

import (
	"net/http"
	"sort"
	"strconv"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/prometheus/client_golang/prometheus"
	"github.com/prometheus/client_golang/prometheus/collectors"
	"github.com/prometheus/client_golang/prometheus/promhttp"
)

// gatewayMetrics is the gateway's Prometheus surface.
//
// The live-runner gauge uses the name D5c's scaling signal already specifies,
// reelbolt_runner_connected{kind}, because this is the number it means, and D5b
// publishes the queue-depth and free-slot gauges it specifies too: the queue
// lives here, and D5c's scaling endpoint reads these numbers rather than
// re-deriving them. Everything else is prefixed reelbolt_runner_gateway_ so the
// sets cannot collide.
type gatewayMetrics struct {
	registry *prometheus.Registry
	handler  http.Handler

	connected          *prometheus.GaugeVec
	handshakes         *prometheus.CounterVec
	messages           *prometheus.CounterVec
	closes             *prometheus.CounterVec
	unexpected         *prometheus.CounterVec
	handshakeTimeouts  prometheus.Counter
	heartbeatTimeouts  prometheus.Counter
	nonceReplays       prometheus.Counter
	nonceSaturated     prometheus.Counter
	revocationRechecks *prometheus.CounterVec
	touchFailures      prometheus.Counter

	// The job queue and its leases (D5b).
	queueDepth    *prometheus.GaugeVec
	freeSlots     *prometheus.GaugeVec
	leases        *prometheus.CounterVec
	jobsCreated   prometheus.Counter
	claimFailures prometheus.Counter
	opResponses   prometheus.Counter
	// sessionOps counts one engine-facing session op by result (D5c). Every other
	// D5c metric is a registration D5a/D5b already made and is READ here:
	// reelbolt_runner_connected, reelbolt_runner_queue_depth and
	// reelbolt_runner_free_slots. Re-registering any of those panics.
	sessionOpResults          *prometheus.CounterVec
	notifications             prometheus.Counter
	notifyReconnects          prometheus.Counter
	executionSweepUnavailable prometheus.Counter
	revocationEvents          *prometheus.CounterVec
	revocationEventReconnects prometheus.Counter

	// The platform kill switches (F11).
	platformSwitchesUnreadable prometheus.Counter
	platformPaused             *prometheus.GaugeVec
	claimDeferred              *prometheus.CounterVec

	// displayLookups counts the best-effort assign.display reads by result.
	displayLookups *prometheus.CounterVec
	// The public crash-report ingest (telemetry.go): accepted reports by kind,
	// and refused ones by reason.
	telemetryReports  *prometheus.CounterVec
	telemetryRejected *prometheus.CounterVec

	// The local-LLM relay (llm_relay.go).
	llmRequests *prometheus.CounterVec
	llmBytes    prometheus.Counter
	llmInflight prometheus.Gauge
}

// Label values for reelbolt_runner_gateway_platform_paused. The gauge has one
// series per switch rather than one per setting key, because that is the
// question an operator asks: is compute being refused, and is that because the
// platform paused it or because this gateway cannot read the table at all.
const (
	switchGaugeCompute  = "compute"
	switchGaugeSettings = "settings"
)

func newGatewayMetrics() *gatewayMetrics {
	registry := prometheus.NewRegistry()
	m := &gatewayMetrics{
		registry: registry,
		connected: prometheus.NewGaugeVec(prometheus.GaugeOpts{
			Name: "reelbolt_runner_connected",
			Help: "Runner WebSocket connections currently held by this gateway instance.",
		}, []string{"kind"}),
		handshakes: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_handshakes_total",
			Help: "Hello handshakes by result: ok or the reason the connection was refused.",
		}, []string{"result"}),
		messages: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_messages_total",
			Help: "Protocol messages by type and direction (in: runner to gateway, out: gateway to runner).",
		}, []string{"type", "direction"}),
		closes: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_connection_closes_total",
			Help: "Connections closed by the gateway, by WebSocket close code.",
		}, []string{"code"}),
		unexpected: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_unexpected_messages_total",
			Help: "Messages a runner sent that this gateway does not act on today: unknown types, and job-scoped messages whose jobs D5b owns.",
		}, []string{"type"}),
		handshakeTimeouts: prometheus.NewCounter(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_handshake_timeouts_total",
			Help: "Connections closed with 4400 because no hello arrived in time.",
		}),
		heartbeatTimeouts: prometheus.NewCounter(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_heartbeat_timeouts_total",
			Help: "Connections closed with 4408 because nothing was heard for the dead window.",
		}),
		nonceReplays: prometheus.NewCounter(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_nonce_replays_total",
			Help: "Handshakes refused because a (deviceId, nonce) pair was replayed.",
		}),
		nonceSaturated: prometheus.NewCounter(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_nonce_cache_saturated_total",
			Help: "Handshakes refused with 4429 because the replay cache is full of live entries.",
		}),
		revocationRechecks: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_revocation_rechecks_total",
			Help: "Revocation sweeps by result: ok or error.",
		}, []string{"result"}),
		touchFailures: prometheus.NewCounter(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_last_seen_write_failures_total",
			Help: "Failures writing runner_devices.last_seen_at. Bookkeeping only; no connection is closed for one.",
		}),
		queueDepth: prometheus.NewGaugeVec(prometheus.GaugeOpts{
			Name: "reelbolt_runner_queue_depth",
			Help: "Queued runner jobs by target and job type, read from runner_jobs on each reaper pass.",
		}, []string{"target", "job_type"}),
		freeSlots: prometheus.NewGaugeVec(prometheus.GaugeOpts{
			Name: "reelbolt_runner_free_slots",
			Help: "Free job slots advertised by the runners connected to this gateway instance, by target.",
		}, []string{"target"}),
		leases: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_leases_total",
			Help: "Lease transitions by result: assigned, accepted, rejected, renewed, resumed, fenced, reaped_requeued, reaped_failed, reaped_expired, succeeded, failed, cancelled.",
		}, []string{"result"}),
		jobsCreated: prometheus.NewCounter(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_jobs_created_total",
			Help: "Jobs created through the internal API. A repeat of an existing idempotency key is not counted.",
		}),
		claimFailures: prometheus.NewCounter(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_claim_failures_total",
			Help: "Failures claiming work for a runner (the queue was unreachable or a row could not be updated).",
		}),
		opResponses: prometheus.NewCounter(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_op_responses_total",
			Help: "Op results accepted past the lease fence. D5c routes them on to the engine.",
		}),
		sessionOpResults: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_session_ops_total",
			Help: "Engine-facing session ops by result: request, ok, op_error, timeout, " +
				"cancelled, session_lost, op_in_flight, proxy_forwarded, proxy_failed, " +
				"caller_gone, abandoned.",
		}, []string{"result"}),
		notifications: prometheus.NewCounter(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_job_notifications_total",
			Help: "runner_jobs_new notifications received from Postgres.",
		}),
		notifyReconnects: prometheus.NewCounter(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_job_listener_reconnects_total",
			Help: "Times the LISTEN connection to runner_jobs_new was re-established.",
		}),
		executionSweepUnavailable: prometheus.NewCounter(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_execution_sweep_unavailable_total",
			Help: "Reaper passes that could not read workflow_executions because this database has no such table.",
		}),
		revocationEvents: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_revocation_events_total",
			Help: "RunnerDeviceRevoked events by outcome: received, malformed, dropped.",
		}, []string{"result"}),
		revocationEventReconnects: prometheus.NewCounter(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_revocation_consumer_reconnects_total",
			Help: "Times the revocation consumer reconnected to the broker.",
		}),
		// The one metric here without the reelbolt_runner_gateway_ prefix, and
		// deliberately: it names a platform-wide condition rather than something
		// this instance did, and it is the alert an operator is expected to write.
		// F11 owns the name.
		platformSwitchesUnreadable: prometheus.NewCounter(prometheus.CounterOpts{
			Name: "reelbolt_platform_switches_unreadable_total",
			Help: "Reads of platform_settings that failed. Every one of them is treated as platform compute being paused, so a rate above zero means this gateway is refusing to assign work because it cannot read the kill switches.",
		}),
		platformPaused: prometheus.NewGaugeVec(prometheus.GaugeOpts{
			Name: "reelbolt_runner_gateway_platform_paused",
			Help: "The kill-switch state this gateway instance last read, 1 for engaged. switch=compute is platform compute, where a failed read reports 1; switch=settings is 1 when the last read of platform_settings failed, so compute is being treated as paused rather than actually being paused.",
		}, []string{"switch"}),
		claimDeferred: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_claims_deferred_total",
			Help: "Claim rounds that deliberately assigned nothing, by reason (compute_paused, switches_unreadable). The jobs are untouched and stay Queued; this is a pause, not a loss.",
		}, []string{"reason"}),
		displayLookups: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_display_lookups_total",
			Help: "assign.display lookups by result: ok, missing (no matching row in the job's organization), error (including a timeout). A failed lookup only omits the names; the assignment is sent regardless.",
		}, []string{"result"}),
		telemetryReports: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_telemetry_reports_total",
			Help: "Opt-in runner crash reports accepted at /telemetry/v1/reports, by kind (crash, job_failure). Each is one telemetry_report log line; nothing is stored.",
		}, []string{"kind"}),
		telemetryRejected: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_telemetry_rejected_total",
			Help: "Crash reports refused at /telemetry/v1/reports, by reason: rate_limited, too_large, media_type, invalid.",
		}, []string{"reason"}),
		llmRequests: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_llm_requests_total",
			Help: "Relayed local-LLM requests by result (ok, or the reason they failed). No request content is recorded.",
		}, []string{"result"}),
		llmBytes: prometheus.NewCounter(prometheus.CounterOpts{
			Name: "reelbolt_runner_gateway_llm_response_bytes_total",
			Help: "Bytes of local-LLM responses relayed back to callers.",
		}),
		llmInflight: prometheus.NewGauge(prometheus.GaugeOpts{
			Name: "reelbolt_runner_gateway_llm_inflight",
			Help: "Local-LLM requests currently being relayed by this gateway instance.",
		}),
	}
	registry.MustRegister(
		m.connected, m.handshakes, m.messages, m.closes, m.unexpected,
		m.handshakeTimeouts, m.heartbeatTimeouts,
		m.nonceReplays, m.nonceSaturated, m.revocationRechecks, m.touchFailures,
		m.queueDepth, m.freeSlots, m.leases, m.jobsCreated, m.claimFailures,
		m.opResponses, m.sessionOpResults, m.notifications, m.notifyReconnects, m.executionSweepUnavailable,
		m.revocationEvents, m.revocationEventReconnects,
		m.platformSwitchesUnreadable, m.platformPaused, m.claimDeferred,
		m.displayLookups, m.telemetryReports, m.telemetryRejected,
		m.llmRequests, m.llmBytes, m.llmInflight,
		collectors.NewGoCollector(),
	)
	m.handler = promhttp.HandlerFor(registry, promhttp.HandlerOpts{})
	return m
}

// setConnected publishes the live-runner gauge for one kind.
func (m *gatewayMetrics) setConnected(kind string, n int) {
	m.connected.WithLabelValues(kind).Set(float64(n))
}

func (m *gatewayMetrics) countMessage(t string, incoming bool) {
	direction := "out"
	if incoming {
		direction = "in"
	}
	m.messages.WithLabelValues(t, direction).Inc()
}

func (m *gatewayMetrics) countClose(code int) {
	m.closes.WithLabelValues(strconv.Itoa(code)).Inc()
}

// sessionOps counts one engine-facing session op by result.
func (m *gatewayMetrics) sessionOps(result string) {
	m.sessionOpResults.WithLabelValues(result).Inc()
}

// leaseEvent counts one lease transition.
func (m *gatewayMetrics) leaseEvent(result string) {
	m.leases.WithLabelValues(result).Inc()
}

// setQueueDepth replaces the queue-depth gauge with the current counts. Labels
// that have emptied are reset, so a depth that falls to zero is reported as zero
// rather than as the last value seen.
func (m *gatewayMetrics) setQueueDepth(depth []QueueDepth) {
	m.queueDepth.Reset()
	for _, row := range depth {
		m.queueDepth.WithLabelValues(row.Target, row.JobType).Set(float64(row.Queued))
	}
}

// setFreeSlots publishes the free slots advertised by this instance's runners.
func (m *gatewayMetrics) setFreeSlots(target string, n int) {
	m.freeSlots.WithLabelValues(target).Set(float64(n))
}

// setPlatformPaused publishes one kill-switch series: 1 when the switch is
// engaged, 0 when it is not. A gauge rather than a counter because the useful
// question is the current state, and because a switch that was briefly engaged
// hours ago is not what an alert should page on.
func (m *gatewayMetrics) setPlatformPaused(name string, paused bool) {
	value := 0.0
	if paused {
		value = 1
	}
	m.platformPaused.WithLabelValues(name).Set(value)
}

// claimDeferredFor counts a claim round that assigned nothing on purpose, by the
// reason it did not. The jobs stay Queued, so nothing is lost and nothing needs
// to be replayed: releasing the switch is enough for the next round to take them.
func (m *gatewayMetrics) claimDeferredFor(reason string) {
	m.claimDeferred.WithLabelValues(reason).Inc()
}

// poolStats is one pgx pool's connection counts at scrape time.
type poolStats struct {
	acquired int32
	idle     int32
	max      int32
}

// pgxPoolStats reads a live pool's counts from pgxpool.Stat.
func pgxPoolStats(p *pgxpool.Pool) func() poolStats {
	return func() poolStats {
		s := p.Stat()
		return poolStats{acquired: s.AcquiredConns(), idle: s.IdleConns(), max: s.MaxConns()}
	}
}

// dbPoolCollector publishes the database pool gauges, one series per pool
// (devices, jobs, switches), read from the pools themselves at scrape time so
// nothing has to keep them up to date:
//
//	reelbolt_runner_gateway_db_pool_acquired_connections{pool}
//	reelbolt_runner_gateway_db_pool_idle_connections{pool}
//	reelbolt_runner_gateway_db_pool_max_connections{pool}
//
// The jobs pool's acquired count never drops below 1 while the gateway runs:
// that is the LISTEN connection (pool.go).
type dbPoolCollector struct {
	names    []string
	pools    map[string]func() poolStats
	acquired *prometheus.Desc
	idle     *prometheus.Desc
	max      *prometheus.Desc
}

func newDBPoolCollector(pools map[string]func() poolStats) *dbPoolCollector {
	names := make([]string, 0, len(pools))
	for name := range pools {
		names = append(names, name)
	}
	sort.Strings(names)
	return &dbPoolCollector{
		names: names,
		pools: pools,
		acquired: prometheus.NewDesc("reelbolt_runner_gateway_db_pool_acquired_connections",
			"Database connections currently checked out of this gateway instance's pgx pool, by pool (devices, jobs, switches). The jobs pool always holds one for LISTEN.",
			[]string{"pool"}, nil),
		idle: prometheus.NewDesc("reelbolt_runner_gateway_db_pool_idle_connections",
			"Open database connections idle in this gateway instance's pgx pool, by pool.",
			[]string{"pool"}, nil),
		max: prometheus.NewDesc("reelbolt_runner_gateway_db_pool_max_connections",
			"The cap on this gateway instance's pgx pool (DATABASE_MAX_CONNS, else pool_max_conns in DATABASE_URL, else 4), by pool.",
			[]string{"pool"}, nil),
	}
}

func (c *dbPoolCollector) Describe(ch chan<- *prometheus.Desc) {
	ch <- c.acquired
	ch <- c.idle
	ch <- c.max
}

func (c *dbPoolCollector) Collect(ch chan<- prometheus.Metric) {
	for _, name := range c.names {
		s := c.pools[name]()
		ch <- prometheus.MustNewConstMetric(c.acquired, prometheus.GaugeValue, float64(s.acquired), name)
		ch <- prometheus.MustNewConstMetric(c.idle, prometheus.GaugeValue, float64(s.idle), name)
		ch <- prometheus.MustNewConstMetric(c.max, prometheus.GaugeValue, float64(s.max), name)
	}
}

// observePools registers the database pool gauges. Call it once, after the
// pools are open: a second registration of the same names panics.
func (m *gatewayMetrics) observePools(pools map[string]func() poolStats) {
	m.registry.MustRegister(newDBPoolCollector(pools))
}
