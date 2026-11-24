package daemon

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent/media"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/governor"
	"github.com/vecchiotom/reelbolt/runner/internal/runtime"
)

// These tests start REAL containers. They are skipped unless
// REELBOLT_RUNNER_DOCKER_TESTS=1 is set, because the default `go test ./...` has to
// stay green on a machine with no container runtime (and on the Windows and
// Linux CI runners that only compile).
//
// They are the answer to the one open question D4b left: no container had ever
// been started, so the workspace and media executors were proven against fakes
// only. What follows drives the real driver, a real runtime image and the real
// op surface that the engine's steps use.
//
//	REELBOLT_RUNNER_DOCKER_TESTS=1 //	REELBOLT_TEST_RUNTIME_IMAGE=reelbolt-sandbox-runtime:local //	go test ./internal/daemon -run Real -v -timeout 30m
const (
	dockerTestsEnv = "REELBOLT_RUNNER_DOCKER_TESTS"
	dockerImageEnv = "REELBOLT_TEST_RUNTIME_IMAGE"
	// renderEnv additionally runs a full Remotion render, which bundles the
	// composition and drives Chrome. It is the slowest and most valuable check
	// here, so it is opt-in.
	renderEnv = "REELBOLT_TEST_REMOTION_RENDER"
	// workDirEnv points the suite at a directory the container runtime can bind-mount.
	workDirEnv = "REELBOLT_TEST_WORKDIR"
)

// testWorkDir returns a directory the container runtime can bind-mount.
//
// When the suite itself runs inside a container with the host socket mounted, a
// t.TempDir() lives in that container and is invisible to the host daemon, so the
// harness points REELBOLT_TEST_WORKDIR at a path both sides can see.
func testWorkDir(t *testing.T, name string) string {
	t.Helper()
	base := os.Getenv(workDirEnv)
	if base == "" {
		return t.TempDir()
	}
	dir := filepath.Join(base, name)
	if err := os.RemoveAll(dir); err != nil {
		t.Fatalf("clean %s: %v", dir, err)
	}
	if err := os.MkdirAll(dir, 0o777); err != nil {
		t.Fatalf("create %s: %v", dir, err)
	}
	return dir
}
func realRuntime(t *testing.T) (string, runtime.Candidate) {
	t.Helper()
	if os.Getenv(dockerTestsEnv) != "1" {
		t.Skipf("set %s=1 to start real containers", dockerTestsEnv)
	}
	binary, err := exec.LookPath("docker")
	if err != nil {
		t.Skipf("no container CLI on this machine: %v", err)
	}
	image := os.Getenv(dockerImageEnv)
	if image == "" {
		image = "reelbolt-sandbox-runtime:local"
	}
	if out, err := exec.CommandContext(context.Background(), "docker", "image", "inspect", "--format", "{{.Id}}", image).Output(); err != nil {
		t.Skipf("the runtime image %q is not present locally (%v: %s)", image, err, out)
	}
	return image, runtime.Candidate{Kind: runtime.KindDocker, Binary: binary, Source: "test"}
}

// op runs one op through a session and returns its result as JSON.
func op(t *testing.T, ctx context.Context, sess agent.Session, name string, args any) map[string]any {
	t.Helper()
	var raw json.RawMessage
	if args != nil {
		encoded, err := json.Marshal(args)
		if err != nil {
			t.Fatal(err)
		}
		raw = encoded
	}
	res, opErr := sess.Handle(ctx, name, raw)
	if opErr != nil {
		t.Fatalf("op %s failed: %s: %s", name, opErr.Code, opErr.Message)
	}
	encoded, err := json.Marshal(res)
	if err != nil {
		t.Fatal(err)
	}
	var out map[string]any
	if err := json.Unmarshal(encoded, &out); err != nil {
		t.Fatalf("op %s returned something that is not an object: %s", name, encoded)
	}
	return out
}

