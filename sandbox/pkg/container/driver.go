// Package container is the container driver behind the sandbox executor: it
// creates one locked-down container per workflow execution, runs allowlisted
// commands inside it, and reads and writes its workspace through an os.Root.
// It drives any CLI that speaks the docker command line (docker or podman), and
// contains no HTTP code so the cloud worker and the desktop runner can embed it.
package container

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

// Config is the container-driver configuration. HTTP-only settings (port, API
// token) live in the executor command, not here.
type Config struct {
	SandboxImage   string
	SandboxRoot    string
	SandboxNetwork string // network used for sandbox containers
	// NetworkEgress reports whether sandbox containers are allowed to reach
	// anything outside their own network. When false (the default) the network
	// is created with --internal, which removes the container's default
	// route entirely: no internet, no other network, and — critically — no route
	// to the host gateway, which is what otherwise exposes every host-published
	// port (Postgres, RabbitMQ, MinIO, nginx) to code running inside the
	// sandbox. Enabling egress trades that guarantee for the ability to
	// npm-install at runtime; see docs/sandbox-service.md.
	NetworkEgress bool
	MaxSandboxes  int
	// MaxRenderBytes caps a single streamed raw read (a rendered video).
	MaxRenderBytes int64
	SandboxTTL     time.Duration
	ExecTimeout    time.Duration
	// MaxExecTimeout caps the deadline a caller may request for one exec.
	// Zero means DefaultMaxExecTimeout. A render's deadline is sized by the
	// engine from the composition's length, so this is a runaway guard, not a
	// render budget: a long film legitimately renders for hours.
	MaxExecTimeout time.Duration
	MemoryLimit    string
	CPULimit       string
	PIDsLimit      int
	// ShmSize is the container's /dev/shm (`--shm-size`). Chrome renders
	// Remotion frames through shared memory; Docker's default of 64 MB holds
	// fewer than two decoded 4K frames (one 2160x3840 RGBA frame is about
	// 33 MB), so a full-size render of 4K or 1440p footage stopped with
	// "could not extract frame". Empty means DefaultShmSize; "0" or "off"
	// passes no flag (the runtime's default). Counts against MemoryLimit.
	ShmSize string
	// TemplateDir is the Remotion template directory on the control plane's
	// own filesystem (the control-plane image is built FROM the runtime
	// image), read by ImportWorkspace to compare a restored package.json
	// against the template. Empty means DefaultTemplateDir.
	TemplateDir string
	// ContainerPrefix names every sandbox container (SANDBOX_CONTAINER_PREFIX); empty means
	// DefaultContainerPrefix. A distinct prefix keeps stacks sharing one Docker daemon apart.
	ContainerPrefix string
	// Slot names this executor in a blue-green deploy (SANDBOX_SLOT). Empty means a single
	// executor and today's behaviour exactly. Containers it creates carry SlotLabel=<slot>.
	Slot string
	// PeerSlots are the slots of OTHER live executors (SANDBOX_PEER_SLOTS). Their containers are
	// never stopped, removed or indexed here.
	PeerSlots string // comma list
}

// Sandbox is one running (or stopped) workspace container.
type Sandbox struct {
	ID                  string    `json:"id"`
	WorkflowExecutionID string    `json:"workflowExecutionId"`
	ContainerName       string    `json:"containerName"`
	WorkspacePath       string    `json:"workspacePath"`
	CreatedAt           time.Time `json:"createdAt"`
	LastActivity        time.Time `json:"lastActivity"`
	IsStopped           bool      `json:"isStopped"`

	// The fields below are the driver's own bookkeeping, guarded by Driver.mu.
	// They are unexported so they never reach the JSON API.

	// inFlight counts execs currently running in the container. The janitor
	// never stops or deletes a sandbox while it is above zero: a render can
	// outlast the inactivity window, and killing it mid-frame is exit 137.
	inFlight int
	// deleting marks a sandbox the janitor (or a caller) has committed to
	// removing, so a late exec cannot start in a container about to be rm -f'd.
	deleting bool
	// transition is non-nil while a docker stop or start is in progress and is
	// closed when it returns. Anyone who needs the container running waits on
	// it rather than racing the CLI.
	transition chan struct{}
}

// Kind identifies which container CLI a Driver talks to.
type Kind string

const (
	KindDocker  Kind = "docker"
	KindPodman  Kind = "podman"
	KindUnknown Kind = "unknown"
)

// Runner executes one container-CLI invocation (the arguments after the
// binary name). Tests substitute a fake.
type Runner func(ctx context.Context, args ...string) ([]byte, error)

