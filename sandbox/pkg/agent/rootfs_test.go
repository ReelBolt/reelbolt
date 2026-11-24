package agent

import (
	"bytes"
	"context"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// ---------------------------------------------------------------------------
// Paths: the worker's own containment, not the engine's.
// ---------------------------------------------------------------------------

func TestVirtualPathRejectsEveryEscape(t *testing.T) {
	bad := []string{
		"", ".", "..", "../etc/passwd", "a/../../b", "/etc/passwd", "/",
		"\\\\server\\share", "C:\\Windows\\System32", "~/.ssh/id_rsa",
		"a/..", "..\\..\\x", "nul\x00byte", "a/b/../c", "a/b/./../c",
	}
	for _, p := range bad {
		if got, err := VirtualPath(p); err == nil {
			t.Errorf("VirtualPath(%q) = %q, want a refusal", p, got)
		}
	}
	good := map[string]string{
		"src/Root.tsx":             "src/Root.tsx",
		"./src/./Root.tsx":         "src/Root.tsx",
		"out/final.mp4":            "out/final.mp4",
		"inputs\\clip.mp4":         "inputs/clip.mp4",
		"file-name_with.dots.mp4":  "file-name_with.dots.mp4",
		"deep/nested/path/file.ts": "deep/nested/path/file.ts",
	}
	for in, want := range good {
		got, err := VirtualPath(in)
		if err != nil {
			t.Errorf("VirtualPath(%q) refused: %v", in, err)
			continue
		}
		if got != want {
			t.Errorf("VirtualPath(%q) = %q, want %q", in, got, want)
		}
	}
}

// A symlink inside the session directory must not become a way out of it. This
// is the case a lexical containment check cannot see, and it is why every file
// operation goes through an os.Root.
func TestRootFSConfinesThroughSymlinks(t *testing.T) {
	root := t.TempDir()
	outside := t.TempDir()
	if err := os.WriteFile(filepath.Join(outside, "secret.txt"), []byte("host secret"), 0o600); err != nil {
		t.Fatalf("seed: %v", err)
	}
	if err := os.Symlink(outside, filepath.Join(root, "escape")); err != nil {
		t.Skipf("symlinks are not available here: %v", err)
	}
	fs, err := OpenRootFS(root, 0)
	if err != nil {
		t.Fatalf("OpenRootFS: %v", err)
	}
	defer func() { _ = fs.Close() }() // best-effort teardown

	if _, err := fs.ReadFile("escape/secret.txt", 0); err == nil {
		t.Fatal("a read through a symlink that leaves the session root succeeded")
	}
	if _, err := fs.WriteFile("escape/planted.txt", []byte("x")); err == nil {
		t.Fatal("a write through a symlink that leaves the session root succeeded")
	}
	if _, err := fs.Delete("escape"); err != nil {
		t.Fatalf("deleting the link itself should be allowed: %v", err)
	}
	if _, err := os.Stat(filepath.Join(outside, "secret.txt")); err != nil {
		t.Fatalf("deleting the link removed what it pointed at: %v", err)
	}
}

func TestRootFSEnforcesTheSessionQuota(t *testing.T) {
	fs, err := OpenRootFS(t.TempDir(), 16)
	if err != nil {
		t.Fatalf("OpenRootFS: %v", err)
	}
	defer func() { _ = fs.Close() }() // best-effort teardown
	if _, err := fs.WriteFile("a.bin", bytes.Repeat([]byte("x"), 10)); err != nil {
		t.Fatalf("the first write should fit: %v", err)
	}
	_, err = fs.WriteFile("b.bin", bytes.Repeat([]byte("x"), 10))
	if !errors.Is(err, ErrNoSpace) {
		t.Fatalf("a write past the quota returned %v, want ErrNoSpace", err)
	}
	if fs.DiskUsed() != 10 {
		t.Fatalf("disk used = %d, want 10", fs.DiskUsed())
	}
}

// "A file larger than maxBytes is an error file_too_large, never a silent short
// read."
func TestReadFileNeverSilentlyTruncates(t *testing.T) {
	fs, err := OpenRootFS(t.TempDir(), 0)
	if err != nil {
		t.Fatalf("OpenRootFS: %v", err)
	}
	defer func() { _ = fs.Close() }() // best-effort teardown
	big := bytes.Repeat([]byte("y"), 4096)
	if _, err := fs.WriteFile("big.bin", big); err != nil {
		t.Fatalf("seed: %v", err)
	}
	if _, err := fs.ReadFile("big.bin", 1024); !errors.Is(err, ErrTooLarge) {
		t.Fatalf("reading past the cap returned %v, want ErrTooLarge", err)
	}
	got, err := fs.ReadFile("big.bin", 4096)
	if err != nil || len(got) != 4096 {
		t.Fatalf("reading at the cap returned %d bytes, %v", len(got), err)
	}
}

func TestRootFSListAndDelete(t *testing.T) {
	fs, err := OpenRootFS(t.TempDir(), 0)
	if err != nil {
		t.Fatalf("OpenRootFS: %v", err)
	}
	defer func() { _ = fs.Close() }() // best-effort teardown
	if _, err := fs.WriteFile("src/a.ts", []byte("a")); err != nil {
		t.Fatalf("write: %v", err)
	}
	if _, err := fs.WriteFile("src/nested/b.ts", []byte("bb")); err != nil {
		t.Fatalf("write: %v", err)
	}
	entries, err := fs.ListRoot(1)
	if err != nil {
		t.Fatalf("ListRoot: %v", err)
	}
	if len(entries) != 1 || entries[0].Path != "src" || entries[0].Kind != "dir" {
		t.Fatalf("ListRoot(1) = %+v", entries)
	}
	entries, err = fs.ListRoot(3)
	if err != nil {
		t.Fatalf("ListRoot: %v", err)
	}
	var paths []string
	for _, e := range entries {
		paths = append(paths, e.Path)
	}
	want := "src,src/a.ts,src/nested,src/nested/b.ts"
	if strings.Join(paths, ",") != want {
		t.Fatalf("ListRoot(3) = %v, want %s", paths, want)
	}
	deleted, err := fs.Delete("src")
	if err != nil || !deleted {
		t.Fatalf("Delete = %v, %v", deleted, err)
	}
	if exists, _, _ := fs.Stat("src/a.ts"); exists {
		t.Fatal("the directory still has content after a recursive delete")
	}
	deleted, err = fs.Delete("src")
	if err != nil || deleted {
		t.Fatalf("deleting a missing path = %v, %v; want false, nil", deleted, err)
	}
}

// ---------------------------------------------------------------------------
// Presigned transfers
// ---------------------------------------------------------------------------

func TestFetchChecksTheDigestAndTheCap(t *testing.T) {
	payload := bytes.Repeat([]byte("v"), 2048)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/missing" {
			w.WriteHeader(http.StatusNotFound)
			return
		}
		_, _ = w.Write(payload)
	}))
	defer server.Close()
	fs, err := OpenRootFS(t.TempDir(), 0)
	if err != nil {
		t.Fatalf("OpenRootFS: %v", err)
	}
	defer func() { _ = fs.Close() }() // best-effort teardown

	// A matching digest is accepted and returned.
	digest := SHA256Bytes(payload)
	got, opErr := FetchToFS(context.Background(), server.Client(), fs, server.URL+"/ok", "good.bin", 4096, digest)
	if opErr != nil {
		t.Fatalf("staging a well-formed object failed: %+v", opErr)
	}
	if got.Bytes != int64(len(payload)) || got.SHA256 != digest {
		t.Fatalf("transfer = %+v, want %d bytes and %s", got, len(payload), digest)
	}

	// A mismatched digest is refused and the file is not left behind.
	wrong := strings.Repeat("0", 64)
	if _, opErr := FetchToFS(context.Background(), server.Client(), fs, server.URL+"/ok", "bad.bin", 4096, wrong); opErr == nil {
		t.Fatal("an object whose digest did not match was kept")
	}
	if exists, _, _ := fs.Stat("bad.bin"); exists {
		t.Fatal("the mismatched object was left in the session directory")
	}

	// The cap is enforced while streaming.
	if _, opErr := FetchToFS(context.Background(), server.Client(), fs, server.URL+"/ok", "big.bin", 128, ""); opErr == nil || opErr.Code != protocol.ErrCodeFileTooLarge {
		t.Fatalf("a transfer past maxBytes = %+v, want file_too_large", opErr)
	}
	if exists, _, _ := fs.Stat("big.bin"); exists {
		t.Fatal("a partial download was left behind")
	}

	// A scheme that is not http(s) is refused before anything is dialled.
	if _, opErr := FetchToFS(context.Background(), server.Client(), fs, "file:///etc/passwd", "x.bin", 0, ""); opErr == nil || opErr.Code != protocol.ErrCodeStageFailed {
		t.Fatalf("a file: URL = %+v, want stage_failed", opErr)
	}
	if _, opErr := FetchToFS(context.Background(), server.Client(), fs, server.URL+"/missing", "x.bin", 0, ""); opErr == nil {
		t.Fatal("a 404 was not reported")
	}
}

