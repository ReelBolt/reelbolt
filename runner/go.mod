module github.com/vecchiotom/reelbolt/runner

go 1.26.0

// The runner daemon embeds the D worker agent library (sandbox/pkg/agent) and
// its protocol package. D4b/D5a left this wiring to E5 because runner/go.mod is
// E3's file; the require/replace below is the exact two-line diff they
// specified, plus the one dependency the keystore needs (go-keyring) and the
// one the governor's Linux idle probe needs (godbus, which go-keyring already
// pulled in: E5c promoted it from indirect to direct when it started importing
// it, so the file now says what the module actually uses).
require (
	github.com/godbus/dbus/v5 v5.2.4
	github.com/vecchiotom/reelbolt/sandbox v0.0.0
	github.com/zalando/go-keyring v0.2.8
)

require (
	github.com/fsnotify/fsnotify v1.10.1
	golang.org/x/sys v0.49.0
)

require (
	github.com/coder/websocket v1.8.15 // indirect
	github.com/danieljoos/wincred v1.2.3 // indirect
	github.com/google/uuid v1.6.0 // indirect
)

replace github.com/vecchiotom/reelbolt/sandbox => ../sandbox
