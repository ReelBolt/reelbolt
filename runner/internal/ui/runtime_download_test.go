package ui

import (
	"context"
	"errors"
	"os/exec"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/daemon"
	"github.com/vecchiotom/reelbolt/runner/internal/runtime"
	"github.com/vecchiotom/reelbolt/runner/internal/runtimeimage"
)

// dockerTranscript is "docker pull" with its output going to a pipe: no
// progress bars, one line per state change of each layer. Captured from Docker
// 27 pulling a node-based image; the ids are shortened by Docker itself.
const dockerTranscript = `ghcr.io/reelbolt/reelbolt-sandbox-runtime@sha256:4f1c0aa1b0f3: Pulling from vecchiotom/reelbolt-sandbox-runtime
2d429b9e73a6: Pulling fs layer
5a7e8c3a4b1d: Pulling fs layer
9c2f1e0d7a6b: Pulling fs layer
c1d2e3f4a5b6: Already exists
5a7e8c3a4b1d: Waiting
2d429b9e73a6: Downloading
2d429b9e73a6: Verifying Checksum
2d429b9e73a6: Download complete
2d429b9e73a6: Pull complete
5a7e8c3a4b1d: Download complete
5a7e8c3a4b1d: Pull complete
9c2f1e0d7a6b: Download complete
9c2f1e0d7a6b: Pull complete
Digest: sha256:4f1c0aa1b0f3e2d1c0b9a8f7e6d5c4b3a2918f7e6d5c4b3a2918f7e6d5c4b3a2
Status: Downloaded newer image for ghcr.io/reelbolt/reelbolt-sandbox-runtime@sha256:4f1c0aa1b0f3
ghcr.io/reelbolt/reelbolt-sandbox-runtime@sha256:4f1c0aa1b0f3`

// podmanTranscript is "podman pull" with no terminal: a line when each blob
// starts, the same line with "done" or "skipped" when it finishes.
const podmanTranscript = `Trying to pull ghcr.io/reelbolt/reelbolt-sandbox-runtime@sha256:4f1c0aa1b0f3...
Getting image source signatures
Copying blob sha256:2d429b9e73a6f1c0b9a8f7e6d5c4b3a2918f7e6d5c4b3a2918f7e6d5c4b3a2
Copying blob sha256:5a7e8c3a4b1d0c9b8a7f6e5d4c3b2a1908f7e6d5c4b3a2918f7e6d5c4b3a2
Copying blob sha256:9c2f1e0d7a6b5c4d3e2f1a0b9c8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c
Copying blob sha256:c1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1 skipped: already exists
Copying blob sha256:2d429b9e73a6f1c0b9a8f7e6d5c4b3a2918f7e6d5c4b3a2918f7e6d5c4b3a2 done   |
Copying blob sha256:5a7e8c3a4b1d0c9b8a7f6e5d4c3b2a1908f7e6d5c4b3a2918f7e6d5c4b3a2 done   |
Copying blob sha256:9c2f1e0d7a6b5c4d3e2f1a0b9c8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c done   |
Copying config sha256:aa11bb22cc33dd44ee55ff66aa77bb88cc99dd00ee11ff22aa33bb44cc55dd66 done   |
Writing manifest to image destination
aa11bb22cc33dd44ee55ff66aa77bb88cc99dd00ee11ff22aa33bb44cc55dd66`

// replay feeds a transcript to a parser up to (and excluding) the line that
// contains stop, or all of it when stop is empty.
func replay(t *testing.T, transcript, stop string) *pullProgress {
	t.Helper()
	p := newPullProgress()
	for _, line := range strings.Split(transcript, "\n") {
		if stop != "" && strings.Contains(line, stop) {
			break
		}
		p.Line(line)
	}
	return p
}

