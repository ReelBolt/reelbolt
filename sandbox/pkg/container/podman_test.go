package container

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
)

const execID = "22222222-2222-2222-2222-222222222222"

// recorder is a fake Runner that logs every invocation and answers `run` by
// materialising package.json in the mounted workspace, as the init script in
// the real container does.
type recorder struct {
	mu    sync.Mutex
	calls [][]string
}

func (r *recorder) run(_ context.Context, args ...string) ([]byte, error) {
	r.mu.Lock()
	r.calls = append(r.calls, append([]string(nil), args...))
	r.mu.Unlock()
	if len(args) > 0 && args[0] == "run" {
		for i, a := range args {
			if a == "-v" && i+1 < len(args) {
				ws := strings.SplitN(args[i+1], ":", 2)[0]
				_ = os.WriteFile(filepath.Join(ws, "package.json"), []byte("{}"), 0o644)
			}
		}
	}
	return nil, nil
}

func (r *recorder) first(sub string) []string {
	r.mu.Lock()
	defer r.mu.Unlock()
	for _, c := range r.calls {
		if len(c) > 0 && c[0] == sub {
			return c
		}
	}
	return nil
}

// Every flag and subcommand this driver issues must be one podman accepts with
// the same meaning. The sets below are the podman-run/exec/network flags used
// today; a new flag fails this test until someone confirms podman supports it.
func TestCreateUsesOnlyPodmanCompatibleFlags(t *testing.T) {
	rec := &recorder{}
	d := NewWithRunner(Config{
		SandboxRoot:    t.TempDir(),
		SandboxNetwork: "sandbox-net",
		SandboxImage:   "img",
		MaxSandboxes:   2,
		MemoryLimit:    "2g",
		CPULimit:       "2",
		PIDsLimit:      256,
	}, "podman", rec.run)

	if _, _, err := d.Create(execID); err != nil {
		t.Fatalf("Create: %v", err)
	}
	args := rec.first("run")
	if args == nil {
		t.Fatal("no run invocation recorded")
	}

	podmanRunFlags := map[string]bool{
		"-d": true, "--name": true, "--label": true, "--network": true, "--read-only": true,
		"--tmpfs": true, "--shm-size": true, "-e": true, "--memory": true, "--cpus": true, "--pids-limit": true,
		"--security-opt": true, "--cap-drop": true, "--user": true,
		"-v": true, "-w": true, "--entrypoint": true,
	}
	// Flags come before the image; everything after it is the container's own
	// command line (sh -c ...), which no CLI interprets.
	for _, a := range args[1:] {
		if a == "img" {
			break
		}
		if strings.HasPrefix(a, "-") && !podmanRunFlags[a] {
			t.Errorf("flag %q is not in the podman-compatible set", a)
		}
	}

	// the security posture must survive any CLI
	joined := strings.Join(args, " ")
	for _, want := range []string{
		"--read-only", "--security-opt no-new-privileges", "--cap-drop ALL",
		"--user node", "--network sandbox-net", "--pids-limit 256",
	} {
		if !strings.Contains(joined, want) {
			t.Errorf("run args missing %q: %s", want, joined)
		}
	}
	if strings.Contains(joined, "--privileged") || strings.Contains(joined, "docker.sock") {
		t.Errorf("run args must never be privileged or mount the socket: %s", joined)
	}
}

func TestDetectKind(t *testing.T) {
	cases := []struct {
		name string
		out  string
		err  error
		want Kind
	}{
		{"docker", "Client:\n Version: 27.0.1\n Context: default\nServer: Docker Engine - Community", nil, KindDocker},
		{"podman", "Client:       Podman Engine\nVersion:      5.2.0", nil, KindPodman},
		{"podman via docker shim", "Emulate Docker CLI using podman.\nClient: Podman Engine", nil, KindPodman},
		{"garbage", "hello", nil, KindUnknown},
	}
	for _, tc := range cases {
		d := NewWithRunner(Config{}, "docker", func(ctx context.Context, args ...string) ([]byte, error) {
			if len(args) != 1 || args[0] != "version" {
				t.Errorf("DetectKind ran %v", args)
			}
			return []byte(tc.out), tc.err
		})
		got, err := d.DetectKind(t.Context())
		if err != nil || got != tc.want || d.Kind != tc.want {
			t.Errorf("%s: kind=%q err=%v, want %q", tc.name, got, err, tc.want)
		}
	}

	d := NewWithRunner(Config{}, "docker", func(ctx context.Context, args ...string) ([]byte, error) {
		return nil, errors.New("cannot connect")
	})
	if kind, err := d.DetectKind(t.Context()); err == nil || kind != KindUnknown {
		t.Errorf("failing CLI: kind=%q err=%v", kind, err)
	}
}

