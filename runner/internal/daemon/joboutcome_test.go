package daemon

import (
	"context"
	"errors"
	"strings"
	"testing"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// endRecordingSession is an executor session that also wants the end, so the
// test can see the counted wrapper passing it on.
type endRecordingSession struct {
	echoSession
	end *agent.JobEnd
}

func (s *endRecordingSession) SetEnd(end agent.JobEnd) { s.end = &end }

func startedSession(t *testing.T, rec *recorder, job *agent.Job) (agent.Session, *endRecordingSession) {
	t.Helper()
	inner := &endRecordingSession{}
	s := sessions{
		media: agent.FactoryFunc(func(_ context.Context, j *agent.Job) (agent.Session, error) {
			inner.job = j
			return inner, nil
		}),
		act:     &activity{},
		observe: rec,
	}
	sess, err := s.New(context.Background(), job)
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	return sess, inner
}

// The finished event carries how this runner's part ended, read from what the
// agent tells the session before it closes it.
func TestTheFinishedEventCarriesTheOutcome(t *testing.T) {
	cases := []struct {
		end     agent.JobEnd
		outcome string
		detail  bool
	}{
		{agent.JobEnd{Reason: agent.EndClosed}, OutcomeFinished, false},
		{agent.JobEnd{Reason: agent.EndCancelled}, OutcomeCancelled, true},
		{agent.JobEnd{Reason: agent.EndTimeout, Class: protocol.FailRunner, Code: protocol.ErrCodeTimeout}, OutcomeTimedOut, true},
		{agent.JobEnd{Reason: agent.EndFailed, Class: protocol.FailRunner, Code: protocol.ErrCodeContainerUnavailable}, OutcomeFailed, true},
		{agent.JobEnd{Reason: agent.EndLeaseLost}, OutcomeInterrupted, true},
		{agent.JobEnd{Reason: agent.EndDisconnected}, OutcomeInterrupted, true},
		{agent.JobEnd{Reason: agent.EndDrain}, OutcomeInterrupted, true},
		{agent.JobEnd{}, OutcomeInterrupted, true},
		{agent.JobEnd{Reason: "a-later-reason"}, OutcomeInterrupted, true},
	}
	for _, tc := range cases {
		rec := newRecorder(2)
		sess, inner := startedSession(t, rec, testJob("job-"+string(tc.end.Reason)))
		aware, ok := sess.(agent.EndAware)
		if !ok {
			t.Fatal("the counted session does not take the agent's end report")
		}
		aware.SetEnd(tc.end)
		sess.Close(context.Background())
		rec.wait(t, 2)
		ev := rec.all()[1]
		if ev.Kind != JobFinished || ev.Outcome != tc.outcome {
			t.Errorf("%q: event = %s/%q, want finished/%q", tc.end.Reason, ev.Kind, ev.Outcome, tc.outcome)
		}
		if (ev.OutcomeDetail != "") != tc.detail {
			t.Errorf("%q: detail = %q", tc.end.Reason, ev.OutcomeDetail)
		}
		if inner.end == nil || *inner.end != tc.end {
			t.Errorf("%q: the executor's own session was not told the end", tc.end.Reason)
		}
	}
}

// The sentence a person reads is plain language: no code, no path, no stack.
func TestAFailedOutcomeIsOnePlainSentence(t *testing.T) {
	for _, code := range []string{protocol.ErrCodeContainerUnavailable, protocol.ErrCodeUnsupportedOp,
		protocol.ErrCodeDiskFull, protocol.ErrCodeTimeout, "job_setup_failed", "/home/sam/x: no such file"} {
		outcome, detail := outcomeOf(agent.JobEnd{Reason: agent.EndFailed, Code: code})
		if outcome != OutcomeFailed {
			t.Fatalf("%q: outcome = %q", code, outcome)
		}
		if strings.Contains(detail, "_") || strings.Contains(detail, "/") || strings.Count(detail, ".") != 1 ||
			!strings.HasSuffix(detail, ".") {
			t.Errorf("%q: detail %q is not one plain sentence", code, detail)
		}
	}
}

// A session closed without an end report (an older agent) still finishes, as
// interrupted: the one outcome that claims nothing about the work.
func TestAFinishWithoutAnEndReportIsInterrupted(t *testing.T) {
	rec := newRecorder(2)
	sess, _ := startedSession(t, rec, testJob("job-old"))
	sess.Close(context.Background())
	rec.wait(t, 2)
	if ev := rec.all()[1]; ev.Outcome != OutcomeInterrupted {
		t.Fatalf("outcome = %q, want interrupted", ev.Outcome)
	}
}

// Every event kind carries the display names the gateway sent.
func TestEveryEventCarriesTheDisplayNames(t *testing.T) {
	rec := newRecorder(3)
	job := testJob("job-named")
	job.Display = protocol.AssignDisplay{
		WorkspaceName: "Acme", ProjectName: "Launch", WorkflowName: "Promo",
		ProjectID: "p-1", WorkflowID: "w-1", ExecutionID: "e-1",
	}
	sess, _ := startedSession(t, rec, job)
	if _, opErr := sess.Handle(context.Background(), "run", nil); opErr != nil {
		t.Fatalf("Handle: %v", opErr)
	}
	sess.(agent.EndAware).SetEnd(agent.JobEnd{Reason: agent.EndClosed})
	sess.Close(context.Background())
	rec.wait(t, 3)
	for _, ev := range rec.all() {
		if ev.WorkspaceName != "Acme" || ev.ProjectName != "Launch" || ev.WorkflowName != "Promo" ||
			ev.ProjectID != "p-1" || ev.WorkflowID != "w-1" || ev.ExecutionID != "e-1" {
			t.Errorf("%s event lost the display names: %+v", ev.Kind, ev)
		}
	}
}

// A job whose session could not be created never produced a started event, so
// it must not produce a finished one either - the refused-job promise, and the
// separate failure stream is where it goes.
func TestARefusedJobProducesNeitherStartNorFinish(t *testing.T) {
	rec := newRecorder(1)
	var refused []JobFailure
	s := sessions{
		media: agent.FactoryFunc(func(context.Context, *agent.Job) (agent.Session, error) {
			return nil, &agent.JobError{Class: protocol.FailRunner, Code: protocol.ErrCodeContainerUnavailable, Message: "no"}
		}),
		act:      &activity{},
		observe:  rec,
		failures: JobFailureObserverFunc(func(f JobFailure) { refused = append(refused, f) }),
	}
	if _, err := s.New(context.Background(), testJob("job-refused")); err == nil {
		t.Fatal("the refused job was accepted")
	}
	if len(rec.all()) != 0 {
		t.Fatalf("a refused job reached the job list: %+v", rec.all())
	}
	if len(refused) != 1 || refused[0].Code != protocol.ErrCodeContainerUnavailable {
		t.Fatalf("refusals = %+v", refused)
	}
	var jobErr *agent.JobError
	_, err := s.New(context.Background(), testJob("job-refused-2"))
	if !errors.As(err, &jobErr) {
		t.Fatalf("the refusal lost its JobError: %v", err)
	}
}