func TestPullProgressReadsDocker(t *testing.T) {
	// After the layers are listed and before any finishes, only the one that
	// already existed counts.
	p := replay(t, dockerTranscript, "2d429b9e73a6: Downloading")
	done, total, pct := p.Counts()
	if total != 4 || done != 1 || pct == nil || *pct != 25 {
		t.Fatalf("listed: done=%d total=%d pct=%v, want 1/4 at 25%%", done, total, deref(pct))
	}
	// One layer downloaded, not unpacked: it counts for most of a layer.
	p = replay(t, dockerTranscript, "2d429b9e73a6: Pull complete")
	done, total, pct = p.Counts()
	if done != 1 || total != 4 || deref(pct) != 43 {
		t.Fatalf("one downloaded: done=%d total=%d pct=%v, want 1/4 at 43%%", done, total, deref(pct))
	}
	// Every layer finished but no digest yet: never claim 100.
	p = replay(t, dockerTranscript, "Digest:")
	done, total, pct = p.Counts()
	if done != 4 || total != 4 || deref(pct) != 99 {
		t.Fatalf("all layers: done=%d total=%d pct=%v, want 4/4 at 99%%", done, total, deref(pct))
	}
	// The whole transcript: the image is in place.
	p = replay(t, dockerTranscript, "")
	if _, _, pct = p.Counts(); deref(pct) != 100 {
		t.Fatalf("finished: pct=%v, want 100", deref(pct))
	}
}

func TestPullProgressReadsPodman(t *testing.T) {
	p := replay(t, podmanTranscript, "2d429b9e73a6f1c0b9a8f7e6d5c4b3a2918f7e6d5c4b3a2918f7e6d5c4b3a2 done")
	done, total, pct := p.Counts()
	if total != 4 || done != 1 || deref(pct) != 25 {
		t.Fatalf("started: done=%d total=%d pct=%v, want 1/4 (the skipped one) at 25%%", done, total, deref(pct))
	}
	p = replay(t, podmanTranscript, "Copying config")
	if done, total, _ = p.Counts(); done != 4 || total != 4 {
		t.Fatalf("blobs done: %d/%d, want 4/4", done, total)
	}
	p = replay(t, podmanTranscript, "")
	if _, _, pct = p.Counts(); deref(pct) != 100 {
		t.Fatalf("finished: pct=%v, want 100", deref(pct))
	}
}

func TestPullProgressHasNoPercentBeforeTheLayersAreListed(t *testing.T) {
	p := newPullProgress()
	p.Line("Trying to pull ghcr.io/x/y:1...")
	p.Line("latest: Pulling from library/node")
	if _, total, pct := p.Counts(); total != 0 || pct != nil {
		t.Fatalf("total=%d pct=%v before any layer, want 0 and no percentage", total, deref(pct))
	}
	// A layer never goes backwards: a late "Waiting" after "Pull complete" is
	// noise, not a regression.
	p.Line("2d429b9e73a6: Pull complete")
	p.Line("2d429b9e73a6: Waiting")
	if done, _, _ := p.Counts(); done != 1 {
		t.Fatalf("done = %d after a late status line, want 1", done)
	}
}

func TestSplitLinesBreaksOnCarriageReturns(t *testing.T) {
	data := []byte("a\rb\nc")
	var got []string
	for len(data) > 0 {
		adv, tok, _ := splitLines(data, true)
		got = append(got, string(tok))
		data = data[adv:]
	}
	if strings.Join(got, "|") != "a|b|c" {
		t.Fatalf("split = %q", got)
	}
}

func TestPullErrorsAreOnePlainSentence(t *testing.T) {
	cases := []struct {
		name   string
		kind   runtime.Kind
		output string
		err    error
		cancel bool
		want   string
	}{
		{"docker not running", runtime.KindDocker,
			"Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?",
			errors.New("exit status 1"), false, "Docker isn't running. Start Docker Desktop and try again."},
		{"podman not running", runtime.KindPodman,
			"Error: unable to connect to Podman socket: failed to connect: dial unix /run/user/1000/podman/podman.sock: connect: no such file or directory",
			errors.New("exit status 125"), false, "Podman isn't running. Start it and try again."},
		{"connection lost", runtime.KindDocker,
			"2d429b9e73a6: Downloading\nerror pulling image configuration: download failed after attempts=6: dial tcp 140.82.121.34:443: i/o timeout",
			errors.New("exit status 1"), false, "The download stopped: the internet connection was lost. Check it and try again."},
		{"disk full", runtime.KindPodman,
			"Error: writing blob: storing blob to file \"/var/tmp/storage123\": write /var/tmp/storage123: no space left on device",
			errors.New("exit status 125"), false, "The download stopped: this computer is out of disk space. Free some space and try again."},
		{"image gone", runtime.KindDocker,
			"Error response from daemon: manifest unknown",
			errors.New("exit status 1"), false, "The video toolkit couldn't be found online. Check for an update to ReelBolt, then try again."},
		{"quit mid-way", runtime.KindDocker, "", context.Canceled, true, "The download was stopped before it finished."},
		{"something else", runtime.KindDocker, "weird", errors.New("exit status 2"), false, "The download stopped unexpectedly. Try again in a moment."},
		{"cli gone", runtime.KindDocker, "", exec.ErrNotFound, false, "Docker isn't installed any more. Install it, then check again."},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got := pullErrorSentence(tc.kind, tc.output, tc.err, tc.cancel)
			if got != tc.want {
				t.Fatalf("sentence = %q, want %q", got, tc.want)
			}
			if strings.Count(got, ". ")+1 > 3 || strings.Contains(got, "\n") {
				t.Fatalf("%q is not a short sentence", got)
			}
		})
	}
}

