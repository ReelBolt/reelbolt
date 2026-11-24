package ui

import (
	"strconv"
	"sync"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/daemon"
)

func fixedClock(t time.Time) func() time.Time { return func() time.Time { return t } }

func TestLedgerRecordsAJobFromStartToFinish(t *testing.T) {
	base := time.Date(2026, 10, 8, 12, 0, 0, 0, time.UTC)
	l := NewLedger(fixedClock(base))
	l.ObserveJob(daemon.JobEvent{Kind: daemon.JobStarted, JobID: "j1", JobType: protocol.JobRemotionWorkspace, OrgID: "org-1"})
	if l.Len() != 1 {
		t.Fatalf("len = %d, want 1", l.Len())
	}
	cur := l.Current()
	if cur == nil || cur.ID != "j1" {
		t.Fatalf("current = %+v", cur)
	}
	p := 30
	l.ObserveJob(daemon.JobEvent{Kind: daemon.JobProgress, JobID: "j1", Stage: "bundling", Percent: &p, Detail: "frame 42"})
	cur = l.Current()
	if cur.Percent == nil || *cur.Percent != 30 || cur.Stage != "bundling" {
		t.Fatalf("progress = %+v", cur)
	}
	l.ObserveJob(daemon.JobEvent{Kind: daemon.JobFinished, JobID: "j1", At: base.Add(90 * time.Second)})
	if l.Current() != nil {
		t.Fatal("a finished job is still current")
	}
	jobs := l.Jobs()
	if len(jobs) != 1 || jobs[0].Running() {
		t.Fatalf("jobs = %+v", jobs)
	}
	if d := jobs[0].Duration(base.Add(90 * time.Second)); d != 90*time.Second {
		t.Fatalf("duration = %s", d)
	}
}

// TestLedgerKeepsFifty: the list is "what has my machine been doing", and it has
// to be bounded or a long-lived tray grows without limit.
func TestLedgerKeepsFifty(t *testing.T) {
	l := NewLedger(nil)
	for i := 0; i < maxJobs+20; i++ {
		l.ObserveJob(daemon.JobEvent{Kind: daemon.JobStarted, JobID: "j" + strconv.Itoa(i), JobType: protocol.JobVideoCompile})
		l.ObserveJob(daemon.JobEvent{Kind: daemon.JobFinished, JobID: "j" + strconv.Itoa(i)})
	}
	if l.Len() != maxJobs {
		t.Fatalf("len = %d, want %d", l.Len(), maxJobs)
	}
	jobs := l.Jobs()
	if jobs[0].ID != "j69" {
		t.Fatalf("most recent = %q, want j69", jobs[0].ID)
	}
	if jobs[len(jobs)-1].ID != "j20" {
		t.Fatalf("oldest kept = %q, want j20", jobs[len(jobs)-1].ID)
	}
}

// TestLedgerDoesNotInventAStartTimeForUnknownProgress: a job the ledger never
// saw start still matters, but the row must say what it knows.
func TestLedgerDoesNotInventAStartTimeForUnknownProgress(t *testing.T) {
	base := time.Date(2026, 10, 8, 12, 0, 0, 0, time.UTC)
	l := NewLedger(fixedClock(base))
	l.ObserveJob(daemon.JobEvent{Kind: daemon.JobProgress, JobID: "ghost", JobType: protocol.JobVideoCompile, Stage: "probing"})
	jobs := l.Jobs()
	if len(jobs) != 1 {
		t.Fatalf("jobs = %+v", jobs)
	}
	if jobs[0].Started != base {
		t.Fatalf("started = %s, want the moment the progress arrived", jobs[0].Started)
	}
	if l.Current() == nil {
		t.Fatal("a job reporting progress is not the current job")
	}
}

// TestLedgerNeverPanicsOnAnUnknownFinish keeps the observer safe: the daemon does
// not recover for it, so a finish for a job the ledger does not hold must be a
// no-op rather than a panic that takes the runner down.
func TestLedgerNeverPanicsOnAnUnknownFinish(t *testing.T) {
	l := NewLedger(nil)
	l.ObserveJob(daemon.JobEvent{Kind: daemon.JobFinished, JobID: "nobody"})
	var nilLedger *Ledger
	nilLedger.ObserveJob(daemon.JobEvent{Kind: daemon.JobStarted, JobID: "x"})
	if nilLedger.Len() != 0 || nilLedger.Current() != nil {
		t.Fatal("a nil ledger answered")
	}
}

// TestLedgerIsSafeUnderConcurrency: the daemon calls the observer from the
// dispatch path and from each job goroutine at once.
func TestLedgerIsSafeUnderConcurrency(t *testing.T) {
	l := NewLedger(nil)
	var wg sync.WaitGroup
	for i := 0; i < 8; i++ {
		wg.Add(1)
		go func(n int) {
			defer wg.Done()
			id := "job-" + strconv.Itoa(n)
			l.ObserveJob(daemon.JobEvent{Kind: daemon.JobStarted, JobID: id, JobType: protocol.JobVideoCompile})
			for j := 0; j < 50; j++ {
				p := j
				l.ObserveJob(daemon.JobEvent{Kind: daemon.JobProgress, JobID: id, Percent: &p})
				_ = l.Current()
				_ = l.Jobs()
			}
			l.ObserveJob(daemon.JobEvent{Kind: daemon.JobFinished, JobID: id})
		}(i)
	}
	wg.Wait()
	if l.Len() != 8 {
		t.Fatalf("len = %d, want 8", l.Len())
	}
	for _, j := range l.Jobs() {
		if j.Running() {
			t.Errorf("%s is still running", j.ID)
		}
	}
}

// TestLedgerReturnsCopies keeps the list from being mutated through a caller.
func TestLedgerReturnsCopies(t *testing.T) {
	l := NewLedger(nil)
	l.ObserveJob(daemon.JobEvent{Kind: daemon.JobStarted, JobID: "j1", JobType: protocol.JobVideoCompile})
	jobs := l.Jobs()
	jobs[0].ID = "tampered"
	if l.Jobs()[0].ID != "j1" {
		t.Fatal("the ledger handed out its own slice")
	}
}

func TestJobTitle(t *testing.T) {
	cases := map[string]string{
		protocol.JobRemotionWorkspace: "Rendering a video",
		protocol.JobVideoCompile:      "Putting the video together",
		protocol.JobVideoAnalyzeEx:    "Analyzing footage",
		// A type this build does not know is still work the computer did.
		"":              "Processing media",
		"something.new": "Processing media",
	}
	for in, want := range cases {
		if got := JobTitle(in); got != want {
			t.Errorf("JobTitle(%q) = %q, want %q", in, got, want)
		}
	}
}

// TestJobViewCarriesNoVerdict: the runner is not told why a job ended, so the
// row must not imply one.
func TestJobViewCarriesNoVerdict(t *testing.T) {
	now := time.Date(2026, 10, 8, 12, 0, 0, 0, time.UTC)
	j := Job{ID: "j1", Type: protocol.JobVideoCompile, Started: now.Add(-2 * time.Minute), Ended: now.Add(-time.Minute)}
	v := jobView(j, now, "")
	if v.Running {
		t.Error("a finished job reported as running")
	}
	if v.DurationSecs != 60 {
		t.Errorf("duration = %v", v.DurationSecs)
	}
	if v.Title != "Putting the video together" {
		t.Errorf("title = %q", v.Title)
	}
}
