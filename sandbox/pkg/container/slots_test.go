package container

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

const (
	slotExecA = "1aa8d6f8-c626-4de1-8f14-c04115ff763b"
	slotExecB = "2bb8d6f8-c626-4de1-8f14-c04115ff763b"
)

type slotFixture struct {
	mu      sync.Mutex
	removed []string
	stopped []string
	d       *Driver
	root    string
}

// newSlotFixture lists three containers: ours-to-adopt (no slot), "green" (a peer) and
// "retired" (a slot that is not a live peer).
func newSlotFixture(t *testing.T, slot string, peers string, image string) *slotFixture {
	t.Helper()
	f := &slotFixture{root: t.TempDir()}
	for _, id := range []string{"mine", "peer", "retired", "stray"} {
		if err := os.MkdirAll(filepath.Join(f.root, id), 0o770); err != nil {
			t.Fatal(err)
		}
	}
	cfg := Config{SandboxRoot: f.root, MaxSandboxes: 10, SandboxImage: image, MemoryLimit: "2g", CPULimit: "2", PIDsLimit: 256, Slot: slot, PeerSlots: peers}
	var fp string
	runner := func(ctx context.Context, args ...string) ([]byte, error) {
		f.mu.Lock()
		defer f.mu.Unlock()
		switch args[0] {
		case "image":
			return []byte("sha256:" + image), nil
		case "ps":
			return []byte(strings.Join([]string{
				"rf-sbx-mine|" + slotExecA + "|2026-10-10 07:47:40 +0000 UTC|" + fp + "|blue",
				"rf-sbx-peer|" + slotExecB + "|2026-10-10 07:47:40 +0000 UTC|" + fp + "|green",
				"rf-sbx-retired|3cc8d6f8-c626-4de1-8f14-c04115ff763b|2026-10-10 07:47:40 +0000 UTC|" + fp + "|old",
			}, "\n")), nil
		case "rm":
			f.removed = append(f.removed, args[len(args)-1])
		case "stop":
			f.stopped = append(f.stopped, args[len(args)-1])
		}
		return nil, nil
	}
	f.d = NewWithRunner(cfg, "docker", runner)
	fp = f.d.configFingerprint(f.d.containerPolicyArgs())
	return f
}

func (f *slotFixture) exists(id string) bool {
	_, err := os.Stat(filepath.Join(f.root, id))
	return err == nil
}

func TestPeerSlotContainersAreLeftAlone(t *testing.T) {
	f := newSlotFixture(t, "blue", "green", "rt")
	f.d.AdoptExisting()
	if strings.Contains(strings.Join(f.removed, ","), "peer") || strings.Contains(strings.Join(f.stopped, ","), "peer") {
		t.Fatalf("peer container touched: removed=%v stopped=%v", f.removed, f.stopped)
	}
	for _, s := range f.d.List() {
		if s.ContainerName == "rf-sbx-peer" {
			t.Fatal("peer container was indexed")
		}
	}
	if len(f.d.List()) != 2 {
		t.Fatalf("expected own + retired adopted, got %d", len(f.d.List()))
	}
	// A peer may be creating a workspace right now: no orphan removal at all.
	if !f.exists("peer") || !f.exists("stray") {
		t.Fatal("workspace folders removed while peers are live")
	}
}

func TestAdoptEndpointPassIsIdempotentAndTakesOver(t *testing.T) {
	f := newSlotFixture(t, "blue", "green", "rt")
	f.d.AdoptExisting()
	f.stopped = nil
	// The peer retired: adopt again with no peers.
	f.d.AdoptExistingWithPeers(nil)
	if len(f.d.List()) != 3 {
		t.Fatalf("takeover should index the former peer, got %d", len(f.d.List()))
	}
	if strings.Contains(strings.Join(f.stopped, ","), "rf-sbx-mine") || strings.Contains(strings.Join(f.stopped, ","), "rf-sbx-retired") {
		t.Fatalf("an already-indexed sandbox was stopped again: %v", f.stopped)
	}
	if strings.Contains(strings.Join(f.removed, ","), "rf-sbx-") {
		t.Fatalf("nothing should be removed: %v", f.removed)
	}
	if f.exists("stray") {
		t.Fatal("orphan folder should go once no peers remain")
	}
	if !f.exists("mine") || !f.exists("peer") || !f.exists("retired") {
		t.Fatal("a claimed workspace was removed")
	}
	f.stopped = nil
	f.d.AdoptExistingWithPeers(nil)
	if len(f.stopped) != 0 || len(f.d.List()) != 3 {
		t.Fatalf("second pass not idempotent: stopped=%v", f.stopped)
	}
}

