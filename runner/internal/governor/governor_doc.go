// Package governor decides when this machine does NOT work.
//
// It is three things, deliberately kept apart from the daemon that uses them:
//
//   - a Schedule, the policy a user configures (always, only-when-idle,
//     only-on-AC-power, time windows);
//   - a Governor, which evaluates that policy against the machine's real idle
//     and power state plus any explicit pause, and broadcasts the verdict;
//   - Limits, the per-job CPU, memory, process and priority caps, resolved from
//     configuration rather than baked into the executor.
//
// # The one invariant
//
// The governor blocks work only for a reason it is able to evaluate RIGHT NOW.
// An unreadable control file, an unparsable schedule entry, an idle probe that
// is unavailable on this machine or a power state the platform cannot report
// all resolve to "allowed" — a runner that silently stops earning because a
// probe broke is a worse failure than one that works while its user is at the
// keyboard, and the second is visible in "reelbolt-runner status" while the
// first is not. Configuration that is unreadable is a separate matter: it is
// refused at load time with a message naming the field, never guessed at.
//
// The single exception is an explicit pause, which is a person's instruction
// rather than a probe's guess. A pause is therefore always clearable and never
// persisted: it lives in memory for the life of one run, and a control request
// left behind by an earlier process is deleted at startup.
package governor
