package telemetry

import (
	"encoding/json"
	"fmt"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

// Schema identifies the payload shape. A collector can therefore refuse a
// document it was not written for rather than mis-reading one.
const Schema = "reelbolt.telemetry/1"

// Kind is what happened. The set is closed: two events are reportable, and a
// kind outside it is not constructed anywhere.
type Kind string

const (
	// KindCrash is a panic one of the runner's own guarded goroutines caught.
	KindCrash Kind = "crash"
	// KindJobFailure is a job the runner REFUSED to start, or whose session
	// could not be created. It is deliberately not "a job that failed": a
	// session that starts and then fails reports its outcome to the GATEWAY, so
	// this is the narrower and honest fact - the runner's own setup path failed.
	KindJobFailure Kind = "job_failure"
)

// Env is the machine's own facts, read at the moment of the report.
//
// Every field is a low-cardinality token with no user data in it. There is
// deliberately no device id, no workspace id, no device name, no config path
// and no gateway URL: those identify the machine or the tenant, and a crash
// report does not need them to be useful.
type Env struct {
	// Version is the runner's own version string.
	Version string
	// OS and Arch are runtime.GOOS and runtime.GOARCH.
	OS   string
	Arch string
	// GoVersion is runtime.Version().
	GoVersion string
	// RuntimeKind and RuntimeMode are E3's probe answer ("docker"/"podman" and
	// "rootful"/"rootless"/"vm"), empty when this machine has no runtime.
	RuntimeKind string
	RuntimeMode string
}

// sanitized applies the allowlist to every field.
func (e Env) sanitized() Env {
	kind, mode := AllowRuntime(e.RuntimeKind, e.RuntimeMode)
	return Env{
		Version:     AllowToken(e.Version),
		OS:          AllowToken(e.OS),
		Arch:        AllowToken(e.Arch),
		GoVersion:   AllowToken(e.GoVersion),
		RuntimeKind: kind,
		RuntimeMode: mode,
	}
}

// Frame is one stack frame, reduced to what is safe to send: a function symbol,
// a BARE FILE NAME and a line number. A path never reaches here - it is turned
// into its base name, and dropped when even that is not a plain name.
type Frame struct {
	Func string `json:"func"`
	File string `json:"file,omitempty"`
	Line int    `json:"line,omitempty"`
}

// Event is the whole wire payload.
//
// THIS STRUCT IS THE ALLOWLIST. A collector can only ever receive the fields
// below, so a panic message, a file path, a project name, a workspace id or a
// presigned URL has nowhere to be written even if a future caller wanted to
// send one.
type Event struct {
	Schema string `json:"schema"`
	Kind   Kind   `json:"kind"`
	// ID is a random identifier for de-duplication. It is generated per event
	// and is never derived from the device, the workspace or the job.
	ID string `json:"id"`
	// At is the observation time, UTC, RFC3339.
	At string `json:"at"`

	RunnerVersion string `json:"runnerVersion,omitempty"`
	OS            string `json:"os,omitempty"`
	Arch          string `json:"arch,omitempty"`
	GoVersion     string `json:"go,omitempty"`
	Runtime       string `json:"runtime,omitempty"`
	RuntimeMode   string `json:"runtimeMode,omitempty"`

	// Stage names which guarded goroutine caught the panic: "probe", "run" or
	// "pairing". It exists so two crashes of the same type can be told apart
	// without reading a stack. A panic in a goroutine nobody guards still takes
	// the process down - this package adds reporting, not supervision - and
	// leaves no report.
	Stage string `json:"stage,omitempty"`
	// PanicType is the panic VALUE'S GO TYPE - "runtime.boundsError", "string",
	// "*errors.errorString". The value itself is never read: the message a
	// panic carries routinely quotes the data that caused it, which on this
	// machine can be a path, a URL or a project's own text.
	PanicType string  `json:"panicType,omitempty"`
	Frames    []Frame `json:"frames,omitempty"`
	// FramesDropped counts stack frames whose symbols failed the allowlist or
	// that were beyond the cap. A count is a fact about the report; the frames
	// themselves are not worth loosening the rule for.
	FramesDropped int `json:"framesDropped,omitempty"`

	// JobType is the PROTOCOL's job type ("video.compile"), never a project,
	// a file or a workflow name.
	JobType string `json:"jobType,omitempty"`
	// FailureClass and FailureCode are the protocol's own bounded values
	// ("runner", "stage_failed"). The failure MESSAGE is deliberately absent:
	// it is free text, and free text is what this payload cannot carry.
	FailureClass string `json:"failureClass,omitempty"`
	FailureCode  string `json:"failureCode,omitempty"`
}

// maxFrames bounds a stack so a runaway recursion cannot produce a large
// document. Sixteen frames is enough to see the shape of a failure.
const maxFrames = 16

// NewCrash builds a crash event. The panic value is used only through its TYPE.
func NewCrash(env Env, id string, at time.Time, stage string, panicValue any, stack []byte) Event {
	e := base(env, id, at, KindCrash)
	e.Stage = AllowCode(stage)
	e.PanicType = AllowSymbol(panicType(panicValue))
	frames, dropped := ParseFrames(stack, maxFrames)
	e.Frames = frames
	e.FramesDropped = dropped
	return e
}

// NewJobFailure builds a job-setup-failure event.
func NewJobFailure(env Env, id string, at time.Time, jobType, class, code string) Event {
	e := base(env, id, at, KindJobFailure)
	e.JobType = AllowJobType(jobType)
	e.FailureClass = AllowCode(class)
	e.FailureCode = AllowCode(code)
	return e
}

func base(env Env, id string, at time.Time, kind Kind) Event {
	s := env.sanitized()
	if at.IsZero() {
		at = time.Now()
	}
	return Event{
		Schema:        Schema,
		Kind:          kind,
		ID:            id,
		At:            at.UTC().Format(time.RFC3339),
		RunnerVersion: s.Version,
		OS:            s.OS,
		Arch:          s.Arch,
		GoVersion:     s.GoVersion,
		Runtime:       s.RuntimeKind,
		RuntimeMode:   s.RuntimeMode,
	}
}

// panicType names the dynamic type of a recovered value, and nothing else.
//
// A panic whose value is a string or an error is reported by its KIND and not
// by its content: on this machine the value routinely IS the message, and the
// message routinely quotes a path, a URL or a project's text.
func panicType(v any) string {
	if v == nil {
		return Unknown
	}
	switch v.(type) {
	case string:
		return "string"
	case error:
		return "error"
	}
	return fmt.Sprintf("%T", v)
}

// Payload serialises an event.
func (e Event) Payload() ([]byte, error) { return json.Marshal(e) }

// ParseFrames reduces a Go stack trace to allowlisted frames.
//
// The trace's own shape is a symbol line followed by an indented "file:line"
// line, so the walk is two-state rather than a parser. Anything it does not
// recognise is counted and dropped, never forwarded.
func ParseFrames(stack []byte, limit int) ([]Frame, int) {
	lines := strings.Split(string(stack), "\n")
	frames := make([]Frame, 0, limit)
	dropped := 0
	pending := ""
	for _, raw := range lines {
		line := strings.TrimRight(raw, "\r")
		if strings.HasPrefix(line, "\t") {
			loc := strings.TrimSpace(line)
			// Strip the "+0x1234" program-counter offset the runtime appends.
			if i := strings.Index(loc, " +0x"); i > 0 {
				loc = loc[:i]
			}
			file, number := splitLocation(loc)
			frames, dropped = addFrame(frames, Frame{
				Func: AllowSymbol(pending),
				File: AllowFile(file),
				Line: number,
			}, limit, dropped)
			pending = ""
			continue
		}
		trimmed := strings.TrimSpace(line)
		switch {
		case trimmed == "":
		case strings.HasPrefix(trimmed, "goroutine "):
			// The header, including its goroutine number: not a frame.
		case strings.HasPrefix(trimmed, "created by "):
			frames, dropped = addFrame(frames, Frame{Func: AllowSymbol(trimmed)}, limit, dropped)
		case strings.HasPrefix(trimmed, "panic:") || strings.HasPrefix(trimmed, "fatal error:"):
			// A panic MESSAGE lives here. It is read by nobody: the line is
			// skipped rather than parsed, which is the whole point.
		default:
			pending = trimmed
		}
	}
	return frames, dropped
}

// addFrame keeps a frame whose symbol passed the allowlist, and counts the rest.
func addFrame(frames []Frame, f Frame, limit, dropped int) ([]Frame, int) {
	if f.Func == Unknown || f.Func == "" {
		if f.File != "" || f.Line != 0 {
			dropped++
		}
		return frames, dropped
	}
	if len(frames) >= limit {
		return frames, dropped + 1
	}
	return append(frames, f), dropped
}

// splitLocation separates "path/to/file.go:42" into a base name and a line.
func splitLocation(loc string) (string, int) {
	i := strings.LastIndex(loc, ":")
	if i <= 0 {
		return filepath.Base(loc), 0
	}
	file := filepath.Base(loc[:i])
	line, err := strconv.Atoi(loc[i+1:])
	if err != nil {
		return file, 0
	}
	return file, line
}
