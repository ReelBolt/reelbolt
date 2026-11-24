package media

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// recordingRunner stands in for ffmpeg: it records what it was asked to run and
// answers with a scripted result.
type recordingRunner struct {
	mu      sync.Mutex
	argv    [][]string
	dirs    []string
	results func(dir string, argv []string) Result
}

func (r *recordingRunner) Run(ctx context.Context, dir string, argv []string) (Result, error) {
	r.mu.Lock()
	r.argv = append(r.argv, append([]string(nil), argv...))
	r.dirs = append(r.dirs, dir)
	r.mu.Unlock()
	if r.results != nil {
		return r.results(dir, argv), nil
	}
	return Result{ExitCode: 0, Duration: time.Millisecond}, nil
}

func (r *recordingRunner) last() []string {
	r.mu.Lock()
	defer r.mu.Unlock()
	if len(r.argv) == 0 {
		return nil
	}
	return r.argv[len(r.argv)-1]
}

func (r *recordingRunner) count() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.argv)
}

func newMediaSession(t *testing.T, opts ...func(*Config)) (*session, *recordingRunner) {
	t.Helper()
	runner := &recordingRunner{}
	cfg := Config{QuotaBytes: 1 << 20}
	// The host path without a real ffmpeg process behind it.
	cfg.Runner = runner
	for _, o := range opts {
		o(&cfg)
	}
	exec, err := New(cfg)
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	job := &agent.Job{
		JobID:      "7d1c2e0a-5b7e-4a43-9d57-3f0c1a9b2e11",
		SessionID:  "0b6f3c58-91d2-4c7a-8e34-5a2d7f1e9c40",
		JobType:    protocol.JobVideoCompile,
		LeaseEpoch: 1,
		JobToken:   "TEST.job.token",
		Timeout:    time.Hour,
		Limits:     protocol.AssignLimits{MaxOutputBytes: 1 << 20},
		Spec:       json.RawMessage([]byte("{\"executionId\":\"e\",\"stepId\":\"s\",\"mode\":\"Reencode\"}")),
		ScratchDir: t.TempDir(),
	}
	created, err := exec.New(context.Background(), job)
	if err != nil {
		t.Fatalf("New session: %v", err)
	}
	s, ok := created.(*session)
	if !ok {
		t.Fatalf("session type = %T", created)
	}
	t.Cleanup(func() { created.Close(context.Background()) })
	return s, runner
}

func callMedia(t *testing.T, s *session, op string, args any) (json.RawMessage, *protocol.OpError) {
	t.Helper()
	raw, err := json.Marshal(args)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	result, opErr := s.Handle(context.Background(), op, raw)
	if opErr != nil {
		return nil, opErr
	}
	out, err := json.Marshal(result)
	if err != nil {
		t.Fatalf("the op result does not encode: %v", err)
	}
	return out, nil
}

// ffmpeg is invoked with the protocol whitelist in force, with argv passed
// straight through (no shell), from the session directory.
func TestFfmpegInvocationIsHardened(t *testing.T) {
	s, runner := newMediaSession(t)
	if _, opErr := callMedia(t, s, protocol.OpStage, map[string]any{
		"url": "https://storage.invalid/clip?X-Amz-Signature=x", "path": "clip.mp4", "maxBytes": 1 << 20,
	}); opErr != nil {
		// The stage op needs a store; this test only needs the file to exist.
		_ = opErr
	}
	if err := os.WriteFile(filepath.Join(s.dir, "clip.mp4"), []byte("media"), 0o600); err != nil {
		t.Fatalf("seed: %v", err)
	}
	runner.results = func(dir string, argv []string) Result {
		return Result{ExitCode: 0, Stderr: []byte("frame= 10 fps=0.0"), Duration: 25 * time.Millisecond}
	}
	out, opErr := callMedia(t, s, protocol.OpFfmpeg, map[string]any{
		"argv": []string{"-y", "-i", "clip.mp4", "-c:v", "libx264", "out.mp4"}, "timeoutSec": 60,
	})
	if opErr != nil {
		t.Fatalf("ffmpeg: %+v", opErr)
	}
	argv := runner.last()
	if len(argv) == 0 || argv[0] != s.exec.cfg.FFmpegPath {
		t.Fatalf("argv = %v", argv)
	}
	joined := strings.Join(argv, " ")
	if !strings.Contains(joined, "-protocol_whitelist file,pipe -i clip.mp4") {
		t.Fatalf("the whitelist was not applied to the input: %v", argv)
	}
	if runner.dirs[0] != s.dir {
		t.Fatalf("ffmpeg ran in %q, want the session directory", runner.dirs[0])
	}
	if !strings.Contains(string(out), "stderrTail") || !strings.Contains(string(out), "25") {
		t.Fatalf("ffmpeg result = %s", out)
	}
}

