package ui

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"errors"
	"fmt"
	"log"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/pairing"
	"github.com/vecchiotom/reelbolt/runner/internal/telemetry"
)

// DefaultOrigin is the ReelBolt origin pairing, the device API and job links use when nothing
// more specific is recorded (config.ServiceOrigin, stamped per build).
func DefaultOrigin() string { return config.Origin() }

// DefaultRuntimeHelpURL is where "Set up video rendering" sends someone with no Docker or Podman:
// the desktop runner page of the user guide, which explains the choice.
func DefaultRuntimeHelpURL() string { return config.HelpURL() }

// AppOptions are the host's own capabilities, passed in so this package can be
// tested without a browser, a window or a process to quit.
type AppOptions struct {
	// OpenURL opens an address in the user's browser. Nil means the platform
	// opener the pairing client already uses.
	OpenURL func(string) error
	// ShowWindow brings the window to the front.
	ShowWindow func()
	// Quit asks the host to exit.
	Quit func()
	// Autostart overrides the platform login-item manager.
	Autostart Autostart
	// DashboardURL is the page "Open dashboard" opens.
	DashboardURL string
	// RuntimeHelpURL is where "Set up video rendering" sends a person who has
	// no working Docker or Podman. The first-run wizard shows the same link,
	// so the two cannot drift.
	RuntimeHelpURL string
	// PairingBaseURL overrides the origin the pairing routes are reached at.
	// It exists for a development stack; a release build leaves it empty and
	// gets DefaultOrigin().
	PairingBaseURL string
	// Logger receives pairing progress lines.
	Logger *log.Logger
	// Ask is how the host asks the person a question the window's action
	// cannot answer by itself: which project, and whether to remove a folder.
	// Nil means those actions refuse with a sentence rather than guessing.
	Ask FolderAsker
	// LocalModel overrides the host behind the Local model screen. Nil means
	// the running daemon's relay.
	LocalModel LocalModelHost
}

// ActionResult is what the window gets back from an action: the view after it,
// and one sentence for the user.
type ActionResult struct {
	OK      bool   `json:"ok"`
	Message string `json:"message,omitempty"`
	// Screen tells the window which screen to show, when the action is a
	// navigation rather than a change.
	Screen string `json:"screen,omitempty"`
	View   View   `json:"view"`
}

// App is the shell's whole command surface.
//
// It is ONE method - Do(action, arg) - rather than a method per button, and
// that is deliberate: the tray menu, the window and the keyboard all dispatch
// through the same switch, so "the Pause button does nothing" cannot be true in
// the window and false in the tray. A test walks every action the views can
// offer and asserts Do handles it.
type App struct {
	shell *Shell
	opts  AppOptions
}

// NewApp wires a shell to a host.
func NewApp(s *Shell, opts AppOptions) *App {
	if opts.OpenURL == nil {
		opts.OpenURL = pairing.OpenBrowser
	}
	if opts.DashboardURL == "" {
		opts.DashboardURL = config.DashboardURL()
	}
	if opts.RuntimeHelpURL == "" {
		opts.RuntimeHelpURL = DefaultRuntimeHelpURL()
	}
	if opts.Autostart == nil {
		opts.Autostart = PlatformAutostart()
	}
	return &App{shell: s, opts: opts}
}

// Shell is the shell behind this app.
func (a *App) Shell() *Shell { return a.shell }

// View is the current document, which is what the Wails host binds.
func (a *App) View() View { return a.shell.View() }

// Menu is the tray menu for the current view.
func (a *App) Menu() []MenuItem { return Menu(a.shell.View()) }

// Tooltip is the tray tooltip for the current view.
func (a *App) Tooltip() string { return Tooltip(a.shell.View()) }

// Options exposes the app's host options, for the windows that need them.
func (a *App) Options() AppOptions { return a.opts }

