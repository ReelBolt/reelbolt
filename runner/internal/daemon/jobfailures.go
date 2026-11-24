package daemon

import (
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
)

// The daemon's second job notification stream, added by E11 (opt-in crash
// reporting).
//
// It is DELIBERATELY SEPARATE from JobObserver in jobevents.go, and the reason
// is the promise that file makes: "a job the executor refused never reaches
// here: nothing entered the machine, so it must not appear in a list of the
// machine's work". That promise is about the job LIST, and it is worth keeping
// exactly. This stream carries the opposite thing - a job this runner could not
// start - so the two cannot be confused by a reader, a test or a future caller.
//
// It is also the narrowest useful shape. A session that started is reported on
// the OTHER stream when it ends, with how this runner's part ended
// (JobEvent.Outcome); whether the job as a whole succeeded is the engine's to
// say, from the ops' results, and the runner does not pretend to know it. What
// lives here is only the runner's own setup path failing, before any session
// existed - which is why such a job never produces a started event, and so
// never a finished one either. And the failure MESSAGE is absent on purpose: it
// is free text that quotes paths, file names and project text, which is
// exactly what E11's telemetry payload cannot carry.

// JobFailure is one job this runner could not start.
type JobFailure struct {
	// JobID, JobType, OrgID and Attempt are the assignment's own fields.
	JobID   string
	JobType string
	OrgID   string
	Attempt int
	// Class and Code are the protocol's bounded reason, read from the same
	// agent.JobError the gateway is told about: "runner", "input" or "platform",
	// and a code such as "stage_failed", "unsupported_op" or
	// "container_unavailable".
	Class string
	Code  string
	// At is when the daemon observed it.
	At time.Time
}

// JobFailureObserver receives the daemon's refusals.
//
// ObserveJobFailure is called from the dispatch path, so an implementation must
// return promptly. It must not panic: the daemon does not recover for it, and a
// reporter that panicked would take the runner down over a diagnostic.
type JobFailureObserver interface {
	ObserveJobFailure(JobFailure)
}

// JobFailureObserverFunc adapts a function to JobFailureObserver.
type JobFailureObserverFunc func(JobFailure)

// ObserveJobFailure implements JobFailureObserver.
func (f JobFailureObserverFunc) ObserveJobFailure(ev JobFailure) { f(ev) }

// refuse reports one failure, tolerating a nil observer.
func (s sessions) refuse(job *agent.Job, class, code string) {
	if s.failures == nil || job == nil {
		return
	}
	s.failures.ObserveJobFailure(JobFailure{
		JobID:   job.JobID,
		JobType: job.JobType,
		OrgID:   job.OrgID,
		Attempt: job.Attempt,
		Class:   class,
		Code:    code,
		At:      time.Now().UTC(),
	})
}
