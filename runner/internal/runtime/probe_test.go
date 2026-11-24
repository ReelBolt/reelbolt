package runtime

import (
	"context"
	"strings"
	"testing"
)

func TestProbe(t *testing.T) {
	docker := Candidate{Kind: KindDocker, Binary: "docker", Source: "test"}
	podman := Candidate{Kind: KindPodman, Binary: "podman", Host: "unix:///run/user/1000/podman/podman.sock", Source: "test"}

	// good returns the rules for a fully healthy engine; tests override the
	// front of the list.
	good := func(version, info, imageArch string) []rule {
		return []rule{
			{match: "version --format", out: version + "\n"},
			{match: "info --format json", out: info},
			{match: "network create --internal", out: "id\n"},
			{match: "network inspect", out: "true\n"},
			{match: "network rm", out: ""},
			{match: "image inspect", out: imageArch + "\n"},
			{match: "--entrypoint true", out: ""},
			{match: "--entrypoint node", out: ""},
		}
	}

	tests := []struct {
		name      string
		cand      Candidate
		rules     []rule
		cfg       ProbeConfig
		wantFail  string
		usable    bool
		remotion  bool
		wantMode  Mode
		wantArch  string
		noRunCmds bool // the throwaway container must never have been started
	}{
		{
			name: "docker rootless full pass", cand: docker,
			rules: good("27.3.1", dockerInfoRootless, "amd64"), cfg: ProbeConfig{Image: testImage, HostArch: "amd64"},
			usable: true, remotion: true, wantMode: ModeRootless, wantArch: "amd64",
		},
		{
			name: "docker rootful warns via mode", cand: docker,
			rules: good("24.0.7", dockerInfoRootful, "amd64"), cfg: ProbeConfig{Image: testImage, HostArch: "amd64"},
			usable: true, remotion: true, wantMode: ModeRootful, wantArch: "amd64",
		},
		{
			name: "docker desktop is vm and arm64 maps from aarch64", cand: docker,
			rules: good("28.0.1", dockerInfoDesktop, "arm64"), cfg: ProbeConfig{Image: testImage, HostArch: "arm64"},
			usable: true, remotion: true, wantMode: ModeVM, wantArch: "arm64",
		},
		{
			name: "podman local rootless", cand: podman,
			rules: good("5.2.0", podmanInfoLocal, "amd64"), cfg: ProbeConfig{Image: testImage, HostArch: "amd64"},
			usable: true, remotion: true, wantMode: ModeRootless, wantArch: "amd64",
		},
		{
			name: "podman machine is vm", cand: podman,
			rules: good("6.0.2", podmanInfoMachine, "amd64"), cfg: ProbeConfig{Image: testImage, HostArch: "amd64"},
			usable: true, remotion: true, wantMode: ModeVM, wantArch: "amd64",
		},
		{
			name: "daemon unreachable", cand: docker,
			rules:    []rule{{match: "version --format", out: "Cannot connect to the Docker daemon", err: errExit}},
			cfg:      ProbeConfig{Image: testImage, HostArch: "amd64"},
			wantFail: FailUnreachable, noRunCmds: true,
		},
		{
			name: "docker too old", cand: docker,
			rules:    []rule{{match: "version --format", out: "23.0.6\n"}},
			cfg:      ProbeConfig{Image: testImage, HostArch: "amd64"},
			wantFail: FailVersionTooOld, noRunCmds: true,
		},
		{
			name: "podman 4.9 is below the 5.0 minimum", cand: podman,
			rules:    []rule{{match: "version --format", out: "4.9.4\n"}},
			cfg:      ProbeConfig{Image: testImage, HostArch: "amd64"},
			wantFail: FailVersionTooOld, noRunCmds: true,
		},
		{
			name: "garbage version string", cand: docker,
			rules:    []rule{{match: "version --format", out: "banana\n"}},
			cfg:      ProbeConfig{Image: testImage, HostArch: "amd64"},
			wantFail: FailVersionTooOld, noRunCmds: true,
		},
		{
			name: "info unparsable", cand: docker,
			rules: []rule{
				{match: "version --format", out: "27.0.0\n"},
				{match: "info --format json", out: "not json"},
			},
			cfg: ProbeConfig{Image: testImage, HostArch: "amd64"}, wantFail: FailInfo, noRunCmds: true,
		},
		{
			name: "internal network creation fails", cand: docker,
			rules: []rule{
				{match: "version --format", out: "27.0.0\n"},
				{match: "info --format json", out: dockerInfoRootless},
				{match: "network create --internal", out: "unknown flag", err: errExit},
			},
			cfg: ProbeConfig{Image: testImage, HostArch: "amd64"}, wantFail: FailInternalNetwork, noRunCmds: true,
		},
		{
			name: "network created but not actually internal", cand: docker,
			rules: []rule{
				{match: "version --format", out: "27.0.0\n"},
				{match: "info --format json", out: dockerInfoRootless},
				{match: "network create --internal", out: "id"},
				{match: "network inspect", out: "false\n"},
				{match: "network rm", out: ""},
			},
			cfg: ProbeConfig{Image: testImage, HostArch: "amd64"}, wantFail: FailInternalNetwork, noRunCmds: true,
		},
		{
			name: "no image configured keeps the runtime usable", cand: docker,
			rules: good("27.3.1", dockerInfoRootless, "amd64"), cfg: ProbeConfig{HostArch: "amd64"},
			usable: true, wantFail: FailNoImage, wantMode: ModeRootless, wantArch: "amd64", noRunCmds: true,
		},
		{
			name: "image not pulled", cand: docker,
			rules: append([]rule{{match: "image inspect", out: "No such image", err: errExit}},
				good("27.3.1", dockerInfoRootless, "amd64")...),
			cfg:    ProbeConfig{Image: testImage, HostArch: "amd64"},
			usable: true, wantFail: FailImageMissing, wantMode: ModeRootless, wantArch: "amd64", noRunCmds: true,
		},
		{
			name: "engine is emulated relative to the host", cand: docker,
			rules: good("27.3.1", dockerInfoRootless, "amd64"), cfg: ProbeConfig{Image: testImage, HostArch: "arm64"},
			usable: true, wantFail: FailArchMismatch, wantMode: ModeRootless, wantArch: "amd64", noRunCmds: true,
		},
		{
			name: "image arch differs from engine", cand: docker,
			rules: good("27.3.1", dockerInfoRootless, "arm64"), cfg: ProbeConfig{Image: testImage, HostArch: "amd64"},
			usable: true, wantFail: FailArchMismatch, wantMode: ModeRootless, wantArch: "amd64", noRunCmds: true,
		},
		{
			name: "image fails under the hardened flag set", cand: docker,
			rules: append([]rule{{match: "--entrypoint true", out: "permission denied", err: errExit}},
				good("27.3.1", dockerInfoRootless, "amd64")...),
			cfg:    ProbeConfig{Image: testImage, HostArch: "amd64"},
			usable: true, wantFail: FailImageRun, wantMode: ModeRootless, wantArch: "amd64",
		},
		{
			name: "container has a route out", cand: docker,
			rules: append([]rule{{match: "--entrypoint node", out: "", err: errExit}},
				good("27.3.1", dockerInfoRootless, "amd64")...),
			cfg:    ProbeConfig{Image: testImage, HostArch: "amd64"},
			usable: true, wantFail: FailEgressPossible, wantMode: ModeRootless, wantArch: "amd64",
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			f := &fakeRunner{rules: tc.rules}
			tc.cfg.NameSuffix = "t1"
			rep, err := Probe(context.Background(), f, tc.cand, tc.cfg)
			if err != nil {
				t.Fatalf("Probe returned error: %v", err)
			}
			if rep.Failure != tc.wantFail {
				t.Errorf("Failure = %q (%s), want %q", rep.Failure, rep.Detail, tc.wantFail)
			}
			if rep.Usable != tc.usable {
				t.Errorf("Usable = %v, want %v", rep.Usable, tc.usable)
			}
			if rep.RemotionReady != tc.remotion {
				t.Errorf("RemotionReady = %v, want %v", rep.RemotionReady, tc.remotion)
			}
			if tc.wantMode != "" && rep.Mode != tc.wantMode {
				t.Errorf("Mode = %q, want %q", rep.Mode, tc.wantMode)
			}
			if tc.wantArch != "" && rep.ServerArch != tc.wantArch {
				t.Errorf("ServerArch = %q, want %q", rep.ServerArch, tc.wantArch)
			}
			if tc.noRunCmds && f.called(" run ") {
				t.Errorf("a container was started but should not have been: %v", f.calls)
			}
			// Once a network was created and inspected it must always be removed.
			if f.called("network inspect") && !f.called("network rm reelbolt-probe-t1") {
				t.Errorf("probe network was not removed: %v", f.calls)
			}
		})
	}
}

