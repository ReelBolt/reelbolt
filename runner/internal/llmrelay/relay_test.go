package llmrelay

import (
	"context"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
)

type msg struct {
	t       protocol.MessageType
	payload any
}

// sink is a fake connection: it records what the relay sends.
type sink struct {
	mu   sync.Mutex
	msgs []msg
	done chan struct{}
	once sync.Once
	fail atomic.Bool
}

func newSink() *sink { return &sink{done: make(chan struct{})} }

func (s *sink) send(t protocol.MessageType, p any) error {
	if s.fail.Load() {
		return fmt.Errorf("closed")
	}
	s.mu.Lock()
	s.msgs = append(s.msgs, msg{t, p})
	s.mu.Unlock()
	if t == protocol.TypeLLMResponseEnd || t == protocol.TypeLLMError {
		s.once.Do(func() { close(s.done) })
	}
	return nil
}

func (s *sink) wait(t *testing.T) []msg {
	t.Helper()
	select {
	case <-s.done:
	case <-time.After(10 * time.Second):
		t.Fatal("no terminal message from the relay")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]msg(nil), s.msgs...)
}

func (s *sink) errCode(t *testing.T) string {
	t.Helper()
	msgs := s.wait(t)
	last := msgs[len(msgs)-1]
	e, ok := last.payload.(protocol.LLMErrorPayload)
	if !ok {
		t.Fatalf("last message is %s, want llm.error", last.t)
	}
	return e.Code
}

func (s *sink) body(t *testing.T) (status int, body string) {
	t.Helper()
	msgs := s.wait(t)
	next := 0
	for _, m := range msgs {
		switch p := m.payload.(type) {
		case protocol.LLMResponseStartPayload:
			status = p.Status
		case protocol.LLMResponseChunkPayload:
			if p.Seq != next {
				t.Fatalf("chunk seq %d, want %d", p.Seq, next)
			}
			next++
			body += p.Data
		case protocol.LLMErrorPayload:
			t.Fatalf("llm.error %s", p.Code)
		}
	}
	return status, body
}

func newRelay(t *testing.T, srv *httptest.Server, tweak func(*Settings)) *Relay {
	t.Helper()
	s := Settings{Enabled: true, BaseURL: srv.URL}
	if tweak != nil {
		tweak(&s)
	}
	r := New(Options{Settings: s})
	return r
}

// waitIdle waits for the relay to finish bookkeeping: the terminal message is
// sent just before the request is released.
func waitIdle(t *testing.T, r *Relay) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for r.Status().Active != 0 {
		if time.Now().After(deadline) {
			t.Fatal("request still counted as active")
		}
		time.Sleep(5 * time.Millisecond)
	}
}

func chatReq(id string) protocol.LLMRequestPayload {
	return protocol.LLMRequestPayload{
		RequestID: id, Method: "POST", Path: protocol.LLMPathChatCompletions,
		Body: `{"model":"m"}`, TimeoutSec: 30,
	}
}

func TestRelayForwardsToConfiguredServerOnly(t *testing.T) {
	var got struct {
		sync.Mutex
		method, path, query, body, host, auth, ct string
		n                                         int
	}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		b, _ := io.ReadAll(r.Body)
		got.Lock()
		got.method, got.path, got.query, got.body, got.host = r.Method, r.URL.Path, r.URL.RawQuery, string(b), r.Host
		got.auth, got.ct = r.Header.Get("Authorization"), r.Header.Get("Content-Type")
		got.n++
		got.Unlock()
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, `{"ok":true}`)
	}))
	defer srv.Close()
	r := newRelay(t, srv, nil)
	s := newSink()
	r.HandleRequest(chatReq("r1"), s.send)
	status, body := s.body(t)
	if status != 200 || body != `{"ok":true}` {
		t.Fatalf("status=%d body=%q", status, body)
	}
	got.Lock()
	defer got.Unlock()
	if got.method != "POST" || got.path != "/v1/chat/completions" || got.query != "" || got.body != `{"model":"m"}` ||
		got.host != strings.TrimPrefix(srv.URL, "http://") || got.auth != "" || got.ct != "application/json" {
		t.Fatalf("server saw %s %s ?%s body=%q host=%s auth=%q ct=%q", got.method, got.path, got.query, got.body, got.host, got.auth, got.ct)
	}
	waitIdle(t, r)
	st := r.Status()
	if st.Served != 1 || st.Failed != 0 || st.LastRequest.IsZero() || st.Active != 0 {
		t.Fatalf("status = %+v", st)
	}
}

