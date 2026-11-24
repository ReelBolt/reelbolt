package ui

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// Package ui's Folders section: what the window shows about computer folder
// sync, and the actions a person takes from it.
//
// The shell INVENTS NO SECOND TRUTH here, the same rule the rest of this
// package follows. The state is the engine's own (internal/foldersync): the
// same Status document the tray and "folders list" read, and the same
// folders.json. The host supplies an implementation of FolderOps; this file
// decides what to show and what a button does, so both are testable without
// Wails, a desktop, or a network.

// ErrNoFolderPicker means this host has no native folder chooser (a headless
// test, or a platform where the chooser is unavailable). It is a plain sentence
// a person can read, and the UI must show it rather than pretend a folder was
// chosen.
var ErrNoFolderPicker = errors.New("this computer has no window to choose a folder from")

// FolderLink is one synced folder as a person sees it.
type FolderLink struct {
	LinkID       string `json:"linkId"`
	LocalPath    string `json:"localPath"`
	ProjectID    string `json:"projectId"`
	ProjectName  string `json:"projectName,omitempty"`
	TargetFolder string `json:"targetFolder"`
	// ProjectURL is where "Open project in browser" goes.
	ProjectURL string `json:"projectUrl,omitempty"`

	// State is one of the foldersync state words: uptodate, syncing, paused,
	// attention. It is carried through unchanged, so the window cannot invent a
	// state the engine does not have.
	State    string `json:"state"`
	Headline string `json:"headline"`
	Reason   string `json:"reason,omitempty"`
	// PausedBy is "computer", "guard" or "dashboard". It decides which button
	// the window offers: Resume here, Resume (files will be deleted), or
	// nothing at all.
	PausedBy         string `json:"pausedBy,omitempty"`
	CanConfirmDelete bool   `json:"canConfirmDelete,omitempty"`

	FileCount    int    `json:"fileCount"`
	Pending      int    `json:"pending,omitempty"`
	LastChangeAt string `json:"lastChangeAt,omitempty"`
	LastSyncAt   string `json:"lastSyncAt,omitempty"`
	// Skipped says what was not copied, and why, in the engine's own words.
	Skipped []SkippedGroup `json:"skipped,omitempty"`
	Note    string         `json:"note,omitempty"`
}

// SkippedGroup is one reason files were not copied, with a few examples.
type SkippedGroup struct {
	Reason   string   `json:"reason"`
	Count    int      `json:"count"`
	Examples []string `json:"examples,omitempty"`
}

// FolderProject is one project a new link may target.
type FolderProject struct {
	ID   string `json:"id"`
	Name string `json:"name"`
}

// FoldersView is the Folders screen's whole document.
type FoldersView struct {
	// Ready is false when folder sync cannot run at all: this computer has no
	// API origin (it was paired before folder sync existed), or it is not
	// paired. Problem says which, in one sentence a person can act on.
	Ready   bool   `json:"ready"`
	Problem string `json:"problem,omitempty"`
	// Running is true when a runner is actually syncing right now. A list can
	// be ready and not running (nothing has started the runner).
	Running bool         `json:"running"`
	Summary string       `json:"summary,omitempty"`
	Links   []FolderLink `json:"links"`
	// Projects is what the Add form offers. It is fetched on demand, so it is
	// empty until someone opens that form.
	Projects []FolderProject `json:"projects"`
	// StatusPath names the engine's status file, for a bug report.
	StatusPath string `json:"statusPath,omitempty"`
	// PickerAvailable is false when this host cannot show a folder chooser.
	PickerAvailable bool `json:"pickerAvailable"`
}

// Link finds one link by its id.
func (v FoldersView) Link(id string) (FolderLink, bool) {
	for _, l := range v.Links {
		if l.LinkID == id {
			return l, true
		}
	}
	return FolderLink{}, false
}

// AddFolderRequest is what the host needs to create a link.
type AddFolderRequest struct {
	// LocalPath is the absolute path the person chose. It is never sent to
	// ReelBolt except as the link's display string.
	LocalPath string
	// ProjectID and TargetFolder come from the person's answers.
	ProjectID    string
	TargetFolder string
}

