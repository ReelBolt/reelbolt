// Package caps builds the runner's capability document: the
// `hello.capabilities` object defined in docs/runner-protocol.md ("Capabilities")
// and exercised by the fixtures in sandbox/protocol/fixtures/hello.*.json.
//
// Document is an ALIAS of protocol.Capabilities, not a second declaration of the
// same wire shape. D5a created the duplicate and D4b confirmed the two could not
// drift silently (this package's TestDocumentMatchesProtocolFixtures compares
// against the same fixtures the library round-trips), but a conversion at the
// boundary was still the one thing between "two structs that agree" and "one
// struct". The alias removes the conversion: a Document IS the value the agent's
// hello carries.
//
// Inventory carries the extra local facts (ffmpeg encoders, CPU, memory, disk)
// that the desktop UI shows but the protocol does not carry, so adding them never
// touches the wire contract.
package caps

import (
	"context"
	"strings"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/runtime"
)

// Document is the `hello.capabilities` object. Aliased, so the runner and the
// worker-agent library share one declaration of the wire shape.
type Document = protocol.Capabilities

// Protocol constants, re-exported so callers of this package keep one import.
const (
	ProtocolVersion    = protocol.ProtocolVersion
	JobRemotion        = protocol.JobRemotionWorkspace
	JobVideoCompile    = protocol.JobVideoCompile
	JobVideoAnalyzeExt = protocol.JobVideoAnalyzeEx
)

// Inputs are the facts Build turns into a Document.
type Inputs struct {
	AgentVersion   string
	OS, Arch       string // GOOS / GOARCH values
	Slots          int
	FontsetVersion string
	MaxOutputBytes int64

	// FfmpegVersion is empty when ffmpeg is missing or unusable; the runner
	// then advertises no job types at all.
	FfmpegVersion string

	// Runtime is the selected, probed container runtime; nil when none works.
	Runtime *runtime.Report
	// Image is the pinned runtime image reference (name@sha256:...).
	Image string

	// LocalLLM is the shared local model, when the user turned sharing on. Nil
	// or not enabled means the document carries no localLlm at all.
	LocalLLM *protocol.LocalLLMCapabilities
}

// Build produces the capability document. Rules from the protocol:
//   - without a usable container runtime, container is "none" and only the two
//     ffmpeg job types are advertised;
//   - remotion.workspace-session requires a fully verified runtime AND the pinned
//     image (RemotionReady), never a version number alone;
//   - runtimeImageDigest is null unless Remotion is ready.
//
// A missing runtime is not an error.
func Build(in Inputs) Document {
	d := Document{
		ProtocolVersion: ProtocolVersion,
		AgentVersion:    in.AgentVersion,
		OS:              in.OS,
		Arch:            in.Arch,
		Slots:           in.Slots,
		Container:       protocol.ContainerNone,
		FfmpegVersion:   in.FfmpegVersion,
		FontsetVersion:  in.FontsetVersion,
		MaxOutputBytes:  in.MaxOutputBytes,
		JobTypes:        []string{},
	}
	if in.FfmpegVersion != "" {
		d.JobTypes = append(d.JobTypes, JobVideoCompile, JobVideoAnalyzeExt)
	}
	if r := in.Runtime; r != nil && r.Usable {
		d.Container = string(r.Candidate.Kind)
		d.ContainerMode = string(r.Mode)
		if r.RemotionReady {
			if digest := imageDigest(in.Image); digest != "" {
				d.RuntimeImageDigest = &digest
				d.JobTypes = append([]string{JobRemotion}, d.JobTypes...)
			}
		}
	}
	return WithLocalLLM(d, in.LocalLLM)
}

// WithLocalLLM returns the document with hello.capabilities.localLlm set, but
// only for an enabled share: a disabled one is left out entirely, which is what
// "absent means the runner serves no LLM requests" asks for. The block carries
// model names and a server kind, never a URL or credential.
func WithLocalLLM(d Document, l *protocol.LocalLLMCapabilities) Document {
	d.LocalLLM = nil
	if l != nil && l.Enabled {
		cp := *l
		cp.Models = append([]string(nil), l.Models...)
		d.LocalLLM = &cp
	}
	return d
}

func imageDigest(ref string) string {
	_, digest, ok := strings.Cut(ref, "@")
	if !ok || !strings.HasPrefix(digest, "sha256:") {
		return ""
	}
	return digest
}