// CLIRunner returns a Runner that shells out to the given binary.
func CLIRunner(binary string) Runner {
	return func(ctx context.Context, args ...string) ([]byte, error) {
		// log the command being executed for debugging purposes
		log.Printf("[%s] running: %s %s", binary, binary, strings.Join(args, " "))
		cmd := exec.CommandContext(ctx, binary, args...)
		output, err := cmd.CombinedOutput()
		if len(output) > 0 {
			// log output even on success so users can diagnose state
			log.Printf("[%s] output: %s", binary, string(output))
		}
		if err != nil {
			log.Printf("[%s] error: %v", binary, err)
		}
		return output, err
	}
}

// Driver manages sandbox containers through a container CLI.
type Driver struct {
	// Kind is the detected CLI flavour (see DetectKind); KindUnknown until detected.
	Kind Kind
	// Binary is the CLI executable name this driver was built for.
	Binary string

	cfg            Config
	run            Runner
	mu             sync.RWMutex
	sandboxes      map[string]*Sandbox
	executionIndex map[string]string
	maxSandboxes   int
	creating       map[string]bool // guarded by mu; sandbox ids whose container Create is still building
	// handoffMu: the adopt pass takes it exclusively; a takeover takes it shared, so a hung scan
	// in one takeover never stalls an unrelated Create. execLocks serializes takeovers of one
	// execution.
	handoffMu   sync.RWMutex
	execLocksMu sync.Mutex
	execLocks   map[string]*execLock
	// runLimited runs a CLI call keeping at most limit bytes of its output; nil falls back to run.
	// janitorHook, when set (tests), runs between the janitor's scan and its deletes.
	janitorHook func()
	runLimited  func(ctx context.Context, limit int, args ...string) ([]byte, error)
}

type execLock struct {
	mu   sync.Mutex
	refs int
}

// lockExec serializes work on one execution id and returns the unlock func.
func (d *Driver) lockExec(execID string) func() {
	d.execLocksMu.Lock()
	if d.execLocks == nil {
		d.execLocks = map[string]*execLock{}
	}
	l := d.execLocks[execID]
	if l == nil {
		l = &execLock{}
		d.execLocks[execID] = l
	}
	l.refs++
	d.execLocksMu.Unlock()
	l.mu.Lock()
	return func() {
		l.mu.Unlock()
		d.execLocksMu.Lock()
		if l.refs--; l.refs == 0 {
			delete(d.execLocks, execID)
		}
		d.execLocksMu.Unlock()
	}
}

// Bounds on every container-CLI call made in the handoff paths (adopt pass, takeover).
const (
	handoffListTimeout = 30 * time.Second
	handoffStopTimeout = 20 * time.Second
	// scanOutputCap bounds what a takeover scan may return, inside the container and in capture.
	scanOutputCap = 64
)

func (d *Driver) runBounded(timeout time.Duration, args ...string) ([]byte, error) {
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()
	return d.run(ctx, args...)
}

// limitedWriter keeps the first max bytes and silently discards the rest, so a flooding process
// is never blocked and never buffered.
type limitedWriter struct {
	buf []byte
	max int
}

func (w *limitedWriter) Write(p []byte) (int, error) {
	if room := w.max - len(w.buf); room > 0 {
		if len(p) < room {
			room = len(p)
		}
		w.buf = append(w.buf, p[:room]...)
	}
	return len(p), nil
}

// limitedCLIRunner is CLIRunner with the captured output capped at limit bytes.
func limitedCLIRunner(binary string) func(ctx context.Context, limit int, args ...string) ([]byte, error) {
	return func(ctx context.Context, limit int, args ...string) ([]byte, error) {
		cmd := exec.CommandContext(ctx, binary, args...)
		w := &limitedWriter{max: limit}
		cmd.Stdout, cmd.Stderr = w, w
		cmd.WaitDelay = 2 * time.Second
		err := cmd.Run()
		return w.buf, err
	}
}

func (d *Driver) runCapped(ctx context.Context, limit int, args ...string) ([]byte, error) {
	if d.runLimited != nil {
		return d.runLimited(ctx, limit, args...)
	}
	out, err := d.run(ctx, args...)
	if len(out) > limit {
		out = out[:limit]
	}
	return out, err
}

// New builds a Driver that shells out to binary ("docker" or "podman"; empty
// means "docker").
func New(cfg Config, binary string) *Driver {
	if binary == "" {
		binary = "docker"
	}
	d := NewWithRunner(cfg, binary, CLIRunner(binary))
	d.runLimited = limitedCLIRunner(binary)
	return d
}

// NewWithRunner builds a Driver around an injected Runner (used by tests and by
// embedders that wrap the CLI).
func NewWithRunner(cfg Config, binary string, run Runner) *Driver {
	return &Driver{
		Kind:           KindUnknown,
		Binary:         binary,
		cfg:            cfg,
		run:            run,
		maxSandboxes:   cfg.MaxSandboxes,
		sandboxes:      make(map[string]*Sandbox),
		executionIndex: make(map[string]string),
	}
}