// The validator is applied at the op boundary, so a bad argv never reaches the
// runner at all.
func TestFfmpegRefusesABadArgvBeforeRunningIt(t *testing.T) {
	s, runner := newMediaSession(t)
	_, opErr := callMedia(t, s, protocol.OpFfmpeg, map[string]any{
		"argv": []string{"-i", "http://example.com/x.mp4", "out.mp4"}, "timeoutSec": 60,
	})
	if opErr == nil || opErr.Code != protocol.ErrCodeArgvNotAllowed {
		t.Fatalf("ffmpeg with an http input = %+v, want argv_not_allowed", opErr)
	}
	if runner.count() != 0 {
		t.Fatal("a refused argv still reached the runner")
	}
}

// ffprobe keeps stdout intact, because that is where its JSON is.
func TestFfprobeReturnsStdout(t *testing.T) {
	s, runner := newMediaSession(t)
	if err := os.WriteFile(filepath.Join(s.dir, "clip.mp4"), []byte("media"), 0o600); err != nil {
		t.Fatalf("seed: %v", err)
	}
	runner.results = func(dir string, argv []string) Result {
		return Result{ExitCode: 0, Stdout: []byte("{\"streams\":[]}"), Stderr: []byte("probe noise")}
	}
	out, opErr := callMedia(t, s, protocol.OpFfprobe, map[string]any{
		"argv": []string{"-v", "quiet", "-print_format", "json", "-show_streams", "clip.mp4"}, "timeoutSec": 30,
	})
	if opErr != nil {
		t.Fatalf("ffprobe: %+v", opErr)
	}
	if !strings.Contains(string(out), "streams") || !strings.Contains(string(out), "probe noise") {
		t.Fatalf("ffprobe result = %s", out)
	}
	if argv := runner.last(); argv[0] != s.exec.cfg.FFprobePath || !strings.Contains(strings.Join(argv, " "), "-protocol_whitelist file,pipe") {
		t.Fatalf("argv = %v", argv)
	}
}

// The program is the worker's, never the caller's: an argv that names one is a
// caller mistake, and the binary that runs is always the configured ffmpeg.
func TestTheWorkerChoosesTheProgram(t *testing.T) {
	s, runner := newMediaSession(t)
	for _, argv := range [][]string{{"ffmpeg", "-i", "clip.mp4", "out.mp4"}, {"ffprobe", "-i", "clip.mp4"}} {
		_, opErr := callMedia(t, s, protocol.OpFfmpeg, map[string]any{"argv": argv, "timeoutSec": 5})
		if opErr == nil || opErr.Code != protocol.ErrCodeArgvNotAllowed {
			t.Fatalf("argv %v = %+v, want argv_not_allowed", argv, opErr)
		}
	}
	if runner.count() != 0 {
		t.Fatal("a refused argv reached the runner")
	}
	// Without a program, the configured binary is the one that runs.
	if _, opErr := callMedia(t, s, protocol.OpFfmpeg, map[string]any{
		"argv": []string{"-y", "-i", "clip.mp4", "out.mp4"}, "timeoutSec": 5,
	}); opErr != nil {
		t.Fatalf("ffmpeg: %+v", opErr)
	}
	if got := runner.last()[0]; got != s.exec.cfg.FFmpegPath {
		t.Fatalf("the runner was asked to run %q", got)
	}
}

