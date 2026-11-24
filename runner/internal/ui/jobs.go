package ui

import (
	"net/url"
	"strings"
	"sync"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/daemon"
)

// maxJobs is how many finished jobs the ledger keeps. The plan says the last
// 50; the number is small on purpose because this is a "what has my machine
// been doing" list, not a history — the dashboard owns the record.
const maxJobs = 50

// Job is one job this runner ran.
//
// Outcome is how THIS COMPUTER'S PART of the job ended, which the daemon now
// reports (daemon.JobEvent.Outcome): the engine closed the session normally,
// the job was cancelled, it timed out, it could not run here, or this computer
// let go of it. It is not the verdict on the whole workflow run: a part that
// finished normally can belong to a run that later failed in a step that never
// touched this computer, so the list says "finished", never "succeeded", and
// links to the run in the dashboard for the rest of the story.
type Job struct {
	ID      string
	Type    string
	OrgID   string
	Attempt int
	// The names and ids ReelBolt sent with the job, for display and for the
	// dashboard link. Any may be empty.
	WorkspaceName string
	ProjectName   string
	WorkflowName  string
	ProjectID     string
	WorkflowID    string
	ExecutionID   string
	Started       time.Time
	// Ended is the zero time while the job is running.
	Ended   time.Time
	Stage   string
	Percent *int
	Detail  string
	// Outcome and OutcomeDetail are set when the job ends (daemon.Outcome*).
	Outcome       string
	OutcomeDetail string
}

// absorb copies the display names and ids an event carries, keeping what the
// row already has when the event carries none.
func (j *Job) absorb(ev daemon.JobEvent) {
	keep := func(dst *string, v string) {
		if v != "" {
			*dst = v
		}
	}
	keep(&j.WorkspaceName, ev.WorkspaceName)
	keep(&j.ProjectName, ev.ProjectName)
	keep(&j.WorkflowName, ev.WorkflowName)
	keep(&j.ProjectID, ev.ProjectID)
	keep(&j.WorkflowID, ev.WorkflowID)
	keep(&j.ExecutionID, ev.ExecutionID)
}

// Running reports whether this job is still in flight.
func (j Job) Running() bool { return j.Ended.IsZero() }

// Duration is how long it ran, or has been running.
func (j Job) Duration(now time.Time) time.Duration {
	if j.Running() {
		if now.Before(j.Started) {
			return 0
		}
		return now.Sub(j.Started)
	}
	return j.Ended.Sub(j.Started)
}

// Ledger records the jobs this runner has run: the daemon's notifications in,
// the job list and the current job out.
//
// It is the daemon.JobObserver the shell installs. Every method is safe for
// concurrent use because the daemon calls the observer from the dispatch path
// and from each job's own goroutine.
type Ledger struct {
	mu      sync.Mutex
	jobs    []Job // most recent first
	current *Job
	now     func() time.Time
}

// NewLedger builds an empty ledger. now may be nil.
func NewLedger(now func() time.Time) *Ledger {
	if now == nil {
		now = time.Now
	}
	return &Ledger{now: now}
}

