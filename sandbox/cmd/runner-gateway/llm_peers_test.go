package main

import (
	"bufio"
	"context"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/gorilla/mux"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// newLLMReplica builds another gateway replica over a shared device store.
func newLLMReplica(t *testing.T, store *fakeStore, tweak func(*appConfig)) *testGateway {
	t.Helper()
	cfg := appConfig{
		Port: "0", DatabaseURL: "unused-in-tests", GatewayInstance: "replica",
		HeartbeatSec: protocol.HeartbeatSec, DeadAfterSec: protocol.DeadAfterSec,
		HelloTimeout: 2 * time.Second, ClockSkew: protocol.HelloSkewSec * time.Second,
		NonceTTL: protocol.NonceTTLSeconds * time.Second, RevocationInterval: time.Hour,
		InternalToken: llmTestToken,
	}
	if tweak != nil {
		tweak(&cfg)
	}
	g := newGateway(cfg, store)
	g.attachSwitches(newPlatformSwitches(newFakeSwitchStore(), g.metrics))
	g.start(context.Background())
	router := mux.NewRouter()
	g.RegisterRoutes(router)
	server := httptest.NewServer(router)
	tg := &testGateway{gateway: g, store: store, server: server}
	t.Cleanup(func() { server.Close(); g.shutdown() })
	return tg
}

// llmPair returns A (no socket, forwards) and B (holds the runner), sharing a store.
func llmPair(t *testing.T, peersOn bool) (a, b *testGateway, r *runner) {
	b = newLLMGateway(t)
	r = llmRunner(t, b, testOrgID, true)
	a = newLLMReplica(t, b.store, func(c *appConfig) {
		if peersOn {
			c.PeersHost = "peers.test"
		}
	})
	bAddr := strings.TrimPrefix(b.server.URL, "http://")
	a.gateway.peerSet.lookup = func(context.Context, string) ([]string, error) {
		return []string{strings.TrimPrefix(a.server.URL, "http://"), bAddr}, nil
	}
	a.gateway.peerSet.locals = func() []string { return nil }
	a.gateway.cfg.GatewayInstance = strings.TrimPrefix(a.server.URL, "http://")
	return a, b, r
}

func TestLLMRelayPeerForwardsStreamInOrder(t *testing.T) {
	a, _, r := llmPair(t, true)
	release := make(chan struct{})
	go func() {
		req := readLLMRequest(t, r.ws)
		send(t, r.ws, mustEnvelope(t, protocol.TypeLLMResponseStart, protocol.LLMResponseStartPayload{
			RequestID: req.RequestID, Status: 200, ContentType: "text/event-stream"}))
		send(t, r.ws, mustEnvelope(t, protocol.TypeLLMResponseChunk, protocol.LLMResponseChunkPayload{
			RequestID: req.RequestID, Seq: 0, Data: "data: 1\n\n"}))
		<-release
		send(t, r.ws, mustEnvelope(t, protocol.TypeLLMResponseChunk, protocol.LLMResponseChunkPayload{
			RequestID: req.RequestID, Seq: 1, Data: "data: 2\n\n"}))
		send(t, r.ws, mustEnvelope(t, protocol.TypeLLMResponseEnd, protocol.LLMResponseEndPayload{RequestID: req.RequestID}))
	}()
	resp := llmPost(t, a, testOrgID, r.deviceID, `{"model":"llama3.2","stream":true}`)
	if resp.StatusCode != http.StatusOK || resp.Header.Get("Content-Type") != "text/event-stream" {
		t.Fatalf("status=%d type=%q", resp.StatusCode, resp.Header.Get("Content-Type"))
	}
	rd := bufio.NewReader(resp.Body)
	if line, err := rd.ReadString('\n'); err != nil || line != "data: 1\n" {
		t.Fatalf("first line %q (%v): not flushed through the forwarder", line, err)
	}
	close(release)
	rest, _ := io.ReadAll(rd)
	if string(rest) != "\ndata: 2\n\n" {
		t.Fatalf("rest = %q", rest)
	}
	if v := a.metricValue(t, `reelbolt_runner_gateway_llm_requests_total{result="forwarded"}`); v != 1 {
		t.Fatalf("forwarded metric = %v", v)
	}
}

func TestLLMRelayPeerHopIsNeverForwarded(t *testing.T) {
	a, b, r := llmPair(t, true)
	var hits atomic.Int32
	// A third peer that would count any further forward.
	spy := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { hits.Add(1) }))
	t.Cleanup(spy.Close)
	b.gateway.peerSet.lookup = func(context.Context, string) ([]string, error) {
		return []string{strings.TrimPrefix(spy.URL, "http://")}, nil
	}
	// A hop arriving at a replica without the socket: use A as the receiver, with
	// its peers pointing at the spy.
	a.gateway.peerSet.lookup = b.gateway.peerSet.lookup
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	req, _ := http.NewRequestWithContext(ctx, http.MethodPost, a.llmURL(testOrgID, r.deviceID, "/v1/chat/completions"), strings.NewReader(`{}`))
	req.Header.Set("Authorization", "Bearer "+llmTestToken)
	req.Header.Set(proxiedHeader, "x")
	req.Header.Set(llmHopHeader, "1")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode != http.StatusServiceUnavailable || hits.Load() != 0 {
		t.Fatalf("status=%d forwards=%d, want 503 and none", resp.StatusCode, hits.Load())
	}
}