// repairHeadlessShell mirrors what sandbox/Dockerfile does at image build time:// it links node_modules/.remotion/headless-shell at the browser binary, and the// container driver refuses to run anything in a workspace without it. An image// built before that step has the browser but no link, so a local test repairs// it rather than pretending the exec path does not exist. It is a no-op on an// image built from the current Dockerfile.
func repairHeadlessShell(t *testing.T, sess agent.Session) {
	t.Helper()
	dir := workspacePathOf(t, sess)
	link := filepath.Join(dir, "node_modules", ".remotion", "headless-shell")
	if _, err := os.Stat(link); err == nil {
		return
	}
	browserRoot := filepath.Join(dir, "node_modules", ".remotion", "chrome-headless-shell")
	found := ""
	_ = filepath.WalkDir(browserRoot, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if d.IsDir() {
			return nil
		}
		if d.Name() == "chrome-headless-shell" || d.Name() == "headless_shell" {
			found = path
			return filepath.SkipAll
		}
		return nil
	})
	if found == "" {
		t.Skipf("the runtime image has no browser binary under %s", browserRoot)
	}
	rel, err := filepath.Rel(filepath.Dir(link), found)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(rel, link); err != nil {
		t.Fatalf("link the headless shell: %v", err)
	}
	t.Logf("the local runtime image predates the Dockerfile step that links .remotion/headless-shell; created the link at %s -> %s", link, rel)
}

// TestRealRemotionWorkspaceSession is the end-to-end proof the workspace
// executor never had: a real container from the pinned image, the template
// copied into a real bind-mounted workspace, a real file round trip through it,
// and the real Remotion CLI running inside it.
func TestRealRemotionWorkspaceSession(t *testing.T) {
	image, cand := realRuntime(t)
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Minute)
	defer cancel()

	ex, err := newRemotionExecutor(workspaceConfig{
		Image:   image,
		Root:    testWorkDir(t, "workspaces"),
		Slots:   1,
		Network: "none",
	}, cand)
	if err != nil {
		t.Fatalf("newRemotionExecutor: %v", err)
	}

	scratch := testWorkDir(t, "scratch")
	job := &agent.Job{
		JobID:      "itest-remotion",
		JobType:    protocol.JobRemotionWorkspace,
		ScratchDir: scratch,
		Spec: json.RawMessage(
			`{"executionId":"11111111-1111-1111-1111-111111111111","template":{"name":"default","version":"1"}}`),
	}
	t.Log("starting a real workspace container (the image is copied into the workspace; this takes a moment)")
	started := time.Now()
	sess, err := ex.New(ctx, job)
	if err != nil {
		t.Fatalf("the workspace container could not be started: %v", err)
	}
	t.Logf("workspace container up in %s", time.Since(started).Round(time.Second))
	repairHeadlessShell(t, sess)

	// The container must be gone once the session closes, however this test ends.
	defer func() {
		sess.Close(context.Background())
		if out, err := exec.CommandContext(context.Background(), "docker", "ps", "-a", "--filter", "name=rf-sbx-", "--format", "{{.Names}}").Output(); err == nil {
			if names := string(out); len(names) > 0 {
				t.Errorf("workspace containers survived the session: %q", names)
			}
		}
	}()

	// status: the session's own view of its workspace.
	status := op(t, ctx, sess, protocol.OpStatus, nil)
	if status["root"] == nil {
		t.Logf("status = %v", status)
	}

	// A file written through the op surface must land in the container's own
	// workspace directory, because that is what is bind-mounted into it.
	written := op(t, ctx, sess, protocol.OpWriteFile, map[string]any{
		"path":    "src/reelbolt-itest.txt",
		"content": "hello from the runner",
	})
	if bytes, ok := written["bytes"].(float64); !ok || int(bytes) != len("hello from the runner") {
		t.Errorf("writeFile reported %v bytes", written["bytes"])
	}
	back := op(t, ctx, sess, protocol.OpReadFile, map[string]any{"path": "src/reelbolt-itest.txt"})
	if content, _ := back["content"].(string); content != "hello from the runner" {
		t.Errorf("readFile returned %q", back["content"])
	}

	// The template really arrived: node_modules and the entry point are there.
	listing := op(t, ctx, sess, protocol.OpListFiles, map[string]any{"path": ".", "depth": 1})
	entries, _ := listing["entries"].([]any)
	if len(entries) == 0 {
		t.Fatalf("the workspace is empty, so the container never copied the template: %v", listing)
	}

	// And a command really runs inside the container: the Remotion CLI lists the
	// compositions of the bundled template.
	execRes := op(t, ctx, sess, protocol.OpExec, map[string]any{
		"command":    "npm",
		"args":       []string{"run", "compositions"},
		"timeoutSec": 600,
	})
	if code, _ := execRes["exitCode"].(float64); code != 0 {
		t.Fatalf("npm run compositions exited %v\nstdout: %v\nstderr: %v",
			execRes["exitCode"], execRes["stdout"], execRes["stderr"])
	}
	t.Logf("remotion compositions output: %v", execRes["stdout"])

	// The argv allowlist is the containment: a command that is not on it must be
	// refused before anything reaches the container.
	if _, opErr := sess.Handle(ctx, protocol.OpExec, json.RawMessage(
		`{"command":"sh","args":["-c","cat /etc/passwd"]}`)); opErr == nil {
		t.Fatal("the exec allowlist let a shell through")
	} else if opErr.Code != protocol.ErrCodeExecNotAllowed {
		t.Errorf("refused with %q, want %q", opErr.Code, protocol.ErrCodeExecNotAllowed)
	}

	if os.Getenv(renderEnv) == "1" {
		// The template owns its composition ids, so the id comes from the CLI's own
		// output instead of being typed in here (the template's own npm render script
		// hard-codes an id the current root.tsx does not register).
		composition := firstComposition(execRes["stdout"].(string))
		if composition == "" {
			t.Fatalf("no composition id in the template output: %v", execRes["stdout"])
		}
		// The composition's default props name a voiceover the engine stages with the
		// stage op; without one the render 404s on its own audio. A silent WAV stands
		// in. It is placed host-side rather than through the write op because the op
		// creates the directory 0700 for the RUNNER's user, and the browser-side node
		// process is uid 1000 in the container: the file op path is covered by the
		// write/read assertions above.
		stageSilentVoiceover(t, sess)
		t.Logf("rendering %s for real (this drives Chrome inside the container)", composition)
		renderStart := time.Now()
		render := op(t, ctx, sess, protocol.OpExec, map[string]any{
			"command": "npx",
			"args": []string{
				"remotion", "render", "src/index.ts", composition, "out/video.mp4",
				"--frames=0-2",
			},
			"timeoutSec": 1800,
		})
		if code, _ := render["exitCode"].(float64); code != 0 {
			t.Fatalf("the render exited %v\nstderr tail: %v", render["exitCode"], render["stderr"])
		}
		t.Logf("the render finished in %s", time.Since(renderStart).Round(time.Second))
		out := filepath.Join(workspacePathOf(t, sess), "out", "video.mp4")
		rendered, err := os.Stat(out)
		if err != nil {
			t.Fatalf("the render reported success but produced no file: %v", err)
		}
		if rendered.Size() < 1024 {
			t.Fatalf("the rendered file is only %d bytes", rendered.Size())
		}
		t.Logf("rendered %d bytes of MP4 to %s", rendered.Size(), out)
	}
}

