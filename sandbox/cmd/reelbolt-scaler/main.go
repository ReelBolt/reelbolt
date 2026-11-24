// Command reelbolt-scaler is the scaling hook for the CloudPool compute plane:
// it reads the gateway's desired CloudPool slot count (D5c), turns it into a
// node count, and hands that to a scale command.
//
// It is NOT the autoscaler. F12 does not exist, and there is no cloud credential
// in this repository, so this program deliberately stops one step short of the
// provider: it computes the number and, when told to, runs POOL_SCALE_COMMAND
// with the decision in its environment. That command is the seam F12 fills in —
// alongside the cooldown, the IAM the provider call needs and the cloud-init
// that makes a new node join the pool. Everything upstream of it (which number,
// from which signal, under which ceiling) is here and is tested.
//
// Default is a DRY RUN: without -apply it prints the decision and changes
// nothing. A scaling hook that acts by default is one that acts the first time
// somebody runs it to see what it would do.
//
// # Environment
//
//	RUNNER_GATEWAY_URL      http://runner-gateway:8090 on the gateway's own host, or the
//	                        gateway's https:// internal TLS address from another host (required).
//	                        Plain http:// to anything but loopback or the compose service name
//	                        is refused unless RUNNER_GATEWAY_ALLOW_PLAIN_HTTP=true
//	RUNNER_GATEWAY_CA_FILE, RUNNER_GATEWAY_CLIENT_CERT_FILE, RUNNER_GATEWAY_CLIENT_KEY_FILE
//	                        the client half of the gateway's mutual-TLS internal link (TLS 1.3)
//	RUNNER_INTERNAL_TOKEN   the gateway's internal API token (required)
//	POOL_SLOTS_PER_NODE     how many worker slots one node provides (default 1)
//	POOL_MIN_NODES          floor on the node count (default 0)
//	POOL_MAX_NODES          ceiling on the node count (default 0 = none); F14
//	                        already puts this variable in the staging env file
//	POOL_SCALE_COMMAND      the command to run with -apply (F12 replaces this)
//	POOL_SCALE_APPLY        "true" is the same as passing -apply
package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/poolwaker"
)

func main() {
	if err := run(os.Args[1:]); err != nil {
		log.Printf("reelbolt-scaler: %v", err)
		os.Exit(1)
	}
}

type appConfig struct {
	GatewayURL   string
	Token        string
	SlotsPerNode int
	MinNodes     int
	MaxNodes     int
	Apply        bool
	Command      string
	Timeout      time.Duration
	// Client is the gateway HTTP client (poolwaker.NewGatewayClient). Nil means the default client.
	Client *http.Client
}

func loadConfig() appConfig {
	return appConfig{
		GatewayURL:   strings.TrimRight(strings.TrimSpace(os.Getenv("RUNNER_GATEWAY_URL")), "/"),
		Token:        strings.TrimSpace(os.Getenv("RUNNER_INTERNAL_TOKEN")),
		SlotsPerNode: intEnv("POOL_SLOTS_PER_NODE", 1),
		MinNodes:     intEnv("POOL_MIN_NODES", 0),
		MaxNodes:     intEnv("POOL_MAX_NODES", 0),
		Apply:        strings.EqualFold(strings.TrimSpace(os.Getenv("POOL_SCALE_APPLY")), "true"),
		Command:      strings.TrimSpace(os.Getenv("POOL_SCALE_COMMAND")),
		Timeout:      durationEnv("POOL_SCALE_TIMEOUT", 30*time.Second),
	}
}

