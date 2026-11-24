package container

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

func TestValidateExec(t *testing.T) {
	good := []ExecRequest{
		{Command: "npm", Args: []string{"run", "build"}},
		{Command: "npm", Args: []string{"run", "render"}},
		{Command: "npm", Args: []string{"run", "typecheck"}},
		{Command: "npm", Args: []string{"run", "render", "--", "--frames=0-30", "--concurrency=2"}},
		{Command: "npx", Args: []string{"remotion", "render", "Main"}},
		{Command: "npx", Args: []string{"remotion", "still", "Main"}},
		{Command: "npx", Args: []string{"remotion", "compositions"}},
		{Command: "node", Args: []string{LayoutLintScriptPath, "--composition=Main", "--frames=0,15,30", "--min-text-px=24", "--output-scale=0.6666667"}},
		{Command: "node", Args: []string{LayoutLintScriptPath}},
	}
	for _, req := range good {
		if err := ValidateExec(req); err != nil {
			t.Errorf("ValidateExec rejected valid request %v: %v", req, err)
		}
	}

	bad := []ExecRequest{
		{Command: "", Args: []string{}},
		{Command: "npm", Args: []string{"install"}},
		{Command: "npm", Args: []string{"run"}},
		{Command: "npm", Args: []string{"run", "notallowed"}},
		{Command: "npm", Args: []string{"run", "lint"}},
		{Command: "npm", Args: []string{"run", "build", "positional"}},
		{Command: "npm", Args: []string{"run", "build", "-e"}},
		{Command: "npm", Args: []string{"run", "render", "--frames=0-30"}},
		{Command: "npm", Args: []string{"run", "render", "--script-shell=/workspace/x"}},
		{Command: "npm", Args: []string{"run", "render", "--prefix=/tmp"}},
		{Command: "npm", Args: []string{"run", "render", "--"}},
		{Command: "npm", Args: []string{"run", "render", "--", "--", "--frames=0"}},
		{Command: "npm", Args: []string{"run", "render", "--", "-e"}},
		{Command: "npm", Args: []string{"run", "render", "--userconfig=/workspace/x", "--", "--frames=0"}},
		{Command: "npm", Args: []string{"run", "render", "--verbose", "--", "--frames=0"}},
		{Command: "npm", Args: []string{"run", "build", "--out=$(id)"}},
		{Command: "npx", Args: []string{"node", "script.js"}},
		{Command: "npx", Args: []string{"remotion"}},
		{Command: "npx", Args: []string{"remotion", "unknown"}},
		{Command: "docker", Args: []string{"run"}},
		// node runs the one shipped layout-lint script, nothing else, with --flag=value args only.
		{Command: "node", Args: []string{}},
		{Command: "node", Args: []string{"script.js"}},
		{Command: "node", Args: []string{"-e", "require('child_process')"}},
		{Command: "node", Args: []string{"/workspace/reelbolt/check-layout.mjs"}},
		{Command: "node", Args: []string{"--eval=1", LayoutLintScriptPath}},
		{Command: "node", Args: []string{LayoutLintScriptPath, "-e"}},
		{Command: "node", Args: []string{LayoutLintScriptPath, "--composition=a b"}},
		{Command: "node", Args: []string{LayoutLintScriptPath, "--composition=$(id)"}},
		{Command: "node", Args: []string{LayoutLintScriptPath, "--frames=../../etc"}},
		{Command: "node", Args: []string{LayoutLintScriptPath, "positional"}},
		{Command: "node", Args: []string{LayoutLintScriptPath, "--a=1", "--a=1", "--a=1", "--a=1", "--a=1", "--a=1", "--a=1", "--a=1", "--a=1"}},
	}
	for _, req := range bad {
		if err := ValidateExec(req); err == nil {
			t.Errorf("ValidateExec accepted invalid request %v", req)
		}
	}
}

