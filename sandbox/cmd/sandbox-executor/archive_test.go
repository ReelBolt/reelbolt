package main

import (
	"archive/tar"
	"bytes"
	"compress/gzip"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/gorilla/mux"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/container"
)

const secondExecutionID = "22222222-2222-2222-2222-222222222222"

// newArchiveTestRouter creates two sandboxes through a fake container CLI and
// serves the workspace archive routes for them.
func newArchiveTestRouter(t *testing.T, limits container.ArchiveLimits) (*container.Driver, http.Handler) {
	t.Helper()
	m := container.NewWithRunner(container.Config{SandboxRoot: t.TempDir(), MaxSandboxes: 5, SandboxImage: "img", TemplateDir: t.TempDir()}, "docker",
		func(ctx context.Context, args ...string) ([]byte, error) {
			for i, a := range args {
				if a == "-v" && i+1 < len(args) {
					ws := strings.SplitN(args[i+1], ":", 2)[0]
					_ = os.WriteFile(filepath.Join(ws, "package.json"), []byte(`{"dependencies":{"remotion":"4.0.0"}}`), 0o644)
				}
			}
			return nil, nil
		})
	for _, id := range []string{testExecutionID, secondExecutionID} {
		if _, _, err := m.Create(id); err != nil {
			t.Fatalf("create %s: %v", id, err)
		}
	}
	h := &apiHandler{manager: m, archiveLimits: limits}
	router := mux.NewRouter()
	router.HandleFunc("/{workflowExecutionId}"+workspaceArchiveSuffix, h.getWorkspaceArchive).Methods(http.MethodGet)
	router.HandleFunc("/{workflowExecutionId}"+workspaceArchiveSuffix, h.putWorkspaceArchive).Methods(http.MethodPut)
	return m, router
}

func decodeJSON(t *testing.T, rec *httptest.ResponseRecorder) map[string]any {
	t.Helper()
	var body map[string]any
	if err := json.Unmarshal(rec.Body.Bytes(), &body); err != nil {
		t.Fatalf("response is not JSON (%d): %q", rec.Code, rec.Body.String())
	}
	return body
}

func TestWorkspaceArchiveGetAndPut(t *testing.T) {
	m, router := newArchiveTestRouter(t, container.ArchiveLimits{})
	src, err := m.GetByExecution(testExecutionID)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(filepath.Join(src.WorkspacePath, "src"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(src.WorkspacePath, "src/root.tsx"), []byte("export const Root = 1"), 0o644); err != nil {
		t.Fatal(err)
	}

	req := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/"+testExecutionID+workspaceArchiveSuffix, nil)
	rec := httptest.NewRecorder()
	router.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("GET status %d: %s", rec.Code, rec.Body.String())
	}
	if ct := rec.Header().Get("Content-Type"); ct != "application/gzip" {
		t.Errorf("Content-Type = %q", ct)
	}
	if rec.Header().Get("Content-Length") != "" {
		t.Errorf("archive should stream without Content-Length")
	}
	if got := rec.Header().Get("X-Workspace-Entries"); got != "3" { // package.json, src/, src/root.tsx
		t.Errorf("X-Workspace-Entries = %q", got)
	}
	if rec.Header().Get("X-Workspace-Bytes") == "" || rec.Header().Get("X-Workspace-Skipped") != "0" {
		t.Errorf("stats headers: bytes=%q skipped=%q", rec.Header().Get("X-Workspace-Bytes"), rec.Header().Get("X-Workspace-Skipped"))
	}
	archive := rec.Body.Bytes()
	zr, err := gzip.NewReader(bytes.NewReader(archive))
	if err != nil {
		t.Fatalf("not gzip: %v", err)
	}
	tr := tar.NewReader(zr)
	var names []string
	for {
		hdr, err := tr.Next()
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			t.Fatalf("not a valid tar: %v", err)
		}
		names = append(names, hdr.Name)
	}
	if strings.Join(names, ",") != "package.json,src/,src/root.tsx" {
		t.Errorf("entries = %v", names)
	}

	put := httptest.NewRequestWithContext(t.Context(), http.MethodPut, "/"+secondExecutionID+workspaceArchiveSuffix, bytes.NewReader(archive))
	rec = httptest.NewRecorder()
	router.ServeHTTP(rec, put)
	if rec.Code != http.StatusOK {
		t.Fatalf("PUT status %d: %s", rec.Code, rec.Body.String())
	}
	if !strings.Contains(rec.Body.String(), `"extraDependencies":[]`) {
		t.Errorf("extraDependencies must be an empty array: %s", rec.Body.String())
	}
	body := decodeJSON(t, rec)
	if body["files"] != float64(2) || body["directories"] != float64(1) || body["templateCompared"] != false {
		t.Errorf("import result = %v", body)
	}
	dst, err := m.GetByExecution(secondExecutionID)
	if err != nil {
		t.Fatal(err)
	}
	if b, err := os.ReadFile(filepath.Join(dst.WorkspacePath, "src/root.tsx")); err != nil || string(b) != "export const Root = 1" {
		t.Errorf("restored file = %q %v", b, err)
	}
}