// Config returns the driver's configuration.
func (d *Driver) Config() Config { return d.cfg }

// DetectKind runs `<binary> version` and classifies the CLI. Podman's docker
// shim still identifies itself as Podman in the version banner. A failing or
// unrecognised CLI yields KindUnknown (and the error, if any); the driver is
// still usable since both CLIs share the flags this package passes.
func (d *Driver) DetectKind(ctx context.Context) (Kind, error) {
	out, err := d.run(ctx, "version")
	d.Kind = classifyVersionOutput(string(out))
	if err != nil && d.Kind == KindUnknown {
		return d.Kind, err
	}
	return d.Kind, nil
}

func classifyVersionOutput(out string) Kind {
	lower := strings.ToLower(out)
	switch {
	case strings.Contains(lower, "podman"):
		return KindPodman
	case strings.Contains(lower, "docker"):
		return KindDocker
	default:
		return KindUnknown
	}
}

// sandboxUID/sandboxGID are the numeric ids of the `node` user in the sandbox
// runtime image, which is what each container runs as (`--user node`). The
// workspace is chowned to them so the container can write it without the
// directory being world-writable on the host.
const (
	sandboxUID = 1000
	sandboxGID = 1000
)

var (
	ErrNotFound    = errors.New("sandbox not found")
	ErrInvalidPath = errors.New("invalid path")
	ErrBadExec     = errors.New("command not allowed; allowed: npm run build|render|typecheck|compositions [--flag[=value]...]; npx remotion render|still|compositions; node " + LayoutLintScriptPath + " --name=value")
	// ErrSandboxNotRunning means the container was not running when a command
	// was about to be exec'd in it and could not be brought back (or was
	// brought back but lost the race twice). The command did not run.
	ErrSandboxNotRunning = errors.New("sandbox container is not running")
	ErrBadExecution      = errors.New("invalid workflowExecutionId")
	ErrTooManySandboxes  = errors.New("sandbox limit reached")
	// ErrPeerSandboxExists: a container for this execution id lives under a peer executor's slot.
	ErrPeerSandboxExists = errors.New("a sandbox for this execution is held by another executor")
	ErrFileTooLarge      = errors.New("file too large")
	allowedNPMScript     = map[string]struct{}{
		"build":        {},
		"render":       {},
		"typecheck":    {},
		"compositions": {},
	}
	// npmPackageNameRe matches valid npm package names including scoped packages.
	// The first character of an unscoped name is deliberately restricted to
	// [a-z0-9]: the previous pattern allowed the whole name to come from
	// [a-z0-9\-_.]+, which matches a leading dash, so a "package" of "--foo" passed
	// validation and was spliced into `npm install --save …` as a flag rather than
	// a package. Names may also not start with "." for the same reason.
	npmPackageNameRe = regexp.MustCompile(`^(@[a-z0-9][a-z0-9\-_.]*/)?[a-z0-9][a-z0-9\-_.]*(@[a-zA-Z0-9.\-_]+)?$`)
)

// ValidPackageName reports whether name is an acceptable npm package spec.
func ValidPackageName(name string) bool { return npmPackageNameRe.MatchString(name) }

// FileExists reports whether path is an existing regular (non-directory) entry.
func FileExists(path string) bool { return fileExists(path) }

// DirExists reports whether path is an existing directory.
func DirExists(path string) bool {
	info, err := os.Stat(path)
	return err == nil && info.IsDir()
}

func fileExists(path string) bool {
	info, err := os.Stat(path)
	return err == nil && !info.IsDir()
}

// InstallPackages runs `npm install --save -- <packages>` inside the sandbox
// container and returns the combined output. The caller is responsible for the
// egress policy check and for validating names with ValidPackageName.
func (d *Driver) InstallPackages(workflowExecutionID string, sb *Sandbox, packages []string) ([]byte, error) {
	ctx, cancel := context.WithTimeout(context.Background(), d.cfg.ExecTimeout)
	defer cancel()

	// "--" terminates npm's own flag parsing, so even if a package name slips past
	// npmPackageNameRe it is treated as an operand and never as an option.
	args := append([]string{"exec", "-w", "/workspace", sb.ContainerName, "npm", "install", "--save", "--"}, packages...)
	output, err := d.run(ctx, args...)
	d.Touch(workflowExecutionID)
	return output, err
}

// ExecutionLabel is the container label carrying the workflow execution id a
// sandbox belongs to, written at creation so a restarted executor can rebuild
// its in-memory index.
const ExecutionLabel = "reelbolt.execution"

