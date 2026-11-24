// Command runner-gateway is the WebSocket endpoint runners dial.
//
// A runner is a process that dials out to wss://runner.<domain>/v1/connect,
// advertises what it can do in hello, and executes the jobs the gateway assigns
// to it. There are two kinds: CloudPool, our own worker VMs, authenticated with
// a shared credential, and Desktop, a customer's paired machine, authenticated
// with a per-device Ed25519 key. docs/runner-protocol.md is the normative
// specification and sandbox/pkg/protocol holds the wire types.
//
// D5a built the service, the endpoint, the handshake, the device registry reads
// and the in-memory registry of live runners. D5b added the durable job queue,
// leases, fencing and routing behind the internal API. Session op proxying and
// the scaling endpoint are D5c. F11 added the platform kill switches: the
// gateway reads platform_settings (cached 10 s) and, when platform compute is
// paused or that table cannot be read, assigns nothing at all — a pause, since
// the queued jobs are durable and stay Queued.
//
// Two small additions serve the desktop app. Each assign carries an optional
// display block (the workspace, project and workflow names, read once with a
// 2-second bound from the inference tables and never required; display.go), and
// the public POST /telemetry/v1/reports ingests the runner's opt-in crash
// reports - validated, rate limited, logged as one line each, never stored
// (telemetry.go).
//
// Configuration (every value except DATABASE_URL has a default):
//
//	DATABASE_URL                 required. runner_devices, runner_jobs,
//	                             runner_sessions and platform_settings (the
//	                             gateway never migrates any of them), plus a
//	                             read of workflow_executions, workflow_definitions,
//	                             projects and organizations for assign.display.
//	DATABASE_MAX_CONNS           the cap on EACH of the three pgx pools (devices,
//	                             jobs, switches), so the gateway holds at most 3x
//	                             this. Unset: pool_max_conns in DATABASE_URL, else
//	                             4. At least 2, because the jobs pool keeps one
//	                             connection for LISTEN (pool.go).
//	PORT                         the listen port (default 8090).
//	GATEWAY_INSTANCE             this replica's identity, in welcome.gatewayInstance
//	                             and runner_jobs.gateway_instance (default: POD_IP,
//	                             else the hostname).
//	RUNNER_PUBLIC_HOST           the hostname the hello signature binds.
//	RUNNER_POOL_CREDENTIALS      accepted pool credentials, comma separated.
//	RUNNER_MIN_AGENT_VERSION     advertised in welcome.minAgentVersion.
//	RUNNER_INTERNAL_TOKEN        REQUIRED for /internal/v1/*. With none configured
//	                             the whole internal API answers 503: those routes
//	                             create and cancel work on other people's runners.
//	RUNNER_JOB_TOKEN_KEY         base64 HS256 key for job tokens. Empty means a
//	                             key generated per process, which is wrong for
//	                             more than one replica and logged as such.
//	RABBITMQ_URL                 the broker the RunnerDeviceRevoked fast path
//	                             consumes from. Empty leaves revocation to the
//	                             60 s revoked_at re-check, which is the backstop.
//	RUNNER_LEASE_SEC             lease length (45).
//	RUNNER_ACCEPT_TIMEOUT_SEC    how long a runner has to answer assign (10).
//	RUNNER_CANCEL_GRACE_SEC      the cancellation window (10).
//	RUNNER_REAPER_INTERVAL       lease reaper period (10s; 0 disables).
//	RUNNER_ASSIGN_INTERVAL       idle-runner sweep period (5s; 0 disables).
//	RUNNER_POOL_ORG_SLOT_CAP     per-organisation CloudPool slot cap (0 = none,
//	                             because the F11 entitlement numbers do not exist).
//	RUNNER_GATEWAY_INTERNAL_PORT the port a PEER replica listens on, used only to
//	                             forward a session op to the replica that owns the
//	                             session (default: PORT).
//	RUNNER_OP_TIMEOUT_SEC        optional cap on one session op below the job's
//	                             own timeout (0 = the job's timeout, the protocol
//	                             rule for exec and render).
//	RUNNER_PROXY_TIMEOUT         slack added to an op's deadline for the extra hop
//	                             to the owning replica (30s).
//	RUNNER_POOL_BUFFER_SLOTS     spare CloudPool slots GET /internal/v1/scaling
//	                             asks for on top of the existing work (1).
//	RUNNER_HEARTBEAT_SEC, RUNNER_DEAD_AFTER_SEC, RUNNER_HELLO_TIMEOUT,
//	RUNNER_CLOCK_SKEW, RUNNER_NONCE_TTL, RUNNER_REVOCATION_INTERVAL,
//	RUNNER_LAST_SEEN_INTERVAL    liveness and handshake timings.
package main

import (
	"context"
	"errors"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/gorilla/mux"
)

var Version = "dev"