// stage, read, stat, publish and close, end to end.
func TestMediaFileOps(t *testing.T) {
	var uploaded []byte
	store := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.Method {
		case http.MethodGet:
			_, _ = w.Write([]byte("staged media"))
		case http.MethodPut:
			uploaded, _ = io.ReadAll(r.Body)
			w.WriteHeader(http.StatusOK)
		}
	}))
	defer store.Close()

	s, _ := newMediaSession(t, func(c *Config) { c.HTTPClient = store.Client() })
	staged, opErr := callMedia(t, s, protocol.OpStage, map[string]any{
		"url": store.URL + "/get?X-Amz-Signature=x", "path": "inputs/clip.mp4", "maxBytes": 1 << 20,
	})
	if opErr != nil {
		t.Fatalf("stage: %+v", opErr)
	}
	if !strings.Contains(string(staged), agent.SHA256Bytes([]byte("staged media"))) {
		t.Fatalf("stage result = %s", staged)
	}

	statted, opErr := callMedia(t, s, protocol.OpStat, map[string]any{"path": "inputs/clip.mp4"})
	if opErr != nil {
		t.Fatalf("stat: %+v", opErr)
	}
	if !strings.Contains(string(statted), "\"exists\":true") || !strings.Contains(string(statted), "\"bytes\":12") {
		t.Fatalf("stat result = %s", statted)
	}
	missing, opErr := callMedia(t, s, protocol.OpStat, map[string]any{"path": "inputs/absent.mp4"})
	if opErr != nil {
		t.Fatalf("stat of a missing file: %+v", opErr)
	}
	if !strings.Contains(string(missing), "\"exists\":false") {
		t.Fatalf("stat of a missing file = %s", missing)
	}

	read, opErr := callMedia(t, s, protocol.OpRead, map[string]any{"path": "inputs/clip.mp4", "maxBytes": 1024})
	if opErr != nil {
		t.Fatalf("read: %+v", opErr)
	}
	if !strings.Contains(string(read), "staged media") {
		t.Fatalf("read result = %s", read)
	}
	if _, opErr := callMedia(t, s, protocol.OpRead, map[string]any{"path": "inputs/clip.mp4", "maxBytes": 4}); opErr == nil || opErr.Code != protocol.ErrCodeFileTooLarge {
		t.Fatalf("a read past maxBytes = %+v, want file_too_large", opErr)
	}

	published, opErr := callMedia(t, s, protocol.OpPublish, map[string]any{
		"path": "inputs/clip.mp4", "putUrl": store.URL + "/put", "maxBytes": 1 << 20, "contentType": "video/mp4",
	})
	if opErr != nil {
		t.Fatalf("publish: %+v", opErr)
	}
	if string(uploaded) != "staged media" {
		t.Fatalf("the store received %q", uploaded)
	}
	if !strings.Contains(string(published), agent.SHA256Bytes([]byte("staged media"))) {
		t.Fatalf("publish result = %s", published)
	}

	dir := s.dir
	if _, opErr := callMedia(t, s, protocol.OpClose, map[string]any{}); opErr != nil {
		t.Fatalf("close: %+v", opErr)
	}
	if _, err := os.Stat(dir); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("the session directory survived close: %v", err)
	}
}

// Every media op validates its own paths.
func TestMediaOpsRefuseHostPaths(t *testing.T) {
	s, _ := newMediaSession(t)
	cases := []struct {
		op   string
		args map[string]any
	}{
		{protocol.OpRead, map[string]any{"path": "/etc/passwd"}},
		{protocol.OpRead, map[string]any{"path": "../../etc/passwd"}},
		{protocol.OpStat, map[string]any{"path": "/etc/passwd"}},
		{protocol.OpPublish, map[string]any{"path": "/etc/passwd", "putUrl": "https://x.invalid/p"}},
		{protocol.OpStage, map[string]any{"url": "https://x.invalid/g", "path": "/etc/cron.d/x"}},
	}
	for _, tc := range cases {
		_, opErr := callMedia(t, s, tc.op, tc.args)
		if opErr == nil {
			t.Errorf("%s accepted %v", tc.op, tc.args)
			continue
		}
		if opErr.Code != protocol.ErrCodeInvalidPath {
			t.Errorf("%s on %v = %q, want invalid_path", tc.op, tc.args, opErr.Code)
		}
	}
}

// A workspace op in a media session is answered unsupported_op.
func TestMediaRefusesWorkspaceOps(t *testing.T) {
	s, _ := newMediaSession(t)
	for _, op := range protocol.WorkspaceOps() {
		if op == protocol.OpStage || op == protocol.OpPublish || op == protocol.OpClose {
			continue // shared with the media set by design
		}
		_, opErr := callMedia(t, s, op, map[string]any{"path": "x"})
		if opErr == nil || opErr.Code != protocol.ErrCodeUnsupportedOp {
			t.Errorf("%s in a media session = %+v, want unsupported_op", op, opErr)
		}
	}
	if _, opErr := callMedia(t, s, "invented.from.the.future", map[string]any{}); opErr == nil || opErr.Code != protocol.ErrCodeUnsupportedOp {
		t.Fatalf("an unknown op = %+v, want unsupported_op", opErr)
	}
}