func TestSanitizeExecRequest(t *testing.T) {
	// should append flag when absent and use headless-shell path
	req := ExecRequest{Command: "npx", Args: []string{"remotion", "render", "Comp"}}
	SanitizeExecRequest(&req)
	wantEnds := "--chromium-executable=/workspace/node_modules/.remotion/headless-shell"
	if req.Args[len(req.Args)-1] != wantEnds {
		t.Errorf("expected headless-shell arg appended, got %v", req.Args)
	}

	// should not duplicate if already present
	req2 := ExecRequest{Command: "npx", Args: []string{"remotion", "render", "C", "--chromium-executable=/workspace/node_modules/.remotion/headless-shell"}}
	SanitizeExecRequest(&req2)
	count := 0
	for _, a := range req2.Args {
		if a == wantEnds {
			count++
		}
	}
	if count != 1 {
		t.Errorf("expected exactly one chromium arg, got %d in %v", count, req2.Args)
	}

	// non-render command should not be touched
	req3 := ExecRequest{Command: "npm", Args: []string{"run", "build"}}
	old := req3.Args
	SanitizeExecRequest(&req3)
	if !reflect.DeepEqual(old, req3.Args) {
		t.Errorf("unexpected modification of npm request: %v vs %v", old, req3.Args)
	}
}

func TestVerifyHeadlessShell(t *testing.T) {
	var lastArgs []string
	m := NewWithRunner(Config{}, "docker", func(ctx context.Context, args ...string) ([]byte, error) {
		lastArgs = args
		return []byte("ok"), nil
	})
	if err := m.verifyHeadlessShell("container123"); err != nil {
		t.Fatalf("expected no error, got %v", err)
	}
	if len(lastArgs) < 5 || lastArgs[0] != "exec" || lastArgs[1] != "container123" {
		t.Errorf("unexpected docker args: %v", lastArgs)
	}
	cmd := lastArgs[len(lastArgs)-1]
	if !strings.Contains(cmd, "[ -x "+headlessShellPath+" ]") {
		t.Errorf("command did not check the stable path: %v", cmd)
	}
	if strings.Contains(cmd, "chromium") && strings.Contains(cmd, "ln ") {
		t.Errorf("command must not fall back to a system chromium symlink: %v", cmd)
	}

	// A missing binary is a hard error carrying the explanatory message.
	m.run = func(ctx context.Context, args ...string) ([]byte, error) {
		return []byte("headless shell not found"), errors.New("exit status 1")
	}
	err := m.verifyHeadlessShell("container123")
	if err == nil || !strings.Contains(err.Error(), "headless shell not found") {
		t.Fatalf("expected a clear hard error, got %v", err)
	}
}

// The architecture-specific chrome-headless-shell directory name (differs between
// amd64 and arm64) must never be hard-coded: the image links the
// real binary to headlessShellPath at build time.
func TestNoArchSpecificHeadlessShellPath(t *testing.T) {
	if headlessShellPath != "/workspace/node_modules/.remotion/headless-shell" {
		t.Errorf("unexpected headlessShellPath %q", headlessShellPath)
	}
	for _, file := range []string{"exec.go", "driver.go", "lifecycle.go", "files.go", "../../template/package.json"} {
		data, err := os.ReadFile(file)
		if err != nil {
			t.Fatalf("read %s: %v", file, err)
		}
		for i, line := range strings.Split(string(data), "\n") {
			if strings.Contains(line, "linux64") {
				t.Errorf("%s:%d hard-codes linux64: %s", file, i+1, strings.TrimSpace(line))
			}
		}
	}
}

// newTestSandbox creates a real workspace directory on disk and returns a
// sandbox pointing at it, so the file-operation tests exercise the actual
// os.Root containment rather than a mock.
func newTestSandbox(t *testing.T) (*Driver, *Sandbox) {
	t.Helper()
	root := t.TempDir()
	workspace := filepath.Join(root, "ws")
	if err := os.MkdirAll(workspace, 0o770); err != nil {
		t.Fatalf("mkdir workspace: %v", err)
	}
	m := NewWithRunner(Config{SandboxRoot: root}, "docker", nil)
	sb := &Sandbox{
		ID:                  "test-id",
		WorkflowExecutionID: "",
		ContainerName:       "rf-sbx-test",
		WorkspacePath:       workspace,
	}
	m.sandboxes[sb.ID] = sb
	return m, sb
}