// ConfigLabel carries a fingerprint of the isolation a sandbox container was
// created under (see configFingerprint). A restarted executor adopts a container
// only when it matches the current one, so a deploy that tightens isolation
// (flags, limits, network, egress, or a rebuilt runtime image) never keeps an
// older, looser container alive.
const ConfigLabel = "reelbolt.config"

// SlotLabel names the executor slot (blue/green) that created a container. Absent when
// SANDBOX_SLOT is empty.
const SlotLabel = "reelbolt.sandbox.slot"

// SandboxTempDir is TMPDIR inside every sandbox container: a folder in the
// workspace bind mount, so on the host's disk. Chrome writes the frames it
// fetches from staged clips to its temp folder; on the 256 MB /tmp tmpfs that
// filled up within a few seconds of footage ("DISK: Cannot write to disk ...
// FILE_ERROR_NO_SPACE", reported by Remotion as "Failed to fetch ... disk space
// is low") and every full-size render with real clips failed (runs 9bf15ff8,
// 9e22258a, 79a831a1). "tmp" is a top-level name the workspace archive never
// saves or restores (WorkspaceArchiveExcludedTopLevel), so it is not captured
// with a film and survives an import. It is removed with the workspace.
const SandboxTempDir = "/workspace/tmp"

// DefaultShmSize is the /dev/shm every sandbox container gets unless Config.ShmSize says otherwise.
const DefaultShmSize = "1g"

// shmSizeArg is the --shm-size value for cfg, or "" for no flag.
func shmSizeArg(cfg Config) string {
	switch strings.TrimSpace(strings.ToLower(cfg.ShmSize)) {
	case "":
		return DefaultShmSize
	case "0", "off", "none":
		return ""
	default:
		return strings.TrimSpace(cfg.ShmSize)
	}
}

// containerPolicyArgs are the isolation-relevant `run` flags every sandbox
// container is created with. Create appends the per-sandbox name, labels,
// workspace mount and entrypoint around them.
func (d *Driver) containerPolicyArgs() []string {
	var args []string
	if d.cfg.SandboxNetwork != "" {
		args = append(args, "--network", d.cfg.SandboxNetwork)
	}
	if shm := shmSizeArg(d.cfg); shm != "" {
		args = append(args, "--shm-size", shm)
	}
	return append(args,
		"-e", "TMPDIR="+SandboxTempDir,
		"--read-only",
		"--tmpfs", "/tmp:rw,nosuid,nodev,size=256m",
		"--tmpfs", "/home/node/.npm:rw,nosuid,nodev,size=512m",
		"--memory", d.cfg.MemoryLimit,
		"--cpus", d.cfg.CPULimit,
		"--pids-limit", strconv.Itoa(d.cfg.PIDsLimit),
		"--security-opt", "no-new-privileges",
		"--cap-drop", "ALL",
		"--user", "node",
	)
}

// configFingerprint hashes the isolation policy flags and the egress setting. The image is
// deliberately NOT part of it: SANDBOX_IMAGE is a per-release tag, and a kept sandbox from the
// previous release must stay adoptable (revise within TTL). It keeps running on the runtime image
// it was created from; only a new sandbox uses the new image.
func (d *Driver) configFingerprint(policy []string) string {
	return fingerprintOf(append([]string{"v2", strconv.FormatBool(d.cfg.NetworkEgress)}, policy...))
}

// legacyConfigFingerprint is the v1 fingerprint, which also hashed the image reference and id.
// Containers created before the image left the fingerprint still carry it, so adoption accepts
// it when the image is unchanged.
func (d *Driver) legacyConfigFingerprint(policy []string) string {
	imageID := "unresolved"
	if out, err := d.runBounded(handoffListTimeout, "image", "inspect", "--format", "{{.Id}}", d.cfg.SandboxImage); err == nil {
		if id := strings.TrimSpace(string(out)); id != "" {
			imageID = id
		}
	}
	return fingerprintOf(append([]string{"v1", d.cfg.SandboxImage, imageID, strconv.FormatBool(d.cfg.NetworkEgress)}, policy...))
}

func fingerprintOf(parts []string) string {
	h := sha256.New()
	for _, part := range parts {
		h.Write([]byte(part))
		h.Write([]byte{0})
	}
	return hex.EncodeToString(h.Sum(nil))[:32]
}

// AdoptExisting rebuilds the in-memory index from sandbox containers that
// survived a restart of this service, so a deploy does not end the TTL a run
// was promised (a finished run's sandbox is kept for SandboxTTL so it can be
// retried or revised). A container is adopted when it carries a valid
// ExecutionLabel, its workspace directory still exists, and no other container
// claimed the same execution; it is registered as stopped with its original
// creation time, so the janitor's TTL keeps counting from when it was made.
// Everything else (pre-label containers, duplicates, orphaned workspace
// folders) is purged exactly as PurgeExisting would.
func (d *Driver) AdoptExisting() {
	d.AdoptExistingWithPeers(ParseSlots(d.cfg.PeerSlots))
}