// The executor only takes the two media job types.
func TestExecutorJobTypes(t *testing.T) {
	exec, err := New(Config{})
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	for _, jobType := range []string{protocol.JobVideoCompile, protocol.JobVideoAnalyzeEx} {
		job := &agent.Job{JobType: jobType, ScratchDir: t.TempDir()}
		if _, err := exec.New(context.Background(), job); err != nil {
			t.Errorf("New(%s): %v", jobType, err)
		}
	}
	if _, err := exec.New(context.Background(), &agent.Job{JobType: protocol.JobRemotionWorkspace, ScratchDir: t.TempDir()}); err == nil {
		t.Fatal("the media executor accepted a Remotion workspace session")
	}
}

// A runner with a container runtime runs ffmpeg inside it, with the session
// directory as the only writable mount and no network.
func TestContainerRunnerFlags(t *testing.T) {
	var got []string
	runner := &ContainerRunner{
		Binary: "docker",
		Image:  "ghcr.io/reelbolt/sandbox-runtime@sha256:abc",
		CLI: func(ctx context.Context, args ...string) ([]byte, []byte, error) {
			got = args
			return []byte("ok"), []byte(""), nil
		},
		RunAs: &[2]int{1000, 1000},
	}
	dir := t.TempDir()
	res, err := runner.Run(context.Background(), dir, []string{"ffmpeg", "-i", "clip.mp4", "out.mp4"})
	if err != nil {
		t.Fatalf("Run: %v", err)
	}
	if res.ExitCode != 0 {
		t.Fatalf("exit code = %d", res.ExitCode)
	}
	joined := strings.Join(got, " ")
	for _, want := range []string{
		"run --rm",
		"--network none",
		"--cap-drop ALL",
		"--security-opt no-new-privileges",
		"--read-only",
		"--user 1000:1000",
		"-v " + dir + ":/media",
		"-w /media",
		"ffmpeg -i clip.mp4 out.mp4",
	} {
		if !strings.Contains(joined, want) {
			t.Errorf("the container invocation is missing %q: %s", want, joined)
		}
	}
	if strings.Contains(joined, "--privileged") {
		t.Errorf("the container was run privileged: %s", joined)
	}
}

// A container CLI that reports a non-zero exit is a normal ffmpeg failure, not
// an infrastructure error.
func TestContainerRunnerReportsExitCodes(t *testing.T) {
	runner := &ContainerRunner{
		Binary: "docker",
		Image:  "img",
		CLI: func(ctx context.Context, args ...string) ([]byte, []byte, error) {
			cmd := exec.CommandContext(context.Background(), "sh", "-c", "exit 3")
			return nil, []byte("boom"), cmd.Run()
		},
	}
	res, err := runner.Run(context.Background(), t.TempDir(), []string{"ffmpeg", "-i", "x"})
	if err != nil {
		t.Fatalf("Run: %v", err)
	}
	if res.ExitCode != 3 {
		t.Fatalf("exit code = %d, want 3", res.ExitCode)
	}
	if string(res.Stderr) != "boom" {
		t.Fatalf("stderr = %q", res.Stderr)
	}
}

// The DEFAULT container CLI — CLI == nil, which is what an embedder that has a
// real Docker daemon uses — must execute the container CLI the runner was
// configured with, not argv[0] of the command line it built. Until D13 ran this
// against a real daemon, execCLI treated args[0] ("run") as the program, so every
// media session on a real runner failed with "exec: run: executable file not
// found in $PATH" while the fake-CLI tests above stayed green.
//
// The stand-in CLI is a one-line script on disk, so this exercises the real
// os/exec path rather than a closure.
func TestContainerRunnerDefaultCLIExecutesTheConfiguredBinary(t *testing.T) {
	if _, err := exec.LookPath("sh"); err != nil {
		t.Skip("no /bin/sh on this machine")
	}
	dir := t.TempDir()
	script := filepath.Join(dir, "fake-docker")
	if err := os.WriteFile(script, []byte("#!/bin/sh\necho \"$@\"\n"), 0o755); err != nil {
		t.Fatal(err)
	}

	runner := &ContainerRunner{Binary: script, Image: "img"}
	res, err := runner.Run(context.Background(), dir, []string{"-version"})
	if err != nil {
		t.Fatalf("Run: %v", err)
	}
	if res.ExitCode != 0 {
		t.Fatalf("exit code = %d, want 0 (stderr %q)", res.ExitCode, res.Stderr)
	}
	stdout := string(res.Stdout)
	if !strings.Contains(stdout, "run --rm") {
		t.Fatalf("the configured binary did not receive the docker command line: %q", stdout)
	}
	if !strings.Contains(stdout, "-version") {
		t.Fatalf("the argv was not passed through: %q", stdout)
	}
}

