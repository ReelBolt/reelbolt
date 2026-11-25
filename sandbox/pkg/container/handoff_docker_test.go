//go:build unix

package container

import (
	"context"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// A handoff sequence against a real Docker daemon; skipped when none is reachable or no local
// alpine:3 image exists (the test never pulls). A container held by green is left running by a
// blue that lists green; a blue that does not list it takes it over through the real ps, scan and
// stop.
func TestHandoffSequenceAgainstRealDocker(t *testing.T) {
	if _, err := exec.LookPath("docker"); err != nil {
		t.Skip("no docker binary")
	}
	if out, err := exec.CommandContext(context.Background(), "docker", "image", "inspect", "alpine:3", "--format", "{{.Id}}").CombinedOutput(); err != nil || len(out) == 0 {
		t.Skip("docker daemon or alpine:3 not available")
	}
	suffix := newExecMarker()[:8]
	prefix := "rf-sbx-t017" + suffix + "-"
	root := t.TempDir()
	mk := func(slot, peers string) *Driver {
		return New(Config{SandboxRoot: root, MaxSandboxes: 5, SandboxImage: "alpine:3", Slot: slot, PeerSlots: peers,
			ContainerPrefix: prefix, SandboxTTL: time.Hour}, "docker")
	}
	green := mk("green", "")
	id := "eeeeeeee-0000-4000-8000-" + suffix + "0001"
	if len(id) != 36 {
		id = "eeeeeeee-0000-4000-8000-000000000005"
	}
	if err := os.MkdirAll(filepath.Join(root, id), 0o770); err != nil {
		t.Fatal(err)
	}
	name := prefix + id
	runArgs := []string{"run", "-d", "--name", name, "--network", "none",
		"--label", ExecutionLabel + "=" + hoExecX,
		"--label", ConfigLabel + "=" + green.configFingerprint(green.containerPolicyArgs()),
		"--label", SlotLabel + "=green", "alpine:3", "sleep", "120"}
	if out, err := exec.CommandContext(context.Background(), "docker", runArgs...).CombinedOutput(); err != nil {
		t.Skipf("cannot start container: %v %s", err, out)
	}
	t.Cleanup(func() { _ = exec.CommandContext(context.Background(), "docker", "rm", "-f", name).Run() })
	if err := green.writeOwner(id, "green"); err != nil {
		t.Fatal(err)
	}
	running := func() bool {
		out, _ := exec.CommandContext(context.Background(), "docker", "inspect", "-f", "{{.State.Running}}", name).Output()
		return strings.TrimSpace(string(out)) == "true"
	}

	blue := mk("blue", "green")
	blue.AdoptExisting()
	blue.cleanupInactive()
	if !running() || len(blue.List()) != 0 {
		t.Fatal("blue touched a container green holds")
	}

	// Green is retired and blue is told so: takeover through the real ps, scan and stop.
	blue2 := mk("blue", "")
	blue2.AdoptExistingWithPeers([]string{})
	if blue2.readOwner(id) != "blue" || len(blue2.List()) != 1 {
		t.Fatalf("blue did not adopt: owner=%q", blue2.readOwner(id))
	}
	if running() {
		t.Fatal("adopted container should have been stopped for the normal restart path")
	}
	// A third executor creating for the same execution takes it over; no second container.
	c := mk("c", "blue")
	if _, created, err := c.Create(hoExecX); err != nil || created {
		t.Fatalf("takeover: created=%v err=%v", created, err)
	}
	if c.readOwner(id) != "c" {
		t.Fatalf("owner = %q", c.readOwner(id))
	}
	out, _ := exec.CommandContext(context.Background(), "docker", "ps", "-a", "-q", "--filter", "label="+ExecutionLabel+"="+hoExecX).Output()
	if n := len(strings.Fields(string(out))); n != 1 {
		t.Fatalf("%d containers for the execution, want 1", n)
	}
}