// Do performs one action.
//
// It never panics and never blocks for long: an action that has to wait (a pause
// across processes) is bounded, and one that polls (pairing) runs in its own
// goroutine. A tray whose Quit button hangs is a tray a user kills with the task
// manager.
func (a *App) Do(action, arg string) (ActionResult, error) {
	res := ActionResult{OK: true}
	var err error
	switch action {
	case ActionPause:
		err = a.shell.Pause(arg)
		if err == nil {
			res.Message = "Paused. This computer won't take new work until you resume it."
		}

	case ActionResume:
		err = a.shell.Resume()
		if err == nil {
			res.Message = resumeMessage(a.shell.View())
		}

	case ActionStartRunner:
		a.shell.StartRunner()

	case ActionStopRunner:
		a.shell.StopRunner()
		res.Message = "Stopping. A video already in progress finishes first."

	case ActionOpenWindow:
		if a.opts.ShowWindow != nil {
			a.opts.ShowWindow()
		}

	case ActionOpenDashboard:
		if oerr := a.opts.OpenURL(a.opts.DashboardURL); oerr != nil {
			err = sayWrap("Couldn't open your browser. Go to "+a.opts.DashboardURL+" instead.", oerr)
		}

	case ActionOpenJob:
		// The run's own page when ReelBolt sent the ids for it, the dashboard
		// otherwise: a button that opens nothing is worse than one that opens
		// the place the run can be found.
		target := a.opts.DashboardURL
		if j, ok := a.shell.Ledger().Find(arg); ok {
			if u := RunURL(a.shell.dashboardURL(), j); u != "" {
				target = u
			}
		}
		if oerr := a.opts.OpenURL(target); oerr != nil {
			err = sayWrap("Couldn't open your browser. Go to "+target+" instead.", oerr)
		}

	case ActionClearMediaCache:
		var freed int64
		freed, err = a.shell.ClearMediaCache()
		if err == nil {
			if freed == 0 {
				res.Message = "The media cache was already empty."
			} else {
				res.Message = fmt.Sprintf("Freed %s. Footage will be downloaded again the next time a job needs it.", humanBytes(freed))
			}
		}

	case ActionOpenSettings, ActionOpenJobs, ActionOpenDevice, ActionOpenFolders:
		res.Screen = screenFor(action)
		if a.opts.ShowWindow != nil {
			a.opts.ShowWindow()
		}

	// ---- folder sync ----

	case ActionFoldersAdd:
		res.Screen = "folders"
		act, err := a.foldersAdd(context.Background())
		res = merge(res, act)
		if err != nil {
			return res, err
		}

	case ActionFoldersPause:
		act, err := a.foldersPause(context.Background(), arg)
		res = merge(res, act)
		if err != nil {
			return res, err
		}

	case ActionFoldersResume:
		act, err := a.foldersResume(context.Background(), arg)
		res = merge(res, act)
		if err != nil {
			return res, err
		}

	case ActionFoldersRemove:
		act, err := a.foldersRemove(context.Background(), arg)
		res = merge(res, act)
		if err != nil {
			return res, err
		}

	case ActionFoldersOpenProject:
		act, err := a.foldersOpenProject(context.Background(), arg)
		res = merge(res, act)
		if err != nil {
			return res, err
		}

	case ActionPair:
		res.Screen = "first-run"
		if a.opts.ShowWindow != nil {
			a.opts.ShowWindow()
		}
		err = a.shell.StartPairing(context.Background())
		if err == nil {
			// The browser is a convenience: the code on screen is what pairs,
			// so a browser that will not open is a sentence, not a failure.
			res.Message = "Enter the code shown here in ReelBolt to approve this computer."
			if url := a.shell.Pairing().URL; url != "" {
				if oerr := a.opts.OpenURL(url); oerr == nil {
					res.Message = "Approve this computer in the browser window that just opened. The code is shown here too."
				}
			}
		}

	case ActionSetupRuntime:
		// The one button for "video rendering is off". When Docker or Podman
		// works and only the video toolkit is missing, the fix is a download
		// this app can do itself; anything else needs the person, and the
		// setup guide is where they are walked through it.
		res.Screen = "first-run"
		if a.shell.toolkitIsTheFix() {
			err = a.shell.DownloadRuntime()
			if err == nil {
				res.Message = downloadStartedMessage
			}
			break
		}
		if oerr := a.opts.OpenURL(a.opts.RuntimeHelpURL); oerr != nil {
			err = sayWrap("Couldn't open your browser. The setup guide is at "+a.opts.RuntimeHelpURL+".", oerr)
		} else {
			res.Message = "The setup guide is open in your browser. Come back and press Try again when you're done."
		}

	case ActionDownloadRuntime:
		err = a.shell.DownloadRuntime()
		if err == nil {
			res.Message = downloadStartedMessage
		}

	case ActionRecheckRuntime:
		a.shell.Recheck()
		res.Message = "Checking this computer again."

	case ActionClearCache:
		var freed int64
		freed, err = a.shell.ClearCache()
		if err == nil {
			if freed == 0 {
				res.Message = "There was nothing to clear."
			} else {
				res.Message = fmt.Sprintf("Freed %s.", humanBytes(freed))
			}
		}

	case ActionOpenConfig:
		// Kept for people who prefer editing the settings file by hand; no
		// remedy offers it any more, because Settings edits everything.
		path := a.shell.ConfigPath()
		if path == "" {
			err = say("There's no settings file on this computer yet.")
			break
		}
		if oerr := a.opts.OpenURL(path); oerr != nil {
			err = sayWrap("Couldn't open the settings file.", oerr)
		}

	case ActionCheckForUpdate:
		err = a.CheckForUpdate(context.Background())

	case ActionUnpair:
		// The window confirms before it sends this; the shell does not ask
		// twice. "keepkey" is the default because re-pairing a machine should
		// not have to generate a new identity.
		var out UnpairResult
		out, err = a.shell.Unpair(context.Background(), arg == "forget-key")
		if err == nil {
			res.Message = out.Message()
		}

	// ---- E11: privacy and diagnostics ----

	case ActionTelemetryOn:
		err = a.shell.SetTelemetry(true)
		if err == nil {
			res.Message = "Crash reporting is on. " + telemetryOnMessage(a.shell.View())
		}

	case ActionTelemetryOff:
		err = a.shell.SetTelemetry(false)
		if err == nil {
			res.Message = "Crash reporting is off. Nothing will be sent."
		}

	case ActionTelemetryAsked:
		// The wizard has shown the question. Recording that it asked is what
		// stops it asking again; it changes no answer either way.
		err = a.shell.MarkTelemetryAsked()

	case ActionExportDiagnostics:
		var out telemetry.DiagnosticsResult
		out, err = a.shell.ExportDiagnostics()
		if err == nil {
			res.Message = fmt.Sprintf("Saved a diagnostics report to %s (%s). Nothing was uploaded; look it over before you send it to support.",
				out.Path, humanBytes(out.Bytes))
		}

	case ActionOpenDiagnosticsDir:
		dir := a.shell.DiagnosticsDir()
		if dir == "" {
			err = say("There's no diagnostics folder on this computer yet.")
			break
		}
		if oerr := a.opts.OpenURL(dir); oerr != nil {
			err = sayWrap("Couldn't open the diagnostics folder.", oerr)
		}

	case ActionQuit:
		a.shell.CancelDownload()
		a.shell.StopRunner()
		a.shell.Release()
		if a.opts.Quit != nil {
			a.opts.Quit()
		}

	default:
		return res, fmt.Errorf("%q is not an action this build knows", action)
	}

	if err != nil {
		res.OK = false
		res.Message = userMessage(err)
		return res, err
	}
	res.View = a.shell.View()
	return res, nil
}

