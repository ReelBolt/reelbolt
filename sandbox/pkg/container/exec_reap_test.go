package container

import (
	"context"
	"os"
	"os/exec"
	"strings"
	"sync"
	"testing"
	"time"
)

// A timed-out exec must not leave its command running inside the container:
// the driver tags the exec with a marker and, on timeout, kills by marker.
func TestRunExecReapsTheTimedOutCommandByMarker(t *testing.T) {
	var mu sync.Mutex
	var marker, reapScript string
	base := fakeWorkspaceRunner(t)
	runner := func(ctx context.Context, args ...string) ([]byte, error) {
		if len(args) > 0 && args[0] == "exec" {
			for i, a := range args {
				if a == "-e" && i+1 < len(args) && strings.HasPrefix(args[i+1], execMarkerVar+"=") {
					mu.Lock()
					marker = strings.TrimPrefix(args[i+1], execMarkerVar+"=")
					mu.Unlock()
					<-ctx.Done() // the command outlives its deadline
					return nil, ctx.Err()
				}
			}
			if args[len(args)-2] == "-c" && strings.Contains(args[len(args)-1], "kill -9") {
				mu.Lock()
				reapScript = args[len(args)-1]
				mu.Unlock()
			}
		}
		return base(ctx, args...)
	}

	driver := NewWithRunner(Config{SandboxRoot: t.TempDir(), MaxSandboxes: 2, ExecTimeout: time.Minute}, "docker", runner)
	execution := "1f8e7d6c-5b4a-4392-8170-a1b2c3d4e5f6"
	if _, _, err := driver.Create(execution); err != nil {
		t.Fatalf("Create: %v", err)
	}
	if _, err := driver.RunExec(execution, ExecRequest{Command: "npm", Args: []string{"run", "build"}, TimeoutSeconds: 1}); err == nil {
		t.Fatal("expected the exec to time out")
	}

	mu.Lock()
	defer mu.Unlock()
	if marker == "" {
		t.Fatal("the exec carried no marker")
	}
	if !strings.Contains(reapScript, execMarkerVar+"="+marker) {
		t.Fatalf("no reap for marker %s; reap script = %q", marker, reapScript)
	}
}

// The reap script itself, run for real against a local process tree.
func TestKillMarkedProcessesScriptKillsOnlyMarkedProcesses(t *testing.T) {
	if _, err := os.Stat("/proc/self/environ"); err != nil {
		t.Skip("needs /proc")
	}
	marker := newExecMarker()
	marked := exec.CommandContext(context.Background(), "sh", "-c", "sleep 60 & wait")
	marked.Env = append(os.Environ(), execMarkerVar+"="+marker)
	other := exec.CommandContext(context.Background(), "sleep", "60")
	if err := marked.Start(); err != nil {
		t.Fatal(err)
	}
	if err := other.Start(); err != nil {
		t.Fatal(err)
	}
	defer func() { _ = other.Process.Kill(); _ = other.Wait() }()
	time.Sleep(200 * time.Millisecond)

	if out, err := exec.CommandContext(context.Background(), "sh", "-c", killMarkedProcessesScript(marker)).CombinedOutput(); err != nil {
		t.Fatalf("script: %v: %s", err, out)
	}
	done := make(chan error, 1)
	go func() { done <- marked.Wait() }()
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		_ = marked.Process.Kill()
		t.Fatal("the marked process survived")
	}
	if other.ProcessState != nil {
		t.Fatal("an unmarked process was killed")
	}
}

func TestExecDeadlineHonoursLongRendersUpToTheCeiling(t *testing.T) {
	d := NewWithRunner(Config{ExecTimeout: 5 * time.Minute}, "docker", nil)
	if got := d.execDeadline(ExecRequest{}); got != 5*time.Minute {
		t.Fatalf("default = %s", got)
	}
	if got := d.execDeadline(ExecRequest{TimeoutSeconds: 3 * 3600}); got != 3*time.Hour {
		t.Fatalf("a 3h render was clamped to %s", got)
	}
	if got := d.execDeadline(ExecRequest{TimeoutSeconds: 100 * 3600}); got != DefaultMaxExecTimeout {
		t.Fatalf("ceiling = %s", got)
	}
	d = NewWithRunner(Config{ExecTimeout: time.Minute, MaxExecTimeout: time.Hour}, "docker", nil)
	if got := d.execDeadline(ExecRequest{TimeoutSeconds: 7200}); got != time.Hour {
		t.Fatalf("configured ceiling = %s", got)
	}
}
