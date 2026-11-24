package main

import (
	"context"
	"errors"
	"fmt"
	"os"
	"runtime"
	"strconv"
	"strings"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/container"
)

// drainReasonSpotReclaim is the reason this worker sends in drain when it is
// stopping on a signal. The protocol leaves the string open ("user pause or spot
// reclaim" are its own examples) so the gateway's log says which one it was.
const drainReasonSpotReclaim = "spot_reclaim"

// appConfig is the worker's configuration. Nothing here has a fallback that
// could connect with the wrong identity: the gateway URL, the pool credential
// and the worker name are all required, and a missing one is fatal at startup
// rather than on every reconnect.
type appConfig struct {
	// GatewayURL is the wss:// (or private-network ws://) address of the runner gateway.
	GatewayURL string
	// WorkerName is the name of this worker's runner_devices row
	// (kind='CloudPool'). The gateway matches it exactly, so a VM whose
	// hostname changed is a worker that has silently lost its identity.
	WorkerName string
	// Credential is the pool secret. It is never logged.
	Credential string
	// DatabaseURL is read by "enroll" only.
	DatabaseURL string

	// VCPU is how many CPUs this worker believes it has, and CPULimit the
	// per-container limit, so slots can be floor(vCPU / CPU_LIMIT).
	VCPU         float64
	CPULimit     string
	Slots        int
	SlotsFromEnv bool
	// AllCores is the node's whole-core count when REELBOLT_WORKER_USE_ALL_CORES is set (a pool
	// node), else 0: ffmpeg threads and Remotion concurrency are raised to it per
	// agent.RaiseFFmpegThreads / agent.RaiseRemotionConcurrency.
	AllCores int

	// Container is the driver's configuration; ContainerCLI is the binary.
	Container    container.Config
	ContainerCLI string

	ScratchRoot    string
	DiskQuotaBytes int64
	DrainTimeout   time.Duration
	ProbeTimeout   time.Duration
	AgentVersion   string
	// RuntimeImageDigest is what hello advertises; empty means "no pinned
	// runtime image", and the worker then does not advertise the Remotion job
	// type it could not run reproducibly.
	RuntimeImageDigest string
}

// hardMaxSlots bounds what a machine may claim. A worker that advertises more
// concurrency than it can run does not fail loudly — it accepts that many jobs
// and makes all of them slow — so the cap is here rather than in a comment.
const hardMaxSlots = 64

// loadConfig reads the environment. It does not validate the identity (that is
// validate's job) because "caps" and "enroll" have different requirements.
func loadConfig() (appConfig, error) {
	vcpu, err := resolveVCPU()
	if err != nil {
		return appConfig{}, err
	}
	cpuLimit := resolveCPULimit(getEnv("SANDBOX_CPU_LIMIT", "2"), vcpu)
	memLimit := resolveMemoryLimit(getEnv("SANDBOX_MEMORY_LIMIT", "2g"), hostMemoryBytes())
	slots, fromEnv := slotsFromEnv(vcpu, cpuLimit)

	cfg := appConfig{
		GatewayURL:   strings.TrimSpace(os.Getenv("REELBOLT_GATEWAY_URL")),
		WorkerName:   strings.TrimSpace(os.Getenv("REELBOLT_WORKER_NAME")),
		Credential:   strings.TrimSpace(os.Getenv("RUNNER_POOL_CREDENTIAL")),
		DatabaseURL:  os.Getenv("DATABASE_URL"),
		VCPU:         vcpu,
		CPULimit:     cpuLimit,
		Slots:        slots,
		SlotsFromEnv: fromEnv,
		AllCores:     allCores(vcpu),
		Container:    containerConfig(slots, cpuLimit, memLimit),
		ContainerCLI: strings.TrimSpace(getEnv("SANDBOX_CONTAINER_CLI", "docker")),

		ScratchRoot:    getEnv("REELBOLT_WORKER_SCRATCH", "/var/lib/reelbolt/worker"),
		DiskQuotaBytes: getInt64Env("REELBOLT_WORKER_DISK_QUOTA_BYTES", 8<<30),
		DrainTimeout:   time.Duration(getIntEnv("REELBOLT_WORKER_DRAIN_SECONDS", 120)) * time.Second,
		ProbeTimeout:   time.Duration(getIntEnv("REELBOLT_WORKER_PROBE_TIMEOUT_SECONDS", 20)) * time.Second,
		AgentVersion:   getEnv("REELBOLT_WORKER_VERSION", Version),
	}
	if cfg.WorkerName == "" {
		if host, err := os.Hostname(); err == nil {
			cfg.WorkerName = host
		}
	}
	if cfg.ContainerCLI == "none" {
		// An explicit "no container runtime on this machine": the worker
		// advertises only the ffmpeg job types and runs them as host processes.
		cfg.ContainerCLI = ""
	}
	if cfg.Credential == "" {
		if path := strings.TrimSpace(os.Getenv("RUNNER_POOL_CREDENTIAL_FILE")); path != "" {
			data, err := os.ReadFile(path)
			if err != nil {
				return appConfig{}, fmt.Errorf("read RUNNER_POOL_CREDENTIAL_FILE %s: %w", path, err)
			}
			cfg.Credential = strings.TrimSpace(string(data))
		}
	}
	cfg.RuntimeImageDigest = resolveRuntimeImageDigest(cfg)
	return cfg, nil
}

