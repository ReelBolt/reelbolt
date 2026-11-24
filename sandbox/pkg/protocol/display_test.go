package protocol_test

import (
	"encoding/json"
	"strings"
	"testing"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// An assign without display must serialise exactly as it did before the field
// existed, so an older runner sees no change at all.
func TestAssignWithoutDisplayOmitsTheField(t *testing.T) {
	raw, err := json.Marshal(protocol.AssignPayload{JobType: protocol.JobVideoCompile, Spec: json.RawMessage(`{}`)})
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(raw), "display") {
		t.Fatalf("an assign with no display names it: %s", raw)
	}
}

func TestAssignDisplayRoundTripsAndOmitsEmptyNames(t *testing.T) {
	in := protocol.AssignPayload{
		JobType: protocol.JobVideoCompile,
		Spec:    json.RawMessage(`{}`),
		Display: &protocol.AssignDisplay{WorkspaceName: "Acme", ProjectName: "Launch", ExecutionID: "e-1"},
	}
	raw, err := json.Marshal(in)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(raw), "workflowName") || strings.Contains(string(raw), "projectId") {
		t.Fatalf("empty display names were written: %s", raw)
	}
	var out protocol.AssignPayload
	if err := json.Unmarshal(raw, &out); err != nil {
		t.Fatal(err)
	}
	if out.Display == nil || *out.Display != *in.Display {
		t.Fatalf("display = %+v, want %+v", out.Display, in.Display)
	}
}

// A newer gateway may add display fields; an older runner must not fail on them.
func TestAssignDisplayToleratesUnknownFields(t *testing.T) {
	var out protocol.AssignPayload
	err := json.Unmarshal([]byte(`{"jobType":"video.compile","spec":{},"display":{"projectName":"P","stepName":"later"}}`), &out)
	if err != nil {
		t.Fatalf("an unknown display field broke the decode: %v", err)
	}
	if out.Display.ProjectName != "P" {
		t.Fatalf("display = %+v", out.Display)
	}
}

func TestAssignDisplayEmpty(t *testing.T) {
	var nilDisplay *protocol.AssignDisplay
	if !nilDisplay.Empty() || !(&protocol.AssignDisplay{}).Empty() {
		t.Fatal("a nil or zero display is not empty")
	}
	if (&protocol.AssignDisplay{WorkflowID: "w"}).Empty() {
		t.Fatal("a display with an id is empty")
	}
}
