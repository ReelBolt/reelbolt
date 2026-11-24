package foldersync

import (
	"encoding/json"
	"fmt"
	"os"
	"sort"
	"time"
)

// Link states. They are what the window and the tray say, in this order of
// importance.
const (
	StateAttention = "attention" // Needs attention (reason)
	StatePaused    = "paused"    // Paused (reason)
	StateSyncing   = "syncing"   // Syncing N files / Checking the folder
	StateUpToDate  = "uptodate"  // Up to date
)

// Who paused a link.
const (
	PausedOnComputer  = "computer"
	PausedByGuard     = "guard"
	PausedByDashboard = "dashboard"
)

// SkipGroup is how many files were not copied for one reason, with a few
// examples so a person can find them.
type SkipGroup struct {
	Reason   string   `json:"reason"`
	Count    int      `json:"count"`
	Examples []string `json:"examples,omitempty"`
}

// LinkStatus is one synced folder's status.
type LinkStatus struct {
	LinkID       string `json:"linkId"`
	LocalPath    string `json:"localPath"`
	ProjectID    string `json:"projectId"`
	ProjectName  string `json:"projectName,omitempty"`
	TargetFolder string `json:"targetFolder"`

	State    string `json:"state"`
	Headline string `json:"headline"`
	Reason   string `json:"reason,omitempty"`
	// PausedBy says who paused it, so the window offers the right button:
	// Resume for a pause made here, "Resume (files will be deleted)" for the
	// mass-delete guard, and nothing for a dashboard pause.
	PausedBy string `json:"pausedBy,omitempty"`
	// CanConfirmDelete is true when the mass-delete guard paused the link and
	// Resume would confirm the deletions.
	CanConfirmDelete bool `json:"canConfirmDelete,omitempty"`

	Pending      int        `json:"pending"`
	FileCount    int        `json:"fileCount"`
	LastChangeAt *time.Time `json:"lastChangeAt,omitempty"`
	LastSyncAt   *time.Time `json:"lastSyncAt,omitempty"`
	// Polling is true when the folder has too many sub-folders to watch, so
	// it is rescanned every two minutes instead; Note says so.
	Polling bool        `json:"polling,omitempty"`
	Note    string      `json:"note,omitempty"`
	Skipped []SkipGroup `json:"skipped,omitempty"`
}

// Status is every link's status plus what is wrong with folder sync as a whole.
type Status struct {
	UpdatedAt time.Time `json:"updatedAt"`
	PID       int       `json:"pid,omitempty"`
	// Problem is set when nothing can sync: this computer is not paired, was
	// paired before folder sync existed, or the server stopped accepting it.
	Problem string       `json:"problem,omitempty"`
	Links   []LinkStatus `json:"links"`
}

// SummaryOf is the tray's one line for a status read from another process (or
// from this one), plus the engine-wide problem that may not be in the document
// yet. It is a free function because the caller that reads a published status
// has a Status value, not an Engine.
func SummaryOf(s Status, have bool, problem string) string {
	if !have {
		if len(s.Links) == 0 && problem == "" {
			return ""
		}
		if problem != "" {
			return "Folder sync: " + problem
		}
	}
	cp := s
	if problem != "" && cp.Problem == "" {
		cp.Problem = problem
	}
	return cp.Summary()
}

// Summary is the tray's one line. It is empty when there are no synced
// folders and nothing is wrong, so a machine that does not use folder sync
// sees no extra row.
func (s Status) Summary() string {
	if s.Problem != "" && len(s.Links) > 0 {
		return "Folder sync: " + s.Problem
	}
	if len(s.Links) == 0 {
		return ""
	}
	var attention, syncing, paused, pending int
	for _, l := range s.Links {
		switch l.State {
		case StateAttention:
			attention++
		case StateSyncing:
			syncing++
			pending += l.Pending
		case StatePaused:
			paused++
		}
	}
	switch {
	case attention == 1 && len(s.Links) == 1:
		return "Folder sync needs attention: " + s.Links[0].Reason
	case attention > 0:
		return fmt.Sprintf("Folder sync: %d %s need attention", attention, plural(attention, "folder", "folders"))
	case syncing > 0 && pending > 0:
		return fmt.Sprintf("Folder sync: syncing %d %s", pending, plural(pending, "file", "files"))
	case syncing > 0:
		return "Folder sync: checking for changes"
	case paused == len(s.Links):
		return "Folder sync: paused"
	default:
		return "Folder sync: up to date"
	}
}

func plural(n int, one, many string) string {
	if n == 1 {
		return one
	}
	return many
}

// headline is the short state text.
func headline(state string, pending int, checking bool) string {
	switch state {
	case StateAttention:
		return "Needs attention"
	case StatePaused:
		return "Paused"
	case StateSyncing:
		if pending > 0 {
			return fmt.Sprintf("Syncing %d %s", pending, plural(pending, "file", "files"))
		}
		if checking {
			return "Checking for changes"
		}
		return "Syncing"
	default:
		return "Up to date"
	}
}

// skipGroups turns reason → paths into sorted groups with three examples.
func skipGroups(m map[string]map[string]struct{}) []SkipGroup {
	var out []SkipGroup
	for reason, paths := range m {
		if len(paths) == 0 {
			continue
		}
		list := make([]string, 0, len(paths))
		for p := range paths {
			list = append(list, p)
		}
		sort.Strings(list)
		g := SkipGroup{Reason: reason, Count: len(list)}
		if len(list) > 3 {
			list = list[:3]
		}
		g.Examples = list
		out = append(out, g)
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Count != out[j].Count {
			return out[i].Count > out[j].Count
		}
		return out[i].Reason < out[j].Reason
	})
	return out
}

// StatusFileName is where the running engine publishes its status, under the
// runner's cache directory, so "reelbolt-runner status" and "folders list"
// can show what a daemon in another process is doing.
const StatusFileName = "folder-sync.status.json"

// ReadStatus reads a published status. Fresh reports whether it was written in
// the last maxAge (a daemon that stopped leaves a stale file behind).
func ReadStatus(path string, maxAge time.Duration) (Status, bool, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return Status{}, false, err
	}
	var s Status
	if err := json.Unmarshal(raw, &s); err != nil {
		return Status{}, false, err
	}
	return s, time.Since(s.UpdatedAt) <= maxAge, nil
}
