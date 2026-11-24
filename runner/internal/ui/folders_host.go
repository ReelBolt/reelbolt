package ui

import (
	"context"
	"errors"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/deviceapi"
	"github.com/vecchiotom/reelbolt/runner/internal/foldersync"
	"github.com/vecchiotom/reelbolt/runner/internal/identity"
)

// The engine-backed FolderOps.
//
// This is the real implementation of the Folders screen: the same folders.json,
// the same signed device client and the same status document the headless
// runner uses, so the window, "folders list" and the tray cannot disagree. It
// lives in this package rather than in the host so it is testable without Wails,
// and the host is left with only the one thing it can do that this cannot: a
// platform folder chooser.

// FolderOptions configure the engine-backed folder operations.
type FolderOptions struct {
	// ConfigPath is the runner.json to read the device identity from. Required:
	// the device id and the API origin both come from it, and a runner that has
	// moved its configuration must be able to say so.
	ConfigPath string
	// APIOrigin overrides the origin from the configuration. A host that has
	// already resolved and validated one passes it here.
	APIOrigin string
	// StatusPath overrides where the engine publishes its status.
	StatusPath string
	// IndexDir overrides where per-link indexes live.
	IndexDir string
	// LinksPath overrides folders.json (a test sets it; production does not).
	LinksPath string
	// DevGateways are the development hosts this build accepts, from the
	// configuration.
	DevGateways []string
	// OpenURL opens the project page. Nil means the platform opener.
	OpenURL func(string) error
	// DashboardURL is where a project page lives. Empty means DefaultOrigin().
	DashboardURL string
	// Log receives progress lines. Nil means stderr.
	Log func(format string, args ...any)
}

// EngineFolders is a FolderOps backed by the folder-sync engine.
type EngineFolders struct {
	opts  FolderOptions
	store *foldersync.LinkStore
	log   func(format string, args ...any)
}

var _ FolderOps = (*EngineFolders)(nil)

// NewEngineFolders opens the local state folder sync uses.
func NewEngineFolders(opts FolderOptions) (*EngineFolders, error) {
	if opts.ConfigPath == "" {
		return nil, errors.New("ui: a configuration path is required for folder sync")
	}
	if opts.DashboardURL == "" {
		opts.DashboardURL = DefaultOrigin()
	}
	if opts.OpenURL == nil {
		opts.OpenURL = openURLInBrowser
	}
	if opts.Log == nil {
		opts.Log = func(string, ...any) {}
	}
	storePath := opts.LinksPath
	if storePath == "" {
		p, err := foldersync.DefaultLinksPath()
		if err != nil {
			return nil, err
		}
		storePath = p
	}
	statusPath := opts.StatusPath
	if statusPath == "" {
		dir, err := config.Dir()
		if err != nil {
			return nil, err
		}
		statusPath = filepath.Join(dir, foldersync.StatusFileName)
	}
	opts.StatusPath = statusPath
	return &EngineFolders{opts: opts, store: foldersync.OpenStore(storePath), log: opts.Log}, nil
}

// Store is the link list, for a host that wants to inspect it.
func (f *EngineFolders) Store() *foldersync.LinkStore { return f.store }

// PickerAvailable reports whether this machine has a folder-chooser program,
// without opening one.
func (f *EngineFolders) PickerAvailable() bool {
	switch runtime.GOOS {
	case "linux":
		for _, name := range []string{"zenity", "kdialog"} {
			if _, err := exec.LookPath(name); err == nil {
				return true
			}
		}
		return false
	case "windows":
		return true
	default:
		return false
	}
}