// validate refuses a configuration that cannot connect. Only "run" calls it:
// "caps" is useful on a machine that has no credential yet, which is exactly
// the machine an operator runs it on.
func (c appConfig) validate() error {
	if c.GatewayURL == "" {
		return errors.New("REELBOLT_GATEWAY_URL is required (wss://host/v1/connect)")
	}
	if c.Credential == "" {
		return errors.New("RUNNER_POOL_CREDENTIAL (or RUNNER_POOL_CREDENTIAL_FILE) is required: the gateway accepts no pool worker without one")
	}
	if c.WorkerName == "" {
		return errors.New("REELBOLT_WORKER_NAME is required: the hostname could not be read")
	}
	if c.Slots < 1 {
		return errors.New("this worker computed 0 slots; set REELBOLT_WORKER_SLOTS or raise REELBOLT_WORKER_CPUS")
	}
	if c.DatabaseURL != "" {
		// Reading it here rather than ignoring it: a worker started with the
		// database URL of the platform is an operator mistake worth failing on.
		return errors.New("DATABASE_URL is set but the running worker must not hold database credentials; " +
			"it is read by \"reelbolt-worker enroll\" only. Remove it from the service environment and keep it in the enroll step")
	}
	return nil
}

// slotsFromEnv reads REELBOLT_WORKER_SLOTS, or derives the slot count from the
// machine: floor(vCPU / SANDBOX_CPU_LIMIT), never below 1 and never above
// hardMaxSlots.
func slotsFromEnv(vcpu float64, cpuLimit string) (int, bool) {
	if raw := strings.TrimSpace(os.Getenv("REELBOLT_WORKER_SLOTS")); raw != "" {
		if n, err := strconv.Atoi(raw); err == nil && n > 0 {
			return clampSlots(n), true
		}
	}
	return SlotsFor(vcpu, cpuLimit), false
}

// SlotsFor is the plan's rule, floor(vCPU / SANDBOX_CPU_LIMIT), as a pure
// function so it can be tested without a machine that has that many CPUs.
//
// An unparseable limit falls back to 1 CPU rather than to zero slots: a typo in
// SANDBOX_CPU_LIMIT must not turn a worker into one that advertises no capacity.
func SlotsFor(vcpu float64, cpuLimit string) int {
	limit, err := strconv.ParseFloat(strings.TrimSpace(cpuLimit), 64)
	if err != nil || limit <= 0 {
		limit = 1
	}
	if vcpu <= 0 {
		vcpu = 1
	}
	return clampSlots(int(vcpu / limit))
}

func clampSlots(n int) int {
	if n < 1 {
		return 1
	}
	if n > hardMaxSlots {
		return hardMaxSlots
	}
	return n
}

// resolveVCPU determines the CPU count this worker should divide by the
// container limit. REELBOLT_WORKER_CPUS wins, then a cgroup CPU quota, then the
// host's CPU count.
//
// The cgroup step is why this is not simply runtime.NumCPU(): a worker shipped
// as a container with --cpus=4 on a 64-core VM must advertise 4 slots, not 32.
// GOMAXPROCS is not a substitute — it is floored at 1 and rounded up, and it
// describes the Go scheduler rather than the machine.
func resolveVCPU() (float64, error) {
	if raw := strings.TrimSpace(os.Getenv("REELBOLT_WORKER_CPUS")); raw != "" {
		n, err := strconv.ParseFloat(raw, 64)
		if err != nil || n <= 0 {
			return 0, fmt.Errorf("REELBOLT_WORKER_CPUS=%q is not a positive number", raw)
		}
		return n, nil
	}
	if quota, ok := cgroupCPUQuota("/sys/fs/cgroup/cpu.max"); ok {
		return quota, nil
	}
	return float64(runtime.NumCPU()), nil
}

// cgroupCPUQuota reads a cgroup v2 cpu.max file ("<quota> <period>" or
// "max <period>") and returns the quota expressed in CPUs.
func cgroupCPUQuota(path string) (float64, bool) {
	data, err := os.ReadFile(path)
	if err != nil {
		return 0, false
	}
	return parseCPUMax(string(data))
}

