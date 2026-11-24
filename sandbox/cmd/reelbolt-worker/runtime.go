package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"os/exec"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent/media"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent/remotion"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/container"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// probeTimeout bounds one probe command. A container-CLI version call on a
// wedged daemon blocks forever, and a worker that never connects because a
// probe hung is a worker an operator cannot diagnose.
const probeTimeout = 20 * time.Second

// runtimeState is this machine's executors: what it can do (the capability
// document the hello carries) and how it does it (one executor per job type).
//
// It is the agent's Factory, so the library asks it for a session per job and
// this file decides which executor that is. The two media job types share one
// executor because they share one op surface; remotion has its own because it is
// the only one that needs a container the model's code runs inside.
type runtimeState struct {
	cfg      appConfig
	caps     protocol.Capabilities
	driver   *container.Driver
	remotion *remotion.Executor
	media    *media.Executor
}

// newRuntime probes the machine and builds the executors.
//
// Every failure here is degraded rather than fatal: an executor is only built
// for a job type the capability document actually advertises, so a worker can
// never be assigned work whose executor would refuse it. That is the D4b
// philosophy (a missing ffmpeg is not an error, it is fewer job types) applied
// to the binary.
func newRuntime(ctx context.Context, cfg appConfig) (*runtimeState, error) {
	r := &runtimeState{cfg: cfg}

	cli := cfg.ContainerCLI
	var driver *container.Driver
	remotionUsable := false
	if cli != "" {
		driver = container.New(cfg.Container, cli)
		if kind, err := driver.DetectKind(ctx); err != nil || kind == container.KindUnknown {
			log.Printf("container runtime %q is not usable (%v): this worker will advertise only the ffmpeg job types", cli, err)
			driver, cli = nil, ""
		} else {
			log.Printf("container runtime: %s", kind)
			// The remotion workspace attaches to an --internal network.
			// Creating it here rather than assuming compose did: a pool VM is
			// not a compose stack, and a missing network would fail EVERY
			// remotion job at container-create time with an error that does not
			// name the network. A failure degrades this worker to the ffmpeg
			// job types, which is a truthful statement about what it can run.
			if err := driver.EnsureNetwork(); err != nil {
				log.Printf("ERROR: %v", err)
				log.Printf("remotion.workspace-session will NOT be advertised by this worker")
			} else {
				remotionUsable = true
			}
			// Leftover containers and workspaces from a previous worker process
			// on this host. One worker per VM is the deployment rule the plan
			// states; on a host shared by two, this would remove the other's.
			driver.PurgeExisting()
		}
	}

	digest := cfg.RuntimeImageDigest
	if !remotionUsable {
		digest = ""
	}
	r.caps = agent.Probe(ctx, agent.ProbeOptions{
		AgentVersion:       cfg.AgentVersion,
		Slots:              cfg.Slots,
		RuntimeImageDigest: digest,
		MaxOutputBytes:     getInt64Env("REELBOLT_WORKER_MAX_OUTPUT_BYTES", 4<<30),
		FfmpegPath:         getEnv("REELBOLT_WORKER_FFMPEG", "ffmpeg"),
		ContainerCLIs:      containerCLIs(cli),
		Run:                probeRunner,
	})
	if digest != "" && !hasJobType(r.caps, protocol.JobRemotionWorkspace) {
		log.Printf("WARNING: runtime digest %s is pinned but remotion.workspace-session was not advertised; check that %q is present on this host",
			digest, cfg.Container.SandboxImage)
	}

	r.driver = driver
	if hasJobType(r.caps, protocol.JobRemotionWorkspace) {
		exec, err := remotion.New(remotion.Config{Driver: driver, QuotaBytes: cfg.DiskQuotaBytes, AllCores: cfg.AllCores})
		if err != nil {
			return nil, fmt.Errorf("build the remotion executor: %w", err)
		}
		r.remotion = exec
	}

	mediaCfg := media.Config{QuotaBytes: cfg.DiskQuotaBytes, AllCores: cfg.AllCores}
	if driver != nil {
		// ffmpeg runs inside the runtime image when there is one: the container
		// is where the argv validator's guarantees are enforced by the kernel as
		// well (no network, a read-only root, the session directory as the only
		// writable mount). Without a runtime it is a host process, which is what
		// a desktop runner without Docker does.
		mediaCfg.Container = &media.ContainerRunner{Binary: driver.Binary, Image: cfg.Container.SandboxImage}
	}
	mediaExec, err := media.New(mediaCfg)
	if err != nil {
		return nil, fmt.Errorf("build the media executor: %w", err)
	}
	r.media = mediaExec
	return r, nil
}

// containerCLIs is what the probe should try. A worker configured with one CLI
// must not silently fall back to another: an operator who set
// SANDBOX_CONTAINER_CLI=podman does not want docker used behind their back.
func containerCLIs(configured string) []string {
	if configured == "" {
		return nil
	}
	return []string{configured}
}

// Capabilities returns the document the hello carries.
func (r *runtimeState) Capabilities() protocol.Capabilities { return r.caps }

// Close releases the host-side state: any workspace container a session left
// behind. Sessions delete their own on close; this is the backstop for a
// shutdown that happened mid-job.
func (r *runtimeState) Close() {
	if r.driver != nil {
		r.driver.PurgeExisting()
	}
}

// New implements agent.Factory: one executor per job type, and a refusal with a
// stable code for a type this worker did not advertise. The gateway should never
// send one — it filters by the capability document — so this branch is a bug
// catcher rather than a routing path.
func (r *runtimeState) New(ctx context.Context, job *agent.Job) (agent.Session, error) {
	switch job.JobType {
	case protocol.JobRemotionWorkspace:
		if r.remotion == nil {
			return nil, &agent.JobError{
				Class:     protocol.FailRunner,
				Code:      protocol.ErrCodeContainerUnavailable,
				Message:   "this worker has no usable container runtime, so it cannot run a remotion workspace session",
				Retryable: true,
			}
		}
		return r.remotion.New(ctx, job)
	case protocol.JobVideoCompile, protocol.JobVideoAnalyzeEx:
		return r.media.New(ctx, job)
	default:
		return nil, &agent.JobError{
			Class:   protocol.FailInput,
			Code:    protocol.ErrCodeUnsupportedOp,
			Message: "this worker does not run " + job.JobType,
		}
	}
}

func hasJobType(caps protocol.Capabilities, jobType string) bool {
	for _, t := range caps.JobTypes {
		if t == jobType {
			return true
		}
	}
	return false
}

// probeRunner runs one probe command with a deadline of its own.
func probeRunner(ctx context.Context, name string, args ...string) ([]byte, error) {
	return runProbe(ctx, name, args...)
}

// runProbe executes one command with the probe deadline. It is used by the
// capability probe and by the image-digest lookup.
func runProbe(ctx context.Context, name string, args ...string) ([]byte, error) {
	ctx, cancel := context.WithTimeout(ctx, probeTimeout)
	defer cancel()
	return exec.CommandContext(ctx, name, args...).CombinedOutput()
}

// marshalCapabilities renders a capability document for a test failure message.
// It never fails the caller: a document that cannot be marshalled is a bug, not
// a reason to refuse to start.
func marshalCapabilities(caps protocol.Capabilities) string {
	out, err := json.Marshal(caps)
	if err != nil {
		return "<unencodable capability document>"
	}
	return string(out)
}