func TestRelayUnsupportedPathsNeverReachTheServer(t *testing.T) {
	var hits atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { hits.Add(1) }))
	defer srv.Close()
	r := newRelay(t, srv, nil)
	for _, c := range []struct{ method, path string }{
		{"POST", "/v1/../etc/passwd"},
		{"POST", "/v1/chat/completions/../../api/pull"},
		{"POST", "http://evil.example/v1/chat/completions"},
		{"POST", "//evil.example/v1/chat/completions"},
		{"POST", "/v1/chat/completions?x=1"},
		{"POST", "/v1/chat/completions/"},
		{"POST", "/api/pull"},
		{"GET", "/v1/embeddings"},
		{"POST", "/V1/chat/completions"},
		{"GET", "/v1/chat/completions"},
		{"POST", "/v1/models"},
		{"POST", "/v1/chat/completions%2f..%2f"},
		{"POST", ""},
	} {
		s := newSink()
		req := chatReq("x")
		req.Method, req.Path = c.method, c.path
		r.HandleRequest(req, s.send)
		if code := s.errCode(t); code != protocol.LLMErrUnsupportedPath {
			t.Errorf("%s %q: code = %s, want unsupported_path", c.method, c.path, code)
		}
	}
	if hits.Load() != 0 {
		t.Fatalf("the server was contacted %d times", hits.Load())
	}
}

func TestRelayDisabledAndPaused(t *testing.T) {
	var hits atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { hits.Add(1) }))
	defer srv.Close()

	off := newRelay(t, srv, func(s *Settings) { s.Enabled = false })
	s := newSink()
	off.HandleRequest(chatReq("a"), s.send)
	if code := s.errCode(t); code != protocol.LLMErrDisabled {
		t.Fatalf("disabled: %s", code)
	}

	var allowed atomic.Bool
	paused := New(Options{Settings: Settings{Enabled: true, BaseURL: srv.URL}, Allowed: allowed.Load})
	s = newSink()
	paused.HandleRequest(chatReq("b"), s.send)
	if code := s.errCode(t); code != protocol.LLMErrDisabled {
		t.Fatalf("paused: %s", code)
	}
	if hits.Load() != 0 {
		t.Fatal("a disabled relay contacted the server")
	}
	if off.Capabilities() != nil {
		t.Fatal("a disabled relay advertises localLlm")
	}
	allowed.Store(true)
	s = newSink()
	paused.HandleRequest(chatReq("c"), s.send)
	s.wait(t)
	if hits.Load() != 1 {
		t.Fatal("resumed relay did not serve")
	}
}

func TestRelayRefusesRedirects(t *testing.T) {
	var other atomic.Int32
	target := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { other.Add(1) }))
	defer target.Close()
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, target.URL+"/steal", http.StatusTemporaryRedirect)
	}))
	defer srv.Close()
	r := newRelay(t, srv, nil)
	s := newSink()
	r.HandleRequest(chatReq("r"), s.send)
	if code := s.errCode(t); code != protocol.LLMErrServerUnreachable {
		t.Fatalf("code = %s", code)
	}
	if other.Load() != 0 {
		t.Fatal("the redirect was followed")
	}
}

func TestRelayNonLoopbackHostIsRefused(t *testing.T) {
	r := New(Options{Settings: Settings{Enabled: true, BaseURL: "http://192.168.1.50:11434"}})
	if r.Settings().Enabled || r.Capabilities() != nil {
		t.Fatal("a LAN host was accepted without the explicit opt-in")
	}
	if err := r.Apply(Settings{Enabled: true, BaseURL: "http://192.168.1.50:11434"}); err == nil {
		t.Fatal("Apply accepted a LAN host")
	}
	s := newSink()
	r.HandleRequest(chatReq("x"), s.send)
	if code := s.errCode(t); code != protocol.LLMErrDisabled {
		t.Fatalf("code = %s", code)
	}
	if err := r.Apply(Settings{Enabled: true, BaseURL: "http://192.168.1.50:11434", AllowRemoteHost: true}); err != nil {
		t.Fatalf("explicit remote host refused: %v", err)
	}
	// The dialer enforces it too, so a name that resolves elsewhere cannot slip by.
	if err := loopbackOnly(false)("tcp", "10.1.2.3:80", nil); err == nil {
		t.Error("dialer allowed a non-loopback address")
	}
	if err := loopbackOnly(false)("tcp", "127.0.0.1:80", nil); err != nil {
		t.Errorf("dialer refused loopback: %v", err)
	}
	if err := loopbackOnly(false)("tcp", "[::1]:80", nil); err != nil {
		t.Errorf("dialer refused ::1: %v", err)
	}
	if err := loopbackOnly(true)("tcp", "10.1.2.3:80", nil); err != nil {
		t.Errorf("explicit remote host refused by dialer: %v", err)
	}
}

