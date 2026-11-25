//go:build unix

package container

import (
	"context"
	"os/exec"
	"testing"
	"time"
)

// Runs hasLiveExec against a real Docker daemon; skipped when none is reachable or no local
// alpine image exists (the test never pulls).
func TestHasLiveExecAgainstRealDocker(t *testing.T) {
	if _, err := exec.LookPath("docker"); err != nil {
		t.Skip("no docker binary")
	}
	if out, err := exec.CommandContext(context.Background(), "docker", "image", "inspect", "alpine:3", "--format", "{{.Id}}").CombinedOutput(); err != nil || len(out) == 0 {
		t.Skip("docker daemon or alpine:3 not available")
	}
	name := "rf-sbx-t013-" + newExecMarker()[:8]
	if out, err := exec.CommandContext(context.Background(), "docker", "run", "-d", "--name", name, "--network", "none", "alpine:3", "sleep", "120").CombinedOutput(); err != nil {
		t.Skipf("cannot start container: %v %s", err, out)
	}
	t.Cleanup(func() { _ = exec.CommandContext(context.Background(), "docker", "rm", "-f", name).Run() })

	d := New(Config{}, "docker")
	if d.hasLiveExec(name) {
		t.Fatal("idle container reported a live exec")
	}
	cmd := exec.CommandContext(context.Background(), "docker", "exec", "-e", execMarkerVar+"=t013", name, "sleep", "60")
	if err := cmd.Start(); err != nil {
		t.Fatal(err)
	}
	defer func() { _ = cmd.Process.Kill() }()
	deadline := time.Now().Add(10 * time.Second)
	for !d.hasLiveExec(name) {
		if time.Now().After(deadline) {
			t.Fatal("running marked exec not detected")
		}
		time.Sleep(200 * time.Millisecond)
	}
	_ = exec.CommandContext(context.Background(), "docker", "stop", "-t", "1", name).Run()
	if !d.hasLiveExec(name) {
		t.Fatal("a failed scan (stopped container) must read as may-be-live")
	}
}
