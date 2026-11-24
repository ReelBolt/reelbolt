package runtime

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	goruntime "runtime"
	"strconv"
	"strings"
)

// Mode says how privileged the engine is, so policy can later refuse some
// modes without a client release (ADR section 2).
type Mode string

const (
	ModeRootful  Mode = "rootful"
	ModeRootless Mode = "rootless"
	ModeVM       Mode = "vm"
)

// Minimum engine versions (ADR section 2; Podman raised from 4.6 to 5.0).
const (
	minDockerMajor = 24
	minPodmanMajor = 5
)

// Failure codes reported by Probe. The empty string means success.
const (
	FailUnreachable      = "unreachable"
	FailVersionTooOld    = "version_too_old"
	FailInfo             = "info_failed"
	FailInternalNetwork  = "internal_network_unsupported"
	FailNoImage          = "no_image_configured"
	FailImageMissing     = "image_missing"
	FailArchMismatch     = "arch_mismatch"
	FailImageRun         = "image_run_failed"
	FailEgressPossible   = "egress_possible"
	maxDetailBytes       = 300
	probeMemory          = "128m"
	probeCPUs            = "1"
	probePIDs            = "64"
	egressProbeJS        = `const s=require('net').connect({host:'1.1.1.1',port:443});s.setTimeout(2000);s.on('connect',()=>process.exit(1));s.on('error',()=>process.exit(0));s.on('timeout',()=>process.exit(0));`
	probeNetworkPrefix   = "reelbolt-probe-"
	probeContainerPrefix = "reelbolt-probe-"
)

// ProbeConfig configures a live probe.
type ProbeConfig struct {
	// Image is the pinned runtime image reference (name@sha256:...). Empty
	// means "runtime checks only": Remotion is then never ready.
	Image string
	// HostArch is the machine architecture the image must match (amd64 or
	// arm64). Empty means this process's GOARCH.
	HostArch string
	// NameSuffix makes throwaway resource names deterministic in tests.
	NameSuffix string
}

// Report is the outcome of probing one candidate.
type Report struct {
	Candidate  Candidate
	Version    string
	Mode       Mode
	ServerArch string

	// Usable means the engine answered, met the minimum version and supports
	// an --internal network. ffmpeg capabilities need nothing more.
	Usable bool
	// RemotionReady additionally means the pinned image is present, matches
	// the host architecture, runs under the full flag set, and has no route out.
	RemotionReady bool

	// Failure is the first failed stage (see Fail* constants); Detail carries
	// trimmed command output for the UI.
	Failure string
	Detail  string
}

// Probe verifies one candidate. Failures of the candidate are reported in the
// Report, never as an error; the error is reserved for context cancellation.
func Probe(ctx context.Context, r Runner, c Candidate, cfg ProbeConfig) (Report, error) {
	rep := Report{Candidate: c}
	env := c.hostEnv()
	run := func(args ...string) ([]byte, error) {
		return r.Run(ctx, env, c.Binary, args...)
	}
	fail := func(code string, out []byte, err error) (Report, error) {
		if cerr := ctx.Err(); cerr != nil {
			return rep, cerr
		}
		rep.Failure = code
		rep.Detail = detail(out, err)
		return rep, nil
	}

	// 1. Version, from the server (an unreachable daemon fails here).
	out, err := run("version", "--format", "{{.Server.Version}}")
	if err != nil {
		return fail(FailUnreachable, out, err)
	}
	rep.Version = strings.TrimSpace(string(out))
	if !versionAtLeast(rep.Version, c.Kind) {
		return fail(FailVersionTooOld, []byte("found "+rep.Version), nil)
	}

	// 2. Mode and server architecture.
	out, err = run("info", "--format", "json")
	if err != nil {
		return fail(FailInfo, out, err)
	}
	info, perr := parseInfo(out, c.Kind)
	if perr != nil {
		return fail(FailInfo, out, perr)
	}
	rep.Mode, rep.ServerArch = info.mode, info.arch

	// 3. A real --internal network, created, verified and removed.
	suffix := cfg.NameSuffix
	if suffix == "" {
		suffix = randomSuffix()
	}
	network := probeNetworkPrefix + suffix
	if out, err = run("network", "create", "--internal", network); err != nil {
		return fail(FailInternalNetwork, out, err)
	}
	defer func() {
		// Best effort, and deliberately not tied to ctx so a cancelled probe
		// still cleans up.
		_, _ = r.Run(context.WithoutCancel(ctx), env, c.Binary, "network", "rm", network)
	}()
	out, err = run("network", "inspect", network, "--format", "{{.Internal}}")
	if err != nil || strings.TrimSpace(string(out)) != "true" {
		return fail(FailInternalNetwork, out, err)
	}
	rep.Usable = true

	// 4. The pinned image.
	if cfg.Image == "" {
		rep.Failure = FailNoImage
		return rep, nil
	}
	hostArch := cfg.HostArch
	if hostArch == "" {
		hostArch = goruntime.GOARCH
	}
	if rep.ServerArch != hostArch {
		return fail(FailArchMismatch, []byte("engine "+rep.ServerArch+", host "+hostArch), nil)
	}
	out, err = run("image", "inspect", "--format", "{{.Architecture}}", cfg.Image)
	if err != nil {
		return fail(FailImageMissing, out, err)
	}
	if imgArch := normalizeArch(strings.TrimSpace(string(out))); imgArch != rep.ServerArch {
		return fail(FailArchMismatch, []byte("image "+imgArch+", engine "+rep.ServerArch), nil)
	}

	// 5. Run the image under the full flag set, then check there is no route out.
	name := probeContainerPrefix + suffix
	if out, err = run(runArgs(name, network, cfg.Image, "true")...); err != nil {
		return fail(FailImageRun, out, err)
	}
	if out, err = run(runArgs(name+"-egress", network, cfg.Image, "node", "-e", egressProbeJS)...); err != nil {
		return fail(FailEgressPossible, out, err)
	}

	rep.RemotionReady = true
	return rep, nil
}