// ListFolders is the Folders screen's document.
func (f *EngineFolders) ListFolders(_ context.Context) (FoldersView, error) {
	view := FoldersView{Links: []FolderLink{}, StatusPath: f.opts.StatusPath}

	if _, apiErr := f.apiBase(); apiErr != nil {
		view.Problem = userMessage(apiErr)
		if !isPlain(apiErr) {
			f.log("folder sync: %v", apiErr)
			view.Problem = "This computer's ReelBolt address can't be used for folder sync. Pair it again to fix it."
		}
	} else {
		view.Ready = true
	}

	links, err := f.store.Load()
	if err != nil {
		return view, err
	}
	// The engine's published status, when a runner is writing one. A stale file
	// (a runner that stopped) is not treated as current.
	var live foldersync.Status
	haveLive := false
	if st, fresh, err := foldersync.ReadStatus(f.opts.StatusPath, 3*time.Minute); err == nil && fresh {
		live, haveLive = st, true
		view.Running = true
	}
	if haveLive && live.Problem != "" {
		view.Ready = false
		view.Problem = live.Problem
	}
	view.Summary = foldersync.SummaryOf(live, haveLive, view.Problem)

	byID := map[string]foldersync.LinkStatus{}
	for _, s := range live.Links {
		byID[s.LinkID] = s
	}
	for _, l := range links {
		link := FolderLink{
			LinkID: l.LinkID, LocalPath: l.LocalPath, ProjectID: l.ProjectID,
			ProjectName: l.ProjectName, TargetFolder: l.TargetFolder,
			ProjectURL: f.projectURL(l.ProjectID),
			State:      "uptodate", Headline: "Up to date",
		}
		if s, ok := byID[l.LinkID]; ok {
			link.State, link.Headline, link.Reason = s.State, s.Headline, s.Reason
			link.PausedBy, link.CanConfirmDelete = s.PausedBy, s.CanConfirmDelete
			link.FileCount, link.Pending = s.FileCount, s.Pending
			link.Note = s.Note
			if s.LastChangeAt != nil {
				link.LastChangeAt = s.LastChangeAt.Format(time.RFC3339)
			}
			if s.LastSyncAt != nil {
				link.LastSyncAt = s.LastSyncAt.Format(time.RFC3339)
			}
			for _, g := range s.Skipped {
				link.Skipped = append(link.Skipped, SkippedGroup{Reason: g.Reason, Count: g.Count, Examples: g.Examples})
			}
		} else if l.Paused {
			link.State, link.Headline = "paused", "Paused"
			link.PausedBy, link.Reason = "computer", "Paused on this computer"
		}
		link.Skipped = folderSkipGroups(link.Skipped)
		view.Links = append(view.Links, link)
	}
	return view, nil
}

// ListProjects is the projects this device may sync into.
func (f *EngineFolders) ListProjects(ctx context.Context) ([]FolderProject, error) {
	client, err := f.client()
	if err != nil {
		return nil, err
	}
	projects, err := client.Projects(ctx)
	if err != nil {
		return nil, folderListMessage(err)
	}
	out := make([]FolderProject, 0, len(projects))
	for _, p := range projects {
		out = append(out, FolderProject{ID: p.ID, Name: p.Name})
	}
	return out, nil
}

// AddFolder creates the link in ReelBolt and records it here.
func (f *EngineFolders) AddFolder(ctx context.Context, req AddFolderRequest) (FolderLink, error) {
	client, err := f.client()
	if err != nil {
		return FolderLink{}, err
	}
	target := strings.TrimSpace(req.TargetFolder)
	if target == "" {
		target = DefaultFolderTarget(req.LocalPath)
	}
	link, err := client.CreateLink(ctx, deviceapi.CreateLinkRequest{
		ProjectID: req.ProjectID, TargetFolder: target, DisplayPath: req.LocalPath,
	})
	if err != nil {
		return FolderLink{}, err
	}
	rec := foldersync.Link{
		LinkID: link.ID, LocalPath: req.LocalPath, ProjectID: link.ProjectID,
		ProjectName: link.ProjectName, TargetFolder: link.TargetFolder,
	}
	if err := f.store.Add(rec); err != nil {
		return FolderLink{}, err
	}
	f.log("folder sync: added %s (link %s)", req.LocalPath, link.ID)
	return FolderLink{
		LinkID: link.ID, LocalPath: req.LocalPath, ProjectID: link.ProjectID,
		ProjectName: link.ProjectName, TargetFolder: link.TargetFolder,
		ProjectURL: f.projectURL(link.ProjectID), State: "syncing", Headline: "Starting",
	}, nil
}

