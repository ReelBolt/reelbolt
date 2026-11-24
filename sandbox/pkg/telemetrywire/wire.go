// Package telemetrywire is the receiving side of the runner's opt-in crash
// reports: the reelbolt.telemetry/1 document and the strict check a collector
// applies before it believes one.
//
// The runner builds these documents in runner/internal/telemetry, whose Event
// struct IS its allowlist: a report can only carry the fields declared there,
// and every value is matched against a bounded pattern before it is written.
// This package states the same allowlist again from the other end, because a
// public, unauthenticated endpoint cannot assume the sender was a runner: a
// document with any field the runner does not write, or any value outside the
// alphabet the runner's own allowlist admits, is refused whole rather than
// trimmed. runner/internal/telemetry's tests decode every event shape the runner
// can produce with Decode, so the two cannot drift apart without a failing test.
package telemetrywire

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"regexp"
	"time"
)

// Schema is the one document version this package accepts.
const Schema = "reelbolt.telemetry/1"

// The two kinds a runner reports.
const (
	KindCrash      = "crash"
	KindJobFailure = "job_failure"
)

// MaxFrames is the runner's own cap on a stack.
const MaxFrames = 16

// MaxBodyBytes is the largest document a collector should read. A real report
// is well under 4 KiB; the margin is for a 16-frame stack of long symbols.
const MaxBodyBytes = 64 << 10

// Frame mirrors the runner's telemetry.Frame.
type Frame struct {
	Func string `json:"func"`
	File string `json:"file,omitempty"`
	Line int    `json:"line,omitempty"`
}

// Report mirrors the runner's telemetry.Event field for field (the runner's
// tests compare the two by their JSON names).
type Report struct {
	Schema string `json:"schema"`
	Kind   string `json:"kind"`
	ID     string `json:"id"`
	At     string `json:"at"`

	RunnerVersion string `json:"runnerVersion,omitempty"`
	OS            string `json:"os,omitempty"`
	Arch          string `json:"arch,omitempty"`
	GoVersion     string `json:"go,omitempty"`
	Runtime       string `json:"runtime,omitempty"`
	RuntimeMode   string `json:"runtimeMode,omitempty"`

	Stage         string  `json:"stage,omitempty"`
	PanicType     string  `json:"panicType,omitempty"`
	Frames        []Frame `json:"frames,omitempty"`
	FramesDropped int     `json:"framesDropped,omitempty"`

	JobType      string `json:"jobType,omitempty"`
	FailureClass string `json:"failureClass,omitempty"`
	FailureCode  string `json:"failureCode,omitempty"`
}

// The patterns, copied from the runner's allowlist (runner/internal/telemetry
// scrub.go) so the two ends admit exactly the same alphabet.
var (
	versionToken  = regexp.MustCompile("^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$")
	codeToken     = regexp.MustCompile("^[a-z][a-z0-9_]{0,63}$")
	jobTypeToken  = regexp.MustCompile("^[a-z][a-z0-9._-]{0,63}$")
	symbolToken   = regexp.MustCompile(`^[A-Za-z0-9_./*\[\](){}<>$,+ -]{1,160}$`)
	baseNameToken = regexp.MustCompile("^[A-Za-z0-9_.-]{1,64}$")
	// idToken is the runner's random event id: 32 hex digits, or 16 when its
	// entropy source failed. A little wider so the check is about shape, not
	// about one generator.
	idToken = regexp.MustCompile("^[A-Za-z0-9_-]{8,64}$")
)

var (
	allowedRuntimes     = map[string]bool{"": true, "docker": true, "podman": true}
	allowedRuntimeModes = map[string]bool{"": true, "rootful": true, "rootless": true, "vm": true}
)

// ErrInvalid wraps every refusal, so a caller can count them under one reason
// without reading the message.
var ErrInvalid = errors.New("telemetrywire: invalid report")

// maxLine bounds a frame's line number and maxDropped a dropped-frame count:
// both are small facts about one stack, and an absurd value is not one.
const (
	maxLine    = 10_000_000
	maxDropped = 10_000
)

// Decode parses and validates one document. It refuses unknown fields, a second
// document after the first, and any value outside the allowlist.
func Decode(body []byte) (Report, error) {
	dec := json.NewDecoder(bytes.NewReader(body))
	dec.DisallowUnknownFields()
	var r Report
	if err := dec.Decode(&r); err != nil {
		return Report{}, fmt.Errorf("%w: %w", ErrInvalid, err)
	}
	if _, err := dec.Token(); !errors.Is(err, io.EOF) {
		return Report{}, fmt.Errorf("%w: trailing data after the document", ErrInvalid)
	}
	if err := r.Validate(); err != nil {
		return Report{}, err
	}
	return r, nil
}

// Validate applies the allowlist to a decoded report.
func (r Report) Validate() error {
	bad := func(field string) error { return fmt.Errorf("%w: %s", ErrInvalid, field) }
	if r.Schema != Schema {
		return bad("schema")
	}
	if !idToken.MatchString(r.ID) {
		return bad("id")
	}
	if _, err := time.Parse(time.RFC3339, r.At); err != nil {
		return bad("at")
	}
	for field, v := range map[string]string{
		"runnerVersion": r.RunnerVersion, "os": r.OS, "arch": r.Arch, "go": r.GoVersion,
	} {
		if v != "" && !versionToken.MatchString(v) {
			return bad(field)
		}
	}
	if !allowedRuntimes[r.Runtime] || !allowedRuntimeModes[r.RuntimeMode] {
		return bad("runtime")
	}
	if r.Runtime == "" && r.RuntimeMode != "" {
		return bad("runtimeMode")
	}

	crashFields := r.Stage != "" || r.PanicType != "" || len(r.Frames) > 0 || r.FramesDropped != 0
	jobFields := r.JobType != "" || r.FailureClass != "" || r.FailureCode != ""
	switch r.Kind {
	case KindCrash:
		if jobFields {
			return bad("a crash carries job-failure fields")
		}
		if r.Stage != "" && !codeToken.MatchString(r.Stage) {
			return bad("stage")
		}
		if r.PanicType != "" && !symbolToken.MatchString(r.PanicType) {
			return bad("panicType")
		}
		if len(r.Frames) > MaxFrames {
			return bad("frames")
		}
		for _, f := range r.Frames {
			if !symbolToken.MatchString(f.Func) {
				return bad("frames.func")
			}
			if f.File != "" && !baseNameToken.MatchString(f.File) {
				return bad("frames.file")
			}
			if f.Line < 0 || f.Line > maxLine {
				return bad("frames.line")
			}
		}
		if r.FramesDropped < 0 || r.FramesDropped > maxDropped {
			return bad("framesDropped")
		}
	case KindJobFailure:
		if crashFields {
			return bad("a job failure carries crash fields")
		}
		if r.JobType != "" && !jobTypeToken.MatchString(r.JobType) {
			return bad("jobType")
		}
		if r.FailureClass != "" && !codeToken.MatchString(r.FailureClass) {
			return bad("failureClass")
		}
		if r.FailureCode != "" && !codeToken.MatchString(r.FailureCode) {
			return bad("failureCode")
		}
	default:
		return bad("kind")
	}
	return nil
}