// FolderAsker is how the host asks the person to choose a project and to
// confirm a removal. It is an interface because the window's answer comes from
// a form, and a test's answer comes from the test.
type FolderAsker interface {
	// ChooseProject asks which project a new folder should sync into, from the
	// projects this device may edit. An empty answer and a nil error means the
	// person cancelled.
	ChooseProject(ctx context.Context, projects []FolderProject) (string, error)
	// ConfirmRemove asks whether to stop syncing a folder, naming the promise
	// that the files already copied stay in the project. True confirms.
	ConfirmRemove(ctx context.Context, link FolderLink) (bool, error)
}

// FolderOps is the host's half of folder sync: the state to show, and the four
// things a person can do to it. The host owns it because it owns the engine and
// the signed device client; the shell owns what is shown and what a button
// does.
type FolderOps interface {
	// ListFolders is the current state: every link and the engine's status for
	// it, or the one sentence explaining why folder sync is off.
	ListFolders(ctx context.Context) (FoldersView, error)
	// ChooseFolder opens the native chooser and returns the absolute path, or
	// ErrNoFolderPicker.
	ChooseFolder(ctx context.Context) (string, error)
	// ListProjects is the projects this device may sync into.
	ListProjects(ctx context.Context) ([]FolderProject, error)
	// AddFolder creates the link in ReelBolt and records it here. TargetFolder
	// is a suggestion the server may reject.
	AddFolder(ctx context.Context, req AddFolderRequest) (FolderLink, error)
	// PauseFolder and ResumeFolder change the LOCAL pause. resumeConfirm is the
	// person's answer to the mass-delete guard.
	PauseFolder(ctx context.Context, linkID string) error
	ResumeFolder(ctx context.Context, linkID string, confirmDelete bool) error
	// RemoveFolder stops syncing a link; the files already copied stay in the
	// project. It reports whether the link was there.
	RemoveFolder(ctx context.Context, linkID string) (bool, error)
	// OpenProject opens the project in the browser.
	OpenProject(ctx context.Context, projectID string) error
}

// DefaultFolderTarget is the target folder name suggested for a local path: the
// folder's own name, which is what the design pre-fills.
func DefaultFolderTarget(localPath string) string {
	clean := filepath.Clean(strings.TrimSpace(localPath))
	if clean == "" || clean == "." || clean == string(filepath.Separator) {
		return ""
	}
	base := filepath.Base(clean)
	if base == "." || base == string(filepath.Separator) {
		return ""
	}
	return base
}

// validateFolderChoice checks a chosen path before the host is asked to use it.
// The checks are the same ones the command line makes, so the window and
// "folders add" refuse the same things with the same words.
func validateFolderChoice(localPath string) (string, error) {
	trimmed := strings.TrimSpace(localPath)
	if trimmed == "" {
		return "", say("Choose the folder to sync.")
	}
	abs, err := filepath.Abs(trimmed)
	if err != nil {
		return "", sayWrap("That folder couldn't be found.", err)
	}
	info, err := os.Stat(abs)
	if err != nil {
		return "", sayWrap("That folder can't be opened. Check that it still exists and that you can open it.", err)
	}
	if !info.IsDir() {
		return "", sayf("%s is a file, not a folder.", abs)
	}
	return abs, nil
}

// folderSkipGroups turns the engine's groups into the screen's, keeping the
// engine's own wording.
func folderSkipGroups(groups []SkippedGroup) []SkippedGroup {
	out := make([]SkippedGroup, 0, len(groups))
	for _, g := range groups {
		if g.Count == 0 {
			continue
		}
		cp := g
		if len(cp.Examples) > 3 {
			cp.Examples = cp.Examples[:3]
		}
		out = append(out, cp)
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Count != out[j].Count {
			return out[i].Count > out[j].Count
		}
		return out[i].Reason < out[j].Reason
	})
	return out
}