// downloadStartedMessage is what the window says when the download starts.
const downloadStartedMessage = "Downloading the video toolkit. This happens once and can take a few minutes."

// userMessage is the sentence a failed action shows. An error that is already
// a sentence for a person is shown as it is; anything else is a failure this
// package did not word, so the person gets a plain sentence and the detail
// goes to the log through the caller.
func userMessage(err error) string {
	if err == nil {
		return ""
	}
	if isPlain(err) {
		return err.Error()
	}
	return "Something went wrong: " + err.Error()
}

// merge folds one action's result into the result the caller is building, so a
// Folder action reads like every other case in the switch.
func merge(into, out ActionResult) ActionResult {
	if out.Message != "" {
		into.Message = out.Message
	}
	if out.Screen != "" {
		into.Screen = out.Screen
	}
	return into
}

// telemetryOnMessage tells the user the one thing they need next: whether
// anything will actually leave the machine. A switch that says "on" over a
// build that cannot send would be the most misleading sentence in this window.
func telemetryOnMessage(v View) string {
	if !v.Settings.Telemetry.Available {
		return "Crash reports will be sent once this version can reach ReelBolt."
	}
	return "If ReelBolt crashes or can't start a job, it sends a short report."
}

// screenFor maps a navigation action to the screen the window should show.
func screenFor(action string) string {
	switch action {
	case ActionOpenSettings:
		return "settings"
	case ActionOpenJobs:
		return "jobs"
	case ActionOpenDevice:
		return "device"
	case ActionOpenFolders:
		return "folders"
	default:
		return ""
	}
}

