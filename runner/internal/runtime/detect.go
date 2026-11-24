package runtime

import (
	"context"
	"os"
	"os/exec"
	"strings"
)

// Kind is the container engine family.
type Kind string

const (
	KindDocker Kind = "docker"
	KindPodman Kind = "podman"
)

// Candidate is one place a container runtime might be reachable.
type Candidate struct {
	Kind   Kind
	Binary string // resolved CLI path
	Host   string // DOCKER_HOST / CONTAINER_HOST value; empty means the CLI default
	Source string // why this candidate exists, for diagnostics
}

// Env expresses the host facts detection depends on.
type Env struct {
	Getenv   func(string) string
	LookPath func(string) (string, error)
	Exists   func(path string) bool
	Runner   Runner
}

// OSEnv is the production Env.
func OSEnv() Env {
	return Env{
		Getenv:   os.Getenv,
		LookPath: exec.LookPath,
		Exists: func(p string) bool {
			_, err := os.Stat(p)
			return err == nil
		},
		Runner: ExecRunner{},
	}
}

// hostEnv returns the environment entry that points the CLI at c.Host.
func (c Candidate) hostEnv() []string {
	if c.Host == "" {
		return nil
	}
	if c.Kind == KindPodman {
		return []string{"CONTAINER_HOST=" + c.Host}
	}
	return []string{"DOCKER_HOST=" + c.Host}
}

// Candidates lists runtime candidates in ADR order. It never contacts a
// daemon except for the cheap `docker context inspect`; liveness is the
// probe's job. Candidates whose CLI is not installed are omitted.
//
// Order: DOCKER_HOST, the active docker context, then the platform list
// (rootless docker, podman, rootful docker last).
func Candidates(ctx context.Context, e Env) []Candidate {
	var out []Candidate
	seen := map[string]bool{}
	add := func(c Candidate) {
		key := string(c.Kind) + "|" + c.Host
		if c.Binary == "" || seen[key] {
			return
		}
		seen[key] = true
		out = append(out, c)
	}

	docker, _ := e.LookPath("docker")

	if h := e.Getenv("DOCKER_HOST"); h != "" {
		add(Candidate{Kind: KindDocker, Binary: docker, Host: h, Source: "DOCKER_HOST"})
	}
	if docker != "" {
		if h := dockerContextHost(ctx, e, docker); h != "" {
			add(Candidate{Kind: KindDocker, Binary: docker, Host: h, Source: "docker-context"})
		}
	}
	for _, c := range platformCandidates(e) {
		add(c)
	}
	return out
}

func dockerContextHost(ctx context.Context, e Env, docker string) string {
	out, err := e.Runner.Run(ctx, nil, docker, "context", "inspect", "--format", "{{.Endpoints.docker.Host}}")
	if err != nil {
		return ""
	}
	h := strings.TrimSpace(string(out))
	if h == "" || h == "<no value>" {
		return ""
	}
	return h
}
