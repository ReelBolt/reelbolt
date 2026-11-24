package ui

import (
	"bufio"
	"bytes"
	"context"
	"errors"
	"io"
	"os"
	"os/exec"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/runtime"
	"github.com/vecchiotom/reelbolt/runner/internal/telemetry"
)

// The in-app download of the video toolkit.
//
// The video toolkit is the container image every video render runs in. The
// probe only checks it; before this file existed the first-run screen had to
// send a person to a terminal to run "docker pull". Now the window starts the
// same pull, reads what the container CLI prints, and turns it into a real
// percentage: layers finished out of layers listed.
//
// Nothing here talks to a registry itself. The pull is the container CLI's own,
// with the same environment (DOCKER_HOST / CONTAINER_HOST) the probe verified,
// so the image lands exactly where the probe and the jobs will look for it.

// CommandRunner runs the container CLI for the download and the image check. It
// is an interface so the tests drive both with a script instead of Docker.
type CommandRunner interface {
	// Run runs a command and returns its combined output.
	Run(ctx context.Context, env []string, name string, args ...string) ([]byte, error)
	// Stream runs a command and calls line once per output line (stdout and
	// stderr together, split on \n and \r), returning when it exits.
	Stream(ctx context.Context, env []string, name string, args []string, line func(string)) error
}

// execCommands is the production CommandRunner.
type execCommands struct{}

func (execCommands) Run(ctx context.Context, env []string, name string, args ...string) ([]byte, error) {
	return runtime.ExecRunner{}.Run(ctx, env, name, args...)
}

func (execCommands) Stream(ctx context.Context, env []string, name string, args []string, line func(string)) error {
	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Env = append(os.Environ(), env...)
	pr, pw := io.Pipe()
	cmd.Stdout = pw
	cmd.Stderr = pw
	if err := cmd.Start(); err != nil {
		_ = pw.Close()
		_ = pr.Close()
		return err
	}
	done := make(chan struct{})
	go func() {
		defer close(done)
		sc := bufio.NewScanner(pr)
		sc.Buffer(make([]byte, 64<<10), 1<<20)
		sc.Split(splitLines)
		for sc.Scan() {
			line(sc.Text())
		}
		// Drain whatever is left so the writer never blocks on a full pipe.
		_, _ = io.Copy(io.Discard, pr)
	}()
	err := cmd.Wait()
	_ = pw.Close()
	<-done
	return err
}

// splitLines is bufio.ScanLines that also breaks on a bare \r, which is how a
// progress line overwrites itself.
func splitLines(data []byte, atEOF bool) (int, []byte, error) {
	if atEOF && len(data) == 0 {
		return 0, nil, nil
	}
	if i := bytes.IndexAny(data, "\r\n"); i >= 0 {
		return i + 1, data[:i], nil
	}
	if atEOF {
		return len(data), data, nil
	}
	return 0, nil, nil
}

// RuntimeDownload is the window's view of the download.
type RuntimeDownload struct {
	Active bool `json:"active"`
	// Percent is absent until the container CLI has listed at least one layer.
	Percent     *int   `json:"percent,omitempty"`
	LayersDone  int    `json:"layersDone"`
	LayersTotal int    `json:"layersTotal"`
	StartedAt   string `json:"startedAt,omitempty"`
	FinishedAt  string `json:"finishedAt,omitempty"`
	// Error is one plain sentence when the download failed.
	Error string `json:"error,omitempty"`
}

// ---- parsing what the container CLI prints ----

// layerState is how far one layer has got.
type layerState int

const (
	layerSeen layerState = iota
	layerDownloaded
	layerDone
)

// downloadedWeight is how much of a layer counts as finished once its bytes
// are on disk but not yet unpacked. Unpacking is quick next to downloading, so
// a pull whose layers are all downloaded is most of the way there.
const downloadedWeight = 0.75

