package main

import (
	"bytes"
	"context"
	"io"
	"log"
	"net"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// Peer fan-out for the local-LLM relay. With several gateway replicas, the one
// that receives a relay request may not hold the runner's socket. When
// RUNNER_GATEWAY_PEERS_HOST is set, that replica resolves the peers (normally a
// headless service), asks them one by one, and relays the first answer that is
// not "runner_offline". There is deliberately no owner record: the relay has no
// database column of its own.

const (
	// llmHopHeader marks a relay request that is already a forwarded hop; a hop
	// is never forwarded again. proxiedHeader (sessions.go) is set alongside it.
	llmHopHeader = "X-ReelBolt-Relay-Hop"

	llmPeerCap         = 8
	llmPeerConnectWait = 2 * time.Second
	llmPeerLookupWait  = 2 * time.Second
	llmPeerCacheTTL    = 5 * time.Second
)

// llmPeerSet caches the resolved peer addresses for a few seconds. The lookup
// and local-address hooks are replaceable so tests need no DNS.
type llmPeerSet struct {
	mu      sync.Mutex
	at      time.Time
	addrs   []string
	lookup  func(ctx context.Context, host string) ([]string, error)
	locals  func() []string
	clock   func() time.Time
	connect time.Duration
	skipped int // addresses refused as not cluster-internal, since last takeSkipped
}

func (s *llmPeerSet) takeSkipped() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	n := s.skipped
	s.skipped = 0
	return n
}

// clusterInternal reports whether an address may receive the internal bearer
// token over plain HTTP: loopback, private (RFC 1918 / ULA) or link-local only.
func clusterInternal(host string) bool {
	ip := net.ParseIP(host)
	return ip != nil && (ip.IsLoopback() || ip.IsPrivate() || ip.IsLinkLocalUnicast())
}

func (s *llmPeerSet) now() time.Time {
	if s.clock != nil {
		return s.clock()
	}
	return time.Now()
}

// peers returns the candidate peer addresses, never this replica's own.
func (s *llmPeerSet) peers(ctx context.Context, cfg appConfig) []string {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.addrs != nil && s.now().Sub(s.at) < llmPeerCacheTTL {
		return s.addrs
	}
	lookup := s.lookup
	if lookup == nil {
		lookup = func(ctx context.Context, host string) ([]string, error) {
			return net.DefaultResolver.LookupHost(ctx, host)
		}
	}
	lctx, cancel := context.WithTimeout(ctx, llmPeerLookupWait)
	defer cancel()
	found, err := lookup(lctx, cfg.PeersHost)
	if err != nil {
		return nil // not cached: the next request tries again
	}
	own := map[string]bool{strings.TrimSpace(cfg.GatewayInstance): true}
	locals := s.locals
	if locals == nil {
		locals = localInterfaceIPs
	}
	for _, ip := range locals() {
		own[ip] = true
	}
	out := []string{}
	seen := map[string]bool{}
	for _, a := range found {
		host := a
		if h, _, err := net.SplitHostPort(a); err == nil {
			host = h
		}
		if own[a] || own[host] || seen[a] {
			continue
		}
		if !clusterInternal(host) {
			s.skipped++
			continue
		}
		seen[a] = true
		out = append(out, a)
		if len(out) == llmPeerCap {
			break
		}
	}
	s.addrs, s.at = out, s.now()
	return out
}

func localInterfaceIPs() []string {
	addrs, err := net.InterfaceAddrs()
	if err != nil {
		return nil
	}
	var out []string
	for _, a := range addrs {
		if ipn, ok := a.(*net.IPNet); ok {
			out = append(out, ipn.IP.String())
		}
	}
	return out
}

// peerForwardable reports whether this request may be fanned out.
func (g *gateway) peerForwardable(r *http.Request) bool {
	return g.cfg.PeersHost != "" && g.cfg.InternalToken != "" &&
		r.Header.Get(proxiedHeader) == "" && r.Header.Get(llmHopHeader) == ""
}