// PauseFolder pauses a link on this computer.
func (f *EngineFolders) PauseFolder(_ context.Context, linkID string) error {
	err := f.store.Modify(linkID, func(l *foldersync.Link) {
		l.Paused = true
		l.ConfirmLargeDelete = false
	})
	if err == nil {
		f.log("folder sync: paused link %s", linkID)
	}
	return err
}

// ResumeFolder resumes a link. confirmDelete answers the mass-delete guard.
func (f *EngineFolders) ResumeFolder(_ context.Context, linkID string, confirmDelete bool) error {
	err := f.store.Modify(linkID, func(l *foldersync.Link) {
		l.Paused = false
		l.ConfirmLargeDelete = confirmDelete
	})
	if err == nil {
		f.log("folder sync: resumed link %s (confirm delete: %t)", linkID, confirmDelete)
	}
	return err
}

// RemoveFolder stops syncing a link. The files already in the project stay.
func (f *EngineFolders) RemoveFolder(ctx context.Context, linkID string) (bool, error) {
	// Tell ReelBolt first, so the dashboard stops listing this computer. A link
	// already gone there must not stop the person removing it HERE.
	if client, err := f.client(); err == nil {
		callCtx, cancel := context.WithTimeout(context.WithoutCancel(ctx), 30*time.Second)
		derr := client.DeleteLink(callCtx, linkID)
		cancel()
		if derr != nil && !errors.Is(derr, deviceapi.ErrLinkNotFound) && !errors.Is(derr, deviceapi.ErrNotConnected) {
			return false, folderListMessage(derr)
		}
	}
	found, err := f.store.Remove(linkID)
	if err == nil && found {
		f.log("folder sync: removed link %s", linkID)
	}
	return found, err
}

// OpenProject opens the project in the browser.
func (f *EngineFolders) OpenProject(_ context.Context, projectID string) error {
	url := f.projectURL(projectID)
	if url == "" {
		return say("There's no ReelBolt address to open the project at.")
	}
	if err := f.opts.OpenURL(url); err != nil {
		return sayWrap("Couldn't open your browser. The project is at "+url+".", err)
	}
	return nil
}

// ChooseFolder opens the platform's folder chooser.
//
// It is the one thing here that a headless test cannot do, which is exactly why
// it returns ErrNoFolderPicker rather than a path: a window that silently
// substituted a folder nobody chose would be worse than one that says it cannot
// ask.
func (f *EngineFolders) ChooseFolder(ctx context.Context) (string, error) {
	path, err := chooseFolder(ctx)
	if errors.Is(err, errNoChooser) {
		return "", ErrNoFolderPicker
	}
	if err != nil {
		return "", err
	}
	return path, nil
}

// errNoChooser means this machine has no usable folder-chooser program.
var errNoChooser = errors.New("no folder chooser")

// chooseFolder runs the platform's own picker. Nothing is installed and nothing
// is downloaded: when the program is absent, this reports that it is absent.
func chooseFolder(ctx context.Context) (string, error) {
	ctx, cancel := context.WithTimeout(ctx, 5*time.Minute)
	defer cancel()
	switch runtime.GOOS {
	case "linux":
		for _, candidate := range [][]string{
			{"zenity", "--file-selection", "--directory", "--title=Choose a folder to sync"},
			{"kdialog", "--getexistingdirectory", "--title", "Choose a folder to sync"},
		} {
			if _, err := exec.LookPath(candidate[0]); err != nil {
				continue
			}
			return runChooser(ctx, candidate[0], candidate[1:]...)
		}
		return "", errNoChooser
	case "windows":
		script := `Add-Type -AssemblyName System.Windows.Forms;` +
			`$d = New-Object System.Windows.Forms.FolderBrowserDialog;` +
			`$d.Description = 'Choose a folder to sync';` +
			`if ($d.ShowDialog() -eq 'OK') { Write-Output $d.SelectedPath }`
		return runChooser(ctx, "powershell", "-NoProfile", "-NonInteractive", "-Command", script)
	default:
		return "", errNoChooser
	}
}

