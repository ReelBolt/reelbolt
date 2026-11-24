package main

import (
	"bufio"
	"context"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"

	"github.com/coder/websocket"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// These tests drive the relay through a real WebSocket against the in-process
// gateway, with the test playing the desktop runner. No database is involved.

const llmTestToken = "llm-test-token"

func newLLMGateway(t *testing.T) *testGateway {
	t.Helper()
	return newTestGateway(t, func(c *appConfig) { c.InternalToken = llmTestToken })
}

// llmRunner connects a paired runner. share=false connects it without the
// localLlm capability.
func llmRunner(t *testing.T, tg *testGateway, orgID string, share bool) *runner {
	t.Helper()
	r := tg.newRunner(t, orgID)
	ws := tg.connectHello(t, desktopHello(t, r, tg.dialedHost(t), func(h *protocol.HelloPayload) {
		if share {
			h.Capabilities.LocalLLM = &protocol.LocalLLMCapabilities{
				Enabled: true, ServerKind: "ollama", Models: []string{"llama3.2"}, MaxConcurrent: 2,
			}
		}
	}))
	if env := readEnvelope(t, ws); env.Type != protocol.TypeWelcome {
		t.Fatalf("first answer is %s, want welcome", env.Type)
	}
	r.ws = ws
	return r
}

func (tg *testGateway) llmURL(orgID, deviceID, path string) string {
	return tg.server.URL + "/internal/v1/llm/orgs/" + orgID + "/devices/" + deviceID + path
}

func llmDo(t *testing.T, ctx context.Context, method, url, body string, auth bool) (*http.Response, error) {
	t.Helper()
	req, err := http.NewRequestWithContext(ctx, method, url, strings.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	if auth {
		req.Header.Set("Authorization", "Bearer "+llmTestToken)
	}
	req.Header.Set("Content-Type", "application/json")
	return http.DefaultClient.Do(req)
}

func llmPost(t *testing.T, tg *testGateway, orgID, deviceID, body string) *http.Response {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	t.Cleanup(cancel)
	resp, err := llmDo(t, ctx, http.MethodPost, tg.llmURL(orgID, deviceID, "/v1/chat/completions"), body, true)
	if err != nil {
		t.Fatalf("post: %v", err)
	}
	t.Cleanup(func() { _ = resp.Body.Close() })
	return resp
}

func readLLMRequest(t *testing.T, ws *websocket.Conn) protocol.LLMRequestPayload {
	t.Helper()
	env := readEnvelope(t, ws)
	if env.Type != protocol.TypeLLMRequest {
		t.Fatalf("runner received %s, want llm.request", env.Type)
	}
	return readPayload[protocol.LLMRequestPayload](t, env)
}

func TestLLMRelayStreamsChunksInOrder(t *testing.T) {
	tg := newLLMGateway(t)
	r := llmRunner(t, tg, testOrgID, true)

	chunks := []string{"data: {\"n\":1}\n\n", "data: {\"n\":2}\n\n", "data: [DONE]\n\n"}
	got := make(chan protocol.LLMRequestPayload, 1)
	release := make(chan struct{})
	go func() {
		req := readLLMRequest(t, r.ws)
		got <- req
		send(t, r.ws, mustEnvelope(t, protocol.TypeLLMResponseStart, protocol.LLMResponseStartPayload{
			RequestID: req.RequestID, Status: 200, ContentType: "text/event-stream"}))
		for i, c := range chunks {
			send(t, r.ws, mustEnvelope(t, protocol.TypeLLMResponseChunk, protocol.LLMResponseChunkPayload{
				RequestID: req.RequestID, Seq: i, Data: c}))
			if i == 0 {
				<-release // the first chunk must reach the client before the rest exist
			}
		}
		send(t, r.ws, mustEnvelope(t, protocol.TypeLLMResponseEnd, protocol.LLMResponseEndPayload{
			RequestID: req.RequestID, Bytes: 1}))
	}()

	resp := llmPost(t, tg, testOrgID, r.deviceID, `{"model":"llama3.2","stream":true}`)
	if resp.StatusCode != 200 || resp.Header.Get("Content-Type") != "text/event-stream" {
		t.Fatalf("status=%d type=%q", resp.StatusCode, resp.Header.Get("Content-Type"))
	}
	reader := bufio.NewReader(resp.Body)
	line, err := reader.ReadString('\n')
	if err != nil || line != "data: {\"n\":1}\n" {
		t.Fatalf("first line = %q (%v): not flushed before the stream ended", line, err)
	}
	close(release)
	rest, _ := io.ReadAll(reader)
	if want := "\n" + chunks[1] + chunks[2]; string(rest) != want {
		t.Fatalf("rest = %q, want %q", rest, want)
	}
	req := <-got
	if req.Path != protocol.LLMPathChatCompletions || req.Method != "POST" || !req.Stream || req.TimeoutSec <= 0 ||
		req.Body != `{"model":"llama3.2","stream":true}` {
		t.Fatalf("llm.request = %+v", req)
	}
	if v := tg.metricValue(t, `reelbolt_runner_gateway_llm_requests_total{result="ok"}`); v != 1 {
		t.Errorf("ok counter = %v", v)
	}
}

func TestLLMRelayModelsIsGet(t *testing.T) {
	tg := newLLMGateway(t)
	r := llmRunner(t, tg, testOrgID, true)
	go func() {
		req := readLLMRequest(t, r.ws)
		if req.Method != "GET" || req.Path != protocol.LLMPathModels || req.Body != "" {
			t.Errorf("llm.request = %+v", req)
		}
		send(t, r.ws, mustEnvelope(t, protocol.TypeLLMResponseStart, protocol.LLMResponseStartPayload{RequestID: req.RequestID, Status: 200, ContentType: "application/json"}))
		send(t, r.ws, mustEnvelope(t, protocol.TypeLLMResponseChunk, protocol.LLMResponseChunkPayload{RequestID: req.RequestID, Data: `{"data":[]}`}))
		send(t, r.ws, mustEnvelope(t, protocol.TypeLLMResponseEnd, protocol.LLMResponseEndPayload{RequestID: req.RequestID, Bytes: 11}))
	}()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	resp, err := llmDo(t, ctx, http.MethodGet, tg.llmURL(testOrgID, r.deviceID, "/v1/models"), "", true)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = resp.Body.Close() }()
	body, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != 200 || string(body) != `{"data":[]}` {
		t.Fatalf("status=%d body=%s", resp.StatusCode, body)
	}
}

func TestLLMRelayOrgMismatchIsRefused(t *testing.T) {
	tg := newLLMGateway(t)
	r := llmRunner(t, tg, testOrgID, true)
	other := "9a8b7c6d-1111-4222-8333-444455556666"
	resp := llmPost(t, tg, other, r.deviceID, `{"model":"m"}`)
	if resp.StatusCode != http.StatusNotFound {
		t.Fatalf("status = %d, want 404", resp.StatusCode)
	}
	expectNoMessage(t, r.ws, 300*time.Millisecond)
}

func TestLLMRelayRevokedAndDisabledAre409(t *testing.T) {
	tg := newLLMGateway(t)
	off := llmRunner(t, tg, testOrgID, false)
	resp := llmPost(t, tg, testOrgID, off.deviceID, `{"model":"m"}`)
	if resp.StatusCode != http.StatusConflict {
		t.Fatalf("disabled: status = %d, want 409", resp.StatusCode)
	}
	expectNoMessage(t, off.ws, 200*time.Millisecond)

	on := llmRunner(t, tg, testOrgID, true)
	tg.store.revoke(on.deviceID)
	resp = llmPost(t, tg, testOrgID, on.deviceID, `{"model":"m"}`)
	if resp.StatusCode != http.StatusConflict {
		t.Fatalf("revoked: status = %d, want 409", resp.StatusCode)
	}
	expectNoMessage(t, on.ws, 200*time.Millisecond)
}

func TestLLMRelayOfflineIs503(t *testing.T) {
	tg := newLLMGateway(t)
	r := tg.newRunner(t, testOrgID) // paired, never connected
	resp := llmPost(t, tg, testOrgID, r.deviceID, `{"model":"m"}`)
	if resp.StatusCode != http.StatusServiceUnavailable {
		t.Fatalf("status = %d, want 503", resp.StatusCode)
	}
	if b, _ := io.ReadAll(resp.Body); !strings.Contains(string(b), "runner_offline") {
		t.Fatalf("body = %s", b)
	}
}

func TestLLMRelayPathOutsideAllowlistNeverReachesRunner(t *testing.T) {
	tg := newLLMGateway(t)
	r := llmRunner(t, tg, testOrgID, true)
	base := tg.llmURL(testOrgID, r.deviceID, "")
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	client := &http.Client{CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	for _, c := range []struct{ method, path string }{
		{"GET", "/v1/embeddings"},
		{"POST", "/v1/chat/completions/extra"},
		{"POST", "/v1/chat/../../models"},
		{"POST", "/api/pull"},
		{"GET", "/v1/chat/completions"},
		{"POST", "/v1/models"},
		{"DELETE", "/v1/models"},
	} {
		req, _ := http.NewRequestWithContext(ctx, c.method, base+c.path, strings.NewReader(`{}`))
		req.Header.Set("Authorization", "Bearer "+llmTestToken)
		resp, err := client.Do(req)
		if err != nil {
			t.Fatalf("%s %s: %v", c.method, c.path, err)
		}
		_ = resp.Body.Close()
		if resp.StatusCode < 300 || resp.StatusCode == 200 {
			t.Errorf("%s %s: status %d, want a refusal", c.method, c.path, resp.StatusCode)
		}
	}
	expectNoMessage(t, r.ws, 300*time.Millisecond)
}

func TestLLMRelayRequiresInternalToken(t *testing.T) {
	tg := newLLMGateway(t)
	r := llmRunner(t, tg, testOrgID, true)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	resp, err := llmDo(t, ctx, http.MethodPost, tg.llmURL(testOrgID, r.deviceID, "/v1/chat/completions"), `{}`, false)
	if err != nil {
		t.Fatal(err)
	}
	_ = resp.Body.Close()
	if resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("status = %d, want 401", resp.StatusCode)
	}
	expectNoMessage(t, r.ws, 200*time.Millisecond)

	closed := newTestGateway(t, nil) // no RUNNER_INTERNAL_TOKEN
	resp, err = llmDo(t, ctx, http.MethodPost, closed.llmURL(testOrgID, r.deviceID, "/v1/chat/completions"), `{}`, true)
	if err != nil {
		t.Fatal(err)
	}
	_ = resp.Body.Close()
	if resp.StatusCode != http.StatusServiceUnavailable {
		t.Fatalf("status = %d, want 503", resp.StatusCode)
	}
}

func TestLLMRelayCancelsWhenTheCallerDisconnects(t *testing.T) {
	tg := newLLMGateway(t)
	r := llmRunner(t, tg, testOrgID, true)
	cancelled := make(chan string, 1)
	started := make(chan struct{})
	go func() {
		req := readLLMRequest(t, r.ws)
		send(t, r.ws, mustEnvelope(t, protocol.TypeLLMResponseStart, protocol.LLMResponseStartPayload{RequestID: req.RequestID, Status: 200, ContentType: "text/event-stream"}))
		send(t, r.ws, mustEnvelope(t, protocol.TypeLLMResponseChunk, protocol.LLMResponseChunkPayload{RequestID: req.RequestID, Seq: 0, Data: "data: 1\n\n"}))
		close(started)
		env := readEnvelope(t, r.ws)
		if env.Type == protocol.TypeLLMCancel {
			cancelled <- readPayload[protocol.LLMCancelPayload](t, env).RequestID
		}
		cancelled <- "unexpected:" + string(env.Type)
	}()
	ctx, cancel := context.WithCancel(context.Background())
	resp, err := llmDo(t, ctx, http.MethodPost, tg.llmURL(testOrgID, r.deviceID, "/v1/chat/completions"), `{"stream":true}`, true)
	if err != nil {
		t.Fatal(err)
	}
	<-started
	buf := make([]byte, 4)
	if _, err := io.ReadFull(resp.Body, buf); err != nil {
		t.Fatal(err)
	}
	cancel() // the engine goes away mid-stream
	_ = resp.Body.Close()
	select {
	case id := <-cancelled:
		if strings.HasPrefix(id, "unexpected:") || id == "" {
			t.Fatalf("runner got %s, want llm.cancel", id)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("the runner never received llm.cancel")
	}
}

func TestLLMRelayTimeoutSendsCancelAnd504(t *testing.T) {
	tg := newLLMGateway(t)
	tg.gateway.llmTimeout = 300 * time.Millisecond
	r := llmRunner(t, tg, testOrgID, true)
	cancelled := make(chan bool, 1)
	go func() {
		readLLMRequest(t, r.ws)
		cancelled <- readEnvelope(t, r.ws).Type == protocol.TypeLLMCancel
	}()
	resp := llmPost(t, tg, testOrgID, r.deviceID, `{"model":"m"}`)
	if resp.StatusCode != http.StatusGatewayTimeout {
		t.Fatalf("status = %d, want 504", resp.StatusCode)
	}
	if !<-cancelled {
		t.Fatal("no llm.cancel after the timeout")
	}
}

func TestLLMRelayConnectionLossIs502(t *testing.T) {
	tg := newLLMGateway(t)
	r := llmRunner(t, tg, testOrgID, true)
	go func() {
		readLLMRequest(t, r.ws)
		_ = r.ws.CloseNow() // the socket dies before any answer
	}()
	resp := llmPost(t, tg, testOrgID, r.deviceID, `{"model":"m"}`)
	if resp.StatusCode != http.StatusBadGateway {
		t.Fatalf("status = %d, want 502", resp.StatusCode)
	}
}

func TestLLMRelayMapsRunnerErrors(t *testing.T) {
	for code, want := range map[string]int{
		protocol.LLMErrBusy:              http.StatusTooManyRequests,
		protocol.LLMErrServerUnreachable: http.StatusBadGateway,
		protocol.LLMErrTimeout:           http.StatusGatewayTimeout,
		protocol.LLMErrDisabled:          http.StatusConflict,
	} {
		tg := newLLMGateway(t)
		r := llmRunner(t, tg, testOrgID, true)
		code := code
		go func() {
			req := readLLMRequest(t, r.ws)
			send(t, r.ws, mustEnvelope(t, protocol.TypeLLMError, protocol.LLMErrorPayload{RequestID: req.RequestID, Code: code, Message: "x"}))
		}()
		if resp := llmPost(t, tg, testOrgID, r.deviceID, `{"model":"m"}`); resp.StatusCode != want {
			t.Errorf("%s: status = %d, want %d", code, resp.StatusCode, want)
		}
	}
}

func TestLLMRelayRejectsBadBodies(t *testing.T) {
	tg := newLLMGateway(t)
	r := llmRunner(t, tg, testOrgID, true)
	if resp := llmPost(t, tg, testOrgID, r.deviceID, `not json`); resp.StatusCode != http.StatusBadRequest {
		t.Errorf("bad json: status = %d", resp.StatusCode)
	}
	big := `{"x":"` + strings.Repeat("a", protocol.LLMMaxRequestBodyBytes) + `"}`
	if resp := llmPost(t, tg, testOrgID, r.deviceID, big); resp.StatusCode != http.StatusRequestEntityTooLarge {
		t.Errorf("oversized: status = %d", resp.StatusCode)
	}
	expectNoMessage(t, r.ws, 200*time.Millisecond)
}

func TestLLMTableOnlyAnswersToTheOwningConnection(t *testing.T) {
	table := newLLMTable()
	owner, other := &conn{runnerID: "a"}, &conn{runnerID: "b"}
	p := &pendingLLM{id: "r1", conn: owner, ch: make(chan llmEvent, 4), done: make(chan struct{}), overflow: make(chan struct{})}
	if !table.begin(p) {
		t.Fatal("begin refused")
	}
	if table.deliver("r1", other, llmEvent{end: &protocol.LLMResponseEndPayload{}}) {
		t.Fatal("another runner answered a request it does not own")
	}
	if !table.deliver("r1", owner, llmEvent{end: &protocol.LLMResponseEndPayload{}}) {
		t.Fatal("the owner was refused")
	}
	table.failConn(owner)
	if len(p.ch) != 2 {
		t.Fatalf("queued = %d, want end + lost", len(p.ch))
	}
	table.end("r1")
	if table.size() != 0 {
		t.Fatal("entry not removed")
	}
}

func llmEmbeddingsPost(t *testing.T, tg *testGateway, orgID, deviceID, body string) *http.Response {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	t.Cleanup(cancel)
	resp, err := llmDo(t, ctx, http.MethodPost, tg.llmURL(orgID, deviceID, "/v1/embeddings"), body, true)
	if err != nil {
		t.Fatalf("post: %v", err)
	}
	t.Cleanup(func() { _ = resp.Body.Close() })
	return resp
}

// serveEmbeddings answers one llm.request with total bytes in chunk-size pieces.
func serveEmbeddings(t *testing.T, r *runner, total int, got chan<- protocol.LLMRequestPayload) {
	// The gateway may drop the connection once the cap is hit, so a failed
	// write ends this feeder quietly instead of failing the test.
	trySend := func(env protocol.Envelope) bool {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		return r.ws.Write(ctx, websocket.MessageText, encode(t, env)) == nil
	}
	go func() {
		req := readLLMRequest(t, r.ws)
		got <- req
		if !trySend(mustEnvelope(t, protocol.TypeLLMResponseStart, protocol.LLMResponseStartPayload{
			RequestID: req.RequestID, Status: 200, ContentType: "application/json"})) {
			return
		}
		piece := strings.Repeat("a", protocol.LLMMaxChunkBytes)
		for i, sent := 0, 0; sent < total; i++ {
			n := min(len(piece), total-sent)
			if !trySend(mustEnvelope(t, protocol.TypeLLMResponseChunk, protocol.LLMResponseChunkPayload{
				RequestID: req.RequestID, Seq: i, Data: piece[:n]})) {
				return
			}
			sent += n
		}
		trySend(mustEnvelope(t, protocol.TypeLLMResponseEnd, protocol.LLMResponseEndPayload{
			RequestID: req.RequestID, Bytes: int64(total)}))
	}()
}

func TestLLMEmbeddingsRelaysAboveChatCap(t *testing.T) {
	tg := newLLMGateway(t)
	r := llmRunner(t, tg, testOrgID, true)
	got := make(chan protocol.LLMRequestPayload, 1)
	total := protocol.LLMMaxResponseBytes + protocol.LLMMaxChunkBytes
	serveEmbeddings(t, r, total, got)
	resp := llmEmbeddingsPost(t, tg, testOrgID, r.deviceID, `{"model":"m","input":"x"}`)
	n, _ := io.Copy(io.Discard, resp.Body)
	req := <-got
	if resp.StatusCode != 200 || n != int64(total) {
		t.Fatalf("status=%d bytes=%d, want %d", resp.StatusCode, n, total)
	}
	if req.Path != protocol.LLMPathEmbeddings || req.Method != "POST" || req.Stream {
		t.Fatalf("llm.request = %+v", req)
	}
}

func TestLLMEmbeddingsCapExceededAborts(t *testing.T) {
	tg := newLLMGateway(t)
	r := llmRunner(t, tg, testOrgID, true)
	got := make(chan protocol.LLMRequestPayload, 1)
	total := protocol.LLMMaxEmbeddingsResponseBytes + protocol.LLMMaxChunkBytes
	serveEmbeddings(t, r, total, got)
	resp := llmEmbeddingsPost(t, tg, testOrgID, r.deviceID, `{"model":"m","input":"x"}`)
	n, err := io.Copy(io.Discard, resp.Body)
	<-got
	if err == nil && n >= int64(total) {
		t.Fatalf("read %d bytes with no error; cap not enforced", n)
	}
	if n > protocol.LLMMaxEmbeddingsResponseBytes {
		t.Fatalf("relayed %d bytes, above the cap", n)
	}
}

func TestLLMEmbeddingsOrgMismatchIsRefused(t *testing.T) {
	tg := newLLMGateway(t)
	r := llmRunner(t, tg, testOrgID, true)
	resp := llmEmbeddingsPost(t, tg, "9a8b7c6d-1111-4222-8333-444455556666", r.deviceID, `{"model":"m"}`)
	if resp.StatusCode != http.StatusNotFound {
		t.Fatalf("status = %d, want 404", resp.StatusCode)
	}
	expectNoMessage(t, r.ws, 300*time.Millisecond)
}

func TestLLMRelayEscapeHeavyBodyIs413(t *testing.T) {
	tg := newLLMGateway(t)
	r := llmRunner(t, tg, testOrgID, true)
	// Under the 3 MiB cap, but every '<' becomes six bytes once JSON-escaped.
	body := `{"p":"` + strings.Repeat("<", protocol.LLMMaxRequestBodyBytes-100) + `"}`
	resp := llmPost(t, tg, testOrgID, r.deviceID, body)
	if resp.StatusCode != 413 {
		t.Fatalf("status = %d, want 413 request_too_large", resp.StatusCode)
	}
}

func TestLLMRelayNullBodyIsBadRequest(t *testing.T) {
	tg := newLLMGateway(t)
	r := llmRunner(t, tg, testOrgID, true)
	if resp := llmPost(t, tg, testOrgID, r.deviceID, `null`); resp.StatusCode != 400 {
		t.Fatalf("status = %d, want 400", resp.StatusCode)
	}
}

func TestLLMTableSlowCallerOverflowsQuickly(t *testing.T) {
	tab := newLLMTable()
	tab.deliverWait = 20 * time.Millisecond
	c := &conn{}
	p := &pendingLLM{id: "x", conn: c, ch: make(chan llmEvent, 1), done: make(chan struct{}), overflow: make(chan struct{})}
	tab.begin(p)
	if !tab.deliver("x", c, llmEvent{}) {
		t.Fatal("first delivery should fit")
	}
	start := time.Now()
	if tab.deliver("x", c, llmEvent{}) {
		t.Fatal("second delivery should be dropped")
	}
	select {
	case <-p.overflow:
	default:
		t.Fatal("overflow not signalled")
	}
	if time.Since(start) > time.Second {
		t.Fatal("deliver blocked too long")
	}
	if llmEventBuffer*protocol.LLMMaxChunkBytes > 8<<20 {
		t.Fatalf("per-request buffer too large")
	}
}
