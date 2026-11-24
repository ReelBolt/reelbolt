package telemetry

import (
	"archive/zip"
	"encoding/json"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// readZip returns the bundle's entries by name.
func readZip(t *testing.T, path string) map[string]string {
	t.Helper()
	r, err := zip.OpenReader(path)
	if err != nil {
		t.Fatalf("open %s: %v", path, err)
	}
	defer func() { _ = r.Close() }()
	out := map[string]string{}
	for _, f := range r.File {
		rc, err := f.Open()
		if err != nil {
			t.Fatalf("open entry %s: %v", f.Name, err)
		}
		body, err := io.ReadAll(rc)
		_ = rc.Close()
		if err != nil {
			t.Fatalf("read entry %s: %v", f.Name, err)
		}
		out[f.Name] = string(body)
	}
	return out
}

// The export is the second thing this package writes that a user might send
// somewhere, so its free text is redacted too - with the same scrubber the
// acceptance criteria name.
func TestDiagnosticsBundleIsWrittenAndScrubbed(t *testing.T) {
	dir := t.TempDir()
	home := t.TempDir()
	logPath := filepath.Join(dir, "runner.log")
	const presigned = "https://storage.reelbolt.ai/projects/0d2f8a11/outputFiles/e/render.mp4" +
		"?X-Amz-Credential=AKIA" + "IOSFODNN7EXAMPLE&X-Amz-Signature=9f8c1d2e3b4a5c6d7e8f9012345678"
	logBody := "2026/10/08 09:14:12 staging from " + presigned + " for alice@example.com\n" +
		"2026/10/08 09:14:13 job ended, scratch " + home + "/jobs/j1 removed\n"
	if err := os.WriteFile(logPath, []byte(logBody), 0o600); err != nil {
		t.Fatalf("write log: %v", err)
	}

	summary, err := json.Marshal(map[string]string{
		"configPath": filepath.Join(home, ".config", "ReelBolt", "runner.json"),
		"deviceId":   "6f1a2c34-9b7e-4c11-8a2d-6c1f0b9d4e77",
		"version":    "1.4.2",
	})
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}

	scrub := NewScrubber()
	scrub.home = home
	scrub.names = []string{"alice"}

	zipPath := filepath.Join(dir, "diagnostics", "reelbolt-diagnostics.zip")
	res, err := WriteDiagnosticsZip(zipPath, Diagnostics{
		App:      "ReelBolt runner",
		Version:  "1.4.2",
		Summary:  summary,
		Config:   []byte("{\"logFile\": \"" + home + "/runner.log\", \"deviceId\": \"dev-x\"}"),
		Logs:     []string{logPath},
		Scrubber: scrub,
		Extra:    map[string][]byte{"notes.txt": []byte("the preset is called Summer Promo")},
		Now:      func() time.Time { return time.Date(2026, 10, 8, 9, 14, 12, 0, time.UTC) },
	})
	if err != nil {
		t.Fatalf("WriteDiagnosticsZip: %v", err)
	}
	if res.Entries < 5 {
		t.Errorf("wrote %d entries, want the readme, the summary, the config, the log and the extra", res.Entries)
	}
	if res.Bytes <= 0 {
		t.Errorf("the report says the bundle is %d bytes", res.Bytes)
	}

	entries := readZip(t, zipPath)
	for _, name := range []string{"README.txt", "summary.json", "runner.json", "logs/runner.log", "notes.txt"} {
		if _, ok := entries[name]; !ok {
			t.Errorf("%s is missing from the bundle: %v", name, keys(entries))
		}
	}
	whole := strings.Join(keys(entries), " ")
	for name, body := range entries {
		whole += "\n" + body
		if name != "logs/runner.log" {
			continue
		}
		for _, secret := range []string{"X-Amz-Signature", "AKIA" + "IOSFODNN7EXAMPLE", "alice@example.com", home} {
			if strings.Contains(body, secret) {
				t.Errorf("the log kept %q:\n%s", secret, body)
			}
		}
		if !strings.Contains(body, "?<redacted>") || !strings.Contains(body, "<email>") {
			t.Errorf("the log was not redacted:\n%s", body)
		}
	}
	for _, secret := range []string{home, "6f1a2c34-9b7e-4c11-8a2d-6c1f0b9d4e77", "alice@example.com"} {
		if strings.Contains(whole, secret) {
			t.Errorf("%q survived somewhere in the bundle", secret)
		}
	}
	// The readme has to say what the bundle is and that it is local.
	readme := entries["README.txt"]
	for _, want := range []string{"diagnostics", "2026-10-08T09:14:12Z", "has not been", "best effort"} {
		if !strings.Contains(readme, want) {
			t.Errorf("the readme does not say %q:\n%s", want, readme)
		}
	}
}

// A log that cannot be read is reported inside the bundle rather than failing
// the export: the user asked for a file, and a partial one beats an error.
func TestAnUnreadableLogDoesNotFailTheExport(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "diagnostics.zip")
	res, err := WriteDiagnosticsZip(path, Diagnostics{
		Logs: []string{filepath.Join(dir, "does-not-exist.log")},
	})
	if err != nil {
		t.Fatalf("WriteDiagnosticsZip: %v", err)
	}
	entries := readZip(t, path)
	found := false
	for name := range entries {
		if strings.HasSuffix(name, ".unreadable.txt") {
			found = true
		}
	}
	if !found {
		t.Errorf("nothing recorded the unreadable log: %v", keys(entries))
	}
	if res.Entries == 0 {
		t.Error("the export reports no entries")
	}
}

// A zip is the one format where a name is a path, so an entry name must not be
// able to escape the archive.
func TestZipEntryNamesCannotTraverse(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "diagnostics.zip")
	if _, err := WriteDiagnosticsZip(path, Diagnostics{
		Extra: map[string][]byte{"../../etc/passwd": []byte("nope")},
	}); err != nil {
		t.Fatalf("WriteDiagnosticsZip: %v", err)
	}
	for name := range readZip(t, path) {
		if strings.Contains(name, "..") || strings.HasPrefix(name, "/") {
			t.Errorf("entry name %q could traverse", name)
		}
	}
}

func keys(m map[string]string) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	return out
}
