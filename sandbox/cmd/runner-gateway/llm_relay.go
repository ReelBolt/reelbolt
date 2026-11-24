package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"log"
	"net/http"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"github.com/google/uuid"
	"github.com/gorilla/mux"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// The local-LLM relay. An engine or inference service posts an OpenAI-style
// request to the internal API; the gateway hands it to the paired desktop runner
// over that runner's existing outbound socket (llm.request) and streams the
// runner's answer (llm.response.*) back as it arrives. The model server is the
// user's own; the gateway only relays. Nothing here logs or stores a body.
//
//	POST /internal/v1/llm/orgs/{orgId}/devices/{deviceId}/v1/chat/completions
//	POST /internal/v1/llm/orgs/{orgId}/devices/{deviceId}/v1/embeddings
//	GET  /internal/v1/llm/orgs/{orgId}/devices/{deviceId}/v1/models

const (
	llmDefaultTimeout = 5 * time.Minute
	// llmMaxPerRunner bounds the requests one runner may hold at once; the
	// runner enforces its own, smaller, concurrency cap and answers busy.
	llmMaxPerRunner = 16
	// llmEventBuffer is how many runner events may wait for a slow HTTP client
	// before the runner's read loop is made to wait.
	llmEventBuffer = 32 // 32 x 256 KiB chunks = 8 MiB at most per request
	// llmDeliverWait is how long the runner's read loop waits for a slow client
	// before the request is abandoned.
	llmDeliverWait = 2 * time.Second
	// llmWriteTimeout bounds one write to the runner's socket.
	llmWriteTimeout = 10 * time.Second
)

// llmEvent is one runner message for a waiting request.
type llmEvent struct {
	start *protocol.LLMResponseStartPayload
	chunk *protocol.LLMResponseChunkPayload
	end   *protocol.LLMResponseEndPayload
	err   *protocol.LLMErrorPayload
	// lost marks the runner's connection ending under the request.
	lost bool
}

// pendingLLM is one in-flight relayed request.
type pendingLLM struct {
	id   string
	conn *conn
	ch   chan llmEvent
	// done is closed when the HTTP handler has finished, so a late delivery does
	// not block the runner's read loop.
	done chan struct{}
	// overflow is closed when the handler fell too far behind the runner.
	overflow     chan struct{}
	overflowOnce sync.Once
}

// llmTable correlates in-flight requests with the runner messages answering
// them. It is per replica: only the replica holding the socket sees the answer.
type llmTable struct {
	mu   sync.Mutex
	reqs map[string]*pendingLLM
	// deliverWait overrides llmDeliverWait when positive (tests).
	deliverWait time.Duration
}

func newLLMTable() *llmTable { return &llmTable{reqs: map[string]*pendingLLM{}} }

// begin registers a request; false means the runner is at its per-connection cap.
func (t *llmTable) begin(p *pendingLLM) bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	n := 0
	for _, o := range t.reqs {
		if o.conn == p.conn {
			n++
		}
	}
	if n >= llmMaxPerRunner {
		return false
	}
	t.reqs[p.id] = p
	return true
}

func (t *llmTable) end(id string) {
	t.mu.Lock()
	defer t.mu.Unlock()
	delete(t.reqs, id)
}

func (t *llmTable) size() int {
	t.mu.Lock()
	defer t.mu.Unlock()
	return len(t.reqs)
}

// lookup returns the request only if it belongs to the given connection: a
// runner can never answer (or hijack) another runner's request.
func (t *llmTable) lookup(id string, c *conn) *pendingLLM {
	t.mu.Lock()
	defer t.mu.Unlock()
	p := t.reqs[id]
	if p == nil || p.conn != c {
		return nil
	}
	return p
}

// deliver hands an event to the waiting handler. It returns false when nobody is
// waiting for the request any more.
func (t *llmTable) deliver(id string, c *conn, ev llmEvent) bool {
	p := t.lookup(id, c)
	if p == nil {
		return false
	}
	select {
	case p.ch <- ev:
		return true
	case <-p.done:
		return false
	default:
	}
	wait := t.deliverWait
	if wait <= 0 {
		wait = llmDeliverWait
	}
	timer := time.NewTimer(wait)
	defer timer.Stop()
	select {
	case p.ch <- ev:
		return true
	case <-p.done:
		return false
	case <-timer.C:
		p.overflowOnce.Do(func() { close(p.overflow) })
		return false
	}
}

