package container

import (
	"archive/tar"
	"bytes"
	"compress/gzip"
	"context"
	"crypto/rand"
	"crypto/sha256"
	"errors"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"testing"
	"time"
)

const secondExecutionID = "22222222-2222-2222-2222-222222222222"

// archiveDriver builds a driver with two registered sandboxes whose
// workspaces are real directories, and a runner that records every CLI call
// (so a test can assert no container was started, stopped or removed).
type archiveDriver struct {
	d     *Driver
	src   *Sandbox
	dst   *Sandbox
	mu    sync.Mutex
	calls []string
}

func (a *archiveDriver) runCalls() []string {
	a.mu.Lock()
	defer a.mu.Unlock()
	return append([]string(nil), a.calls...)
}

func newArchiveDriver(t *testing.T) *archiveDriver {
	t.Helper()
	a := &archiveDriver{}
	root := t.TempDir()
	a.d = NewWithRunner(Config{SandboxRoot: root, SandboxTTL: time.Hour, TemplateDir: filepath.Join(root, "no-template")}, "docker",
		func(ctx context.Context, args ...string) ([]byte, error) {
			a.mu.Lock()
			a.calls = append(a.calls, strings.Join(args, " "))
			a.mu.Unlock()
			return nil, nil
		})
	register := func(id, execution string) *Sandbox {
		ws := filepath.Join(root, id)
		if err := os.MkdirAll(ws, 0o770); err != nil {
			t.Fatalf("mkdir workspace: %v", err)
		}
		sb := &Sandbox{ID: id, WorkflowExecutionID: execution, ContainerName: "rf-sbx-" + id, WorkspacePath: ws, LastActivity: time.Now().UTC()}
		a.d.sandboxes[id] = sb
		a.d.executionIndex[execution] = id
		return sb
	}
	a.src = register("src", testExecutionID)
	a.dst = register("dst", secondExecutionID)
	return a
}

func writeTree(t *testing.T, base string, files map[string]string) {
	t.Helper()
	for rel, content := range files {
		p := filepath.Join(base, filepath.FromSlash(rel))
		if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(p, []byte(content), 0o644); err != nil {
			t.Fatal(err)
		}
	}
}

// snapshotTree maps every path under base to "dir" or "file:<content>" or
// "link", leaving out node_modules.
func snapshotTree(t *testing.T, base string) map[string]string {
	t.Helper()
	out := map[string]string{}
	err := filepath.WalkDir(base, func(p string, de fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		rel, _ := filepath.Rel(base, p)
		if rel == "." {
			return nil
		}
		rel = filepath.ToSlash(rel)
		if de.Name() == "node_modules" {
			return filepath.SkipDir
		}
		switch {
		case de.Type()&fs.ModeSymlink != 0:
			out[rel] = "link"
		case de.IsDir():
			out[rel] = "dir"
		default:
			b, err := os.ReadFile(p)
			if err != nil {
				return err
			}
			out[rel] = "file:" + string(b)
		}
		return nil
	})
	if err != nil {
		t.Fatalf("walk: %v", err)
	}
	return out
}

func exportBytes(t *testing.T, d *Driver, execution string, limits ArchiveLimits) ([]byte, ArchiveStats) {
	t.Helper()
	var buf bytes.Buffer
	stats, err := d.ExportWorkspace(execution, limits, func(ArchiveStats) (io.Writer, error) { return &buf, nil })
	if err != nil {
		t.Fatalf("ExportWorkspace: %v", err)
	}
	return buf.Bytes(), stats
}

type tarEntry struct {
	hdr  tar.Header
	body []byte
}

