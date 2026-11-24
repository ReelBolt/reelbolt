package protocol_test

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// envelope is the minimal wire envelope from docs/runner-protocol.md. This test
// keeps the golden fixtures valid JSON with the required fields; the typed
// contract test below checks them against sandbox/pkg/protocol.
type envelope struct {
	V         int             `json:"v"`
	Type      string          `json:"type"`
	ID        string          `json:"id"`
	JobID     string          `json:"jobId"`
	SessionID string          `json:"sessionId"`
	Payload   json.RawMessage `json:"payload"`
}

var messageTypes = []string{
	"hello", "welcome", "heartbeat", "ready", "assign", "accept", "reject", "progress",
	"op.request", "op.response", "complete", "fail", "cancel", "drain", "bye",
	"llm.request", "llm.response.start", "llm.response.chunk", "llm.response.end", "llm.error", "llm.cancel",
}

// Types that are scoped to a job; the rest are connection-scoped.
var jobScoped = map[string]bool{
	"assign": true, "accept": true, "reject": true, "progress": true, "op.request": true,
	"op.response": true, "complete": true, "fail": true, "cancel": true,
}

func fixtureFiles(t *testing.T) []string {
	t.Helper()
	files, err := filepath.Glob("fixtures/*.json")
	if err != nil {
		t.Fatal(err)
	}
	if len(files) == 0 {
		t.Fatal("no fixtures found")
	}
	return files
}

func TestFixturesParseAgainstEnvelope(t *testing.T) {
	seen := map[string]int{}
	known := map[string]bool{}
	for _, mt := range messageTypes {
		known[mt] = true
	}
	for _, f := range fixtureFiles(t) {
		name := filepath.Base(f)
		raw, err := os.ReadFile(f)
		if err != nil {
			t.Fatal(err)
		}
		var env envelope
		if err := json.Unmarshal(raw, &env); err != nil {
			t.Errorf("%s: invalid JSON: %v", name, err)
			continue
		}
		if env.V != 1 {
			t.Errorf("%s: v = %d, want 1", name, env.V)
		}
		if env.ID == "" {
			t.Errorf("%s: missing id", name)
		}
		if !known[env.Type] {
			t.Errorf("%s: unknown type %q", name, env.Type)
			continue
		}
		if !strings.HasPrefix(name, env.Type+".") {
			t.Errorf("%s: file name must start with %q", name, env.Type+".")
		}
		if len(env.Payload) == 0 || env.Payload[0] != '{' {
			t.Errorf("%s: payload must be a JSON object", name)
		}
		if jobScoped[env.Type] && env.JobID == "" {
			t.Errorf("%s: job-scoped type %s needs jobId", name, env.Type)
		}
		seen[env.Type]++
	}
	for _, mt := range messageTypes {
		if seen[mt] == 0 {
			t.Errorf("no fixture for message type %q", mt)
		}
	}
}

// TestFixturesRoundTripThroughTypedModels is the contract test the specification
// asks the gateway for: every golden message must decode into its typed payload
// in sandbox/pkg/protocol and re-encode to the same JSON tree.
//
// Two comparisons, because they catch different mistakes. The JSON tree
// comparison (with empty and absent values treated alike) catches a wrong or
// missing struct tag anywhere in the payload. The per-type checks below catch a
// payload that parses but does not bind — the fields that actually drive
// behaviour being read back empty.
func TestFixturesRoundTripThroughTypedModels(t *testing.T) {
	for _, file := range fixtureFiles(t) {
		name := filepath.Base(file)
		t.Run(name, func(t *testing.T) {
			raw, err := os.ReadFile(file)
			if err != nil {
				t.Fatal(err)
			}
			env, err := protocol.Decode(raw)
			if err != nil {
				t.Fatalf("Decode: %v", err)
			}
			if err := env.Validate(); err != nil {
				t.Fatalf("Validate: %v", err)
			}
			payload, err := env.TypedPayload()
			if err != nil {
				t.Fatalf("TypedPayload: %v", err)
			}
			encoded, err := env.Encode()
			if err != nil {
				t.Fatalf("Encode: %v", err)
			}
			if got, want := canonicalJSON(t, encoded), canonicalJSON(t, raw); got != want {
				t.Errorf("re-encoded fixture differs from the golden message\n got: %s\nwant: %s", got, want)
			}
			// Decoding our own output must land on the same payload.
			again, err := protocol.Decode(encoded)
			if err != nil {
				t.Fatalf("Decode of our own encoding: %v", err)
			}
			if _, err := again.TypedPayload(); err != nil {
				t.Fatalf("TypedPayload of our own encoding: %v", err)
			}
			checkPayload(t, env.Type, payload)
		})
	}
}

