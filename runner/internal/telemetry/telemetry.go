package telemetry

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"net/http"
	"runtime"
	"sync"
	"sync/atomic"
	"time"
)

// ConsentVersion identifies the disclosure a user agreed to. It is stored in
// runner.json beside the switch, so a later, wider disclosure can be told apart
// from the one that was accepted, and asked about again rather than silently
// inheriting consent that was given for a narrower thing.
const ConsentVersion = "2026-10-08"

// Disclosure is the plain-language statement shown wherever the switch is. It
// is one string so the Settings screen, the first-run ask and the exports
// cannot drift into three different promises.
const Disclosure = "When you turn this on, ReelBolt on this computer sends a short report if it " +
	"crashes or can't start a job. The report says which version of the app you have, your " +
	"operating system and processor type, whether you use Docker or Podman, the kind of error, " +
	"and the names of the app's own code that was running. It never includes your files or their " +
	"names, folder locations, your name or your computer's name, your workspace or project names, " +
	"what a job was asked to do, or web addresses. Each report gets a random number that isn't " +
	"tied to this computer."

// NeverSent is the list the Settings screen renders as "what is never sent". It
// is enumerated rather than summarised because a promise a user can check
// against the payload preview is worth more than an adjective. Every line is
// something the Event type has no field for (see event.go), not a filter.
var NeverSent = []string{
	"the names of your files and folders, or where they are on this computer",
	"what is in any file, video or project",
	"your name, your computer's name or your user folder",
	"this computer's ReelBolt id, your workspace or your organisation",
	"project, workflow or video names",
	"what a job was asked to do: its settings, scripts or prompts",
	"web addresses, including the private download links jobs use",
	"the text of an error message (only the kind of error is sent)",
}

// Transport sends one serialised payload to a collector.
//
// It is an interface for one reason: this repository has no collector and no
// vendor credential, so every test in this package runs against a fake that
// records what it was asked to send. A test that could not observe "nothing was
// sent" would not be able to prove the property that matters most here.
type Transport interface {
	Send(ctx context.Context, endpoint string, body []byte) error
}

// HTTPTransport POSTs the payload as JSON to the collector.
type HTTPTransport struct {
	// Client is the HTTP client. Nil means one with a 10-second timeout, which
	// is ample for a few hundred bytes and short enough that a dead collector
	// cannot hold a dying process open.
	Client *http.Client
}

// Send implements Transport.
func (t HTTPTransport) Send(ctx context.Context, endpoint string, body []byte) error {
	client := t.Client
	if client == nil {
		client = &http.Client{Timeout: 10 * time.Second}
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, endpoint, bytes.NewReader(body))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("User-Agent", "reelbolt-runner-telemetry/"+Schema)
	res, err := client.Do(req)
	if err != nil {
		return err
	}
	defer func() { _ = res.Body.Close() }()
	// Drain a bounded amount so the connection can be reused, and never let a
	// chatty collector's body into this process's memory unbounded.
	_, _ = io.Copy(io.Discard, io.LimitReader(res.Body, 4<<10))
	if res.StatusCode < 200 || res.StatusCode > 299 {
		return fmt.Errorf("the collector answered %s", res.Status)
	}
	return nil
}

// Options configure a Reporter.
type Options struct {
	// Enabled reports whether the user has turned telemetry on. It is read on
	// every report rather than captured, so flipping the switch takes effect
	// without rebuilding anything.
	Enabled func() bool
	// Endpoint is the collector. Empty means this build cannot send anything.
	Endpoint string
	// Env returns the machine's facts at the moment of the report. Nil means
	// nothing but the Go runtime's own values.
	Env func() Env
	// Transport sends the payload. Nil means HTTPTransport.
	Transport Transport
	// Log receives one line per failure. Nil means the lines are dropped: this
	// package must never be a reason for a caller to need a logger.
	Log func(format string, args ...any)
	// Now overrides the clock, for tests.
	Now func() time.Time
	// Rand is the source of event ids, for tests.
	Rand io.Reader
	// Queue bounds how many asynchronous reports may be waiting. Zero means
	// defaultQueue.
	Queue int
	// SendTimeout bounds one send. Zero means defaultSendTimeout.
	SendTimeout time.Duration
}

const (
	defaultQueue       = 32
	defaultSendTimeout = 5 * time.Second
)