func TestRelayOversizedRequestBody(t *testing.T) {
	var hits atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { hits.Add(1) }))
	defer srv.Close()
	r := newRelay(t, srv, nil)
	s := newSink()
	req := chatReq("big")
	req.Body = strings.Repeat("a", protocol.LLMMaxRequestBodyBytes+1)
	r.HandleRequest(req, s.send)
	if code := s.errCode(t); code != protocol.LLMErrTooLarge || hits.Load() != 0 {
		t.Fatalf("code=%s hits=%d", code, hits.Load())
	}
}

func TestRelayOversizedResponse(t *testing.T) {
	old := maxResponseBytes
	maxResponseBytes = 1 << 20
	defer func() { maxResponseBytes = old }()
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		chunk := []byte(strings.Repeat("x", 64<<10))
		for i := 0; i < 64; i++ {
			if _, err := w.Write(chunk); err != nil {
				return
			}
		}
	}))
	defer srv.Close()
	r := newRelay(t, srv, nil)
	s := newSink()
	r.HandleRequest(chatReq("big"), s.send)
	if code := s.errCode(t); code != protocol.LLMErrTooLarge {
		t.Fatalf("code = %s", code)
	}
	waitIdle(t, r)
	for _, m := range s.msgs {
		if c, ok := m.payload.(protocol.LLMResponseChunkPayload); ok && len(c.Data) > protocol.LLMMaxChunkBytes {
			t.Fatalf("chunk of %d bytes", len(c.Data))
		}
	}
	waitIdle(t, r)
	if r.Status().Failed != 1 {
		t.Fatalf("failed = %d", r.Status().Failed)
	}
}

func TestRelaySlowServerTimesOut(t *testing.T) {
	release := make(chan struct{})
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		select {
		case <-release:
		case <-r.Context().Done():
		}
	}))
	defer srv.Close()
	defer close(release)
	r := newRelay(t, srv, nil)
	s := newSink()
	req := chatReq("slow")
	req.TimeoutSec = 1
	r.HandleRequest(req, s.send)
	if code := s.errCode(t); code != protocol.LLMErrTimeout {
		t.Fatalf("code = %s", code)
	}
}

func TestRelayUnreachableServer(t *testing.T) {
	srv := httptest.NewServer(http.NotFoundHandler())
	url := srv.URL
	srv.Close() // nothing listens any more
	r := New(Options{Settings: Settings{Enabled: true, BaseURL: url}})
	s := newSink()
	r.HandleRequest(chatReq("x"), s.send)
	if code := s.errCode(t); code != protocol.LLMErrServerUnreachable {
		t.Fatalf("code = %s", code)
	}
}

func TestRelayStreamsChunksAndCancelsMidStream(t *testing.T) {
	first := make(chan struct{})
	serverGone := make(chan struct{})
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/event-stream")
		fl := w.(http.Flusher)
		_, _ = io.WriteString(w, "data: 1\n\n")
		fl.Flush()
		close(first)
		<-r.Context().Done() // stays open until the relay hangs up
		close(serverGone)
	}))
	defer srv.Close()
	r := newRelay(t, srv, nil)
	s := newSink()
	req := chatReq("stream")
	req.Stream = true
	r.HandleRequest(req, s.send)
	<-first
	deadline := time.After(5 * time.Second)
	for {
		s.mu.Lock()
		n := len(s.msgs)
		s.mu.Unlock()
		if n >= 2 { // start + first chunk arrived while the stream is still open
			break
		}
		select {
		case <-deadline:
			t.Fatal("the first chunk was not relayed while the response was still open")
		case <-time.After(10 * time.Millisecond):
		}
	}
	r.Cancel("stream")
	if code := s.errCode(t); code != protocol.LLMErrCancelled {
		t.Fatalf("code = %s", code)
	}
	select {
	case <-serverGone:
	case <-time.After(5 * time.Second):
		t.Fatal("the upstream request was not cancelled")
	}
	waitIdle(t, r)
}