// A symlink planted inside the workspace must never be followed out of it. This
// is the sandbox-to-host escalation path: the sandbox container can create the
// link, and this service resolves it inside its own mount namespace, which has
// the Docker socket mounted.
func TestWorkspaceRootRefusesSymlinkEscape(t *testing.T) {
	_, sb := newTestSandbox(t)

	outside := filepath.Join(filepath.Dir(sb.WorkspacePath), "outside.txt")
	if err := os.WriteFile(outside, []byte("host secret"), 0o600); err != nil {
		t.Fatalf("seed outside file: %v", err)
	}

	links := map[string]string{
		"abs-escape": "/",
		"rel-escape": "../",
		"direct":     outside,
	}
	for name, target := range links {
		if err := os.Symlink(target, filepath.Join(sb.WorkspacePath, name)); err != nil {
			t.Fatalf("symlink %s: %v", name, err)
		}
	}

	root, err := openWorkspace(sb)
	if err != nil {
		t.Fatalf("openWorkspace: %v", err)
	}
	defer func() { _ = root.Close() }() // best-effort teardown; the assertions
	// above are what this test is about.

	for name := range links {
		if _, err := root.Open(name + "/outside.txt"); err == nil {
			t.Errorf("reading through symlink %q escaped the workspace", name)
		}
		if _, err := root.OpenFile(name+"/planted.txt", os.O_WRONLY|os.O_CREATE, 0o644); err == nil {
			t.Errorf("writing through symlink %q escaped the workspace", name)
		}
	}

	if _, err := os.ReadFile(outside); err != nil {
		t.Fatalf("outside file should be untouched: %v", err)
	}
}

// Deleting a symlink must unlink the link, never recurse into its target.
func TestRemoveAllInDoesNotFollowSymlink(t *testing.T) {
	_, sb := newTestSandbox(t)

	victimDir := filepath.Join(filepath.Dir(sb.WorkspacePath), "victim")
	if err := os.MkdirAll(victimDir, 0o750); err != nil {
		t.Fatalf("mkdir victim: %v", err)
	}
	victimFile := filepath.Join(victimDir, "keep.txt")
	if err := os.WriteFile(victimFile, []byte("keep"), 0o600); err != nil {
		t.Fatalf("seed victim file: %v", err)
	}
	if err := os.Symlink(victimDir, filepath.Join(sb.WorkspacePath, "link")); err != nil {
		t.Fatalf("symlink: %v", err)
	}

	root, err := openWorkspace(sb)
	if err != nil {
		t.Fatalf("openWorkspace: %v", err)
	}
	defer func() { _ = root.Close() }() // best-effort teardown

	if err := removeAllIn(root, "link"); err != nil {
		t.Fatalf("removeAllIn: %v", err)
	}
	if _, err := os.Stat(victimFile); err != nil {
		t.Errorf("removeAllIn followed the symlink and deleted its target: %v", err)
	}
}

func TestNormalizeSandboxPath(t *testing.T) {
	cases := map[string]string{
		"":                 ".",
		"/":                ".",
		"src/root.tsx":     "src/root.tsx",
		"/src/root.tsx":    "src/root.tsx",
		"../../etc/passwd": "etc/passwd",
		"src/../../../etc": "etc",
		"./src//a.tsx":     "src/a.tsx",
		`..\..\etc\passwd`: "etc/passwd",
	}
	for input, want := range cases {
		if got := NormalizePath(input); got != want {
			t.Errorf("NormalizePath(%q) = %q, want %q", input, got, want)
		}
	}
}

// A package name may not begin with a dash, or it lands in `npm install --save`
// as a flag rather than as a package.
func TestNpmPackageNameRejectsFlags(t *testing.T) {
	bad := []string{"--foo", "-g", "--registry=http://evil", ".hidden", "-", "--"}
	for _, name := range bad {
		if npmPackageNameRe.MatchString(name) {
			t.Errorf("package name %q should be rejected", name)
		}
	}
	good := []string{"react", "d3@7.0.0", "@remotion/shapes", "@remotion/shapes@4.0.0", "framer-motion"}
	for _, name := range good {
		if !npmPackageNameRe.MatchString(name) {
			t.Errorf("package name %q should be accepted", name)
		}
	}
}