// AdoptExistingWithPeers is AdoptExisting with an explicit list of peer slots. Containers
// labelled with a peer slot are left completely alone, and orphan workspace folders are only
// removed when the list is empty (a peer may be creating one this instant). A sandbox this
// driver already indexes is untouched, so the pass is idempotent.
func (d *Driver) AdoptExistingWithPeers(peerSlots []string) {
	// The container stops happen after handoffMu is released: a sandbox that ignores SIGTERM must
	// not hold every takeover off for N x the stop timeout. Each adopted sandbox carries a
	// transition channel meanwhile, so acquire waits for its stop instead of racing it.
	for _, sb := range d.adoptPass(peerSlots) {
		_, _ = d.runBounded(handoffStopTimeout, "stop", "-t", "1", sb.ContainerName)
		d.mu.Lock()
		ch := sb.transition
		sb.transition = nil
		d.mu.Unlock()
		if ch != nil {
			close(ch)
		}
	}
}

// adoptPass is the listing and indexing half of the adopt pass, under handoffMu. It returns the
// sandboxes whose containers still have to be stopped.
func (d *Driver) adoptPass(peerSlots []string) (pending []*Sandbox) {
	d.handoffMu.Lock()
	defer d.handoffMu.Unlock()
	peers := map[string]bool{}
	for _, p := range peerSlots {
		if p = strings.TrimSpace(p); p != "" {
			peers[p] = true
		}
	}

	out, err := d.runBounded(handoffListTimeout, "ps", "-a", "--filter", "name="+d.prefix(),
		"--format", "{{.Names}}|{{.Label \""+ExecutionLabel+"\"}}|{{.CreatedAt}}|{{.Label \""+ConfigLabel+"\"}}|{{.Label \""+SlotLabel+"\"}}")
	if err != nil {
		log.Printf("warning: could not list existing sandbox containers: %v", err)
		return
	}

	adopted := map[string]bool{}    // workspace ids kept
	containers := map[string]bool{} // ids of every container this pass saw (owner entries stay)
	policy := d.containerPolicyArgs()
	current := d.configFingerprint(policy)
	legacy := ""
	for _, line := range strings.Split(strings.TrimSpace(string(out)), "\n") {
		parts := strings.SplitN(strings.TrimSpace(line), "|", 5)
		if len(parts) == 0 || parts[0] == "" {
			continue
		}
		name := parts[0]
		if !strings.HasPrefix(name, d.prefix()) {
			continue // the name filter is a substring match: not ours
		}
		id := strings.TrimPrefix(name, d.prefix())
		execID := ""
		if len(parts) > 1 {
			execID = strings.TrimSpace(parts[1])
		}
		validID := id != name && ownerIDRE.MatchString(id)
		if validID {
			containers[id] = true
		}
		workspace := filepath.Join(d.cfg.SandboxRoot, id)

		// The holder is the owner entry, else the creating slot label (no label: legacy, unheld).
		// A container whose holder is a listed live peer is left completely alone.
		label := ""
		if len(parts) > 4 {
			label = strings.TrimSpace(parts[4])
		}
		owner := label
		if d.cfg.Slot != "" && validID {
			owner = d.holderOf(id, label)
		}
		if peers[owner] {
			continue // a live peer's container: not ours to stop, remove or index
		}

		d.mu.Lock()
		_, already := d.sandboxes[id]
		building := d.creating[id]
		_, claimed := d.executionIndex[execID]
		full := len(d.sandboxes) >= d.maxSandboxes
		d.mu.Unlock()
		if already || building {
			// Indexed here but the entry names another (unlisted, so retired) slot or none: re-own,
			// or the janitor forgets a sandbox it still holds.
			if already && d.cfg.Slot != "" && d.readOwner(id) != d.cfg.Slot {
				if err := d.writeOwner(id, d.cfg.Slot); err != nil {
					log.Printf("warning: could not re-own indexed sandbox %s: %v", id, err)
				}
			}
			adopted[id] = true
			continue // indexed by an earlier pass: never stopped, restarted or purged again
		}

		sameConfig := false
		if len(parts) > 3 {
			label := strings.TrimSpace(parts[3])
			sameConfig = label == current
			if !sameConfig && label != "" {
				if legacy == "" {
					legacy = d.legacyConfigFingerprint(policy)
				}
				sameConfig = label == legacy
			}
		}
		if !validID || ValidateWorkflowExecutionID(execID) != nil || claimed || full || !sameConfig || !dirExists(workspace) {
			log.Printf("purging sandbox container %s (not adoptable)", name)
			_, _ = d.runBounded(handoffStopTimeout, "rm", "-f", name)
			if validID { // an id that failed validation is never joined into a path or deleted
				_ = os.RemoveAll(workspace)
				d.removeOwner(id)
			}
			continue
		}

		created := time.Now().UTC()
		if len(parts) > 2 {
			if t, perr := time.Parse("2006-01-02 15:04:05 -0700 MST", strings.TrimSpace(parts[2])); perr == nil {
				created = t.UTC()
			}
		}
		// Record this slot as the holder, always. If the write fails the sandbox is still indexed
		// (so its TTL runs) and the janitor retries the write (dropTakenOver).
		if d.cfg.Slot != "" && d.readOwner(id) != d.cfg.Slot {
			if err := d.writeOwner(id, d.cfg.Slot); err != nil {
				log.Printf("warning: could not record ownership of sandbox %s, will retry: %v", id, err)
			}
		}
		// Stopped (after the lock is released) so the next use goes through the normal restart path.
		d.mu.Lock()
		sbNew := &Sandbox{
			ID:                  id,
			WorkflowExecutionID: execID,
			ContainerName:       name,
			WorkspacePath:       workspace,
			CreatedAt:           created,
			LastActivity:        time.Now().UTC(),
			IsStopped:           true,
			transition:          make(chan struct{}),
		}
		d.sandboxes[id] = sbNew
		pending = append(pending, sbNew)
		d.executionIndex[execID] = id
		d.mu.Unlock()
		adopted[id] = true
		log.Printf("adopted sandbox container %s for workflow %s (created %s)", name, execID, created.Format(time.RFC3339))
	}

	defer d.pruneOwners(containers)
	if len(peers) > 0 {
		return
	}
	if entries, err := os.ReadDir(d.cfg.SandboxRoot); err == nil {
		for _, e := range entries {
			if e.IsDir() && e.Name() != OwnersDirName && ownerIDRE.MatchString(e.Name()) && !adopted[e.Name()] && !d.indexes(e.Name()) {
				_ = os.RemoveAll(filepath.Join(d.cfg.SandboxRoot, e.Name()))
			}
		}
	}
	return pending
}

