package main

import (
	"context"
	"crypto/ed25519"
	"errors"
	"flag"
	"fmt"
	"log"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/deviceapi"
	"github.com/vecchiotom/reelbolt/runner/internal/foldersync"
)

// Folder sync from the command line.
//
// The headless runner runs the same engine the desktop app does; these commands
// are the parts a person does by hand (add, list, pause, remove) plus the wiring
// that starts the engine beside the job loop. Everything here reads and writes
// the same folders.json the app does, through folderStore.

// notPairedForFolders is what every folder command says when this machine has
// no API origin. It is a plain sentence with the one action that fixes it:
// "apiBaseUrl is empty" is not something a person can act on. An EMPTY value is
// never guessed into a default — a device paired before folder sync existed has
// none, and sending its signature to a host it never agreed to would be wrong.
const notPairedForFolders = "Pair this computer again to turn on folder sync"

// errNoAPIOrigin means the configuration has no apiBaseUrl.
var errNoAPIOrigin = errors.New(notPairedForFolders) //nolint:staticcheck // user-facing sentence, capitalised on purpose

// folderStore opens this machine's link list.
func folderStore() (*foldersync.LinkStore, error) {
	path, err := foldersync.DefaultLinksPath()
	if err != nil {
		return nil, err
	}
	return foldersync.OpenStore(path), nil
}

// folderStatusPath is where a running engine publishes its status, next to
// runner.json so "status" and "folders list" find the same file whatever the
// process's cache directory is.
func folderStatusPath() (string, error) {
	dir, err := config.Dir()
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, foldersync.StatusFileName), nil
}

// apiOrigin is the origin folder sync signs requests for.
func apiOrigin(cfg *config.Config) (string, error) {
	if strings.TrimSpace(cfg.APIBaseURL) == "" {
		return "", errNoAPIOrigin
	}
	origin, err := config.CheckAPIBase(cfg.APIBaseURL, cfg.DevGateways)
	if err != nil {
		return "", err
	}
	return origin, nil
}

// deviceKey loads the Ed25519 key this device signs with.
func deviceKey(e *env) (ed25519.PrivateKey, error) {
	if e.cfg.DeviceID == "" {
		return nil, errNotPaired
	}
	key, err := e.keys.Load(e.cfg.DeviceID)
	if err != nil {
		return nil, fmt.Errorf("the device key could not be read: %w", err)
	}
	return key, nil
}

// deviceClient builds the signed client folder sync uses.
func deviceClient(e *env) (*deviceapi.Client, error) {
	origin, err := apiOrigin(e.cfg)
	if err != nil {
		return nil, err
	}
	key, err := deviceKey(e)
	if err != nil {
		return nil, err
	}
	return deviceapi.New(origin, e.cfg.DeviceID, key), nil
}

// syncUnavailable explains, in one sentence, why the engine cannot run here.
// Nil when it can.
func syncUnavailable(e *env) error {
	if _, err := apiOrigin(e.cfg); err != nil {
		return err
	}
	if e.cfg.DeviceID == "" {
		return errNotPaired
	}
	return nil
}

// newFolderEngine builds the engine the daemon runs.
func newFolderEngine(e *env, logger *log.Logger) (*foldersync.Engine, error) {
	store, err := folderStore()
	if err != nil {
		return nil, err
	}
	indexDir, err := foldersync.DefaultIndexDir()
	if err != nil {
		return nil, err
	}
	statusPath, err := folderStatusPath()
	if err != nil {
		return nil, err
	}
	opts := foldersync.Options{
		Store:      store,
		IndexDir:   indexDir,
		StatusPath: statusPath,
		Logger:     logger,
	}
	if problem := syncUnavailable(e); problem != nil {
		// The engine still runs: it must be able to SAY folder sync is off, and
		// to notice a later "config set-api" without restarting the runner.
		opts.Unavailable = problem.Error()
	} else if client, err := deviceClient(e); err != nil {
		opts.Unavailable = err.Error()
	} else {
		opts.API = client
	}
	return foldersync.New(opts)
}