func TestWorkspaceArchiveErrors(t *testing.T) {
	_, router := newArchiveTestRouter(t, container.ArchiveLimits{MaxArchiveBytes: 64})

	cases := []struct {
		name, method, id, query string
		body                    []byte
		wantStatus              int
		wantCode                string
	}{
		{"GET unknown execution", http.MethodGet, "33333333-3333-3333-3333-333333333333", "", nil, http.StatusNotFound, ""},
		{"GET bad id", http.MethodGet, "not-a-uuid", "", nil, http.StatusBadRequest, ""},
		{"PUT unknown execution", http.MethodPut, "33333333-3333-3333-3333-333333333333", "", []byte("x"), http.StatusNotFound, ""},
		{"PUT garbage", http.MethodPut, secondExecutionID, "", []byte("this is not a gzip stream"), http.StatusBadRequest, "ARCHIVE_INVALID"},
		{"PUT over the archive cap", http.MethodPut, secondExecutionID, "", gzipNoise(t, 4096), http.StatusRequestEntityTooLarge, "ARCHIVE_TOO_LARGE"},
		{"PUT bad replace", http.MethodPut, secondExecutionID, "?replace=maybe", nil, http.StatusBadRequest, ""},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			req := httptest.NewRequestWithContext(t.Context(), tc.method, "/"+tc.id+workspaceArchiveSuffix+tc.query, bytes.NewReader(tc.body))
			rec := httptest.NewRecorder()
			router.ServeHTTP(rec, req)
			if rec.Code != tc.wantStatus {
				t.Fatalf("status %d, want %d: %s", rec.Code, tc.wantStatus, rec.Body.String())
			}
			body := decodeJSON(t, rec)
			if s, _ := body["error"].(string); s == "" {
				t.Errorf("no error message: %v", body)
			}
			if tc.wantCode != "" && body["code"] != tc.wantCode {
				t.Errorf("code = %v, want %s", body["code"], tc.wantCode)
			}
		})
	}
}

// gzipNoise returns a valid gzip stream of n incompressible bytes.
func gzipNoise(t *testing.T, n int) []byte {
	t.Helper()
	noise := make([]byte, n)
	x := uint32(2463534242)
	for i := range noise {
		x ^= x << 13
		x ^= x >> 17
		x ^= x << 5
		noise[i] = byte(x)
	}
	var buf bytes.Buffer
	zw := gzip.NewWriter(&buf)
	if _, err := zw.Write(noise); err != nil {
		t.Fatal(err)
	}
	if err := zw.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

// Through the middleware, an upload over the archive cap is refused by the
// MaxBytesReader with the same 413 ARCHIVE_TOO_LARGE body.
func TestWorkspaceArchivePutOverMiddlewareLimit(t *testing.T) {
	_, router := newArchiveTestRouter(t, container.ArchiveLimits{})
	handler := requireAPITokenWithLimits("s3cret", bodyLimitFor(256), router)
	req := httptest.NewRequestWithContext(t.Context(), http.MethodPut, "/"+secondExecutionID+workspaceArchiveSuffix, bytes.NewReader(gzipNoise(t, 4096)))
	req.Header.Set("Authorization", "Bearer s3cret")
	rec := httptest.NewRecorder()
	handler.ServeHTTP(rec, req)
	if rec.Code != http.StatusRequestEntityTooLarge {
		t.Fatalf("status %d: %s", rec.Code, rec.Body.String())
	}
	if body := decodeJSON(t, rec); body["code"] != "ARCHIVE_TOO_LARGE" {
		t.Errorf("code = %v", body["code"])
	}
}

// The import route's body cap comes from the archive limit; the 96 MiB cap
// still applies to every other route, including other PUTs.
func TestRequireAPITokenBodyLimits(t *testing.T) {
	const archiveMax = 200 << 20
	var gotLimit int64
	var readErr error
	next := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, readErr = io.Copy(io.Discard, r.Body)
		gotLimit = 0
		var mbe *http.MaxBytesError
		if errors.As(readErr, &mbe) {
			gotLimit = mbe.Limit
		}
		w.WriteHeader(http.StatusOK)
	})
	handler := requireAPITokenWithLimits("s3cret", bodyLimitFor(archiveMax), next)

	send := func(method, path string, size int64) {
		req := httptest.NewRequestWithContext(t.Context(), method, path, io.LimitReader(zeroReader{}, size))
		req.Header.Set("Authorization", "Bearer s3cret")
		handler.ServeHTTP(httptest.NewRecorder(), req)
	}
	archivePath := "/api/v1/sandboxes/" + testExecutionID + workspaceArchiveSuffix

	// An archive import larger than 96 MiB is accepted...
	send(http.MethodPut, archivePath, maxRequestBodyBytes+(4<<20))
	if readErr != nil {
		t.Errorf("archive import over 96 MiB was cut off: %v", readErr)
	}
	// ...up to the archive cap.
	send(http.MethodPut, archivePath, archiveMax+1)
	if gotLimit != archiveMax {
		t.Errorf("archive import limit = %d, want %d (err %v)", gotLimit, archiveMax, readErr)
	}
	// Every other route keeps 96 MiB.
	send(http.MethodPut, "/api/v1/sandboxes/"+testExecutionID+"/files/content?path=a", maxRequestBodyBytes+1)
	if gotLimit != maxRequestBodyBytes {
		t.Errorf("files/content limit = %d, want %d (err %v)", gotLimit, maxRequestBodyBytes, readErr)
	}
	// A GET of the archive route is not an upload.
	send(http.MethodGet, archivePath, maxRequestBodyBytes+1)
	if gotLimit != maxRequestBodyBytes {
		t.Errorf("GET archive limit = %d, want %d", gotLimit, maxRequestBodyBytes)
	}
}

type zeroReader struct{}

func (zeroReader) Read(p []byte) (int, error) {
	clear(p)
	return len(p), nil
}
