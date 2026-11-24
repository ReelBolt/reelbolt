package daemon

import (
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// The daemon's job notifications, added for the desktop shell (E6).
//
// A tray has to show a job list and a "current job" line, and neither can be
// invented: the daemon is the only thing that sees a session being created and
// closed. This is an OBSERVER, not a control surface — it is called from the
// dispatch path, it must not block, and a nil observer is exactly today's
// behaviour, which is what every headless run has.
//
// What a finished event reports is how THIS RUNNER'S PART of the job ended,
// which the worker agent now tells the session (agent.EndAware): the engine
// closed the session normally, the job was cancelled, it timed out, it could
// not run here, or the runner let go of it (a lost lease, a lost connection, a
// drain, the runner stopping). It is still NOT the verdict on the workflow run:
// a session the engine closed normally can belong to a run that later failed
// in a step that never touched this machine, and the gateway's and engine's
// records stay the authority on that. "finished" means "this machine did what
// was asked of it until the engine said stop", nothing more.
//
// Progress is the worker agent's own non-authoritative progress sink, teed. The
// agent library documents it as advisory, so the shell renders it as such.

// JobEventKind is what happened.
type JobEventKind string

const (
	// JobStarted is a session that was created for an assigned job. A job the
	// executor REFUSED never reaches here: nothing entered the machine, so it
	// must not appear in a list of the machine's work.
	JobStarted JobEventKind = "started"
	// JobProgress is the session's own progress report.
	JobProgress JobEventKind = "progress"
	// JobFinished is a session that has closed. Outcome says how this
	// runner's part of the job ended, not whether the workflow run succeeded.
	JobFinished JobEventKind = "finished"
)

// The values of JobEvent.Outcome on a finished event.
const (
	// OutcomeFinished: the engine closed the session normally (the close op).
	OutcomeFinished = "finished"
	// OutcomeCancelled: someone stopped the run, or the run ended elsewhere.
	OutcomeCancelled = "cancelled"
	// OutcomeTimedOut: the session outlived the time the gateway allowed.
	OutcomeTimedOut = "timed-out"
	// OutcomeFailed: the job could not run on this machine.
	OutcomeFailed = "failed"
	// OutcomeInterrupted: the runner let go of the job before it ended - a
	// lost lease, a lost connection, a drain or the runner stopping. The
	// platform moves such a job to another machine or retries it.
	OutcomeInterrupted = "interrupted"
)

// JobEvent is one notification.
type JobEvent struct {
	Kind JobEventKind
	// JobID, JobType, OrgID and Attempt are the assignment's own fields.
	JobID   string
	JobType string
	OrgID   string
	Attempt int
	// WorkspaceName, ProjectName and WorkflowName are the names the gateway
	// looked up for the job (assign.display), and ProjectID, WorkflowID and
	// ExecutionID the ids a dashboard link is built from. They are set on
	// every kind of event, and every one is "" when the gateway sent none (an
	// older gateway, or a lookup that did not answer in time). Display-only:
	// nothing in the runner decides anything by them.
	WorkspaceName string
	ProjectName   string
	WorkflowName  string
	ProjectID     string
	WorkflowID    string
	ExecutionID   string
	// At is when the daemon observed it.
	At time.Time
	// Stage, Percent and Detail are set on a progress event. Percent is a
	// pointer because "unknown" and "0%" are different facts.
	Stage   string
	Percent *int
	Detail  string
	// Outcome is set on a finished event only: one of the Outcome* values.
	// OutcomeDetail is one plain-language sentence for a person, set when the
	// outcome is not OutcomeFinished; it never quotes a path, a URL or an
	// error's text.
	Outcome       string
	OutcomeDetail string
}

// jobEvent is an event of the given kind carrying the job's own fields.
func jobEvent(kind JobEventKind, job *agent.Job) JobEvent {
	return JobEvent{
		Kind:          kind,
		JobID:         job.JobID,
		JobType:       job.JobType,
		OrgID:         job.OrgID,
		Attempt:       job.Attempt,
		WorkspaceName: job.Display.WorkspaceName,
		ProjectName:   job.Display.ProjectName,
		WorkflowName:  job.Display.WorkflowName,
		ProjectID:     job.Display.ProjectID,
		WorkflowID:    job.Display.WorkflowID,
		ExecutionID:   job.Display.ExecutionID,
	}
}

// outcomeOf maps how the agent says the job ended onto the outcome a person
// reads. A reason this build does not know - a later agent's - is reported as
// interrupted, which is the one outcome that claims nothing about the work.
func outcomeOf(end agent.JobEnd) (string, string) {
	switch end.Reason {
	case agent.EndClosed:
		return OutcomeFinished, ""
	case agent.EndCancelled:
		return OutcomeCancelled, "The run was stopped, or it ended before this part was needed."
	case agent.EndTimeout:
		return OutcomeTimedOut, "This part ran longer than ReelBolt allows, so this computer stopped it."
	case agent.EndFailed:
		return OutcomeFailed, failedSentence(end.Code)
	case agent.EndLeaseLost:
		return OutcomeInterrupted, "ReelBolt moved this part to another computer."
	case agent.EndDisconnected:
		return OutcomeInterrupted, "This computer lost its connection to ReelBolt, so the part was moved elsewhere."
	default:
		// EndDrain, the agent's own lifetime ending, and anything newer.
		return OutcomeInterrupted, "This computer stopped taking work, so the part was moved elsewhere."
	}
}

// failedSentence explains a failure code in words. Only codes the runner itself
// produces are named; anything else gets the general sentence rather than the
// code, which means nothing to a person.
func failedSentence(code string) string {
	switch code {
	case protocol.ErrCodeContainerUnavailable:
		return "This computer could not start its container runtime for this part."
	case protocol.ErrCodeUnsupportedOp:
		return "This computer cannot run this kind of part."
	case protocol.ErrCodeDiskFull:
		return "This computer ran out of disk space for this part."
	case protocol.ErrCodeTimeout:
		return "This part ran longer than ReelBolt allows, so this computer stopped it."
	default:
		return "This computer could not run this part."
	}
}

// JobObserver receives the daemon's job notifications.
//
// ObserveJob is called from the job's own goroutine and from the dispatch path,
// so an implementation must return promptly and be safe for concurrent use. It
// must not panic: the daemon does not recover for it.
type JobObserver interface {
	ObserveJob(JobEvent)
}

// JobObserverFunc adapts a function to JobObserver.
type JobObserverFunc func(JobEvent)

// ObserveJob implements JobObserver.
func (f JobObserverFunc) ObserveJob(ev JobEvent) { f(ev) }

// observe reports one event, tolerating a nil observer and a nil event sink.
//
// A job the executor REFUSED does not come through here: see jobfailures.go,
// which is a separate stream precisely so that promise stays true.
func (s sessions) notify(ev JobEvent) {
	if s.observe == nil {
		return
	}
	if ev.At.IsZero() {
		ev.At = time.Now().UTC()
	}
	s.observe.ObserveJob(ev)
}

// teeProgress forwards the session's progress to the observer as well as to
// the gateway.
type teeProgress struct {
	inner agent.ProgressSink
	sess  sessions
	job   *agent.Job
}

// Progress implements agent.ProgressSink.
func (t teeProgress) Progress(stage string, percent *int, detail string) {
	if t.inner != nil {
		t.inner.Progress(stage, percent, detail)
	}
	ev := jobEvent(JobProgress, t.job)
	ev.Stage, ev.Percent, ev.Detail = stage, percent, detail
	t.sess.notify(ev)
}
