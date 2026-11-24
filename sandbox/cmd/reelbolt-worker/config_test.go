package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/container"
)

// TestSlotsFor is the plan's rule — slots = floor(vCPU / SANDBOX_CPU_LIMIT) —
// checked at its edges, because the number decides how many renders a worker
// admits at once and a slot count that is too high makes every one of them slow
// rather than failing loudly.
func TestSlotsFor(t *testing.T) {
	cases := []struct {
		name  string
		vcpu  float64
		limit string
		want  int
	}{
		{"8 vCPU at 2 each", 8, "2", 4},
		{"8 vCPU at 2.5 each", 8, "2.5", 3},
		{"4 vCPU at 2 each", 4, "2", 2},
		{"2 vCPU at 4 each", 2, "4", 1},
		{"1 vCPU at 1 each", 1, "1", 1},
		{"a fractional remainders to at least one", 3, "2", 1},
		{"an unparseable limit falls back to one CPU", 8, "two", 8},
		{"an empty limit falls back to one CPU", 8, "", 8},
		{"a zero limit falls back to one CPU", 8, "0", 8},
		{"a negative limit falls back to one CPU", 8, "-2", 8},
		{"unknown vCPU still yields one slot", 0, "2", 1},
		{"a negative vCPU still yields one slot", -4, "2", 1},
		{"a huge machine is clamped", 4096, "1", hardMaxSlots},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := SlotsFor(tc.vcpu, tc.limit); got != tc.want {
				t.Fatalf("SlotsFor(%v, %q) = %d, want %d", tc.vcpu, tc.limit, got, tc.want)
			}
		})
	}
}

// TestParseCPUMax reads cgroup v2's cpu.max, which is how a worker shipped as a
// container with --cpus=4 on a 64-core VM reports 4 rather than 64.
func TestParseCPUMax(t *testing.T) {
	cases := []struct {
		name string
		in   string
		want float64
		ok   bool
	}{
		{"quota and period", "200000 100000", 2, true},
		{"fractional quota", "250000 100000", 2.5, true},
		{"unlimited", "max 100000", 0, false},
		{"missing period", "200000", 0, false},
		{"garbage", "not-a-number 100000", 0, false},
		{"zero period", "200000 0", 0, false},
		{"empty", "", 0, false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, ok := parseCPUMax(tc.in)
			if ok != tc.ok {
				t.Fatalf("parseCPUMax(%q) ok = %v, want %v", tc.in, ok, tc.ok)
			}
			if ok && got != tc.want {
				t.Fatalf("parseCPUMax(%q) = %v, want %v", tc.in, got, tc.want)
			}
		})
	}
}

// TestValidateRefusesADatabaseURLOnRun is the sharp edge worth a test: this
// binary is meant to hold no database credential while it serves jobs, and an
// operator who pasted the code-init environment into the service would otherwise
// never find out.
func TestValidateRefusesADatabaseURLOnRun(t *testing.T) {
	cfg := appConfig{
		GatewayURL:   "wss://runner.example/v1/connect",
		WorkerName:   "pool-1",
		Credential:   "secret",
		DatabaseURL:  "postgres://postgres:postgres@127.0.0.1:5432/postgres",
		Slots:        2,
		ContainerCLI: "docker",
	}
	err := cfg.validate()
	if err == nil {
		t.Fatal("a run configuration carrying DATABASE_URL was accepted")
	}
	if !strings.Contains(err.Error(), "enroll") {
		t.Fatalf("the refusal does not point at enroll: %v", err)
	}
	cfg.DatabaseURL = ""
	if err := cfg.validate(); err != nil {
		t.Fatalf("a valid configuration was refused: %v", err)
	}
}

func TestValidateRequiresTheIdentity(t *testing.T) {
	base := appConfig{GatewayURL: "wss://gw/v1/connect", WorkerName: "pool-1", Credential: "c", Slots: 1}
	for _, tc := range []struct {
		name string
		drop func(*appConfig)
	}{
		{"gateway url", func(c *appConfig) { c.GatewayURL = "" }},
		{"pool credential", func(c *appConfig) { c.Credential = "" }},
		{"worker name", func(c *appConfig) { c.WorkerName = "" }},
		{"slots", func(c *appConfig) { c.Slots = 0 }},
	} {
		t.Run(tc.name, func(t *testing.T) {
			cfg := base
			tc.drop(&cfg)
			if err := cfg.validate(); err == nil {
				t.Fatalf("a configuration with no %s was accepted", tc.name)
			}
		})
	}
}