// workspacePathOf recovers the bind-mounted workspace directory from the
// container's own filesystem view by asking the driver.
func workspacePathOf(t *testing.T, sess agent.Session) string {
	t.Helper()
	out, err := exec.CommandContext(context.Background(), "docker", "ps", "-a", "--filter", "name=rf-sbx-", "--format", "{{.Names}}").Output()
	if err != nil {
		t.Skipf("cannot ask the runtime for the container: %v", err)
	}
	name := ""
	if fields := strings.Fields(string(out)); len(fields) > 0 {
		name = fields[0]
	}
	if name == "" {
		t.Skip("no workspace container is running")
	}
	mount, err := exec.CommandContext(context.Background(), "docker", "inspect", name, "--format",
		"{{range .Mounts}}{{if eq .Destination \"/workspace\"}}{{.Source}}{{end}}{{end}}").Output()
	if err != nil {
		t.Skipf("cannot inspect the container: %v", err)
	}
	src := strings.TrimSpace(string(mount))
	if src == "" {
		t.Skip("the container has no /workspace mount")
	}
	return src
}

// TestRealMediaSessionCompilesWithRealFFmpeg drives the media executor's
// container path: a real ffmpeg inside the runtime image, writing a real file
// into the job's scratch directory.
func TestRealMediaSessionCompilesWithRealFFmpeg(t *testing.T) {
	image, cand := realRuntime(t)
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Minute)
	defer cancel()

	ex, err := newMediaExecutor(image, &cand, governor.LimitsConfig{}.Resolve(governor.Host{}), media.Config{
		FFmpegPath:  DefaultFfmpegPath,
		FFprobePath: DefaultFFprobePath,
	})
	if err != nil {
		t.Fatalf("newMediaExecutor: %v", err)
	}
	if ex == nil {
		t.Fatal("no media executor was built")
	}

	scratch := testWorkDir(t, "scratch")
	job := &agent.Job{
		JobID:      "itest-media",
		JobType:    protocol.JobVideoCompile,
		ScratchDir: scratch,
		Spec: json.RawMessage(
			`{"executionId":"11111111-1111-1111-1111-111111111111","stepId":"s1","mode":"Reencode"}`),
	}
	sess, err := ex.New(ctx, job)
	if err != nil {
		t.Fatalf("the media session could not start: %v", err)
	}
	defer func() {
		sess.Close(context.Background())
	}()

	// A real encode: synthesise one second of video and write it into /media,
	// which is the scratch directory bind-mounted read-write.
	encoded := op(t, ctx, sess, protocol.OpFfmpeg, map[string]any{
		"argv": []string{
			"-hide_banner", "-nostdin",
			"-f", "lavfi", "-i", "testsrc=size=128x128:rate=10",
			"-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p", "out.mp4",
		},
		"timeoutSec": 300,
	})
	if code, _ := encoded["exitCode"].(float64); code != 0 {
		t.Fatalf("the encode exited %v: %v", encoded["exitCode"], encoded["stderrTail"])
	}

	info, err := os.Stat(filepath.Join(scratch, "out.mp4"))
	if err != nil {
		t.Fatalf("the container reported success but wrote nothing into the scratch directory: %v", err)
	}
	if info.Size() < 512 {
		t.Fatalf("out.mp4 is only %d bytes", info.Size())
	}

	// And the probe agrees about what was produced.
	probed := op(t, ctx, sess, protocol.OpFfprobe, map[string]any{
		"argv": []string{
			"-v", "error",
			"-show_entries", "stream=codec_name,width,height",
			"-of", "json", "out.mp4",
		},
		"timeoutSec": 120,
	})
	if code, _ := probed["exitCode"].(float64); code != 0 {
		t.Fatalf("ffprobe exited %v: %v", probed["exitCode"], probed["stderrTail"])
	}
	stdout, _ := probed["stdout"].(string)
	if !containsAll(stdout, "h264", "128") {
		t.Fatalf("ffprobe did not describe an h264 128px stream: %s", stdout)
	}
	t.Logf("ffprobe: %s", strings.TrimSpace(stdout))

	// A path outside the session directory must never reach ffmpeg.
	if _, opErr := sess.Handle(ctx, protocol.OpFfmpeg, json.RawMessage(
		`{"argv":["-i","/etc/passwd","escape.mp4"]}`)); opErr == nil {
		t.Fatal("the argv validator accepted an absolute input path")
	}
	// And the container the encode ran in is gone with the session.
	sess.Close(context.Background())
	if _, err := os.Stat(filepath.Join(scratch, "out.mp4")); !errors.Is(err, os.ErrNotExist) {
		t.Errorf("the scratch directory survived the session close: %v", err)
	}
}

