package agent

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"reflect"
	"regexp"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// ---------------------------------------------------------------------------
// SECURITY INVARIANTS (docs/runner-protocol.md), one test each.
//
// "These hold for every message, op, fixture and implementation. They are the
// review checklist for the gateway, the worker library, the engine client and
// the desktop app."
// ---------------------------------------------------------------------------

// providerish matches a field name that could hold a provider credential or a
// provider reference. The worker library must have no such field anywhere.
var providerish = regexp.MustCompile("(?i)(provider|apikey|api_key|secret|password|inference|model|endpoint|env)")

// billingish matches a field name that could make a runner-reported number look
// like a price. Billing is priced from the platform-side job spec.
var billingish = regexp.MustCompile("(?i)(credit|cost|price|billed|billing|charge|amount)")

// INVARIANT: "No message or op ever carries a provider credential."
//
// The strongest available form of this check is structural: the library has no
// field that could hold one, in its configuration, in a job it hands to a
// session, or in any message it can build.
func TestNoProviderCredentialCanBeCarried(t *testing.T) {
	types := []any{
		Config{}, Identity{}, Job{},
		protocol.HelloAuth{}, protocol.HelloPayload{}, protocol.Capabilities{},
		protocol.WelcomePayload{}, protocol.AssignPayload{}, protocol.AssignLimits{},
		protocol.AssignRequires{}, protocol.AcceptPayload{}, protocol.RejectPayload{},
		protocol.ProgressPayload{}, protocol.OpRequestPayload{}, protocol.OpResponsePayload{},
		protocol.CompletePayload{}, protocol.FailPayload{}, protocol.CancelPayload{},
		protocol.DrainPayload{}, protocol.ByePayload{}, protocol.JobTokenClaims{},
	}
	for _, v := range types {
		for _, f := range exportedFields(reflect.TypeOf(v)) {
			if providerish.MatchString(f) {
				t.Errorf("%T.%s could hold a provider credential or reference; the runner never carries one", v, f)
			}
			if billingish.MatchString(f) {
				t.Errorf("%T.%s is a billing-shaped field; billing is priced from the platform-side job spec", v, f)
			}
		}
	}
}

// A second, mechanical form: the library does not link a provider SDK, so it
// cannot call a model provider however it is configured.
func TestTheWorkerLibraryLinksNoProviderSDK(t *testing.T) {
	entries, err := os.ReadDir(".")
	if err != nil {
		t.Fatalf("read the package directory: %v", err)
	}
	// A module path that names a provider is enough: there is no legitimate
	// reason for the runner to import one.
	denied := []string{"openai", "anthropic", "azure", "gemini", "minimax", "fish", "deepseek", "bedrock", "vertex"}
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".go") {
			continue
		}
		source, err := os.ReadFile(e.Name())
		if err != nil {
			t.Fatalf("read %s: %v", e.Name(), err)
		}
		for _, line := range strings.Split(string(source), "\n") {
			trimmed := strings.TrimSpace(line)
			if !strings.HasPrefix(trimmed, "\"") || !strings.Contains(trimmed, "/") {
				continue
			}
			importPath := strings.ToLower(trimmed)
			for _, d := range denied {
				if strings.Contains(importPath, d) {
					t.Errorf("%s imports %s: the runner never talks to a model provider", e.Name(), trimmed)
				}
			}
		}
	}
}

// INVARIANT: "Billing is priced from the platform-side job spec ... Nothing in
// complete.result, complete.metrics, progress or any runner-reported timing,
// size or count changes what a customer is charged."
//
// The reflection test above covers the fields; this one covers what actually
// goes out on the wire during a whole job.
func TestRunnerMessagesCarryNoBillingOrProviderMaterial(t *testing.T) {
	ta := startTestAgent(t)
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)
	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID))
	c.nextOf(t, protocol.TypeAccept)
	waitFor(t, "the session to start", func() bool { return ta.factory.sessionCount() == 1 })
	c.sendJob(t, protocol.TypeOpRequest, testJobID, testSessionID, protocol.OpRequestPayload{
		OpID: "op-1", Op: protocol.OpStatus, Args: json.RawMessage([]byte("{}")), TimeoutSec: 5,
	})
	c.nextOf(t, protocol.TypeOpResponse)
	c.sendJob(t, protocol.TypeCancel, testJobID, testSessionID, protocol.CancelPayload{Reason: protocol.CancelUser, GraceSec: 5})
	c.nextOf(t, protocol.TypeComplete)

	frames := c.framesWritten()
	if len(frames) < 3 {
		t.Fatalf("only %d frames were captured; the test is not observing the job", len(frames))
	}
	forbidden := []string{"credit", "price", "cost", "billed", "apikey", "api_key", "authorization", "bearer ", "sk-", "env:", "provider"}
	for _, raw := range frames {
		lower := strings.ToLower(string(raw))
		for _, f := range forbidden {
			if strings.Contains(lower, f) {
				t.Errorf("a runner message carries %q: %s", f, raw)
			}
		}
	}
}

