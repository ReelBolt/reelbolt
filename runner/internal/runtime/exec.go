// Package runtime discovers and verifies a container runtime (Docker or
// Podman) on the user's machine, following the detection order and live-probe
// rule in docs/design/desktop-runner-adr.md section 2.
//
// Everything that touches the operating system goes through the Runner and Env
// interfaces so tests drive the whole package with a fake.
package runtime

import (
	"bytes"
	"context"
	"os"
	"os/exec"
)

// Runner executes a command and returns its combined stdout and stderr. env is
// a list of extra KEY=VALUE entries layered over the process environment.
type Runner interface {
	Run(ctx context.Context, env []string, name string, args ...string) ([]byte, error)
}

// ExecRunner is the production Runner backed by os/exec.
type ExecRunner struct{}

// Run implements Runner.
func (ExecRunner) Run(ctx context.Context, env []string, name string, args ...string) ([]byte, error) {
	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Env = append(os.Environ(), env...)
	var buf bytes.Buffer
	cmd.Stdout = &buf
	cmd.Stderr = &buf
	err := cmd.Run()
	return buf.Bytes(), err
}