// DefaultContainerPrefix is the container name prefix used when none is configured.
const DefaultContainerPrefix = "rf-sbx-"

var containerPrefixRE = regexp.MustCompile(`^[a-z0-9-]+-$`)

// ValidateContainerPrefix accepts [a-z0-9-]+ ending in "-".
func ValidateContainerPrefix(p string) error {
	if !containerPrefixRE.MatchString(p) {
		return fmt.Errorf("invalid SANDBOX_CONTAINER_PREFIX %q: want [a-z0-9-]+ ending in '-'", p)
	}
	return nil
}

func (d *Driver) prefix() string {
	if d.cfg.ContainerPrefix == "" {
		return DefaultContainerPrefix
	}
	return d.cfg.ContainerPrefix
}

// ParseSlots splits a comma list of slot names, dropping blanks.
func ParseSlots(v string) []string {
	var out []string
	for _, p := range strings.Split(v, ",") {
		if p = strings.TrimSpace(p); p != "" {
			out = append(out, p)
		}
	}
	return out
}

func (d *Driver) indexes(id string) bool {
	d.mu.RLock()
	defer d.mu.RUnlock()
	_, ok := d.sandboxes[id]
	return ok || d.creating[id]
}

// liveExecTimeout bounds the in-container scan of hasLiveExec.
const liveExecTimeout = 10 * time.Second

// hasLiveExec reports whether a process carrying the exec marker variable MAY run in the
// container. The scan is the same /proc/environ one the reaper uses; docker top shows no
// environment. It fails closed: only an explicit "none" answer from a scan that completed means
// no live exec, so a timeout, a daemon error or a dead container all read as "may be live" and
// the caller refuses (409) instead of taking over.
//
// Sandboxed code can spoof this scan (set or hide the variable, or fake /proc output via its own
// processes), but that only changes the answer for its own sandbox's execution. The engine's run
// lease is the real guard against two executors running one execution.
func (d *Driver) hasLiveExec(name string) bool {
	// The pipe through head caps what the scan can print inside the container; runCapped caps the
	// capture on our side too, because the sandbox controls the binaries the script runs.
	script := `{ for p in /proc/[0-9]*; do ` +
		`if tr '\0' '\n' < "$p/environ" 2>/dev/null | grep -q '^` + execMarkerVar + `='; then echo live; exit 0; fi; done; echo none; } | head -c ` + strconv.Itoa(scanOutputCap)
	ctx, cancel := context.WithTimeout(context.Background(), liveExecTimeout)
	defer cancel()
	out, err := d.runCapped(ctx, scanOutputCap, "exec", name, "sh", "-c", script)
	if err != nil {
		return true
	}
	return strings.TrimSpace(string(out)) != "none"
}