func TestDifferentImageStaysAdoptable(t *testing.T) {
	f := newSlotFixture(t, "", "", "rt:old")
	// Containers carry a fingerprint made under the old image; the driver now runs rt:new.
	old := f.d.configFingerprint(f.d.containerPolicyArgs())
	f2 := newSlotFixture(t, "", "", "rt:new")
	f2.d.cfg.SandboxImage = "rt:new"
	if f2.d.configFingerprint(f2.d.containerPolicyArgs()) != old {
		t.Fatal("fingerprint depends on the image")
	}
}

func TestLegacyV1FingerprintStillAdoptable(t *testing.T) {
	f := newSlotFixture(t, "", "", "rt")
	legacy := f.d.legacyConfigFingerprint(f.d.containerPolicyArgs())
	runner := func(ctx context.Context, args ...string) ([]byte, error) {
		switch args[0] {
		case "image":
			return []byte("sha256:rt"), nil
		case "ps":
			return []byte("rf-sbx-mine|" + slotExecA + "|2026-10-10 07:47:40 +0000 UTC|" + legacy), nil
		}
		return nil, nil
	}
	f.d.run = runner
	f.d.AdoptExisting()
	if len(f.d.List()) != 1 {
		t.Fatal("a v1-fingerprinted sandbox of the same image must still be adopted")
	}
}

func TestEmptySlotAddsNoLabelAndSlotDoes(t *testing.T) {
	for _, slot := range []string{"", "blue"} {
		var runArgs []string
		base := fakeWorkspaceRunner(t)
		runner := func(ctx context.Context, args ...string) ([]byte, error) {
			if len(args) > 0 && args[0] == "run" {
				runArgs = args
			}
			return base(ctx, args...)
		}
		d := NewWithRunner(Config{SandboxRoot: t.TempDir(), MaxSandboxes: 2, Slot: slot}, "docker", runner)
		if _, _, err := d.Create("2f8e7d6c-5b4a-4392-8170-a1b2c3d4e5f6"); err != nil {
			t.Fatal(err)
		}
		has := strings.Contains(strings.Join(runArgs, " "), SlotLabel)
		if has != (slot != "") {
			t.Fatalf("slot %q: label present = %v", slot, has)
		}
	}
}

// peerTakeoverFixture sets up a kept container of slot "green" for slotExecA with its workspace on
// disk, seen from a "blue" executor that lists green as a peer.
func peerTakeoverFixture(t *testing.T, state string, liveExec bool) (*Driver, *[]string, string) {
	t.Helper()
	root := t.TempDir()
	if err := os.MkdirAll(filepath.Join(root, "kept"), 0o770); err != nil {
		t.Fatal(err)
	}
	var calls []string
	var d *Driver
	runner := func(ctx context.Context, args ...string) ([]byte, error) {
		calls = append(calls, strings.Join(args, " "))
		switch args[0] {
		case "image":
			return []byte("sha256:rt"), nil
		case "ps":
			return []byte("rf-sbx-kept|" + state + "|2026-10-10 07:47:40 +0000 UTC|" + d.configFingerprint(d.containerPolicyArgs()) + "|green"), nil
		case "exec":
			if liveExec {
				return []byte("live\n"), nil
			}
			return []byte("none\n"), nil
		}
		return nil, nil
	}
	d = NewWithRunner(Config{SandboxRoot: root, MaxSandboxes: 5, SandboxImage: "rt", Slot: "blue", PeerSlots: "green"}, "docker", runner)
	return d, &calls, root
}

