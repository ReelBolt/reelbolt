package container

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
)

// A restart of the executor (every deploy) used to purge every sandbox, so the
// TTL a finished run was promised ended at the next deploy and a revision of an
// older run found nothing. Labelled containers whose workspace survived are now
// adopted; everything else is still purged.
func TestAdoptExistingKeepsLabelledSandboxesAndPurgesTheRest(t *testing.T) {
	root := t.TempDir()
	const keepExec = "1aa8d6f8-c626-4de1-8f14-c04115ff763b"
	for _, id := range []string{"keep", "legacy", "nodir-missing-skip", "orphan"} {
		if id == "nodir-missing-skip" {
			continue
		}
		if err := os.MkdirAll(filepath.Join(root, id), 0o770); err != nil {
			t.Fatal(err)
		}
	}

	cfg := Config{SandboxRoot: root, MaxSandboxes: 10, SandboxImage: "rt", MemoryLimit: "2g", CPULimit: "2", PIDsLimit: 256}
	var mu sync.Mutex
	var removed, stopped []string
	var fingerprint string
	runner := func(ctx context.Context, args ...string) ([]byte, error) {
		mu.Lock()
		defer mu.Unlock()
		switch args[0] {
		case "image":
			return []byte("sha256:abc"), nil
		case "ps":
			return []byte(strings.Join([]string{
				"rf-sbx-keep|" + keepExec + "|2026-10-10 07:47:40 +0000 UTC|" + fingerprint,
				"rf-sbx-legacy||2026-10-09 10:00:00 +0000 UTC|",
				"rf-sbx-nodir|9f8e7d6c-5b4a-4392-8170-a1b2c3d4e5f6|2026-10-10 07:00:00 +0000 UTC|" + fingerprint,
				"rf-sbx-orphan|3f8e7d6c-5b4a-4392-8170-a1b2c3d4e5f6|2026-10-10 07:00:00 +0000 UTC|stale-isolation-config",
			}, "\n")), nil
		case "rm":
			removed = append(removed, args[len(args)-1])
		case "stop":
			stopped = append(stopped, args[len(args)-1])
		}
		return nil, nil
	}

	d := NewWithRunner(cfg, "docker", runner)
	fingerprint = d.configFingerprint(d.containerPolicyArgs())
	d.AdoptExisting()

	var sb *Sandbox
	for _, s := range d.List() {
		if s.WorkflowExecutionID == keepExec {
			sb = s
		}
	}
	if sb == nil || len(d.List()) != 1 {
		t.Fatalf("expected exactly the labelled sandbox to be adopted, got %v", d.List())
	}
	if !sb.IsStopped || sb.ContainerName != "rf-sbx-keep" || sb.CreatedAt.Year() != 2026 || sb.CreatedAt.Hour() != 7 {
		t.Fatalf("adopted sandbox = %+v", sb)
	}
	if _, err := os.Stat(filepath.Join(root, "keep")); err != nil {
		t.Fatalf("the adopted workspace was deleted: %v", err)
	}
	for _, gone := range []string{"legacy", "orphan"} {
		if _, err := os.Stat(filepath.Join(root, gone)); !os.IsNotExist(err) {
			t.Fatalf("workspace %s should have been purged", gone)
		}
	}
	got := strings.Join(removed, ",")
	// rf-sbx-orphan has a workspace and a valid execution but was created under different
	// isolation settings: it must be purged, never adopted.
	if !strings.Contains(got, "rf-sbx-legacy") || !strings.Contains(got, "rf-sbx-nodir") ||
		!strings.Contains(got, "rf-sbx-orphan") || strings.Contains(got, "rf-sbx-keep") {
		t.Fatalf("removed = %v", removed)
	}
	if strings.Join(stopped, ",") != "rf-sbx-keep" {
		t.Fatalf("stopped = %v", stopped)
	}
}

func TestCreateLabelsTheContainerWithItsExecution(t *testing.T) {
	var runArgs []string
	base := fakeWorkspaceRunner(t)
	runner := func(ctx context.Context, args ...string) ([]byte, error) {
		if len(args) > 0 && args[0] == "run" {
			runArgs = args
		}
		return base(ctx, args...)
	}
	d := NewWithRunner(Config{SandboxRoot: t.TempDir(), MaxSandboxes: 2}, "docker", runner)
	const exec = "2f8e7d6c-5b4a-4392-8170-a1b2c3d4e5f6"
	if _, _, err := d.Create(exec); err != nil {
		t.Fatalf("Create: %v", err)
	}
	if !strings.Contains(strings.Join(runArgs, " "), "--label "+ExecutionLabel+"="+exec) {
		t.Fatalf("run args carry no execution label: %v", runArgs)
	}
}

func TestConfigFingerprintChangesWithIsolationSettings(t *testing.T) {
	image := "sha256:one"
	runner := func(ctx context.Context, args ...string) ([]byte, error) {
		if args[0] == "image" {
			return []byte(image), nil
		}
		return nil, nil
	}
	base := Config{SandboxImage: "rt", MemoryLimit: "2g", CPULimit: "2", PIDsLimit: 256, SandboxNetwork: "sandbox-net"}
	fp := func(c Config) string {
		d := NewWithRunner(c, "docker", runner)
		return d.configFingerprint(d.containerPolicyArgs())
	}
	ref := fp(base)
	if fp(base) != ref {
		t.Fatal("fingerprint is not deterministic")
	}
	looser := base
	looser.NetworkEgress = true
	bigger := base
	bigger.PIDsLimit = 4096
	for name, c := range map[string]Config{"egress": looser, "pids": bigger} {
		if fp(c) == ref {
			t.Fatalf("changing %s did not change the fingerprint", name)
		}
	}
	// The image is not part of the fingerprint: a per-release tag must not purge kept sandboxes.
	image = "sha256:rebuilt"
	if fp(base) != ref {
		t.Fatal("a different image changed the fingerprint")
	}
	other := base
	other.SandboxImage = "rt:next-release"
	if fp(other) != ref {
		t.Fatal("a different image tag changed the fingerprint")
	}
}