// takeOverExisting handles a create for an execution whose kept container already exists. The
// container is found by the execution label, whoever created it. If this slot holds it, it is
// used; if anyone else holds it (a listed peer, a retired slot, or nobody) it goes through the
// takeover path: bounded live-exec scan, then re-own. It never leads to a second container or an
// empty workspace: a failed ps or scan refuses with an error. With SANDBOX_SLOT empty it does
// nothing. It reports false when no container exists for the execution.
func (d *Driver) takeOverExisting(execID string) (bool, error) {
	if d.cfg.Slot == "" {
		return false, nil
	}
	// Create holds the per-execution lock. Shared: concurrent takeovers of different executions proceed; only an adopt pass excludes.
	d.handoffMu.RLock()
	defer d.handoffMu.RUnlock()
	d.mu.RLock()
	_, indexed := d.executionIndex[execID]
	d.mu.RUnlock()
	if indexed {
		return true, nil // an adopt pass indexed it meanwhile
	}
	out, err := d.runBounded(handoffListTimeout, "ps", "-a", "--filter", "label="+ExecutionLabel+"="+execID,
		"--format", "{{.Names}}|{{.State}}|{{.CreatedAt}}|{{.Label \""+ConfigLabel+"\"}}|{{.Label \""+SlotLabel+"\"}}")
	if err != nil {
		return false, fmt.Errorf("could not list containers for takeover: %w", err)
	}
	// Several containers can carry one execution label (a crash between create and index). Pick
	// deterministically: the one this slot holds, else the newest, ties by name. Nothing else is
	// touched, so a live peer's workspace is never purged here.
	var rows [][]string
	for _, line := range strings.Split(strings.TrimSpace(string(out)), "\n") {
		parts := strings.SplitN(strings.TrimSpace(line), "|", 5)
		if len(parts) < 5 || !strings.HasPrefix(parts[0], d.prefix()) {
			continue
		}
		rows = append(rows, parts)
	}
	rank := func(p []string) (bool, time.Time) {
		t, _ := time.Parse("2006-01-02 15:04:05 -0700 MST", strings.TrimSpace(p[2]))
		id := strings.TrimPrefix(p[0], d.prefix())
		return ownerIDRE.MatchString(id) && d.holderOf(id, strings.TrimSpace(p[4])) == d.cfg.Slot, t
	}
	sort.SliceStable(rows, func(i, j int) bool {
		mi, ti := rank(rows[i])
		mj, tj := rank(rows[j])
		if mi != mj {
			return mi
		}
		if !ti.Equal(tj) {
			return ti.After(tj)
		}
		return rows[i][0] < rows[j][0]
	})
	if len(rows) > 0 { // only the best-ranked candidate is considered
		parts := rows[0]
		name := parts[0]
		id := strings.TrimPrefix(name, d.prefix())
		if !ownerIDRE.MatchString(id) {
			return false, ErrPeerSandboxExists
		}
		workspace := filepath.Join(d.cfg.SandboxRoot, id)
		if !dirExists(workspace) {
			return false, ErrPeerSandboxExists
		}
		policy := d.containerPolicyArgs()
		label := strings.TrimSpace(parts[3])
		if label != d.configFingerprint(policy) && label != d.legacyConfigFingerprint(policy) {
			return false, ErrPeerSandboxExists
		}
		state := strings.TrimSpace(parts[1])
		holder := d.holderOf(id, strings.TrimSpace(parts[4]))
		mine := holder == d.cfg.Slot
		if !mine && state == "running" && d.hasLiveExec(name) {
			return false, ErrPeerSandboxExists
		}
		d.mu.Lock()
		full := len(d.sandboxes) >= d.maxSandboxes
		d.mu.Unlock()
		if full {
			return false, ErrTooManySandboxes
		}
		if d.readOwner(id) != d.cfg.Slot {
			if d.writeOwner(id, d.cfg.Slot) != nil {
				return false, ErrPeerSandboxExists
			}
		}
		created := time.Now().UTC()
		if t, perr := time.Parse("2006-01-02 15:04:05 -0700 MST", strings.TrimSpace(parts[2])); perr == nil {
			created = t.UTC()
		}
		stopped := true
		if mine {
			stopped = state != "running"
		} else {
			_, _ = d.runBounded(handoffStopTimeout, "stop", "-t", "1", name)
		}
		d.mu.Lock()
		d.sandboxes[id] = &Sandbox{
			ID: id, WorkflowExecutionID: execID, ContainerName: name, WorkspacePath: workspace,
			CreatedAt: created, LastActivity: time.Now().UTC(), IsStopped: stopped,
		}
		d.executionIndex[execID] = id
		d.mu.Unlock()
		log.Printf("took over sandbox container %s for workflow %s (holder was %q)", name, execID, holder)
		return true, nil
	}
	return false, nil
}

