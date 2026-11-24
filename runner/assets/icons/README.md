# ReelBolt desktop runner — icon pack

Every icon the ReelBolt desktop runner (`runner/`, Area E) and its installers need. **These files
are generated. Do not edit, redraw or export them by hand** — edit the master in `brand/` and re-run
the generator, which is the rule `docs/brand.md` states for all ReelBolt artwork:

```sh
cd brand && npm install          # once: sharp + png-to-ico
node brand/scripts/runner-icons.mjs
```

The generator records where every file came from in `icons.manifest.json` (the sha256 of each
master it read and of each file it wrote). `node brand/scripts/sync.mjs --check` verifies that
record, so CI fails when a master changed without the pack being regenerated, or when a file in this
directory was edited by hand.

## Platforms

| Platform | Status |
|---|---|
| Windows x64 | supported — `.ico` containers below |
| Linux x64 / arm64 | supported — PNG tray icons and `.desktop` artwork below |
| macOS (arm64) | **not supported** (decision D26) — nothing here is for macOS |

**What D26 dropped, and what adding it back would take.** The macOS half of the G2c plan is
deliberately absent: no `trayTemplate.png` (16x16), no `trayTemplate@2x.png` (32x32), no monochrome
macOS status variants and no `AppIcon.icns`. Nothing here relies on AppKit tinting a file whose name
ends in `Template`, and `brand/tray-template.svg` (the black-plus-alpha master that set was to be
rendered from) is currently unused by the runner. If D26 is ever reversed: add the sizes to a
`macos` entry in `brand/scripts/runner-icons.mjs`, render from `brand/tray-template.svg` instead of
`brand/mark.svg`, and add an `.icns` writer — `iconutil` is macOS-only and `png2icons` is not a
dependency here, because neither Windows nor Linux needs one, so that writer would be a small
in-repo `.icns` packer or a new dependency.

## Files

| File | Sizes | Made from | Used for |
|---|---|---|---|
| `tray.ico` | 16, 20, 24, 32 | `brand/mark.svg` | Windows notification-area icon, state **idle** |
| `tray-busy.ico` | 16, 20, 24, 32 | `brand/mark.svg` + badge | Windows tray, state **working** |
| `tray-paused.ico` | 16, 20, 24, 32 | `brand/mark.svg` + badge | Windows tray, state **paused** |
| `tray-error.ico` | 16, 20, 24, 32 | `brand/mark.svg` + badge | Windows tray, state **error** |
| `tray-unpaired.ico` | 16, 20, 24, 32 | `brand/mark.svg` + badge | Windows tray, state **unpaired** |
| `app.ico` | 16, 24, 32, 48, 64, 256 | `brand/app-icon.svg` | `reelbolt-runner-desktop.exe`, installer and shortcuts (`go-winres`/`rsrc`/`-ldflags -H=windowsgui` resource) |
| `tray-NN.png` (22, 24) | 22, 24 | `brand/mark.svg` | Linux panel icon, state **idle** |
| `tray-<state>-NN.png` | 22, 24 | `brand/mark.svg` + badge | Linux panel icon per state (`busy`, `paused`, `error`, `unpaired`) |
| `reelbolt-runner-256.png`, `reelbolt-runner-512.png` | 256, 512 | `brand/app-icon.svg` | `reelbolt-runner.desktop`'s `Icon=` (application menu, launchers, HiDPI) |
| `icons.manifest.json` | — | generator | Provenance record for `brand/scripts/sync.mjs --check` |

The Windows tray sizes are the ones the shell asks a 16 px-baseline icon for at 100/125/150/200 %
display scaling; the executable carries the usual 16…256 ladder. Linux panels ask for 22 px (GNOME,
KDE) or 24 px.

### States

The tray states are the runner's own (`E6a`: idle / working / paused / error / unpaired); the icon
pack spells them `busy`, `paused`, `error` and `unpaired`, with the plain mark as `idle`. The
badge differs in **shape** as well as colour, so the states stay apart at 16 px and for a
colour-blind user, and every colour is an existing token from `docs/site-design-system.md` (the
mark itself is never recoloured):

| State | Badge | Colour |
|---|---|---|
| idle | none — the mark alone | — |
| busy/working | filled dot | `accent` `#A855F7` |
| paused | two bars (the transport pause glyph) | `accent-strong` `#7C3AED` |
| error | filled dot | `danger` `#F87171` (the audited error token) |
| unpaired | hollow ring | `accent-strong` `#7C3AED` |

The badge sits in the bottom-right corner of the mark's box with a transparent gap cut out of the
mark underneath it, so it reads on a light or a dark panel. Verified property: a variant differs
from the plain mark **only** inside that badge — at 22 px, no differing pixel is further than 7.3 px
from the badge centre (the generator's own output, measured with `sharp`).

## Using it from Go

`go:embed` cannot reach outside the package directory (no `..`), so the embed directive has to live
in a Go file **in this directory**, not in `runner/internal/...`:

```go
// runner/assets/icons/icons.go  — package icons
package icons

import _ "embed"

//go:embed tray.ico
var TrayIdle []byte

//go:embed tray-busy.ico
var TrayBusy []byte
// ...and tray-paused.ico, tray-error.ico, tray-unpaired.ico, app.ico, the Linux PNGs
```

The tray WP (E6a, `runner/internal/ui/tray.go`) owns that file and the binding to the systray API;
this icon pack deliberately adds **no Go code**, so the two work packages do not collide. Linux
panels generally want a file path rather than bytes — write the PNG to the runner's cache directory
and point the tray at it — while the Windows shell takes the `.ico` bytes directly.
