package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"net"
	"net/http"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/gorilla/mux"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// gateway is the runner-gateway service: it accepts runner WebSocket
// connections, authenticates the hello handshake, keeps the in-memory registry
// of live runners, matches queued jobs to them under leases and fencing (D5b),
// and serves /healthz, /metrics and the engine-facing internal API.
//
// Session op proxying and the scaling signal are D5c. What they need from here
// is present: the registry (conn by runner id), each connection's freeSlots and
// running set, conn.send, gatewayInstance on every welcome, and the
// runner_sessions row D5b opens at assignment.
type gateway struct {
	// interfaceAddrs lists the gateway's own interface addresses for the
	// plain-listener local-peer check; nil means net.InterfaceAddrs (tests inject).
	interfaceAddrs func() ([]net.Addr, error)

	cfg      appConfig
	store    DeviceStore
	registry *registry
	nonces   *nonceCache
	metrics  *gatewayMetrics

	// jobs is the durable queue and tokens mints the per-job lease tokens. Both
	// are nil for a gateway started without a queue (the D5a tests exercise the
	// handshake without a database), and every job path checks for that rather
	// than panicking.
	jobs   JobStore
	tokens *jobTokenSigner

	// display reads assign.display, the names a runner's UI shows next to a
	// job (display.go). Nil means assignments carry none.
	display JobDisplayStore

	// telemetry is the public crash-report ingest's per-address rate limiter
	// (telemetry.go).
	telemetry *telemetryLimiter

	// ops correlates the engine's in-flight session ops with the op.responses
	// coming back over the socket (D5c). It is per replica, which is exactly right:
	// only the replica holding the connection can see the answer, and
	// runner_sessions.gateway_instance is what sends the request there.
	ops *opTable

	// llm correlates relayed local-LLM requests with the runner's answers, and
	// llmTimeout is the default per-request deadline (zero = 5 minutes).
	llm        *llmTable
	llmTimeout time.Duration
	peerSet    llmPeerSet // relay peer fan-out (llm_peers.go)

	// proxyClient forwards a session op to the replica that owns the session. It
	// carries no Timeout of its own: every hop sets a context deadline from the
	// op's own timeout, which is a number this process cannot know in advance.
	proxyClient *http.Client

	// switches are the platform kill switches (F11). A nil facade is not the same
	// as "no gating": every decision it feeds fails closed, so a gateway started
	// without one — or with a store it cannot read — assigns nothing and refuses
	// no hello. The tests inject a fake explicitly rather than relying on the
	// absence behaving like one.
	switches *platformSwitches

	// poolKey signs pool node tokens (RUNNER_POOL_TOKEN_KEY); poolNodes and waker
	// back the on-demand pool routes. See pool_nodes.go.
	poolKey   []byte
	poolNodes PoolNodeStore
	waker     WakerStore

	// cancelMu guards pendingCancels: the cancellations waiting out their grace
	// window before the job is marked Cancelled.
	cancelMu       sync.Mutex
	pendingCancels map[string]pendingCancel

	// assignWake wakes the assignment loop when the queue announces new work.
	assignWake chan struct{}

	// now is the clock. It is a field so tests can drive skew and expiry
	// without sleeping.
	now     func() time.Time
	started time.Time

	// writeTimeout bounds a single protocol write.
	writeTimeout time.Duration

	draining atomic.Bool

	rootCtx context.Context
	cancel  context.CancelFunc
	wg      sync.WaitGroup
}

func newGateway(cfg appConfig, store DeviceStore) *gateway {
	g := &gateway{
		cfg:            cfg,
		store:          store,
		registry:       newRegistry(),
		nonces:         newNonceCache(cfg.NonceTTL),
		metrics:        newGatewayMetrics(),
		now:            time.Now,
		started:        time.Now(),
		writeTimeout:   10 * time.Second,
		assignWake:     make(chan struct{}, 1),
		pendingCancels: map[string]pendingCancel{},
		ops:            newOpTable(),
		llm:            newLLMTable(),
		telemetry:      newTelemetryLimiter(time.Now),
		proxyClient: &http.Client{
			// No client-level Timeout: a render op legitimately runs for minutes,
			// and the per-hop deadline comes from the op's own timeout.
			Transport: &http.Transport{
				DialContext:         (&net.Dialer{Timeout: 5 * time.Second}).DialContext,
				MaxIdleConnsPerHost: 8,
				IdleConnTimeout:     30 * time.Second,
			},
		},
	}
	g.rootCtx, g.cancel = context.WithCancel(context.Background())
	return g
}

// attachQueue gives the gateway its durable queue and job-token signer. It is
// separate from newGateway so the handshake can be exercised without a database,
// which is what the D5a tests do.
func (g *gateway) attachQueue(jobs JobStore, tokens *jobTokenSigner) {
	g.jobs = jobs
	g.tokens = tokens
}