// cmdFolders dispatches the folders subcommands.
func cmdFolders(args []string) error {
	if len(args) == 0 {
		return errors.New("usage: reelbolt-runner folders list|add|remove|pause|resume")
	}
	switch args[0] {
	case "list", "ls":
		return cmdFoldersList(args[1:])
	case "add":
		return cmdFoldersAdd(args[1:])
	case "remove", "rm":
		return cmdFoldersRemove(args[1:])
	case "pause":
		return cmdFoldersPause(args[1:])
	case "resume":
		return cmdFoldersResume(args[1:])
	default:
		return fmt.Errorf("unknown folders subcommand %q (try: list, add, remove, pause, resume)", args[0])
	}
}

// ---- folders list ----

type foldersListFlags struct {
	asJSON bool
}

func newFoldersListFlags(o *foldersListFlags) *flag.FlagSet {
	fs := flagSet("folders list", "show the folders this computer syncs")
	fs.BoolVar(&o.asJSON, "json", false, "print JSON")
	return fs
}

func cmdFoldersList(args []string) error {
	var o foldersListFlags
	fs := newFoldersListFlags(&o)
	_ = fs.Parse(args)

	store, err := folderStore()
	if err != nil {
		return err
	}
	links, err := store.Load()
	if err != nil {
		return err
	}
	// The live status, when a runner is publishing one. A stale document (a
	// daemon that stopped) is ignored rather than shown as current.
	var live foldersync.Status
	haveLive := false
	statusPath := ""
	if path, err := folderStatusPath(); err == nil {
		statusPath = path
		if st, fresh, err := foldersync.ReadStatus(path, 3*time.Minute); err == nil && fresh {
			live, haveLive = st, true
		}
	}

	if o.asJSON {
		rows := make([]foldersReportRow, 0, len(links))
		for _, l := range links {
			rows = append(rows, foldersRow(l, live, haveLive))
		}
		rep := foldersReport{Links: rows, Problem: live.Problem, StatusPath: statusPath}
		if haveLive {
			rep.StatusAt = live.UpdatedAt.Format(time.RFC3339)
		}
		return printJSON(rep)
	}

	if len(links) == 0 {
		fmt.Println("No folders are synced on this computer.")
		fmt.Println("Add one with: reelbolt-runner folders add <path> --project <project-id>")
		return nil
	}
	if haveLive && live.Problem != "" {
		fmt.Printf("Folder sync is not running: %s\n\n", live.Problem)
	}
	for _, l := range links {
		printFolder(l, live, haveLive)
	}
	return nil
}

// foldersReport is the machine-readable form of "folders list".
type foldersReport struct {
	Links      []foldersReportRow `json:"links"`
	Problem    string             `json:"problem,omitempty"`
	StatusAt   string             `json:"statusAt,omitempty"`
	StatusPath string             `json:"statusPath,omitempty"`
}

type foldersReportRow struct {
	LinkID       string `json:"linkId"`
	LocalPath    string `json:"localPath"`
	ProjectID    string `json:"projectId"`
	ProjectName  string `json:"projectName,omitempty"`
	TargetFolder string `json:"targetFolder"`
	Paused       bool   `json:"paused"`
	// Live is the running engine's row, when one is publishing. It is absent for
	// a link no runner is working on, which is not the same as "up to date".
	State    string `json:"state,omitempty"`
	Headline string `json:"headline,omitempty"`
	Reason   string `json:"reason,omitempty"`
	PausedBy string `json:"pausedBy,omitempty"`
	Files    int    `json:"files,omitempty"`
	LastSync string `json:"lastSyncAt,omitempty"`
	// LastError is the reason a link needs attention, spelled the same way the
	// window spells it.
	LastError string `json:"lastError,omitempty"`
}

func foldersRow(l foldersync.Link, live foldersync.Status, haveLive bool) foldersReportRow {
	row := foldersReportRow{
		LinkID: l.LinkID, LocalPath: l.LocalPath, ProjectID: l.ProjectID,
		ProjectName: l.ProjectName, TargetFolder: l.TargetFolder, Paused: l.Paused,
	}
	if !haveLive {
		return row
	}
	for _, s := range live.Links {
		if s.LinkID != l.LinkID {
			continue
		}
		row.State, row.Headline, row.Reason, row.PausedBy = s.State, s.Headline, s.Reason, s.PausedBy
		row.Files = s.FileCount
		if s.LastSyncAt != nil {
			row.LastSync = s.LastSyncAt.Format(time.RFC3339)
		}
		if s.State == foldersync.StateAttention {
			row.LastError = s.Reason
		}
	}
	return row
}

