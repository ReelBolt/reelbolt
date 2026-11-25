//go:build unix

package container

import (
	"context"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"testing"
	"time"
)

// The S3 rehearsal findings, pinned against a real Docker daemon (skipped when none is reachable or
// no local sandbox runtime image exists (SANDBOX_TEST_IMAGE, reelbolt-sandbox-runtime:local; never pulled); the test never pulls):
//
//   - DELETE /api/v1/sandboxes/{id} is StopByExecution, documented as identical to /complete: the
//     container stops and the workspace stays until the janitor's TTL. It never removes anything.
//   - A sandbox taken over by a later executor (hostile workspace: FIFO, /dev/zero symlink, forged
//     marker) is served by the executor holding it and invisible to a chained executor that lists
//     that holder as a peer; once the holder retires and the survivor adopts with no peers, the
//     survivor serves it.
func TestTakeoverStopAndChainAgainstRealDocker(t *testing.T) {
	if _, err := exec.LookPath("docker"); err != nil {
		t.Skip("no docker binary")
	}
	image := ""
	for _, c := range []string{os.Getenv("SANDBOX_TEST_IMAGE"), "reelbolt-sandbox-runtime:local"} {
		if c == "" {
			continue
		}
		if out, err := exec.CommandContext(context.Background(), "docker", "image", "inspect", c, "--format", "{{.Id}}").CombinedOutput(); err == nil && len(out) > 0 {
			image = c
			break
		}
	}
	if image == "" {
		t.Skip("docker daemon or a local sandbox runtime image not available (SANDBOX_TEST_IMAGE)")
	}
	prefix := "rf-sbx-t029" + newExecMarker()[:8] + "-"
	root := t.TempDir()
	mk := func(slot, peers string) *Driver {
		return New(Config{SandboxRoot: root, MaxSandboxes: 5, SandboxImage: image, Slot: slot, PeerSlots: peers,
			ContainerPrefix: prefix, SandboxTTL: time.Hour, MemoryLimit: "256m", CPULimit: "1", PIDsLimit: 64}, "docker")
	}
	t.Cleanup(func() {
		out, _ := exec.CommandContext(context.Background(), "docker", "ps", "-aq", "--filter", "name="+prefix).Output()
		for _, id := range strings.Fields(string(out)) {
			_ = exec.CommandContext(context.Background(), "docker", "rm", "-f", id).Run()
		}
		_ = exec.CommandContext(context.Background(), "docker", "network", "rm", prefix+"net").Run()
	})
	running := func(name string) bool {
		out, _ := exec.CommandContext(context.Background(), "docker", "inspect", "-f", "{{.State.Running}}", name).Output()
		return strings.TrimSpace(string(out)) == "true"
	}
	const (
		execZ = "22222222-2222-4222-8222-222222222222"
		execW = "33333333-3333-4333-8333-333333333333"
	)

	blue := mk("blue", "")
	zb, _, err := blue.Create(execZ)
	if err != nil {
		t.Skipf("cannot create a sandbox on this daemon: %v", err)
	}
	wb, _, err := blue.Create(execW)
	if err != nil {
		t.Fatal(err)
	}
	if err := syscall.Mkfifo(filepath.Join(zb.WorkspacePath, "hostile.fifo"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink("/dev/zero", filepath.Join(zb.WorkspacePath, "zero.link")); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(zb.WorkspacePath, ".reelbolt-taken-over-by"), []byte("green\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	for _, e := range []string{execZ, execW} {
		if err := blue.StopByExecution(e); err != nil {
			t.Fatal(err)
		}
	}

	// green starts with blue as a peer and takes Z over; blue drops it, the forged marker is ignored.
	green := mk("green", "blue")
	if _, _, err := green.Create(execZ); err != nil {
		t.Fatalf("takeover of the hostile sandbox: %v", err)
	}
	if green.readOwner(zb.ID) != "green" {
		t.Fatalf(".owners entry = %q, want green", green.readOwner(zb.ID))
	}
	if _, err := blue.GetByExecution(execZ); !errors.Is(err, ErrNotFound) {
		t.Fatalf("blue still indexes the taken-over sandbox: %v", err)
	}

	// The HTTP DELETE path: a stop. It answers success and removes neither workspace nor container.
	if err := green.StopByExecution(execZ); err != nil {
		t.Fatal(err)
	}
	if running(zb.ContainerName) {
		t.Fatal("the container should be stopped")
	}
	if _, err := os.Lstat(filepath.Join(zb.WorkspacePath, "hostile.fifo")); err != nil {
		t.Fatalf("a stop must keep the workspace: %v", err)
	}
	if out, _ := exec.CommandContext(context.Background(), "docker", "ps", "-aq", "--filter", "name="+zb.ContainerName).Output(); strings.TrimSpace(string(out)) == "" {
		t.Fatal("a stop must keep the container")
	}
	// Removal is DeleteByExecution (what the runner session uses), and it copes with the hostile files.
	if err := green.DeleteByExecution(execZ); err != nil {
		t.Fatalf("delete of the hostile sandbox: %v", err)
	}
	if _, err := os.Lstat(zb.WorkspacePath); !os.IsNotExist(err) {
		t.Fatalf("workspace should be gone: %v", err)
	}

	// Chain: green holds W (adopted when blue retired); a newer blue lists green as its peer.
	green2 := mk("green", "")
	green2.AdoptExistingWithPeers([]string{})
	if green2.readOwner(wb.ID) != "green" {
		t.Fatalf("W not adopted by green: %q", green2.readOwner(wb.ID))
	}
	chained := mk("blue", "green")
	chained.AdoptExistingWithPeers([]string{"green"})
	if _, err := chained.GetByExecution(execW); !errors.Is(err, ErrNotFound) {
		t.Fatalf("the chained executor must not index a sandbox its peer holds: %v", err)
	}
	if sb, err := green2.GetByExecution(execW); err != nil || sb == nil {
		t.Fatalf("the holder must serve W: %v", err)
	}
	// green retires; the survivor adopts with no peers and now serves W.
	chained.AdoptExistingWithPeers([]string{})
	if sb, err := chained.GetByExecution(execW); err != nil || sb == nil {
		t.Fatalf("the survivor must serve W after adopting: %v", err)
	}
	if chained.readOwner(wb.ID) != "blue" {
		t.Fatalf("W owner = %q, want blue", chained.readOwner(wb.ID))
	}
}