func TestRelayChunkOrderAndRuneBoundaries(t *testing.T) {
	text := strings.Repeat("héllo wörld ✓ ", 40000) // ~600 KB, multi-byte throughout
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		raw := []byte(text)
		fl := w.(http.Flusher)
		for len(raw) > 0 {
			n := min(len(raw), 7919) // an awkward size so runes straddle writes
			_, _ = w.Write(raw[:n])
			fl.Flush()
			raw = raw[n:]
		}
	}))
	defer srv.Close()
	r := newRelay(t, srv, nil)
	s := newSink()
	r.HandleRequest(chatReq("utf"), s.send)
	_, body := s.body(t)
	if body != text {
		t.Fatalf("body differs (%d vs %d bytes)", len(body), len(text))
	}
	for _, m := range s.msgs {
		if c, ok := m.payload.(protocol.LLMResponseChunkPayload); ok && c.Validate() != nil {
			t.Fatalf("invalid chunk: %v", c.Validate())
		}
	}
}

func TestRelayBusyAtConcurrencyCap(t *testing.T) {
	hold := make(chan struct{})
	entered := make(chan struct{}, 4)
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		entered <- struct{}{}
		select {
		case <-hold:
		case <-r.Context().Done():
		}
	}))
	defer srv.Close()
	r := newRelay(t, srv, func(s *Settings) { s.MaxConcurrent = 1 })
	first, second := newSink(), newSink()
	r.HandleRequest(chatReq("one"), first.send)
	<-entered
	r.HandleRequest(chatReq("two"), second.send)
	if code := second.errCode(t); code != protocol.LLMErrBusy {
		t.Fatalf("code = %s", code)
	}
	close(hold)
	first.wait(t)
	third := newSink()
	r.HandleRequest(chatReq("three"), third.send)
	third.wait(t) // a slot was released
}

func TestRelayCancelAllAndGoneConnection(t *testing.T) {
	hold := make(chan struct{})
	entered := make(chan struct{}, 2)
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		entered <- struct{}{}
		select {
		case <-hold:
		case <-r.Context().Done():
		}
	}))
	defer srv.Close()
	defer close(hold)
	r := newRelay(t, srv, nil)
	s := newSink()
	r.HandleRequest(chatReq("a"), s.send)
	<-entered
	r.CancelAll()
	if code := s.errCode(t); code != protocol.LLMErrCancelled {
		t.Fatalf("code = %s", code)
	}
}

func TestRelayDuplicateRequestIDRefused(t *testing.T) {
	hold := make(chan struct{})
	entered := make(chan struct{}, 2)
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		entered <- struct{}{}
		<-hold
	}))
	defer srv.Close()
	closed := false
	release := func() {
		if !closed {
			closed = true
			close(hold)
		}
	}
	defer release()
	r := newRelay(t, srv, nil)
	r.HandleRequest(chatReq("dup"), newSink().send)
	<-entered
	s := newSink()
	r.HandleRequest(chatReq("dup"), s.send)
	if code := s.errCode(t); code != protocol.LLMErrBadRequest {
		t.Fatalf("code = %s", code)
	}
	// Let the first request finish so no goroutine outlives the test.
	release()
	waitIdle(t, r)
}

func TestRelayModelsAndCapabilities(t *testing.T) {
	var items []string
	for i := 0; i < 130; i++ {
		items = append(items, fmt.Sprintf(`{"id":"model-%d","owned_by":"library"}`, i))
	}
	items = append(items, fmt.Sprintf(`{"id":"%s"}`, strings.Repeat("z", 201)))
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/v1/models" {
			t.Errorf("unexpected path %s", r.URL.Path)
		}
		_, _ = io.WriteString(w, `{"object":"list","data":[`+strings.Join(items, ",")+`]}`)
	}))
	defer srv.Close()
	r := newRelay(t, srv, func(s *Settings) { s.MaxConcurrent = 3 })
	if changed, err := r.RefreshModels(context.Background()); err != nil || !changed {
		t.Fatalf("refresh: changed=%v err=%v", changed, err)
	}
	if changed, _ := r.RefreshModels(context.Background()); changed {
		t.Error("an identical list reported as changed")
	}
	c := r.Capabilities()
	if c == nil || !c.Enabled || len(c.Models) != 100 || c.MaxConcurrent != 3 || c.ServerKind != KindOllama {
		t.Fatalf("capabilities = %+v", c)
	}
	doc := protocol.Capabilities{ProtocolVersion: protocol.ProtocolVersion, Slots: 1, Container: protocol.ContainerNone, LocalLLM: c}
	if err := doc.Validate(); err != nil {
		t.Fatalf("advertised document invalid: %v", err)
	}
	if st := r.Status(); !st.Reachable || len(st.Models) != 100 {
		t.Fatalf("status = %+v", st)
	}
	if r.ModelsStale() {
		t.Error("fresh list reported stale")
	}
}