func printFolder(l foldersync.Link, live foldersync.Status, haveLive bool) {
	name := l.ProjectName
	if name == "" {
		name = l.ProjectID
	}
	fmt.Printf("%s  %s\n", shortFoldersID(l.LinkID), l.LocalPath)
	fmt.Printf("    project  %s\n", name)
	fmt.Printf("    folder   %s (in the project)\n", l.TargetFolder)
	switch {
	case l.Paused:
		fmt.Printf("    status   Paused on this computer\n")
	case haveLive && live.Problem != "":
		fmt.Printf("    status   Needs attention (%s)\n", live.Problem)
	case haveLive:
		printed := false
		for _, s := range live.Links {
			if s.LinkID != l.LinkID {
				continue
			}
			printed = true
			fmt.Printf("    status   %s\n", folderStatusLine(s))
			fmt.Printf("    files    %d\n", s.FileCount)
			if s.LastChangeAt != nil {
				fmt.Printf("    changed  %s\n", s.LastChangeAt.Local().Format(time.RFC3339))
			}
			for _, g := range s.Skipped {
				fmt.Printf("    skipped  %d: %s\n", g.Count, g.Reason)
			}
		}
		if !printed {
			fmt.Printf("    status   Starting\n")
		}
	default:
		fmt.Printf("    status   not running (start it with \"reelbolt-runner run\")\n")
	}
}

// folderStatusLine is the design's wording: Up to date / Syncing N files /
// Paused (reason) / Needs attention (reason).
func folderStatusLine(s foldersync.LinkStatus) string {
	switch s.State {
	case foldersync.StateUpToDate:
		return "Up to date"
	case foldersync.StateSyncing:
		if s.Pending > 0 {
			return fmt.Sprintf("Syncing %d %s", s.Pending, pluralWord(s.Pending, "file", "files"))
		}
		return "Checking the folder"
	case foldersync.StatePaused:
		if s.Reason != "" {
			return "Paused (" + s.Reason + ")"
		}
		return "Paused"
	case foldersync.StateAttention:
		if s.Reason != "" {
			return "Needs attention (" + s.Reason + ")"
		}
		return "Needs attention"
	default:
		if s.Headline != "" {
			return s.Headline
		}
		return s.State
	}
}

func pluralWord(n int, one, many string) string {
	if n == 1 {
		return one
	}
	return many
}

func shortFoldersID(id string) string {
	if len(id) <= 8 {
		return id
	}
	return id[:8]
}

// ---- folders add ----

type foldersAddFlags struct {
	project string
	target  string
	api     string
}

func newFoldersAddFlags(o *foldersAddFlags) *flag.FlagSet {
	fs := flagSet("folders add", "sync a folder on this computer into a ReelBolt project")
	fs.StringVar(&o.project, "project", "", "the project id to copy files into (required)")
	fs.StringVar(&o.target, "target", "", "the folder name inside the project (default: the local folder's name)")
	fs.StringVar(&o.api, "api", "", "API origin to use instead of runner.json")
	return fs
}

func cmdFoldersAdd(args []string) error {
	var o foldersAddFlags
	fs := newFoldersAddFlags(&o)
	_ = fs.Parse(args)
	if o.project == "" {
		return errors.New("name the project to sync into: folders add <path> --project <project-id>")
	}
	if fs.NArg() != 1 {
		return errors.New("name exactly one folder to sync")
	}
	local := strings.TrimSpace(fs.Arg(0))
	if local == "" {
		return errors.New("name the folder to sync")
	}
	abs, err := filepath.Abs(local)
	if err != nil {
		return err
	}
	info, err := os.Stat(abs)
	if err != nil {
		return fmt.Errorf("that folder cannot be read: %w", err)
	}
	if !info.IsDir() {
		return fmt.Errorf("%s is a file, not a folder", abs)
	}

	e, err := loadEnv(func(c *config.Config) {
		if o.api != "" {
			c.APIBaseURL = o.api
		}
	})
	if err != nil {
		return err
	}
	client, err := deviceClient(e)
	if err != nil {
		return err
	}
	target := o.target
	if target == "" {
		target = filepath.Base(abs)
	}

	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	// The server assigns the id and validates the target folder name. The local
	// path goes as the display string and is never sent any other way.
	link, err := client.CreateLink(ctx, deviceapi.CreateLinkRequest{
		ProjectID: o.project, TargetFolder: target, DisplayPath: abs,
	})
	if err != nil {
		return foldersAddError(err)
	}
	store, err := folderStore()
	if err != nil {
		return err
	}
	rec := foldersync.Link{
		LinkID: link.ID, LocalPath: abs, ProjectID: link.ProjectID,
		ProjectName: link.ProjectName, TargetFolder: link.TargetFolder,
	}
	if err := store.Add(rec); err != nil {
		return err
	}
	fmt.Printf("Syncing %s into %s as %s (link %s).\n", abs, projectName(link), link.TargetFolder, shortFoldersID(link.ID))
	fmt.Println("Start the runner with \"reelbolt-runner run\" to copy files now.")
	return nil
}