// failConn tells every request on a dying connection that the runner is gone.
func (t *llmTable) failConn(c *conn) {
	t.mu.Lock()
	var hit []*pendingLLM
	for _, p := range t.reqs {
		if p.conn == c {
			hit = append(hit, p)
		}
	}
	t.mu.Unlock()
	for _, p := range hit {
		select {
		case p.ch <- llmEvent{lost: true}:
		default:
			p.overflowOnce.Do(func() { close(p.overflow) })
		}
	}
}

// llmRelayErrorHeader names, on a response, the gateway's own error code. It is
// set on every refusal the gateway makes and never on a relayed server answer.
const llmRelayErrorHeader = "X-ReelBolt-Relay-Error"

func writeLLMError(w http.ResponseWriter, status int, code, message string) {
	// The header is how a caller tells the gateway's own refusal from an error the
	// user's model server produced (which is relayed verbatim, body and status): a
	// body alone cannot, because the server controls its body.
	w.Header().Set(llmRelayErrorHeader, code)
	writeJSON(w, status, map[string]any{"error": map[string]any{"code": code, "message": message}})
}

// registerLLMRoutes wires the relay onto the internal (token-guarded) subrouter.
func (g *gateway) registerLLMRoutes(internal *mux.Router) {
	base := "/llm/orgs/{orgId}/devices/{deviceId}"
	internal.HandleFunc(base+protocol.LLMPathChatCompletions, g.handleLLMRelay(protocol.LLMMethodPost, protocol.LLMPathChatCompletions)).
		Methods(http.MethodPost)
	internal.HandleFunc(base+protocol.LLMPathEmbeddings, g.handleLLMRelay(protocol.LLMMethodPost, protocol.LLMPathEmbeddings)).
		Methods(http.MethodPost)
	internal.HandleFunc(base+protocol.LLMPathModels, g.handleLLMRelay(protocol.LLMMethodGet, protocol.LLMPathModels)).
		Methods(http.MethodGet)
}

// handleLLMMessage routes a runner's llm.response.* / llm.error to the waiter.
func (g *gateway) handleLLMMessage(c *conn, env protocol.Envelope) {
	var id string
	var ev llmEvent
	switch env.Type {
	case protocol.TypeLLMResponseStart:
		p, err := protocol.DecodePayload[protocol.LLMResponseStartPayload](env)
		if err != nil {
			return
		}
		id, ev = p.RequestID, llmEvent{start: &p}
	case protocol.TypeLLMResponseChunk:
		p, err := protocol.DecodePayload[protocol.LLMResponseChunkPayload](env)
		if err != nil || p.Validate() != nil {
			g.metrics.llmRequests.WithLabelValues("bad_runner_message").Inc()
			return
		}
		id, ev = p.RequestID, llmEvent{chunk: &p}
	case protocol.TypeLLMResponseEnd:
		p, err := protocol.DecodePayload[protocol.LLMResponseEndPayload](env)
		if err != nil {
			return
		}
		id, ev = p.RequestID, llmEvent{end: &p}
	case protocol.TypeLLMError:
		p, err := protocol.DecodePayload[protocol.LLMErrorPayload](env)
		if err != nil {
			return
		}
		id, ev = p.RequestID, llmEvent{err: &p}
	default:
		return
	}
	if !g.llm.deliver(id, c, ev) {
		// A late message for a finished request, or one for a request this
		// runner does not own: ignored.
		g.metrics.unexpected.WithLabelValues(string(env.Type)).Inc()
	}
}

// llmErrorStatus maps a runner's llm.error code to the HTTP answer.
func llmErrorStatus(code string) (int, string) {
	switch code {
	case protocol.LLMErrBusy:
		return http.StatusTooManyRequests, "busy"
	case protocol.LLMErrServerUnreachable:
		return http.StatusBadGateway, "local_server_unreachable"
	case protocol.LLMErrTimeout:
		return http.StatusGatewayTimeout, "timeout"
	case protocol.LLMErrDisabled:
		return http.StatusConflict, "local_llm_disabled"
	case protocol.LLMErrTooLarge:
		return http.StatusBadGateway, "local_response_too_large"
	case protocol.LLMErrCancelled:
		return http.StatusBadGateway, "cancelled"
	default: // unsupported_path, bad_request and anything unknown
		return http.StatusBadGateway, "runner_rejected_request"
	}
}

