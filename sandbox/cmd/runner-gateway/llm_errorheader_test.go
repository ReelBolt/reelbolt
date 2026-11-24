package main

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

// The relay error header is how the .NET caller tells the gateway's own refusal
// from an error the user's model server produced.
func TestLLMGatewayRefusalsCarryTheRelayErrorHeader(t *testing.T) {
	rec := httptest.NewRecorder()
	writeLLMError(rec, http.StatusServiceUnavailable, "runner_offline", "x")
	if got := rec.Header().Get(llmRelayErrorHeader); got != "runner_offline" {
		t.Fatalf("header = %q, want runner_offline", got)
	}

	g := &gateway{}
	g.cfg.InternalToken = ""
	h := g.requireInternalToken(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {}))
	rec = httptest.NewRecorder()
	h.ServeHTTP(rec, httptest.NewRequest(http.MethodPost, "/", nil))
	if got := rec.Header().Get(llmRelayErrorHeader); got != "internal_api_disabled" {
		t.Fatalf("disabled header = %q", got)
	}

	g.cfg.InternalToken = "secret"
	h = g.requireInternalToken(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {}))
	rec = httptest.NewRecorder()
	h.ServeHTTP(rec, httptest.NewRequest(http.MethodPost, "/", nil))
	if rec.Code != http.StatusUnauthorized || rec.Header().Get(llmRelayErrorHeader) != "unauthorized" {
		t.Fatalf("unauthorized: %d %q", rec.Code, rec.Header().Get(llmRelayErrorHeader))
	}
}