// pullProgress reads the non-interactive output of "docker pull" and
// "podman pull" and counts layers.
//
// Docker prints one line per state change of each layer, keyed by a short hex
// id ("3f4ca61aafcd: Pull complete"). Podman prints "Copying blob <digest>" when
// it starts a layer and the same line with "done" or "skipped: already exists"
// when it finishes. Both list layers before finishing any of them, which is
// what makes the total meaningful.
type pullProgress struct {
	layers map[string]layerState
	// finished is set once the CLI said the image itself is in place.
	finished bool
}

var dockerLayerLine = regexp.MustCompile(`^([0-9a-f]{12,64}): (.+)$`)

func newPullProgress() *pullProgress { return &pullProgress{layers: map[string]layerState{}} }

// Line reads one output line. Lines it does not recognise change nothing.
func (p *pullProgress) Line(raw string) {
	line := strings.TrimSpace(raw)
	if line == "" {
		return
	}
	lower := strings.ToLower(line)

	// Podman.
	if rest, ok := strings.CutPrefix(line, "Copying blob "); ok {
		fields := strings.Fields(rest)
		if len(fields) == 0 {
			return
		}
		id := shortLayerID(fields[0])
		state := layerSeen
		tail := strings.ToLower(strings.Join(fields[1:], " "))
		if strings.Contains(tail, "done") || strings.Contains(tail, "skipped") || strings.Contains(tail, "already exists") {
			state = layerDone
		}
		p.set(id, state)
		return
	}
	if strings.HasPrefix(lower, "writing manifest") || strings.HasPrefix(lower, "storing signatures") {
		p.finishAll()
		return
	}

	// Docker.
	if strings.HasPrefix(lower, "digest: ") || strings.HasPrefix(lower, "status: ") {
		p.finishAll()
		return
	}
	m := dockerLayerLine.FindStringSubmatch(line)
	if m == nil {
		return
	}
	id, status := shortLayerID(m[1]), strings.ToLower(m[2])
	switch {
	case strings.HasPrefix(status, "pulling from"):
		// "latest: Pulling from x" is not a layer, but a hex-looking tag could
		// match the pattern, so it is filtered by its status too.
		return
	case strings.HasPrefix(status, "pull complete"), strings.HasPrefix(status, "already exists"):
		p.set(id, layerDone)
	case strings.HasPrefix(status, "download complete"), strings.HasPrefix(status, "verifying checksum"),
		strings.HasPrefix(status, "extracting"):
		p.set(id, layerDownloaded)
	default:
		// "Pulling fs layer", "Waiting", "Downloading", "Retrying in 5 seconds".
		p.set(id, layerSeen)
	}
}

// set records a layer's state, never moving one backwards.
func (p *pullProgress) set(id string, s layerState) {
	if cur, ok := p.layers[id]; ok && cur >= s {
		return
	}
	p.layers[id] = s
}

func (p *pullProgress) finishAll() {
	p.finished = true
	for id := range p.layers {
		p.layers[id] = layerDone
	}
}

// Counts is layers finished, layers listed and the percentage. The percentage
// is nil until a layer has been listed, and it stops at 99 until the CLI has
// said the image is in place: a bar at 100% over a pull that is still writing
// its manifest is a bar that lies.
func (p *pullProgress) Counts() (done, total int, percent *int) {
	total = len(p.layers)
	if total == 0 {
		return 0, 0, nil
	}
	sum := 0.0
	for _, s := range p.layers {
		switch s {
		case layerDone:
			done++
			sum++
		case layerDownloaded:
			sum += downloadedWeight
		}
	}
	pct := int(sum * 100 / float64(total))
	if !p.finished && pct > 99 {
		pct = 99
	}
	return done, total, &pct
}

// shortLayerID normalises a layer id: docker prints 12 hex characters, podman
// prints the whole digest with or without "sha256:".
func shortLayerID(id string) string {
	id = strings.TrimPrefix(strings.TrimSpace(id), "sha256:")
	if len(id) > 12 {
		id = id[:12]
	}
	return id
}