// resumeMessage explains what happened after a resume, including the case a
// user is most likely to hit: resumed, and still not working.
//
// It is the same truth "reelbolt-runner resume" prints, because a user who
// resumes from the tray and then runs status must not read two answers.
func resumeMessage(v View) string {
	if v.Runner.GovernorKnown && !v.Runner.Allowed {
		if v.Runner.Held {
			return "It's still paused: " + v.Runner.ReasonDetail
		}
		return "Resumed, but this computer still isn't working. " + v.Detail
	}
	return "Resumed. This computer is taking work again."
}

// CheckForUpdate runs the release check by hand.
//
// The daemon's own background check is deliberately not installed in the
// desktop shell: it stops the process to swap the binary, which would make the
// tray window vanish mid-click. The window offers the check instead.
func (a *App) CheckForUpdate(ctx context.Context) error {
	if a.shell.opts.ReleaseCheck == nil {
		return say("This version can't check for updates by itself. Get the latest version from reelbolt.ai.")
	}
	ctx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	v, err := a.shell.opts.ReleaseCheck(ctx)
	if err != nil {
		a.shell.log.Printf("update check: %v", err)
		return sayWrap("Couldn't check for updates. Check your internet connection and try again.", err)
	}
	a.shell.setUpdate(v)
	return nil
}

// ---- pairing ----

// PairingView is the wizard's second step: the code the user types into the
// dashboard, and the address they type it at.
type PairingView struct {
	Active  bool   `json:"active"`
	Code    string `json:"code,omitempty"`
	URL     string `json:"url,omitempty"`
	Error   string `json:"error,omitempty"`
	Message string `json:"message,omitempty"`
}

// StartPairing runs the first step of the device flow and returns as soon as
// there is a code to show.
//
// It is deliberately two steps - Start here, Poll in the background - rather
// than pairing.Client.Pair, which blocks until the user approves. A window that
// froze for the ten minutes a pairing code lives would be a window nobody could
// close.
func (s *Shell) StartPairing(ctx context.Context) error {
	s.mu.Lock()
	if s.pairing != nil && s.pairing.Active {
		s.mu.Unlock()
		return say("Pairing has already started. Finish it in ReelBolt, or cancel it here.")
	}
	name := s.opts.Config.DeviceName
	if name == "" {
		name = "this computer"
	}
	base := s.opts.PairingBaseURL
	if base == "" {
		base = DefaultOrigin()
	}
	s.mu.Unlock()

	// A fresh key per attempt: the API refuses a public key that is already a
	// device ("one key, one device"), so reusing a stale one would make
	// re-pairing impossible.
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		s.log.Printf("pairing: generate a device key: %v", err)
		return sayWrap("Couldn't start pairing on this computer. Try again.", err)
	}
	client := &pairing.Client{
		APIBaseURL: base,
		DeviceName: name,
		Version:    s.opts.Version,
		Logger:     s.log,
	}
	pctx, cancel := context.WithTimeout(ctx, 15*time.Minute)
	s.mu.Lock()
	s.pairCancel = cancel
	s.pairing = &PairingView{Active: true, Message: "Asking ReelBolt for a code."}
	s.mu.Unlock()
	s.notify()

	p, err := client.Start(pctx, pairing.Request{PublicKey: pub})
	if err != nil {
		cancel()
		s.log.Printf("pairing: %v", err)
		msg := pairingMessage(err)
		s.mu.Lock()
		s.pairing = &PairingView{Error: msg}
		s.pairCancel = nil
		s.mu.Unlock()
		s.notify()
		return sayWrap(msg, err)
	}
	url := p.CodeURL
	if url == "" {
		url = p.VerificationURI
	}
	s.mu.Lock()
	s.pairing = &PairingView{
		Active:  true,
		Code:    p.UserCode,
		URL:     url,
		Message: fmt.Sprintf("Enter this code at %s. It expires in %s.", p.VerificationURI, plural(max(p.ExpiresIn/60, 1), "minute")),
	}
	s.mu.Unlock()
	s.notify()

	go s.finishPairing(pctx, cancel, client, p, priv, base)
	return nil
}

