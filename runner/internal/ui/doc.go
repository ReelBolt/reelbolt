// Package ui is the ReelBolt desktop shell: the tray, the window's data and the
// actions a person takes from either.
//
// It exists as a package rather than inside a main() for one reason: the shell
// is a process a user cannot see. A tray that is wrong is not a crash, it is a
// machine that quietly stops taking work — so the part that decides what to show
// and what a button does is pure Go, has no GUI toolkit in it, and is tested
// directly. The Wails host (runner/cmd/reelbolt-runner-desktop) turns a View
// into pixels and nothing else.
//
// Three properties are deliberate:
//
//   - The tray invents no second truth. It reads the same things
//     "reelbolt-runner status" reads: runner.json, the live document the daemon
//     publishes, and the governor's own Status — which is where the REASON a
//     machine is not working already lives. If the tray said "paused" while
//     status said "the schedule is holding it", one of them would be lying.
//   - Every state names what to do about it. A tray that says "not working" and
//     stops is worse than no tray: the user's next move is the whole point of
//     the icon. Remedy is that move, and a state with no remedy says so.
//   - It listens on no port. Nothing here opens a listener; the shell talks to
//     its own daemon in-process and to the gateway over the connection E5
//     already makes. Wails' "server" build tag (which would serve the UI over
//     HTTP) is deliberately not used — see cmd/reelbolt-runner-desktop/README.md.
package ui
