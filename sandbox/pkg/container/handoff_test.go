//go:build unix

package container

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"testing"
	"time"
)

const (
	hoExecX = "1aa8d6f8-c626-4de1-8f14-c04115ff763b"
	hoIDX   = "aaaaaaaa-0000-4000-8000-000000000001"
)

type fakeCtr struct {
	exec, slot, cfg, state string
}

// fakeDaemon is one container daemon shared by several executors on one SANDBOX_ROOT.
type fakeDaemon struct {
	mu      sync.Mutex
	root    string
	ctrs    map[string]*fakeCtr // by name
	calls   []string
	psErr   bool
	scanOut string
	scanErr error
}

func newFakeDaemon(t *testing.T) *fakeDaemon {
	return &fakeDaemon{root: t.TempDir(), ctrs: map[string]*fakeCtr{}, scanOut: "none\n"}
}

func (f *fakeDaemon) runner(d **Driver) Runner {
	return func(ctx context.Context, args ...string) ([]byte, error) {
		f.mu.Lock()
		defer f.mu.Unlock()
		f.calls = append(f.calls, strings.Join(args, " "))
		switch args[0] {
		case "image":
			return []byte("sha256:rt"), nil
		case "ps":
			if f.psErr {
				return nil, errors.New("daemon down")
			}
			filter, takeover := "", false
			for i, a := range args {
				if a == "--filter" && strings.HasPrefix(args[i+1], "label="+ExecutionLabel+"=") {
					filter = strings.TrimPrefix(args[i+1], "label="+ExecutionLabel+"=")
				}
				if strings.Contains(a, ".State") {
					takeover = true
				}
			}
			var lines []string
			for name, c := range f.ctrs {
				if filter != "" && c.exec != filter {
					continue
				}
				second := c.exec
				if takeover {
					second = c.state
				}
				lines = append(lines, name+"|"+second+"|2026-10-10 07:47:40 +0000 UTC|"+c.cfg+"|"+c.slot)
			}
			return []byte(strings.Join(lines, "\n")), nil
		case "run":
			c := &fakeCtr{state: "running", cfg: (*d).configFingerprint((*d).containerPolicyArgs())}
			name := ""
			for i, a := range args {
				switch {
				case a == "--name":
					name = args[i+1]
				case a == "--label" && strings.HasPrefix(args[i+1], ExecutionLabel+"="):
					c.exec = strings.TrimPrefix(args[i+1], ExecutionLabel+"=")
				case a == "--label" && strings.HasPrefix(args[i+1], SlotLabel+"="):
					c.slot = strings.TrimPrefix(args[i+1], SlotLabel+"=")
				case a == "-v":
					ws := strings.SplitN(args[i+1], ":", 2)[0]
					_ = os.MkdirAll(ws, 0o770)
					_ = os.WriteFile(filepath.Join(ws, "package.json"), []byte("{}"), 0o644)
				}
			}
			f.ctrs[name] = c
		case "rm":
			delete(f.ctrs, args[len(args)-1])
		case "stop":
			if c := f.ctrs[args[len(args)-1]]; c != nil {
				c.state = "exited"
			}
		case "start":
			if c := f.ctrs[args[len(args)-1]]; c != nil {
				c.state = "running"
			}
		case "exec":
			return []byte(f.scanOut), f.scanErr
		}
		return nil, nil
	}
}

func (f *fakeDaemon) executor(slot, peers string, ttl time.Duration) *Driver {
	var d *Driver
	d = NewWithRunner(Config{SandboxRoot: f.root, MaxSandboxes: 10, SandboxImage: "rt", Slot: slot, PeerSlots: peers, SandboxTTL: ttl}, "docker", f.runner(&d))
	return d
}

// seed plants a kept container for execution exec with its workspace, held by label slot.
func (f *fakeDaemon) seed(d *Driver, id, exec, slot, state string) {
	_ = os.MkdirAll(filepath.Join(f.root, id), 0o770)
	f.ctrs["rf-sbx-"+id] = &fakeCtr{exec: exec, slot: slot, state: state, cfg: d.configFingerprint(d.containerPolicyArgs())}
}