// finishPairing waits for the approval and, when it comes, stores the identity.
//
// It records the origin it paired at as the API origin, because that is the
// origin the device API (the workspace name, folder sync) signs for; and it
// checks the gateway it was handed before storing it, exactly like the command
// line's "pair", so a value the runner would refuse to dial never reaches the
// file.
func (s *Shell) finishPairing(ctx context.Context, cancel context.CancelFunc, client *pairing.Client, p *pairing.Pending, priv ed25519.PrivateKey, base string) {
	defer cancel()
	// E11: this goroutine holds a private key and writes the configuration, so
	// it is the other boundary worth guarding. A panic is reported and then
	// re-raised.
	defer telemetry.Recover(s.telemetry, "pairing")
	res, err := client.Poll(ctx, p)
	fail := func(msg string, cause error) {
		if cause != nil {
			s.log.Printf("pairing: %v", cause)
		}
		s.mu.Lock()
		s.pairCancel = nil
		s.pairing = &PairingView{Error: msg}
		s.mu.Unlock()
		s.notify()
	}
	if err != nil {
		fail(pairingMessage(err), err)
		return
	}

	s.mu.Lock()
	cfg := s.opts.Config
	next := *cfg
	s.mu.Unlock()
	if err := config.CheckGateway(res.GatewayURL, next.DevGateways); err != nil {
		fail("ReelBolt answered with an address this version won't connect to. Update the app and try again.", err)
		return
	}
	if err := s.opts.Keys.Save(res.DeviceID, priv); err != nil {
		fail("Couldn't save this computer's sign-in. Try again.", err)
		return
	}
	next.DeviceID = res.DeviceID
	next.GatewayURL = res.GatewayURL
	next.OrganizationID = res.OrganizationID
	if origin, err := config.CheckAPIBase(base, next.DevGateways); err == nil {
		next.APIBaseURL = origin
	} else {
		s.log.Printf("pairing: not recording %s as the API origin: %v", base, err)
	}
	if err := next.Validate(); err != nil {
		fail("ReelBolt's answer couldn't be saved. Update the app and try again.", err)
		return
	}
	if err := s.saveConfig(&next); err != nil {
		fail("Couldn't save your settings. Check that your user folder isn't full, then try again.", err)
		return
	}
	s.mu.Lock()
	s.opts.Config = &next
	s.pairCancel = nil
	s.revoked = false
	s.pairing = &PairingView{Message: "Paired. This computer is ready to take work from ReelBolt."}
	s.mu.Unlock()
	s.notify()
	s.refreshDeviceInfo(true)
	s.StartRunner()
}

// pairingMessage turns a pairing error into a sentence, keeping the ones the
// user can act on distinct.
func pairingMessage(err error) string {
	switch {
	case errors.Is(err, pairing.ErrAccessDenied):
		return "The request was declined in ReelBolt. Nothing was paired."
	case errors.Is(err, pairing.ErrExpired):
		return "The code expired. Start again for a new one."
	case errors.Is(err, pairing.ErrAlreadyPaired):
		return "This computer's sign-in is already in use. Start again to get a new code."
	case errors.Is(err, pairing.ErrNoGateway):
		return "ReelBolt approved this computer but is not ready for runners to connect yet. Nothing is wrong on your side; please contact support."
	case errors.Is(err, context.Canceled):
		return "Pairing was cancelled."
	default:
		return "Couldn't reach ReelBolt. Check your internet connection and try again."
	}
}

// Pairing is the wizard's current state.
func (s *Shell) Pairing() PairingView {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.pairing == nil {
		return PairingView{}
	}
	return *s.pairing
}

// CancelPairing abandons an open request.
func (s *Shell) CancelPairing() {
	s.mu.Lock()
	cancel := s.pairCancel
	s.pairCancel = nil
	s.pairing = nil
	s.mu.Unlock()
	if cancel != nil {
		cancel()
	}
	s.notify()
}

// humanBytes renders a byte count for a sentence.
func humanBytes(n int64) string {
	const unit = 1024
	if n < unit {
		return fmt.Sprintf("%d B", n)
	}
	units := []string{"KB", "MB", "GB", "TB"}
	v := float64(n)
	i := -1
	for v >= unit && i < len(units)-1 {
		v /= unit
		i++
	}
	return fmt.Sprintf("%.1f %s", v, units[i])
}
