package main

// The `loop` subcommand: the pool waker (initiative on-demand-compute). It is
// off unless POOL_WAKER_ENABLED=true; the legacy dry-run behaviour of this
// command is untouched.

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"log"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/poolwaker"
)

// ProviderFactory builds a Provider from the environment (never logs it).
type ProviderFactory func(getenv func(string) string) (poolwaker.Provider, error)

// providerFactories maps POOL_PROVIDER to a factory.
//
// REGISTRATION POINT (T-002): the Hetzner provider registers here, for example
//
//	providerFactories["hetzner"] = hetznerFactory
//
// via an init() in a file of this package that wraps hetzner.New. Until then
// only "fake" exists.
var providerFactories = map[string]ProviderFactory{
	"fake": func(func(string) string) (poolwaker.Provider, error) { return poolwaker.NewFake(nil), nil },
}

type loopConfig struct {
	poolwaker.Config
	Provider     string
	Interval     time.Duration
	LedgerPath   string
	UsagePath    string
	GatewayURL   string
	GatewayToken string
}

func floatEnv(key string, fallback float64) float64 {
	v := strings.TrimSpace(os.Getenv(key))
	if v == "" {
		return fallback
	}
	f, err := strconv.ParseFloat(v, 64)
	if err != nil {
		return fallback
	}
	return f
}

func secondsEnv(key string, fallback time.Duration) time.Duration {
	n := intEnv(key, int(fallback/time.Second))
	if n <= 0 {
		return fallback
	}
	return time.Duration(n) * time.Second
}

// defaultServerTypes is the size list when nothing is configured: dedicated-core ccx servers,
// largest first, then smaller ones the account may still be allowed to create.
var defaultServerTypes = []string{"ccx33", "ccx23", "ccx13"}

// serverTypesFromEnv reads HCLOUD_SERVER_TYPES (comma separated, in order of preference), else the
// single HCLOUD_SERVER_TYPE kept for existing configurations, else defaultServerTypes.
func serverTypesFromEnv(getenv func(string) string) []string {
	var out []string
	for _, key := range []string{"HCLOUD_SERVER_TYPES", "HCLOUD_SERVER_TYPE"} {
		for _, t := range strings.Split(getenv(key), ",") {
			if t = strings.TrimSpace(t); t != "" {
				out = append(out, t)
			}
		}
		if len(out) > 0 {
			return out
		}
	}
	return append([]string(nil), defaultServerTypes...)
}

func loadLoopConfig() loopConfig {
	app := loadConfig()
	state := strings.TrimSpace(os.Getenv("POOL_STATE_FILE"))
	if state == "" {
		state = "pool-waker-ledger.json"
	}
	if st, err := os.Stat(state); (err == nil && st.IsDir()) || strings.HasSuffix(state, "/") {
		state = filepath.Join(state, "ledger.json")
	}
	usage := strings.TrimSpace(os.Getenv("POOL_USAGE_FILE"))
	if usage == "" {
		usage = filepath.Join(filepath.Dir(state), "usage.jsonl")
	}
	maxNodes := intEnv("POOL_MAX_NODES", 2)
	if maxNodes <= 0 {
		maxNodes = 2
	}
	serverTypes := serverTypesFromEnv(os.Getenv)
	provider := strings.TrimSpace(os.Getenv("POOL_PROVIDER"))
	if provider == "" {
		provider = "hetzner"
	}
	return loopConfig{
		Config: poolwaker.Config{
			Instance:       strings.TrimSpace(os.Getenv("POOL_INSTANCE")),
			ServerTypes:    serverTypes,
			MaxNodes:       maxNodes,
			SlotsPerNode:   app.SlotsPerNode,
			IdleWindow:     secondsEnv("POOL_IDLE_SECONDS", 0),
			HourEndMargin:  secondsEnv("POOL_HOUR_END_MARGIN_SECONDS", 5*time.Minute),
			Cooldown:       secondsEnv("POOL_COOLDOWN_SECONDS", 60*time.Second),
			BootDeadline:   secondsEnv("POOL_BOOT_DEADLINE_SECONDS", 300*time.Second),
			DailyCapEUR:    floatEnv("POOL_DAILY_SPEND_CAP_EUR", 5),
			AlertFraction:  floatEnv("POOL_ALERT_FRACTION", 0.8),
			NodeGatewayURL: strings.TrimSpace(os.Getenv("POOL_NODE_GATEWAY_URL")),
			CPULimit:       strings.TrimSpace(os.Getenv("SANDBOX_CPU_LIMIT")),

			SizeRefusalBackoff: secondsEnv("POOL_SIZE_REFUSAL_BACKOFF_SECONDS", time.Hour),
		},
		Provider:     provider,
		Interval:     secondsEnv("POOL_LOOP_INTERVAL_SECONDS", 15*time.Second),
		LedgerPath:   state,
		UsagePath:    usage,
		GatewayURL:   app.GatewayURL,
		GatewayToken: app.Token,
	}
}