// touching reports calls that act on a container (everything but ps/image).
func (f *fakeDaemon) acted() []string {
	f.mu.Lock()
	defer f.mu.Unlock()
	var out []string
	for _, c := range f.calls {
		if !strings.HasPrefix(c, "ps ") && !strings.HasPrefix(c, "image ") {
			out = append(out, c)
		}
	}
	return out
}

func (f *fakeDaemon) reset() { f.mu.Lock(); f.calls = nil; f.mu.Unlock() }

func (f *fakeDaemon) state(id string) string {
	f.mu.Lock()
	defer f.mu.Unlock()
	if c := f.ctrs["rf-sbx-"+id]; c != nil {
		return c.state
	}
	return "gone"
}

// blue creates, green adopts, new blue starts with peer green: the sandbox is untouched.
func TestHandoffBlueCreatesGreenAdoptsNewBlueLeavesIt(t *testing.T) {
	f := newFakeDaemon(t)
	blue := f.executor("blue", "", time.Hour)
	sb, _, err := blue.Create(hoExecX)
	if err != nil {
		t.Fatal(err)
	}
	if got := blue.readOwner(sb.ID); got != "blue" {
		t.Fatalf("creator did not record itself as holder: %q", got)
	}
	green := f.executor("green", "", time.Hour)
	green.AdoptExistingWithPeers([]string{})
	if got := green.readOwner(sb.ID); got != "green" || len(green.List()) != 1 {
		t.Fatalf("green did not adopt: owner=%q list=%d", got, len(green.List()))
	}
	f.reset()
	newBlue := f.executor("blue", "green", time.Hour)
	newBlue.AdoptExisting()
	if a := f.acted(); len(a) != 0 || len(newBlue.List()) != 0 {
		t.Fatalf("new blue touched green's sandbox: %v", a)
	}
	// Even the janitor and a lookup stay off it: the entry names green.
	newBlue.cleanupInactive()
	if _, err := newBlue.GetByExecution(hoExecX); !errors.Is(err, ErrNotFound) {
		t.Fatalf("lookup: %v", err)
	}
	if a := f.acted(); len(a) != 0 {
		t.Fatalf("calls: %v", a)
	}
	if f.state(sb.ID) == "gone" {
		t.Fatal("container removed")
	}
}

// An executor started before its peer, with an empty peer list, honours the peer's later entry.
func TestHandoffExecutorStartedBeforePeerHonoursItsEntry(t *testing.T) {
	f := newFakeDaemon(t)
	blue := f.executor("blue", "", time.Nanosecond)
	sb, _, err := blue.Create(hoExecX)
	if err != nil {
		t.Fatal(err)
	}
	blue.sandboxes[sb.ID].LastActivity = time.Now().Add(-time.Hour)
	green := f.executor("green", "blue", time.Hour)
	if _, _, err := green.Create(hoExecX); err != nil {
		t.Fatalf("green takeover: %v", err)
	}
	f.reset()
	blue.cleanupInactive() // TTL is 1ns
	if a := f.acted(); len(a) != 0 {
		t.Fatalf("blue acted on a sandbox green holds: %v", a)
	}
	if len(blue.List()) != 0 || len(green.List()) != 1 {
		t.Fatalf("index: blue=%d green=%d", len(blue.List()), len(green.List()))
	}
	if !dirExists(filepath.Join(f.root, sb.ID)) {
		t.Fatal("workspace deleted")
	}
}

// B created it, A takes from B, A retires, C adopts (peers empty or naming only B).
func TestHandoffTakeFromBThenARetiresThenCAdopts(t *testing.T) {
	for _, peers := range [][]string{{}, {"b"}} {
		f := newFakeDaemon(t)
		b := f.executor("b", "", time.Hour)
		sb, _, err := b.Create(hoExecX)
		if err != nil {
			t.Fatal(err)
		}
		a := f.executor("a", "b", time.Hour)
		if _, _, err := a.Create(hoExecX); err != nil {
			t.Fatal(err)
		}
		if a.readOwner(sb.ID) != "a" {
			t.Fatal("A did not re-own")
		}
		c := f.executor("c", "", time.Hour)
		c.AdoptExistingWithPeers(peers)
		if c.readOwner(sb.ID) != "c" || len(c.List()) != 1 {
			t.Fatalf("peers %v: C did not take the sandbox: owner=%q", peers, c.readOwner(sb.ID))
		}
		if _, err := c.GetByExecution(hoExecX); err != nil {
			t.Fatalf("C cannot reach its sandbox: %v", err)
		}
	}
}