// Inventory is local-only detail for the UI and diagnostics.
type Inventory struct {
	FFmpeg       FFmpeg `json:"ffmpeg"`
	CPUCores     int    `json:"cpuCores"`
	MemTotal     uint64 `json:"memTotalBytes"`
	DiskFree     uint64 `json:"scratchDiskFreeBytes"`
	RuntimeKind  string `json:"runtimeKind"`
	RuntimeMode  string `json:"runtimeMode,omitempty"`
	RuntimeVer   string `json:"runtimeVersion,omitempty"`
	RuntimeFault string `json:"runtimeFailure,omitempty"`
}

// FFmpeg is what the bundled ffmpeg reports about itself. Hardware encoders
// are listed but not advertised as job-eligible (that is a later capability).
type FFmpeg struct {
	Version          string   `json:"version"`
	Encoders         []string `json:"encoders"`
	HardwareEncoders []string `json:"hardwareEncoders"`
}

// InventoryInputs gathers the values SystemFacts and ProbeFFmpeg produced.
type InventoryInputs struct {
	FFmpeg   FFmpeg
	CPUCores int
	MemTotal uint64
	DiskFree uint64
	Runtime  *runtime.Report
	// Reports, when Runtime is nil, lets the last failure explain why.
	Reports []runtime.Report
}

// BuildInventory assembles an Inventory.
func BuildInventory(in InventoryInputs) Inventory {
	inv := Inventory{
		FFmpeg: in.FFmpeg, CPUCores: in.CPUCores, MemTotal: in.MemTotal, DiskFree: in.DiskFree,
		RuntimeKind: "none",
	}
	if r := in.Runtime; r != nil {
		inv.RuntimeKind, inv.RuntimeMode, inv.RuntimeVer = string(r.Candidate.Kind), string(r.Mode), r.Version
		inv.RuntimeFault = r.Failure
	} else if n := len(in.Reports); n > 0 {
		inv.RuntimeFault = in.Reports[n-1].Failure
	}
	return inv
}

// ProbeFFmpeg runs `ffmpeg -version` and `ffmpeg -encoders` through r. A
// missing or broken ffmpeg yields a zero FFmpeg and no error: the caller
// simply advertises no media jobs.
func ProbeFFmpeg(ctx context.Context, r runtime.Runner, ffmpegPath string) FFmpeg {
	var f FFmpeg
	out, err := r.Run(ctx, nil, ffmpegPath, "-hide_banner", "-version")
	if err != nil {
		return f
	}
	f.Version = ParseFFmpegVersion(string(out))
	if f.Version == "" {
		return FFmpeg{}
	}
	if out, err = r.Run(ctx, nil, ffmpegPath, "-hide_banner", "-encoders"); err == nil {
		f.Encoders, f.HardwareEncoders = ParseEncoders(string(out))
	}
	return f
}

// ParseFFmpegVersion extracts "7.1" from "ffmpeg version 7.1 Copyright ...".
func ParseFFmpegVersion(out string) string {
	line, _, _ := strings.Cut(out, "\n")
	fields := strings.Fields(line)
	for i, f := range fields {
		if f == "version" && i+1 < len(fields) {
			return fields[i+1]
		}
	}
	return ""
}

var hardwareSuffixes = []string{"_nvenc", "_qsv", "_vaapi", "_videotoolbox", "_amf", "_mf", "_v4l2m2m"}

// ParseEncoders reads `ffmpeg -encoders`: after a "------" separator each row
// is "<6 flag chars> <name> <description>". Video and audio encoders are both
// returned; hardware ones are also listed separately.
func ParseEncoders(out string) (all, hardware []string) {
	inTable := false
	for _, line := range strings.Split(out, "\n") {
		line = strings.TrimSpace(line)
		if strings.HasPrefix(line, "------") {
			inTable = true
			continue
		}
		if !inTable {
			continue
		}
		fields := strings.Fields(line)
		if len(fields) < 2 || len(fields[0]) != 6 {
			continue
		}
		name := fields[1]
		all = append(all, name)
		for _, s := range hardwareSuffixes {
			if strings.HasSuffix(name, s) {
				hardware = append(hardware, name)
				break
			}
		}
	}
	return all, hardware
}
