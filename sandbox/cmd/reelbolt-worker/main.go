// Command reelbolt-worker is the compute-plane worker a pool VM runs: it dials
// the runner gateway, authenticates with the pool credential, advertises what
// the machine can actually do, and executes the jobs the gateway assigns to it.
// It is the embedder of sandbox/pkg/agent (D4b) — everything about the protocol,
// the connection, the lease, the op cache and the reconnects lives in that
// library; this binary supplies the configuration, the capability probe, the
// session executors and the process lifecycle.
//
// It is deliberately NOT the sandbox-executor: that process is the self-host
// HTTP control plane, holds no queue and is reachable from the engine. A pool
// worker dials OUT and is reached by nobody. The two share sandbox/pkg/container
// and nothing else.
//
// # Enrollment (the reason this binary exists in the shape it does)
//
// The gateway refuses a pool hello with 4401 unless runner_devices has a row
// with kind='CloudPool' and a matching name. That is deliberate (D5a): a leaked
// pool credential must not be enough to conjure an identity, because a synthetic
// identity cannot be revoked. Nothing enrolled those rows, so the cloud pool was
// unreachable — this binary closes that gap with the "enroll" subcommand, which
// is the only thing here that writes to Postgres and is meant to be run by
// cloud-init (or an operator) before the service starts. The running worker
// never enrolls itself.
//
// # Environment
//
//	REELBOLT_GATEWAY_URL            wss://runner.example/v1/connect  (required)
//	RUNNER_POOL_CREDENTIAL          the pool secret                  (required)
//	RUNNER_POOL_CREDENTIAL_FILE     ...or the file holding it (read only when
//	                                the environment variable is empty)
//	REELBOLT_WORKER_NAME            the name of this worker's enrolled row
//	                                (default: the hostname)
//	DATABASE_URL                    read by "enroll" only; a "run" with it set
//	                                is refused rather than quietly ignored
//	REELBOLT_WORKER_SLOTS           jobs this worker runs at once
//	                                (default: floor(vCPU / SANDBOX_CPU_LIMIT))
//	REELBOLT_WORKER_USE_ALL_CORES   pool nodes: raise ffmpeg -threads and Remotion
//	                                --concurrency to the node's core count
//	                                (SANDBOX_CPU_LIMIT=all and SANDBOX_MEMORY_LIMIT=auto
//	                                give the one job the whole node)
//	REELBOLT_WORKER_CPUS            vCPU count override, for a host or a
//	                                container whose quota Go cannot see
//	REELBOLT_WORKER_SCRATCH         per-job scratch root
//	REELBOLT_WORKER_DRAIN_SECONDS   how long a drain may take (default 120)
//	REELBOLT_WORKER_VERSION         the agentVersion advertised in hello
//	REELBOLT_RUNTIME_IMAGE_DIGEST   the runtime image digest to advertise; empty
//	                                means "not pinned", and the worker then does
//	                                not offer remotion.workspace-session at all
//
// The container driver reads the same SANDBOX_* variables the sandbox-executor
// reads (see sandbox/cmd/sandbox-executor), because it is the same driver:
//
//	SANDBOX_CONTAINER_CLI           docker (default), podman, or "none" to run
//	                                no container at all
//	SANDBOX_IMAGE                   the sandbox-runtime image; in the cloud this
//	                                is the digest from sandbox/runtime-image.lock
//	SANDBOX_ROOT                    where workspaces live
//	SANDBOX_NETWORK                 the --internal network remotion sessions use
//	SANDBOX_CPU_LIMIT, SANDBOX_MEMORY_LIMIT, SANDBOX_PIDS_LIMIT, SANDBOX_TTL
package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"log"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/container"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// Version is stamped at build time (-X main.Version=...). It is what the worker
// advertises as agentVersion, so a rollout can be read off the gateway's own
// runner_devices.runner_version.
var Version = "dev"

func main() {
	command := "run"
	args := os.Args[1:]
	if len(args) > 0 && !isFlag(args[0]) {
		command = args[0]
		args = args[1:]
	}

	switch command {
	case "run":
		if err := runCommand(args); err != nil {
			log.Printf("reelbolt-worker: %v", err)
			os.Exit(exitCodeFor(err))
		}
	case "enroll":
		if err := enrollCommand(args); err != nil {
			log.Printf("reelbolt-worker: %v", err)
			os.Exit(1)
		}
	case "caps":
		if err := capsCommand(args); err != nil {
			log.Printf("reelbolt-worker: %v", err)
			os.Exit(1)
		}
	case "version":
		fmt.Println(Version)
	case "help", "-h", "--help":
		usage(os.Stdout)
	default:
		fmt.Fprintf(os.Stderr, "reelbolt-worker: unknown command %q\n", command)
		usage(os.Stderr)
		os.Exit(2)
	}
}