func TestLLMRelayPeersAllOffline(t *testing.T) {
	b := newLLMGateway(t) // no runner connected anywhere
	d := b.newRunner(t, testOrgID)
	a := newLLMReplica(t, b.store, func(c *appConfig) { c.PeersHost = "peers.test" })
	a.gateway.peerSet.lookup = func(context.Context, string) ([]string, error) {
		return []string{strings.TrimPrefix(b.server.URL, "http://")}, nil
	}
	a.gateway.peerSet.locals = func() []string { return nil }
	resp := llmPost(t, a, testOrgID, d.deviceID, `{}`)
	body, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusServiceUnavailable || !strings.Contains(string(body), "runner_offline") {
		t.Fatalf("status=%d body=%s", resp.StatusCode, body)
	}
}

func TestLLMRelayPeerOrgMismatchNotForwarded(t *testing.T) {
	a, _, r := llmPair(t, true)
	var hits atomic.Int32
	spy := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { hits.Add(1) }))
	t.Cleanup(spy.Close)
	a.gateway.peerSet.lookup = func(context.Context, string) ([]string, error) {
		return []string{strings.TrimPrefix(spy.URL, "http://")}, nil
	}
	resp := llmPost(t, a, "11111111-1111-4111-8111-111111111111", r.deviceID, `{}`)
	if resp.StatusCode != http.StatusNotFound || hits.Load() != 0 {
		t.Fatalf("status=%d forwards=%d, want 404 and none", resp.StatusCode, hits.Load())
	}
}

func TestLLMRelayPeersDisabledUnchanged(t *testing.T) {
	a, b, r := llmPair(t, false)
	var hits atomic.Int32
	b.server.Config.Handler = http.HandlerFunc(func(http.ResponseWriter, *http.Request) { hits.Add(1) })
	resp := llmPost(t, a, testOrgID, r.deviceID, `{}`)
	if resp.StatusCode != http.StatusServiceUnavailable || hits.Load() != 0 {
		t.Fatalf("status=%d forwards=%d, want 503 and none", resp.StatusCode, hits.Load())
	}
}

func TestClusterInternalAddresses(t *testing.T) {
	for host, want := range map[string]bool{
		"127.0.0.1": true, "10.1.2.3": true, "172.16.0.9": true, "192.168.1.1": true,
		"169.254.1.1": true, "fd00::1": true, "::1": true,
		"8.8.8.8": false, "172.32.0.1": false, "2001:db8::1": false, "example.com": false,
	} {
		if got := clusterInternal(host); got != want {
			t.Errorf("clusterInternal(%q) = %v, want %v", host, got, want)
		}
	}
}

func TestLLMPeerSetSkipsPublicAddresses(t *testing.T) {
	s := &llmPeerSet{
		lookup: func(context.Context, string) ([]string, error) { return []string{"8.8.8.8", "10.0.0.2"}, nil },
		locals: func() []string { return nil },
	}
	got := s.peers(context.Background(), appConfig{PeersHost: "peers.test"})
	if len(got) != 1 || got[0] != "10.0.0.2" || s.takeSkipped() != 1 {
		t.Fatalf("peers = %v", got)
	}
}

func TestLLMRelayPeerOfflineDecidedByHeaderNotBody(t *testing.T) {
	a, _, r := llmPair(t, true)
	// A model server whose answer body merely contains the text must be relayed.
	a.gateway.peerSet.lookup = func(context.Context, string) ([]string, error) {
		srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
			w.WriteHeader(http.StatusServiceUnavailable)
			_, _ = w.Write([]byte(`{"error":"runner_offline"}`))
		}))
		t.Cleanup(srv.Close)
		return []string{strings.TrimPrefix(srv.URL, "http://")}, nil
	}
	resp := llmPost(t, a, testOrgID, r.deviceID, `{}`)
	b, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusServiceUnavailable || !strings.Contains(string(b), `"error":"runner_offline"`) {
		t.Fatalf("status=%d body=%q: a body substring must not read as offline", resp.StatusCode, b)
	}
	if v := a.metricValue(t, `reelbolt_runner_gateway_llm_requests_total{result="forwarded"}`); v != 1 {
		t.Fatalf("forwarded metric = %v", v)
	}
}

func TestLLMRelayPeerOversizedBodyIs413(t *testing.T) {
	a, b, r := llmPair(t, true)
	_ = b
	a.gateway.peerSet.lookup = func(context.Context, string) ([]string, error) { return []string{"127.0.0.1:1"}, nil }
	big := `{"x":"` + strings.Repeat("a", protocol.LLMMaxRequestBodyBytes+10) + `"}`
	resp := llmPost(t, a, testOrgID, r.deviceID, big)
	if resp.StatusCode != http.StatusRequestEntityTooLarge {
		t.Fatalf("status = %d, want 413", resp.StatusCode)
	}
}

func TestLLMRelayPeerTruncatedStreamAbortsCaller(t *testing.T) {
	a, _, r := llmPair(t, true)
	a.gateway.peerSet.lookup = func(context.Context, string) ([]string, error) {
		srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
			w.Header().Set("Content-Type", "text/event-stream")
			w.WriteHeader(http.StatusOK)
			_, _ = w.Write([]byte("data: 1\n\n"))
			w.(http.Flusher).Flush()
			panic(http.ErrAbortHandler)
		}))
		t.Cleanup(srv.Close)
		return []string{strings.TrimPrefix(srv.URL, "http://")}, nil
	}
	resp := llmPost(t, a, testOrgID, r.deviceID, `{"stream":true}`)
	_, err := io.ReadAll(resp.Body)
	if err == nil {
		t.Fatal("a truncated peer stream ended as a clean EOF")
	}
}
