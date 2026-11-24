package telemetry

import (
	"context"
	"runtime/debug"
)

// Recover is the crash-reporting hook, installed as a deferred call at each
// goroutine boundary the runner guards:
//
//	defer telemetry.Recover(rep, "run")
//
// Two decisions are worth stating.
//
// IT RE-PANICS. This package adds reporting, not crash suppression. A panic
// that was going to take the process down still takes it down, with the same
// value and the same trace; the only difference is that a report was sent
// first. Swallowing it would leave a half-dead runner that the shell still
// believes in, which is a worse failure than the one it hides.
//
// IT REPORTS THE TYPE OF THE VALUE AND NEVER THE VALUE. The message a panic
// carries routinely quotes what caused it - a path, a URL, a project's text -
// so the event builder is given the value only to name its Go type (see
// NewCrash). A nil reporter changes nothing but the reporting.
func Recover(r *Reporter, stage string) {
	v := recover()
	if v == nil {
		return
	}
	ReportPanic(r, stage, v, debug.Stack())
	// The report is sent; the crash is not suppressed. See the note above.
	panic(v)
}

// ReportPanic sends one crash report and returns. It exists separately from
// Recover so a test can exercise the reporting half without a panic unwinding
// through it.
func ReportPanic(r *Reporter, stage string, value any, stack []byte) {
	if r == nil || !r.Enabled() {
		return
	}
	ev := NewCrash(r.Env(), r.newID(), r.now(), stage, value, stack)
	// A crash path may not outlive the call, so the send is synchronous and
	// bounded rather than queued. A failure here is counted and logged by the
	// reporter and never propagates: the panic is the caller's to re-raise.
	ctx, cancel := context.WithTimeout(context.Background(), r.sendTimeout)
	defer cancel()
	_ = r.ReportSync(ctx, ev)
}