func TestEnsureNetworkRefusesNonInternalNetwork(t *testing.T) {
	d := NewWithRunner(Config{SandboxNetwork: "sandbox-net"}, "podman", func(ctx context.Context, args ...string) ([]byte, error) {
		switch {
		case len(args) > 1 && args[1] == "create":
			return []byte("network name sandbox-net already used: network already exists"), errors.New("exit status 125")
		case len(args) > 1 && args[1] == "inspect":
			return []byte("false\n"), nil
		}
		return nil, nil
	})
	var ne *NetworkError
	if err := d.EnsureNetwork(); !errors.As(err, &ne) {
		t.Fatalf("want *NetworkError for an egress-capable leftover network, got %v", err)
	}
	if !strings.Contains(ne.Error(), "podman network rm sandbox-net") {
		t.Errorf("remediation should name the active CLI: %v", ne)
	}
}

func TestEnsureNetworkEgressOptInSkipsInternal(t *testing.T) {
	var createArgs []string
	d := NewWithRunner(Config{SandboxNetwork: "sandbox-net", NetworkEgress: true}, "docker", func(ctx context.Context, args ...string) ([]byte, error) {
		if len(args) > 1 && args[1] == "create" {
			createArgs = args
		}
		return []byte("false"), nil
	})
	if err := d.EnsureNetwork(); err != nil {
		t.Fatal(err)
	}
	for _, a := range createArgs {
		if a == "--internal" {
			t.Errorf("egress opt-in must not pass --internal: %v", createArgs)
		}
	}
}

// RunExec must refuse anything outside the allowlist before any CLI call that
// would execute it.
func TestRunExecRejectsDisallowedCommandBeforeExec(t *testing.T) {
	rec := &recorder{}
	d := NewWithRunner(Config{SandboxRoot: t.TempDir(), MaxSandboxes: 2, SandboxImage: "img"}, "docker", rec.run)
	if _, _, err := d.Create(execID); err != nil {
		t.Fatal(err)
	}
	_, err := d.RunExec(execID, ExecRequest{Command: "sh", Args: []string{"-c", "id"}})
	if !errors.Is(err, ErrBadExec) {
		t.Fatalf("want ErrBadExec, got %v", err)
	}
	if rec.first("exec") != nil {
		t.Error("a CLI exec was issued for a rejected command")
	}
}

func TestCreatePassesShmSize(t *testing.T) {
	cases := []struct {
		shm  string
		want string // "" = no --shm-size flag
	}{
		{"", DefaultShmSize},
		{"2g", "2g"},
		{"0", ""},
		{"off", ""},
	}
	for _, tc := range cases {
		rec := &recorder{}
		d := NewWithRunner(Config{
			SandboxRoot:  t.TempDir(),
			SandboxImage: "img",
			MaxSandboxes: 2,
			MemoryLimit:  "2g",
			CPULimit:     "2",
			PIDsLimit:    256,
			ShmSize:      tc.shm,
		}, "docker", rec.run)
		if _, _, err := d.Create(execID); err != nil {
			t.Fatalf("Create(%q): %v", tc.shm, err)
		}
		joined := strings.Join(rec.first("run"), " ")
		if tc.want == "" {
			if strings.Contains(joined, "--shm-size") {
				t.Errorf("ShmSize %q: expected no --shm-size, got %s", tc.shm, joined)
			}
			continue
		}
		if !strings.Contains(joined, "--shm-size "+tc.want) {
			t.Errorf("ShmSize %q: expected --shm-size %s in %s", tc.shm, tc.want, joined)
		}
	}
}

func TestCreatePointsTmpdirAtTheWorkspaceDisk(t *testing.T) {
	rec := &recorder{}
	d := NewWithRunner(Config{
		SandboxRoot:  t.TempDir(),
		SandboxImage: "img",
		MaxSandboxes: 2,
		MemoryLimit:  "2g",
		CPULimit:     "2",
		PIDsLimit:    256,
	}, "docker", rec.run)
	if _, _, err := d.Create(execID); err != nil {
		t.Fatalf("Create: %v", err)
	}
	joined := strings.Join(rec.first("run"), " ")
	if !strings.Contains(joined, "-e TMPDIR="+SandboxTempDir) {
		t.Errorf("run args missing TMPDIR=%s: %s", SandboxTempDir, joined)
	}
	if !strings.Contains(joined, "mkdir -p "+SandboxTempDir+";") {
		t.Errorf("init command does not create %s: %s", SandboxTempDir, joined)
	}
	// The folder must stay out of captured workspaces and survive an import.
	if !IsExcludedWorkspacePath(strings.TrimPrefix(SandboxTempDir, "/workspace/")) {
		t.Errorf("%s is not excluded from the workspace archive", SandboxTempDir)
	}
}