// "Credentials, device signatures and job tokens MUST NOT be logged. Presigned
// URLs MUST be logged with the query string removed."
func TestLogsNeverCarrySecrets(t *testing.T) {
	const jobToken = "SECRET.job.token.value"
	signedURL := "https://storage.example.invalid/bucket/key?X-Amz-Signature=deadbeef&X-Amz-Credential=leak"
	ta := startTestAgent(t)
	c := ta.gw.awaitDial(t)
	frame := c.nextOf(t, protocol.TypeHello)
	hello, err := protocol.DecodePayload[protocol.HelloPayload](frame.env)
	if err != nil {
		t.Fatalf("hello payload: %v", err)
	}
	c.send(t, protocol.TypeWelcome, protocol.WelcomePayload{
		RunnerID: "runner-1", Kind: protocol.KindDesktop,
		ProtocolVersion: protocol.ProtocolVersion, HeartbeatSec: 1,
		ServerTime: time.Now().UTC().Format(time.RFC3339), Resumed: []protocol.JobLease{},
	})

	payload := assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID)
	payload.JobToken = jobToken
	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, payload)
	c.nextOf(t, protocol.TypeAccept)
	waitFor(t, "the session to start", func() bool { return ta.factory.sessionCount() == 1 })

	// A session that logs a presigned URL is the stand-in for any log line
	// written near one; the library's own redaction is protocol.RedactURL's job
	// (see transfer.go) and this asserts nothing of the sort ever reaches the
	// logger from the agent itself.
	ta.factory.last().handle = func(ctx context.Context, op string, args json.RawMessage) (any, *protocol.OpError) {
		var a map[string]any
		_ = json.Unmarshal(args, &a)
		if url, ok := a["url"].(string); ok {
			ta.log.Printf("staging from %s", RedactForLog(url))
		}
		return map[string]any{"ok": true}, nil
	}
	c.sendJob(t, protocol.TypeOpRequest, testJobID, testSessionID, protocol.OpRequestPayload{
		OpID: "op-stage", Op: protocol.OpStage,
		Args:       json.RawMessage([]byte(fmt.Sprintf("{\"url\":%q,\"path\":\"a.mp4\"}", signedURL))),
		TimeoutSec: 5,
	})
	c.nextOf(t, protocol.TypeOpResponse)
	c.sendJob(t, protocol.TypeCancel, testJobID, testSessionID, protocol.CancelPayload{Reason: protocol.CancelUser, GraceSec: 5})
	c.nextOf(t, protocol.TypeComplete)
	time.Sleep(20 * time.Millisecond)

	logs := ta.log.String()
	if logs == "" {
		t.Fatal("the agent logged nothing; this test cannot prove anything")
	}
	if strings.Contains(logs, jobToken) {
		t.Errorf("the job token reached the log:\n%s", logs)
	}
	if strings.Contains(logs, hello.Auth.Sig) {
		t.Errorf("the device signature reached the log:\n%s", logs)
	}
	if strings.Contains(logs, "X-Amz-Signature") || strings.Contains(logs, "X-Amz-Credential") {
		t.Errorf("a presigned URL was logged with its query string:\n%s", logs)
	}
	if !strings.Contains(logs, "storage.example.invalid") {
		t.Errorf("the test's own log line never ran, so nothing was proven:\n%s", logs)
	}
}