// An adopt with [] is the operator saying nobody else is live: everything is taken and re-owned.
func TestHandoffAdoptWithEmptyListTakesEverything(t *testing.T) {
	f := newFakeDaemon(t)
	d := f.executor("blue", "", time.Hour)
	f.seed(d, hoIDX, hoExecX, "green", "running")
	_ = d.writeOwner(hoIDX, "green")
	d.AdoptExistingWithPeers([]string{})
	if d.readOwner(hoIDX) != "blue" || len(d.List()) != 1 {
		t.Fatalf("owner=%q list=%d", d.readOwner(hoIDX), len(d.List()))
	}
}

// A policy change (fingerprint differs) purges only what nobody live holds.
func TestHandoffPolicyChangePurgeSkipsLivePeerHeldSandbox(t *testing.T) {
	f := newFakeDaemon(t)
	d := f.executor("blue", "green", time.Hour)
	f.seed(d, hoIDX, hoExecX, "blue", "running") // label blue (reused name), entry green
	f.ctrs["rf-sbx-"+hoIDX].cfg = "stale-policy"
	_ = d.writeOwner(hoIDX, "green")
	other := "bbbbbbbb-0000-4000-8000-000000000002"
	f.seed(d, other, "2bb8d6f8-c626-4de1-8f14-c04115ff763b", "blue", "running")
	f.ctrs["rf-sbx-"+other].cfg = "stale-policy"
	_ = d.writeOwner(other, "blue")
	d.AdoptExisting()
	if f.state(hoIDX) != "running" || !dirExists(filepath.Join(f.root, hoIDX)) {
		t.Fatal("purge hit a sandbox a live peer holds")
	}
	if f.state(other) != "gone" {
		t.Fatal("this slot's own stale-policy sandbox should be purged")
	}
}

// A retired name reused: old blue's entries are not acted on by new blue once green re-owned them,
// and blue's own adopted entries are rewritten so a later blue does not trust them either.
func TestHandoffReusedSlotNameDoesNotActOnReownedEntries(t *testing.T) {
	f := newFakeDaemon(t)
	oldBlue := f.executor("blue", "", time.Hour)
	sb, _, err := oldBlue.Create(hoExecX)
	if err != nil {
		t.Fatal(err)
	}
	green := f.executor("green", "blue", time.Hour)
	if _, _, err := green.Create(hoExecX); err != nil {
		t.Fatal(err)
	}
	f.reset()
	newBlue := f.executor("blue", "green", time.Hour)
	newBlue.AdoptExisting()
	newBlue.cleanupInactive()
	if a := f.acted(); len(a) != 0 || green.readOwner(sb.ID) != "green" {
		t.Fatalf("new blue acted: %v owner=%q", a, green.readOwner(sb.ID))
	}
	// green restarts later with an empty list while new blue is gone: it keeps the sandbox
	green2 := f.executor("green", "", time.Hour)
	green2.AdoptExisting()
	if len(green2.List()) != 1 {
		t.Fatal("green lost its sandbox")
	}
}

// No entry and no label: legacy, unheld. A create takes the existing container, never a second.
func TestHandoffCreateTakesLegacyContainerNoSecondContainer(t *testing.T) {
	f := newFakeDaemon(t)
	d := f.executor("blue", "", time.Hour)
	f.seed(d, hoIDX, hoExecX, "", "exited")
	sb, created, err := d.Create(hoExecX)
	if err != nil || created || sb.ID != hoIDX {
		t.Fatalf("sb=%+v created=%v err=%v", sb, created, err)
	}
	if d.readOwner(hoIDX) != "blue" {
		t.Fatal("not re-owned")
	}
	for _, c := range f.acted() {
		if strings.HasPrefix(c, "run ") {
			t.Fatalf("a second container was created: %s", c)
		}
	}
}

