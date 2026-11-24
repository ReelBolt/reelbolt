package ui

import (
	"context"
	"errors"
	"fmt"
	"strings"
)

// The Folders actions, and the shell methods behind them.
//
// Every action goes through App.Do, like every other button in this window, so
// the tray, the window and the keyboard cannot disagree about what "Resume"
// does. The work itself is the host's (FolderOps); this file is the decision
// about which of the four things a person can do, and what to say afterwards.

// Folder action IDs.
const (
	ActionOpenFolders   = "open-folders"
	ActionFoldersAdd    = "folders-add"
	ActionFoldersPause  = "folders-pause"
	ActionFoldersResume = "folders-resume"
	ActionFoldersRemove = "folders-remove"
	// ActionFoldersOpenProject takes the link id in Arg.
	ActionFoldersOpenProject = "folders-open-project"
)

// Folders is the screen's document, for the local API.
func (a *App) Folders() FoldersView {
	return a.shell.FoldersDocument(context.Background())
}

// FoldersView is the Folders screen's document, read from the host. A shell
// with no host folder support reports why instead of showing an empty list,
// because "there is nothing here" and "this build cannot do this" are different
// sentences.
func (s *Shell) FoldersDocument(ctx context.Context) FoldersView {
	if s.opts.Folders == nil {
		return FoldersView{Problem: noFolderSupportSentence, Links: []FolderLink{}}
	}
	v, err := s.opts.Folders.ListFolders(ctx)
	if err != nil {
		return FoldersView{Problem: userMessage(err), Links: []FolderLink{}, StatusPath: s.foldersStatusPath()}
	}
	if v.Links == nil {
		v.Links = []FolderLink{}
	}
	if v.StatusPath == "" {
		v.StatusPath = s.foldersStatusPath()
	}
	v.PickerAvailable = s.folderPickerAvailable()
	if !v.Ready && v.Problem == "" {
		v.Problem = noFolderSupportSentence
	}
	return v
}

// FolderPickerChecker is an optional FolderOps extension: a host that knows,
// without opening a dialog, whether it has a folder chooser. The window uses it
// to disable a button that could only fail; a host that does not implement it is
// assumed to have one, which is what a real desktop host wants.
type FolderPickerChecker interface {
	PickerAvailable() bool
}

// noFolderSupportSentence is what a build with no folder support says. It is
// deliberately not an error: a headless test and an unsupported platform are
// both legitimately without a chooser, and neither should look broken.
const noFolderSupportSentence = "Folder sync isn't available in this version of the app."

// folderPickerAvailable reports whether the host can show a chooser at all,
// without opening one.
func (s *Shell) folderPickerAvailable() bool {
	if s.opts.Folders == nil {
		return false
	}
	if s.opts.PickerAvailable != nil {
		return s.opts.PickerAvailable()
	}
	if c, ok := s.opts.Folders.(FolderPickerChecker); ok {
		return c.PickerAvailable()
	}
	return true
}

func (s *Shell) foldersStatusPath() string { return s.opts.FoldersStatusPath }

// --- actions ---

func (a *App) foldersAdd(ctx context.Context) (ActionResult, error) {
	var res ActionResult
	if a.shell.opts.Folders == nil {
		return res, say(noFolderSupportSentence)
	}
	// The project first: choosing one and then being told the folder chooser is
	// unavailable would waste the person's click.
	projects, err := a.shell.opts.Folders.ListProjects(ctx)
	if err != nil {
		return res, err
	}
	if len(projects) == 0 {
		return res, say("There's no project this computer can add files to. Ask the person who paired it for access, or pair it again.")
	}
	project := projects[0]
	if len(projects) > 1 {
		if a.opts.Ask == nil {
			return res, say("Choose a project for the folder in ReelBolt first.")
		}
		chosen, err := a.opts.Ask.ChooseProject(ctx, projects)
		if err != nil {
			return res, err
		}
		if chosen == "" {
			return res, say("No project was chosen, so nothing was added.")
		}
		project = FolderProject{ID: chosen}
		for _, p := range projects {
			if p.ID == chosen {
				project = p
				break
			}
		}
	}

	local, err := a.shell.opts.Folders.ChooseFolder(ctx)
	if errors.Is(err, ErrNoFolderPicker) {
		return res, sayWrap("This computer can't show a folder chooser, so no folder was added.", err)
	}
	if err != nil {
		return res, err
	}
	abs, err := validateFolderChoice(local)
	if err != nil {
		return res, err
	}
	target := DefaultFolderTarget(abs)
	link, err := a.shell.opts.Folders.AddFolder(ctx, AddFolderRequest{
		LocalPath: abs, ProjectID: project.ID, TargetFolder: target,
	})
	if err != nil {
		return res, folderAddMessage(err, abs)
	}
	res.Message = fmt.Sprintf("Syncing %s into %s as %s. If you stop syncing later, the files already copied stay in the project.",
		abs, folderProjectName(project, link), link.TargetFolder)
	return res, nil
}