// attachSwitches gives the gateway its platform kill switches (F11). It is
// separate from newGateway for the same reason attachQueue is: a test that wants
// a specific switch state injects a facade over a store it controls, and a test
// that wants to see the fail-closed behaviour leaves it nil.
func (g *gateway) attachSwitches(switches *platformSwitches) {
	g.switches = switches
}

// RegisterRoutes wires the gateway's HTTP surface. It follows the repository's
// Register*Routes convention; main calls it once.
func (g *gateway) RegisterRoutes(router *mux.Router) {
	router.HandleFunc("/healthz", g.handleHealthz).Methods(http.MethodGet)
	router.Handle("/metrics", g.metrics.handler).Methods(http.MethodGet)
	router.HandleFunc(protocol.ConnectPath, g.handleConnect).Methods(http.MethodGet)
	router.HandleFunc(TelemetryPath, g.handleTelemetry).Methods(http.MethodPost)
	g.registerInternalRoutes(router, true)
}

// start launches the background loops. The context passed in is the process
// lifetime; cancelling it (or calling shutdown) stops them.
func (g *gateway) start(ctx context.Context) {
	g.rootCtx, g.cancel = context.WithCancel(ctx)
	loops := []func(context.Context){
		g.runRevocationSweeper,
		g.runReaper,
		g.runAssigner,
	}
	if g.jobs != nil {
		// Only a gateway with a queue has anything to listen for or to consume
		// revocation events for.
		loops = append(loops, g.runJobListener, g.runRevocationConsumer)
	}
	for _, loop := range loops {
		g.wg.Add(1)
		go func(loop func(context.Context)) {
			defer g.wg.Done()
			loop(g.rootCtx)
		}(loop)
	}
}

// shutdown drains: every live runner is told goodbye and closed with 1001, the
// background loops stop, and the caller can then stop the HTTP server. A runner
// that reconnects to another replica resumes through hello.running (D5b).
func (g *gateway) shutdown() {
	g.draining.Store(true)
	g.cancel()
	// One goroutine per connection: coder/websocket's Close waits for the peer's
	// close frame, so closing thousands of runners in sequence would take seconds
	// each.
	var closing sync.WaitGroup
	for _, c := range g.registry.snapshot() {
		closing.Add(1)
		go func(c *conn) {
			defer closing.Done()
			ctx, cancel := context.WithTimeout(context.Background(), g.writeTimeout)
			defer cancel()
			_ = c.sendPayload(ctx, protocol.TypeBye, protocol.ByePayload{Reason: protocol.CancelShutdown})
			g.metrics.countClose(int(protocol.CloseGoingAway))
			c.closeWith(protocol.CloseGoingAway, "gateway shutting down")
		}(c)
	}
	closing.Wait()
	g.wg.Wait()
}

// watchdog runs fire after d unless the returned stop function is called first.
// It exists because coder/websocket cannot deliver a close code once the reader
// itself has hit a deadline: the connection is already failed by then and the
// peer sees an abrupt EOF instead of the code the specification requires. So a
// deadline that must reach the runner as a code is enforced from another
// goroutine, which closes the connection while its reader is still blocked.
func watchdog(d time.Duration, fire func()) (stop func()) {
	done := make(chan struct{})
	timer := time.NewTimer(d)
	go func() {
		select {
		case <-timer.C:
			fire()
		case <-done:
			timer.Stop()
		}
	}()
	var once sync.Once
	return func() { once.Do(func() { close(done) }) }
}

// watchLiveness enforces the dead window: a connection with no message from
// either side for deadAfterSec is closed with 4408. Any message, not just
// heartbeat, pushes the window back.
func (g *gateway) watchLiveness(ctx context.Context, c *conn, done <-chan struct{}) {
	interval := g.cfg.deadAfter() / 4
	if interval < 5*time.Millisecond {
		interval = 5 * time.Millisecond
	}
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for {
		select {
		case <-done:
			return
		case <-ctx.Done():
			return
		case <-ticker.C:
			if g.now().Sub(c.lastSeenAt()) < g.cfg.deadAfter() {
				continue
			}
			g.metrics.heartbeatTimeouts.Inc()
			g.metrics.countClose(int(protocol.CloseHeartbeatTimeout))
			log.Printf("%s: no message within %s, closing", c.logTag, g.cfg.deadAfter())
			c.closeWith(protocol.CloseHeartbeatTimeout, "no message within "+g.cfg.deadAfter().String())
			return
		}
	}
}