func TestCreateTakesOverPeerSandbox(t *testing.T) {
	d, calls, root := peerTakeoverFixture(t, "exited", false)
	sb, created, err := d.Create(slotExecA)
	if err != nil || created {
		t.Fatalf("want takeover, got created=%v err=%v", created, err)
	}
	if sb.ContainerName != "rf-sbx-kept" || sb.WorkspacePath != filepath.Join(root, "kept") {
		t.Fatalf("sandbox = %+v", sb)
	}
	if got := d.readOwner("kept"); got != "blue" {
		t.Fatalf("owner entry = %q", got)
	}
	if _, err := os.Stat(filepath.Join(root, "kept", ".reelbolt-taken-over-by")); err == nil {
		t.Fatal("takeover must not write anything into the workspace")
	}
	for _, c := range *calls {
		if strings.HasPrefix(c, "run ") || strings.HasPrefix(c, "rm ") {
			t.Fatalf("takeover must not create or remove a container: %s", c)
		}
	}
	if len(d.List()) != 1 {
		t.Fatal("takeover should index exactly one sandbox")
	}
}

func TestCreateRefusesWhileLiveExecRunsInPeerSandbox(t *testing.T) {
	d, calls, _ := peerTakeoverFixture(t, "running", true)
	if _, _, err := d.Create(slotExecA); err != ErrPeerSandboxExists {
		t.Fatalf("want ErrPeerSandboxExists, got %v", err)
	}
	if d.readOwner("kept") != "" {
		t.Fatal("owner entry written despite refusal")
	}
	for _, c := range *calls {
		if strings.HasPrefix(c, "rm ") || strings.HasPrefix(c, "stop ") {
			t.Fatalf("peer container touched: %s", c)
		}
	}
	if len(d.List()) != 0 {
		t.Fatal("refused takeover must not index")
	}
}

func TestCreateRefusesWhenPeerWorkspaceIsGone(t *testing.T) {
	d, _, root := peerTakeoverFixture(t, "exited", false)
	_ = os.RemoveAll(filepath.Join(root, "kept"))
	if _, _, err := d.Create(slotExecA); err != ErrPeerSandboxExists {
		t.Fatalf("want ErrPeerSandboxExists, got %v", err)
	}
}

// The executor that lost a sandbox must forget it without touching the container, on every path.
func TestPeerForgetsTakenOverSandbox(t *testing.T) {
	for _, path := range []string{"janitor", "stop", "delete", "get"} {
		root := t.TempDir()
		ws := filepath.Join(root, "kept")
		_ = os.MkdirAll(ws, 0o770)
		var calls []string
		d := NewWithRunner(Config{SandboxRoot: root, MaxSandboxes: 5, Slot: "green", PeerSlots: "blue", SandboxTTL: time.Nanosecond}, "docker",
			func(ctx context.Context, args ...string) ([]byte, error) {
				calls = append(calls, strings.Join(args, " "))
				return nil, nil
			})
		d.sandboxes["kept"] = &Sandbox{ID: "kept", WorkflowExecutionID: slotExecA, ContainerName: "rf-sbx-kept", WorkspacePath: ws,
			LastActivity: time.Now().Add(-time.Hour)}
		d.executionIndex[slotExecA] = "kept"
		if err := d.writeOwner("kept", "blue"); err != nil {
			t.Fatal(err)
		}
		switch path {
		case "janitor":
			d.cleanupInactive()
			if len(d.List()) != 0 {
				t.Fatal("janitor kept a taken-over sandbox in the index")
			}
		case "stop":
			if err := d.StopByExecution(slotExecA); err != ErrNotFound {
				t.Fatalf("stop: %v", err)
			}
		case "delete":
			if err := d.DeleteByExecution(slotExecA); err != ErrNotFound {
				t.Fatalf("delete: %v", err)
			}
		case "get":
			if _, err := d.GetByExecution(slotExecA); err != ErrNotFound {
				t.Fatalf("get: %v", err)
			}
		}
		if len(calls) != 0 {
			t.Fatalf("%s touched the container: %v", path, calls)
		}
		if _, err := os.Stat(ws); err != nil {
			t.Fatalf("%s removed the workspace", path)
		}
	}
}