// "It never mounts anything outside its own scratch root and never follows a
// presigned URL it was not handed in an op."
//
// A whole job that never hands the runner a URL must produce no HTTP request at
// all: the library has no other reason to reach the network.
func TestNoHTTPRequestWithoutAnOpURL(t *testing.T) {
	counter := &countingTransport{}
	ta := startTestAgent(t, func(cfg *Config) {
		cfg.HTTPClient = &http.Client{Transport: counter, Timeout: 5 * time.Second}
	})
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)
	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID))
	c.nextOf(t, protocol.TypeAccept)
	waitFor(t, "the session to start", func() bool { return ta.factory.sessionCount() == 1 })
	for i := 0; i < 3; i++ {
		c.sendJob(t, protocol.TypeOpRequest, testJobID, testSessionID, protocol.OpRequestPayload{
			OpID: fmt.Sprintf("op-%d", i), Op: protocol.OpStatus, Args: json.RawMessage([]byte("{}")), TimeoutSec: 5,
		})
		c.nextOf(t, protocol.TypeOpResponse)
	}
	if n := counter.count(); n != 0 {
		t.Fatalf("the runner made %d HTTP requests with no URL in any op", n)
	}
}

// A redirect off a presigned URL is where a one-object, one-method grant would
// stop meaning anything, so it is refused rather than followed.
func TestPresignedRedirectsAreNotFollowed(t *testing.T) {
	var redirected bool
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/redirect" {
			http.Redirect(w, r, "/other", http.StatusFound)
			return
		}
		redirected = true
		_, _ = w.Write([]byte("hello"))
	}))
	defer server.Close()

	dir := t.TempDir()
	fs, err := OpenRootFS(dir, 0)
	if err != nil {
		t.Fatalf("OpenRootFS: %v", err)
	}
	defer func() { _ = fs.Close() }() // best-effort teardown
	_, opErr := FetchToFS(context.Background(), DefaultHTTPClient(), fs, server.URL+"/redirect", "a.txt", 1024, "")
	if opErr == nil {
		t.Fatal("the redirect was followed")
	}
	if opErr.Code != protocol.ErrCodeStageFailed {
		t.Fatalf("error code = %q, want stage_failed", opErr.Code)
	}
	if redirected {
		t.Fatal("the runner fetched the redirect target")
	}
}

// "Least authority per credential": a job token covers one job at one epoch and
// is echoed only on that job's messages.
func TestJobTokenAndEpochAreScopedToOneJob(t *testing.T) {
	first := "11111111-1111-4111-8111-111111111111"
	second := "22222222-2222-4222-8222-222222222222"
	ta := startTestAgent(t, withSlots(2))
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)

	p1 := assignPayload(protocol.JobRemotionWorkspace, 7, testSessionID)
	p1.JobToken = "TOKEN.ONE"
	c.sendJob(t, protocol.TypeAssign, first, testSessionID, p1)
	c.nextOf(t, protocol.TypeAccept)
	p2 := assignPayload(protocol.JobRemotionWorkspace, 9, testSessionID)
	p2.JobToken = "TOKEN.TWO"
	c.sendJob(t, protocol.TypeAssign, second, testSessionID, p2)
	c.nextOf(t, protocol.TypeAccept)
	waitFor(t, "both sessions to start", func() bool { return ta.factory.sessionCount() == 2 })

	c.sendJob(t, protocol.TypeOpRequest, second, testSessionID, protocol.OpRequestPayload{
		OpID: "op-second", Op: protocol.OpStatus, Args: json.RawMessage([]byte("{}")), TimeoutSec: 5,
	})
	resp := decodeOpResponse(t, c)
	if resp.JobToken != "TOKEN.TWO" || resp.LeaseEpoch != 9 {
		t.Fatalf("the second job's answer carried the wrong fencing: %+v", resp)
	}
	c.sendJob(t, protocol.TypeOpRequest, first, testSessionID, protocol.OpRequestPayload{
		OpID: "op-first", Op: protocol.OpStatus, Args: json.RawMessage([]byte("{}")), TimeoutSec: 5,
	})
	resp = decodeOpResponse(t, c)
	if resp.JobToken != "TOKEN.ONE" || resp.LeaseEpoch != 7 {
		t.Fatalf("the first job's answer carried the wrong fencing: %+v", resp)
	}
}

// An op for a job this runner does not hold is not answered under someone
// else's fencing: the runner has no token to echo and stays silent rather than
// inventing one.
func TestOpForAnUnknownJobIsNotAnswered(t *testing.T) {
	ta := startTestAgent(t)
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)

	c.sendJob(t, protocol.TypeOpRequest, "33333333-3333-4333-8333-333333333333", testSessionID, protocol.OpRequestPayload{
		OpID: "op-unknown", Op: protocol.OpStatus, Args: json.RawMessage([]byte("{}")), TimeoutSec: 5,
	})
	c.noFrameOf(t, protocol.TypeOpResponse, 200*time.Millisecond)
}