// checkPayload asserts the fields a message cannot work without are bound.
func checkPayload(t *testing.T, ty protocol.MessageType, payload any) {
	t.Helper()
	switch p := payload.(type) {
	case *protocol.HelloPayload:
		if p.ProtocolVersion != protocol.ProtocolVersion {
			t.Errorf("protocolVersion = %q", p.ProtocolVersion)
		}
		if p.Auth.Kind != protocol.AuthKindDevice && p.Auth.Kind != protocol.AuthKindPool {
			t.Errorf("auth.kind = %q", p.Auth.Kind)
		}
		if p.Capabilities.Container == protocol.ContainerNone && p.Capabilities.Supports(protocol.JobRemotionWorkspace) {
			t.Error("a container-less runner advertises a Remotion job type")
		}
		if p.Capabilities.Slots < 1 || len(p.Capabilities.JobTypes) == 0 {
			t.Errorf("capabilities = %+v", p.Capabilities)
		}
		if l := p.Capabilities.LocalLLM; l != nil {
			if err := p.Capabilities.Validate(); err != nil || !l.Enabled || len(l.Models) == 0 {
				t.Errorf("localLlm = %+v (%v)", l, err)
			}
		}
	case *protocol.WelcomePayload:
		if p.RunnerID == "" || p.ProtocolVersion == "" || p.GatewayInstance == "" {
			t.Errorf("welcome = %+v", p)
		}
		if p.HeartbeatSec != protocol.HeartbeatSec || p.DeadAfterSec != protocol.DeadAfterSec {
			t.Errorf("welcome timings = %d/%d", p.HeartbeatSec, p.DeadAfterSec)
		}
		if p.MaxInlineBytes != protocol.MaxPayloadBytes {
			t.Errorf("maxInlineBytes = %d", p.MaxInlineBytes)
		}
		if p.Resumed == nil {
			t.Error("welcome.resumed must be an array, not null")
		}
	case *protocol.HeartbeatPayload:
		if p.FreeSlots == nil && p.ServerTime == "" {
			t.Error("a heartbeat is neither a runner heartbeat nor a gateway heartbeat")
		}
	case *protocol.ReadyPayload:
		if p.FreeSlots < 1 {
			t.Errorf("ready.freeSlots = %d", p.FreeSlots)
		}
	case *protocol.AssignPayload:
		if p.JobType == "" || p.JobToken == "" || p.LeaseEpoch == 0 || p.Attempt == 0 {
			t.Errorf("assign = %+v", p)
		}
		if len(p.Spec) == 0 || p.Limits.MaxOutputBytes == 0 {
			t.Errorf("assign.spec/limits are empty: %+v", p)
		}
		switch p.JobType {
		case protocol.JobRemotionWorkspace:
			var spec protocol.RemotionWorkspaceSpec
			if err := p.SpecInto(&spec); err != nil || spec.ExecutionID == "" || spec.Template.Name == "" {
				t.Errorf("remotion spec = %+v (%v)", spec, err)
			}
		case protocol.JobVideoCompile:
			var spec protocol.VideoCompileSpec
			if err := p.SpecInto(&spec); err != nil || spec.ExecutionID == "" || spec.StepID == "" || spec.Mode == "" {
				t.Errorf("compile spec = %+v (%v)", spec, err)
			}
		case protocol.JobVideoAnalyzeEx:
			var spec protocol.VideoAnalyzeExtractSpec
			if err := p.SpecInto(&spec); err != nil || spec.ExecutionID == "" || spec.StepID == "" {
				t.Errorf("extract spec = %+v (%v)", spec, err)
			}
		default:
			t.Errorf("unexpected job type %q", p.JobType)
		}
	case *protocol.AcceptPayload:
		if p.JobToken == "" || p.LeaseEpoch == 0 {
			t.Errorf("accept = %+v", p)
		}
	case *protocol.RejectPayload:
		if p.JobToken == "" || p.Reason == "" {
			t.Errorf("reject = %+v", p)
		}
	case *protocol.ProgressPayload:
		if p.JobToken == "" || p.Stage == "" {
			t.Errorf("progress = %+v", p)
		}
	case *protocol.OpRequestPayload:
		if p.OpID == "" || p.Op == "" || p.TimeoutSec == 0 {
			t.Errorf("op.request = %+v", p)
		}
		var args map[string]any
		if err := p.ArgsInto(&args); err != nil {
			t.Errorf("op.request.args: %v", err)
		}
	case *protocol.OpResponsePayload:
		if p.OpID == "" || p.JobToken == "" {
			t.Errorf("op.response = %+v", p)
		}
		if p.OK && len(p.Result) == 0 {
			t.Error("a successful op.response carries no result")
		}
		if !p.OK && p.Error == nil {
			t.Error("a failed op.response carries no error")
		}
	case *protocol.CompletePayload:
		if p.Outcome != protocol.OutcomeSucceeded && p.Outcome != protocol.OutcomeCancelled {
			t.Errorf("complete.outcome = %q", p.Outcome)
		}
	case *protocol.FailPayload:
		switch p.Class {
		case protocol.FailPlatform, protocol.FailRunner, protocol.FailInput:
		default:
			t.Errorf("fail.class = %q", p.Class)
		}
		if p.Code == "" || p.Message == "" {
			t.Errorf("fail = %+v", p)
		}
	case *protocol.CancelPayload:
		if p.Reason == "" || p.GraceSec == 0 {
			t.Errorf("cancel = %+v", p)
		}
	case *protocol.DrainPayload:
		if p.Reason == "" {
			t.Errorf("drain = %+v", p)
		}
	case *protocol.ByePayload:
		if p.Reason == "" {
			t.Errorf("bye = %+v", p)
		}
	case *protocol.LLMRequestPayload:
		if err := p.Validate(); err != nil || !protocol.LLMPathAllowed(p.Method, p.Path) || p.TimeoutSec == 0 {
			t.Errorf("llm.request = %+v (%v)", p, err)
		}
	case *protocol.LLMResponseStartPayload:
		if p.RequestID == "" || p.Status == 0 {
			t.Errorf("llm.response.start = %+v", p)
		}
	case *protocol.LLMResponseChunkPayload:
		if err := p.Validate(); err != nil || p.Data == "" {
			t.Errorf("llm.response.chunk = %+v (%v)", p, err)
		}
	case *protocol.LLMResponseEndPayload:
		if p.RequestID == "" || p.Bytes == 0 {
			t.Errorf("llm.response.end = %+v", p)
		}
	case *protocol.LLMErrorPayload:
		if p.RequestID == "" || p.Code == "" {
			t.Errorf("llm.error = %+v", p)
		}
	case *protocol.LLMCancelPayload:
		if p.RequestID == "" {
			t.Errorf("llm.cancel = %+v", p)
		}
	default:
		t.Errorf("%s: no typed payload for the fixture", ty)
	}
}