func TestPublishRefusesAPathOutsideAndCapsTheSize(t *testing.T) {
	var got []byte
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		got, _ = io.ReadAll(r.Body)
		w.WriteHeader(http.StatusOK)
	}))
	defer server.Close()
	fs, err := OpenRootFS(t.TempDir(), 0)
	if err != nil {
		t.Fatalf("OpenRootFS: %v", err)
	}
	defer func() { _ = fs.Close() }() // best-effort teardown
	body := []byte("rendered bytes")
	if _, err := fs.WriteFile("out/final.mp4", body); err != nil {
		t.Fatalf("seed: %v", err)
	}

	transfer, opErr := PublishFromFS(context.Background(), server.Client(), fs, server.URL+"/put", "out/final.mp4", 1024, "video/mp4")
	if opErr != nil {
		t.Fatalf("publish: %+v", opErr)
	}
	if !bytes.Equal(got, body) {
		t.Fatalf("the object store received %q", got)
	}
	if transfer.SHA256 != SHA256Bytes(body) {
		t.Fatalf("the published digest = %s", transfer.SHA256)
	}

	if _, opErr := PublishFromFS(context.Background(), server.Client(), fs, server.URL+"/put", "out/final.mp4", 4, "video/mp4"); opErr == nil || opErr.Code != protocol.ErrCodeOutputTooLarge {
		t.Fatalf("publishing past maxBytes = %+v, want output_too_large", opErr)
	}
	if _, opErr := PublishFromFS(context.Background(), server.Client(), fs, server.URL+"/put", "../etc/passwd", 0, ""); opErr == nil || opErr.Code != protocol.ErrCodeInvalidPath {
		t.Fatalf("publishing a path outside the session = %+v, want invalid_path", opErr)
	}
	if _, opErr := PublishFromFS(context.Background(), server.Client(), fs, "gopher://x/y", "out/final.mp4", 0, ""); opErr == nil || opErr.Code != protocol.ErrCodePublishFailed {
		t.Fatalf("publishing to a non-http URL = %+v, want publish_failed", opErr)
	}
}

