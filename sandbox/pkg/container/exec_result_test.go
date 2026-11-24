package container

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// Small string helpers, so this file's source carries no escape sequences a
// reader has to decode by hand.
const (
	newline        = string(rune(10))
	singleQuote    = "'"
	expectedStdout = "{" + string(rune(34)) + "compositions" + string(rune(34)) + ":[]}"
)

// fakeWorkspaceRunner answers the CLI invocations Create makes and
// materialises the workspace the real CLI would create, so the driver's own
// behaviour can be tested without a container runtime.
func fakeWorkspaceRunner(t *testing.T) Runner {
	t.Helper()
	return func(ctx context.Context, args ...string) ([]byte, error) {
		if len(args) > 0 && args[0] == "run" {
			// The workspace is the left side of the -v bind mount.
			for i, a := range args {
				if a == "-v" && i+1 < len(args) {
					mount := strings.SplitN(args[i+1], ":", 2)[0]
					if err := os.MkdirAll(mount, 0o770); err != nil {
						return nil, err
					}
					if err := os.WriteFile(filepath.Join(mount, "package.json"), []byte("{}"), 0o644); err != nil {
						return nil, err
					}
				}
			}
		}
		return []byte("ok"), nil
	}
}

// The exec op of a runner session needs the two streams apart: a build's errors
// are on stderr and a composition list is JSON on stdout, and a caller that
// merged them could parse neither.
func TestRunExecResultSeparatesStreamsAndReportsTheExitCode(t *testing.T) {
	script := filepath.Join(t.TempDir(), "fake-container-cli")
	quote := string(rune(34))
	lines := []string{
		"#!/bin/sh",
		"echo " + singleQuote + "{" + quote + "compositions" + quote + ":[]}" + singleQuote,
		"echo " + singleQuote + "a warning" + singleQuote + " 1>&2",
		"exit 3",
	}
	if err := os.WriteFile(script, []byte(strings.Join(lines, newline)), 0o755); err != nil {
		t.Fatalf("write the fake CLI: %v", err)
	}

	driver := NewWithRunner(Config{SandboxRoot: t.TempDir(), MaxSandboxes: 2, ExecTimeout: time.Minute}, script, fakeWorkspaceRunner(t))
	execution := "9f8e7d6c-5b4a-4392-8170-a1b2c3d4e5f6"
	if _, _, err := driver.Create(execution); err != nil {
		t.Fatalf("Create: %v", err)
	}

	res, err := driver.RunExecResult(execution, ExecRequest{Command: "npm", Args: []string{"run", "build"}})
	if err != nil {
		t.Fatalf("RunExecResult: %v", err)
	}
	if res.ExitCode != 3 {
		t.Fatalf("exit code = %d, want 3", res.ExitCode)
	}
	if strings.TrimSpace(string(res.Stdout)) != expectedStdout {
		t.Fatalf("stdout = %q", res.Stdout)
	}
	if strings.TrimSpace(string(res.Stderr)) != "a warning" {
		t.Fatalf("stderr = %q", res.Stderr)
	}
	if res.Duration <= 0 {
		t.Fatalf("duration = %s", res.Duration)
	}

	// The allowlist applies here too.
	if _, err := driver.RunExecResult(execution, ExecRequest{Command: "sh", Args: []string{"-c", "x"}}); err == nil {
		t.Fatal("RunExecResult ran a command outside the allowlist")
	}
}

// A runner session is over when its workspace is gone; StopByExecution alone
// leaves the container and the customer's files behind for the TTL sweep.
func TestDeleteByExecutionRemovesTheWorkspace(t *testing.T) {
	driver := NewWithRunner(Config{SandboxRoot: t.TempDir(), MaxSandboxes: 2}, "docker", fakeWorkspaceRunner(t))
	execution := "9f8e7d6c-5b4a-4392-8170-a1b2c3d4e5f6"
	sb, _, err := driver.Create(execution)
	if err != nil {
		t.Fatalf("Create: %v", err)
	}
	if _, err := os.Stat(sb.WorkspacePath); err != nil {
		t.Fatalf("the workspace was not created: %v", err)
	}
	if err := driver.DeleteByExecution(execution); err != nil {
		t.Fatalf("DeleteByExecution: %v", err)
	}
	if _, err := os.Stat(sb.WorkspacePath); !os.IsNotExist(err) {
		t.Fatalf("the workspace survived: %v", err)
	}
	if _, err := driver.GetByExecution(execution); err == nil {
		t.Fatal("the sandbox is still registered after a delete")
	}
	if err := driver.DeleteByExecution(execution); !errors.Is(err, ErrNotFound) {
		t.Fatalf("deleting twice = %v, want ErrNotFound", err)
	}
}