// Reporter turns events into requests, or into nothing at all.
//
// Every method is safe on a nil receiver, so a caller that was never given a
// reporter - the headless commands, a test, a build with the feature compiled
// out - needs no branch.
type Reporter struct {
	opts        Options
	transport   Transport
	now         func() time.Time
	rand        io.Reader
	logf        func(string, ...any)
	sendTimeout time.Duration

	queue   chan []byte
	stop    chan struct{}
	started sync.Once
	wg      sync.WaitGroup
	closeMu sync.Mutex
	closed  bool

	sent     atomic.Int64
	dropped  atomic.Int64
	failed   atomic.Int64
	unsent   atomic.Int64
	inflight atomic.Int64
	// pending counts queued reports until the worker has finished sending them. Flush waits on it
	// rather than on len(queue): the worker takes a report off the queue BEFORE its send begins,
	// so an empty queue with nothing in flight could otherwise be a report on its way out.
	pending atomic.Int64
	lastMu  sync.Mutex
	lastErr string
	warned  atomic.Bool
}

// New builds a reporter.
func New(opts Options) *Reporter {
	r := &Reporter{
		opts:        opts,
		transport:   opts.Transport,
		now:         opts.Now,
		rand:        opts.Rand,
		logf:        opts.Log,
		sendTimeout: opts.SendTimeout,
		queue:       make(chan []byte, queueSize(opts.Queue)),
		stop:        make(chan struct{}),
	}
	if r.transport == nil {
		r.transport = HTTPTransport{}
	}
	if r.now == nil {
		r.now = time.Now
	}
	if r.rand == nil {
		r.rand = rand.Reader
	}
	if r.sendTimeout <= 0 {
		r.sendTimeout = defaultSendTimeout
	}
	return r
}

func queueSize(n int) int {
	if n <= 0 {
		return defaultQueue
	}
	return n
}

// Enabled reports the user's answer, read live.
func (r *Reporter) Enabled() bool {
	if r == nil || r.opts.Enabled == nil {
		return false
	}
	return r.opts.Enabled()
}

// Endpoint is the collector this build would send to. Empty means none is
// compiled in, which is the state of every build made from this source tree.
func (r *Reporter) Endpoint() string {
	if r == nil {
		return ""
	}
	return r.opts.Endpoint
}

// Available reports whether a collector is configured at all.
func (r *Reporter) Available() bool { return r != nil && r.opts.Endpoint != "" }

// Env is the machine's facts, as the reporter would report them.
func (r *Reporter) Env() Env {
	env := Env{OS: runtime.GOOS, Arch: runtime.GOARCH, GoVersion: runtime.Version()}
	if r != nil && r.opts.Env != nil {
		got := r.opts.Env()
		if got.OS == "" {
			got.OS = env.OS
		}
		if got.Arch == "" {
			got.Arch = env.Arch
		}
		if got.GoVersion == "" {
			got.GoVersion = env.GoVersion
		}
		return got
	}
	return env
}

// Report queues an event for a background send.
//
// It NEVER blocks and never panics: it is called from a job's own goroutine and
// from a deferred recover. A queue that is full drops the event and counts the
// drop, because a runner that stalls its own job loop in order to talk about
// itself has the trade-off exactly backwards.
func (r *Reporter) Report(ev Event) {
	if r == nil || !r.Enabled() {
		return
	}
	if !r.Available() {
		r.noteUnsent()
		return
	}
	body, err := ev.Payload()
	if err != nil {
		r.record(fmt.Errorf("could not serialise the report: %w", err))
		return
	}
	r.start()
	r.closeMu.Lock()
	closed := r.closed
	r.closeMu.Unlock()
	if closed {
		return
	}
	r.pending.Add(1)
	select {
	case r.queue <- body:
	default:
		r.pending.Add(-1)
		r.dropped.Add(1)
	}
}

// ReportSync sends an event and waits for the answer. It is what the crash path
// uses: a process that is unwinding may not live long enough for a background
// send, and a bounded wait is worth more there than a lost report.
func (r *Reporter) ReportSync(ctx context.Context, ev Event) error {
	if r == nil || !r.Enabled() {
		return nil
	}
	if !r.Available() {
		r.noteUnsent()
		return errors.New("no telemetry collector is configured in this build")
	}
	body, err := ev.Payload()
	if err != nil {
		return err
	}
	return r.send(ctx, body)
}