// runArgs builds the throwaway container command line. The flag set mirrors
// sandbox/main.go's create (read-only root, capped resources, no capabilities,
// non-root user, internal network) so a probe pass means a real sandbox can run.
func runArgs(name, network, image string, entrypointAndArgs ...string) []string {
	args := []string{
		"run", "--rm", "--name", name,
		"--network", network,
		"--read-only",
		"--tmpfs", "/tmp:rw,nosuid,nodev,size=16m",
		"--tmpfs", "/home/node/.npm:rw,nosuid,nodev,size=16m",
		"--memory", probeMemory,
		"--cpus", probeCPUs,
		"--pids-limit", probePIDs,
		"--security-opt", "no-new-privileges",
		"--cap-drop", "ALL",
		"--user", "node",
		"--entrypoint", entrypointAndArgs[0],
		image,
	}
	return append(args, entrypointAndArgs[1:]...)
}

// Select probes candidates in ADR order and stops at the first usable one. It
// returns every report gathered so far (so the UI can explain each skipped
// candidate) plus the chosen report, which is nil when none is usable.
func Select(ctx context.Context, e Env, cfg ProbeConfig) ([]Report, *Report, error) {
	var reports []Report
	for _, c := range Candidates(ctx, e) {
		rep, err := Probe(ctx, e.Runner, c, cfg)
		if err != nil {
			return reports, nil, err
		}
		reports = append(reports, rep)
		if rep.Usable {
			chosen := reports[len(reports)-1]
			return reports, &chosen, nil
		}
	}
	return reports, nil, nil
}

type infoResult struct {
	mode Mode
	arch string
}

// parseInfo reads both `docker info --format json` and `podman info --format
// json`, which share nothing but the flag.
func parseInfo(raw []byte, kind Kind) (infoResult, error) {
	var v struct {
		// Docker
		Architecture    string   `json:"Architecture"`
		OperatingSystem string   `json:"OperatingSystem"`
		SecurityOptions []string `json:"SecurityOptions"`
		// Podman
		Host struct {
			Arch     string `json:"arch"`
			Security struct {
				Rootless bool `json:"rootless"`
			} `json:"security"`
			ServiceIsRemote bool `json:"serviceIsRemote"`
		} `json:"host"`
	}
	if err := json.Unmarshal(raw, &v); err != nil {
		return infoResult{}, fmt.Errorf("parse info: %w", err)
	}
	if kind == KindPodman {
		res := infoResult{arch: normalizeArch(v.Host.Arch), mode: ModeRootful}
		switch {
		case v.Host.ServiceIsRemote:
			res.mode = ModeVM // a podman machine is always reached remotely
		case v.Host.Security.Rootless:
			res.mode = ModeRootless
		}
		return res, nil
	}
	res := infoResult{arch: normalizeArch(v.Architecture), mode: ModeRootful}
	switch {
	case strings.Contains(v.OperatingSystem, "Docker Desktop"):
		res.mode = ModeVM
	default:
		for _, o := range v.SecurityOptions {
			if strings.Contains(o, "rootless") {
				res.mode = ModeRootless
			}
		}
	}
	return res, nil
}

func normalizeArch(a string) string {
	switch strings.ToLower(a) {
	case "x86_64", "amd64":
		return "amd64"
	case "aarch64", "arm64":
		return "arm64"
	}
	return strings.ToLower(a)
}

func versionAtLeast(v string, kind Kind) bool {
	major, _, ok := parseVersion(v)
	if !ok {
		return false
	}
	if kind == KindPodman {
		return major >= minPodmanMajor
	}
	return major >= minDockerMajor
}

func parseVersion(v string) (major, minor int, ok bool) {
	v = strings.TrimPrefix(strings.TrimSpace(v), "v")
	parts := strings.SplitN(v, ".", 3)
	if len(parts) < 2 {
		return 0, 0, false
	}
	major, err := strconv.Atoi(parts[0])
	if err != nil {
		return 0, 0, false
	}
	minor, err = strconv.Atoi(leadingDigits(parts[1]))
	if err != nil {
		return 0, 0, false
	}
	return major, minor, true
}

func leadingDigits(s string) string {
	i := 0
	for i < len(s) && s[i] >= '0' && s[i] <= '9' {
		i++
	}
	return s[:i]
}

func detail(out []byte, err error) string {
	s := strings.TrimSpace(string(out))
	if s == "" && err != nil {
		s = err.Error()
	}
	if len(s) > maxDetailBytes {
		s = s[:maxDetailBytes]
	}
	return s
}

func randomSuffix() string {
	b := make([]byte, 4)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}