func (g *gateway) handleLLMRelay(method, path string) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		started := time.Now()
		vars := mux.Vars(r)
		orgID, deviceID := strings.ToLower(vars["orgId"]), strings.ToLower(vars["deviceId"])
		result := "error"
		var inBytes, outBytes int64
		reqID := uuid.NewString()
		defer func() {
			g.metrics.llmRequests.WithLabelValues(result).Inc()
			g.metrics.llmBytes.Add(float64(outBytes))
			// Ids, sizes and the outcome only: never a prompt or a response.
			log.Printf("llm relay: req=%s org=%s device=%s result=%s in=%d out=%d dur=%s",
				shortID(reqID), shortID(orgID), shortID(deviceID), result, inBytes, outBytes,
				time.Since(started).Round(time.Millisecond))
		}()

		if !validRunnerID(orgID) || !validRunnerID(deviceID) {
			result = "device_not_found"
			writeLLMError(w, http.StatusNotFound, "device_not_found", "no such runner device in this organisation")
			return
		}
		dev, err := g.store.LookupDevice(r.Context(), deviceID)
		switch {
		case errors.Is(err, ErrDeviceNotFound):
			result = "device_not_found"
			writeLLMError(w, http.StatusNotFound, "device_not_found", "no such runner device in this organisation")
			return
		case err != nil:
			result = "registry_unavailable"
			writeLLMError(w, http.StatusServiceUnavailable, "registry_unavailable", "the device registry is unreachable")
			return
		}
		// Org scoping: a device of another organisation is indistinguishable from
		// a device that does not exist.
		if dev.Kind != protocol.KindDesktop || !strings.EqualFold(dev.OrganizationID, orgID) {
			result = "device_not_found"
			writeLLMError(w, http.StatusNotFound, "device_not_found", "no such runner device in this organisation")
			return
		}
		if dev.Revoked() {
			result = "local_llm_disabled"
			writeLLMError(w, http.StatusConflict, "local_llm_disabled", "this runner is no longer paired")
			return
		}
		c := g.registry.get(deviceID)
		if (c == nil || g.draining.Load()) && g.peerForwardable(r) {
			switch g.forwardLLMToPeers(w, r, method, path, orgID, deviceID) {
			case "too_large":
				result = "request_too_large"
				writeLLMError(w, http.StatusRequestEntityTooLarge, "request_too_large", "the request body is too large")
				return
			case "forwarded":
				result = "forwarded"
				return
			case "peer_unreachable":
				result = "peer_unreachable"
				writeLLMError(w, http.StatusServiceUnavailable, "runner_offline", "the runner is not connected to this gateway")
				return
			}
		}
		if c == nil || g.draining.Load() {
			result = "runner_offline"
			writeLLMError(w, http.StatusServiceUnavailable, "runner_offline", "the runner is not connected to this gateway")
			return
		}
		if !strings.EqualFold(c.orgID, orgID) {
			result = "device_not_found"
			writeLLMError(w, http.StatusNotFound, "device_not_found", "no such runner device in this organisation")
			return
		}
		if l := c.caps.LocalLLM; l == nil || !l.Enabled {
			result = "local_llm_disabled"
			writeLLMError(w, http.StatusConflict, "local_llm_disabled", "this runner does not share a local model")
			return
		}

		body := ""
		stream := false
		if method == protocol.LLMMethodPost {
			r.Body = http.MaxBytesReader(w, r.Body, protocol.LLMMaxRequestBodyBytes)
			raw, err := io.ReadAll(r.Body)
			if err != nil {
				result = "request_too_large"
				writeLLMError(w, http.StatusRequestEntityTooLarge, "request_too_large", "the request body is too large")
				return
			}
			inBytes = int64(len(raw))
			if !utf8.Valid(raw) {
				result = "bad_request"
				writeLLMError(w, http.StatusBadRequest, "bad_request", "the request body must be UTF-8 JSON")
				return
			}
			var probe struct {
				Stream bool `json:"stream"`
			}
			if json.Unmarshal(raw, &probe) != nil || !bytes.HasPrefix(bytes.TrimSpace(raw), []byte("{")) {
				result = "bad_request"
				writeLLMError(w, http.StatusBadRequest, "bad_request", "the request body must be a JSON object")
				return
			}
			body, stream = string(raw), probe.Stream
		}

		timeout := g.llmTimeout
		if timeout <= 0 {
			timeout = llmDefaultTimeout
		}
		ctx, cancel := context.WithTimeout(r.Context(), timeout)
		defer cancel()

		p := &pendingLLM{
			id: reqID, conn: c, ch: make(chan llmEvent, llmEventBuffer),
			done: make(chan struct{}), overflow: make(chan struct{}),
		}
		if !g.llm.begin(p) {
			result = "busy"
			writeLLMError(w, http.StatusTooManyRequests, "busy", "too many requests are in flight for this runner")
			return
		}
		defer func() {
			close(p.done)
			g.llm.end(reqID)
		}()
		g.metrics.llmInflight.Inc()
		defer g.metrics.llmInflight.Dec()

		reqPayload := protocol.LLMRequestPayload{
			RequestID: reqID, Method: method, Path: path, Body: body, Stream: stream,
			TimeoutSec: int(timeout / time.Second),
		}
		// JSON escaping can grow a body under the cap past the envelope limit.
		if enc, merr := json.Marshal(reqPayload); merr != nil || len(enc) > protocol.MaxPayloadBytes {
			result = "request_too_large"
			writeLLMError(w, http.StatusRequestEntityTooLarge, "request_too_large", "the request body is too large")
			return
		}
		sendCtx, sendCancel := context.WithTimeout(ctx, llmWriteTimeout)
		err = c.sendPayload(sendCtx, protocol.TypeLLMRequest, reqPayload)
		sendCancel()
		if errors.Is(err, protocol.ErrTooLarge) {
			result = "request_too_large"
			writeLLMError(w, http.StatusRequestEntityTooLarge, "request_too_large", "the request body is too large")
			return
		}
		if err != nil {
			result = "runner_connection_lost"
			writeLLMError(w, http.StatusBadGateway, "runner_connection_lost", "the runner connection failed")
			return
		}
		g.metrics.countMessage(string(protocol.TypeLLMRequest), false)

		cancelRunner := func() {
			cctx, ccancel := context.WithTimeout(context.Background(), llmWriteTimeout)
			defer ccancel()
			_ = c.sendPayload(cctx, protocol.TypeLLMCancel, protocol.LLMCancelPayload{RequestID: reqID})
			g.metrics.countMessage(string(protocol.TypeLLMCancel), false)
		}

		flusher, _ := w.(http.Flusher)
		started2 := false
		var expectSeq int
		for {
			select {
			case <-ctx.Done():
				cancelRunner()
				if r.Context().Err() != nil {
					result = "client_disconnected"
					return
				}
				result = "timeout"
				if !started2 {
					writeLLMError(w, http.StatusGatewayTimeout, "timeout", "the local model did not answer in time")
					return
				}
				panic(http.ErrAbortHandler)
			case <-p.overflow:
				cancelRunner()
				result = "client_too_slow"
				if !started2 {
					writeLLMError(w, http.StatusBadGateway, "runner_connection_lost", "the response could not be relayed")
					return
				}
				panic(http.ErrAbortHandler)
			case ev := <-p.ch:
				switch {
				case ev.lost:
					result = "runner_connection_lost"
					if !started2 {
						writeLLMError(w, http.StatusBadGateway, "runner_connection_lost", "the runner disconnected before it answered")
						return
					}
					panic(http.ErrAbortHandler)
				case ev.err != nil:
					status, code := llmErrorStatus(ev.err.Code)
					result = "runner_error_" + code
					if !started2 {
						writeLLMError(w, status, code, "the local model could not serve the request")
						return
					}
					panic(http.ErrAbortHandler)
				case ev.start != nil:
					if started2 {
						continue
					}
					status := ev.start.Status
					if status < 200 || status > 599 {
						cancelRunner()
						result = "bad_runner_message"
						writeLLMError(w, http.StatusBadGateway, "runner_rejected_request", "the runner sent an invalid response")
						return
					}
					if ct := ev.start.ContentType; ct != "" && len(ct) <= 200 && !strings.ContainsAny(ct, "\r\n") {
						w.Header().Set("Content-Type", ct)
					}
					w.Header().Set("Cache-Control", "no-cache")
					w.Header().Set("X-Accel-Buffering", "no")
					w.WriteHeader(status)
					started2 = true
					if flusher != nil {
						flusher.Flush()
					}
				case ev.chunk != nil:
					if !started2 || ev.chunk.Seq != expectSeq {
						cancelRunner()
						result = "bad_runner_message"
						if !started2 {
							writeLLMError(w, http.StatusBadGateway, "runner_rejected_request", "the runner sent an invalid response")
							return
						}
						panic(http.ErrAbortHandler)
					}
					expectSeq++
					if outBytes+int64(len(ev.chunk.Data)) > protocol.LLMMaxResponseBytesFor(path) {
						cancelRunner()
						result = "response_too_large"
						panic(http.ErrAbortHandler)
					}
					n, werr := w.Write([]byte(ev.chunk.Data))
					outBytes += int64(n)
					if werr != nil {
						cancelRunner()
						result = "client_disconnected"
						return
					}
					if flusher != nil {
						flusher.Flush()
					}
				case ev.end != nil:
					if !started2 {
						result = "bad_runner_message"
						writeLLMError(w, http.StatusBadGateway, "runner_rejected_request", "the runner sent an invalid response")
						return
					}
					result = "ok"
					return
				}
			}
		}
	}
}
