// Package llmrelay lets the user share a model server they already run
// (Ollama, LM Studio, llama.cpp, vLLM, ...) with their ReelBolt workspace.
//
// The server is the user's own. This package only relays: the gateway sends
// llm.request over the runner's existing outbound connection, the relay calls
// the user's server on their machine and streams the answer back. Nothing is
// opened for inbound traffic, and the target is decided by the user's own
// settings alone:
//
//   - only a fixed path allowlist is served;
//   - the host, URL and headers of a message are never used (the protocol has no
//     field for them);
//   - redirects are refused;
//   - connections are made only to loopback addresses unless the user explicitly
//     allowed another host;
//   - body, time and concurrency are capped.
//
// Prompts and responses live in memory for the length of one request. They are
// never written to disk or to a log.
package llmrelay

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"syscall"
	"time"
	"unicode/utf8"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
)

const (
	// defaultTimeout applies when a request names none.
	defaultTimeout = 5 * time.Minute
	// maxTimeout caps what a request may ask for.
	maxTimeout = 15 * time.Minute
	// dialTimeout bounds connecting to the local server.
	dialTimeout = 5 * time.Second
	// modelsCap bounds the /v1/models document the relay reads for itself.
	modelsCap = 1 << 20
	// modelsTTL is how long the model list is considered fresh.
	modelsTTL = 5 * time.Minute
)

// maxResponseBytes is the most one request may stream back. A variable so a
// test can lower it.
var maxResponseBytes int64 = protocol.LLMMaxResponseBytes

// maxEmbeddingsResponseBytes is the same cap for /v1/embeddings.
var maxEmbeddingsResponseBytes int64 = protocol.LLMMaxEmbeddingsResponseBytes

// Settings is the persisted setting (runner.json "localLlm").
type Settings = config.LocalLLM

// Options build a Relay.
type Options struct {
	Settings Settings
	// Allowed reports whether the runner may serve right now (the governor's
	// verdict). Nil means always. When it says no, requests are answered
	// "disabled".
	Allowed func() bool
	// Client overrides the HTTP client, for tests. It must not follow redirects.
	Client *http.Client
	// Now overrides the clock, for tests.
	Now func() time.Time
}

// Status is what the UI shows. It carries counters and the model names, never
// request content.
type Status struct {
	Enabled     bool      `json:"enabled"`
	BaseURL     string    `json:"baseUrl"`
	Active      int       `json:"active"`
	Served      int64     `json:"served"`
	Failed      int64     `json:"failed"`
	LastRequest time.Time `json:"lastRequest,omitzero"`
	ServerKind  string    `json:"serverKind,omitempty"`
	Models      []string  `json:"models,omitempty"`
	// Reachable is the result of the last model-list refresh.
	Reachable bool `json:"reachable"`
}

// Relay serves llm.request messages. It implements agent.LLMHandler.
type Relay struct {
	opts     Options
	now      func() time.Time
	mu       sync.Mutex
	set      Settings
	base     *url.URL
	client   *http.Client
	inflight map[string]context.CancelFunc
	served   int64
	failed   int64
	last     time.Time

	models      []string
	kind        string
	reachable   bool
	modelsAt    time.Time
	allowRemote bool
}

var _ agent.LLMHandler = (*Relay)(nil)

// New builds a relay. Invalid settings leave it disabled; Apply reports why.
func New(opts Options) *Relay {
	r := &Relay{opts: opts, inflight: map[string]context.CancelFunc{}, now: opts.Now}
	if r.now == nil {
		r.now = time.Now
	}
	_ = r.Apply(opts.Settings)
	return r
}