func run(args []string) error {
	if len(args) > 0 && args[0] == "loop" {
		return runLoop(args[1:])
	}
	fs := flag.NewFlagSet("reelbolt-scaler", flag.ContinueOnError)
	gateway := fs.String("gateway", "", "overrides RUNNER_GATEWAY_URL")
	token := fs.String("token", "", "overrides RUNNER_INTERNAL_TOKEN")
	slotsPerNode := fs.Int("slots-per-node", 0, "overrides POOL_SLOTS_PER_NODE")
	minNodes := fs.Int("min-nodes", -1, "overrides POOL_MIN_NODES")
	maxNodes := fs.Int("max-nodes", -1, "overrides POOL_MAX_NODES")
	apply := fs.Bool("apply", false, "run the scale command; without it the run is a dry run")
	command := fs.String("command", "", "overrides POOL_SCALE_COMMAND")
	asJSON := fs.Bool("json", false, "print the decision as JSON")
	skipMetrics := fs.Bool("no-metrics", false, "do not read /metrics for the fleet-wide cross-check")
	if err := fs.Parse(args); err != nil {
		return err
	}

	cfg := loadConfig()
	if *gateway != "" {
		cfg.GatewayURL = strings.TrimRight(*gateway, "/")
	}
	if *token != "" {
		cfg.Token = *token
	}
	if *slotsPerNode > 0 {
		cfg.SlotsPerNode = *slotsPerNode
	}
	if *minNodes >= 0 {
		cfg.MinNodes = *minNodes
	}
	if *maxNodes >= 0 {
		cfg.MaxNodes = *maxNodes
	}
	if *command != "" {
		cfg.Command = *command
	}
	if *apply {
		cfg.Apply = true
	}

	if cfg.GatewayURL == "" {
		return errors.New("RUNNER_GATEWAY_URL is required")
	}
	if cfg.Token == "" {
		return errors.New("RUNNER_INTERNAL_TOKEN is required: the scaling endpoint is behind the internal API and " +
			"an unauthenticated scaler would read nothing")
	}
	if cfg.Apply && cfg.Command == "" {
		return errors.New("-apply was given but no POOL_SCALE_COMMAND is set: there would be nothing to apply the " +
			"decision with. Leave it off for a dry run, or configure the command F12's provider call will replace")
	}
	if cfg.SlotsPerNode < 1 {
		return fmt.Errorf("POOL_SLOTS_PER_NODE=%d is not a positive number of slots", cfg.SlotsPerNode)
	}

	client, err := poolwaker.NewGatewayClient(cfg.GatewayURL, poolwaker.GatewayTLSFromEnv(os.Getenv))
	if err != nil {
		return err
	}
	cfg.Client = client

	ctx, cancel := context.WithTimeout(context.Background(), cfg.Timeout)
	defer cancel()

	scaling, err := fetchScaling(ctx, cfg)
	if err != nil {
		return err
	}
	decision := Decide(scaling, cfg.MinNodes, cfg.MaxNodes, cfg.SlotsPerNode)

	var metrics *MetricsSummary
	if !*skipMetrics {
		if summary, err := fetchMetrics(ctx, cfg); err != nil {
			log.Printf("note: /metrics could not be read (%v); the fleet-wide cross-check is skipped", err)
		} else {
			metrics = &summary
			crossCheck(decision, summary)
		}
	}

	if *asJSON {
		out := struct {
			Decision Decision        `json:"decision"`
			Metrics  *MetricsSummary `json:"metrics,omitempty"`
			Applied  bool            `json:"applied"`
		}{Decision: decision}
		if metrics != nil {
			out.Metrics = metrics
		}
		out.Applied = cfg.Apply
		enc := json.NewEncoder(os.Stdout)
		enc.SetIndent("", "  ")
		if err := enc.Encode(out); err != nil {
			return err
		}
	} else {
		printDecision(decision)
	}
	for _, w := range decision.Warnings {
		log.Printf("WARNING: %s", w)
	}

	if !cfg.Apply {
		log.Printf("dry run: nothing was scaled. Pass -apply (or POOL_SCALE_APPLY=true) to run the scale command.")
		return nil
	}
	return applyScale(ctx, cfg, decision)
}

func printDecision(d Decision) {
	log.Printf("desired %d slot(s) -> %d node(s) of %d slot(s) each (cappedBy=%s): queued=%d running=%d buffer=%d "+
		"connected=%d available=%d freeSlots=%d gateway=%s",
		d.DesiredSlots, d.Nodes, d.SlotsPerNode, d.CappedBy, d.Queued, d.Running, d.Buffer,
		d.Connected, d.Available, d.FreeSlots, d.GatewayInst)
}

