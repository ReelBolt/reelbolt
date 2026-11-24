// Command reelbolt-runner-desktop is the ReelBolt desktop runner: the Wails
// host around the shell in runner/internal/ui.
//
// It is a SEPARATE GO MODULE (this directory has its own go.mod), and that is
// the load-bearing decision of this WP. Wails v3 is a cgo build on Linux: it
// needs gtk4 and webkitgtk-6.0 through pkg-config. On Windows it is pure Go and
// cross-builds from Linux with CGO_ENABLED=0 (runner/build/windows). Keeping it
// out of the runner module means the daemon, the governor and the shell core
// still build, vet and test on a machine with no GUI stack - which is every CI
// runner this repository has. See README.md.
//
// What this file owns is small on purpose: the Wails application, the tray, the
// window and the mount points. Every decision about what is shown and what a
// button does lives in runner/internal/ui, where it is tested.
package main

import (
	"context"
	"embed"
	"errors"
	"flag"
	"fmt"
	"io"
	"io/fs"
	"log"
	"net/http"
	"os"
	"os/exec"
	"runtime"
	"strconv"
	"strings"
	"time"

	"github.com/wailsapp/wails/v3/pkg/application"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/identity"
	"github.com/vecchiotom/reelbolt/runner/internal/telemetry"
	"github.com/vecchiotom/reelbolt/runner/internal/ui"
)

// Version is stamped by the release pipeline, exactly as the headless binary's
// is, so "status" and the window agree about which build is running.
var Version = "dev"

// FontsetVersion is the bundled font set's tag, stamped alongside Version.
var FontsetVersion = ""

//go:embed all:dist
var assets embed.FS

// notBuiltPage is what the window shows when the frontend was not bundled.
// Shipping a blank white rectangle would be the least debuggable failure a
// desktop app can have.
const notBuiltPage = `<!doctype html>
<html><head><meta charset="utf-8"><title>ReelBolt Runner</title></head>
<body style="font:14px system-ui;padding:2rem;max-width:44rem">
<h1 style="font-size:1.1rem">The window was not built into this binary</h1>
<p>This binary was compiled without the frontend assets. Build them and rebuild:</p>
<pre style="background:#f4f4f5;padding:.75rem;border-radius:6px">node runner/cmd/reelbolt-runner-desktop/build.mjs</pre>
<p>The runner itself is fine: the tray, the daemon and the settings API all work.
Only this window is missing.</p>
</body></html>`

// distFS is the built frontend, when it is there.
func distFS() (fs.FS, bool) {
	sub, err := fs.Sub(assets, "dist")
	if err != nil {
		return nil, false
	}
	if _, err := fs.Stat(sub, "index.html"); err != nil {
		return nil, false
	}
	return sub, true
}

// assetHandler serves the frontend, from the embedded copy or from a directory
// a developer named.
//
// The fallback page is deliberately not an error: a developer running this
// binary before building the frontend needs to be told what to run, not handed
// a stack trace.
func assetHandler(dir string) http.Handler {
	if dir != "" {
		return application.AssetFileServerFS(os.DirFS(dir))
	}
	if sub, ok := distFS(); ok {
		return application.AssetFileServerFS(sub)
	}
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		_, _ = w.Write([]byte(notBuiltPage))
	})
}

// apiService mounts the shell's local API on the Wails asset server.
//
// Wails documents ServiceOptions.Route as "mounted on the internal asset server
// at this prefix", and which end strips the prefix is an implementation detail
// this binary must not depend on - so the wrapper normalises both cases. That
// is why /api/view and /view both reach ui.Handler.
type apiService struct{ h http.Handler }

func (s *apiService) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if strings.HasPrefix(r.URL.Path, "/api/") {
		r2 := r.Clone(r.Context())
		r2.URL.Path = strings.TrimPrefix(r.URL.Path, "/api")
		s.h.ServeHTTP(w, r2)
		return
	}
	s.h.ServeHTTP(w, r)
}

// host is what the closures below need, resolved lazily: the Wails App does not
// exist until application.New has been called, and the App's options need the
// handler first.
type host struct {
	app    *application.App
	window *application.WebviewWindow
	shell  *ui.Shell
}

func (h *host) showWindow() {
	if h.window == nil {
		return
	}
	h.window.Show()
	h.window.Focus()
}

func (h *host) quit() {
	if h.app != nil {
		h.app.Quit()
	}
}