func TestParseImageInspect(t *testing.T) {
	if present, size, known := parseImageInspect([]byte("1288490188\n"), nil); !present || size != 1288490188 || !known {
		t.Errorf("present image = (%v, %d, %v)", present, size, known)
	}
	if present, _, known := parseImageInspect([]byte("Error: No such image: ghcr.io/x@sha256:1"), errors.New("exit status 1")); present || !known {
		t.Errorf("docker's missing image = (%v, %v), want known and absent", present, known)
	}
	if present, _, known := parseImageInspect([]byte("Error: ghcr.io/x: image not known"), errors.New("exit status 125")); present || !known {
		t.Errorf("podman's missing image = (%v, %v), want known and absent", present, known)
	}
	if _, _, known := parseImageInspect([]byte("Cannot connect to the Docker daemon"), errors.New("exit status 1")); known {
		t.Error("an engine that did not answer reported the image as known")
	}
}

// ---- the state machine, with a scripted CLI ----

const testImage = "ghcr.io/reelbolt/reelbolt-sandbox-runtime@sha256:4f1c0aa1b0f3"

// scriptedCLI is a container CLI that pulls from a transcript.
type scriptedCLI struct {
	mu      sync.Mutex
	pulled  bool
	pulls   int
	lines   []string
	pullErr error
	// gate, when set, holds the pull open until it is closed, after the first
	// line, so a test can look at a download in flight.
	gate chan struct{}
	// started is closed once the first line has been delivered.
	started chan struct{}
}

func (c *scriptedCLI) Run(_ context.Context, _ []string, _ string, args ...string) ([]byte, error) {
	if len(args) >= 2 && args[0] == "image" && args[1] == "inspect" {
		c.mu.Lock()
		defer c.mu.Unlock()
		if c.pulled {
			return []byte("1288490188\n"), nil
		}
		return []byte("Error: No such image: " + testImage), errors.New("exit status 1")
	}
	return nil, errors.New("unexpected command " + strings.Join(args, " "))
}

func (c *scriptedCLI) Stream(ctx context.Context, _ []string, _ string, args []string, line func(string)) error {
	if len(args) == 0 || args[0] != "pull" {
		return errors.New("unexpected command")
	}
	c.mu.Lock()
	c.pulls++
	lines, gate, started := c.lines, c.gate, c.started
	c.mu.Unlock()
	for i, l := range lines {
		line(l)
		if i == 0 && started != nil {
			close(started)
		}
		if i == 0 && gate != nil {
			select {
			case <-gate:
			case <-ctx.Done():
				return ctx.Err()
			}
		}
	}
	if c.pullErr != nil {
		return c.pullErr
	}
	c.mu.Lock()
	c.pulled = true
	c.mu.Unlock()
	return nil
}

// toolkitProber answers "Docker works, the video toolkit is missing" until
// the CLI has pulled it, and "ready" after.
type toolkitProber struct {
	cli   *scriptedCLI
	calls atomic.Int32
}