func usage(w *os.File) {
	fmt.Fprint(w, `reelbolt-worker — the cloud compute-plane worker

  reelbolt-worker [run]     connect to the gateway and serve jobs (default)
  reelbolt-worker enroll    create/refresh this worker's runner_devices row
  reelbolt-worker caps      probe this machine and print its capability document
  reelbolt-worker version   print the build version

"enroll" is the only subcommand that touches Postgres (DATABASE_URL); the
running worker never does. See the package comment for the environment.`)
}

func isFlag(s string) bool { return len(s) > 0 && s[0] == '-' }

// exitCodeFor separates "this machine or identity is wrong, so a restart loop
// will not fix it" from a transient failure. systemd's Restart=on-failure plus
// StartLimitBurst is the backstop for the transient case; a terminal refusal
// exits with a code of its own so the unit's status says which one it was.
func exitCodeFor(err error) int {
	switch {
	case errors.Is(err, agent.ErrUnauthenticated):
		return 4
	case errors.Is(err, agent.ErrUnsupportedVersion):
		return 5
	case errors.Is(err, agent.ErrForbidden):
		return 6
	case errors.Is(err, agent.ErrSuperseded):
		return 7
	case agent.IsTerminal(err):
		return 3
	default:
		return 1
	}
}

// runCommand is the default subcommand: probe, connect, serve, drain, bye.
func runCommand(args []string) error {
	fs := flag.NewFlagSet("run", flag.ContinueOnError)
	gatewayURL := fs.String("gateway", "", "overrides REELBOLT_GATEWAY_URL")
	printCaps := fs.Bool("print-caps", false, "print the capability document and exit")
	if err := fs.Parse(args); err != nil {
		return err
	}

	cfg, err := loadConfig()
	if err != nil {
		return err
	}
	if *gatewayURL != "" {
		cfg.GatewayURL = *gatewayURL
	}
	if err := cfg.validate(); err != nil {
		return err
	}

	log.Printf("reelbolt-worker %s starting as pool worker %q", Version, cfg.WorkerName)
	log.Printf("gateway %s, slots %d (vCPU %.2f / SANDBOX_CPU_LIMIT %s)",
		cfg.GatewayURL, cfg.Slots, cfg.VCPU, cfg.Container.CPULimit)

	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGTERM, syscall.SIGINT)
	defer stop()

	rt, err := newRuntime(ctx, cfg)
	if err != nil {
		return err
	}
	defer rt.Close()

	caps := rt.Capabilities()
	log.Printf("capabilities: container=%s jobTypes=%v ffmpeg=%s digest=%s",
		caps.Container, caps.JobTypes, caps.FfmpegVersion, digestOrNone(caps.RuntimeImageDigest))
	if len(caps.JobTypes) == 0 {
		// Not fatal: the gateway accepts the connection and assigns nothing
		// (D5c counts such a runner as connected-and-useless rather than as
		// capacity). Saying why is what stops this being a mystery.
		log.Printf("WARNING: this worker advertises no job types, so it will never be assigned a job. " +
			"Check that ffmpeg runs here and, for remotion.workspace-session, that SANDBOX_IMAGE is a digest-pinned runtime image.")
	}
	if *printCaps {
		return writeCapabilities(os.Stdout, caps)
	}

	a, err := agent.New(agent.Config{
		GatewayURL: cfg.GatewayURL,
		Identity: agent.Identity{
			Credential: cfg.Credential,
			WorkerName: cfg.WorkerName,
		},
		Capabilities:   caps,
		Sessions:       rt,
		ScratchRoot:    cfg.ScratchRoot,
		DiskQuotaBytes: cfg.DiskQuotaBytes,
	})
	if err != nil {
		return err
	}

	// The drain runs on SIGTERM: a spot reclaim or a scale-in gives the worker a
	// window to stop taking work and say goodbye. Sessions are not waited for —
	// the protocol's rule is that they are rehydrated elsewhere (D7b) — so what
	// the window bounds is the drain, the bye and the close that follow it. The
	// plan's "finish one-shot jobs within 120 s" applies to the reserved
	// one-shot job class D5b keeps a requeue branch for; v1 defines none, so
	// today every held job here is a session.
	finished := make(chan error, 1)
	go func() { finished <- a.Run(ctx) }()

	select {
	case err := <-finished:
		return explainRefusal(err, cfg)
	case <-ctx.Done():
		log.Printf("signal received: draining for up to %s", cfg.DrainTimeout)
		shutdownCtx, cancel := context.WithTimeout(context.Background(), cfg.DrainTimeout)
		defer cancel()
		if err := a.Drain(drainReasonSpotReclaim, cfg.DrainTimeout); err != nil {
			log.Printf("drain: %v", err)
		}
		if err := a.Shutdown(shutdownCtx); err != nil {
			log.Printf("shutdown: %v", err)
		}
		select {
		case err := <-finished:
			log.Printf("agent stopped: %v", err)
		case <-shutdownCtx.Done():
			log.Printf("agent did not stop within the drain window")
		}
		return nil
	}
}