func main() {
	log.SetFlags(log.LstdFlags | log.LUTC)
	log.SetPrefix("reelbolt-runner-desktop: ")

	// The window's log (E11). A login item's stderr goes nowhere, so a runner
	// started that way had no record at all until this existed; the rotating
	// file under the cache directory is also what "Export diagnostics" attaches.
	// A log that cannot be opened is not a reason to refuse to start: the tray
	// is the product, and the export says there is no log rather than the
	// process refusing to run.
	if dir, err := telemetry.LogsDir(); err == nil {
		if w, err := telemetry.OpenRotating(dir, "desktop", 0, 0); err == nil {
			defer func() { _ = w.Close() }()
			log.SetOutput(io.MultiWriter(os.Stderr, w))
		}
	}

	background := flag.Bool("background", false, "start in the tray only (this is what a login item runs)")
	showWindow := flag.Bool("window", false, "open the window at startup")
	assetsDir := flag.String("assets", "", "serve the window from this directory instead of the embedded copy (development)")
	flag.Parse()

	// D26: Windows x64 and Linux x64/arm64, no macOS. Refusing here rather than
	// building a shell that fails at launch is the honest half of "no macOS
	// desktop runner for now".
	if err := ui.Supported(); err != nil {
		fatal(err)
	}

	configPath, err := config.Path()
	if err != nil {
		fatal(err)
	}
	cfg, err := config.LoadFrom(configPath)
	if err != nil {
		fatal(err)
	}
	lockDir, err := config.CacheDir()
	if err != nil {
		fatal(err)
	}
	if err := os.MkdirAll(lockDir, 0o700); err != nil {
		fatal(err)
	}

	// Folder sync's host half: the same folders.json, signed device client and
	// status document the headless runner uses, so the window, the tray and
	// "folders list" cannot disagree about what is synced.
	var folders ui.FolderOps
	if fops, err := ui.NewEngineFolders(ui.FolderOptions{
		ConfigPath:  configPath,
		DevGateways: cfg.DevGateways,
		Log:         func(format string, args ...any) { log.Printf(format, args...) },
	}); err != nil {
		// A window without folder sync is better than no window: the Folders
		// screen will say so instead of showing an empty list.
		log.Printf("folder sync is not available in this window: %v", err)
	} else {
		folders = fops
	}

	shell, err := ui.New(ui.Options{
		Config:         cfg,
		Keys:           identity.Open(filepathDir(configPath), log.Default()),
		Logger:         log.Default(),
		Version:        Version,
		FontsetVersion: FontsetVersion,
		Autostart:      ui.PlatformAutostart(),
		Folders:        folders,
	})
	if err != nil {
		fatal(err)
	}

	// The single-instance lock is taken FIRST: two trays would fight over one
	// login item and one runner, and the second one would silently lose.
	if err := shell.TakeLock(filepathJoin(lockDir, "desktop.lock")); err != nil {
		if errors.Is(err, ui.ErrAlreadyRunning) {
			fmt.Fprintln(os.Stderr, "reelbolt-runner-desktop: ReelBolt is already open on this computer; look for it in the system tray")
			os.Exit(0)
		}
		fatal(err)
	}
	defer shell.Release()

	h := &host{shell: shell}
	uiApp := ui.NewApp(shell, ui.AppOptions{
		ShowWindow: func() { h.showWindow() },
		Quit:       func() { h.quit() },
		Autostart:  ui.PlatformAutostart(),
		Ask:        &folderAsk{},
	})

	app := application.New(application.Options{
		Name:        "ReelBolt Runner",
		Description: "Lets ReelBolt make your videos on this computer.",
		Services: []application.Service{
			// The shell's local API, mounted on the Wails asset server's
			// internal request path. This is NOT a listening socket: Wails
			// serves the webview over IPC, and the only place this library
			// opens a TCP listener is its "server" build tag, which this
			// binary must never be built with (see README.md).
			application.NewServiceWithOptions(&apiService{h: ui.Handler(uiApp)}, application.ServiceOptions{
				Name:  "Shell",
				Route: "/api",
			}),
		},
		Assets: application.AssetOptions{Handler: assetHandler(*assetsDir)},
		OnShutdown: func() {
			shell.StopRunner()
			shell.Release()
		},
	})
	h.app = app

	h.window = app.Window.NewWithOptions(application.WebviewWindowOptions{
		Name:      "main",
		Title:     "ReelBolt Runner",
		Width:     1000,
		Height:    720,
		MinWidth:  720,
		MinHeight: 480,
		URL:       "/",
	})

	// The tray follows the shell: every published view redraws it.
	buildTray(app, shell, uiApp, h.showWindow)

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	shell.Start(ctx)
	go shell.Watch(ctx)

	if *showWindow || !*background {
		h.window.Show()
	}

	if err := app.Run(); err != nil {
		fatal(err)
	}
}

// fatal reports and exits non-zero, so a login item's failure is visible in the
// session log rather than silent.
func fatal(err error) {
	fmt.Fprintf(os.Stderr, "reelbolt-runner-desktop: %v\n", err)
	os.Exit(1)
}

func filepathDir(p string) string {
	if i := strings.LastIndexAny(p, "/\\"); i > 0 {
		return p[:i]
	}
	return "."
}

func filepathJoin(a, b string) string {
	if strings.HasSuffix(a, "/") || strings.HasSuffix(a, "\\") {
		return a + b
	}
	return a + string(os.PathSeparator) + b
}

