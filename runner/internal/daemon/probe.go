package daemon

import (
	"context"
	goruntime "runtime"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/caps"
	"github.com/vecchiotom/reelbolt/runner/internal/runtime"
	"github.com/vecchiotom/reelbolt/runner/internal/runtimeimage"
)

// Defaults for the facts a probe cannot discover for itself.
const (
	// DefaultMaxOutputBytes is the largest object a runner will publish (the
	// protocol's compile output cap).
	DefaultMaxOutputBytes int64 = 4 << 30
	// DefaultFfmpegPath and DefaultFFprobePath are the media binaries.
	DefaultFfmpegPath  = "ffmpeg"
	DefaultFFprobePath = "ffprobe"
	// RuntimeImageEnv is the environment override for the runtime image. It is
	// consulted only when nothing is pinned and nothing is configured.
	RuntimeImageEnv = "REELBOLT_RUNTIME_IMAGE"
)

// ProbeRequest is what the daemon asks a prober to survey.
type ProbeRequest struct {
	Version        string
	FontsetVersion string
	Slots          int
	// Image overrides the pinned runtime image. Empty means pinned, then the
	// environment, then the development default.
	Image string
	// ContainerCLI pins the container CLI. Empty means the E3 detection order.
	ContainerCLI string
	FfmpegPath   string
	ScratchRoot  string
}

// ProbeResult is the whole picture the daemon starts from: the capability
// document the hello carries, the local inventory the UI shows, the image that
// was resolved and every runtime candidate that was tried.
type ProbeResult struct {
	Capabilities protocol.Capabilities
	Inventory    caps.Inventory
	Image        runtimeimage.Source
	Runtime      *runtime.Report
	Reports      []runtime.Report
	ContainerCLI string
}

// RemotionReady reports whether this runner can advertise and run
// remotion.workspace-session.
func (r ProbeResult) RemotionReady() bool {
	return r.Runtime != nil && r.Runtime.RemotionReady && r.Capabilities.Supports(protocol.JobRemotionWorkspace)
}

// Prober surveys the machine. It is an interface so the daemon's tests can
// describe a machine instead of probing the real one.
type Prober interface {
	Probe(ctx context.Context, req ProbeRequest) (ProbeResult, error)
}

// SystemProber is the production prober: it drives E3's runtime detection and
// ffmpeg probe.
//
// The runtime image is resolved in exactly one place (runtimeimage.Resolve) and
// the digest the capability document advertises is the digest of that same
// reference, so a runner can never advertise an image it is not running: the
// gateway matches requires.runtimeImageDigest against capabilities, and a
// mismatch means no Remotion work at all.
type SystemProber struct {
	// Env is E3's environment seam. Zero means the real OS environment.
	Env runtime.Env
	// Facts reports total memory and free disk for the inventory. Nil means
	// caps.SystemFacts.
	Facts func(dir string) (memTotal, diskFree uint64)
	// NumCPU reports the core count. Nil means runtime.NumCPU.
	NumCPU func() int
	// Getenv reads the environment. Nil means os.Getenv.
	Getenv func(string) string
	// Log reports what the probe found. Nil means nothing is logged.
	Log func(format string, args ...any)
}

// Probe implements Prober.
func (p *SystemProber) Probe(ctx context.Context, req ProbeRequest) (ProbeResult, error) {
	env := p.Env
	if env.Runner == nil {
		env = runtime.OSEnv()
	}
	image := runtimeimage.Resolve(req.Image, p.getenv(RuntimeImageEnv))

	cfg := runtime.ProbeConfig{Image: image.Ref}
	var reports []runtime.Report
	var chosen *runtime.Report
	for _, c := range listCandidates(ctx, env, req.ContainerCLI) {
		rep, err := runtime.Probe(ctx, env.Runner, c, cfg)
		if err != nil {
			// Probe returns an error only for cancellation; candidates that do
			// not work are reported in the Report.
			return ProbeResult{Image: image, Reports: reports}, err
		}
		reports = append(reports, rep)
		if rep.Usable {
			chosen = &reports[len(reports)-1]
			break
		}
	}

	ffmpegPath := req.FfmpegPath
	if ffmpegPath == "" {
		ffmpegPath = DefaultFfmpegPath
	}
	ff := caps.ProbeFFmpeg(ctx, env.Runner, ffmpegPath)

	slots := req.Slots
	if slots < 1 {
		slots = 1
	}
	containerCLI := ""
	if chosen != nil {
		containerCLI = chosen.Candidate.Binary
	}
	result := ProbeResult{
		Image:   image,
		Runtime: chosen,
		Reports: reports,
		Capabilities: caps.Build(caps.Inputs{
			AgentVersion:   req.Version,
			OS:             goruntime.GOOS,
			Arch:           goruntime.GOARCH,
			Slots:          slots,
			FontsetVersion: req.FontsetVersion,
			MaxOutputBytes: DefaultMaxOutputBytes,
			FfmpegVersion:  ff.Version,
			Runtime:        chosen,
			Image:          image.Ref,
		}),
		ContainerCLI: containerCLI,
	}
	mem, disk := p.facts(req.ScratchRoot)
	result.Inventory = caps.BuildInventory(caps.InventoryInputs{
		FFmpeg:   ff,
		CPUCores: p.numCPU(),
		MemTotal: mem,
		DiskFree: disk,
		Runtime:  chosen,
		Reports:  reports,
	})
	p.logf("runtime image %s (%s); container runtime %s", image.Ref, image.Origin,
		describeRuntime(chosen))
	return result, nil
}

// listCandidates applies the pinned-CLI filter before anything is probed, so a
// runner told to use podman never even tests docker.
func listCandidates(ctx context.Context, env runtime.Env, pinned string) []runtime.Candidate {
	cands := runtime.Candidates(ctx, env)
	if pinned == "" {
		return cands
	}
	out := make([]runtime.Candidate, 0, len(cands))
	for _, c := range cands {
		if string(c.Kind) == pinned {
			out = append(out, c)
		}
	}
	return out
}

func describeRuntime(rep *runtime.Report) string {
	if rep == nil {
		return "none (ffmpeg-only)"
	}
	return string(rep.Candidate.Kind) + " " + rep.Version + " (" + string(rep.Mode) + ")"
}

func (p *SystemProber) facts(dir string) (uint64, uint64) {
	if p.Facts != nil {
		return p.Facts(dir)
	}
	return caps.SystemFacts(dir)
}

func (p *SystemProber) numCPU() int {
	if p.NumCPU != nil {
		return p.NumCPU()
	}
	return goruntime.NumCPU()
}

func (p *SystemProber) getenv(key string) string {
	if p.Getenv != nil {
		return p.Getenv(key)
	}
	return getenv(key)
}

func (p *SystemProber) logf(format string, args ...any) {
	if p.Log != nil {
		p.Log(format, args...)
	}
}