// validateLoopConfig checks the loaded settings the way the controller will (on a copy, so lc is
// untouched): the instance name and the configured size list.
func validateLoopConfig(lc loopConfig) error {
	cfg := lc.Config
	if err := cfg.Normalize(); err != nil {
		return fmt.Errorf("POOL_INSTANCE / HCLOUD_SERVER_TYPES: %w", err)
	}
	return nil
}

// refuseOnMediaHost is the guard for RB-SEC-032: the media host runs untrusted render workloads, so
// a read/write cloud provider token must not live there. The waker belongs on the control host
// (docker-compose.control.yml). REELBOLT_ROLE is the role name the deploy scripts use; the
// control compose file passes "control". POOL_WAKER_ALLOW_ON_MEDIA_HOST=true lifts the refusal.
// It only looks at whether the settings are present and never reads the token.
func refuseOnMediaHost(getenv func(string) string, provider string) error {
	get := func(k string) string { return strings.TrimSpace(getenv(k)) }
	if !strings.EqualFold(get("REELBOLT_ROLE"), "media") {
		return nil
	}
	if provider != "hetzner" && get("HCLOUD_TOKEN") == "" && get("HCLOUD_TOKEN_FILE") == "" {
		return nil
	}
	if strings.EqualFold(get("POOL_WAKER_ALLOW_ON_MEDIA_HOST"), "true") {
		log.Printf("WARNING: the pool waker is starting with a cloud provider token on the media host " +
			"(POOL_WAKER_ALLOW_ON_MEDIA_HOST=true). That host runs untrusted render workloads.")
		return nil
	}
	return errors.New("REELBOLT_ROLE=media: the pool waker holds a read/write cloud provider token and must not run on " +
		"the media host, which runs untrusted render workloads. Run it on the control host " +
		"(docker-compose.control.yml, profile poolwaker), or set POOL_WAKER_ALLOW_ON_MEDIA_HOST=true to accept the risk")
}

