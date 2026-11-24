package main

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"

	"github.com/gorilla/mux"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/container"
)

const testExecutionID = "11111111-1111-1111-1111-111111111111"

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

func TestStatusFromErr(t *testing.T) {
	cases := []struct {
		err  error
		want int
	}{
		{container.ErrNotFound, http.StatusNotFound},
		// A missing file inside the workspace is a 404, not a 500 — os.Root
		// returns *PathError wrapping ENOENT and callers pass it through.
		{&os.PathError{Op: "openat", Path: "src/gone.tsx", Err: os.ErrNotExist}, http.StatusNotFound},
		{container.ErrInvalidPath, http.StatusBadRequest},
		{container.ErrBadExec, http.StatusBadRequest},
		{container.ErrBadExecution, http.StatusBadRequest},
		{container.ErrFileTooLarge, http.StatusRequestEntityTooLarge},
		{container.ErrArchiveTooLarge, http.StatusRequestEntityTooLarge},
		{container.ErrInvalidArchive, http.StatusBadRequest},
		{errors.New("boom"), http.StatusInternalServerError},
	}
	for _, tc := range cases {
		if got := statusFromErr(tc.err); got != tc.want {
			t.Errorf("statusFromErr(%v) = %d, want %d", tc.err, got, tc.want)
		}
	}
}

func TestRequireAPIToken(t *testing.T) {
	var reached bool
	handler := requireAPIToken("s3cret", http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		reached = true
		w.WriteHeader(http.StatusOK)
	}))

	cases := []struct {
		header string
		want   int
	}{
		{"", http.StatusUnauthorized},
		{"Bearer wrong", http.StatusUnauthorized},
		{"s3cretx", http.StatusUnauthorized},
		{"Bearer s3cret", http.StatusOK},
		{"s3cret", http.StatusOK},
	}
	for _, tc := range cases {
		reached = false
		req := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/api/v1/sandboxes", nil)
		if tc.header != "" {
			req.Header.Set("Authorization", tc.header)
		}
		rec := httptest.NewRecorder()
		handler.ServeHTTP(rec, req)
		if rec.Code != tc.want {
			t.Errorf("Authorization %q: got %d, want %d", tc.header, rec.Code, tc.want)
		}
		if reached != (tc.want == http.StatusOK) {
			t.Errorf("Authorization %q: handler reached = %v", tc.header, reached)
		}
	}
}

func TestRawFileStreamsLargeFile(t *testing.T) {
	root := t.TempDir()
	m := container.NewWithRunner(container.Config{SandboxRoot: root, SandboxNetwork: "", MaxSandboxes: 5, SandboxImage: "img"}, "docker",
		func(ctx context.Context, args ...string) ([]byte, error) {
			// "run" must materialise package.json for the init wait loop.
			for i, a := range args {
				if a == "-v" && i+1 < len(args) {
					ws := strings.SplitN(args[i+1], ":", 2)[0]
					_ = os.WriteFile(filepath.Join(ws, "package.json"), []byte("{}"), 0o644)
				}
			}
			return nil, nil
		})
	sb, _, err := m.Create(testExecutionID)
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	// larger than the /files/content cap, which must not apply here
	size := int64(container.MaxReadFileBytes + 1024)
	writeSparse(t, filepath.Join(sb.WorkspacePath, "out.mp4"), size)
	h := &apiHandler{manager: m}
	router := mux.NewRouter()
	router.HandleFunc("/{workflowExecutionId}/files/raw", h.getFileRaw)

	req := httptest.NewRequestWithContext(context.Background(), http.MethodGet, "/"+testExecutionID+"/files/raw?path=out.mp4", nil)
	rec := httptest.NewRecorder()
	router.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("status %d: %s", rec.Code, rec.Body.String()[:min(rec.Body.Len(), 200)])
	}
	if got := rec.Header().Get("Content-Length"); got != strconv.FormatInt(size, 10) {
		t.Errorf("Content-Length = %q, want %d", got, size)
	}
	if int64(rec.Body.Len()) != size {
		t.Errorf("body len = %d, want %d", rec.Body.Len(), size)
	}
}

// SBX-10: the status check looks at the workspace's own entries. A package.json
// or node_modules that sandboxed code replaced with a symlink to a path in the
// control plane's namespace must not report whether that path exists.
func TestSandboxStatusDoesNotFollowWorkspaceSymlinks(t *testing.T) {
	ws := t.TempDir()
	outside := t.TempDir()
	if err := os.WriteFile(filepath.Join(outside, "secret"), []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(filepath.Join(outside, "secret"), filepath.Join(ws, "package.json")); err != nil {
		t.Skipf("symlinks are not available here: %v", err)
	}
	if err := os.Symlink(outside, filepath.Join(ws, "node_modules")); err != nil {
		t.Fatal(err)
	}
	if workspaceHas(ws, "package.json", false) {
		t.Error("a package.json symlink out of the workspace was reported as present")
	}
	if workspaceHas(ws, "node_modules", true) {
		t.Error("a node_modules symlink out of the workspace was reported as present")
	}

	real := t.TempDir()
	if err := os.WriteFile(filepath.Join(real, "package.json"), []byte("{}"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(filepath.Join(real, "node_modules"), 0o755); err != nil {
		t.Fatal(err)
	}
	if !workspaceHas(real, "package.json", false) || !workspaceHas(real, "node_modules", true) {
		t.Error("a real package.json and node_modules were not reported")
	}
	if workspaceHas(real, "node_modules", false) || workspaceHas(real, "package.json", true) {
		t.Error("a directory passed for a file, or a file for a directory")
	}
}
