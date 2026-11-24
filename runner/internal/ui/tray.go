package ui

import (
	_ "embed"
	"fmt"
	"strings"
	"time"
)

// Action IDs. They are strings rather than an enum because they cross the IPC
// boundary to the window as well as driving the tray menu, and a string that
// says what it does beats a number that does not.
const (
	ActionPause          = "pause"
	ActionResume         = "resume"
	ActionOpenWindow     = "open-window"
	ActionOpenDashboard  = "open-dashboard"
	ActionOpenSettings   = "open-settings"
	ActionOpenJobs       = "open-jobs"
	ActionOpenDevice     = "open-device"
	ActionPair           = "pair"
	ActionStartRunner    = "start-runner"
	ActionStopRunner     = "stop-runner"
	ActionClearCache     = "clear-cache"
	ActionCheckForUpdate = "check-for-update"
	ActionSetupRuntime   = "setup-runtime"
	// ActionDownloadRuntime downloads the video toolkit in the background,
	// with progress in View().Runtime.Download.
	ActionDownloadRuntime = "download-runtime"
	// ActionRecheckRuntime checks the machine again: the setup screen's
	// "Try again" after starting Docker or Podman.
	ActionRecheckRuntime = "recheck-runtime"
	// ActionOpenJob opens a job's run in the dashboard; Arg is the job id.
	ActionOpenJob = "open-job"
	// ActionClearMediaCache empties the downloaded-media cache.
	ActionClearMediaCache = "clear-media-cache"
	ActionOpenConfig      = "open-config"
	ActionUnpair          = "unpair"
	ActionQuit            = "quit"
	// E11's four. The switch is two actions rather than one toggle, so neither
	// the window nor the tray can disagree with the file about which way it
	// went, and "ask once" is its own action because it changes no setting.
	ActionTelemetryOn        = "telemetry-on"
	ActionTelemetryOff       = "telemetry-off"
	ActionTelemetryAsked     = "telemetry-asked"
	ActionExportDiagnostics  = "export-diagnostics"
	ActionOpenDiagnosticsDir = "open-diagnostics"
)

// Actions lists every action the shell handles. It exists so the tray's menu
// and the window's buttons can be checked against one list in a test: an action
// offered by a view but not handled here is a dead button, and that is exactly
// the sort of thing nobody notices until a user clicks it.
func Actions() []string {
	return []string{
		ActionPause, ActionResume, ActionOpenWindow, ActionOpenDashboard,
		ActionOpenSettings, ActionOpenJobs, ActionOpenDevice, ActionPair,
		ActionStartRunner, ActionStopRunner, ActionClearCache,
		ActionCheckForUpdate, ActionSetupRuntime, ActionDownloadRuntime, ActionRecheckRuntime,
		ActionOpenConfig, ActionUnpair, ActionQuit, ActionOpenJob, ActionClearMediaCache,
		ActionTelemetryOn, ActionTelemetryOff, ActionTelemetryAsked,
		ActionExportDiagnostics, ActionOpenDiagnosticsDir,
		ActionOpenFolders, ActionFoldersAdd, ActionFoldersPause,
		ActionFoldersResume, ActionFoldersRemove, ActionFoldersOpenProject,
	}
}

// MenuItem is one row of the tray menu, in toolkit-neutral terms.
//
// The Wails host turns these into platform menu items and a click into the ID;
// nothing here knows what a Menu is. That is what makes "the menu while paused
// offers Resume and not Pause" a unit test rather than something a person has
// to click through on three platforms.
type MenuItem struct {
	ID        string     `json:"id,omitempty"`
	Label     string     `json:"label"`
	Enabled   bool       `json:"enabled"`
	Checked   bool       `json:"checked,omitempty"`
	Separator bool       `json:"separator,omitempty"`
	Submenu   []MenuItem `json:"submenu,omitempty"`
}