// "The worker MUST keep the results of the last 256 ops per session. A repeated
// opId MUST return the cached op.response without executing the op again."
func TestOpResultCache(t *testing.T) {
	executions := 0
	ta := startTestAgent(t)
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)
	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID))
	c.nextOf(t, protocol.TypeAccept)
	waitFor(t, "the session to start", func() bool { return ta.factory.sessionCount() == 1 })
	ta.factory.last().handle = func(ctx context.Context, op string, args json.RawMessage) (any, *protocol.OpError) {
		executions++
		return map[string]any{"n": executions}, nil
	}

	op := func(id string) protocol.OpResponsePayload {
		c.sendJob(t, protocol.TypeOpRequest, testJobID, testSessionID, protocol.OpRequestPayload{
			OpID: id, Op: protocol.OpStatus, Args: json.RawMessage([]byte("{}")), TimeoutSec: 5,
		})
		return decodeOpResponse(t, c)
	}
	first := op("op-repeat")
	again := op("op-repeat")
	if executions != 1 {
		t.Fatalf("the op ran %d times, want 1", executions)
	}
	if string(first.Result) != string(again.Result) {
		t.Fatalf("the cached answer differs: %s vs %s", first.Result, again.Result)
	}

	// Past the cache's bound the oldest id can only be answered op_expired: it
	// is one the runner has forgotten, not one it never saw.
	for i := 0; i < protocol.OpResultCacheSize; i++ {
		op(fmt.Sprintf("fill-%d", i))
	}
	expired := op("op-repeat")
	if expired.OK || expired.Error == nil || expired.Error.Code != protocol.ErrCodeOpExpired {
		t.Fatalf("a repeat of an evicted opId = %+v, want op_expired", expired)
	}
}

// "A receiver that gets an unknown type MUST ignore the message."
func TestMessageTypesTheRunnerDoesNotKnowAreIgnored(t *testing.T) {
	ta := startTestAgent(t)
	c := ta.gw.awaitDial(t)
	c.handshake(t)
	c.nextOf(t, protocol.TypeReady)
	// A frame from a future minor version: an envelope this package cannot
	// decode, in the shape the protocol reserves for additive change.
	c.sendRaw(t, "{\"v\":1,\"type\":\"heartbeat.v2\",\"id\":\"n1\",\"payload\":{\"serverTime\":\"2026-01-01T00:00:00Z\"}}")
	c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobRemotionWorkspace, 1, testSessionID))
	c.nextOf(t, protocol.TypeAccept)
}

// The fixtures in sandbox/protocol/fixtures are the contract both ends are
// built against; the worker library round-trips every one of them.
func TestFixturesRoundTripThroughTheWorker(t *testing.T) {
	dir := filepath.Join("..", "..", "protocol", "fixtures")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("read the fixtures: %v", err)
	}
	seen := map[protocol.MessageType]bool{}
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".json") {
			continue
		}
		raw, err := os.ReadFile(filepath.Join(dir, e.Name()))
		if err != nil {
			t.Fatalf("read %s: %v", e.Name(), err)
		}
		env, err := protocol.Decode(raw)
		if err != nil {
			t.Errorf("%s does not decode: %v", e.Name(), err)
			continue
		}
		seen[env.Type] = true
		payload, err := env.TypedPayload()
		if err != nil {
			t.Errorf("%s has no typed payload: %v", e.Name(), err)
			continue
		}
		envType := env.Type
		var built protocol.Envelope
		switch envType.Scope() {
		case protocol.ScopeConnection:
			built, err = protocol.New(envType, payload)
		default:
			built, err = protocol.NewJob(envType, env.JobID, env.SessionID, payload)
		}
		if err != nil {
			t.Errorf("%s could not be rebuilt: %v", e.Name(), err)
			continue
		}
		// Compared as JSON with empty arrays and nulls folded away: the
		// protocol requires a payload to carry the fields its type promises,
		// and "the array is absent" and "the array is empty" are the same
		// statement on this wire.
		var from, to any
		if err := json.Unmarshal(env.Payload, &from); err != nil {
			t.Errorf("%s payload: %v", e.Name(), err)
			continue
		}
		if err := json.Unmarshal(built.Payload, &to); err != nil {
			t.Errorf("%s rebuilt payload: %v", e.Name(), err)
			continue
		}
		if !reflect.DeepEqual(foldEmpty(from), foldEmpty(to)) {
			t.Errorf("%s does not round-trip: want %s, got %s", e.Name(), env.Payload, built.Payload)
		}
	}
	for _, typ := range protocol.MessageTypes() {
		if !seen[typ] {
			t.Errorf("the catalogue has no fixture for %s", typ)
		}
	}
}