// TestRealMediaFfprobeAcceptsStreamSelectors pins the fixed behaviour where
// the old tripwire stood: the argv validator used to read "v:0" as a protocol
// prefix (a letter, then a colon) and refuse -select_streams v:0 with
// argv_not_allowed. It now applies the scheme check only in an input
// position, so a standard ffprobe stream selector reaches ffprobe and the
// selector's own job — refusing to select anything from a missing input —
// plays out in ffprobe's exit code, not in the validator.
//
// This test was deliberately written as a tripwire: it failed the day the
// validator was fixed, and the signal was to put the selector back into the
// real probe above (TestRealMediaSessionCompilesWithRealFFmpeg) and assert
// success here instead of refusal.
func TestRealMediaFfprobeAcceptsStreamSelectors(t *testing.T) {
	image, cand := realRuntime(t)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()
	ex, err := newMediaExecutor(image, &cand, governor.LimitsConfig{}.Resolve(governor.Host{}),
		media.Config{FFmpegPath: DefaultFfmpegPath, FFprobePath: DefaultFFprobePath})
	if err != nil {
		t.Fatal(err)
	}
	scratch := testWorkDir(t, "selector")
	sess, err := ex.New(ctx, &agent.Job{
		JobID: "itest-selector", JobType: protocol.JobVideoCompile, ScratchDir: scratch,
	})
	if err != nil {
		t.Fatal(err)
	}
	defer sess.Close(context.Background())
	// The probe needs an input to select a stream from.
	if err := os.WriteFile(filepath.Join(scratch, "in.mp4"), []byte("probe me"), 0o600); err != nil {
		t.Fatalf("stage the probe input: %v", err)
	}
	// A stream selector with a missing stream: the validator must not refuse
	// it — ffprobe does, with its own error, not with argv_not_allowed.
	args, err := json.Marshal(map[string]any{
		"argv": []string{"-v", "error", "-select_streams", "v:0", "in.mp4"},
	})
	if err != nil {
		t.Fatal(err)
	}
	_, opErr := sess.Handle(ctx, protocol.OpFfprobe, args)
	if opErr != nil {
		t.Fatalf("the validator refused a standard stream selector: code=%q message=%q", opErr.Code, opErr.Message)
	}
	// And the validator still refuses a real scheme in an input position.
	bad, err := json.Marshal(map[string]any{
		"argv": []string{"-i", "http://example.com/x.mp4"},
	})
	if err != nil {
		t.Fatal(err)
	}
	_, opErr = sess.Handle(ctx, protocol.OpFfprobe, bad)
	if opErr == nil {
		t.Fatal("an http input was accepted through the op")
	}
	if opErr.Code != protocol.ErrCodeArgvNotAllowed {
		t.Fatalf("refused with %q, want %q", opErr.Code, protocol.ErrCodeArgvNotAllowed)
	}
}

