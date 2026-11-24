package telemetry

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// A log that grows without bound is a disk-full bug on a user's own machine, so
// the cap is a property worth a test rather than a comment.
func TestRotatingLogStaysWithinItsBudget(t *testing.T) {
	dir := t.TempDir()
	w, err := OpenRotating(dir, "runner", 200, 3)
	if err != nil {
		t.Fatalf("OpenRotating: %v", err)
	}
	defer func() { _ = w.Close() }()

	line := strings.Repeat("x", 49) + "\n" // 50 bytes
	for i := 0; i < 20; i++ {
		if _, err := w.Write([]byte(line)); err != nil {
			t.Fatalf("Write %d: %v", i, err)
		}
	}
	files := w.Files()
	if len(files) > 3 {
		t.Errorf("kept %d files, want at most 3: %v", len(files), files)
	}
	if len(files) < 2 {
		t.Fatalf("nothing rotated: %v", files)
	}
	var total int64
	for _, f := range files {
		info, err := os.Stat(f)
		if err != nil {
			t.Fatalf("stat %s: %v", f, err)
		}
		total += info.Size()
		if info.Size() > 200+50 {
			t.Errorf("%s is %d bytes, past the cap", f, info.Size())
		}
	}
	if total > 3*250 {
		t.Errorf("the log costs %d bytes, past 3 files at the cap", total)
	}
	// The live file is the one new lines go to, and it is the last entry.
	if got := files[len(files)-1]; got != w.Path() {
		t.Errorf("Files() ends with %s, want the live file %s", got, w.Path())
	}
	if _, err := os.Stat(w.Path()); err != nil {
		t.Errorf("the live file does not exist: %v", err)
	}
}

// Rotation keeps the END of the history, which is the part a failure is in.
func TestRotationKeepsTheMostRecentLines(t *testing.T) {
	dir := t.TempDir()
	w, err := OpenRotating(dir, "runner", 100, 3)
	if err != nil {
		t.Fatalf("OpenRotating: %v", err)
	}
	defer func() { _ = w.Close() }()
	for i := 0; i < 12; i++ {
		if _, err := w.Write([]byte("line-" + string(rune('a'+i)) + strings.Repeat(".", 30) + "\n")); err != nil {
			t.Fatalf("Write: %v", err)
		}
	}
	var all strings.Builder
	for _, f := range w.Files() {
		raw, err := os.ReadFile(f)
		if err != nil {
			t.Fatalf("read %s: %v", f, err)
		}
		all.Write(raw)
	}
	if !strings.Contains(all.String(), "line-l") {
		t.Errorf("the most recent line was rotated away:\n%s", all.String())
	}
}

// A single enormous write must still respect the ceiling rather than blowing
// past it before the size check runs.
func TestAnEnormousLineIsTruncated(t *testing.T) {
	dir := t.TempDir()
	w, err := OpenRotating(dir, "runner", 1024, 2)
	if err != nil {
		t.Fatalf("OpenRotating: %v", err)
	}
	defer func() { _ = w.Close() }()
	big := strings.Repeat("y", MaxLogEntryBytes*2)
	if _, err := w.Write([]byte(big)); err != nil {
		t.Fatalf("Write: %v", err)
	}
	info, err := os.Stat(w.Path())
	if err != nil {
		t.Fatalf("stat: %v", err)
	}
	if info.Size() > MaxLogEntryBytes {
		t.Errorf("the file is %d bytes, want at most %d", info.Size(), MaxLogEntryBytes)
	}
}

func TestRotatingLogCreatesItsDirectory(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "ReelBolt", "logs")
	w, err := OpenRotating(dir, "runner", 0, 0)
	if err != nil {
		t.Fatalf("OpenRotating: %v", err)
	}
	defer func() { _ = w.Close() }()
	if filepath.Dir(w.Path()) != dir {
		t.Errorf("Path = %s, want it under %s", w.Path(), dir)
	}
	if base := filepath.Base(w.Path()); base != "runner.log" {
		t.Errorf("Path base = %s", base)
	}
}

func TestLogsDirIsUnderTheRunnerCacheDirectory(t *testing.T) {
	dir, err := LogsDir()
	if err != nil {
		t.Fatalf("LogsDir: %v", err)
	}
	if !strings.HasSuffix(filepath.ToSlash(dir), "ReelBolt/logs") {
		t.Errorf("LogsDir = %s, want it under ReelBolt/logs", dir)
	}
}