// ---- turning failures into sentences ----

// runtimeName is the name a person knows the container runtime by.
func runtimeName(kind runtime.Kind) string {
	if kind == runtime.KindPodman {
		return "Podman"
	}
	return "Docker"
}

// notRunningSentence is the one fix for an engine that does not answer.
func notRunningSentence(kind runtime.Kind) string {
	if kind == runtime.KindPodman {
		return "Podman isn't running. Start it and try again."
	}
	return "Docker isn't running. Start Docker Desktop and try again."
}

// pullErrorSentence turns a failed pull into ONE plain sentence. The raw output
// goes to the log, where a support engineer can read it.
func pullErrorSentence(kind runtime.Kind, output string, err error, cancelled bool) string {
	if cancelled {
		return "The download was stopped before it finished."
	}
	text := strings.ToLower(output)
	if err != nil {
		text += "\n" + strings.ToLower(err.Error())
	}
	has := func(needles ...string) bool {
		for _, n := range needles {
			if strings.Contains(text, n) {
				return true
			}
		}
		return false
	}
	switch {
	case has("no space left on device", "not enough space", "disk quota exceeded"):
		return "The download stopped: this computer is out of disk space. Free some space and try again."
	case has("cannot connect to the docker daemon", "is the docker daemon running", "error during connect",
		"cannot connect to podman", "unable to connect to podman", "docker_engine: the system cannot find",
		"docker desktop is not running", "dockerdesktoplinuxengine"):
		return notRunningSentence(kind)
	case has("permission denied") && has(".sock", "socket", "pipe"):
		return "Your account isn't allowed to use " + runtimeName(kind) + " yet. The setup guide shows how to fix it."
	case has("pull access denied", "unauthorized", "manifest unknown", "not found: manifest", "repository does not exist", "name unknown"):
		return "The video toolkit couldn't be found online. Check for an update to ReelBolt, then try again."
	case has("no such host", "dial tcp", "i/o timeout", "tls handshake timeout", "connection reset", "connection refused",
		"network is unreachable", "unexpected eof", "temporary failure in name resolution", "context deadline exceeded",
		"client.timeout exceeded", "broken pipe"):
		return "The download stopped: the internet connection was lost. Check it and try again."
	case errors.Is(err, exec.ErrNotFound):
		return runtimeName(kind) + " isn't installed any more. Install it, then check again."
	default:
		return "The download stopped unexpectedly. Try again in a moment."
	}
}

// ---- the state machine ----

// toolkitState is the shell's download and image facts. It is guarded by the
// shell's mutex.
type toolkitState struct {
	download *RuntimeDownload
	cancel   context.CancelFunc
	// imagePresent is nil until checked, and imageBytes is the size the CLI
	// reported for it.
	imagePresent *bool
	imageBytes   int64
}

// engine is the container CLI the probe verified, and the environment it was
// verified with.
type engine struct {
	kind   runtime.Kind
	binary string
	env    []string
}

// engineLocked picks the verified container CLI from the probe. The caller
// holds s.mu. It returns a sentence when there is none, which is the answer to
// "why can't it download".
func (s *Shell) engineLocked() (engine, string) {
	if s.probe == nil {
		if s.probing {
			return engine{}, "Still checking this computer. Try again in a few seconds."
		}
		return engine{}, "This computer hasn't been checked yet. Try again in a few seconds."
	}
	if r := s.probe.Runtime; r != nil && r.Usable && r.Candidate.Binary != "" {
		return engine{kind: r.Candidate.Kind, binary: r.Candidate.Binary, env: candidateEnv(r.Candidate)}, ""
	}
	for _, r := range s.probe.Reports {
		switch r.Failure {
		case runtime.FailUnreachable:
			return engine{}, notRunningSentence(r.Candidate.Kind)
		case runtime.FailVersionTooOld:
			return engine{}, "Your version of " + runtimeName(r.Candidate.Kind) + " is too old. Update it, then check again."
		}
	}
	if len(s.probe.Reports) == 0 {
		return engine{}, "Docker or Podman isn't installed. Install one, then check again."
	}
	return engine{}, "Docker or Podman isn't working on this computer. The setup guide shows how to fix it."
}