func main() {
	cfg, err := loadConfig()
	if err != nil {
		log.Fatalf("configuration: %v", err)
	}
	if cfg.DatabaseURL == "" {
		log.Fatal("DATABASE_URL is required: the gateway authenticates every desktop runner against " +
			"runner_devices.public_key and re-checks revoked_at on live connections, so it refuses to " +
			"start without the device registry.")
	}
	if cfg.DeadAfterSec <= cfg.HeartbeatSec {
		log.Fatalf("RUNNER_DEAD_AFTER_SEC (%d) must be greater than RUNNER_HEARTBEAT_SEC (%d): a dead "+
			"window at or below the heartbeat interval closes healthy runners.", cfg.DeadAfterSec, cfg.HeartbeatSec)
	}
	if len(cfg.PoolCredentials) == 0 {
		log.Printf("RUNNER_POOL_CREDENTIALS is empty: no CloudPool worker can authenticate on this instance")
	}

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	store, err := NewPostgresDeviceStore(ctx, cfg.DatabaseURL, cfg.DatabaseMaxConns)
	if err != nil {
		// Unwind the deferred cancel() ourselves: log.Fatal calls os.Exit, which
		// skips defers.
		cancel()
		log.Fatalf("device registry unavailable: %v", err) //nolint:gocritic // exitAfterDefer: cancel() is invoked explicitly above
	}
	defer store.Close()

	jobs, err := NewPostgresJobStore(ctx, cfg.DatabaseURL, cfg.DatabaseMaxConns)
	if err != nil {
		cancel()
		log.Fatalf("job queue unavailable: %v", err) //nolint:gocritic // exitAfterDefer: cancel() is invoked explicitly above
	}
	defer jobs.Close()

	settings, err := NewPostgresSwitchStore(ctx, cfg.DatabaseURL, cfg.DatabaseMaxConns)
	if err != nil {
		cancel()
		log.Fatalf("platform settings unavailable: %v", err) //nolint:gocritic // exitAfterDefer: cancel() is invoked explicitly above
	}
	defer settings.Close()

	key, ephemeral, err := LoadJobTokenKey(cfg.JobTokenKey)
	if err != nil {
		cancel()
		log.Fatalf("job token key: %v", err) //nolint:gocritic // exitAfterDefer: cancel() is invoked explicitly above
	}
	if ephemeral {
		log.Printf("RUNNER_JOB_TOKEN_KEY is not set: job tokens are signed with a key generated for this " +
			"process, so with more than one replica a runner that reconnects to a different replica is told " +
			"its lease was lost. Set the key for any deployment with more than one gateway pod.")
	}
	tokens, err := newJobTokenSigner(key)
	if err != nil {
		cancel()
		log.Fatalf("job token signer: %v", err) //nolint:gocritic // exitAfterDefer: cancel() is invoked explicitly above
	}
	if cfg.InternalToken == "" {
		log.Printf("RUNNER_INTERNAL_TOKEN is not set: the internal API is closed, so no engine can create or " +
			"cancel a job on this instance")
	}

	g := newGateway(cfg, store)
	g.metrics.observePools(map[string]func() poolStats{
		poolDevices:  pgxPoolStats(store.pool),
		poolJobs:     pgxPoolStats(jobs.pool),
		poolSwitches: pgxPoolStats(settings.pool),
	})
	log.Printf("database pools: devices %d, jobs %d, switches %d max connections",
		store.pool.Config().MaxConns, jobs.pool.Config().MaxConns, settings.pool.Config().MaxConns)
	g.attachQueue(jobs, tokens)
	g.attachDisplay(jobs)
	g.attachSwitches(newPlatformSwitches(settings, g.metrics))
	if cfg.PoolTokenKey != "" {
		poolStore := NewPostgresPoolStore(store.pool)
		g.attachPool([]byte(cfg.PoolTokenKey), poolStore, poolStore)
	} else {
		log.Printf("RUNNER_POOL_TOKEN_KEY is not set: the on-demand pool routes answer 503 and rbn1. credentials are refused")
	}

	// One read before anything is served, so an unreadable settings table says so
	// in the startup log rather than surprising an operator on the first claim.
	// It is deliberately not fatal: F11's rule is that an unreadable store assigns
	// no work, not that the gateway stops, and a gateway that exited over it would
	// drop every connected runner with it.
	primeCtx, primeCancel := context.WithTimeout(ctx, 10*time.Second)
	g.switches.prime(primeCtx)
	primeCancel()

	g.start(ctx)

	router := mux.NewRouter()
	g.RegisterRoutes(router)

	server := &http.Server{
		Addr:    ":" + cfg.Port,
		Handler: router,
		// No WriteTimeout: a WebSocket connection is long lived and a write
		// deadline on the handler would cut it off. Each protocol write carries
		// its own context deadline instead.
		ReadHeaderTimeout: 10 * time.Second,
		IdleTimeout:       2 * time.Minute,
	}

	tlsServer, err := g.newInternalTLSServer()
	if err != nil {
		cancel()
		log.Fatalf("internal TLS listener: %v", err) //nolint:gocritic // exitAfterDefer: cancel() is invoked explicitly above
	}
	if tlsServer != nil {
		go func() {
			log.Printf("internal TLS listener on %s (client certificates required: %t)",
				tlsServer.Addr, cfg.InternalTLSClientCAFile != "")
			if err := tlsServer.ListenAndServeTLS("", ""); err != nil && !errors.Is(err, http.ErrServerClosed) {
				log.Printf("internal TLS listener stopped: %v", err)
				cancel()
			}
		}()
	}

	shutdownDone := make(chan struct{})
	go func() {
		defer close(shutdownDone)
		signals := make(chan os.Signal, 1)
		signal.Notify(signals, os.Interrupt, syscall.SIGTERM)
		<-signals
		log.Printf("shutting down: telling %d connected runner(s) goodbye", g.registry.len())
		g.shutdown()
		shutdownCtx, shutdownCancel := context.WithTimeout(context.Background(), 15*time.Second)
		defer shutdownCancel()
		if tlsServer != nil {
			if err := tlsServer.Shutdown(shutdownCtx); err != nil {
				log.Printf("internal TLS shutdown: %v", err)
			}
		}
		if err := server.Shutdown(shutdownCtx); err != nil {
			log.Printf("http shutdown: %v", err)
		}
	}()

	log.Printf("ReelBolt Runner Gateway %s listening on %s (instance %s)", Version, server.Addr, cfg.GatewayInstance)
	if err := server.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		// Unwind the deferred cancel() ourselves: log.Fatal calls os.Exit, which
		// skips defers.
		cancel()
		log.Fatal(err)
	}
	<-shutdownDone
	log.Printf("stopped")
}
