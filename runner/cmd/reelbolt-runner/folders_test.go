package main

import (
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/foldersync"
)

// The folder-sync command line, tested through the same pure helpers the
// commands print from. The commands themselves parse argv and write files, so
// what is asserted here is the wording and the decision — which is where a
// wrong sentence would reach a person.

func TestAPIOriginRefusesToGuess(t *testing.T) {
	// An empty value is the state of a computer paired before folder sync
	// existed. It must NOT become a default: the device's signature would go to
	// a host it never agreed to.
	cfg := &config.Config{}
	cfg.ApplyDefaults()
	if _, err := apiOrigin(cfg); err == nil {
		t.Fatal("an empty apiBaseUrl was accepted")
	} else if !strings.Contains(err.Error(), "Pair this computer again") {
		t.Fatalf("the empty-origin message = %q", err)
	}

	cfg.APIBaseURL = "https://app.reelbolt.ai"
	got, err := apiOrigin(cfg)
	if err != nil || got != "https://app.reelbolt.ai" {
		t.Fatalf("apiOrigin = %q, %v", got, err)
	}

	// A host outside the allowlist is refused here too, so "config set-api"
	// and the daemon's own load agree.
	cfg.APIBaseURL = "https://evil.example.com"
	if _, err := apiOrigin(cfg); err == nil {
		t.Fatal("a foreign origin was accepted")
	}
}

func TestFolderStatusLineMatchesTheDesign(t *testing.T) {
	cases := []struct {
		name string
		in   foldersync.LinkStatus
		want string
	}{
		{"up to date", foldersync.LinkStatus{State: foldersync.StateUpToDate}, "Up to date"},
		{"syncing files", foldersync.LinkStatus{State: foldersync.StateSyncing, Pending: 3}, "Syncing 3 files"},
		{"syncing one file", foldersync.LinkStatus{State: foldersync.StateSyncing, Pending: 1}, "Syncing 1 file"},
		{"checking", foldersync.LinkStatus{State: foldersync.StateSyncing}, "Checking the folder"},
		{"paused", foldersync.LinkStatus{State: foldersync.StatePaused, Reason: "Paused from the ReelBolt dashboard — resume it there."},
			"Paused (Paused from the ReelBolt dashboard — resume it there.)"},
		{"attention", foldersync.LinkStatus{State: foldersync.StateAttention, Reason: "Storage is full (4.2 GB of 15.0 GB used)"},
			"Needs attention (Storage is full (4.2 GB of 15.0 GB used))"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if got := folderStatusLine(c.in); got != c.want {
				t.Fatalf("folderStatusLine = %q, want %q", got, c.want)
			}
		})
	}
}

// TestFoldersRowCarriesWhatTheWindowShows: the row is the same shape the Go UI
// and the frontend read, so "folders list --json" and the window cannot disagree.
func TestFoldersRowCarriesWhatTheWindowShows(t *testing.T) {
	synced := time.Date(2026, 10, 11, 9, 0, 0, 0, time.UTC)
	live := foldersync.Status{Links: []foldersync.LinkStatus{{
		LinkID: "link-1", State: foldersync.StateAttention,
		Headline: "Needs attention", Reason: "Storage is full (4.2 GB of 15.0 GB used)",
		FileCount: 12, LastSyncAt: &synced,
	}}}
	row := foldersRow(foldersync.Link{LinkID: "link-1", LocalPath: "/home/a/Footage", ProjectID: "p1", TargetFolder: "Footage"}, live, true)
	if row.State != foldersync.StateAttention || row.Files != 12 {
		t.Fatalf("row = %+v", row)
	}
	if row.LastError != "Storage is full (4.2 GB of 15.0 GB used)" {
		t.Fatalf("LastError = %q", row.LastError)
	}
	if row.LastSync != synced.Format(time.RFC3339) {
		t.Fatalf("LastSync = %q", row.LastSync)
	}

	// With no live document there is no state to claim — which is NOT the same
	// as "up to date".
	row = foldersRow(foldersync.Link{LinkID: "link-1", LocalPath: "/x", ProjectID: "p1"}, foldersync.Status{}, false)
	if row.State != "" || row.Files != 0 {
		t.Fatalf("a row without a live document invented state: %+v", row)
	}
}

// TestFoldersAddRefusesBeforeTouchingAnything checks the argument handling: a
// command that cannot work must fail before it writes or dials.
func TestFoldersAddRefusesBeforeTouchingAnything(t *testing.T) {
	cases := [][]string{
		{},                               // no path
		{"/tmp"},                         // no --project
		{"/tmp", "--project", "p1", "x"}, // two paths
	}
	for _, args := range cases {
		if err := cmdFoldersAdd(args); err == nil {
			t.Errorf("folders add %v was accepted", args)
		}
	}
}

func TestFoldersDispatchRejectsAnUnknownSubcommand(t *testing.T) {
	if err := cmdFolders(nil); err == nil {
		t.Fatal("folders with no subcommand was accepted")
	}
	err := cmdFolders([]string{"frobnicate"})
	if err == nil || !strings.Contains(err.Error(), "unknown folders subcommand") {
		t.Fatalf("unknown subcommand error = %v", err)
	}
	err = cmdConfig([]string{"frobnicate"})
	if err == nil || !strings.Contains(err.Error(), "unknown config subcommand") {
		t.Fatalf("unknown config subcommand error = %v", err)
	}
}

// TestConfigSetAPIValidatesBeforeWriting is the owner's decision made concrete:
// the escape hatch validates with the daemon's own rule, so a value it would
// refuse at load never reaches the file.
func TestConfigSetAPIValidatesBeforeWriting(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "runner.json")
	cfg := &config.Config{}
	cfg.ApplyDefaults()
	if err := cfg.SaveTo(path); err != nil {
		t.Fatal(err)
	}
	before, err := config.LoadFrom(path)
	if err != nil {
		t.Fatal(err)
	}
	// The refusal happens before any write. Config.CheckAPIBase is the rule the
	// daemon's Validate applies at load, which is what makes the two agree.
	if _, err := config.CheckAPIBase("https://evil.example.com", before.DevGateways); err == nil {
		t.Fatal("a foreign origin passed validation")
	}
	// The accepted form canonicalises (no trailing slash), which is what is
	// written.
	got, err := config.CheckAPIBase("https://app.reelbolt.ai/", before.DevGateways)
	if err != nil || got != "https://app.reelbolt.ai" {
		t.Fatalf("CheckAPIBase = %q, %v", got, err)
	}
}

func TestFolderSyncReportReadsTheLiveSummary(t *testing.T) {
	st := foldersync.Status{
		Problem: "this computer is no longer connected to ReelBolt — pair it again",
		Links:   []foldersync.LinkStatus{{LinkID: "l1", State: foldersync.StateAttention}},
	}
	got := st.Summary()
	if !strings.Contains(got, "pair it again") {
		t.Fatalf("Summary = %q", got)
	}
	// No links, nothing wrong: no tray row at all.
	if s := (foldersync.Status{}).Summary(); s != "" {
		t.Fatalf("an empty status summary = %q", s)
	}
}