// ObserveJob implements daemon.JobObserver.
//
// It must not block and must not panic: the daemon does not recover for it, and
// a job observer that panicked would take the runner down over a display bug.
func (l *Ledger) ObserveJob(ev daemon.JobEvent) {
	if l == nil {
		return
	}
	at := ev.At
	if at.IsZero() {
		at = l.now()
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	switch ev.Kind {
	case daemon.JobStarted:
		j := Job{
			ID: ev.JobID, Type: ev.JobType, OrgID: ev.OrgID,
			Attempt: ev.Attempt, Started: at,
		}
		j.absorb(ev)
		// A retried job arrives under a NEW job id, so an id that is already in
		// the list is a re-announcement, not a second job.
		if i := l.indexLocked(ev.JobID); i >= 0 {
			l.jobs[i].Started = at
			l.jobs[i].Ended = time.Time{}
			l.jobs[i].Outcome, l.jobs[i].OutcomeDetail = "", ""
			l.jobs[i].absorb(ev)
			l.current = &l.jobs[i]
			return
		}
		l.jobs = append([]Job{j}, l.jobs...)
		l.trimLocked()
		l.current = &l.jobs[0]

	case daemon.JobProgress:
		i := l.indexLocked(ev.JobID)
		if i < 0 {
			// Progress for a job the ledger never saw started. Recording it
			// would invent a start time, so the row is created from the
			// progress itself — a job that is running matters more than a
			// perfectly accurate start.
			l.jobs = append([]Job{{
				ID: ev.JobID, Type: ev.JobType, OrgID: ev.OrgID,
				Attempt: ev.Attempt, Started: at,
			}}, l.jobs...)
			l.trimLocked()
			i = 0
		}
		l.jobs[i].absorb(ev)
		l.jobs[i].Stage = ev.Stage
		l.jobs[i].Detail = ev.Detail
		if ev.Percent != nil {
			p := *ev.Percent
			l.jobs[i].Percent = &p
		}
		l.current = &l.jobs[i]

	case daemon.JobFinished:
		i := l.indexLocked(ev.JobID)
		if i < 0 {
			return
		}
		l.jobs[i].Ended = at
		l.jobs[i].Outcome = ev.Outcome
		l.jobs[i].OutcomeDetail = ev.OutcomeDetail
		l.jobs[i].absorb(ev)
		if l.current != nil && l.current.ID == ev.JobID {
			l.current = nil
		}
	}
}

// indexLocked finds a job by id. The caller holds the lock.
func (l *Ledger) indexLocked(id string) int {
	for i := range l.jobs {
		if l.jobs[i].ID == id {
			return i
		}
	}
	return -1
}

// trimLocked enforces the cap. The caller holds the lock.
func (l *Ledger) trimLocked() {
	if len(l.jobs) > maxJobs {
		l.jobs = l.jobs[:maxJobs]
	}
}

// Current is the job in flight, or nil.
func (l *Ledger) Current() *Job {
	if l == nil {
		return nil
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.current == nil {
		return nil
	}
	j := *l.current
	// A job whose copy is no longer in the list is gone, not current.
	if l.indexLocked(j.ID) < 0 {
		return nil
	}
	return &j
}

// Find is one job by id.
func (l *Ledger) Find(id string) (Job, bool) {
	if l == nil {
		return Job{}, false
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	if i := l.indexLocked(id); i >= 0 {
		return l.jobs[i], true
	}
	return Job{}, false
}

// Jobs is the list, most recent first.
func (l *Ledger) Jobs() []Job {
	if l == nil {
		return nil
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	return append([]Job(nil), l.jobs...)
}

// Len is how many jobs the ledger holds.
func (l *Ledger) Len() int {
	if l == nil {
		return 0
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	return len(l.jobs)
}

// JobTitle is the plain-language name for a job type.
//
// The protocol's own strings are dotted identifiers ("remotion.workspace-session"),
// which is the right thing on the wire and the wrong thing in a menu. A type
// this build does not know is still work this computer did, so it gets a
// generic name rather than its identifier; JobView.Type keeps the identifier
// for a bug report.
func JobTitle(jobType string) string {
	switch jobType {
	case protocol.JobRemotionWorkspace:
		return "Rendering a video"
	case protocol.JobVideoCompile:
		return "Putting the video together"
	case protocol.JobVideoAnalyzeEx:
		return "Analyzing footage"
	default:
		return "Processing media"
	}
}

// JobView is one row of the Job list.
type JobView struct {
	ID      string `json:"id"`
	Type    string `json:"type"`
	Title   string `json:"title"`
	OrgID   string `json:"orgId,omitempty"`
	Attempt int    `json:"attempt,omitempty"`
	// The names ReelBolt sent with the job, for display only. Any may be absent
	// (an older ReelBolt, or a lookup that did not answer in time).
	WorkspaceName string  `json:"workspaceName,omitempty"`
	ProjectName   string  `json:"projectName,omitempty"`
	WorkflowName  string  `json:"workflowName,omitempty"`
	StartedAt     string  `json:"startedAt"`
	EndedAt       string  `json:"endedAt,omitempty"`
	DurationSecs  float64 `json:"durationSeconds"`
	Stage         string  `json:"stage,omitempty"`
	Percent       *int    `json:"percent,omitempty"`
	Running       bool    `json:"running"`
	// Outcome is how this computer's part ended, once Running is false.
	Outcome string `json:"outcome,omitempty"`
	// OutcomeDetail is one plain sentence, set when the outcome is not
	// "finished".
	OutcomeDetail string `json:"outcomeDetail,omitempty"`
	// DashboardURL opens this job's run in the browser, when ReelBolt sent the
	// three ids it is built from.
	DashboardURL string `json:"dashboardUrl,omitempty"`
}

// RunURL is the dashboard page of the run a job belongs to, or "" when any of
// the three ids is missing. The page is the web app's execution view,
// /app/projects/{projectId}/workflows/{workflowId}/executions/{executionId}.
func RunURL(base string, j Job) string {
	if base == "" || j.ProjectID == "" || j.WorkflowID == "" || j.ExecutionID == "" {
		return ""
	}
	return strings.TrimRight(base, "/") + "/app/projects/" + url.PathEscape(j.ProjectID) +
		"/workflows/" + url.PathEscape(j.WorkflowID) +
		"/executions/" + url.PathEscape(j.ExecutionID)
}

func jobView(j Job, now time.Time, dashboard string) JobView {
	v := JobView{
		ID:            j.ID,
		Type:          j.Type,
		Title:         JobTitle(j.Type),
		OrgID:         j.OrgID,
		Attempt:       j.Attempt,
		WorkspaceName: j.WorkspaceName,
		ProjectName:   j.ProjectName,
		WorkflowName:  j.WorkflowName,
		StartedAt:     j.Started.Format(time.RFC3339),
		DurationSecs:  j.Duration(now).Seconds(),
		Stage:         j.Stage,
		Running:       j.Running(),
		DashboardURL:  RunURL(dashboard, j),
	}
	if !j.Running() {
		v.EndedAt = j.Ended.Format(time.RFC3339)
		v.Outcome = j.Outcome
		v.OutcomeDetail = j.OutcomeDetail
	}
	if j.Percent != nil {
		p := *j.Percent
		v.Percent = &p
	}
	return v
}

func jobViews(jobs []Job, now time.Time, dashboard string) []JobView {
	out := make([]JobView, 0, len(jobs))
	for _, j := range jobs {
		out = append(out, jobView(j, now, dashboard))
	}
	return out
}
