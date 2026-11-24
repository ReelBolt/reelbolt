// Package telemetry is the runner's opt-in crash reporting, its rotating log
// and its diagnostics export.
//
// Four properties are deliberate, and they matter more than the mechanism:
//
//   - NOTHING IS SENT UNTIL THE USER TURNS IT ON. The switch defaults to off,
//     the configuration records "absent" as off, and a report built while it is
//     off never reaches a transport. The first-run wizard asks once; a "no" is
//     recorded as an answer, not as an absence, so it is never asked twice.
//
//   - THE WIRE CARRIES NO FREE TEXT. An event is built from a fixed set of
//     fields, and every one of them is validated against an ALLOWLIST pattern
//     (scrub.go) before it is serialised. OS, architecture, the runner's own
//     version, the container runtime's kind and mode, a job's PROTOCOL TYPE, a
//     failure CODE and the Go types in a panic's stack are reportable; a panic's
//     MESSAGE, a file name, a path, a URL, a device id, a workspace id, a user
//     name and a project name are not — and cannot become reportable, because
//     there is no field for them. A crash report is not a way to exfiltrate a
//     render or a project, and that is enforced by the shape of the payload
//     rather than by remembering to redact.
//
//   - THE USER CAN SEE THE PAYLOAD BEFORE ANYTHING IS SENT. Preview() returns
//     the two events this build can produce, built from THIS machine's real
//     facts, with the two fields that vary per event marked as placeholders.
//     The Settings screen shows that document.
//
//   - THE DIAGNOSTICS EXPORT IS LOCAL AND STAYS LOCAL. It writes a zip the user
//     chooses to attach to a support conversation; nothing in this package
//     uploads it. Its free-text half (the log) IS redacted, by Scrubber below —
//     which is a denylist, and is therefore the WEAKER of the two mechanisms and
//     is never applied to a wire payload.
//
// The collector is the runner gateway's POST /telemetry/v1/reports, which
// re-validates every document against the same allowlist
// (sandbox/pkg/telemetrywire) and keeps nothing but a log line and a counter.
// Endpoint() is stamped by the release pipeline exactly as update.TrustedKeys
// is ("reelbolt-manifest ldflags"), and it is empty in a plain build from this
// source tree - so there telemetry can be switched on, is recorded as on, and
// still sends nothing, which the Settings screen says out loud rather than
// pretending otherwise.
package telemetry