// ReportJobFailure reports a job the runner could not start.
func (r *Reporter) ReportJobFailure(jobType, class, code string) {
	if r == nil || !r.Enabled() {
		return
	}
	r.Report(NewJobFailure(r.Env(), r.newID(), r.now(), jobType, class, code))
}

// start launches the sender goroutine at the first report.
func (r *Reporter) start() {
	r.started.Do(func() {
		r.wg.Add(1)
		go r.worker()
	})
}

func (r *Reporter) worker() {
	defer r.wg.Done()
	for {
		select {
		case <-r.stop:
			return
		case body := <-r.queue:
			ctx, cancel := context.WithTimeout(context.Background(), r.sendTimeout)
			err := r.send(ctx, body)
			cancel()
			r.pending.Add(-1)
			if err != nil {
				// A send failure is not retried: a report is a diagnostic, and
				// a retry loop that fights a dead collector would be a worse
				// bug than the report going missing.
				r.record(err)
			}
		}
	}
}

// send performs one transport call and updates the counters.
func (r *Reporter) send(ctx context.Context, body []byte) error {
	r.inflight.Add(1)
	defer r.inflight.Add(-1)
	if err := r.transport.Send(ctx, r.opts.Endpoint, body); err != nil {
		r.record(err)
		return err
	}
	r.sent.Add(1)
	return nil
}

// record notes a failure, keeping the last one and logging it when it changes.
func (r *Reporter) record(err error) {
	if err == nil {
		return
	}
	r.failed.Add(1)
	r.lastMu.Lock()
	changed := r.lastErr != err.Error()
	r.lastErr = err.Error()
	r.lastMu.Unlock()
	if changed && r.logf != nil {
		r.logf("telemetry: %v", err)
	}
}

// noteUnsent counts a report that could not be sent because no collector is
// configured, and says so once rather than once per event.
func (r *Reporter) noteUnsent() {
	if r == nil {
		return
	}
	r.unsent.Add(1)
	if r.logf != nil && r.warned.CompareAndSwap(false, true) {
		r.logf("telemetry is on, but this build has no collector address compiled in; nothing is being sent")
	}
}

// Flush waits until the queue is empty and nothing is in flight.
func (r *Reporter) Flush(ctx context.Context) error {
	if r == nil {
		return nil
	}
	for {
		if r.pending.Load() == 0 && r.inflight.Load() == 0 {
			return nil
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(5 * time.Millisecond):
		}
	}
}

// Close stops the sender goroutine. It is idempotent.
func (r *Reporter) Close() {
	if r == nil {
		return
	}
	r.closeMu.Lock()
	if r.closed {
		r.closeMu.Unlock()
		return
	}
	r.closed = true
	r.closeMu.Unlock()
	// stop is closed exactly once, guarded by the same flag, and the worker's
	// select is its only reader.
	close(r.stop)
	r.wg.Wait()
}

// Stats is what the Settings screen shows.
type Stats struct {
	Schema    string `json:"schema"`
	Enabled   bool   `json:"enabled"`
	Available bool   `json:"available"`
	Endpoint  string `json:"endpoint,omitempty"`
	// Sent counts reports a collector accepted.
	Sent int64 `json:"sent"`
	// Unsent counts reports built while the switch was on but no collector is
	// configured. It is a separate number from "failed" because it means
	// "nothing was attempted", not "the attempt failed".
	Unsent int64 `json:"unsent"`
	// Dropped counts events discarded because the queue was full.
	Dropped int64 `json:"dropped"`
	// Failed counts sends the collector or the network refused.
	Failed int64 `json:"failed"`
	// Queued is how many are waiting right now.
	Queued int `json:"queued"`
	// LastError is the most recent failure, or empty.
	LastError string `json:"lastError,omitempty"`
}

// Stats returns the current counters.
func (r *Reporter) Stats() Stats {
	if r == nil {
		return Stats{Schema: Schema}
	}
	r.lastMu.Lock()
	last := r.lastErr
	r.lastMu.Unlock()
	return Stats{
		Schema:    Schema,
		Enabled:   r.Enabled(),
		Available: r.Available(),
		Endpoint:  r.opts.Endpoint,
		Sent:      r.sent.Load(),
		Unsent:    r.unsent.Load(),
		Dropped:   r.dropped.Load(),
		Failed:    r.failed.Load(),
		Queued:    len(r.queue),
		LastError: last,
	}
}