// ---------------------------------------------------------------------------
// Capability probe and backoff
// ---------------------------------------------------------------------------

func TestProbeReportsWhatItHas(t *testing.T) {
	run := func(ctx context.Context, name string, args ...string) ([]byte, error) {
		switch {
		case strings.HasSuffix(name, "ffmpeg"):
			return []byte("ffmpeg version 7.1 Copyright (c) 2000-2025"), nil
		case name == "docker" && args[0] == "version":
			return []byte("Docker version 27.0.3, build abc"), nil
		case name == "docker" && args[0] == "info":
			return []byte("[name=seccomp,profile=builtin name=rootless]"), nil
		case name == "podman":
			return nil, errors.New("not found")
		}
		return nil, errors.New("unexpected probe")
	}
	caps := Probe(context.Background(), ProbeOptions{
		AgentVersion: "1.2.3", Slots: 4, FontsetVersion: "1",
		RuntimeImageDigest: "sha256:deadbeef", Run: run,
	})
	if caps.FfmpegVersion != "7.1" || caps.Container != protocol.ContainerDocker || caps.ContainerMode != "rootless" {
		t.Fatalf("probe = %+v", caps)
	}
	if !caps.Supports(protocol.JobRemotionWorkspace) {
		t.Fatal("a runner with a runtime and a pinned image must advertise the Remotion job type")
	}
	if err := caps.Validate(); err != nil {
		t.Fatalf("the probed document is not valid: %v", err)
	}
}