// Every gateway-to-runner fixture is fed to a live agent: the contract is that
// the worker survives what the gateway actually sends.
func TestGatewayFixturesDriveTheWorker(t *testing.T) {
	dir := filepath.Join("..", "..", "protocol", "fixtures")
	cases := []struct {
		file string
		// want is what the fixture must make Run return, when it ends the
		// connection at all.
		want error
		// reject is set for a fixture that makes the runner stop taking work.
		reject bool
	}{
		{file: "heartbeat.gateway.json"},
		{file: "assign.remotion-session.json"},
		{file: "op.request.exec.json"},
		{file: "op.request.status.json"},
		{file: "cancel.user.json"},
		{file: "drain.runner.json", reject: true},
		{file: "bye.gateway.json", want: ErrUnauthenticated},
	}
	for _, tc := range cases {
		t.Run(tc.file, func(t *testing.T) {
			raw, err := os.ReadFile(filepath.Join(dir, tc.file))
			if err != nil {
				t.Fatalf("read %s: %v", tc.file, err)
			}
			ta := startTestAgent(t)
			c := ta.gw.awaitDial(t)
			c.handshake(t)
			c.nextOf(t, protocol.TypeReady)
			// The fixture names a job the runner does not hold; the point is
			// that the worker neither tears the connection down nor acts on a
			// host path.
			c.sendRaw(t, string(raw))
			if tc.want != nil {
				// bye{revoked}: the runner waits for the close frame, gets none
				// inside byeWait, and treats the reason as final.
				err := ta.runErr(3 * time.Second)
				if !errors.Is(err, tc.want) {
					t.Fatalf("Run returned %v, want %v", err, tc.want)
				}
				return
			}
			time.Sleep(50 * time.Millisecond)
			c.sendJob(t, protocol.TypeAssign, testJobID, testSessionID, assignPayload(protocol.JobVideoCompile, 1, testSessionID))
			if tc.reject {
				reject, err := protocol.DecodePayload[protocol.RejectPayload](c.nextOf(t, protocol.TypeReject).env)
				if err != nil {
					t.Fatalf("reject payload: %v", err)
				}
				if reject.Reason != protocol.RejectShuttingDown {
					t.Fatalf("reject reason = %q, want shutting_down", reject.Reason)
				}
				return
			}
			c.nextOf(t, protocol.TypeAccept)
		})
	}
}

// countingTransport counts requests without answering them.
type countingTransport struct {
	mu sync.Mutex
	n  int
}

func (c *countingTransport) RoundTrip(r *http.Request) (*http.Response, error) {
	c.mu.Lock()
	c.n++
	c.mu.Unlock()
	return nil, errors.New("the transport is not meant to be used")
}

func (c *countingTransport) count() int {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.n
}

// foldEmpty removes null values and empty arrays from a decoded JSON tree, so
// that "absent" and "empty" compare equal.
func foldEmpty(v any) any {
	switch t := v.(type) {
	case map[string]any:
		out := make(map[string]any, len(t))
		for k, val := range t {
			folded := foldEmpty(val)
			if arr, ok := folded.([]any); ok && len(arr) == 0 {
				continue
			}
			if folded == nil {
				continue
			}
			out[k] = folded
		}
		return out
	case []any:
		out := make([]any, 0, len(t))
		for _, val := range t {
			out = append(out, foldEmpty(val))
		}
		return out
	default:
		return v
	}
}

func exportedFields(t reflect.Type) []string {
	var out []string
	for i := 0; i < t.NumField(); i++ {
		f := t.Field(i)
		if !f.IsExported() {
			continue
		}
		name := f.Name
		if tag := string(f.Tag); strings.Contains(tag, "json") {
			name += " " + tag
		}
		out = append(out, name)
	}
	return out
}