// explainRefusal turns the one refusal an operator will actually meet into the
// sentence that fixes it: the 4401 this WP exists to stop being mysterious.
func explainRefusal(err error, cfg appConfig) error {
	if err == nil {
		return nil
	}
	if errors.Is(err, agent.ErrUnauthenticated) {
		return fmt.Errorf("%w\n"+
			"  A 4401 means the gateway has no usable identity for this worker. Either the pool credential is wrong, "+
			"or runner_devices has no row with kind='CloudPool' AND name=%q (or that row is revoked). Enroll it with "+
			"reelbolt-worker enroll, or let the pool waker create the node",
			err, cfg.WorkerName)
	}
	return err
}

// digestOrNone keeps a nil digest out of a log line.
func digestOrNone(digest *string) string {
	if digest == nil || *digest == "" {
		return "(none)"
	}
	return *digest
}

func writeCapabilities(f *os.File, caps protocol.Capabilities) error {
	enc := json.NewEncoder(f)
	enc.SetIndent("", "  ")
	return enc.Encode(caps)
}

// capsCommand prints what this machine would advertise without connecting. Ops
// use it to answer "why is this worker not taking remotion jobs?" without
// reading the log of a running service.
func capsCommand(args []string) error {
	fs := flag.NewFlagSet("caps", flag.ContinueOnError)
	if err := fs.Parse(args); err != nil {
		return err
	}
	cfg, err := loadConfig()
	if err != nil {
		return err
	}
	rt, err := newRuntime(context.Background(), cfg)
	if err != nil {
		return err
	}
	defer rt.Close()
	return writeCapabilities(os.Stdout, rt.Capabilities())
}

// containerConfig builds the same driver configuration the sandbox-executor
// reads, so one vocabulary describes the driver in both binaries. MaxSandboxes
// follows the worker's slot count: a worker that runs four jobs at once must be
// allowed four workspaces.
func containerConfig(slots int, cpuLimit, memoryLimit string) container.Config {
	return container.Config{
		SandboxImage:   getEnv("SANDBOX_IMAGE", "reelbolt-sandbox-runtime:local"),
		SandboxRoot:    getEnv("SANDBOX_ROOT", "/var/lib/reelbolt/sandboxes"),
		SandboxNetwork: getEnv("SANDBOX_NETWORK", "sandbox-net"),
		NetworkEgress:  getBoolEnv("SANDBOX_NETWORK_EGRESS", false),
		MaxSandboxes:   slots,
		MaxRenderBytes: getInt64Env("SANDBOX_MAX_RENDER_BYTES", container.DefaultMaxRenderBytes),
		SandboxTTL:     getDurationEnv("SANDBOX_TTL", 7*24*time.Hour),
		ExecTimeout:    getDurationEnv("SANDBOX_EXEC_TIMEOUT", 5*time.Minute),
		MaxExecTimeout: getDurationEnv("SANDBOX_EXEC_MAX_TIMEOUT", container.DefaultMaxExecTimeout),
		MemoryLimit:    memoryLimit,
		ShmSize:        getEnv("SANDBOX_SHM_SIZE", container.DefaultShmSize),
		CPULimit:       cpuLimit,
		PIDsLimit:      getIntEnv("SANDBOX_PIDS_LIMIT", 256),
	}
}