func (p *toolkitProber) Probe(context.Context, daemon.ProbeRequest) (daemon.ProbeResult, error) {
	p.calls.Add(1)
	p.cli.mu.Lock()
	pulled := p.cli.pulled
	p.cli.mu.Unlock()
	rep := runtime.Report{
		Candidate: runtime.Candidate{Kind: runtime.KindDocker, Binary: "docker"},
		Usable:    true, Failure: runtime.FailImageMissing,
	}
	jobs := []string{protocol.JobVideoCompile}
	if pulled {
		rep.Failure, rep.RemotionReady = "", true
		jobs = append(jobs, protocol.JobRemotionWorkspace)
	}
	return daemon.ProbeResult{
		Capabilities: protocol.Capabilities{
			AgentVersion: "test", OS: "linux", Arch: "amd64", Slots: 1,
			JobTypes: jobs, FfmpegVersion: "7.1",
		},
		Image:        runtimeimage.Source{Ref: testImage, Origin: "pinned"},
		Runtime:      &rep,
		Reports:      []runtime.Report{rep},
		ContainerCLI: "docker",
	}, nil
}

func newToolkitShell(t *testing.T, cli *scriptedCLI) (*Shell, *toolkitProber, func()) {
	t.Helper()
	prober := &toolkitProber{cli: cli}
	s, cleanup := newTestShell(t, func(o *Options) {
		o.Prober = prober
		o.Commands = cli
	})
	// The image check runs right after the first probe.
	waitFor(t, 5*time.Second, func() bool { return s.View().Runtime.ImagePresent != nil })
	return s, prober, cleanup
}

func TestDownloadRuntimeReportsProgressAndThenRendering(t *testing.T) {
	cli := &scriptedCLI{
		lines:   strings.Split(dockerTranscript, "\n"),
		gate:    make(chan struct{}),
		started: make(chan struct{}),
	}
	s, prober, cleanup := newToolkitShell(t, cli)
	defer cleanup()

	v := s.View()
	if v.Runtime.RemotionReady || *v.Runtime.ImagePresent {
		t.Fatalf("before the download: ready=%v present=%v", v.Runtime.RemotionReady, *v.Runtime.ImagePresent)
	}
	// Idle, and the one button is "Set up video rendering".
	if v.State != StateIdle || v.Remedy == nil || v.Remedy.Kind != RemedyRuntime {
		t.Fatalf("state=%q remedy=%+v, want idle with the runtime remedy", v.State, v.Remedy)
	}
	app := NewApp(s, AppOptions{OpenURL: func(string) error { t.Error("a browser was opened for a download"); return nil }, Autostart: &fakeAutostart{}})
	// The remedy's own button downloads, because Docker works.
	res, err := app.Do(RemedyAction(v.Remedy.Kind), "")
	if err != nil || !res.OK {
		t.Fatalf("setup-runtime = %+v, %v", res, err)
	}
	<-cli.started

	// One at a time.
	if _, err := app.Do(ActionDownloadRuntime, ""); err == nil || !strings.Contains(err.Error(), "already downloading") {
		t.Fatalf("a second download = %v, want a refusal", err)
	}
	d := s.View().Runtime.Download
	if d == nil || !d.Active || d.StartedAt == "" {
		t.Fatalf("download in flight = %+v", d)
	}
	close(cli.gate)

	waitFor(t, 10*time.Second, func() bool {
		r := s.View().Runtime
		return r.RemotionReady && r.ImagePresent != nil && *r.ImagePresent
	})
	v = s.View()
	d = v.Runtime.Download
	if d == nil || d.Active || d.Error != "" || deref(d.Percent) != 100 || d.LayersDone != 4 || d.LayersTotal != 4 || d.FinishedAt == "" {
		t.Fatalf("finished download = %+v", d)
	}
	if v.Runtime.ImagePresent == nil || !*v.Runtime.ImagePresent || v.Runtime.ImageBytes != 1288490188 {
		t.Fatalf("image after the download: present=%v bytes=%d", v.Runtime.ImagePresent, v.Runtime.ImageBytes)
	}
	if prober.calls.Load() < 2 {
		t.Fatal("the machine was not checked again after the download")
	}
	// The runner restarts so it advertises the new job type; wait for it to be
	// back before the cleanup stops it.
	waitFor(t, 15*time.Second, func() bool { v := s.View(); return v.Runner.Running && v.State == StateIdle })
	if s.View().Remedy != nil {
		t.Errorf("a ready machine still offers %+v", s.View().Remedy)
	}
}