// No ffmpeg means no job types at all: advertising work the machine cannot do
// only fails its own jobs.
func TestProbeWithoutFFmpegAdvertisesNothing(t *testing.T) {
	caps := Probe(context.Background(), ProbeOptions{
		AgentVersion: "1.2.3",
		Run: func(ctx context.Context, name string, args ...string) ([]byte, error) {
			return nil, errors.New("not found")
		},
	})
	if caps.FfmpegVersion != "" || len(caps.JobTypes) != 0 {
		t.Fatalf("probe = %+v, want no job types", caps)
	}
	if caps.RuntimeImageDigest != nil {
		t.Fatal("a runner with no container runtime must report a null runtimeImageDigest")
	}
	if err := caps.Validate(); err != nil {
		t.Fatalf("the probed document is not valid: %v", err)
	}
}

// A container runtime with no pinned image still does media work, but not the
// Remotion session the runtime image is required for.
func TestProbeWithRuntimeButNoImage(t *testing.T) {
	caps := Probe(context.Background(), ProbeOptions{
		AgentVersion: "1.2.3",
		Run: func(ctx context.Context, name string, args ...string) ([]byte, error) {
			if strings.HasSuffix(name, "ffmpeg") {
				return []byte("ffmpeg version 7.1"), nil
			}
			if name == "docker" {
				return []byte("Docker version 27.0.3"), nil
			}
			return nil, errors.New("nope")
		},
	})
	if caps.Container != protocol.ContainerDocker {
		t.Fatalf("container = %q", caps.Container)
	}
	if caps.Supports(protocol.JobRemotionWorkspace) {
		t.Fatal("the Remotion job type was advertised without a pinned image")
	}
	if !caps.Supports(protocol.JobVideoCompile) {
		t.Fatal("the media job types were not advertised")
	}
}

func TestBackoffGrowsAndStaysJittered(t *testing.T) {
	b := Backoff{Base: time.Second, Max: 8 * time.Second, ResetAfter: time.Minute}
	// A deterministic entropy source: every draw returns 0x7f.., half the range.
	r := bytes.NewReader(bytes.Repeat([]byte{0x7f}, 64))
	floor := map[int]time.Duration{0: 500 * time.Millisecond, 1: time.Second, 2: 2 * time.Second}
	ceiling := map[int]time.Duration{0: time.Second, 1: 2 * time.Second, 2: 4 * time.Second}
	for attempt, lo := range floor {
		d := b.Delay(attempt, r)
		if d < lo || d > ceiling[attempt] {
			t.Errorf("attempt %d delay = %s, want within [%s, %s]", attempt, d, lo, ceiling[attempt])
		}
	}
	// The ceiling is capped.
	if d := b.Delay(20, r); d > 8*time.Second {
		t.Errorf("a late attempt waited %s, past the cap", d)
	}
}

