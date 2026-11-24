package main

import (
	"context"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/handlers"
	"github.com/vecchiotom/reelbolt/services"
	"github.com/vecchiotom/reelbolt/telemetry"
)

var Version = "0.4.0-alpha"

func main() {
	os.Exit(run())
}

// run holds the startup sequence and returns the process exit code. It does not
// call log.Fatal: a fatal call would skip the deferred telemetry shutdown.
func run() int {
	config.Version = Version
	config.Load()

	// Say which email provider will carry this deployment's mail, before anything can need it.
	// A silent fall back to console logging is how "nobody gets the password-reset mail" becomes
	// a mystery instead of a log line.
	services.LogEmailProvider()
	// C4: load MAIL_LOG_REF_KEY now, so an unset key is warned about once, at startup.
	services.InitMailLogRef()

	// Opt-in OTLP export (WP F9): a no-op unless OTEL_EXPORTER_OTLP_ENDPOINT is set.
	shutdownTelemetry, otelOn, err := telemetry.Init(context.Background(), Version)
	if err != nil {
		log.Printf("Failed to initialise telemetry: %v", err)
		return 1
	}
	if otelOn {
		log.Printf("OpenTelemetry export enabled")
		defer func() { _ = shutdownTelemetry(context.Background()) }()
	}

	db, err := database.Initialize(config.Cfg.DatabaseURL)
	if err != nil {
		log.Printf("Failed to connect to database: %v", err)
		return 1
	}
	if err := database.ApplyPoolLimits(db, config.Cfg.DBPool); err != nil {
		log.Printf("Failed to configure the database pool: %v", err)
		return 1
	}
	log.Printf("Database pool: max open %d, max idle %d, max lifetime %s",
		config.Cfg.DBPool.MaxOpenConns, config.Cfg.DBPool.MaxIdleConns, config.Cfg.DBPool.ConnMaxLifetime)
	// Pool gauges ride the OTLP meter: a no-op unless telemetry export is on.
	if sqlDB, err := db.DB(); err == nil {
		if err := telemetry.RegisterDBPoolMetrics(sqlDB.Stats); err != nil {
			log.Printf("database pool metrics unavailable: %v", err)
		}
	}

	services.EnsureAdminExists()
	services.StartRabbitMQConsumer()

	handlers.RegisterHandlers()

	log.Printf("ReelBolt API %s listening on :8080", Version)
	srv := newServer(telemetry.Wrap(handlers.Router))
	serveErr := make(chan error, 1)
	go func() { serveErr <- srv.ListenAndServe() }()

	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGTERM, syscall.SIGINT)
	select {
	case err := <-serveErr:
		log.Print(err)
		return 1
	case s := <-sig:
		log.Printf("received %s: draining", s)
	}

	// Stop consuming before anything else: the shared queue is auto-acked, so a message handed to
	// this dying process would be lost.
	stopCtx, stopCancel := context.WithTimeout(context.Background(), 5*time.Second)
	services.StopRabbitMQConsumer(stopCtx)
	stopCancel()

	// Close SSE streams first (Shutdown would otherwise wait on them) so browsers reconnect at once.
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 25*time.Second)
	defer cancel()
	go func() {
		// Streams opened between the first close and the listener closing are closed too.
		for i := 0; i < 5; i++ {
			services.Hub.CloseAll()
			select {
			case <-shutdownCtx.Done():
				return
			case <-time.After(time.Second):
			}
		}
	}()
	if err := srv.Shutdown(shutdownCtx); err != nil {
		log.Printf("shutdown: %v", err)
	}
	// A mail started from a shared delivery just before the consumer stopped must finish.
	notifyCtx, notifyCancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer notifyCancel()
	services.WaitNotifications(notifyCtx)
	return 0
}

// newServer builds the API's http.Server with the timeouts that bound a slow or idle client
// (API-GO-08). ReadHeaderTimeout and ReadTimeout cover reading a request (every body the API
// reads is small and size-capped); IdleTimeout covers a keep-alive connection between
// requests. There is deliberately no WriteTimeout: it is a deadline on the whole response and
// would cut a long-lived SSE stream. ReadTimeout does not: once the request is read, net/http
// clears the read deadline while the handler runs, so the event routes keep writing.
func newServer(h http.Handler) *http.Server {
	return &http.Server{
		Addr:              ":8080",
		Handler:           h,
		ReadHeaderTimeout: 10 * time.Second,
		ReadTimeout:       30 * time.Second,
		IdleTimeout:       120 * time.Second,
	}
}