func TestDownloadRuntimeFailureIsOneSentence(t *testing.T) {
	cli := &scriptedCLI{
		lines: []string{
			"2d429b9e73a6: Pulling fs layer",
			"2d429b9e73a6: Downloading",
			"error pulling image configuration: dial tcp 140.82.121.34:443: i/o timeout",
		},
		pullErr: errors.New("exit status 1"),
	}
	s, prober, cleanup := newToolkitShell(t, cli)
	defer cleanup()
	if err := s.DownloadRuntime(); err != nil {
		t.Fatalf("DownloadRuntime: %v", err)
	}
	waitFor(t, 5*time.Second, func() bool { d := s.View().Runtime.Download; return d != nil && !d.Active })
	d := s.View().Runtime.Download
	if d.Error != "The download stopped: the internet connection was lost. Check it and try again." {
		t.Fatalf("error = %q", d.Error)
	}
	if d.LayersTotal != 1 || d.LayersDone != 0 {
		t.Errorf("layers = %d/%d", d.LayersDone, d.LayersTotal)
	}
	if prober.calls.Load() != 1 {
		t.Error("a failed download re-checked the machine")
	}
	// The idle detail carries the reason, so the person knows why.
	if v := s.View(); !strings.Contains(v.Detail, "internet connection was lost") {
		t.Errorf("detail = %q", v.Detail)
	}
	// And it can be tried again.
	cli.mu.Lock()
	cli.pullErr = nil
	cli.mu.Unlock()
	if err := s.DownloadRuntime(); err != nil {
		t.Fatalf("a second attempt: %v", err)
	}
	waitFor(t, 10*time.Second, func() bool { return s.View().Runtime.RemotionReady })
}

func TestDownloadRuntimeNeedsAWorkingEngine(t *testing.T) {
	// capableProbe has no container runtime at all.
	s, cleanup := newTestShell(t, func(o *Options) { o.Commands = &scriptedCLI{} })
	defer cleanup()
	err := s.DownloadRuntime()
	if err == nil || err.Error() != "Docker or Podman isn't installed. Install one, then check again." {
		t.Fatalf("DownloadRuntime = %v", err)
	}

	// Docker installed but not running.
	notRunning := capableProbe()
	notRunning.Reports = []runtime.Report{{Candidate: runtime.Candidate{Kind: runtime.KindDocker, Binary: "docker"}, Failure: runtime.FailUnreachable}}
	s2, cleanup2 := newTestShell(t, func(o *Options) { o.Prober = fakeProber{res: notRunning}; o.Commands = &scriptedCLI{} })
	defer cleanup2()
	if err := s2.DownloadRuntime(); err == nil || err.Error() != "Docker isn't running. Start Docker Desktop and try again." {
		t.Fatalf("DownloadRuntime = %v", err)
	}
	v := s2.View()
	if v.Runtime.Failure != "Docker isn't running. Start Docker Desktop and try again." {
		t.Errorf("runtime failure = %q", v.Runtime.Failure)
	}
	if len(v.Runtime.Candidates) != 1 || v.Runtime.Candidates[0] != "Docker: not running" {
		t.Errorf("candidates = %q", v.Runtime.Candidates)
	}
}

func TestQuitStopsADownload(t *testing.T) {
	cli := &scriptedCLI{lines: []string{"2d429b9e73a6: Pulling fs layer", "never"}, gate: make(chan struct{}), started: make(chan struct{})}
	s, _, cleanup := newToolkitShell(t, cli)
	defer cleanup()
	if err := s.DownloadRuntime(); err != nil {
		t.Fatal(err)
	}
	<-cli.started
	s.CancelDownload()
	waitFor(t, 5*time.Second, func() bool { d := s.View().Runtime.Download; return d != nil && !d.Active })
	if got := s.View().Runtime.Download.Error; got != "The download was stopped before it finished." {
		t.Fatalf("error = %q", got)
	}
}

func deref(p *int) int {
	if p == nil {
		return -1
	}
	return *p
}

func TestImageDownloadableOnlyForAPinnedPublishedImage(t *testing.T) {
	for ref, want := range map[string]bool{
		"ghcr.io/reelbolt/reelbolt-sandbox-runtime@sha256:abc": true,
		"reelbolt-sandbox-runtime:local":                         false,
		"ghcr.io/reelbolt/reelbolt-sandbox-runtime:0.1.0":      false,
		"": false,
	} {
		if got := imageDownloadable(ref); got != want {
			t.Errorf("imageDownloadable(%q) = %v, want %v", ref, got, want)
		}
	}
}