// parseCPUMax is the pure half of cgroupCPUQuota. An unlimited cgroup ("max")
// reports false, so the caller falls back to the host's CPU count.
func parseCPUMax(s string) (float64, bool) {
	fields := strings.Fields(strings.TrimSpace(s))
	if len(fields) != 2 {
		return 0, false
	}
	if fields[0] == "max" {
		return 0, false
	}
	quota, err := strconv.ParseFloat(fields[0], 64)
	if err != nil || quota <= 0 {
		return 0, false
	}
	period, err := strconv.ParseFloat(fields[1], 64)
	if err != nil || period <= 0 {
		return 0, false
	}
	return quota / period, true
}

// resolveRuntimeImageDigest decides what hello advertises as
// runtimeImageDigest. REELBOLT_RUNTIME_IMAGE_DIGEST wins; then a digest already
// pinned into SANDBOX_IMAGE; then the image's own RepoDigest from the runtime.
//
// Empty is a legitimate answer — a locally built image genuinely has no digest
// — and it means the worker does not advertise remotion.workspace-session,
// because a render whose runtime image is not pinned is a render whose result
// this fleet cannot reproduce.
func resolveRuntimeImageDigest(cfg appConfig) string {
	if d := strings.TrimSpace(os.Getenv("REELBOLT_RUNTIME_IMAGE_DIGEST")); d != "" {
		return d
	}
	image := cfg.Container.SandboxImage
	if at := strings.LastIndex(image, "@sha256:"); at >= 0 {
		return image[at+1:]
	}
	if cfg.ContainerCLI == "" || image == "" {
		return ""
	}
	out, err := runProbe(context.Background(), cfg.ContainerCLI, "image", "inspect", image, "--format", "{{index .RepoDigests 0}}")
	if err != nil {
		return ""
	}
	digest := strings.TrimSpace(string(out))
	if at := strings.LastIndex(digest, "@"); at >= 0 {
		digest = digest[at+1:]
	}
	if digest == "<no value>" || digest == "" {
		return ""
	}
	return digest
}

func getEnv(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

func getIntEnv(key string, fallback int) int {
	v := os.Getenv(key)
	if v == "" {
		return fallback
	}
	n, err := strconv.Atoi(v)
	if err != nil || n <= 0 {
		return fallback
	}
	return n
}

func getInt64Env(key string, fallback int64) int64 {
	v := os.Getenv(key)
	if v == "" {
		return fallback
	}
	n, err := strconv.ParseInt(v, 10, 64)
	if err != nil || n <= 0 {
		return fallback
	}
	return n
}

func getBoolEnv(key string, fallback bool) bool {
	v := strings.TrimSpace(os.Getenv(key))
	if v == "" {
		return fallback
	}
	b, err := strconv.ParseBool(v)
	if err != nil {
		return fallback
	}
	return b
}

func getDurationEnv(key string, fallback time.Duration) time.Duration {
	v := os.Getenv(key)
	if v == "" {
		return fallback
	}
	d, err := time.ParseDuration(v)
	if err != nil || d <= 0 {
		return fallback
	}
	return d
}

// allCores returns the whole-core count when REELBOLT_WORKER_USE_ALL_CORES is true, else 0.
func allCores(vcpu float64) int {
	if !getBoolEnv("REELBOLT_WORKER_USE_ALL_CORES", false) {
		return 0
	}
	if vcpu < 1 {
		return 1
	}
	return int(vcpu)
}

// resolveCPULimit turns SANDBOX_CPU_LIMIT=all into the machine's whole-core count, so one
// container may use every core and the slot count is 1. Any other value is returned as is.
func resolveCPULimit(raw string, vcpu float64) string {
	if !strings.EqualFold(strings.TrimSpace(raw), "all") {
		return raw
	}
	n := int(vcpu)
	if n < 1 {
		n = 1
	}
	return strconv.Itoa(n)
}

// resolveMemoryLimit turns SANDBOX_MEMORY_LIMIT=auto into three quarters of the machine's memory
// (the rest is for the OS, Docker and the worker). When the memory cannot be read it keeps the
// 2 GiB default rather than guess.
func resolveMemoryLimit(raw string, totalBytes int64) string {
	if !strings.EqualFold(strings.TrimSpace(raw), "auto") {
		return raw
	}
	mib := totalBytes / (1 << 20) * 3 / 4
	if mib < 512 {
		return "2g"
	}
	return strconv.FormatInt(mib, 10) + "m"
}

// hostMemoryBytes reads MemTotal from /proc/meminfo; 0 when unknown.
func hostMemoryBytes() int64 {
	data, err := os.ReadFile("/proc/meminfo")
	if err != nil {
		return 0
	}
	return parseMemTotal(string(data))
}

func parseMemTotal(meminfo string) int64 {
	for _, line := range strings.Split(meminfo, "\n") {
		f := strings.Fields(line)
		if len(f) >= 2 && f[0] == "MemTotal:" {
			kb, err := strconv.ParseInt(f[1], 10, 64)
			if err != nil || kb <= 0 {
				return 0
			}
			return kb * 1024
		}
	}
	return 0
}