func TestSettingsPersistThroughConfig(t *testing.T) {
	var c config.Config
	c.ApplyDefaults()
	if err := c.SetLocalLLM(Settings{Enabled: true}); err != nil {
		t.Fatal(err)
	}
	r := New(Options{Settings: c.LocalLLMSettings()})
	if !r.Status().Enabled || r.Status().BaseURL != config.DefaultLocalLLMBaseURL {
		t.Fatalf("status = %+v", r.Status())
	}
}

func TestDetectFindsServersAndGuessesKind(t *testing.T) {
	models := `{"data":[{"id":"llama3.2:3b","owned_by":"library"},{"id":"qwen"}]}`
	good := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/v1/models" {
			_, _ = io.WriteString(w, models)
			return
		}
		http.NotFound(w, r)
	}))
	defer good.Close()
	notJSON := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { _, _ = io.WriteString(w, "<html>") }))
	defer notJSON.Close()
	redirector := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, good.URL+"/v1/models", http.StatusFound)
	}))
	defer redirector.Close()
	port := func(s *httptest.Server) int {
		var p int
		_, _ = fmt.Sscanf(s.URL[strings.LastIndex(s.URL, ":")+1:], "%d", &p)
		return p
	}
	found := DetectPorts(context.Background(), "127.0.0.1", []int{port(good), port(notJSON), port(redirector), 1})
	if len(found) != 1 || found[0].BaseURL != good.URL || found[0].Kind != KindOllama ||
		len(found[0].Models) != 2 || found[0].Models[0] != "llama3.2:3b" {
		t.Fatalf("found = %+v", found)
	}
}

func TestGuessKind(t *testing.T) {
	for _, c := range []struct{ port, owner, want string }{
		{"11434", "", KindOllama}, {"1234", "", KindLMStudio}, {"8080", "", KindLlamaCpp},
		{"8000", "", KindVLLM}, {"9999", "", KindOpenAICompat}, {"9999", "vllm", KindVLLM},
		{"1234", "llamacpp", KindLlamaCpp},
	} {
		if got := guessKind(c.port, c.owner); got != c.want {
			t.Errorf("guessKind(%s,%s) = %s, want %s", c.port, c.owner, got, c.want)
		}
	}
}

func TestSendFailureStopsServing(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { _, _ = io.WriteString(w, "hi") }))
	defer srv.Close()
	r := newRelay(t, srv, nil)
	s := newSink()
	s.fail.Store(true)
	r.HandleRequest(chatReq("x"), s.send)
	deadline := time.Now().Add(5 * time.Second)
	for r.Status().Active != 0 || r.Status().Failed+r.Status().Served == 0 {
		if time.Now().After(deadline) {
			t.Fatal("request never finished after the connection closed")
		}
		time.Sleep(5 * time.Millisecond)
	}
}

func embReq(id string) protocol.LLMRequestPayload {
	return protocol.LLMRequestPayload{
		RequestID: id, Method: "POST", Path: protocol.LLMPathEmbeddings,
		Body: `{"model":"m","input":"x"}`, TimeoutSec: 30,
	}
}

func bigServer(path string, chunks int, hits *atomic.Int32) *httptest.Server {
	return httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		hits.Add(1)
		if r.URL.Path != path {
			http.NotFound(w, r)
			return
		}
		chunk := []byte(strings.Repeat("x", 64<<10))
		for i := 0; i < chunks; i++ {
			if _, err := w.Write(chunk); err != nil {
				return
			}
		}
	}))
}