// Apply installs new settings (from the UI or a reloaded config). Invalid
// settings are refused and the relay is switched off.
func (r *Relay) Apply(s Settings) error {
	base, err := config.ParseLocalLLMBase(s.BaseURL, s.AllowRemoteHost)
	if err == nil && (s.MaxConcurrent < 0 || s.MaxConcurrent > config.MaxLocalLLMConcurrent) {
		err = fmt.Errorf("maxConcurrent out of range")
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if err != nil {
		r.set = Settings{}
		r.base = nil
		r.models, r.kind, r.reachable = nil, "", false
		return err
	}
	changedTarget := r.base == nil || r.base.String() != base.String()
	r.set = s
	r.base = base
	r.allowRemote = s.AllowRemoteHost
	r.client = r.opts.Client
	if r.client == nil {
		r.client = newClient(s.AllowRemoteHost)
	}
	if changedTarget || !s.Enabled {
		r.models, r.kind, r.reachable, r.modelsAt = nil, "", false, time.Time{}
	}
	return nil
}

// Settings returns the settings in force.
func (r *Relay) Settings() Settings {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.set
}

// Status is a snapshot for the UI.
func (r *Relay) Status() Status {
	r.mu.Lock()
	defer r.mu.Unlock()
	st := Status{
		Enabled: r.set.Enabled && r.base != nil, Active: len(r.inflight),
		Served: r.served, Failed: r.failed, LastRequest: r.last,
		ServerKind: r.kind, Models: append([]string(nil), r.models...), Reachable: r.reachable,
	}
	if r.base != nil {
		st.BaseURL = r.base.String()
	}
	return st
}

// Capabilities is hello.capabilities.localLlm: nil unless sharing is enabled.
// It carries names only, never the URL.
func (r *Relay) Capabilities() *protocol.LocalLLMCapabilities {
	r.mu.Lock()
	defer r.mu.Unlock()
	if !r.set.Enabled || r.base == nil {
		return nil
	}
	models := make([]string, 0, len(r.models))
	for _, m := range r.models {
		if len(models) == protocol.LLMMaxModels {
			break
		}
		if m != "" && len(m) <= protocol.LLMMaxModelNameLen {
			models = append(models, m)
		}
	}
	return &protocol.LocalLLMCapabilities{
		Enabled: true, ServerKind: r.kind, Models: models, MaxConcurrent: r.set.Concurrency(),
	}
}

// ModelsStale reports whether the model list is due for a refresh.
func (r *Relay) ModelsStale() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.set.Enabled && r.base != nil && r.now().Sub(r.modelsAt) >= modelsTTL
}

// RefreshModels asks the user's server for its model list and server kind.
// It reports whether the list changed. An unreachable server clears the list.
func (r *Relay) RefreshModels(ctx context.Context) (changed bool, err error) {
	r.mu.Lock()
	base, client, enabled := r.base, r.client, r.set.Enabled
	r.mu.Unlock()
	if base == nil || !enabled {
		return false, errors.New("llmrelay: sharing is off")
	}
	models, owner, err := fetchModels(ctx, client, targetURL(base, protocol.LLMPathModels))
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.base != base {
		return false, nil // settings changed while we were asking
	}
	r.modelsAt = r.now()
	if err != nil {
		changed = r.reachable || len(r.models) > 0
		r.models, r.reachable = nil, false
		return changed, err
	}
	kind := guessKind(portOf(base), owner)
	changed = !r.reachable || r.kind != kind || !equalStrings(r.models, models)
	r.models, r.kind, r.reachable = models, kind, true
	return changed, nil
}

// HandleRequest implements agent.LLMHandler: it never blocks the read loop.
func (r *Relay) HandleRequest(req protocol.LLMRequestPayload, send agent.LLMSender) {
	go r.serve(req, send)
}

// Cancel implements agent.LLMHandler.
func (r *Relay) Cancel(requestID string) {
	r.mu.Lock()
	cancel := r.inflight[requestID]
	r.mu.Unlock()
	if cancel != nil {
		cancel()
	}
}

// CancelAll implements agent.LLMHandler.
func (r *Relay) CancelAll() {
	r.mu.Lock()
	all := make([]context.CancelFunc, 0, len(r.inflight))
	for _, c := range r.inflight {
		all = append(all, c)
	}
	r.mu.Unlock()
	for _, c := range all {
		c()
	}
}

func (r *Relay) fail(send agent.LLMSender, id, code, msg string) {
	_ = send(protocol.TypeLLMError, protocol.LLMErrorPayload{RequestID: id, Code: code, Message: msg})
}

// begin reserves a slot, or says why not.
func (r *Relay) begin(req protocol.LLMRequestPayload, cancel context.CancelFunc) (code, msg string) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if !r.set.Enabled || r.base == nil || (r.opts.Allowed != nil && !r.opts.Allowed()) {
		return protocol.LLMErrDisabled, "this runner is not sharing a local model right now"
	}
	if !protocol.LLMPathAllowed(req.Method, req.Path) {
		return protocol.LLMErrUnsupportedPath, "that path is not served"
	}
	if len(req.Body) > protocol.LLMMaxRequestBodyBytes {
		return protocol.LLMErrTooLarge, "the request body is too large"
	}
	if _, dup := r.inflight[req.RequestID]; dup {
		return protocol.LLMErrBadRequest, "duplicate request id"
	}
	if len(r.inflight) >= r.set.Concurrency() {
		return protocol.LLMErrBusy, "the runner is already serving its maximum number of requests"
	}
	r.inflight[req.RequestID] = cancel
	r.last = r.now()
	return "", ""
}