func buildArchive(t *testing.T, entries ...tarEntry) []byte {
	t.Helper()
	var buf bytes.Buffer
	zw := gzip.NewWriter(&buf)
	tw := tar.NewWriter(zw)
	for _, e := range entries {
		hdr := e.hdr
		if hdr.Typeflag == tar.TypeReg && hdr.Size == 0 {
			hdr.Size = int64(len(e.body))
		}
		if hdr.Mode == 0 {
			hdr.Mode = 0o644
		}
		if err := tw.WriteHeader(&hdr); err != nil {
			t.Fatalf("WriteHeader %q: %v", hdr.Name, err)
		}
		if len(e.body) > 0 {
			if _, err := tw.Write(e.body); err != nil {
				t.Fatalf("Write %q: %v", hdr.Name, err)
			}
		}
	}
	if err := tw.Close(); err != nil {
		t.Fatal(err)
	}
	if err := zw.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

func file(name, body string) tarEntry {
	return tarEntry{hdr: tar.Header{Typeflag: tar.TypeReg, Name: name}, body: []byte(body)}
}

func TestWorkspaceArchiveRoundTrip(t *testing.T) {
	a := newArchiveDriver(t)
	binary := make([]byte, 4096)
	for i := range binary {
		binary[i] = byte(i % 256)
	}
	writeTree(t, a.src.WorkspacePath, map[string]string{
		"package.json":             `{"name":"promo"}`,
		"src/index.ts":             "registerRoot(Root)",
		"src/nested/deep/a.tsx":    "export const A = 1",
		"src/ünïcødé ✓.tsx":        "unicode",
		"src/build/keep.ts":        "nested build dirs are source",
		"assets/bin.dat":           string(binary),
		"scripts/run.sh":           "#!/bin/sh\necho hi\n",
		"node_modules/x/index.js":  "excluded",
		"src/node_modules/y.js":    "excluded at any depth",
		"out/video.mp4":            "excluded",
		"build/bundle.js":          "excluded",
		".cache/c":                 "excluded",
		".remotion/r":              "excluded",
		".git/HEAD":                "excluded",
		"tmp/t":                    "excluded",
		"dist/d":                   "excluded",
		".npm/n":                   "excluded",
		"src/components/Hero.tsx":  "hero",
		"src/components/Hero2.tsx": "hero2",
	})
	if err := os.Chmod(filepath.Join(a.src.WorkspacePath, "scripts/run.sh"), 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(filepath.Join(a.src.WorkspacePath, "empty"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink("/", filepath.Join(a.src.WorkspacePath, "esc")); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink("src/index.ts", filepath.Join(a.src.WorkspacePath, "inlink")); err != nil {
		t.Fatal(err)
	}

	// The target already holds the template's node_modules and a stale file.
	writeTree(t, a.dst.WorkspacePath, map[string]string{
		"node_modules/remotion/index.js": "template dependency",
		"src/old.tsx":                    "stale",
		"package.json":                   `{"name":"template"}`,
	})

	archive, stats := exportBytes(t, a.d, testExecutionID, ArchiveLimits{})
	if stats.Skipped != 2 {
		t.Errorf("Skipped = %d, want 2 (both symlinks)", stats.Skipped)
	}

	result, err := a.d.ImportWorkspace(secondExecutionID, bytes.NewReader(archive), ArchiveLimits{}, ImportOptions{Replace: true})
	if err != nil {
		t.Fatalf("ImportWorkspace: %v", err)
	}

	want := snapshotTree(t, a.src.WorkspacePath)
	for rel := range want {
		top := strings.SplitN(rel, "/", 2)[0]
		if rel == "esc" || rel == "inlink" || IsExcludedWorkspacePath(rel) || (top != rel && IsExcludedWorkspacePath(top)) {
			delete(want, rel)
		}
	}
	got := snapshotTree(t, a.dst.WorkspacePath)
	if len(got) != len(want) {
		t.Errorf("restored %d paths, want %d\ngot:  %v\nwant: %v", len(got), len(want), keys(got), keys(want))
	}
	for rel, w := range want {
		if got[rel] != w {
			t.Errorf("%s: got %.40q, want %.40q", rel, got[rel], w)
		}
	}
	if _, ok := got["src/old.tsx"]; ok {
		t.Error("replace kept the stale src/old.tsx")
	}
	if b, err := os.ReadFile(filepath.Join(a.dst.WorkspacePath, "node_modules/remotion/index.js")); err != nil || string(b) != "template dependency" {
		t.Errorf("template node_modules did not survive the replace: %q %v", b, err)
	}
	info, err := os.Stat(filepath.Join(a.dst.WorkspacePath, "scripts/run.sh"))
	if err != nil || info.Mode().Perm() != 0o755 {
		t.Errorf("executable bit not restored: %v %v", info, err)
	}
	if result.Files != stats.Entries-result.Directories || result.Bytes != stats.Bytes {
		t.Errorf("result %+v does not match export stats %+v", result, stats)
	}
	if result.ExtraDependencies == nil {
		t.Error("ExtraDependencies must never be nil")
	}
}

func keys(m map[string]string) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

func TestWorkspaceExportIsDeterministic(t *testing.T) {
	a := newArchiveDriver(t)
	writeTree(t, a.src.WorkspacePath, map[string]string{
		"package.json":   "{}",
		"src/b.tsx":      "b",
		"src/a.tsx":      "a",
		"src/a-b/c.tsx":  "c",
		"z/deep/file.ts": "z",
	})
	first, _ := exportBytes(t, a.d, testExecutionID, ArchiveLimits{})
	second, _ := exportBytes(t, a.d, testExecutionID, ArchiveLimits{})
	if sha256.Sum256(first) != sha256.Sum256(second) {
		t.Fatal("two exports of an unchanged workspace differ")
	}

	later := time.Now().Add(3 * time.Hour)
	err := filepath.WalkDir(a.src.WorkspacePath, func(p string, de fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		return os.Chtimes(p, later, later)
	})
	if err != nil {
		t.Fatal(err)
	}
	third, _ := exportBytes(t, a.d, testExecutionID, ArchiveLimits{})
	if sha256.Sum256(first) != sha256.Sum256(third) {
		t.Fatal("touching mtimes changed the archive")
	}

	// Entries come out sorted, directories before their children, with fixed metadata.
	zr, err := gzip.NewReader(bytes.NewReader(first))
	if err != nil {
		t.Fatal(err)
	}
	if !zr.ModTime.IsZero() || zr.Name != "" {
		t.Errorf("gzip header carries metadata: %v %q", zr.ModTime, zr.Name)
	}
	tr := tar.NewReader(zr)
	var names []string
	for {
		hdr, err := tr.Next()
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			t.Fatal(err)
		}
		if !hdr.ModTime.Equal(time.Unix(0, 0)) || hdr.Uid != 0 || hdr.Gid != 0 || hdr.Uname != "" || hdr.Gname != "" {
			t.Errorf("%s: non-fixed metadata %+v", hdr.Name, hdr)
		}
		names = append(names, hdr.Name)
	}
	wantNames := []string{"package.json", "src/", "src/a-b/", "src/a-b/c.tsx", "src/a.tsx", "src/b.tsx", "z/", "z/deep/", "z/deep/file.ts"}
	if strings.Join(names, ",") != strings.Join(wantNames, ",") {
		t.Errorf("order = %v, want %v", names, wantNames)
	}
}

// listing returns the names directly under dir.
func listing(t *testing.T, dir string) []string {
	t.Helper()
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	out := make([]string, 0, len(entries))
	for _, e := range entries {
		out = append(out, e.Name())
	}
	return out
}

func TestWorkspaceImportRejectsMaliciousEntries(t *testing.T) {
	cases := map[string]tarEntry{
		"parent":       file("../evil", "x"),
		"absolute":     file("/abs/path", "x"),
		"nested-up":    file("a/../../evil", "x"),
		"dot":          {hdr: tar.Header{Typeflag: tar.TypeDir, Name: "./"}},
		"symlink":      {hdr: tar.Header{Typeflag: tar.TypeSymlink, Name: "esc", Linkname: "/"}},
		"hardlink":     {hdr: tar.Header{Typeflag: tar.TypeLink, Name: "hl", Linkname: "/etc/passwd"}},
		"char-device":  {hdr: tar.Header{Typeflag: tar.TypeChar, Name: "null", Devmajor: 1, Devminor: 3}},
		"block-device": {hdr: tar.Header{Typeflag: tar.TypeBlock, Name: "sda", Devmajor: 8}},
		"fifo":         {hdr: tar.Header{Typeflag: tar.TypeFifo, Name: "pipe"}},
		"node_modules": file("node_modules/pkg/index.js", "x"),
		"nested-nm":    file("src/node_modules/pkg/index.js", "x"),
		"excluded-top": file("out/video.mp4", "x"),
		"backslash":    file(`src\..\..\evil`, "x"),
	}
	for name, entry := range cases {
		t.Run(name, func(t *testing.T) {
			a := newArchiveDriver(t)
			sandboxRoot := filepath.Dir(a.dst.WorkspacePath)
			before := listing(t, sandboxRoot)
			archive := buildArchive(t, file("src/ok.tsx", "fine"), entry)
			_, err := a.d.ImportWorkspace(secondExecutionID, bytes.NewReader(archive), ArchiveLimits{}, ImportOptions{Replace: true})
			if !errors.Is(err, ErrInvalidArchive) {
				t.Fatalf("want ErrInvalidArchive, got %v", err)
			}
			if after := listing(t, sandboxRoot); strings.Join(after, ",") != strings.Join(before, ",") {
				t.Errorf("sandbox root changed: %v -> %v", before, after)
			}
			for _, p := range []string{"evil", "abs", "esc", "hl", "null", "sda", "pipe", "node_modules", "out"} {
				if _, err := os.Lstat(filepath.Join(a.dst.WorkspacePath, p)); err == nil {
					t.Errorf("%s was written", p)
				}
			}
		})
	}
}

func TestWorkspaceImportRejectsNonArchives(t *testing.T) {
	a := newArchiveDriver(t)
	for name, body := range map[string][]byte{
		"plain text":   []byte("definitely not gzip"),
		"gzip not tar": gzipBytes(t, []byte("hello, this is not a tar stream at all, just text padded out to be long enough")),
		"empty":        nil,
	} {
		_, err := a.d.ImportWorkspace(secondExecutionID, bytes.NewReader(body), ArchiveLimits{}, ImportOptions{})
		if !errors.Is(err, ErrInvalidArchive) {
			t.Errorf("%s: want ErrInvalidArchive, got %v", name, err)
		}
	}
}

func gzipBytes(t *testing.T, data []byte) []byte {
	t.Helper()
	var buf bytes.Buffer
	zw := gzip.NewWriter(&buf)
	if _, err := zw.Write(data); err != nil {
		t.Fatal(err)
	}
	if err := zw.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

func TestWorkspaceImportCaps(t *testing.T) {
	random := make([]byte, 64<<10)
	if _, err := rand.Read(random); err != nil {
		t.Fatal(err)
	}
	bomb := buildArchive(t, tarEntry{hdr: tar.Header{Typeflag: tar.TypeReg, Name: "zeros.bin"}, body: make([]byte, 16<<20)})
	if len(bomb) > 1<<20 {
		t.Fatalf("bomb fixture is not a bomb: %d compressed bytes", len(bomb))
	}
	cases := []struct {
		name    string
		archive []byte
		limits  ArchiveLimits
	}{
		{"entries", buildArchive(t, file("a", "1"), file("b", "2"), file("c", "3")), ArchiveLimits{MaxEntries: 2}},
		{"file size", buildArchive(t, file("big", strings.Repeat("x", 100))), ArchiveLimits{MaxFileBytes: 99}},
		{"gzip bomb", bomb, ArchiveLimits{MaxTotalBytes: 1 << 20}},
		{"total across files", buildArchive(t, file("a", strings.Repeat("x", 60)), file("b", strings.Repeat("y", 60))), ArchiveLimits{MaxTotalBytes: 100}},
		{"compressed input", buildArchive(t, tarEntry{hdr: tar.Header{Typeflag: tar.TypeReg, Name: "random.bin"}, body: random}), ArchiveLimits{MaxArchiveBytes: 4096}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			a := newArchiveDriver(t)
			_, err := a.d.ImportWorkspace(secondExecutionID, bytes.NewReader(tc.archive), tc.limits, ImportOptions{Replace: true})
			if !errors.Is(err, ErrArchiveTooLarge) {
				t.Fatalf("want ErrArchiveTooLarge, got %v", err)
			}
		})
	}
	// The gzip bomb never got further than the cap on disk.
	a := newArchiveDriver(t)
	_, _ = a.d.ImportWorkspace(secondExecutionID, bytes.NewReader(bomb), ArchiveLimits{MaxTotalBytes: 1 << 20}, ImportOptions{})
	if info, err := os.Stat(filepath.Join(a.dst.WorkspacePath, "zeros.bin")); err == nil && info.Size() > 1<<20+1 {
		t.Errorf("bomb wrote %d bytes past the cap", info.Size())
	}
}

func TestWorkspaceExportRefusesOversizedWorkspaceBeforeWriting(t *testing.T) {
	cases := []struct {
		name   string
		files  map[string]string
		limits ArchiveLimits
	}{
		{"entries", map[string]string{"a": "1", "b": "2", "c": "3"}, ArchiveLimits{MaxEntries: 2}},
		{"file size", map[string]string{"big": strings.Repeat("x", 11)}, ArchiveLimits{MaxFileBytes: 10}},
		{"total", map[string]string{"a": strings.Repeat("x", 6), "b": strings.Repeat("y", 6)}, ArchiveLimits{MaxTotalBytes: 10}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			a := newArchiveDriver(t)
			writeTree(t, a.src.WorkspacePath, tc.files)
			began := false
			_, err := a.d.ExportWorkspace(testExecutionID, tc.limits, func(ArchiveStats) (io.Writer, error) {
				began = true
				return io.Discard, nil
			})
			if !errors.Is(err, ErrArchiveTooLarge) {
				t.Fatalf("want ErrArchiveTooLarge, got %v", err)
			}
			if began {
				t.Error("begin was called for a workspace over the cap")
			}
		})
	}
}

func TestWorkspaceExportUnknownOrInvalidExecution(t *testing.T) {
	a := newArchiveDriver(t)
	begin := func(ArchiveStats) (io.Writer, error) { return io.Discard, nil }
	if _, err := a.d.ExportWorkspace("33333333-3333-3333-3333-333333333333", ArchiveLimits{}, begin); !errors.Is(err, ErrNotFound) {
		t.Errorf("unknown execution: %v", err)
	}
	if _, err := a.d.ExportWorkspace("not-a-uuid", ArchiveLimits{}, begin); !errors.Is(err, ErrBadExecution) {
		t.Errorf("invalid id: %v", err)
	}
	if _, err := a.d.ImportWorkspace("not-a-uuid", bytes.NewReader(nil), ArchiveLimits{}, ImportOptions{}); !errors.Is(err, ErrBadExecution) {
		t.Errorf("invalid id on import: %v", err)
	}
}

// A link the sandbox planted at a directory path of the target workspace must
// not redirect the extraction outside it.
func TestWorkspaceImportDoesNotFollowPlantedSymlink(t *testing.T) {
	a := newArchiveDriver(t)
	outside := t.TempDir()
	if err := os.Symlink(outside, filepath.Join(a.dst.WorkspacePath, "src")); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(filepath.Join(outside, "target.txt"), filepath.Join(a.dst.WorkspacePath, "file-link")); err != nil {
		t.Fatal(err)
	}
	archive := buildArchive(t,
		tarEntry{hdr: tar.Header{Typeflag: tar.TypeDir, Name: "src/", Mode: 0o755}},
		file("src/x.tsx", "planted?"),
		file("file-link", "planted?"),
	)
	_, err := a.d.ImportWorkspace(secondExecutionID, bytes.NewReader(archive), ArchiveLimits{}, ImportOptions{Replace: false})
	if err != nil {
		t.Fatalf("ImportWorkspace: %v", err)
	}
	if entries := listing(t, outside); len(entries) != 0 {
		t.Fatalf("import wrote outside the workspace: %v", entries)
	}
	info, err := os.Lstat(filepath.Join(a.dst.WorkspacePath, "src"))
	if err != nil || !info.IsDir() {
		t.Errorf("src should now be a real directory: %v %v", info, err)
	}
	if b, err := os.ReadFile(filepath.Join(a.dst.WorkspacePath, "src/x.tsx")); err != nil || string(b) != "planted?" {
		t.Errorf("src/x.tsx = %q %v", b, err)
	}
	info, err = os.Lstat(filepath.Join(a.dst.WorkspacePath, "file-link"))
	if err != nil || !info.Mode().IsRegular() {
		t.Errorf("file-link should now be a regular file: %v %v", info, err)
	}
}

func TestWorkspaceImportDependencyComparison(t *testing.T) {
	template := `{"dependencies":{"remotion":"4.0.0","react":"18.3.1"},"devDependencies":{"typescript":"5.6.0"}}`
	cases := []struct {
		name         string
		manifest     string
		noTemplate   bool
		wantCompared bool
		wantMatch    bool
		wantExtra    []string
	}{
		{"equal", template, false, true, true, []string{}},
		{"dev dep moved to deps still matches", `{"dependencies":{"remotion":"4.0.0","react":"18.3.1","typescript":"5.6.0"}}`, false, true, true, []string{}},
		{"extra dependency", `{"dependencies":{"remotion":"4.0.0","three":"0.170.0"},"devDependencies":{"@types/three":"0.170.0"}}`, false, true, false, []string{"@types/three@0.170.0", "three@0.170.0"}},
		{"changed spec", `{"dependencies":{"remotion":"4.0.1"}}`, false, true, false, []string{"remotion@4.0.1"}},
		{"missing template", template, true, false, false, []string{}},
		{"unparseable manifest", `{not json`, false, false, false, []string{}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			a := newArchiveDriver(t)
			if !tc.noTemplate {
				dir := t.TempDir()
				writeTree(t, dir, map[string]string{"package.json": template})
				a.d.cfg.TemplateDir = dir
			}
			archive := buildArchive(t, file("package.json", tc.manifest))
			result, err := a.d.ImportWorkspace(secondExecutionID, bytes.NewReader(archive), ArchiveLimits{}, ImportOptions{Replace: true})
			if err != nil {
				t.Fatalf("ImportWorkspace: %v", err)
			}
			if result.TemplateCompared != tc.wantCompared || result.DependenciesMatchTemplate != tc.wantMatch {
				t.Errorf("compared=%v match=%v, want %v %v", result.TemplateCompared, result.DependenciesMatchTemplate, tc.wantCompared, tc.wantMatch)
			}
			if result.ExtraDependencies == nil || strings.Join(result.ExtraDependencies, ",") != strings.Join(tc.wantExtra, ",") {
				t.Errorf("extra = %#v, want %#v", result.ExtraDependencies, tc.wantExtra)
			}
		})
	}
}

// An export holds the sandbox in flight, so the janitor can neither stop nor
// delete it mid-transfer, and it never starts a stopped container.
func TestWorkspaceExportPinsTheSandbox(t *testing.T) {
	a := newArchiveDriver(t)
	writeTree(t, a.src.WorkspacePath, map[string]string{"package.json": "{}"})
	a.d.mu.Lock()
	a.src.IsStopped = true
	a.d.mu.Unlock()

	_, err := a.d.ExportWorkspace(testExecutionID, ArchiveLimits{}, func(ArchiveStats) (io.Writer, error) {
		if _, inFlight, _ := sandboxState(a.d, testExecutionID); inFlight != 1 {
			t.Errorf("inFlight during export = %d, want 1", inFlight)
		}
		ageSandbox(a.d, testExecutionID, 2*time.Hour)
		a.d.cleanupInactive()
		if _, _, present := sandboxState(a.d, testExecutionID); !present {
			t.Error("the janitor deleted a sandbox with an export in flight")
		}
		return io.Discard, nil
	})
	if err != nil {
		t.Fatalf("ExportWorkspace: %v", err)
	}
	if stopped, inFlight, present := sandboxState(a.d, testExecutionID); !present || inFlight != 0 || !stopped {
		t.Errorf("after export: stopped=%v inFlight=%d present=%v", stopped, inFlight, present)
	}
	for _, c := range a.runCalls() {
		if strings.HasPrefix(c, "start ") || strings.HasPrefix(c, "rm ") || strings.HasPrefix(c, "stop ") {
			t.Errorf("export ran %q", c)
		}
	}
}

// A sandbox being deleted is gone as far as archives are concerned.
func TestWorkspaceArchiveRefusesADeletingSandbox(t *testing.T) {
	a := newArchiveDriver(t)
	a.d.mu.Lock()
	a.src.deleting = true
	a.d.mu.Unlock()
	_, err := a.d.ExportWorkspace(testExecutionID, ArchiveLimits{}, func(ArchiveStats) (io.Writer, error) { return io.Discard, nil })
	if !errors.Is(err, ErrNotFound) {
		t.Fatalf("want ErrNotFound, got %v", err)
	}
}

func TestIsExcludedWorkspacePath(t *testing.T) {
	for rel, want := range map[string]bool{
		"node_modules":            true,
		"node_modules/x":          true,
		"src/node_modules/x":      true,
		"out":                     true,
		"out/video.mp4":           true,
		".git/HEAD":               true,
		"src/out/x.ts":            false,
		"src/build":               false,
		"src/index.ts":            false,
		"output/x":                false,
		"package.json":            false,
		"node_modules_extra/x.js": false,
	} {
		if got := IsExcludedWorkspacePath(rel); got != want {
			t.Errorf("IsExcludedWorkspacePath(%q) = %v, want %v", rel, got, want)
		}
	}
}
