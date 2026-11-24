module github.com/vecchiotom/reelbolt/runner/cmd/reelbolt-runner-desktop

go 1.26.0

// This module exists ONLY to hold the Wails v3 dependency. Wails is a cgo
// build (gtk4 + webkitgtk-6.0 on Linux, MinGW on Windows), and a Go module's
// dependencies are resolved as a whole - so requiring it from runner/go.mod
// would break "go vet ./..." on any machine without a GUI toolchain, which is
// every CI runner here. A nested module is excluded from the parent's "./...",
// so the runner module stays buildable everywhere and this one needs a native
// runner (E10b).
require (
	github.com/vecchiotom/reelbolt/runner v0.0.0
	github.com/wailsapp/wails/v3 v3.0.0-beta.28
)

require (
	github.com/adrg/xdg v0.5.3 // indirect
	github.com/coder/websocket v1.8.15 // indirect
	github.com/danieljoos/wincred v1.2.3 // indirect
	github.com/fsnotify/fsnotify v1.10.1 // indirect
	github.com/go-ole/go-ole v1.3.0 // indirect
	github.com/godbus/dbus/v5 v5.2.2 // indirect
	github.com/google/uuid v1.6.0 // indirect
	github.com/mattn/go-colorable v0.1.14 // indirect
	github.com/mattn/go-isatty v0.0.20 // indirect
	github.com/vecchiotom/reelbolt/sandbox v0.0.0 // indirect
	github.com/zalando/go-keyring v0.2.8 // indirect
	golang.org/x/sys v0.49.0 // indirect
)

// The two replaces below are load-bearing and are NOT inherited.
//
// A replace directive in a dependency module is ignored, so the runner module's
// own "sandbox => ../sandbox" does not reach this build: without the second line
// here, this module would try to fetch a published sandbox version that does not
// exist.
replace github.com/vecchiotom/reelbolt/runner => ../..

replace github.com/vecchiotom/reelbolt/sandbox => ../../../sandbox