// folderAddMessage turns the server's refusals into sentences a person can act
// on. The codes are the device API's own, matched by text because this package
// deliberately does not import the device client.
func folderAddMessage(err error, localPath string) error {
	msg := err.Error()
	switch {
	case strings.Contains(msg, "target_folder_taken"):
		return sayf("The project already has a folder called %q. Rename the folder on this computer and try again.", DefaultFolderTarget(localPath))
	case strings.Contains(msg, "invalid_target_folder"):
		return sayf("%q can't be used as a folder name in a project. Rename the folder on this computer and try again.", DefaultFolderTarget(localPath))
	case strings.Contains(msg, "device_owner_lacks_access"):
		return say("The person who paired this computer can't edit that project any more.")
	case strings.Contains(msg, "device_unauthorized"):
		return say("This computer isn't connected to ReelBolt any more. Pair it again.")
	default:
		return folderListMessage(err)
	}
}

func folderProjectName(p FolderProject, l FolderLink) string {
	if l.ProjectName != "" {
		return l.ProjectName
	}
	if p.Name != "" {
		return p.Name
	}
	return p.ID
}

func (a *App) foldersPause(ctx context.Context, linkID string) (ActionResult, error) {
	var res ActionResult
	if a.shell.opts.Folders == nil {
		return res, say(noFolderSupportSentence)
	}
	if err := a.shell.opts.Folders.PauseFolder(ctx, linkID); err != nil {
		return res, err
	}
	res.Message = "Paused. Nothing more is copied from that folder until you resume it."
	return res, nil
}

func (a *App) foldersResume(ctx context.Context, linkID string) (ActionResult, error) {
	var res ActionResult
	if a.shell.opts.Folders == nil {
		return res, say(noFolderSupportSentence)
	}
	// Resume after the mass-delete guard has to confirm the deletions, or the
	// server would hold them back again and the button would do nothing. The
	// window asks before it reaches here; the shell does not ask twice.
	confirm := false
	if doc := a.shell.FoldersDocument(ctx); doc.Ready {
		if link, ok := doc.Link(linkID); ok && link.CanConfirmDelete {
			confirm = true
		}
	}
	if err := a.shell.opts.Folders.ResumeFolder(ctx, linkID, confirm); err != nil {
		return res, err
	}
	if confirm {
		res.Message = "Resumed. The files you deleted on this computer will be removed from the project on the next check."
	} else {
		res.Message = "Resumed. Changes in that folder are copied again."
	}
	return res, nil
}

func (a *App) foldersRemove(ctx context.Context, linkID string) (ActionResult, error) {
	var res ActionResult
	if a.shell.opts.Folders == nil {
		return res, say(noFolderSupportSentence)
	}
	doc := a.shell.FoldersDocument(ctx)
	link, ok := doc.Link(linkID)
	if !ok {
		return res, say("That folder isn't synced on this computer any more.")
	}
	if a.opts.Ask == nil {
		return res, say("This computer can't show the confirmation, so nothing was removed.")
	}
	// The confirmation names the one promise that matters: the files already in
	// the project are not deleted.
	ok, err := a.opts.Ask.ConfirmRemove(ctx, link)
	if err != nil {
		return res, err
	}
	if !ok {
		res.Message = "Nothing was removed."
		return res, nil
	}
	found, err := a.shell.opts.Folders.RemoveFolder(ctx, linkID)
	if err != nil {
		return res, err
	}
	if !found {
		return res, say("That folder isn't synced on this computer any more.")
	}
	res.Message = "Stopped syncing that folder. The files already copied stay in the project."
	return res, nil
}

func (a *App) foldersOpenProject(ctx context.Context, linkID string) (ActionResult, error) {
	var res ActionResult
	if a.shell.opts.Folders == nil {
		return res, say(noFolderSupportSentence)
	}
	doc := a.shell.FoldersDocument(ctx)
	link, ok := doc.Link(linkID)
	if !ok {
		return res, say("That folder isn't synced on this computer any more.")
	}
	if err := a.shell.opts.Folders.OpenProject(ctx, link.ProjectID); err != nil {
		return res, err
	}
	res.Message = "Opened the project in your browser."
	return res, nil
}