// The sandbox network must be created with --internal unless egress is
// explicitly opted into, since that flag is what removes the route to the host.
func TestEnsureNetworkRequestsInternal(t *testing.T) {
	var createArgs []string
	d := NewWithRunner(Config{SandboxNetwork: "sandbox-net", NetworkEgress: false}, "docker", func(ctx context.Context, args ...string) ([]byte, error) {
		if len(args) > 1 && args[1] == "create" {
			createArgs = args
		}
		if len(args) > 1 && args[1] == "inspect" {
			return []byte("true\n"), nil
		}
		return []byte(""), nil
	})

	if err := d.EnsureNetwork(); err != nil {
		t.Fatalf("EnsureNetwork: %v", err)
	}

	var hasInternal bool
	for _, a := range createArgs {
		if a == "--internal" {
			hasInternal = true
		}
	}
	if !hasInternal {
		t.Errorf("network was not created with --internal: %v", createArgs)
	}
}

const testExecutionID = "11111111-1111-1111-1111-111111111111"

func newIndexedTestSandbox(t *testing.T) (*Driver, *Sandbox) {
	t.Helper()
	m, sb := newTestSandbox(t)
	m.executionIndex[testExecutionID] = sb.ID
	return m, sb
}

func writeSparse(t *testing.T, path string, size int64) {
	t.Helper()
	f, err := os.Create(path)
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	defer func() { _ = f.Close() }() // best-effort teardown
	if err := f.Truncate(size); err != nil {
		t.Skipf("sparse file unsupported: %v", err)
	}
}

func TestReadFileRejectsOversize(t *testing.T) {
	m, sb := newIndexedTestSandbox(t)
	writeSparse(t, filepath.Join(sb.WorkspacePath, "big.bin"), MaxReadFileBytes+1)
	_, err := m.ReadFile(testExecutionID, "big.bin")
	if !errors.Is(err, ErrFileTooLarge) {
		t.Fatalf("want ErrFileTooLarge, got %v", err)
	}
	// exactly at the cap is still readable
	writeSparse(t, filepath.Join(sb.WorkspacePath, "ok.bin"), MaxReadFileBytes)
	data, err := m.ReadFile(testExecutionID, "ok.bin")
	if err != nil || int64(len(data)) != MaxReadFileBytes {
		t.Fatalf("file at cap: len=%d err=%v", len(data), err)
	}
}

func TestRawFileGuards(t *testing.T) {
	m, sb := newIndexedTestSandbox(t)
	m.cfg.MaxRenderBytes = 10
	if err := os.WriteFile(filepath.Join(sb.WorkspacePath, "small.txt"), []byte("hello"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(sb.WorkspacePath, "big.txt"), []byte("hello world!!"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(filepath.Join(sb.WorkspacePath, "dir"), 0o755); err != nil {
		t.Fatal(err)
	}
	outside := filepath.Join(filepath.Dir(sb.WorkspacePath), "secret.txt")
	if err := os.WriteFile(outside, []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(outside, filepath.Join(sb.WorkspacePath, "link")); err != nil {
		t.Fatal(err)
	}

	f, n, err := m.OpenRawFile(testExecutionID, "small.txt")
	if err != nil || n != 5 {
		t.Fatalf("small: n=%d err=%v", n, err)
	}
	if err := f.Close(); err != nil {
		// Closing a file we never read can only fail on a broken filesystem,
		// which is not what this test is about.
		t.Logf("close: %v", err)
	}
	if _, _, err := m.OpenRawFile(testExecutionID, "big.txt"); !errors.Is(err, ErrFileTooLarge) {
		t.Errorf("cap: got %v", err)
	}
	if _, _, err := m.OpenRawFile(testExecutionID, "dir"); !errors.Is(err, ErrInvalidPath) {
		t.Errorf("directory: got %v", err)
	}
	if _, _, err := m.OpenRawFile(testExecutionID, "link"); err == nil {
		t.Errorf("symlink escape was followed")
	}
	if _, _, err := m.OpenRawFile(testExecutionID, "nope"); !errors.Is(err, ErrNotFound) && !errors.Is(err, os.ErrNotExist) {
		t.Errorf("missing: got %v", err)
	}
}