func (r *Relay) countFailure() {
	r.mu.Lock()
	r.failed++
	r.mu.Unlock()
}

func (r *Relay) finish(id string, ok bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	delete(r.inflight, id)
	if ok {
		r.served++
	} else {
		r.failed++
	}
}

func (r *Relay) serve(req protocol.LLMRequestPayload, send agent.LLMSender) {
	timeout := time.Duration(req.TimeoutSec) * time.Second
	if timeout <= 0 {
		timeout = defaultTimeout
	}
	if timeout > maxTimeout {
		timeout = maxTimeout
	}
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()

	if len(req.Body) > protocol.LLMMaxRequestBodyBytes {
		r.countFailure()
		r.fail(send, req.RequestID, protocol.LLMErrTooLarge, "the request body is too large")
		return
	}
	if err := req.Validate(); err != nil {
		r.countFailure()
		r.fail(send, req.RequestID, protocol.LLMErrBadRequest, "the request was not valid")
		return
	}
	if code, msg := r.begin(req, cancel); code != "" {
		r.countFailure()
		r.fail(send, req.RequestID, code, msg)
		return
	}
	ok := false
	defer func() { r.finish(req.RequestID, ok) }()

	r.mu.Lock()
	base, client := r.base, r.client
	r.mu.Unlock()

	var body io.Reader
	if req.Method == protocol.LLMMethodPost {
		body = strings.NewReader(req.Body)
	}
	httpReq, err := http.NewRequestWithContext(ctx, req.Method, targetURL(base, req.Path), body)
	if err != nil {
		r.fail(send, req.RequestID, protocol.LLMErrBadRequest, "the request could not be built")
		return
	}
	if body != nil {
		httpReq.Header.Set("Content-Type", "application/json")
	}
	if req.Stream {
		httpReq.Header.Set("Accept", "text/event-stream")
	} else {
		httpReq.Header.Set("Accept", "application/json")
	}
	resp, err := client.Do(httpReq)
	if err != nil {
		r.fail(send, req.RequestID, classify(ctx, err), "")
		return
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode >= 300 && resp.StatusCode < 400 {
		// A redirect is never followed: it could lead anywhere.
		r.fail(send, req.RequestID, protocol.LLMErrServerUnreachable, "the local server tried to redirect")
		return
	}
	ct := resp.Header.Get("Content-Type")
	if len(ct) > 200 || strings.ContainsAny(ct, "\r\n") {
		ct = ""
	}
	if err := send(protocol.TypeLLMResponseStart, protocol.LLMResponseStartPayload{
		RequestID: req.RequestID, Status: resp.StatusCode, ContentType: ct,
	}); err != nil {
		return // the connection is gone
	}
	limit := maxResponseBytes
	if req.Path == protocol.LLMPathEmbeddings {
		limit = maxEmbeddingsResponseBytes
	}
	total, err := pump(ctx, resp.Body, req.RequestID, send, limit)
	switch {
	case err == nil:
		if err := send(protocol.TypeLLMResponseEnd, protocol.LLMResponseEndPayload{RequestID: req.RequestID, Bytes: total}); err == nil {
			ok = resp.StatusCode < 400
		}
	case errors.Is(err, errTooLarge):
		r.fail(send, req.RequestID, protocol.LLMErrTooLarge, "the response is too large")
	case errors.Is(err, errSend):
		// the gateway side is gone; nothing to tell it
	default:
		r.fail(send, req.RequestID, classify(ctx, err), "")
	}
}

var (
	errTooLarge = errors.New("llmrelay: response too large")
	errSend     = errors.New("llmrelay: send failed")
)

// pump streams the body as chunks of at most LLMMaxChunkBytes of valid UTF-8,
// sending each read as soon as it arrives so SSE events are not held back.
func pump(ctx context.Context, body io.Reader, id string, send agent.LLMSender, limit int64) (int64, error) {
	buf := make([]byte, protocol.LLMMaxChunkBytes)
	var carry []byte // an incomplete rune at the end of the previous read
	var total int64
	seq := 0
	emit := func(b []byte) error {
		if len(b) == 0 {
			return nil
		}
		data := string(b)
		if !utf8.ValidString(data) {
			data = strings.ToValidUTF8(data, "�")
		}
		if len(data) > protocol.LLMMaxChunkBytes {
			// Replacement characters can grow the text; split on a rune boundary.
			for len(data) > 0 {
				n := min(len(data), protocol.LLMMaxChunkBytes)
				for n > 0 && n < len(data) && !utf8.RuneStart(data[n]) {
					n--
				}
				if err := send(protocol.TypeLLMResponseChunk, protocol.LLMResponseChunkPayload{RequestID: id, Seq: seq, Data: data[:n]}); err != nil {
					return errSend
				}
				seq++
				data = data[n:]
			}
			return nil
		}
		if err := send(protocol.TypeLLMResponseChunk, protocol.LLMResponseChunkPayload{RequestID: id, Seq: seq, Data: data}); err != nil {
			return errSend
		}
		seq++
		return nil
	}
	for {
		start := copy(buf, carry)
		carry = carry[:0]
		n, rerr := body.Read(buf[start : len(buf)-3])
		data := buf[:start+n]
		total += int64(n)
		if total > limit {
			return total, errTooLarge
		}
		if rerr == nil {
			// hold back an incomplete trailing rune for the next read
			if cut := incompleteTail(data); cut > 0 {
				carry = append(carry, data[len(data)-cut:]...)
				data = data[:len(data)-cut]
			}
		}
		if err := emit(data); err != nil {
			return total, err
		}
		if rerr != nil {
			if errors.Is(rerr, io.EOF) {
				if err := emit(carry); err != nil {
					return total, err
				}
				return total, nil
			}
			return total, rerr
		}
		if err := ctx.Err(); err != nil {
			return total, err
		}
	}
}

// incompleteTail returns how many bytes at the end of b begin a rune that is
// not finished yet (0 when b ends on a rune boundary or in invalid bytes).
func incompleteTail(b []byte) int {
	for i := 1; i <= 3 && i <= len(b); i++ {
		c := b[len(b)-i]
		if utf8.RuneStart(c) {
			if c >= 0xC0 && !utf8.FullRune(b[len(b)-i:]) {
				return i
			}
			return 0
		}
	}
	return 0
}

// classify maps a transport error to an llm.error code.
func classify(ctx context.Context, err error) string {
	switch {
	case errors.Is(ctx.Err(), context.DeadlineExceeded) || errors.Is(err, context.DeadlineExceeded):
		return protocol.LLMErrTimeout
	case errors.Is(ctx.Err(), context.Canceled) || errors.Is(err, context.Canceled):
		return protocol.LLMErrCancelled
	}
	// A connect that timed out (a machine that is switched off) is "unreachable",
	// not a slow answer: only a timeout after the connection was made is "timeout".
	var oe *net.OpError
	if errors.As(err, &oe) && oe.Op == "dial" {
		return protocol.LLMErrServerUnreachable
	}
	var ne net.Error
	if errors.As(err, &ne) && ne.Timeout() {
		return protocol.LLMErrTimeout
	}
	return protocol.LLMErrServerUnreachable
}

// targetURL joins the user's base with an allowlisted path. The path is one of
// two constants by the time it gets here.
func targetURL(base *url.URL, path string) string {
	return strings.TrimRight(base.String(), "/") + path
}

func portOf(u *url.URL) string { return u.Port() }

// newClient builds the HTTP client for the user's server: no proxy, no
// redirects, and a dialer that connects only to loopback unless the user
// allowed another host (which also defeats a name that resolves elsewhere).
func newClient(allowRemote bool) *http.Client {
	dialer := &net.Dialer{Timeout: dialTimeout, Control: loopbackOnly(allowRemote)}
	return &http.Client{
		Transport: &http.Transport{
			Proxy:       nil,
			DialContext: dialer.DialContext,
			// No ResponseHeaderTimeout: servers send headers for a non-streamed
			// answer only once it is complete, so the wait is bounded by the
			// request's own context (its timeout, capped by maxTimeout).
			MaxIdleConnsPerHost: 2,
			IdleConnTimeout:     30 * time.Second,
			DisableCompression:  true,
		},
		CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },
	}
}

// loopbackOnly is a net.Dialer Control hook that refuses a connection to any
// address that is not loopback, after name resolution.
func loopbackOnly(allowRemote bool) func(network, address string, c syscall.RawConn) error {
	return func(_, address string, _ syscall.RawConn) error {
		if allowRemote {
			return nil
		}
		host, _, err := net.SplitHostPort(address)
		if err != nil {
			return err
		}
		if ip := net.ParseIP(host); ip == nil || !ip.IsLoopback() {
			return fmt.Errorf("llmrelay: %s is not a loopback address", host)
		}
		return nil
	}
}

func equalStrings(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}