// Menu is the tray menu for a view: the status line, the current job, the
// pause/resume pair, the window and dashboard links, and Quit.
//
// Rules it follows, each of which is a test:
//
//   - The first row is always a disabled status line. It is the one thing a
//     user reads without opening anything, so it is never a button.
//   - Pause and Resume are mutually exclusive and chosen from the governor's
//     own verdict, never from a local guess. A gateway hold gets neither: only
//     the gateway can release it, so offering Resume would be a dead button.
//   - "Open ReelBolt" is always enabled, even unpaired: the window is where the
//     pairing wizard lives.
//   - Quit is always last and always enabled. A tray a user cannot get rid of
//     is worse than any bug it reports.
func Menu(v View) []MenuItem {
	items := []MenuItem{{
		Label:   statusLine(v),
		Enabled: false,
	}}
	if v.Detail != "" {
		items = append(items, MenuItem{Label: v.Detail, Enabled: false})
	}
	// Folder sync's own one line. It is absent unless there is something to
	// say, so a machine that does not use folder sync sees no extra row.
	if v.FolderSummary != "" {
		items = append(items, MenuItem{Label: v.FolderSummary, Enabled: false, ID: ActionOpenFolders})
	}
	if v.Current != nil {
		label := "  " + v.Current.Title
		if v.Current.Percent != nil {
			label += fmt.Sprintf(" · %d%%", *v.Current.Percent)
		}
		label += " · " + shortDuration(time.Duration(v.Current.DurationSecs*float64(time.Second)))
		items = append(items, MenuItem{Label: label, Enabled: false, ID: ActionOpenJobs})
	}
	items = append(items, MenuItem{Separator: true})

	// The pause/resume row.
	switch {
	case v.State == StateUnpaired:
		items = append(items, MenuItem{ID: ActionPair, Label: "Pair this computer…", Enabled: true})
	case !v.Runner.Running:
		// Disabled only while the app restarts itself to apply new settings,
		// which is the one "not running" whose remedy is to wait.
		items = append(items, MenuItem{ID: ActionStartRunner, Label: "Start", Enabled: v.Remedy == nil || v.Remedy.Kind != RemedyNone})
	default:
		if v.Governor.Status.Held && v.Governor.Status.HeldBy != "gateway" {
			items = append(items, MenuItem{ID: ActionResume, Label: "Resume", Enabled: true})
		} else if !v.Governor.Status.Held {
			items = append(items, MenuItem{ID: ActionPause, Label: "Pause", Enabled: v.Governor.CanPause})
		}
		// A gateway hold lands here with no row at all, which is correct: see
		// classify() for why Resume is not offered.
		if v.Runner.Running {
			items = append(items, MenuItem{ID: ActionStopRunner, Label: "Stop", Enabled: true})
		}
	}

	items = append(items, MenuItem{Separator: true})
	items = append(items, MenuItem{ID: ActionOpenWindow, Label: "Open ReelBolt", Enabled: true})
	items = append(items, MenuItem{ID: ActionOpenDashboard, Label: "Open ReelBolt in your browser", Enabled: true})
	items = append(items, MenuItem{Separator: true})
	items = append(items, MenuItem{ID: ActionQuit, Label: "Quit", Enabled: true})
	return items
}

// statusLine is the menu's first row and the tray's tooltip: it must read
// correctly with nothing else on screen, which every headline is written to do.
func statusLine(v View) string {
	return v.Headline
}

// Tooltip is the hover text: the status line, plus the "why" when there is one.
func Tooltip(v View) string {
	line := statusLine(v)
	if !strings.Contains(line, "ReelBolt") {
		line = "ReelBolt: " + line
	}
	if v.Detail != "" {
		line += "\n" + v.Detail
	}
	if v.FolderSummary != "" {
		line += "\n" + v.FolderSummary
	}
	if v.Remedy != nil && v.Remedy.Kind != RemedyNone {
		line += "\n" + v.Remedy.Label
	}
	return line
}

// shortDuration renders a duration the way a status line wants it.
func shortDuration(d time.Duration) string {
	if d < 0 {
		d = 0
	}
	switch {
	case d < time.Minute:
		return fmt.Sprintf("%ds", int(d.Seconds()))
	case d < time.Hour:
		return fmt.Sprintf("%dm%02ds", int(d.Minutes()), int(d.Seconds())%60)
	default:
		return fmt.Sprintf("%dh%02dm", int(d.Hours()), int(d.Minutes())%60)
	}
}

// ---- icons ----
//
// The mark is the brand's, not this package's: brand/tray-template.svg is the
// master (docs/brand.md, "Minimum sizes" — render it at 16, 22 or 24 px), and
// these PNGs are that file rasterised by icons/generate.mjs. Nothing here
// redraws it.
//
// There is ONE icon per size, not one per state. The plan's per-state set is
// "coordinated with the G brand WPs" and the macOS template variants are
// deferred by D26; more to the point, inventing five pieces of artwork in a
// runner WP is exactly the "no app may redraw them" rule this repository
// already has. Until the brand set lands, a state is carried by the tooltip and
// by the menu's first line — which is where a person reads it anyway — and the
// IconSet seam below is where the per-state art drops in with no other change.

//go:embed icons/mark-16.png
var mark16 []byte

//go:embed icons/mark-24.png
var mark24 []byte

//go:embed icons/mark-32.png
var mark32 []byte

//go:embed icons/mark-dark-16.png
var markDark16 []byte

//go:embed icons/mark-dark-24.png
var markDark24 []byte

//go:embed icons/mark-dark-32.png
var markDark32 []byte

// IconSet lets a host supply per-state tray artwork.
type IconSet interface {
	// Icon returns the bytes for a state at a size, and whether it has one.
	Icon(state TrayState, size int, dark bool) ([]byte, bool)
}

// Icon is the tray image for a state at a pixel size.
//
// It prefers the host's set and falls back to the brand mark, so a host with no
// per-state art still gets a correct icon rather than a blank tray.
func Icon(set IconSet, state TrayState, size int, dark bool) []byte {
	if set != nil {
		if b, ok := set.Icon(state, size, dark); ok && len(b) > 0 {
			return b
		}
	}
	return Mark(size, dark)
}

// Mark is the brand's tray mark at a pixel size, in the colour the tray needs:
// black on a light tray, white on a dark one, which is the pair docs/brand.md
// allows for a one-colour mark. An unknown size gets the 24 px image, which
// every platform scales acceptably, rather than nothing.
func Mark(size int, dark bool) []byte {
	if dark {
		switch size {
		case 16:
			return markDark16
		case 32:
			return markDark32
		default:
			return markDark24
		}
	}
	switch size {
	case 16:
		return mark16
	case 32:
		return mark32
	default:
		return mark24
	}
}