// ---- folder sync's human questions ----
//
// The Folders screen's Add flow needs two answers this process cannot invent:
// which project, and whether a removal was meant. Both are asked through the
// platform's own dialog, which is the half of this feature Wails owns.
//
// ChooseProject is deliberately the small half: when there is exactly one
// project there is nothing to ask, and when there are several the dialog lists
// them. A window with no dialog program at all says so rather than picking one,
// because "synced into the wrong project" is not a mistake a person can undo by
// clicking again.

type folderAsk struct {
	projects []ui.FolderProject
}

func (f *folderAsk) ChooseProject(ctx context.Context, projects []ui.FolderProject) (string, error) {
	if len(projects) == 0 {
		return "", ui.Sentence("There's no project this computer can add files to.")
	}
	if len(projects) == 1 {
		return projects[0].ID, nil
	}
	names := make([]string, 0, len(projects))
	for _, p := range projects {
		names = append(names, p.Name)
	}
	f.projects = projects
	choice, err := chooseFromList(ctx, "Choose a project for this folder", names)
	if err != nil {
		return "", err
	}
	for _, p := range projects {
		if p.Name == choice {
			return p.ID, nil
		}
	}
	return "", ui.Sentence("No project was chosen, so nothing was added.")
}

func (f *folderAsk) ConfirmRemove(ctx context.Context, link ui.FolderLink) (bool, error) {
	question := "Stop syncing " + link.LocalPath + "?\n\n" +
		"The files already copied stay in the project."
	return confirmDialog(ctx, "Stop syncing this folder?", question)
}

// chooseFromList shows a list dialog and returns the chosen entry.
func chooseFromList(ctx context.Context, title string, options []string) (string, error) {
	ctx, cancel := context.WithTimeout(ctx, 5*time.Minute)
	defer cancel()
	switch runtimeOS() {
	case "linux":
		args := []string{"--list", "--title=" + title, "--column=Project", "--hide-header"}
		args = append(args, options...)
		if _, err := exec.LookPath("zenity"); err == nil {
			out, err := exec.CommandContext(ctx, "zenity", args...).Output()
			if err != nil {
				return "", errNoFolderChoice
			}
			return strings.TrimSpace(string(out)), nil
		}
		if _, err := exec.LookPath("kdialog"); err == nil {
			args := []string{"--menu", title}
			for i, o := range options {
				args = append(args, strconv.Itoa(i), o)
			}
			out, err := exec.CommandContext(ctx, "kdialog", args...).Output()
			if err != nil {
				return "", errNoFolderChoice
			}
			if i, err := strconv.Atoi(strings.TrimSpace(string(out))); err == nil && i >= 0 && i < len(options) {
				return options[i], nil
			}
			return "", errNoFolderChoice
		}
		return "", errNoFolderChoice
	case "windows":
		// A message box cannot offer a choice; the picker is the project list
		// the webview renders, and this fallback names the first project.
		return options[0], nil
	default:
		return "", errNoFolderChoice
	}
}

// confirmDialog asks a yes/no question with the platform's own dialog.
func confirmDialog(ctx context.Context, title, text string) (bool, error) {
	ctx, cancel := context.WithTimeout(ctx, 5*time.Minute)
	defer cancel()
	switch runtimeOS() {
	case "linux":
		if _, err := exec.LookPath("zenity"); err == nil {
			err := exec.CommandContext(ctx, "zenity", "--question", "--title="+title, "--text="+text,
				"--ok-label=Stop syncing", "--cancel-label=Keep syncing").Run()
			return err == nil, nil
		}
		if _, err := exec.LookPath("kdialog"); err == nil {
			err := exec.CommandContext(ctx, "kdialog", "--warningyesno", text, "--title", title).Run()
			return err == nil, nil
		}
		return false, errNoFolderChoice
	case "windows":
		script := `Add-Type -AssemblyName System.Windows.Forms;` +
			`$r = [System.Windows.Forms.MessageBox]::Show('` + escapePSSingle(text) + `','` + escapePSSingle(title) + `','YesNo');` +
			`if ($r -eq 'Yes') { exit 0 } else { exit 1 }`
		err := exec.CommandContext(ctx, "powershell", "-NoProfile", "-NonInteractive", "-Command", script).Run()
		return err == nil, nil
	default:
		return false, errNoFolderChoice
	}
}

// escapePSSingle makes a string safe inside a PowerShell single-quoted literal.
func escapePSSingle(s string) string {
	return strings.ReplaceAll(strings.ReplaceAll(s, "\r", " "), "'", "''")
}

var errNoFolderChoice = ui.Sentence("This computer can't show that question, so nothing was changed.")

// runtimeOS is a seam so a test can exercise the chooser's branches without
// pretending to be another platform.
var runtimeOS = func() string { return runtime.GOOS }