func foldersAddError(err error) error {
	switch {
	case errors.Is(err, deviceapi.ErrTargetTaken):
		return errors.New("that folder name is already used in the project; pick another with --target")
	case errors.Is(err, deviceapi.ErrInvalidTarget):
		return errors.New("that folder name is not allowed in a project; pick another with --target")
	case errors.Is(err, deviceapi.ErrNotFound):
		return errors.New("that project was not found, or this computer cannot edit it")
	case errors.Is(err, deviceapi.ErrOwnerLacksAccess):
		return errors.New("the person who paired this computer can no longer edit that project")
	case errors.Is(err, deviceapi.ErrNotConnected):
		return errors.New("this computer is no longer connected to ReelBolt — pair it again")
	default:
		return err
	}
}

func projectName(l deviceapi.Link) string {
	if l.ProjectName != "" {
		return l.ProjectName
	}
	return l.ProjectID
}

// ---- folders remove / pause / resume ----

func newFoldersRemoveFlags() *flag.FlagSet {
	return flagSet("folders remove", "stop syncing a folder (files already copied stay in the project)")
}

func cmdFoldersRemove(args []string) error {
	fs := newFoldersRemoveFlags()
	_ = fs.Parse(args)
	id, store, err := resolveFolder(fs)
	if err != nil {
		return err
	}
	// Tell ReelBolt first, so the dashboard stops showing this computer as the
	// one syncing the folder. A link already gone, or a computer that is
	// offline, must not stop the person from removing it HERE.
	if e, err := loadEnv(nil); err == nil {
		if client, cerr := deviceClient(e); cerr == nil {
			ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
			derr := client.DeleteLink(ctx, id.LinkID)
			cancel()
			if derr != nil && !errors.Is(derr, deviceapi.ErrLinkNotFound) && !errors.Is(derr, deviceapi.ErrNotConnected) {
				return derr
			}
		}
	}
	if _, err := store.Remove(id.LinkID); err != nil {
		return err
	}
	fmt.Printf("Stopped syncing %s. The files already copied stay in the project.\n", id.LocalPath)
	return nil
}

func cmdFoldersPause(args []string) error {
	fs := flagSet("folders pause", "stop copying a folder until you resume it")
	_ = fs.Parse(args)
	id, store, err := resolveFolder(fs)
	if err != nil {
		return err
	}
	if err := store.Modify(id.LinkID, func(l *foldersync.Link) {
		l.Paused = true
		// Resuming from a plain pause must not silently apply deletions the
		// guard held back; only --confirm-large-delete does that.
		l.ConfirmLargeDelete = false
	}); err != nil {
		return err
	}
	fmt.Printf("Paused %s. Nothing is copied from it until you resume.\n", id.LocalPath)
	return nil
}

func cmdFoldersResume(args []string) error {
	var confirm bool
	fs := flagSet("folders resume", "take a paused folder again")
	fs.BoolVar(&confirm, "confirm-large-delete", false,
		"confirm that files really were removed, allowing the deletions the mass-delete guard held back")
	_ = fs.Parse(args)
	id, store, err := resolveFolder(fs)
	if err != nil {
		return err
	}
	if err := store.Modify(id.LinkID, func(l *foldersync.Link) {
		l.Paused = false
		l.ConfirmLargeDelete = confirm
	}); err != nil {
		return err
	}
	fmt.Printf("Resumed %s.\n", id.LocalPath)
	if confirm {
		fmt.Println("The next check will apply the deletions the guard held back.")
	}
	return nil
}