func TestRelayEmbeddingsAboveChatCapPasses(t *testing.T) {
	old, oldE := maxResponseBytes, maxEmbeddingsResponseBytes
	maxResponseBytes, maxEmbeddingsResponseBytes = 1<<20, 4<<20
	defer func() { maxResponseBytes, maxEmbeddingsResponseBytes = old, oldE }()
	var hits atomic.Int32
	srv := bigServer("/v1/embeddings", 32, &hits) // 2 MiB
	defer srv.Close()
	r := newRelay(t, srv, nil)
	s := newSink()
	r.HandleRequest(embReq("e1"), s.send)
	s.wait(t)
	waitIdle(t, r)
	if r.Status().Failed != 0 || hits.Load() != 1 {
		t.Fatalf("failed=%d hits=%d", r.Status().Failed, hits.Load())
	}
}

func TestRelayEmbeddingsOverCapIsTooLarge(t *testing.T) {
	old := maxEmbeddingsResponseBytes
	maxEmbeddingsResponseBytes = 1 << 20
	defer func() { maxEmbeddingsResponseBytes = old }()
	var hits atomic.Int32
	srv := bigServer("/v1/embeddings", 64, &hits)
	defer srv.Close()
	r := newRelay(t, srv, nil)
	s := newSink()
	r.HandleRequest(embReq("e2"), s.send)
	if code := s.errCode(t); code != protocol.LLMErrTooLarge {
		t.Fatalf("code = %s", code)
	}
	waitIdle(t, r)
}

func TestRelayEmbeddingsRejectsWrongMethod(t *testing.T) {
	var hits atomic.Int32
	srv := bigServer("/v1/embeddings", 1, &hits)
	defer srv.Close()
	r := newRelay(t, srv, nil)
	s := newSink()
	req := embReq("e3")
	req.Method = "GET"
	r.HandleRequest(req, s.send)
	if code := s.errCode(t); code != protocol.LLMErrUnsupportedPath || hits.Load() != 0 {
		t.Fatalf("code=%s hits=%d", code, hits.Load())
	}
}

func TestNewClientHasNoFixedHeaderTimeout(t *testing.T) {
	tr := newClient(false).Transport.(*http.Transport)
	if tr.ResponseHeaderTimeout != 0 {
		t.Fatalf("ResponseHeaderTimeout = %s: the request's own timeout must bound the wait", tr.ResponseHeaderTimeout)
	}
}

func TestRelaySlowHeadersTimeOutAsTimeoutNotUnreachable(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		select {
		case <-time.After(5 * time.Second):
		case <-r.Context().Done():
		}
	}))
	defer srv.Close()
	r := newRelay(t, srv, nil)
	s := newSink()
	req := chatReq("slow")
	req.TimeoutSec = 1
	r.HandleRequest(req, s.send)
	if code := s.errCode(t); code != protocol.LLMErrTimeout {
		t.Fatalf("code = %s, want timeout", code)
	}
	waitIdle(t, r)
}

type timeoutErr struct{}

func (timeoutErr) Error() string   { return "timeout awaiting response headers" }
func (timeoutErr) Timeout() bool   { return true }
func (timeoutErr) Temporary() bool { return false }

func TestClassifyNetTimeoutIsTimeout(t *testing.T) {
	if got := classify(context.Background(), timeoutErr{}); got != protocol.LLMErrTimeout {
		t.Fatalf("classify = %s", got)
	}
}

func TestClassifyConnectTimeoutIsUnreachableNotTimeout(t *testing.T) {
	err := &net.OpError{Op: "dial", Net: "tcp", Err: timeoutErr{}}
	if got := classify(context.Background(), err); got != protocol.LLMErrServerUnreachable {
		t.Fatalf("classify(dial timeout) = %s, want server_unreachable", got)
	}
}

func TestDetectFollowsPortOrderNotURLOrder(t *testing.T) {
	mk := func() (*httptest.Server, int) {
		s := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
			_, _ = io.WriteString(w, `{"data":[{"id":"m"}]}`)
		}))
		u, _ := url.Parse(s.URL)
		p, _ := strconv.Atoi(u.Port())
		return s, p
	}
	s1, p1 := mk()
	defer s1.Close()
	s2, p2 := mk()
	defer s2.Close()
	for _, ports := range [][]int{{p1, p2}, {p2, p1}} {
		got := DetectPorts(context.Background(), "127.0.0.1", ports)
		if len(got) != 2 || !strings.HasSuffix(got[0].BaseURL, ":"+strconv.Itoa(ports[0])) {
			t.Fatalf("ports %v: got %+v", ports, got)
		}
	}
}
