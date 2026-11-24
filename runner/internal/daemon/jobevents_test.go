package daemon

import (
	"context"
	"encoding/json"
	"errors"
	"sync"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// recorder collects the observer calls so a test can assert the sequence.
type recorder struct {
	mu   sync.Mutex
	evs  []JobEvent
	done chan struct{}
}

func newRecorder(want int) *recorder {
	return &recorder{done: make(chan struct{}, want)}
}

func (r *recorder) ObserveJob(ev JobEvent) {
	r.mu.Lock()
	r.evs = append(r.evs, ev)
	r.mu.Unlock()
	select {
	case r.done <- struct{}{}:
	default:
	}
}

func (r *recorder) all() []JobEvent {
	r.mu.Lock()
	defer r.mu.Unlock()
	return append([]JobEvent(nil), r.evs...)
}

func (r *recorder) wait(t *testing.T, n int) {
	t.Helper()
	deadline := time.After(5 * time.Second)
	for {
		if len(r.all()) >= n {
			return
		}
		select {
		case <-r.done:
		case <-deadline:
			t.Fatalf("only %d events arrived, want %d", len(r.all()), n)
		}
	}
}

// echoSession reports progress through the job sink and records its close.
type echoSession struct {
	job    *agent.Job
	closed bool
}

func (s *echoSession) Handle(context.Context, string, json.RawMessage) (any, *protocol.OpError) {
	p := 7
	s.job.Sink.Progress("bundling", &p, "frame 7")
	return map[string]any{"ok": true}, nil
}

func (s *echoSession) Close(context.Context) { s.closed = true }

func testJob(id string) *agent.Job {
	return &agent.Job{
		JobID:   id,
		JobType: protocol.JobVideoCompile,
		OrgID:   "org-1",
		Sink:    noopSink{},
	}
}

type noopSink struct{}

func (noopSink) Progress(string, *int, string) {}

// TestTheObserverSeesAJobFromStartToFinish is the property the desktop shell
// depends on: a session starting, reporting progress and closing.
func TestTheObserverSeesAJobFromStartToFinish(t *testing.T) {
	rec := newRecorder(3)
	var created *echoSession
	s := sessions{
		media: agent.FactoryFunc(func(_ context.Context, job *agent.Job) (agent.Session, error) {
			created = &echoSession{job: job}
			return created, nil
		}),
		act:     &activity{},
		observe: rec,
	}
	sess, err := s.New(context.Background(), testJob("job-1"))
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	if created == nil {
		t.Fatal("the factory was not called")
	}
	// The sink the observer installed must still reach the library sink, and
	// must also reach the observer.
	if _, opErr := sess.Handle(context.Background(), "run", nil); opErr != nil {
		t.Fatalf("Handle: %v", opErr)
	}
	sess.Close(context.Background())
	rec.wait(t, 3)

	evs := rec.all()
	if len(evs) != 3 {
		t.Fatalf("events = %d, want 3 (%+v)", len(evs), evs)
	}
	if evs[0].Kind != JobStarted || evs[0].JobID != "job-1" || evs[0].JobType != protocol.JobVideoCompile {
		t.Errorf("started event = %+v", evs[0])
	}
	if evs[0].At.IsZero() {
		t.Error("the start event carries no time")
	}
	if evs[1].Kind != JobProgress || evs[1].Stage != "bundling" {
		t.Errorf("progress event = %+v", evs[1])
	}
	if evs[1].Percent == nil || *evs[1].Percent != 7 {
		t.Errorf("progress percent = %v", evs[1].Percent)
	}
	if evs[2].Kind != JobFinished {
		t.Errorf("finish event = %+v", evs[2])
	}
	if !created.closed {
		t.Error("the session was not closed")
	}
}

// TestARefusedJobIsNeverReportedAsStarted: a job the executor refused never
// entered the machine, so it must not appear in a list of the machine work.
func TestARefusedJobIsNeverReportedAsStarted(t *testing.T) {
	rec := newRecorder(1)
	s := sessions{
		media: agent.FactoryFunc(func(context.Context, *agent.Job) (agent.Session, error) {
			return nil, errors.New("no")
		}),
		act:     &activity{},
		observe: rec,
	}
	job := testJob("job-2")
	original := job.Sink
	if _, err := s.New(context.Background(), job); err == nil {
		t.Fatal("the refused job was accepted")
	}
	if len(rec.all()) != 0 {
		t.Fatalf("a refused job was reported: %+v", rec.all())
	}
	if job.Sink != original {
		t.Error("the sink of a refused job was left wrapped")
	}
}

// TestNoObserverChangesNothing: a headless run installs no observer, and that
// must be exactly the behaviour it had before the observer existed.
func TestNoObserverChangesNothing(t *testing.T) {
	var got *agent.Job
	s := sessions{
		media: agent.FactoryFunc(func(_ context.Context, job *agent.Job) (agent.Session, error) {
			got = job
			return &echoSession{job: job}, nil
		}),
		act: &activity{},
	}
	job := testJob("job-3")
	sink := job.Sink
	sess, err := s.New(context.Background(), job)
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	if got.Sink != sink {
		t.Error("the sink was replaced with no observer installed")
	}
	if !s.act.busy() {
		t.Error("the in-flight count did not start")
	}
	sess.Close(context.Background())
	if s.act.busy() {
		t.Error("the in-flight count did not end")
	}
}

func TestJobObserverFunc(t *testing.T) {
	var seen JobEvent
	var o JobObserver = JobObserverFunc(func(ev JobEvent) { seen = ev })
	o.ObserveJob(JobEvent{Kind: JobStarted, JobID: "x"})
	if seen.JobID != "x" {
		t.Fatalf("seen = %+v", seen)
	}
}