// A retired slot's container (label not listed anywhere) also goes through takeover.
func TestHandoffCreateTakesRetiredSlotContainer(t *testing.T) {
	f := newFakeDaemon(t)
	d := f.executor("blue", "green", time.Hour)
	f.seed(d, hoIDX, hoExecX, "old", "running")
	f.scanOut = "live\n"
	if _, _, err := d.Create(hoExecX); !errors.Is(err, ErrPeerSandboxExists) {
		t.Fatalf("live exec must refuse: %v", err)
	}
	f.scanOut = "none\n"
	if _, created, err := d.Create(hoExecX); err != nil || created {
		t.Fatalf("created=%v err=%v", created, err)
	}
	stopped := false
	for _, c := range f.acted() {
		stopped = stopped || strings.HasPrefix(c, "stop ")
	}
	if !stopped {
		t.Fatal("taken-over container was not stopped")
	}
}

// Holder is this slot but the sandbox is not indexed: used as it is, no scan.
func TestHandoffCreateUsesOwnContainerWithoutScan(t *testing.T) {
	f := newFakeDaemon(t)
	d := f.executor("blue", "", time.Hour)
	f.seed(d, hoIDX, hoExecX, "green", "running")
	_ = d.writeOwner(hoIDX, "blue")
	if _, created, err := d.Create(hoExecX); err != nil || created {
		t.Fatalf("created=%v err=%v", created, err)
	}
	for _, c := range f.acted() {
		if strings.HasPrefix(c, "exec ") || strings.HasPrefix(c, "stop ") || strings.HasPrefix(c, "run ") {
			t.Fatalf("own container was scanned, stopped or duplicated: %s", c)
		}
	}
}

func TestHandoffFailedPsRefusesAndNeverCreates(t *testing.T) {
	f := newFakeDaemon(t)
	d := f.executor("blue", "", time.Hour)
	f.psErr = true
	if _, _, err := d.Create(hoExecX); err == nil {
		t.Fatal("create succeeded with a failed ps")
	}
	for _, c := range f.acted() {
		if strings.HasPrefix(c, "run ") {
			t.Fatalf("container created after failed ps: %s", c)
		}
	}
}

// Empty slot: no ps, no entries, same as before.
func TestHandoffEmptySlotNeverListsOrWritesOwners(t *testing.T) {
	f := newFakeDaemon(t)
	d := f.executor("", "", time.Hour)
	if _, _, err := d.Create(hoExecX); err != nil {
		t.Fatal(err)
	}
	for _, c := range f.calls {
		if strings.HasPrefix(c, "ps ") {
			t.Fatalf("empty slot listed containers: %s", c)
		}
	}
	if _, err := os.Stat(filepath.Join(f.root, OwnersDirName)); err == nil {
		t.Fatal("empty slot wrote an owners dir")
	}
}

// The scan is capped inside the container and in capture.
func TestHandoffScanOutputIsCapped(t *testing.T) {
	var script string
	d := NewWithRunner(Config{}, "docker", func(ctx context.Context, args ...string) ([]byte, error) {
		script = args[len(args)-1]
		return []byte(strings.Repeat("none\n", 1<<20)), nil
	})
	if !d.hasLiveExec("x") {
		t.Fatal("a flooding scan must read as may-be-live")
	}
	if !strings.Contains(script, "head -c 64") {
		t.Fatalf("scan not capped in the container: %s", script)
	}
	out, _ := d.runCapped(context.Background(), 64, "x")
	if len(out) != 64 {
		t.Fatalf("capture not capped: %d", len(out))
	}
	w := &limitedWriter{max: 8}
	if n, _ := w.Write([]byte(strings.Repeat("a", 1000))); n != 1000 || len(w.buf) != 8 {
		t.Fatalf("limitedWriter kept %d", len(w.buf))
	}
}

// The real CLI runner never buffers more than the cap, however much the process prints.
func TestLimitedCLIRunnerCapsRealProcessOutput(t *testing.T) {
	out, err := limitedCLIRunner("sh")(context.Background(), 64, "-c", "yes flood | head -c 20000000")
	if err != nil || len(out) != 64 {
		t.Fatalf("len=%d err=%v", len(out), err)
	}
}