// resolveFolder finds the link the command names, from the flag set's remaining
// argument (an id, or a unique prefix of one).
func resolveFolder(fs *flag.FlagSet) (foldersync.Link, *foldersync.LinkStore, error) {
	store, err := folderStore()
	if err != nil {
		return foldersync.Link{}, nil, err
	}
	if fs.NArg() != 1 {
		return foldersync.Link{}, nil, errors.New("name one synced folder by its id (see \"reelbolt-runner folders list\")")
	}
	links, err := store.Load()
	if err != nil {
		return foldersync.Link{}, nil, err
	}
	link, err := foldersync.Find(links, fs.Arg(0))
	if err != nil {
		return foldersync.Link{}, nil, err
	}
	return link, store, nil
}

// ---- config ----

func cmdConfig(args []string) error {
	if len(args) == 0 {
		return errors.New("usage: reelbolt-runner config set-api <url> | config show")
	}
	switch args[0] {
	case "set-api":
		return cmdConfigSetAPI(args[1:])
	case "show":
		return cmdConfigShow(args[1:])
	default:
		return fmt.Errorf("unknown config subcommand %q (try: set-api, show)", args[0])
	}
}

// cmdConfigSetAPI is the escape hatch the owner's decision names: pairing
// records the origin, and this sets it by hand when a device predates that.
func cmdConfigSetAPI(args []string) error {
	var clear bool
	fs := flagSet("config set-api", "set the origin folder sync signs its requests for")
	fs.BoolVar(&clear, "clear", false, "forget the origin, so folder sync asks you to pair this computer again")
	_ = fs.Parse(args)

	e, err := loadEnv(nil)
	if err != nil {
		return err
	}
	if clear {
		e.cfg.APIBaseURL = ""
		if err := e.cfg.SaveTo(e.cfgPath); err != nil {
			return fmt.Errorf("write %s: %w", e.cfgPath, err)
		}
		fmt.Printf("Cleared apiBaseUrl in %s. Folder sync will ask you to pair this computer again.\n", e.cfgPath)
		return nil
	}
	if fs.NArg() != 1 {
		return errors.New("usage: reelbolt-runner config set-api <url>  (or --clear)")
	}
	raw := strings.TrimSpace(fs.Arg(0))
	// Validate BEFORE writing, with the same rule the daemon applies at load: a
	// value that cannot be used should never reach the file.
	origin, err := config.CheckAPIBase(raw, e.cfg.DevGateways)
	if err != nil {
		return err
	}
	e.cfg.APIBaseURL = origin
	if err := e.cfg.SaveTo(e.cfgPath); err != nil {
		return fmt.Errorf("write %s: %w", e.cfgPath, err)
	}
	fmt.Printf("Folder sync will use %s.\n", origin)
	fmt.Printf("It was written to %s. Start the runner if it is not already running.\n", e.cfgPath)
	return nil
}

type configShowFlags struct {
	asJSON bool
}

func cmdConfigShow(args []string) error {
	var o configShowFlags
	fs := flagSet("config show", "show the local configuration")
	fs.BoolVar(&o.asJSON, "json", false, "print JSON")
	_ = fs.Parse(args)

	e, err := loadEnv(nil)
	if err != nil {
		return err
	}
	origin, apiErr := apiOrigin(e.cfg)
	row := configRow{
		ConfigPath: e.cfgPath,
		DeviceID:   e.cfg.DeviceID,
		GatewayURL: e.cfg.GatewayURL,
		APIBaseURL: origin,
		FolderSync: "ready",
	}
	if apiErr != nil {
		row.FolderSync = apiErr.Error()
	}
	if o.asJSON {
		return printJSON(row)
	}
	fmt.Printf("config      %s\n", row.ConfigPath)
	fmt.Printf("device      %s\n", presentOrDash(row.DeviceID))
	fmt.Printf("gateway     %s\n", presentOrDash(row.GatewayURL))
	fmt.Printf("api         %s\n", presentOrDash(row.APIBaseURL))
	fmt.Printf("folder sync %s\n", row.FolderSync)
	return nil
}

type configRow struct {
	ConfigPath string `json:"configPath"`
	DeviceID   string `json:"deviceId,omitempty"`
	GatewayURL string `json:"gatewayUrl,omitempty"`
	APIBaseURL string `json:"apiBaseUrl,omitempty"`
	FolderSync string `json:"folderSync"`
}

func presentOrDash(s string) string {
	if s == "" {
		return "(none)"
	}
	return s
}