// handleHealthz reports liveness, not dependency health.
//
// It deliberately does not query Postgres: a database blip must not restart the
// gateway and drop every live runner with it. An unreachable registry already
// shows up as these symptoms that a scrape can see: refused handshakes
// (reelbolt_runner_gateway_handshakes_total{result="internal"}), failed
// revocation re-checks and failed last_seen_at writes.
func (g *gateway) handleHealthz(w http.ResponseWriter, r *http.Request) {
	body := map[string]any{
		"status":          "ok",
		"version":         Version,
		"gatewayInstance": g.cfg.GatewayInstance,
		"connected":       g.registry.len(),
		"uptimeSeconds":   int(g.now().Sub(g.started).Seconds()),
	}
	status := http.StatusOK
	if g.draining.Load() {
		body["status"] = "draining"
		status = http.StatusServiceUnavailable
	}
	writeJSON(w, status, body)
}

// runRevocationSweeper re-checks revoked_at for open connections. The
// specification requires it at least every 60 s, and also on the
// RunnerDeviceRevoked event — the event bus is F/D-area work, so the periodic
// re-check is the backstop that exists today.
func (g *gateway) runRevocationSweeper(ctx context.Context) {
	interval := g.cfg.RevocationInterval
	if interval <= 0 {
		return
	}
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			g.revocationSweep(ctx)
		}
	}
}

// revocationSweep reads the revoked state of every connected runner in one
// query and drops the ones that have been revoked.
func (g *gateway) revocationSweep(ctx context.Context) {
	conns := g.registry.snapshot()
	if len(conns) == 0 {
		return
	}
	ids := make([]string, 0, len(conns))
	for _, c := range conns {
		ids = append(ids, c.runnerID)
	}
	queryCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	revoked, err := g.store.RevokedAmong(queryCtx, ids)
	if err != nil {
		g.metrics.revocationRechecks.WithLabelValues("error").Inc()
		log.Printf("revocation re-check failed: %v", err)
		return
	}
	g.metrics.revocationRechecks.WithLabelValues("ok").Inc()
	for _, c := range conns {
		if _, gone := revoked[c.runnerID]; !gone {
			continue
		}
		log.Printf("%s: device revoked while connected, dropping the connection", c.logTag)
		// The same path the RunnerDeviceRevoked event drives: the device's jobs
		// are cancelled and disposed of before the socket closes.
		g.handleRevokedDevice(ctx, c.runnerID, "re-check")
	}
}

// touch refreshes runner_devices.last_seen_at and the capability document. It is
// throttled: a fleet of thousands of runners must not turn a 15 s heartbeat into
// a write per runner per heartbeat. A failure is logged and counted, never fatal
// to the connection.
func (g *gateway) touch(ctx context.Context, c *conn) {
	now := g.now().UTC()
	if !c.touchDue(now, g.cfg.LastSeenInterval) {
		return
	}
	writeCtx, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()
	if err := g.store.Touch(writeCtx, c.runnerID, now, c.caps); err != nil {
		g.metrics.touchFailures.Inc()
		log.Printf("%s: updating last_seen_at failed: %v", c.logTag, err)
	}
}

// gatewayHosts returns the candidate hostnames the hello signature may have been
// bound to, most specific first.
//
// The signature binds the host the runner dialled. Behind nginx that is the
// request Host ($host, without the port); a runner talking to a gateway replica
// directly would include the port. RUNNER_PUBLIC_HOST pins it for a deployment
// that wants no ambiguity. Both spellings are accepted: they are the same
// gateway, and binding the host exists to stop replay at a *different* gateway.
func (g *gateway) gatewayHosts(r *http.Request) []string {
	raw := g.cfg.PublicHost
	if raw == "" {
		raw = r.Header.Get("X-Forwarded-Host")
	}
	if raw == "" {
		raw = r.Host
	}
	if i := strings.Index(raw, ","); i >= 0 {
		raw = raw[:i]
	}
	raw = strings.TrimSpace(raw)
	canonical := protocol.NormalizeGatewayHost(raw)
	hosts := []string{canonical}
	if withoutDots := strings.TrimSuffix(strings.ToLower(raw), "."); withoutDots != "" && withoutDots != canonical {
		hosts = append(hosts, withoutDots)
	}
	return hosts
}

// remoteAddr is the peer's address, preferring the proxy's view.
func remoteAddr(r *http.Request) string {
	if forwarded := r.Header.Get("X-Forwarded-For"); forwarded != "" {
		if i := strings.Index(forwarded, ","); i >= 0 {
			return strings.TrimSpace(forwarded[:i])
		}
		return strings.TrimSpace(forwarded)
	}
	return r.RemoteAddr
}

func writeJSON(w http.ResponseWriter, status int, body any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(body)
}

// internalError is a gateway-side failure (the device registry is unreachable,
// say) rather than an authentication decision: the runner should retry rather
// than conclude its identity is bad.
func internalError(format string, args ...any) *handshakeError {
	return &handshakeError{
		code:   protocol.CloseInternal,
		metric: "internal",
		reason: fmt.Sprintf(format, args...),
	}
}
