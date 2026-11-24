package agent

import (
	"context"
	"os/exec"
	"runtime"
	"strings"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// CommandRunner runs one probe command and returns its combined output. Tests
// inject a fake so the probe is exercised without a container runtime.
type CommandRunner func(ctx context.Context, name string, args ...string) ([]byte, error)

// ProbeOptions are the facts a probe cannot discover for itself.
type ProbeOptions struct {
	// AgentVersion is the worker's own version string.
	AgentVersion string
	// Slots is how many jobs may run at once.
	Slots int
	// FontsetVersion is the version tag of the bundled font set.
	FontsetVersion string
	// MaxOutputBytes is the largest object this runner will publish.
	MaxOutputBytes int64
	// RuntimeImageDigest is the pinned Remotion image digest. Empty means the
	// runner does not have one, and it will not advertise the Remotion job type.
	RuntimeImageDigest string
	// FfmpegPath is the ffmpeg binary to probe. Empty means "ffmpeg".
	FfmpegPath string
	// ContainerCLIs are the runtimes to try, in order. Empty means docker then
	// podman.
	ContainerCLIs []string
	// Run executes a probe command. Nil means a real exec.
	Run CommandRunner
}

// Probe discovers what this machine can do and returns the capability document
// the hello carries.
//
// It is deliberately forgiving: a missing ffmpeg or a missing container runtime
// is not an error, it is fewer job types. A runner that refuses to start
// because a probe failed would be less useful than one that connects, advertises
// what it has and receives only that work.
func Probe(ctx context.Context, opts ProbeOptions) protocol.Capabilities {
	run := opts.Run
	if run == nil {
		run = execRunner
	}
	slots := opts.Slots
	if slots < 1 {
		slots = 1
	}
	maxOutput := opts.MaxOutputBytes
	if maxOutput <= 0 {
		maxOutput = 4 << 30 // 4 GiB: the compile output cap in the specification
	}
	caps := protocol.Capabilities{
		ProtocolVersion: protocol.ProtocolVersion,
		AgentVersion:    opts.AgentVersion,
		OS:              runtime.GOOS,
		Arch:            runtime.GOARCH,
		Slots:           slots,
		Container:       protocol.ContainerNone,
		FontsetVersion:  opts.FontsetVersion,
		MaxOutputBytes:  maxOutput,
		JobTypes:        []string{},
	}

	ffmpeg := opts.FfmpegPath
	if ffmpeg == "" {
		ffmpeg = "ffmpeg"
	}
	if out, err := run(ctx, ffmpeg, "-hide_banner", "-version"); err == nil {
		caps.FfmpegVersion = parseFFmpegVersion(string(out))
	}
	if caps.FfmpegVersion == "" {
		// No usable ffmpeg: the runner can do nothing this protocol asks for, and
		// advertising a job type it cannot run would only fail its own jobs.
		return caps
	}
	caps.JobTypes = append(caps.JobTypes, protocol.JobVideoCompile, protocol.JobVideoAnalyzeEx)

	clis := opts.ContainerCLIs
	if len(clis) == 0 {
		clis = []string{"docker", "podman"}
	}
	for _, cli := range clis {
		out, err := run(ctx, cli, "version")
		if err != nil {
			continue
		}
		kind := classifyRuntime(string(out))
		if kind == "" {
			continue
		}
		caps.Container = kind
		caps.ContainerMode = probeContainerMode(ctx, run, cli)
		if opts.RuntimeImageDigest != "" {
			digest := opts.RuntimeImageDigest
			caps.RuntimeImageDigest = &digest
			// remotion.workspace-session requires a verified container runtime,
			// so it is advertised first only when one was actually found.
			caps.JobTypes = append([]string{protocol.JobRemotionWorkspace}, caps.JobTypes...)
		}
		break
	}
	return caps
}

func execRunner(ctx context.Context, name string, args ...string) ([]byte, error) {
	return exec.CommandContext(ctx, name, args...).CombinedOutput()
}

// classifyRuntime reads the version banner. Podman's docker shim still says
// Podman, and an unrecognised banner is not a runtime.
func classifyRuntime(out string) string {
	lower := strings.ToLower(out)
	switch {
	case strings.Contains(lower, "podman"):
		return protocol.ContainerPodman
	case strings.Contains(lower, "docker"):
		return protocol.ContainerDocker
	default:
		return ""
	}
}

// probeContainerMode is best-effort: the specification only uses it to let
// policy refuse some modes later, so an unknown answer stays empty rather than
// being guessed at.
func probeContainerMode(ctx context.Context, run CommandRunner, cli string) string {
	out, err := run(ctx, cli, "info", "--format", "{{.SecurityOptions}}")
	if err != nil {
		return ""
	}
	lower := strings.ToLower(string(out))
	switch {
	case strings.Contains(lower, "rootless"):
		return "rootless"
	case strings.Contains(lower, "desktop"), strings.Contains(lower, "vm"):
		return "vm"
	case strings.TrimSpace(lower) == "":
		return ""
	default:
		return "rootful"
	}
}

// parseFFmpegVersion extracts "7.1" from "ffmpeg version 7.1 Copyright ...".
func parseFFmpegVersion(out string) string {
	line, _, _ := strings.Cut(out, "\n")
	fields := strings.Fields(line)
	for i, f := range fields {
		if f == "version" && i+1 < len(fields) {
			return fields[i+1]
		}
	}
	return ""
}