func TestFetchAbandonsADownloadThatStopsFlowing(t *testing.T) {
	old := StallTimeout
	StallTimeout = 200 * time.Millisecond
	defer func() { StallTimeout = old }()
	release := make(chan struct{})
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Length", "1000")
		_, _ = w.Write([]byte("abc"))
		w.(http.Flusher).Flush()
		<-release
	}))
	defer server.Close()
	defer close(release)
	fs, err := OpenRootFS(t.TempDir(), 0)
	if err != nil {
		t.Fatal(err)
	}
	start := time.Now()
	if _, opErr := FetchToFS(context.Background(), server.Client(), fs, server.URL+"/x", "stall.bin", 4096, ""); opErr == nil {
		t.Fatal("a stalled download must fail")
	}
	if exists, _, _ := fs.Stat("stall.bin"); exists {
		t.Fatal("the partial file must be removed")
	}
	if time.Since(start) > 5*time.Second {
		t.Fatalf("took %s", time.Since(start))
	}
}

type noCache struct{}

func (noCache) Acquire(string) SourceEntry          { return nil }
func (noCache) Create(string) (SourceWriter, error) { return nil, errors.New("no cache") }
func (noCache) RecordHit(int64)                     {}
func (noCache) RecordMiss()                         {}

func TestCachedFetchAbandonsADownloadThatStopsFlowing(t *testing.T) {
	old := StallTimeout
	StallTimeout = 200 * time.Millisecond
	defer func() { StallTimeout = old }()
	release := make(chan struct{})
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Length", "1000")
		w.Header().Set("ETag", "\"x\"")
		_, _ = w.Write([]byte("abc"))
		w.(http.Flusher).Flush()
		<-release
	}))
	defer server.Close()
	defer close(release)
	fs, err := OpenRootFS(t.TempDir(), 0)
	if err != nil {
		t.Fatal(err)
	}
	start := time.Now()
	_, opErr := FetchToFSCached(context.Background(), server.Client(), noCache{}, fs, server.URL+"/x?sig=secret", "stall.bin", 4096, "")
	if opErr == nil || time.Since(start) > 5*time.Second {
		t.Fatalf("a stalled cached download must fail fast: %v after %s", opErr, time.Since(start))
	}
	if strings.Contains(opErr.Message, "secret") {
		t.Fatal("the presigned query leaked")
	}
}

func TestDownloadErrorsDoNotCarryThePresignedQuery(t *testing.T) {
	fs, err := OpenRootFS(t.TempDir(), 0)
	if err != nil {
		t.Fatal(err)
	}
	_, opErr := FetchToFS(context.Background(), http.DefaultClient, fs, "http://127.0.0.1:1/x?X-Amz-Signature=secret", "a.bin", 10, "")
	if opErr == nil || strings.Contains(opErr.Message, "secret") {
		t.Fatalf("got %+v", opErr)
	}
}

// A failed upload must not carry the presigned URL's query (the signature) into the op error.
func TestPublishFailureDoesNotLeakThePresignedQuery(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {}))
	url := server.URL + "/put?X-Amz-Signature=sekret123"
	server.Close() // connection refused: client.Do returns a *url.Error carrying the full URL
	fs, err := OpenRootFS(t.TempDir(), 0)
	if err != nil {
		t.Fatalf("OpenRootFS: %v", err)
	}
	defer func() { _ = fs.Close() }() // best-effort teardown
	if _, err := fs.WriteFile("out/final.mp4", []byte("x")); err != nil {
		t.Fatalf("seed: %v", err)
	}
	_, opErr := PublishFromFS(context.Background(), http.DefaultClient, fs, url, "out/final.mp4", 1024, "video/mp4")
	if opErr == nil {
		t.Fatal("want a publish failure")
	}
	if strings.Contains(opErr.Message, "sekret123") || strings.Contains(opErr.Message, "X-Amz-Signature") {
		t.Fatalf("op error leaks the presigned query: %q", opErr.Message)
	}
}
