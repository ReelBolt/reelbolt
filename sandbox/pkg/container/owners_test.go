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

const (
	ownerID1 = "11111111-1111-4111-8111-111111111111"
)

// plantedFiles writes the attack shapes of the T-011 PoC into a workspace under the old marker's
// name: a FIFO, a /dev/zero symlink, a sparse file and a forged owner.
func plantAttacks(t *testing.T, ws string) {
	t.Helper()
	m := filepath.Join(ws, ".reelbolt-taken-over-by")
	if err := os.WriteFile(m, []byte("green\n"), 0o660); err != nil {
		t.Fatal(err)
	}
	for _, n := range []string{"fifo", "zero", "sparse"} {
		p := filepath.Join(ws, ".reelbolt-taken-over-by-"+n)
		switch n {
		case "fifo":
			if err := syscall.Mkfifo(p, 0o660); err != nil {
				t.Fatal(err)
			}
		case "zero":
			_ = os.Symlink("/dev/zero", p)
		case "sparse":
			f, _ := os.Create(p)
			_ = f.Truncate(1 << 40)
			_ = f.Close()
		}
	}
}

func ownerFixture(t *testing.T, slot, peers string) (*Driver, string) {
	root := t.TempDir()
	d := NewWithRunner(Config{SandboxRoot: root, MaxSandboxes: 5, Slot: slot, PeerSlots: peers, SandboxTTL: time.Nanosecond}, "docker",
		func(ctx context.Context, args ...string) ([]byte, error) { return nil, nil })
	ws := filepath.Join(root, ownerID1)
	if err := os.MkdirAll(ws, 0o770); err != nil {
		t.Fatal(err)
	}
	d.sandboxes[ownerID1] = &Sandbox{ID: ownerID1, WorkflowExecutionID: slotExecA, ContainerName: "rf-sbx-" + ownerID1,
		WorkspacePath: ws, LastActivity: time.Now().Add(-time.Hour), IsStopped: true}
	d.executionIndex[slotExecA] = ownerID1
	return d, ws
}

