package agent

import (
	"sync"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

type fakeLLM struct {
	mu        sync.Mutex
	requests  []protocol.LLMRequestPayload
	cancelled []string
	all       int
}

func (f *fakeLLM) HandleRequest(req protocol.LLMRequestPayload, send LLMSender) {
	f.mu.Lock()
	f.requests = append(f.requests, req)
	f.mu.Unlock()
	_ = send(protocol.TypeLLMResponseStart, protocol.LLMResponseStartPayload{RequestID: req.RequestID, Status: 200})
	_ = send(protocol.TypeLLMResponseEnd, protocol.LLMResponseEndPayload{RequestID: req.RequestID})
}
func (f *fakeLLM) Cancel(id string) {
	f.mu.Lock()
	f.cancelled = append(f.cancelled, id)
	f.mu.Unlock()
}
func (f *fakeLLM) CancelAll() { f.mu.Lock(); f.all++; f.mu.Unlock() }

func TestLLMRequestsReachTheHandlerAndAnswersGoBack(t *testing.T) {
	llm := &fakeLLM{}
	ta := startTestAgent(t, func(c *Config) { c.LLM = llm })
	conn := ta.gw.awaitDial(t)
	conn.handshake(t)

	conn.send(t, protocol.TypeLLMRequest, protocol.LLMRequestPayload{
		RequestID: "r1", Method: "POST", Path: protocol.LLMPathChatCompletions, Body: "{}", TimeoutSec: 5,
	})
	start := conn.nextOf(t, protocol.TypeLLMResponseStart)
	if p := readTyped[protocol.LLMResponseStartPayload](t, start.env); p.RequestID != "r1" || p.Status != 200 {
		t.Fatalf("start = %+v", p)
	}
	conn.nextOf(t, protocol.TypeLLMResponseEnd)

	conn.send(t, protocol.TypeLLMCancel, protocol.LLMCancelPayload{RequestID: "r1"})
	waitFor(t, "cancel reaches the handler", func() bool {
		llm.mu.Lock()
		defer llm.mu.Unlock()
		return len(llm.cancelled) == 1 && llm.cancelled[0] == "r1"
	})
	if len(llm.requests) != 1 || llm.requests[0].Path != protocol.LLMPathChatCompletions {
		t.Fatalf("requests = %+v", llm.requests)
	}
}

func TestLLMRequestsAreIgnoredWithoutAHandler(t *testing.T) {
	ta := startTestAgent(t)
	conn := ta.gw.awaitDial(t)
	conn.handshake(t)
	conn.send(t, protocol.TypeLLMRequest, protocol.LLMRequestPayload{
		RequestID: "r1", Method: "POST", Path: protocol.LLMPathChatCompletions, TimeoutSec: 5,
	})
	conn.noFrameOf(t, protocol.TypeLLMResponseStart, 200*time.Millisecond)
	conn.noFrameOf(t, protocol.TypeLLMError, 50*time.Millisecond)
}

func TestInvalidLLMRequestIsRefusedNotServed(t *testing.T) {
	llm := &fakeLLM{}
	ta := startTestAgent(t, func(c *Config) { c.LLM = llm })
	conn := ta.gw.awaitDial(t)
	conn.handshake(t)
	conn.send(t, protocol.TypeLLMRequest, protocol.LLMRequestPayload{RequestID: "bad", Method: "DELETE", Path: "/x"})
	e := readTyped[protocol.LLMErrorPayload](t, conn.nextOf(t, protocol.TypeLLMError).env)
	if e.RequestID != "bad" || e.Code != protocol.LLMErrBadRequest {
		t.Fatalf("error = %+v", e)
	}
	if len(llm.requests) != 0 {
		t.Fatal("an invalid request reached the handler")
	}
}

func TestHelloCapabilitiesAreRebuiltPerConnect(t *testing.T) {
	var mu sync.Mutex
	models := []string{"a"}
	ta := startTestAgent(t, func(c *Config) {
		c.CapabilitiesFunc = func() protocol.Capabilities {
			mu.Lock()
			defer mu.Unlock()
			caps := testCapabilities(true)
			caps.LocalLLM = &protocol.LocalLLMCapabilities{Enabled: true, ServerKind: "ollama", Models: append([]string(nil), models...), MaxConcurrent: 2}
			return caps
		}
	})
	conn := ta.gw.awaitDial(t)
	hello := conn.handshake(t)
	if l := hello.Capabilities.LocalLLM; l == nil || len(l.Models) != 1 || l.Models[0] != "a" {
		t.Fatalf("hello localLlm = %+v", l)
	}
}

func readTyped[T any](t *testing.T, env protocol.Envelope) T {
	t.Helper()
	p, err := protocol.DecodePayload[T](env)
	if err != nil {
		t.Fatal(err)
	}
	return p
}
