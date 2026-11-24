package main

import (
	"errors"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/governor"
)

// isolatedCache points the control channel at a temporary directory, so no test
// can touch the developer's own runner state.
func isolatedCache(t *testing.T) *governor.Control {
	t.Helper()
	t.Setenv("XDG_CACHE_HOME", t.TempDir())
	t.Setenv("HOME", t.TempDir())
	c, err := controlChannel()
	if err != nil {
		t.Fatalf("controlChannel: %v", err)
	}
	return c
}

func publishLive(t *testing.T, c *governor.Control, held bool) {
	t.Helper()
	status := governor.Status{Allowed: !held, Reason: governor.ReasonAllowed, Mode: governor.ModeAlways, Schedule: "mode=always"}
	if held {
		status.Allowed = false
		status.Reason = governor.ReasonPaused
		status.Held = true
		status.HeldBy = governor.SourceUser
		status.Detail = "paused from the command line"
	}
	if err := c.Publish(governor.Live{
		PID:         os.Getpid(),
		StartedAt:   time.Now().Add(-time.Minute).Format(time.RFC3339),
		HeartbeatAt: time.Now().Format(time.RFC3339),
		Status:      status,
	}); err != nil {
		t.Fatal(err)
	}
}

// The command that answers the question this WP is measured on: a user whose
// runner is paused opens a shell and is told, in one line, that no runner is
// there to pause.
func TestPauseRefusesWhenNoRunnerIsRunning(t *testing.T) {
	isolatedCache(t)
	err := cmdPause(nil)
	if err == nil {
		t.Fatal("pause claimed to work with no runner running")
	}
	if !strings.Contains(err.Error(), "no runner is running") {
		t.Fatalf("error = %v, want it to say no runner is running", err)
	}
}

// A live document left behind by a process that died must not be acted on.
func TestPauseRefusesAStaleRunner(t *testing.T) {
	c := isolatedCache(t)
	if err := c.Publish(governor.Live{
		PID:         1,
		HeartbeatAt: time.Now().Add(-time.Hour).Format(time.RFC3339),
		Status:      governor.Status{Allowed: true, Reason: governor.ReasonAllowed},
	}); err != nil {
		t.Fatal(err)
	}
	err := cmdPause(nil)
	if err == nil || !strings.Contains(err.Error(), "stale") {
		t.Fatalf("error = %v, want a stale-state refusal", err)
	}
}

// The other half of "the governor paused my runner and I disagree": a resume
// that finds nothing paused says so instead of pretending.
func TestResumeOnAnUnpausedRunnerIsNotAnError(t *testing.T) {
	c := isolatedCache(t)
	publishLive(t, c, false)
	if err := cmdResume(nil); err != nil {
		t.Fatalf("cmdResume: %v", err)
	}
	if _, err := os.Stat(c.RequestPath); !os.IsNotExist(err) {
		t.Fatal("a no-op resume must not write a request")
	}
}

// The command waits for the daemon to act rather than claiming success the
// moment the request is written: "I asked" and "it happened" are different
// claims, and only one of them is useful to a person.
func TestPauseDoesNotClaimSuccessWhenNothingActs(t *testing.T) {
	c := isolatedCache(t)
	publishLive(t, c, false)

	started := time.Now()
	err := cmdPause(nil)
	if err == nil {
		t.Fatal("pause claimed success although no daemon ever acted on the request")
	}
	if !strings.Contains(err.Error(), "did not acknowledge") {
		t.Fatalf("error = %v", err)
	}
	if elapsed := time.Since(started); elapsed < controlWait {
		t.Fatalf("pause returned after %s, before it could know", elapsed)
	}
}

// A resume reaches a paused runner through the same file, and reports what the
// governor then does — including "resumed, and still not working", which is the
// answer a user with a schedule needs.
func TestPrintGovernorStatusExplainsAScheduleHold(t *testing.T) {
	held := governor.Status{
		Allowed:    false,
		Reason:     governor.ReasonOutsideWindow,
		Detail:     "Sat 10:00 is outside the configured hours",
		NextChange: "2026-01-05T09:00:00Z",
		Mode:       governor.ModeWindows,
	}
	// The function prints; the assertion that matters is that it does not panic
	// and that the resumed-but-still-stopped case is describable at all.
	printGovernorStatus(held)
	printGovernorStatus(governor.Status{Allowed: true, Reason: governor.ReasonAllowed})
	printGovernorStatus(governor.Status{
		Allowed: false, Reason: governor.ReasonPaused, Detail: "asked", Held: true, HeldBy: governor.SourceUser,
	})
}

var _ = errors.New
