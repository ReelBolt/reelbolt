package main

import (
	"github.com/wailsapp/wails/v3/pkg/application"

	"github.com/vecchiotom/reelbolt/runner/internal/ui"
)

// buildTray wires the shell's view onto a Wails system tray.
//
// It contains NO policy: which rows exist, what they say and whether they are
// enabled is ui.Menu's answer, and that answer is tested in Go. This file turns
// a ui.MenuItem into a Wails MenuItem and a click into ui.App.Do. A tray whose
// contents were decided here could disagree with the window, and that is the
// one thing this design exists to prevent.
func buildTray(app *application.App, shell *ui.Shell, uiApp *ui.App, showWindow func()) {
	tray := app.SystemTray.New()

	// 24 px is the size docs/brand.md names for a tray; Wails scales it.
	tray.SetIcon(ui.Mark(24, false))
	tray.SetDarkModeIcon(ui.Mark(24, true))
	// A left click opens the window: the menu is the right-click, and a tray
	// whose only affordance is a menu is one people click at twice.
	tray.OnClick(showWindow)

	redraw := func(v ui.View) {
		tray.SetTooltip(ui.Tooltip(v))
		menu := application.NewMenu()
		for _, item := range ui.Menu(v) {
			if item.Separator {
				menu.AddSeparator()
				continue
			}
			entry := menu.Add(item.Label)
			entry.SetEnabled(item.Enabled)
			if !item.Enabled || item.ID == "" {
				continue
			}
			// The loop variable is captured per iteration since Go 1.22;
			// the copy makes that explicit rather than depending on it.
			action := item.ID
			entry.OnClick(func(*application.Context) {
				res, err := uiApp.Do(action, "")
				if err != nil {
					// The tray has nowhere to show a sentence, so the window
					// does. A refusal a user cannot read is a refusal they
					// will repeat.
					showWindow()
				}
				_ = res
			})
		}
		tray.SetMenu(menu)
	}

	redraw(shell.View())
	shell.Subscribe(redraw)
}