// runLoop is `reelbolt-scaler loop [-once] [-dry-run] [-listen addr]`.
func runLoop(args []string) error {
	fs := flag.NewFlagSet("reelbolt-scaler loop", flag.ContinueOnError)
	once := fs.Bool("once", false, "run one iteration and exit")
	dry := fs.Bool("dry-run", false, "log decisions without creating or deleting anything")
	listen := fs.String("listen", "", "serve Prometheus metrics on this address (for example 127.0.0.1:9108); unauthenticated, keep it on loopback or a private network")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if !strings.EqualFold(strings.TrimSpace(os.Getenv("POOL_WAKER_ENABLED")), "true") {
		return errors.New("the pool waker is disabled: set POOL_WAKER_ENABLED=true to run `loop`")
	}
	lc := loadLoopConfig()
	lc.DryRun = *dry
	if lc.GatewayURL == "" || lc.GatewayToken == "" {
		return errors.New("RUNNER_GATEWAY_URL and RUNNER_INTERNAL_TOKEN are required")
	}
	if lc.NodeGatewayURL == "" {
		return errors.New("POOL_NODE_GATEWAY_URL (the wss or private ws URL nodes dial) is required")
	}
	if _, _, err := poolwaker.GatewayEndpoint(lc.NodeGatewayURL); err != nil {
		return fmt.Errorf("POOL_NODE_GATEWAY_URL: %w", err)
	}
	factory, ok := providerFactories[lc.Provider]
	if !ok {
		return fmt.Errorf("POOL_PROVIDER=%q is not available in this build", lc.Provider)
	}
	if err := refuseOnMediaHost(os.Getenv, lc.Provider); err != nil {
		return err
	}
	prov, err := factory(os.Getenv)
	if err != nil {
		return fmt.Errorf("provider %s: %w", lc.Provider, err)
	}
	if lc.Instance == "" {
		if lc.Provider == "hetzner" {
			return errors.New("POOL_INSTANCE is required for the hetzner provider (letters a-z, digits and -)")
		}
		lc.Instance = "default"
	}
	if err := validateLoopConfig(lc); err != nil {
		return err
	}
	gwClient, err := poolwaker.NewGatewayClient(lc.GatewayURL, poolwaker.GatewayTLSFromEnv(os.Getenv))
	if err != nil {
		return err
	}
	appCfg := loadConfig()
	appCfg.GatewayURL, appCfg.Token, appCfg.Client = lc.GatewayURL, lc.GatewayToken, gwClient
	slots := lc.SlotsPerNode
	gw := &poolwaker.HTTPGateway{
		BaseURL: lc.GatewayURL, Token: lc.GatewayToken, Client: gwClient,
		DemandFn: func(ctx context.Context) (poolwaker.Demand, error) {
			s, err := fetchScaling(ctx, appCfg)
			if err != nil {
				return poolwaker.Demand{}, err
			}
			d := Decide(s, 0, 0, slots)
			return poolwaker.Demand{DesiredNodes: d.Nodes, RunningNodes: d.RunningNodes, Queued: d.Awaiting, Running: d.Running}, nil
		},
	}
	ledger, err := poolwaker.OpenLedger(lc.LedgerPath, time.Now())
	if err != nil {
		return err
	}
	ctl, err := poolwaker.NewController(lc.Config, prov, gw, ledger, poolwaker.NewUsageWriter(lc.UsagePath), nil)
	if err != nil {
		return err
	}
	// Plain summary: caps and names only, never a credential.
	log.Printf("pool waker starting: provider=%s instance=%s serverTypes=%s maxNodes=%d idleOverride=%s hourEndMargin=%s cooldown=%s "+
		"bootDeadline=%s dailySpendCapEUR=%.2f alertAt=%.0f%% dryRun=%t once=%t ledger=%s usage=%s",
		prov.Name(), lc.Instance, strings.Join(lc.ServerTypes, ","), lc.MaxNodes, lc.IdleWindow, lc.HourEndMargin, lc.Cooldown, lc.BootDeadline,
		lc.DailyCapEUR, lc.AlertFraction*100, lc.DryRun, *once, lc.LedgerPath, lc.UsagePath)

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	if *listen != "" {
		mux := http.NewServeMux()
		mux.HandleFunc("/metrics", func(w http.ResponseWriter, _ *http.Request) {
			w.Header().Set("Content-Type", "text/plain; version=0.0.4")
			_, _ = w.Write([]byte(ctl.Metrics()))
		})
		srv := &http.Server{Addr: *listen, Handler: mux, ReadHeaderTimeout: 5 * time.Second}
		go func() {
			if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
				log.Printf("metrics server: %v", err)
			}
		}()
		defer srv.Close()
	}
	if *once {
		ctl.Tick(ctx)
		log.Printf("metrics:\n%s", ctl.Metrics())
		return nil
	}
	ctl.Run(ctx, lc.Interval)
	return nil
}