// A hung scan on one execution does not stall a Create for another.
func TestHandoffHungScanDoesNotStallOtherCreates(t *testing.T) {
	f := newFakeDaemon(t)
	d := f.executor("blue", "", time.Hour)
	f.seed(d, hoIDX, hoExecX, "green", "running")
	release := make(chan struct{})
	inner := d.run
	d.run = func(ctx context.Context, args ...string) ([]byte, error) {
		if args[0] == "exec" {
			<-release
			return []byte("none"), nil
		}
		return inner(ctx, args...)
	}
	first := make(chan struct{})
	go func() { defer close(first); _, _, _ = d.Create(hoExecX) }()
	time.Sleep(200 * time.Millisecond)
	done := make(chan error, 1)
	go func() { _, _, err := d.Create("2bb8d6f8-c626-4de1-8f14-c04115ff763b"); done <- err }()
	select {
	case err := <-done:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("an unrelated Create stalled behind a hung scan")
	}
	close(release)
	<-first // never leave a writer running into the temp dir cleanup
}

// A container named <prefix>.. must not make the adopt pass delete the parent of SANDBOX_ROOT.
func TestHandoffAdoptRejectsDotDotId(t *testing.T) {
	parent := t.TempDir()
	sentinel := filepath.Join(parent, "keep.txt")
	if err := os.WriteFile(sentinel, []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	root := filepath.Join(parent, "root")
	_ = os.MkdirAll(root, 0o770)
	f := &fakeDaemon{root: root, ctrs: map[string]*fakeCtr{}, scanOut: "none\n"}
	d := f.executor("blue", "", time.Hour)
	for _, slot := range []string{"blue", ""} {
		f.ctrs["rf-sbx-.."] = &fakeCtr{exec: hoExecX, slot: slot, state: "running", cfg: d.configFingerprint(d.containerPolicyArgs())}
		d.AdoptExisting()
		if _, err := os.Stat(sentinel); err != nil {
			t.Fatalf("slot %q: parent of the sandbox root was deleted", slot)
		}
	}
	if f.state("..") != "gone" {
		t.Fatal("the malformed container should be removed")
	}
}

func TestHandoffPruneOwnersOnlyWellFormedAndOldTemps(t *testing.T) {
	f := newFakeDaemon(t)
	d := f.executor("blue", "", time.Hour)
	idGone, idCtr := "cccccccc-0000-4000-8000-000000000003", "dddddddd-0000-4000-8000-000000000004"
	for _, id := range []string{idGone, idCtr} {
		if err := d.writeOwner(id, "blue"); err != nil {
			t.Fatal(err)
		}
	}
	dir := d.ownersDir()
	young := filepath.Join(dir, idGone+".tmp-1-abc")
	old := filepath.Join(dir, idGone+".tmp-2-abc")
	foreign := filepath.Join(dir, "notes.txt")
	for _, p := range []string{young, old, foreign} {
		_ = os.WriteFile(p, []byte("blue"), 0o600)
	}
	past := time.Now().Add(-2 * time.Hour)
	_ = os.Chtimes(old, past, past)
	d.pruneOwners(map[string]bool{idCtr: true})
	for p, want := range map[string]bool{filepath.Join(dir, idGone): false, filepath.Join(dir, idCtr): true, young: true, old: false, foreign: true} {
		if _, err := os.Stat(p); (err == nil) != want {
			t.Fatalf("%s exists=%v want %v", filepath.Base(p), err == nil, want)
		}
	}
}

// A FIFO planted in a workspace hangs no file call.
func TestFilesRefuseFIFOWithoutHanging(t *testing.T) {
	d, ws := ownerFixture(t, "", "")
	if err := syscall.Mkfifo(filepath.Join(ws, "pipe"), 0o660); err != nil {
		t.Fatal(err)
	}
	d.cfg.SandboxTTL = time.Hour
	d.sandboxes[ownerID1].LastActivity = time.Now()
	calls := map[string]func() error{
		"read": func() error { _, err := d.ReadFile(slotExecA, "pipe"); return err },
		"raw": func() error {
			f, _, err := d.OpenRawFile(slotExecA, "pipe")
			if f != nil {
				_ = f.Close()
			}
			return err
		},
		"list":  func() error { _, err := d.ListFiles(slotExecA, "pipe"); return err },
		"write": func() error { return d.WriteFile(slotExecA, "pipe", []byte("x")) },
	}
	for name, call := range calls {
		done := make(chan error, 1)
		go func() { done <- call() }()
		select {
		case err := <-done:
			if err == nil && name != "list" {
				t.Fatalf("%s: a FIFO was accepted", name)
			}
		case <-time.After(5 * time.Second):
			t.Fatalf("%s hung on a FIFO", name)
		}
	}
}

// T-019 regressions.

// Two Creates for one execution at once yield one container and the same sandbox.
func TestHandoffConcurrentCreatesSameExecShareOneSandbox(t *testing.T) {
	f := newFakeDaemon(t)
	d := f.executor("blue", "", time.Hour)
	inner := d.run
	d.run = func(ctx context.Context, args ...string) ([]byte, error) {
		if args[0] == "run" {
			time.Sleep(300 * time.Millisecond)
		}
		return inner(ctx, args...)
	}
	ids := make(chan string, 2)
	errs := make(chan error, 2)
	for i := 0; i < 2; i++ {
		go func() {
			sb, _, err := d.Create(hoExecX)
			if err != nil {
				errs <- err
				return
			}
			ids <- sb.ID
		}()
	}
	var got [2]string
	for i := 0; i < 2; i++ {
		select {
		case got[i] = <-ids:
		case err := <-errs:
			t.Fatal(err)
		case <-time.After(10 * time.Second):
			t.Fatal("timeout")
		}
	}
	if got[0] != got[1] || f.state(got[0]) == "gone" || !dirExists(filepath.Join(f.root, got[0])) || len(f.ctrs) != 1 {
		t.Fatalf("ids %v, containers %d", got, len(f.ctrs))
	}
}

// An indexed sandbox whose entry names a retired slot is re-owned, so the janitor keeps it.
func TestHandoffAlreadyIndexedSandboxIsReowned(t *testing.T) {
	f := newFakeDaemon(t)
	d := f.executor("blue", "", time.Hour)
	sb, _, err := d.Create(hoExecX)
	if err != nil {
		t.Fatal(err)
	}
	_ = d.writeOwner(sb.ID, "green") // green took it, then retired
	d.AdoptExistingWithPeers([]string{})
	if d.readOwner(sb.ID) != "blue" {
		t.Fatalf("entry = %q", d.readOwner(sb.ID))
	}
	d.cleanupInactive()
	if len(d.List()) != 1 {
		t.Fatal("janitor forgot a sandbox this slot holds")
	}
}

// A failed docker run that left a container behind removes it, so the execution is not refused.
func TestHandoffFailedRunRemovesLeftoverContainer(t *testing.T) {
	f := newFakeDaemon(t)
	d := f.executor("blue", "", time.Hour)
	inner := d.run
	d.run = func(ctx context.Context, args ...string) ([]byte, error) {
		out, _ := inner(ctx, args...)
		if args[0] == "run" {
			return out, errors.New("exit 125")
		}
		return out, nil
	}
	if _, _, err := d.Create(hoExecX); err == nil {
		t.Fatal("expected failure")
	}
	if len(f.ctrs) != 0 {
		t.Fatalf("leftover containers: %d", len(f.ctrs))
	}
}

func TestHandoffImageInspectIsBounded(t *testing.T) {
	d := NewWithRunner(Config{SandboxImage: "rt"}, "docker", func(ctx context.Context, args ...string) ([]byte, error) {
		if _, ok := ctx.Deadline(); !ok {
			t.Error("image inspect runs without a deadline")
		}
		return nil, nil
	})
	_ = d.legacyConfigFingerprint(nil)
}

// A hung stop in the adopt pass does not hold handoffMu.
func TestHandoffAdoptStopsRunOutsideTheLock(t *testing.T) {
	f := newFakeDaemon(t)
	d := f.executor("blue", "", time.Hour)
	f.seed(d, hoIDX, hoExecX, "old", "running")
	inStop, release := make(chan struct{}), make(chan struct{})
	inner := d.run
	d.run = func(ctx context.Context, args ...string) ([]byte, error) {
		if args[0] == "stop" {
			close(inStop)
			<-release
		}
		return inner(ctx, args...)
	}
	done := make(chan struct{})
	go func() { d.AdoptExistingWithPeers([]string{}); close(done) }()
	<-inStop
	if !d.handoffMu.TryLock() {
		t.Fatal("handoffMu held across a stop")
	}
	d.handoffMu.Unlock()
	close(release)
	<-done
	d.mu.RLock()
	pending := d.sandboxes[hoIDX].transition
	d.mu.RUnlock()
	if pending != nil {
		t.Fatal("transition not cleared after the stop")
	}
}

// Two containers on one execution label: this slot's, else the newest, else the lowest name.
func TestHandoffTwoContainersOneExecutionPicksDeterministically(t *testing.T) {
	idLow, idHigh := "aaaaaaaa-0000-4000-8000-0000000000a1", "bbbbbbbb-0000-4000-8000-0000000000b2"
	for _, mine := range []string{"", idHigh} {
		f := newFakeDaemon(t)
		d := f.executor("blue", "green", time.Hour)
		f.seed(d, idLow, hoExecX, "old", "exited")
		f.seed(d, idHigh, hoExecX, "old", "exited")
		if mine != "" {
			_ = d.writeOwner(mine, "blue")
		}
		sb, _, err := d.Create(hoExecX)
		if err != nil {
			t.Fatal(err)
		}
		want := idLow
		if mine != "" {
			want = mine
		}
		if sb.ID != want {
			t.Fatalf("mine=%q picked %s want %s", mine, sb.ID, want)
		}
		for _, c := range f.acted() {
			if strings.HasPrefix(c, "rm ") {
				t.Fatalf("takeover removed a container: %s", c)
			}
		}
	}
}

// A failed entry write at adoption keeps the sandbox indexed; the janitor retries the write.
func TestHandoffFailedOwnerWriteIsRetriedByJanitor(t *testing.T) {
	f := newFakeDaemon(t)
	d := f.executor("blue", "", time.Hour)
	f.seed(d, hoIDX, hoExecX, "old", "running")
	_ = os.WriteFile(d.ownersDir(), []byte("x"), 0o600) // .owners is a file: writes fail
	d.AdoptExistingWithPeers([]string{})
	if len(d.List()) != 1 {
		t.Fatal("sandbox not indexed after a failed entry write")
	}
	_ = os.Remove(d.ownersDir())
	d.cleanupInactive()
	if d.readOwner(hoIDX) != "blue" || len(d.List()) != 1 {
		t.Fatalf("janitor did not record the holder: %q", d.readOwner(hoIDX))
	}
}

// A takeover that lands during the janitor's stop loop must not lose the container or workspace.
func TestHandoffJanitorRechecksOwnerBeforeDelete(t *testing.T) {
	f := newFakeDaemon(t)
	blue := f.executor("blue", "", time.Nanosecond)
	sb, _, err := blue.Create(hoExecX)
	if err != nil {
		t.Fatal(err)
	}
	blue.mu.Lock()
	blue.sandboxes[sb.ID].LastActivity = time.Now().Add(-time.Hour)
	blue.mu.Unlock()
	f.reset()
	inner := blue.run
	blue.run = func(ctx context.Context, args ...string) ([]byte, error) {
		if args[0] == "rm" || args[0] == "stop" {
			t.Errorf("janitor acted on a sandbox another slot holds: %v", args)
		}
		return inner(ctx, args...)
	}
	// green takes over after the janitor's scan: simulate by writing the entry just before delete.
	blue.janitorHook = func() { _ = blue.writeOwner(sb.ID, "green") }
	blue.cleanupInactive()
	if len(blue.List()) != 0 || f.state(sb.ID) == "gone" || !dirExists(filepath.Join(f.root, sb.ID)) {
		t.Fatalf("sandbox lost: list=%d state=%s", len(blue.List()), f.state(sb.ID))
	}
}