// forwardLLMToPeers tries each peer and relays the first answer that is not
// runner_offline. It returns "forwarded" when one answered, "peer_unreachable"
// when none was reachable, and "too_large" when the body is over the cap, and "" when every reachable peer
// said runner_offline (the caller then answers 503 runner_offline itself). Nothing is written to w
// unless a peer won.
func (g *gateway) forwardLLMToPeers(w http.ResponseWriter, r *http.Request, method, path, orgID, deviceID string) string {
	var body []byte
	if r.Body != nil && method != http.MethodGet {
		r.Body = http.MaxBytesReader(w, r.Body, protocol.LLMMaxRequestBodyBytes)
		b, err := io.ReadAll(r.Body)
		if err != nil {
			return "too_large"
		}
		body = b
	}
	addrs := g.peerSet.peers(r.Context(), g.cfg)
	for i := g.peerSet.takeSkipped(); i > 0; i-- {
		g.metrics.llmRequests.WithLabelValues("peer_address_skipped").Inc()
	}
	if len(addrs) == 0 {
		return "peer_unreachable"
	}
	timeout := g.llmTimeout
	if timeout <= 0 {
		timeout = llmDefaultTimeout
	}
	ctx, cancel := context.WithTimeout(r.Context(), timeout+g.cfg.ProxyTimeout)
	defer cancel()
	client := &http.Client{Transport: &http.Transport{
		DialContext:       (&net.Dialer{Timeout: llmPeerConnectWait}).DialContext,
		DisableKeepAlives: true,
	}}
	reachable := false
	for _, a := range addrs {
		target, err := gatewayEndpoint(a, g.cfg.InternalPort)
		if err != nil {
			continue
		}
		url := "http://" + target + "/internal/v1/llm/orgs/" + orgID + "/devices/" + deviceID + path
		req, err := http.NewRequestWithContext(ctx, method, url, bytes.NewReader(body))
		if err != nil {
			continue
		}
		if ct := r.Header.Get("Content-Type"); ct != "" {
			req.Header.Set("Content-Type", ct)
		}
		req.Header.Set("Authorization", "Bearer "+g.cfg.InternalToken)
		req.Header.Set(proxiedHeader, g.cfg.GatewayInstance)
		req.Header.Set(llmHopHeader, "1")
		resp, err := client.Do(req)
		if err != nil {
			if ctx.Err() != nil {
				break
			}
			continue
		}
		reachable = true
		if resp.StatusCode == http.StatusServiceUnavailable {
			// Decided on the header only: a model server's body cannot set it.
			if resp.Header.Get(llmRelayErrorHeader) == "runner_offline" {
				_ = resp.Body.Close()
				continue
			}
		}
		g.relayPeerAnswer(w, resp, nil)
		return "forwarded"
	}
	if !reachable {
		return "peer_unreachable"
	}
	return ""
}

// relayPeerAnswer copies a peer's status, content type and body, flushing as it
// goes. head is body bytes already consumed from resp.
func (g *gateway) relayPeerAnswer(w http.ResponseWriter, resp *http.Response, head []byte) {
	defer func() { _ = resp.Body.Close() }()
	if ct := resp.Header.Get("Content-Type"); ct != "" {
		w.Header().Set("Content-Type", ct)
	}
	if v := resp.Header.Get("Cache-Control"); v != "" {
		w.Header().Set("Cache-Control", v)
	}
	if v := resp.Header.Get(llmRelayErrorHeader); v != "" {
		w.Header().Set(llmRelayErrorHeader, v)
	}
	w.WriteHeader(resp.StatusCode)
	flusher, _ := w.(http.Flusher)
	if len(head) > 0 {
		_, _ = w.Write(head)
		if flusher != nil {
			flusher.Flush()
		}
	}
	buf := make([]byte, 8192)
	for {
		n, err := resp.Body.Read(buf)
		if n > 0 {
			if _, werr := w.Write(buf[:n]); werr != nil {
				return
			}
			if flusher != nil {
				flusher.Flush()
			}
		}
		if err != nil {
			if err != io.EOF {
				log.Printf("llm relay: peer stream ended early: %v", err)
				// A half answer must never look like a clean end.
				panic(http.ErrAbortHandler)
			}
			return
		}
	}
}