// stageSilentVoiceover writes the audio the template's default props expect into
// the session's workspace, readable by the container's user.
func stageSilentVoiceover(t *testing.T, sess agent.Session) {
	t.Helper()
	dir := filepath.Join(workspacePathOf(t, sess), "public", "voiceover")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatalf("stage the voiceover directory: %v", err)
	}
	if err := os.Chmod(filepath.Dir(dir), 0o755); err != nil {
		t.Fatalf("chmod the public directory: %v", err)
	}
	path := filepath.Join(dir, "scene-0.wav")
	if err := os.WriteFile(path, silentWAV(2), 0o644); err != nil {
		t.Fatalf("stage the voiceover: %v", err)
	}
	// Best effort: the container runs as uid 1000, and the test process may be root.
	_ = os.Chown(filepath.Dir(dir), 1000, 1000)
	_ = os.Chown(dir, 1000, 1000)
	_ = os.Chown(path, 1000, 1000)
}

// silentWAV builds a silent 16-bit mono 8 kHz WAV of the given length. It is
// written through the file op so a render that expects staged audio has
// something valid to play, without inventing a second transport for it.
func silentWAV(seconds int) []byte {
	const (
		sampleRate = 8000
		channels   = 1
		bits       = 16
	)
	dataBytes := seconds * sampleRate * channels * bits / 8
	total := 44 + dataBytes
	b := make([]byte, total)
	copy(b[0:4], "RIFF")
	binary.LittleEndian.PutUint32(b[4:8], uint32(total-8))
	copy(b[8:12], "WAVE")
	copy(b[12:16], "fmt ")
	binary.LittleEndian.PutUint32(b[16:20], 16)
	binary.LittleEndian.PutUint16(b[20:22], 1)
	binary.LittleEndian.PutUint16(b[22:24], channels)
	binary.LittleEndian.PutUint32(b[24:28], sampleRate)
	binary.LittleEndian.PutUint32(b[28:32], sampleRate*channels*bits/8)
	binary.LittleEndian.PutUint16(b[32:34], channels*bits/8)
	binary.LittleEndian.PutUint16(b[34:36], bits)
	copy(b[36:40], "data")
	binary.LittleEndian.PutUint32(b[40:44], uint32(dataBytes))
	return b
}

// firstComposition returns the id of the first composition remotion listed.
func firstComposition(out string) string {
	seen := false
	for _, line := range strings.Split(out, "\n") {
		line = strings.TrimSpace(line)
		if strings.HasPrefix(line, "The following compositions") {
			seen = true
			continue
		}
		if !seen || line == "" {
			continue
		}
		fields := strings.Fields(line)
		if len(fields) >= 2 {
			return fields[0]
		}
	}
	return ""
}

func containsAll(haystack string, needles ...string) bool {
	for _, n := range needles {
		found := false
		for i := 0; i+len(n) <= len(haystack); i++ {
			if haystack[i:i+len(n)] == n {
				found = true
				break
			}
		}
		if !found {
			return false
		}
	}
	return true
}