// newID makes a random event id. It is never derived from the device: a stable
// per-machine id would be exactly the identifier this package promises not to
// send.
func (r *Reporter) newID() string {
	var buf [16]byte
	if _, err := io.ReadFull(r.rand, buf[:]); err != nil {
		// A failed entropy read must not stop a report: the id is for
		// de-duplication only.
		return fmt.Sprintf("%016x", uint64(r.now().UnixNano()))
	}
	return hex.EncodeToString(buf[:])
}

// FieldNote describes one payload field, for the preview's legend.
type FieldNote struct {
	Name string `json:"name"`
	Note string `json:"note"`
}

// Preview is the document the Settings screen shows when a user asks what would
// be sent. It is built from THIS machine's real facts, so it is not a mock-up:
// only the fields that vary per event are marked as placeholders.
type Preview struct {
	Schema         string      `json:"schema"`
	ConsentVersion string      `json:"consentVersion"`
	Disclosure     string      `json:"disclosure"`
	Enabled        bool        `json:"enabled"`
	Available      bool        `json:"available"`
	Endpoint       string      `json:"endpoint,omitempty"`
	Crash          Event       `json:"crash"`
	JobFailure     Event       `json:"jobFailure"`
	Fields         []FieldNote `json:"fields"`
	NeverSent      []string    `json:"neverSent"`
	Notes          []string    `json:"notes"`
}

// The placeholders mark the fields that differ per event. They are not valid
// values and are not sent: they exist so the preview reads as a document rather
// than as a schema.
const (
	PlaceholderPanicType = "<the kind of error>"
	PlaceholderJobCode   = "<a short code for what went wrong>"
	PlaceholderJobType   = "<the kind of job>"
	PlaceholderClass     = "<runner|platform|input>"
	PlaceholderID        = "<a random number, different every time>"
)

// Preview builds the disclosure document.
func (r *Reporter) Preview() Preview {
	env := r.Env()
	stats := r.Stats()
	at := r.now()
	crash := NewCrash(env, PlaceholderID, at, "run", nil, nil)
	crash.PanicType = PlaceholderPanicType
	crash.Frames = []Frame{
		{Func: "github.com/vecchiotom/reelbolt/runner/internal/ui.(*Shell).startRunner.func1", File: "shell.go", Line: 271},
		{Func: "runtime.gopanic", File: "panic.go", Line: 720},
	}
	job := NewJobFailure(env, PlaceholderID, at, "", "", "")
	job.JobType = PlaceholderJobType
	job.FailureClass = PlaceholderClass
	job.FailureCode = PlaceholderJobCode
	return Preview{
		Schema:         Schema,
		ConsentVersion: ConsentVersion,
		Disclosure:     Disclosure,
		Enabled:        stats.Enabled,
		Available:      stats.Available,
		Endpoint:       stats.Endpoint,
		Crash:          crash,
		JobFailure:     job,
		Fields: []FieldNote{
			{Name: "schema", Note: "the version of the report format"},
			{Name: "kind", Note: "whether the app crashed or a job couldn't start"},
			{Name: "id", Note: "a random number, new for every report and never tied to this computer"},
			{Name: "at", Note: "when it happened"},
			{Name: "runnerVersion", Note: "which version of the app you have, for example 0.4.1"},
			{Name: "os / arch / go", Note: "your operating system and processor type, and the toolchain version the app was built with"},
			{Name: "runtime / runtimeMode", Note: "whether you use Docker or Podman, and how it is set up"},
			{Name: "stage", Note: "which part of the app failed, for example pairing"},
			{Name: "panicType", Note: "the kind of error, never its message"},
			{Name: "frames", Note: "the names of the app's own code that was running, with line numbers, never folder locations"},
			{Name: "jobType", Note: "the kind of job, for example video.compile, never a project name"},
			{Name: "failureClass / failureCode", Note: "a short code for what went wrong, from a fixed list"},
		},
		NeverSent: append([]string(nil), NeverSent...),
		Notes: []string{
			"Nothing is sent until you turn this on, and turning it off stops it at once.",
			"The two reports above are real: they are built from this computer. Only the kind of " +
				"error, the code that was running, the kind of job and the error code change from one report to the next.",
			"A report is sent only when the app crashes or a job can't start. Nothing is sent on a " +
				"schedule, and nothing is sent while everything works.",
		},
	}
}