// A sandbox that plants the old marker (or a FIFO, a /dev/zero symlink, a sparse file in its
// place) has no effect: the executor never reads inside a workspace for ownership.
func TestWorkspaceFilesNamedLikeOldMarkerHaveNoEffect(t *testing.T) {
	d, ws := ownerFixture(t, "blue", "green")
	plantAttacks(t, ws)
	done := make(chan error, 1)
	go func() {
		_, err := d.GetByExecution(slotExecA)
		d.cleanupInactive() // TTL is 1ns: the sandbox must be deleted by the janitor, not hidden
		done <- err
	}()
	select {
	case err := <-done:
		if err != nil {
			t.Fatalf("forged marker made the sandbox invisible: %v", err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("executor stalled on a planted special file")
	}
	if len(d.List()) != 0 {
		t.Fatal("forged marker escaped the TTL")
	}
}

func TestPlantedFilesCannotTakeSandboxOverArchiveOrWrite(t *testing.T) {
	d, ws := ownerFixture(t, "blue", "green")
	if err := os.WriteFile(filepath.Join(ws, ".reelbolt-taken-over-by"), []byte("green\n"), 0o660); err != nil {
		t.Fatal(err)
	}
	if d.ownedElsewhere(ownerID1) {
		t.Fatal("a workspace file changed ownership")
	}
}

func TestForgedMarkerCountsAgainstCapAndDelete(t *testing.T) {
	d, ws := ownerFixture(t, "blue", "green")
	_ = os.WriteFile(filepath.Join(ws, ".reelbolt-taken-over-by"), []byte("green\n"), 0o660)
	if err := d.DeleteByExecution(slotExecA); err != nil {
		t.Fatalf("delete with forged marker: %v", err)
	}
	if _, err := os.Stat(ws); err == nil {
		t.Fatal("workspace survived delete")
	}
}

// The owner entry write must not follow a symlink planted at the entry path or the temp path.
func TestWriteOwnerRefusesPlantedSymlinks(t *testing.T) {
	d, _ := ownerFixture(t, "blue", "green")
	if err := os.MkdirAll(d.ownersDir(), 0o700); err != nil {
		t.Fatal(err)
	}
	victim := filepath.Join(t.TempDir(), "victim")
	_ = os.WriteFile(victim, []byte("keep"), 0o600)
	_ = os.Symlink(victim, filepath.Join(d.ownersDir(), ownerID1))
	if err := d.writeOwner(ownerID1, "blue"); err != nil {
		t.Fatalf("rename over a symlink entry should succeed: %v", err)
	}
	if b, _ := os.ReadFile(victim); string(b) != "keep" {
		t.Fatalf("write followed a symlink: %q", b)
	}
	if d.readOwner(ownerID1) != "blue" {
		t.Fatal("entry not replaced")
	}
}

func TestReadOwnerIgnoresHostileEntries(t *testing.T) {
	d, _ := ownerFixture(t, "blue", "green")
	dir := d.ownersDir()
	_ = os.MkdirAll(dir, 0o700)
	p := filepath.Join(dir, ownerID1)
	_ = syscall.Mkfifo(p, 0o600)
	if d.readOwner(ownerID1) != "" {
		t.Fatal("fifo entry honoured")
	}
	_ = os.Remove(p)
	_ = os.Symlink("/dev/zero", p)
	if d.readOwner(ownerID1) != "" {
		t.Fatal("symlink entry honoured")
	}
	_ = os.Remove(p)
	_ = os.WriteFile(p, []byte(strings.Repeat("a", 4096)), 0o600)
	if d.readOwner(ownerID1) != "" {
		t.Fatal("oversized entry honoured")
	}
	_ = os.WriteFile(p, []byte("../x\n"), 0o600)
	if d.readOwner(ownerID1) != "" {
		t.Fatal("malformed slot honoured")
	}
	for _, bad := range []string{"../etc", "a/b", "", ".hidden", "x.tmp-1"} {
		if _, ok := d.ownerPath(bad); ok {
			t.Fatalf("id %q accepted", bad)
		}
	}
}

func TestEmptySlotNeverTouchesOwners(t *testing.T) {
	d, ws := ownerFixture(t, "", "")
	_ = os.WriteFile(filepath.Join(ws, ".reelbolt-taken-over-by"), []byte("green\n"), 0o660)
	d.removeOwner(ownerID1)
	d.pruneOwners(nil)
	d.dropTakenOver()
	if _, err := d.GetByExecution(slotExecA); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(d.ownersDir()); err == nil {
		t.Fatal("owners dir created with SANDBOX_SLOT empty")
	}
}

func TestAdoptSkipsOwnersDirAndPrunesStaleEntries(t *testing.T) {
	f := newSlotFixture(t, "blue", "", "rt")
	if err := f.d.writeOwner("gone", "blue"); err != nil {
		t.Fatal(err)
	}
	if err := f.d.writeOwner("mine", "blue"); err != nil {
		t.Fatal(err)
	}
	f.d.AdoptExistingWithPeers(nil)
	if _, err := os.Stat(f.d.ownersDir()); err != nil {
		t.Fatalf("orphan sweep removed the owners dir: %v", err)
	}
	if f.d.readOwner("gone") != "" {
		t.Fatal("stale entry not pruned")
	}
	if f.d.readOwner("mine") != "blue" {
		t.Fatal("live entry pruned")
	}
}

// A takes from B; A retires; C adopts. C must hold the sandbox and re-own the entry.
func TestStaleOwnerChainAThenCAdopts(t *testing.T) {
	f := newSlotFixture(t, "slotc", "", "rt")
	// The container was created by slot "old"; slot A took it over, then A retired.
	f.d.run = func(ctx context.Context, args ...string) ([]byte, error) {
		if args[0] == "ps" {
			fp := f.d.configFingerprint(f.d.containerPolicyArgs())
			return []byte("rf-sbx-mine|" + slotExecA + "|2026-10-10 07:47:40 +0000 UTC|" + fp + "|slotb"), nil
		}
		return nil, nil
	}
	if err := f.d.writeOwner("mine", "slota"); err != nil {
		t.Fatal(err)
	}
	f.d.AdoptExistingWithPeers([]string{})
	if _, err := f.d.GetByExecution(slotExecA); errors.Is(err, ErrNotFound) {
		t.Fatal("C answered not-found for a sandbox it adopted")
	}
	if got := f.d.readOwner("mine"); got != "slotc" {
		t.Fatalf("adoption did not re-own: entry = %q", got)
	}
	// An entry naming another slot is honoured whether or not that slot is a listed peer.
	d2, _ := ownerFixture(t, "slotc", "")
	_ = d2.writeOwner(ownerID1, "slota")
	if !d2.ownedElsewhere(ownerID1) {
		t.Fatal("an entry naming another slot must be honoured")
	}
}

func TestAdoptRequiresTakenOverBySelfSurvivesRestart(t *testing.T) {
	f := newSlotFixture(t, "blue", "green", "rt")
	// "peer" is labelled green but blue took it over: after a restart blue keeps it.
	_ = f.d.writeOwner("peer", "blue")
	f.d.AdoptExistingWithPeers([]string{"green"})
	found := false
	for _, sb := range f.d.List() {
		if sb.ID == "peer" {
			found = true
		}
	}
	if !found {
		t.Fatal("a sandbox this slot took over was dropped on restart")
	}
}

// hasLiveExec fails closed.
func TestHasLiveExecFailsClosed(t *testing.T) {
	cases := map[string]struct {
		out  string
		err  error
		live bool
	}{
		"none":    {"none\n", nil, false},
		"live":    {"live\n", nil, true},
		"error":   {"", errors.New("daemon down"), true},
		"timeout": {"", context.DeadlineExceeded, true},
		"garbage": {"", nil, true},
		"exit1":   {"", exec.CommandContext(context.Background(), "sh", "-c", "exit 1").Run(), true},
	}
	for name, c := range cases {
		d := NewWithRunner(Config{}, "docker", func(ctx context.Context, args ...string) ([]byte, error) {
			return []byte(c.out), c.err
		})
		if got := d.hasLiveExec("x"); got != c.live {
			t.Fatalf("%s: hasLiveExec = %v, want %v", name, got, c.live)
		}
	}
}

func TestHasLiveExecIsBounded(t *testing.T) {
	d := NewWithRunner(Config{}, "docker", func(ctx context.Context, args ...string) ([]byte, error) {
		if _, ok := ctx.Deadline(); !ok {
			t.Error("scan runs without a deadline")
		}
		return nil, ctx.Err()
	})
	_ = d.hasLiveExec("x")
}

func TestTakeoverRefusedWhenScanFails(t *testing.T) {
	d, _, root := peerTakeoverFixture(t, "running", false)
	inner := d.run
	d.run = func(ctx context.Context, args ...string) ([]byte, error) {
		if args[0] == "exec" {
			return nil, errors.New("exec failed")
		}
		return inner(ctx, args...)
	}
	if _, _, err := d.Create(slotExecA); !errors.Is(err, ErrPeerSandboxExists) {
		t.Fatalf("want ErrPeerSandboxExists, got %v", err)
	}
	if d.readOwner("kept") != "" {
		t.Fatal("took over despite a failed scan")
	}
	_ = root
}