// A pool node (AllCores set) raises the engine's -threads to the node's cores; an ordinary worker
// leaves the argv exactly as it was sent.
func TestFfmpegThreadsRaisedOnlyInAllCoresMode(t *testing.T) {
	for _, tc := range []struct {
		name  string
		cores int
		want  string
	}{{"pool node", 8, "-threads 8"}, {"ordinary worker", 0, "-threads 4"}} {
		s, runner := newMediaSession(t, func(c *Config) { c.AllCores = tc.cores })
		if err := os.WriteFile(filepath.Join(s.dir, "clip.mp4"), []byte("media"), 0o600); err != nil {
			t.Fatalf("seed: %v", err)
		}
		runner.results = func(dir string, argv []string) Result { return Result{} }
		if _, opErr := callMedia(t, s, protocol.OpFfmpeg, map[string]any{
			"argv": []string{"-y", "-threads", "4", "-i", "clip.mp4", "out.mp4"}, "timeoutSec": 60,
		}); opErr != nil {
			t.Fatalf("%s: %+v", tc.name, opErr)
		}
		if got := strings.Join(runner.last(), " "); !strings.Contains(got, tc.want) {
			t.Fatalf("%s: %q does not contain %q", tc.name, got, tc.want)
		}
	}
}

// The ffmpeg op applies the output check itself: an output whose extension
// picks the hls muxer is refused before ffmpeg runs.
func TestFfmpegOpRefusesAGuessedMuxerOutput(t *testing.T) {
	s, runner := newMediaSession(t)
	_, opErr := callMedia(t, s, protocol.OpFfmpeg, map[string]any{
		"argv": []string{"-i", "clip.mp4", "-c", "copy", "out.m3u8"}, "timeoutSec": 60,
	})
	if opErr == nil || opErr.Code != protocol.ErrCodeArgvNotAllowed {
		t.Fatalf("ffmpeg with an .m3u8 output = %+v, want argv_not_allowed", opErr)
	}
	if runner.count() != 0 {
		t.Fatal("a refused argv still reached the runner")
	}
}

// The read op answers a 2 MiB file, and refuses (never an oversize frame) a
// 3.86 MB binary whose base64 form cannot fit one payload.
func TestMediaReadOpInlineLimits(t *testing.T) {
	s, _ := newMediaSession(t)
	big := make([]byte, 2<<20)
	for i := range big {
		big[i] = byte(i%251) | 0x80
	}
	if err := os.WriteFile(filepath.Join(s.dir, "two.bin"), big, 0o600); err != nil {
		t.Fatalf("seed: %v", err)
	}
	out, opErr := callMedia(t, s, protocol.OpRead, map[string]any{"path": "two.bin"})
	if opErr != nil {
		t.Fatalf("read 2 MiB: %+v", opErr)
	}
	if !agent.FitsInline(json.RawMessage(out)) {
		t.Fatalf("a 2 MiB read result does not fit inline: %d bytes", len(out))
	}

	huge := make([]byte, 3860000)
	for i := range huge {
		huge[i] = byte(i%251) | 0x80
	}
	if err := os.WriteFile(filepath.Join(s.dir, "huge.bin"), huge, 0o600); err != nil {
		t.Fatalf("seed: %v", err)
	}
	_, opErr = callMedia(t, s, protocol.OpRead, map[string]any{"path": "huge.bin"})
	if opErr == nil || opErr.Code != protocol.ErrCodeFileTooLarge || opErr.Retryable {
		t.Fatalf("read 3.86 MB binary = %+v, want file_too_large", opErr)
	}
}

// An ffprobe whose stdout cannot fit one payload is refused, not sent.
func TestFfprobeRefusesOversizeStdout(t *testing.T) {
	s, runner := newMediaSession(t)
	if err := os.WriteFile(filepath.Join(s.dir, "clip.mp4"), []byte("media"), 0o600); err != nil {
		t.Fatalf("seed: %v", err)
	}
	runner.results = func(dir string, argv []string) Result {
		return Result{ExitCode: 0, Stdout: []byte(strings.Repeat("x", agent.MaxInlineBytes+1))}
	}
	_, opErr := callMedia(t, s, protocol.OpFfprobe, map[string]any{
		"argv": []string{"-v", "quiet", "-print_format", "json", "-show_streams", "clip.mp4"}, "timeoutSec": 30,
	})
	if opErr == nil || opErr.Code != protocol.ErrCodeOutputTooLarge {
		t.Fatalf("oversize ffprobe stdout = %+v, want output_too_large", opErr)
	}
}
