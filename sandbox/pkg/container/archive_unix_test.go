//go:build unix

package container

import (
	"bytes"
	"errors"
	"io"
	"os"
	"path/filepath"
	"syscall"
	"testing"
	"time"
)

// within fails the test if fn has not returned after a few seconds: every
// case here is about a FIFO that would block a naive open forever.
func within(t *testing.T, fn func()) {
	t.Helper()
	done := make(chan struct{})
	go func() {
		defer close(done)
		fn()
	}()
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		t.Fatal("blocked on a FIFO planted in the workspace")
	}
}

func mkfifo(t *testing.T, p string) {
	t.Helper()
	if err := syscall.Mkfifo(p, 0o644); err != nil {
		t.Skipf("mkfifo unsupported: %v", err)
	}
}

func TestWorkspaceExportSkipsAFIFO(t *testing.T) {
	a := newArchiveDriver(t)
	writeTree(t, a.src.WorkspacePath, map[string]string{"src/a.tsx": "a"})
	mkfifo(t, filepath.Join(a.src.WorkspacePath, "src/pipe.tsx"))
	within(t, func() {
		_, stats := exportBytes(t, a.d, testExecutionID, ArchiveLimits{})
		if stats.Skipped != 1 || stats.Entries != 2 {
			t.Errorf("stats = %+v, want the FIFO skipped", stats)
		}
	})
}

// A file replaced after the pre-pass (begin runs between the pre-pass and
// the copy) is refused promptly: a FIFO is not opened blocking, and a link to
// another workspace file is not the object the pre-pass measured.
func TestWorkspaceExportRefusesAFileSwappedAfterThePrePass(t *testing.T) {
	swaps := map[string]func(t *testing.T, p string){
		"fifo": func(t *testing.T, p string) { mkfifo(t, p) },
		"symlink to an in-workspace file": func(t *testing.T, p string) {
			if err := os.Symlink("../package.json", p); err != nil {
				t.Fatal(err)
			}
		},
		"symlink out of the workspace": func(t *testing.T, p string) {
			if err := os.Symlink("/etc/hostname", p); err != nil {
				t.Fatal(err)
			}
		},
	}
	for name, swap := range swaps {
		t.Run(name, func(t *testing.T) {
			a := newArchiveDriver(t)
			writeTree(t, a.src.WorkspacePath, map[string]string{"package.json": "{}", "src/a.tsx": "a"})
			target := filepath.Join(a.src.WorkspacePath, "src/a.tsx")
			within(t, func() {
				_, err := a.d.ExportWorkspace(testExecutionID, ArchiveLimits{}, func(ArchiveStats) (io.Writer, error) {
					if err := os.Remove(target); err != nil {
						t.Fatal(err)
					}
					swap(t, target)
					return io.Discard, nil
				})
				if err == nil {
					t.Fatal("export read a file swapped after the pre-pass")
				}
			})
		})
	}
}

// A directory swapped for a FIFO between its Lstat and the walk's open is
// refused, not opened blocking.
func TestRemoveAllInDoesNotBlockOnAFIFO(t *testing.T) {
	_, sb := newTestSandbox(t)
	mkfifo(t, filepath.Join(sb.WorkspacePath, "pipe"))
	root, err := openWorkspace(sb)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = root.Close() }() // best-effort teardown
	within(t, func() {
		if _, _, err := openVerifiedIn(root, "pipe", true, nil); !errors.Is(err, errNotRegular) {
			t.Errorf("FIFO opened as a directory: %v", err)
		}
		if _, _, err := openVerifiedIn(root, "pipe", false, nil); !errors.Is(err, errNotRegular) {
			t.Errorf("FIFO opened as a file: %v", err)
		}
		if err := removeAllIn(root, "pipe"); err != nil {
			t.Errorf("removeAllIn: %v", err)
		}
	})
}

// A FIFO named package.json (in the restored workspace, or in the template)
// must not hang the dependency comparison.
func TestWorkspaceImportDoesNotBlockOnAFIFOManifest(t *testing.T) {
	t.Run("workspace", func(t *testing.T) {
		a := newArchiveDriver(t)
		dir := t.TempDir()
		writeTree(t, dir, map[string]string{"package.json": `{"dependencies":{}}`})
		a.d.cfg.TemplateDir = dir
		mkfifo(t, filepath.Join(a.dst.WorkspacePath, "package.json"))
		within(t, func() {
			archive := buildArchive(t, file("src/x.tsx", "x"))
			result, err := a.d.ImportWorkspace(secondExecutionID, bytes.NewReader(archive), ArchiveLimits{}, ImportOptions{Replace: false})
			if err != nil {
				t.Fatalf("ImportWorkspace: %v", err)
			}
			if result.TemplateCompared {
				t.Error("compared against a FIFO manifest")
			}
		})
	})
	t.Run("template", func(t *testing.T) {
		a := newArchiveDriver(t)
		dir := t.TempDir()
		mkfifo(t, filepath.Join(dir, "package.json"))
		a.d.cfg.TemplateDir = dir
		within(t, func() {
			archive := buildArchive(t, file("package.json", `{"dependencies":{}}`))
			result, err := a.d.ImportWorkspace(secondExecutionID, bytes.NewReader(archive), ArchiveLimits{}, ImportOptions{Replace: true})
			if err != nil {
				t.Fatalf("ImportWorkspace: %v", err)
			}
			if result.TemplateCompared {
				t.Error("compared against a FIFO template manifest")
			}
		})
	})
}

// A FIFO already at a path the archive writes is replaced by the file, not
// opened for writing (which would block until a reader appeared).
func TestWorkspaceImportReplacesAFIFOAtAFilePath(t *testing.T) {
	a := newArchiveDriver(t)
	if err := os.MkdirAll(filepath.Join(a.dst.WorkspacePath, "src"), 0o755); err != nil {
		t.Fatal(err)
	}
	mkfifo(t, filepath.Join(a.dst.WorkspacePath, "src/x.tsx"))
	within(t, func() {
		archive := buildArchive(t, file("src/x.tsx", "restored"))
		if _, err := a.d.ImportWorkspace(secondExecutionID, bytes.NewReader(archive), ArchiveLimits{}, ImportOptions{Replace: false}); err != nil {
			t.Fatalf("ImportWorkspace: %v", err)
		}
	})
	if b, err := os.ReadFile(filepath.Join(a.dst.WorkspacePath, "src/x.tsx")); err != nil || string(b) != "restored" {
		t.Errorf("src/x.tsx = %q %v", b, err)
	}
}