// canonicalJSON renders a message as a sorted JSON tree with empty strings,
// empty arrays, empty objects and nulls removed, so a field the wire allows to
// be present-but-empty compares equal to the same field omitted.
func canonicalJSON(t *testing.T, raw []byte) string {
	t.Helper()
	var value any
	if err := json.Unmarshal(raw, &value); err != nil {
		t.Fatalf("unmarshal %s: %v", raw, err)
	}
	out, err := json.Marshal(stripEmpty(value))
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	return string(out)
}

func stripEmpty(value any) any {
	switch v := value.(type) {
	case map[string]any:
		out := map[string]any{}
		for key, item := range v {
			cleaned := stripEmpty(item)
			if isEmptyValue(cleaned) {
				continue
			}
			out[key] = cleaned
		}
		return out
	case []any:
		out := make([]any, 0, len(v))
		for _, item := range v {
			if cleaned := stripEmpty(item); !isEmptyValue(cleaned) {
				out = append(out, cleaned)
			}
		}
		return out
	default:
		return value
	}
}

func isEmptyValue(value any) bool {
	switch v := value.(type) {
	case nil:
		return true
	case string:
		return v == ""
	case []any:
		return len(v) == 0
	case map[string]any:
		return len(v) == 0
	default:
		return false
	}
}