func TestProbeRunsFullFlagSet(t *testing.T) {
	f := &fakeRunner{rules: []rule{
		{match: "version --format", out: "27.3.1\n"},
		{match: "info --format json", out: dockerInfoRootless},
		{match: "network create", out: "id"},
		{match: "network inspect", out: "true\n"},
		{match: "network rm", out: ""},
		{match: "image inspect", out: "amd64\n"},
		{match: " run ", out: ""},
	}}
	_, err := Probe(context.Background(), f, Candidate{Kind: KindDocker, Binary: "docker"},
		ProbeConfig{Image: testImage, HostArch: "amd64", NameSuffix: "x"})
	if err != nil {
		t.Fatal(err)
	}
	var runLine string
	for _, c := range f.calls {
		if strings.Contains(c, " run ") && strings.Contains(c, "--entrypoint true") {
			runLine = c
		}
	}
	if runLine == "" {
		t.Fatalf("no run command recorded: %v", f.calls)
	}
	for _, want := range []string{
		"--network reelbolt-probe-x", "--read-only", "--cap-drop ALL",
		"--security-opt no-new-privileges", "--user node", "--pids-limit", "--memory", "--cpus",
		"--tmpfs /tmp:", testImage,
	} {
		if !strings.Contains(runLine, want) {
			t.Errorf("run command missing %q:\n%s", want, runLine)
		}
	}
}