// candidateEnv is the environment the probe ran the candidate with. It mirrors
// runtime.Candidate's own (unexported) rule.
func candidateEnv(c runtime.Candidate) []string {
	if c.Host == "" {
		return nil
	}
	if c.Kind == runtime.KindPodman {
		return []string{"CONTAINER_HOST=" + c.Host}
	}
	return []string{"DOCKER_HOST=" + c.Host}
}

// imageRefLocked is the image the probe resolved. The caller holds s.mu.
func (s *Shell) imageRefLocked() string {
	if s.probe != nil && s.probe.Image.Ref != "" {
		return s.probe.Image.Ref
	}
	return ""
}

func (s *Shell) commands() CommandRunner {
	if s.opts.Commands != nil {
		return s.opts.Commands
	}
	return execCommands{}
}

// errDownloadActive is the one-at-a-time rule.
var errDownloadActive = say("The video toolkit is already downloading.")

// DownloadRuntime starts downloading the video toolkit in the background.
//
// It returns as soon as the download has started (or refuses with a sentence).
// Progress is in View().Runtime.Download; on success the machine is checked
// again, so remotionReady turns true without a restart of the app.
func (s *Shell) DownloadRuntime() error {
	s.mu.Lock()
	if s.toolkit.download != nil && s.toolkit.download.Active {
		s.mu.Unlock()
		return errDownloadActive
	}
	eng, why := s.engineLocked()
	if why != "" {
		s.mu.Unlock()
		return say(why)
	}
	image := s.imageRefLocked()
	if image == "" || !imageDownloadable(image) {
		s.mu.Unlock()
		return say("This copy of ReelBolt has no published video toolkit to download. Install the latest version of the app.")
	}
	parent := s.rootCtx
	if parent == nil {
		parent = context.Background()
	}
	ctx, cancel := context.WithCancel(parent)
	s.toolkit.cancel = cancel
	s.toolkit.download = &RuntimeDownload{Active: true, StartedAt: s.now().UTC().Format(time.RFC3339)}
	s.mu.Unlock()
	s.notify()

	go s.runDownload(ctx, cancel, eng, image)
	return nil
}

// runDownload is the background half of DownloadRuntime.
func (s *Shell) runDownload(ctx context.Context, cancel context.CancelFunc, eng engine, image string) {
	defer cancel()
	defer telemetry.Recover(s.telemetry, "download")
	s.log.Printf("video toolkit: pulling %s with %s", image, eng.binary)

	progress := newPullProgress()
	var tailMu sync.Mutex
	var tail []string
	lastNotify := time.Time{}
	err := s.commands().Stream(ctx, eng.env, eng.binary, []string{"pull", image}, func(line string) {
		tailMu.Lock()
		tail = append(tail, line)
		if len(tail) > 40 {
			tail = tail[len(tail)-40:]
		}
		tailMu.Unlock()

		s.mu.Lock()
		progress.Line(line)
		done, total, pct := progress.Counts()
		if d := s.toolkit.download; d != nil {
			d.LayersDone, d.LayersTotal, d.Percent = done, total, pct
		}
		s.mu.Unlock()
		// A redraw per line would be a redraw per layer state change, which is
		// fine for a pull; the throttle only stops a chatty CLI flooding it.
		if now := time.Now(); now.Sub(lastNotify) > 150*time.Millisecond {
			lastNotify = now
			s.notify()
		}
	})

	tailMu.Lock()
	output := strings.Join(tail, "\n")
	tailMu.Unlock()

	s.mu.Lock()
	d := s.toolkit.download
	if d == nil {
		d = &RuntimeDownload{}
		s.toolkit.download = d
	}
	d.Active = false
	d.FinishedAt = s.now().UTC().Format(time.RFC3339)
	s.toolkit.cancel = nil
	if err == nil {
		full := 100
		d.Percent = &full
		if d.LayersTotal > 0 {
			d.LayersDone = d.LayersTotal
		}
		d.Error = ""
	} else {
		d.Error = pullErrorSentence(eng.kind, output, err, ctx.Err() != nil)
	}
	s.mu.Unlock()

	if err != nil {
		s.log.Printf("video toolkit: the pull failed: %v\n%s", err, output)
		s.notify()
		return
	}
	s.log.Printf("video toolkit: %s is downloaded", image)
	s.notify()
	// The image is here: check the machine again so the window (and the
	// runner) learn that videos can be rendered now.
	s.reprobe(ctx)
}