// TestLoadConfigReadsTheCredentialFromAFile covers the mounted-secret shape: a
// pool VM gets its credential as a file, and the trailing newline a file
// carries must not become part of the secret.
func TestLoadConfigReadsTheCredentialFromAFile(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "credential")
	if err := os.WriteFile(path, []byte("s3cret-value\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	t.Setenv("RUNNER_POOL_CREDENTIAL", "")
	t.Setenv("RUNNER_POOL_CREDENTIAL_FILE", path)
	t.Setenv("REELBOLT_WORKER_NAME", "pool-7")
	t.Setenv("REELBOLT_WORKER_CPUS", "8")
	t.Setenv("SANDBOX_CPU_LIMIT", "2")

	cfg, err := loadConfig()
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Credential != "s3cret-value" {
		t.Fatalf("credential = %q, want the trimmed file contents", cfg.Credential)
	}
	if cfg.WorkerName != "pool-7" {
		t.Fatalf("worker name = %q", cfg.WorkerName)
	}
	if cfg.Slots != 4 || cfg.SlotsFromEnv {
		t.Fatalf("slots = %d (fromEnv=%v), want the derived 4", cfg.Slots, cfg.SlotsFromEnv)
	}
}

// TestSlotsFromEnvWins is the escape hatch an operator uses when the machine's
// own capacity is not the right answer.
func TestSlotsFromEnvWins(t *testing.T) {
	t.Setenv("REELBOLT_WORKER_SLOTS", "3")
	slots, fromEnv := slotsFromEnv(64, "2")
	if !fromEnv || slots != 3 {
		t.Fatalf("slotsFromEnv = (%d, %v), want (3, true)", slots, fromEnv)
	}
	t.Setenv("REELBOLT_WORKER_SLOTS", "not-a-number")
	if slots, fromEnv := slotsFromEnv(8, "2"); fromEnv || slots != 4 {
		t.Fatalf("a malformed override was honoured: (%d, %v)", slots, fromEnv)
	}
}

// TestContainerCLINoneDisablesTheRuntime is the documented way to run a worker
// with no container runtime at all: it must produce an empty CLI rather than a
// driver that shells out to a binary named "none".
func TestContainerCLINoneDisablesTheRuntime(t *testing.T) {
	t.Setenv("SANDBOX_CONTAINER_CLI", "none")
	t.Setenv("REELBOLT_WORKER_CPUS", "2")
	cfg, err := loadConfig()
	if err != nil {
		t.Fatal(err)
	}
	if cfg.ContainerCLI != "" {
		t.Fatalf("ContainerCLI = %q, want the empty string", cfg.ContainerCLI)
	}
	if cfg.Container == (container.Config{}) {
		t.Fatal("the driver configuration was left zero; the SANDBOX_* defaults still apply")
	}
}

// TestResolveRuntimeImageDigest: the digest is what makes a render
// reproducible, so the three sources are pinned in order — and "no digest" is a
// legitimate answer that must survive as an empty string rather than as a tag.
func TestResolveRuntimeImageDigest(t *testing.T) {
	t.Setenv("REELBOLT_RUNTIME_IMAGE_DIGEST", "sha256:from-env")
	cfg := appConfig{Container: container.Config{SandboxImage: "ghcr.io/x/y@sha256:from-image"}}
	if got := resolveRuntimeImageDigest(cfg); got != "sha256:from-env" {
		t.Fatalf("digest = %q, want the explicit override", got)
	}

	t.Setenv("REELBOLT_RUNTIME_IMAGE_DIGEST", "")
	if got := resolveRuntimeImageDigest(cfg); got != "sha256:from-image" {
		t.Fatalf("digest = %q, want the digest pinned into SANDBOX_IMAGE", got)
	}

	// A locally built tag has no RepoDigest and no CLI here to ask, so the
	// answer is empty — and that is what stops the worker advertising a
	// remotion job type it cannot reproduce.
	cfg = appConfig{Container: container.Config{SandboxImage: "reelbolt-sandbox-runtime:local"}}
	if got := resolveRuntimeImageDigest(cfg); got != "" {
		t.Fatalf("digest = %q, want empty for an unpinned local tag", got)
	}
}

// TestDigestOrNone keeps a nil digest out of a log line.
func TestDigestOrNone(t *testing.T) {
	if got := digestOrNone(nil); got != "(none)" {
		t.Fatalf("digestOrNone(nil) = %q", got)
	}
	empty := ""
	if got := digestOrNone(&empty); got != "(none)" {
		t.Fatalf("digestOrNone(empty) = %q", got)
	}
	full := "sha256:abc"
	if got := digestOrNone(&full); got != "sha256:abc" {
		t.Fatalf("digestOrNone = %q", got)
	}
}

func TestResolveCPULimitAll(t *testing.T) {
	if got := resolveCPULimit("all", 8); got != "8" {
		t.Fatalf("all on 8 vCPU = %q", got)
	}
	if got := resolveCPULimit(" ALL ", 2.5); got != "2" {
		t.Fatalf("all on 2.5 vCPU = %q", got)
	}
	if got := resolveCPULimit("all", 0.4); got != "1" {
		t.Fatalf("all below one core = %q", got)
	}
	if got := resolveCPULimit("4", 8); got != "4" {
		t.Fatalf("explicit value changed: %q", got)
	}
	if n := SlotsFor(8, resolveCPULimit("all", 8)); n != 1 {
		t.Fatalf("all cores must be exactly one slot, got %d", n)
	}
}

func TestResolveMemoryLimitAuto(t *testing.T) {
	const gib = int64(1) << 30
	if got := resolveMemoryLimit("auto", 32*gib); got != "24576m" {
		t.Fatalf("auto on 32 GiB = %q", got)
	}
	if got := resolveMemoryLimit("auto", 0); got != "2g" {
		t.Fatalf("auto with unknown memory = %q", got)
	}
	if got := resolveMemoryLimit("6g", 32*gib); got != "6g" {
		t.Fatalf("explicit value changed: %q", got)
	}
}

func TestParseMemTotal(t *testing.T) {
	if got := parseMemTotal("MemFree: 1 kB\nMemTotal:       32768000 kB\n"); got != 32768000*1024 {
		t.Fatalf("got %d", got)
	}
	if parseMemTotal("garbage") != 0 || parseMemTotal("MemTotal: x kB") != 0 {
		t.Fatal("unreadable meminfo must be 0")
	}
}

func TestAllCoresNeedsTheFlag(t *testing.T) {
	t.Setenv("REELBOLT_WORKER_USE_ALL_CORES", "")
	if allCores(8) != 0 {
		t.Fatal("off by default")
	}
	t.Setenv("REELBOLT_WORKER_USE_ALL_CORES", "true")
	if allCores(8) != 8 || allCores(0.5) != 1 {
		t.Fatal("on: whole cores, never below 1")
	}
}

// TestPoolNodeRunsOneJobAtATime pins the contract the pool relies on once idle nodes are kept
// until the end of their paid hour: a node started with the pool's settings (cpu limit "all", no
// slot override) advertises exactly one slot, so the gateway offers it one job and the agent
// rejects a second one as busy.
func TestPoolNodeRunsOneJobAtATime(t *testing.T) {
	for _, cpus := range []string{"2", "4", "8", "16", "48"} {
		t.Run(cpus+" vCPU", func(t *testing.T) {
			t.Setenv("SANDBOX_CPU_LIMIT", "all")
			t.Setenv("REELBOLT_WORKER_SLOTS", "")
			t.Setenv("REELBOLT_WORKER_CPUS", cpus)
			cfg, err := loadConfig()
			if err != nil {
				t.Fatal(err)
			}
			if cfg.Slots != 1 || cfg.Container.MaxSandboxes != 1 {
				t.Fatalf("slots %d, max sandboxes %d; a pool node must run one job at a time", cfg.Slots, cfg.Container.MaxSandboxes)
			}
		})
	}
}

// A pool node on the private network dials the gateway over plain ws://.
func TestValidateAcceptsAPrivateWS(t *testing.T) {
	cfg := appConfig{GatewayURL: "ws://10.0.2.4:8080/v1/connect", WorkerName: "pool-1", Credential: "c", Slots: 1}
	if err := cfg.validate(); err != nil {
		t.Fatalf("ws:// to a private address was refused: %v", err)
	}
}