func TestProbeSetsHostEnvPerKind(t *testing.T) {
	for _, tc := range []struct {
		cand Candidate
		want string
	}{
		{Candidate{Kind: KindDocker, Binary: "docker", Host: "tcp://h:2375"}, "DOCKER_HOST=tcp://h:2375"},
		{Candidate{Kind: KindPodman, Binary: "podman", Host: "unix:///p.sock"}, "CONTAINER_HOST=unix:///p.sock"},
	} {
		f := &fakeRunner{rules: []rule{{match: "version --format", out: "banana", err: errExit}}}
		if _, err := Probe(context.Background(), f, tc.cand, ProbeConfig{}); err != nil {
			t.Fatal(err)
		}
		if len(f.envs) == 0 || len(f.envs[0]) != 1 || f.envs[0][0] != tc.want {
			t.Errorf("env = %v, want [%s]", f.envs, tc.want)
		}
	}
}

func TestProbeCancelledContextIsAnError(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	f := &fakeRunner{rules: []rule{{match: "version --format", out: "", err: context.Canceled}}}
	if _, err := Probe(ctx, f, Candidate{Kind: KindDocker, Binary: "docker"}, ProbeConfig{}); err == nil {
		t.Fatal("expected context error")
	}
}

func TestParseVersion(t *testing.T) {
	for _, tc := range []struct {
		in         string
		major, min int
		ok         bool
	}{
		{"27.3.1", 27, 3, true},
		{"v5.2.0", 5, 2, true},
		{"6.0.0-rc1", 6, 0, true},
		{"5.4-dev", 5, 4, true},
		{"24", 0, 0, false},
		{"", 0, 0, false},
		{"x.y", 0, 0, false},
	} {
		maj, mn, ok := parseVersion(tc.in)
		if maj != tc.major || mn != tc.min || ok != tc.ok {
			t.Errorf("parseVersion(%q) = %d,%d,%v want %d,%d,%v", tc.in, maj, mn, ok, tc.major, tc.min, tc.ok)
		}
	}
}

func TestSelectTakesFirstUsableAndExplainsSkips(t *testing.T) {
	// DOCKER_HOST points at a dead daemon; the docker context is healthy.
	f := &fakeRunner{}
	f.rules = []rule{
		{match: "context inspect", out: "unix:///ctx.sock\n"},
		{match: "version --format", out: "27.3.1\n"},
		{match: "info --format json", out: dockerInfoRootless},
		{match: "network create", out: "id"},
		{match: "network inspect", out: "true\n"},
		{match: "network rm", out: ""},
	}
	// A distinct runner per host so the first candidate fails.
	r := &hostRouter{inner: f, dead: "tcp://dead:1"}
	env := Env{
		Getenv:   func(k string) string { return map[string]string{"DOCKER_HOST": "tcp://dead:1"}[k] },
		LookPath: func(n string) (string, error) { return n, nil },
		Exists:   func(string) bool { return false },
		Runner:   r,
	}
	reports, chosen, err := Select(context.Background(), env, ProbeConfig{HostArch: "amd64"})
	if err != nil {
		t.Fatal(err)
	}
	if chosen == nil {
		t.Fatalf("no runtime chosen; reports: %+v", reports)
	}
	if reports[0].Candidate.Source != "DOCKER_HOST" || reports[0].Failure != FailUnreachable {
		t.Errorf("first report = %+v, want unreachable DOCKER_HOST", reports[0])
	}
	if chosen.Candidate.Source != "docker-context" {
		t.Errorf("chosen source = %q, want docker-context", chosen.Candidate.Source)
	}
}

func TestSelectNoRuntimeIsNotAnError(t *testing.T) {
	env := Env{
		Getenv:   func(string) string { return "" },
		LookPath: func(string) (string, error) { return "", errExit },
		Exists:   func(string) bool { return false },
		Runner:   &fakeRunner{},
	}
	reports, chosen, err := Select(context.Background(), env, ProbeConfig{})
	if err != nil || chosen != nil || len(reports) != 0 {
		t.Fatalf("got reports=%v chosen=%v err=%v, want empty/nil/nil", reports, chosen, err)
	}
}

// hostRouter fails every command aimed at a "dead" DOCKER_HOST.
type hostRouter struct {
	inner *fakeRunner
	dead  string
}

func (h *hostRouter) Run(ctx context.Context, env []string, name string, args ...string) ([]byte, error) {
	for _, e := range env {
		if e == "DOCKER_HOST="+h.dead {
			return []byte("Cannot connect"), errExit
		}
	}
	return h.inner.Run(ctx, env, name, args...)
}