// fetchScaling reads D5c's endpoint.
func fetchScaling(ctx context.Context, cfg appConfig) (scalingResponse, error) {
	var out scalingResponse
	body, err := get(ctx, cfg, "/internal/v1/scaling")
	if err != nil {
		return out, err
	}
	if err := json.Unmarshal(body, &out); err != nil {
		return out, fmt.Errorf("decode %s: %w", "/internal/v1/scaling", err)
	}
	// A 200 that is not the scaling document (an HTML error page from a proxy,
	// say) must not become "scale the pool to zero".
	if out.GatewayInstance == "" {
		return out, errNoDecision
	}
	return out, nil
}

// fetchMetrics reads the gateway's Prometheus text endpoint for the fleet-wide
// gauge values.
func fetchMetrics(ctx context.Context, cfg appConfig) (MetricsSummary, error) {
	body, err := get(ctx, cfg, "/metrics")
	if err != nil {
		return MetricsSummary{}, err
	}
	return ParseMetrics(string(body)), nil
}

func get(ctx context.Context, cfg appConfig, path string) ([]byte, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, cfg.GatewayURL+path, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Authorization", "Bearer "+cfg.Token)
	client := cfg.Client
	if client == nil {
		client = http.DefaultClient
	}
	resp, err := client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("GET %s: %w", path, err)
	}
	defer resp.Body.Close()
	// maxBody bounds what a hostile or broken endpoint can make this process
	// allocate. The answer is a handful of gauge lines.
	const maxBody = 4 << 20
	body, err := io.ReadAll(io.LimitReader(resp.Body, maxBody))
	if err != nil {
		return nil, fmt.Errorf("GET %s: %w", path, err)
	}
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("GET %s: HTTP %d: %s", path, resp.StatusCode, strings.TrimSpace(truncate(string(body), 300)))
	}
	return body, nil
}

func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n] + "..."
}

// crossCheck compares the endpoint's per-replica view with the fleet-wide gauge.
//
// D5c is explicit that connectedCloudPool/freeSlotsCloudPool describe ONE
// replica. A scaler pointed at a single replica therefore sees the whole queue
// and one pod's capacity, which over-scales; the gauge is the fleet-wide number
// and this is where the difference is said out loud rather than discovered as a
// bill.
func crossCheck(d Decision, m MetricsSummary) {
	if m.Series == 0 {
		log.Printf("note: /metrics carried neither reelbolt_runner_free_slots nor reelbolt_runner_queue_depth; " +
			"this gateway may predate D5b")
		return
	}
	if m.QueueDepth != float64(d.Queued) {
		log.Printf("note: the fleet-wide queue depth gauge is %.0f but this replica reports %d queued; the pool "+
			"workload is database-wide and the capacity counts are not", m.QueueDepth, d.Queued)
	}
	if m.FreeSlots > float64(d.FreeSlots) {
		log.Printf("note: the fleet advertises %.0f free CloudPool slot(s) and this replica sees %d; a scaler "+
			"pointed at one replica under-counts capacity", m.FreeSlots, d.FreeSlots)
	}
}

// applyScale runs the operator's scale command. The decision travels in the
// environment so the hook is a complete record of the round it acted on.
//
// The command is the operator's own, run through a shell because that is how it
// is configured; every value in its environment is an integer or a fixed word
// this program computed, so there is nothing in it an attacker could steer.
func applyScale(ctx context.Context, cfg appConfig, d Decision) error {
	cmd := exec.CommandContext(ctx, "sh", "-c", cfg.Command)
	cmd.Env = append(os.Environ(), d.Env()...)
	cmd.Stdout, cmd.Stderr = os.Stdout, os.Stderr
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("scale command failed for %d node(s): %w", d.Nodes, err)
	}
	log.Printf("scale command ran: %d node(s)", d.Nodes)
	return nil
}

func intEnv(key string, fallback int) int {
	v := strings.TrimSpace(os.Getenv(key))
	if v == "" {
		return fallback
	}
	n, err := strconv.Atoi(v)
	if err != nil {
		return fallback
	}
	return n
}

func durationEnv(key string, fallback time.Duration) time.Duration {
	v := strings.TrimSpace(os.Getenv(key))
	if v == "" {
		return fallback
	}
	d, err := time.ParseDuration(v)
	if err != nil || d <= 0 {
		return fallback
	}
	return d
}