func TestOwnEntryDoesNotDropOwnSandbox(t *testing.T) {
	d := NewWithRunner(Config{SandboxRoot: t.TempDir(), Slot: "blue"}, "docker", nil)
	if err := d.writeOwner("kept", "blue"); err != nil {
		t.Fatal(err)
	}
	if d.ownedElsewhere("kept") {
		t.Fatal("an entry naming this executor must not drop its own sandbox")
	}
}

func TestCreateWithoutPeersNeverListsContainers(t *testing.T) {
	base := fakeWorkspaceRunner(t)
	runner := func(ctx context.Context, args ...string) ([]byte, error) {
		if args[0] == "ps" {
			t.Fatal("ps issued although no peers are configured")
		}
		return base(ctx, args...)
	}
	d := NewWithRunner(Config{SandboxRoot: t.TempDir(), MaxSandboxes: 2}, "docker", runner)
	if _, _, err := d.Create(slotExecA); err != nil {
		t.Fatal(err)
	}
}

func TestEnsureNetworkToleratesExistingNetwork(t *testing.T) {
	d := NewWithRunner(Config{SandboxNetwork: "sandbox-net"}, "docker", func(ctx context.Context, args ...string) ([]byte, error) {
		if args[0] == "network" && args[1] == "create" {
			return []byte("Error response from daemon: network with name sandbox-net already exists"), context.DeadlineExceeded
		}
		return []byte("true"), nil
	})
	if err := d.EnsureNetwork(); err != nil {
		t.Fatalf("existing network shared with a peer must be fine: %v", err)
	}
}

func TestParseSlots(t *testing.T) {
	if got := ParseSlots(" green, ,blue "); len(got) != 2 || got[0] != "green" || got[1] != "blue" {
		t.Fatalf("got %v", got)
	}
	if ParseSlots("") != nil {
		t.Fatal("empty must give no peers")
	}
}

func TestContainerPrefix(t *testing.T) {
	for _, ok := range []string{"rf-sbx-", "stack2-sbx-"} {
		if ValidateContainerPrefix(ok) != nil {
			t.Errorf("%q should be valid", ok)
		}
	}
	for _, bad := range []string{"", "rf-sbx", "RF-", "a_b-", "-"[:0] + "x y-"} {
		if ValidateContainerPrefix(bad) == nil {
			t.Errorf("%q should be invalid", bad)
		}
	}
	var filter string
	var removed []string
	d := NewWithRunner(Config{SandboxRoot: t.TempDir(), ContainerPrefix: "st2-"}, "docker", func(ctx context.Context, args ...string) ([]byte, error) {
		if args[0] == "ps" {
			filter = strings.Join(args, " ")
			return []byte("st2-abc|" + slotExecA + "|2026-10-10 07:47:40 +0000 UTC||\nrf-sbx-other|x|2026-10-10 07:47:40 +0000 UTC||"), nil
		}
		if args[0] == "rm" {
			removed = append(removed, args[len(args)-1])
		}
		return nil, nil
	})
	d.AdoptExisting()
	if !strings.Contains(filter, "name=st2-") {
		t.Fatalf("filter %q", filter)
	}
	for _, r := range removed {
		if strings.HasPrefix(r, "rf-sbx-") {
			t.Fatalf("foreign container purged: %v", removed)
		}
	}
	d.PurgeExisting()
	for _, r := range removed {
		if strings.HasPrefix(r, "rf-sbx-") {
			t.Fatalf("foreign container purged: %v", removed)
		}
	}
}
