package main

import (
	"context"
	"errors"
	"testing"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// TestRuntimeRefusesAJobTypeItDidNotAdvertise pins the two refusals New can
// make. Both are bug catchers — the gateway filters by the capability document —
// but a refusal that names the wrong cause sends an operator to the wrong
// machine, so the codes are asserted rather than described.
func TestRuntimeRefusesAJobTypeItDidNotAdvertise(t *testing.T) {
	rt := &runtimeState{}

	_, err := rt.New(context.Background(), &agent.Job{JobType: protocol.JobRemotionWorkspace})
	var jobErr *agent.JobError
	if !asJobError(err, &jobErr) {
		t.Fatalf("a remotion job with no runtime returned %v, want a JobError", err)
	}
	if jobErr.Code != protocol.ErrCodeContainerUnavailable || jobErr.Class != protocol.FailRunner || !jobErr.Retryable {
		t.Fatalf("remotion refusal = %+v, want container_unavailable/runner/retryable", jobErr)
	}

	_, err = rt.New(context.Background(), &agent.Job{JobType: "video.dance"})
	if !asJobError(err, &jobErr) {
		t.Fatalf("an unknown job type returned %v, want a JobError", err)
	}
	if jobErr.Code != protocol.ErrCodeUnsupportedOp || jobErr.Class != protocol.FailInput || jobErr.Retryable {
		t.Fatalf("unknown-type refusal = %+v, want unsupported_op/input/not retryable", jobErr)
	}
}

// TestContainerCLIsNeverFallsBack is the operator's explicit choice: a worker
// told to use podman must not quietly use docker instead.
func TestContainerCLIsNeverFallsBack(t *testing.T) {
	if got := containerCLIs(""); got != nil {
		t.Fatalf("containerCLIs(\"\") = %v, want nil so the probe's own default applies", got)
	}
	got := containerCLIs("podman")
	if len(got) != 1 || got[0] != "podman" {
		t.Fatalf("containerCLIs(\"podman\") = %v, want exactly podman", got)
	}
}

// TestHasJobType covers the helper the capability decision rests on.
func TestHasJobType(t *testing.T) {
	caps := protocol.Capabilities{JobTypes: []string{protocol.JobVideoCompile}}
	if !hasJobType(caps, protocol.JobVideoCompile) {
		t.Fatal("hasJobType missed an advertised type")
	}
	if hasJobType(caps, protocol.JobRemotionWorkspace) {
		t.Fatal("hasJobType invented a type that was not advertised")
	}
	// marshalCapabilities is only ever used to explain a failure; it must not
	// itself fail.
	if marshalCapabilities(caps) == "" {
		t.Fatal("marshalCapabilities returned nothing")
	}
}

func asJobError(err error, target **agent.JobError) bool {
	if err == nil {
		return false
	}
	return errors.As(err, target)
}