// CancelDownload stops a download in flight. It is what Quit calls.
func (s *Shell) CancelDownload() {
	s.mu.Lock()
	cancel := s.toolkit.cancel
	s.mu.Unlock()
	if cancel != nil {
		cancel()
	}
}

// checkImage asks the container CLI whether the video toolkit is on this
// computer, and how big it is. It is cheap (no network) and its answer is
// cached until the next probe or download.
func (s *Shell) checkImage(ctx context.Context) {
	s.mu.Lock()
	eng, why := s.engineLocked()
	image := s.imageRefLocked()
	s.mu.Unlock()
	if why != "" || image == "" {
		s.mu.Lock()
		s.toolkit.imagePresent, s.toolkit.imageBytes = nil, 0
		s.mu.Unlock()
		return
	}
	ctx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()
	out, err := s.commands().Run(ctx, eng.env, eng.binary, "image", "inspect", "--format", "{{.Size}}", image)
	present, size, known := parseImageInspect(out, err)
	s.mu.Lock()
	if known {
		s.toolkit.imagePresent, s.toolkit.imageBytes = &present, size
	} else {
		s.toolkit.imagePresent, s.toolkit.imageBytes = nil, 0
	}
	s.mu.Unlock()
}

// parseImageInspect reads "image inspect --format {{.Size}}". known is false
// when the answer says nothing about the image (the engine did not answer), so
// the window shows nothing rather than a guess.
func parseImageInspect(out []byte, err error) (present bool, size int64, known bool) {
	text := strings.TrimSpace(string(out))
	if err == nil {
		// Some engines print one line per platform; the first is enough.
		first, _, _ := strings.Cut(text, "\n")
		if n, perr := strconv.ParseInt(strings.TrimSpace(first), 10, 64); perr == nil && n >= 0 {
			return true, n, true
		}
		return true, 0, true
	}
	lower := strings.ToLower(text)
	for _, missing := range []string{"no such image", "no such object", "image not known", "failed to find image", "not found"} {
		if strings.Contains(lower, missing) {
			return false, 0, true
		}
	}
	return false, 0, false
}

// toolkitIsTheFix reports whether the one thing between this computer and
// video rendering is the video toolkit download: Docker or Podman works, and
// the image is missing.
func (s *Shell) toolkitIsTheFix() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.probe == nil || s.probe.RemotionReady() {
		return false
	}
	r := s.probe.Runtime
	if r == nil || !r.Usable {
		return false
	}
	if p := s.toolkit.imagePresent; p != nil {
		return !*p
	}
	return r.Failure == runtime.FailImageMissing
}

// imageDownloadable reports whether ref names a published, pinned toolkit
// (repository@sha256:...). Only a pinned image can render (caps.Build requires
// the digest), and only a published one can be pulled; an unpinned local tag
// such as the development default exists on no registry.
func imageDownloadable(ref string) bool {
	_, digest, ok := strings.Cut(ref, "@")
	return ok && strings.HasPrefix(digest, "sha256:")
}