func runChooser(ctx context.Context, name string, args ...string) (string, error) {
	cmd := exec.CommandContext(ctx, name, args...)
	out, err := cmd.Output()
	if err != nil {
		// A person who closed the chooser without choosing is not an error
		// worth a red banner, but it is also not a path.
		return "", errNoChooser
	}
	path := strings.TrimSpace(string(out))
	if path == "" {
		return "", errNoChooser
	}
	return path, nil
}

// apiBase resolves the origin folder sync signs for, refusing to guess.
func (f *EngineFolders) apiBase() (string, error) {
	if strings.TrimSpace(f.opts.APIOrigin) != "" {
		return config.CheckAPIBase(f.opts.APIOrigin, f.opts.DevGateways)
	}
	cfg, err := config.LoadFrom(f.opts.ConfigPath)
	if err != nil {
		return "", err
	}
	if cfg.APIBaseURL == "" {
		return "", say("Pair this computer again to turn on folder sync.")
	}
	return config.CheckAPIBase(cfg.APIBaseURL, append(cfg.DevGateways, f.opts.DevGateways...))
}

// client builds the signed client from the configuration on disk, so a re-pair
// or a "config set-api" is picked up without rebuilding this.
func (f *EngineFolders) client() (*deviceapi.Client, error) {
	cfg, err := config.LoadFrom(f.opts.ConfigPath)
	if err != nil {
		return nil, err
	}
	if cfg.DeviceID == "" {
		return nil, say("This computer isn't paired yet. Pair it first.")
	}
	origin, err := f.apiBase()
	if err != nil {
		return nil, err
	}
	store := identity.Open(filepath.Dir(f.opts.ConfigPath), nil)
	key, err := store.Load(cfg.DeviceID)
	if err != nil {
		return nil, sayWrap("This computer's sign-in couldn't be read. Pair it again.", err)
	}
	return deviceapi.New(origin, cfg.DeviceID, key), nil
}

func (f *EngineFolders) projectURL(projectID string) string {
	if projectID == "" {
		return ""
	}
	base := strings.TrimRight(f.opts.DashboardURL, "/")
	return base + "/app/projects/" + projectID
}

// folderListMessage turns a device API refusal into a sentence.
func folderListMessage(err error) error {
	switch {
	case isPlain(err):
		return err
	case errors.Is(err, deviceapi.ErrNotConnected):
		return sayWrap("This computer isn't connected to ReelBolt any more. Pair it again.", err)
	case errors.Is(err, deviceapi.ErrOwnerLacksAccess):
		return sayWrap("The person who paired this computer can't edit this project any more.", err)
	case errors.Is(err, deviceapi.ErrNotFound):
		return sayWrap("That project wasn't found, or this computer can't edit it.", err)
	default:
		return sayWrap("ReelBolt couldn't do that right now. Check your internet connection and try again.", err)
	}
}

// openURLInBrowser is the default opener. It is intentionally the platform's
// own: this process holds a private key, and a shell escape here would be a
// way to run a command with it in the environment.
func openURLInBrowser(url string) error {
	// The URL is always one this package built from the configured dashboard
	// origin, so it cannot carry a shell metacharacter from the server; the
	// check is kept anyway, because this is the boundary where a URL becomes an
	// argv entry.
	if strings.ContainsAny(url, "\x00\n\r") {
		return errors.New("that address cannot be opened")
	}
	var cmd *exec.Cmd
	switch runtime.GOOS {
	case "windows":
		cmd = exec.CommandContext(context.Background(), "rundll32", "url.dll,FileProtocolHandler", url)
	case "darwin":
		cmd = exec.CommandContext(context.Background(), "open", url)
	default:
		cmd = exec.CommandContext(context.Background(), "xdg-open", url)
	}
	if err := cmd.Start(); err != nil {
		return err
	}
	go func() { _ = cmd.Wait() }()
	return nil
}