func dirExists(path string) bool {
	info, err := os.Stat(path)
	return err == nil && info.IsDir()
}

// PurgeExisting removes any lingering sandbox containers from previous runs
// (name prefix rf-sbx-) along with their workspace directories. This ensures a
// clean state on service startup; the in-memory driver has no knowledge of
// containers created before it started, so without this step they would hang
// around forever.
func (d *Driver) PurgeExisting() {
	cfg := d.cfg
	out, err := d.run(context.Background(), "ps", "-a", "--filter", "name="+d.prefix(), "--format", "{{.Names}}")
	if err != nil {
		log.Printf("warning: could not list existing sandbox containers: %v", err)
		return
	}
	for _, name := range strings.Fields(string(out)) {
		if name == "" || !strings.HasPrefix(name, d.prefix()) {
			continue
		}
		log.Printf("purging leftover sandbox container %s", name)
		_, _ = d.run(context.Background(), "rm", "-f", name)
		id := strings.TrimPrefix(name, d.prefix())
		if id != name {
			dir := filepath.Join(cfg.SandboxRoot, id)
			if err := os.RemoveAll(dir); err != nil {
				log.Printf("error removing leftover workspace %s: %v", dir, err)
			}
		}
	}
	// wipe any orphaned workspace folders that don't correspond to a container
	if entries, err := os.ReadDir(cfg.SandboxRoot); err == nil {
		for _, e := range entries {
			if e.IsDir() {
				_ = os.RemoveAll(filepath.Join(cfg.SandboxRoot, e.Name()))
			}
		}
	}
}

// NetworkError is returned by EnsureNetwork when the sandbox network cannot be
// created or does not have the required isolation. It is fatal for the caller:
// falling back to the default bridge would silently drop isolation.
type NetworkError struct{ Msg string }

func (e *NetworkError) Error() string { return e.Msg }

// EnsureNetwork creates the network sandbox containers attach to, and verifies
// it has the isolation this service assumes.
//
// The network is created with --internal unless egress is explicitly enabled.
// --internal removes the container's default route, which is the only thing that
// actually stops sandboxed code from reaching the host: on a normal bridge the
// container can address the host gateway, and every port published is bound
// there, so Postgres, RabbitMQ, MinIO and nginx are all one hop away. Network
// segmentation alone does not prevent that.
//
// The network is also re-inspected when it already exists, because a network
// left over from an earlier release was created without --internal and would
// silently keep its egress.
func (d *Driver) EnsureNetwork() error {
	cfg := d.cfg
	args := []string{"network", "create"}
	if !cfg.NetworkEgress {
		args = append(args, "--internal")
	}
	args = append(args, cfg.SandboxNetwork)

	if out, err := d.run(context.Background(), args...); err != nil {
		// Already exists is the normal path on restart; anything else is fatal,
		// since falling back to the default bridge would silently drop isolation.
		if !strings.Contains(string(out), "already exists") {
			return &NetworkError{Msg: "failed to create sandbox network " + cfg.SandboxNetwork + ": " + err.Error() + "\noutput: " + string(out)}
		}
	}

	out, err := d.run(context.Background(), "network", "inspect", cfg.SandboxNetwork, "--format", "{{.Internal}}")
	if err != nil {
		return &NetworkError{Msg: "failed to inspect sandbox network " + cfg.SandboxNetwork + ": " + err.Error() + "\noutput: " + string(out)}
	}
	isInternal := strings.TrimSpace(string(out)) == "true"
	switch {
	case !cfg.NetworkEgress && !isInternal:
		return &NetworkError{Msg: "sandbox network " + cfg.SandboxNetwork + " exists but is not internal, so sandboxed code could reach the host " +
			"gateway and every published port. Remove it (" + d.Binary + " network rm " + cfg.SandboxNetwork + ") and restart, or set " +
			"SANDBOX_NETWORK_EGRESS=true to accept that exposure deliberately."}
	case cfg.NetworkEgress:
		log.Printf("WARNING: SANDBOX_NETWORK_EGRESS is enabled — sandbox containers can reach the network. "+
			"Ensure no service is published on a host interface the sandbox can route to; only nginx should "+
			"be published beyond loopback. (network %s, internal=%v)", cfg.SandboxNetwork, isInternal)
	default:
		log.Printf("sandbox network %s ready (internal=true, no egress)", cfg.SandboxNetwork)
	}
	return nil
}
