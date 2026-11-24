package telemetry

import (
	"archive/zip"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// Diagnostics is what goes into an export.
//
// The export is LOCAL. Nothing in this file uploads anything: it writes a zip
// the user opens, reads and then chooses to attach to a support conversation.
// That is the whole point of the button, and it is why the export may carry
// more than a wire report does - and why its free text is still redacted
// (Scrubber), since a file a user forwards should not be a worse disclosure
// than the one they were promised.
type Diagnostics struct {
	// App and Version name what produced the bundle.
	App     string
	Version string
	// Summary is the machine's own facts, as JSON. It is redacted before it is
	// written.
	Summary []byte
	// Scan is the optional capability probe's own document, as JSON.
	Scan []byte
	// Config is runner.json, verbatim. It is redacted before it is written.
	Config []byte
	// Logs are the files to include, scrubbed file by file.
	Logs []string
	// Scrubber redacts everything textual. Nil gets a fresh one.
	Scrubber *Scrubber
	// Extra are any additional named files.
	Extra map[string][]byte
	// Now overrides the clock, for tests.
	Now func() time.Time
}

// DiagnosticsResult is what the export reports back to the UI.
type DiagnosticsResult struct {
	Path    string
	Entries int
	Bytes   int64
}

// DefaultDiagnosticsDir returns where exports are written.
func DefaultDiagnosticsDir() (string, error) {
	base, err := os.UserCacheDir()
	if err != nil {
		return "", fmt.Errorf("telemetry: user cache directory: %w", err)
	}
	return filepath.Join(base, DirName, "diagnostics"), nil
}

// WriteDiagnosticsZip writes the bundle. The caller decides the path, so a test
// can write into a temporary directory and a user can be told where it went by
// name rather than by guess.
func WriteDiagnosticsZip(path string, d Diagnostics) (DiagnosticsResult, error) {
	res := DiagnosticsResult{Path: path}
	scrub := d.Scrubber
	if scrub == nil {
		scrub = NewScrubber()
	}
	now := d.Now
	if now == nil {
		now = time.Now
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return res, fmt.Errorf("telemetry: create %s: %w", filepath.Dir(path), err)
	}
	f, err := os.OpenFile(path, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o600)
	if err != nil {
		return res, fmt.Errorf("telemetry: create %s: %w", path, err)
	}
	defer func() { _ = f.Close() }()

	zw := zip.NewWriter(f)
	write := func(name, body string) error {
		w, err := zw.Create(name)
		if err != nil {
			return err
		}
		if _, err := w.Write([]byte(body)); err != nil {
			return err
		}
		res.Entries++
		return nil
	}
	abort := func(err error) (DiagnosticsResult, error) {
		_ = zw.Close()
		return res, err
	}

	if err := write("README.txt", readmeText(d, now())); err != nil {
		return abort(err)
	}
	if len(d.Summary) > 0 {
		if err := write("summary.json", withNewline(scrub.Text(string(d.Summary)))); err != nil {
			return abort(err)
		}
	}
	if len(d.Scan) > 0 {
		if err := write("capabilities.json", withNewline(scrub.Text(string(d.Scan)))); err != nil {
			return abort(err)
		}
	}
	if len(d.Config) > 0 {
		if err := write("runner.json", withNewline(scrub.Text(string(d.Config)))); err != nil {
			return abort(err)
		}
	}
	for name, body := range d.Extra {
		if err := write(sanitizeEntry(name), withNewline(scrub.Text(string(body)))); err != nil {
			return abort(err)
		}
	}
	for _, logPath := range d.Logs {
		name := sanitizeEntry(filepath.Base(logPath))
		raw, err := os.ReadFile(logPath)
		if err != nil {
			// A log that rotated away between listing and reading is not a
			// reason to fail the export; it is a reason to say so.
			if werr := write("logs/"+name+".unreadable.txt",
				withNewline("this file could not be read: "+scrub.Text(err.Error()))); werr != nil {
				return abort(werr)
			}
			continue
		}
		if err := write("logs/"+name, withNewline(scrub.Text(string(raw)))); err != nil {
			return abort(err)
		}
	}
	if err := zw.Close(); err != nil {
		return res, fmt.Errorf("telemetry: close %s: %w", path, err)
	}
	if info, err := os.Stat(path); err == nil {
		res.Bytes = info.Size()
	}
	return res, nil
}

// withNewline ends a text entry with exactly one newline.
func withNewline(s string) string {
	if strings.HasSuffix(s, "\n") {
		return s
	}
	return s + "\n"
}

// sanitizeEntry strips anything that could be read as a directory traversal out
// of a zip entry name. A zip is the one format where a name is a path.
func sanitizeEntry(name string) string {
	name = strings.ReplaceAll(name, "\\", "/")
	name = strings.TrimPrefix(name, "/")
	name = strings.ReplaceAll(name, "..", "_")
	if name == "" {
		return "unnamed"
	}
	return name
}

// readmeText is the first thing a reader sees, and it says what the bundle is,
// what was removed and what to do with it.
func readmeText(d Diagnostics, at time.Time) string {
	app := d.App
	if app == "" {
		app = "ReelBolt runner"
	}
	version := d.Version
	if version == "" {
		version = "unknown version"
	}
	return strings.Join([]string{
		app + " diagnostics",
		"version:  " + version,
		"created:  " + at.UTC().Format(time.RFC3339) + " (UTC)",
		"",
		"What this is",
		"",
		"  A snapshot of this machine's " + app + " state, written by the",
		"  \"Export diagnostics\" button in the runner window. It has not been",
		"  sent anywhere: it is a file on your own disk, and you choose whether",
		"  to attach it to a support conversation.",
		"",
		"What is in it",
		"",
		"  summary.json      what the runner knows about this machine: version,",
		"                    platform, the container runtime it found, its job",
		"                    types, its resource limits and its settings. Paths",
		"                    and identifiers are redacted.",
		"  runner.json       a copy of this machine's runner configuration.",
		"  capabilities.json the container runtime's own capability document,",
		"                    when the runner has probed it.",
		"  logs/             the runner's own log, rotated at 5 MB x 5 files.",
		"",
		"What has been removed",
		"",
		"  home directories, your user name, this computer's name, other users'",
		"  home directories, e-mail addresses, URL query strings and fragments,",
		"  credentials embedded in URLs, device and workspace identifiers, IP",
		"  addresses and long opaque tokens such as signatures and content",
		"  hashes. Redactions appear as ~, <id>, <email>, <ip> or <redacted>.",
		"",
		"  This removal is best effort. It is a list of things known to be",
		"  sensitive, not a proof that nothing sensitive is left, which is why",
		"  this file exists for you to read before you send it anywhere.",
		"",
		"What this file is not",
		"",
		"  It is not a crash report and it is not telemetry. The runner's opt-in",
		"  crash reporting is a separate, narrow document described in the",
		"  Settings screen; this export never leaves your machine on its own.",
		"",
	}, "\n")
}
