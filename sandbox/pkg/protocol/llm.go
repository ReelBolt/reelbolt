package protocol

import (
	"fmt"
	"unicode/utf8"
)

// Local-LLM relay: the gateway forwards an OpenAI-compatible request to the
// user's own model server through the runner's existing outbound connection.
// No URL, host or header ever crosses the wire; the runner decides the target
// from its own configuration.

// Request paths a runner will serve. Anything else is refused.
const (
	LLMPathChatCompletions = "/v1/chat/completions"
	LLMPathModels          = "/v1/models"
	LLMPathEmbeddings      = "/v1/embeddings"
)

// llm.request methods.
const (
	LLMMethodPost = "POST"
	LLMMethodGet  = "GET"
)

// llm.error codes.
const (
	LLMErrDisabled          = "disabled"
	LLMErrUnsupportedPath   = "unsupported_path"
	LLMErrBusy              = "busy"
	LLMErrServerUnreachable = "server_unreachable"
	LLMErrTimeout           = "timeout"
	LLMErrTooLarge          = "too_large"
	LLMErrCancelled         = "cancelled"
	LLMErrBadRequest        = "bad_request"
)

// Size and count limits for the relay.
const (
	// LLMMaxRequestBodyBytes is the largest llm.request body.
	LLMMaxRequestBodyBytes = 3 << 20
	// LLMMaxChunkBytes is the largest llm.response.chunk data.
	LLMMaxChunkBytes = 256 << 10
	// LLMMaxResponseBytes is the largest total response the gateway relays.
	LLMMaxResponseBytes = 32 << 20
	// LLMMaxEmbeddingsResponseBytes is the response cap for /v1/embeddings
	// only: a batch of vectors is much larger than a chat answer.
	LLMMaxEmbeddingsResponseBytes = 64 << 20
	// LLMMaxModels and LLMMaxModelNameLen bound capabilities.localLlm.models.
	LLMMaxModels       = 100
	LLMMaxModelNameLen = 200
	// LLMMaxServerKindLen bounds capabilities.localLlm.serverKind.
	LLMMaxServerKindLen = 64
	// LLMMaxConcurrent bounds capabilities.localLlm.maxConcurrent.
	LLMMaxConcurrent = 64
	// LLMMaxRequestIDLen bounds requestId.
	LLMMaxRequestIDLen = 128
	// LLMMaxTimeoutSec bounds llm.request.timeoutSec.
	LLMMaxTimeoutSec = 900
)

// LLMRequestPayload is llm.request (G to R).
type LLMRequestPayload struct {
	RequestID  string `json:"requestId"`
	Method     string `json:"method"`
	Path       string `json:"path"`
	Body       string `json:"body,omitempty"`
	Stream     bool   `json:"stream"`
	TimeoutSec int    `json:"timeoutSec"`
}

// Validate applies the structural rules. It does not apply the path allowlist:
// an unsupported path is answered with llm.error unsupported_path, not dropped.
func (p LLMRequestPayload) Validate() error {
	if err := validRequestID(p.RequestID); err != nil {
		return err
	}
	if p.Method != LLMMethodPost && p.Method != LLMMethodGet {
		return fmt.Errorf("llm.request.method %q is not POST or GET", p.Method)
	}
	if len(p.Body) > LLMMaxRequestBodyBytes {
		return fmt.Errorf("llm.request.body is %d bytes, limit is %d", len(p.Body), LLMMaxRequestBodyBytes)
	}
	if p.TimeoutSec < 0 || p.TimeoutSec > LLMMaxTimeoutSec {
		return fmt.Errorf("llm.request.timeoutSec %d is out of range", p.TimeoutSec)
	}
	return nil
}

// LLMPathAllowed reports whether method and path are on the fixed allowlist.
func LLMPathAllowed(method, path string) bool {
	switch path {
	case LLMPathChatCompletions, LLMPathEmbeddings:
		return method == LLMMethodPost
	case LLMPathModels:
		return method == LLMMethodGet
	}
	return false
}

// LLMMaxResponseBytesFor is the total response cap for a request path.
func LLMMaxResponseBytesFor(path string) int64 {
	if path == LLMPathEmbeddings {
		return LLMMaxEmbeddingsResponseBytes
	}
	return LLMMaxResponseBytes
}

// LLMResponseStartPayload is llm.response.start (R to G).
type LLMResponseStartPayload struct {
	RequestID   string `json:"requestId"`
	Status      int    `json:"status"`
	ContentType string `json:"contentType,omitempty"`
}

// LLMResponseChunkPayload is llm.response.chunk (R to G).
type LLMResponseChunkPayload struct {
	RequestID string `json:"requestId"`
	Seq       int    `json:"seq"`
	Data      string `json:"data"`
}

// Validate checks the chunk size and that the data is valid UTF-8.
func (p LLMResponseChunkPayload) Validate() error {
	if err := validRequestID(p.RequestID); err != nil {
		return err
	}
	if p.Seq < 0 {
		return fmt.Errorf("llm.response.chunk.seq must not be negative")
	}
	if len(p.Data) > LLMMaxChunkBytes {
		return fmt.Errorf("llm.response.chunk.data is %d bytes, limit is %d", len(p.Data), LLMMaxChunkBytes)
	}
	if !utf8.ValidString(p.Data) {
		return fmt.Errorf("llm.response.chunk.data is not valid UTF-8")
	}
	return nil
}

// LLMResponseEndPayload is llm.response.end (R to G).
type LLMResponseEndPayload struct {
	RequestID string `json:"requestId"`
	Bytes     int64  `json:"bytes"`
}

// LLMErrorPayload is llm.error (R to G).
type LLMErrorPayload struct {
	RequestID string `json:"requestId"`
	Code      string `json:"code"`
	Message   string `json:"message,omitempty"`
}

// LLMCancelPayload is llm.cancel (G to R).
type LLMCancelPayload struct {
	RequestID string `json:"requestId"`
}

// LocalLLMCapabilities is hello.capabilities.localLlm. It is self-reported and
// untrusted, and never carries a URL or credential.
type LocalLLMCapabilities struct {
	Enabled       bool     `json:"enabled"`
	ServerKind    string   `json:"serverKind,omitempty"`
	Models        []string `json:"models,omitempty"`
	MaxConcurrent int      `json:"maxConcurrent,omitempty"`
}

// Validate bounds the counts and lengths of the self-reported fields.
func (l LocalLLMCapabilities) Validate() error {
	if len(l.ServerKind) > LLMMaxServerKindLen {
		return fmt.Errorf("capabilities.localLlm.serverKind is longer than %d", LLMMaxServerKindLen)
	}
	if len(l.Models) > LLMMaxModels {
		return fmt.Errorf("capabilities.localLlm.models has %d entries, limit is %d", len(l.Models), LLMMaxModels)
	}
	for _, m := range l.Models {
		if len(m) > LLMMaxModelNameLen {
			return fmt.Errorf("capabilities.localLlm.models entry is longer than %d", LLMMaxModelNameLen)
		}
	}
	if l.MaxConcurrent < 0 || l.MaxConcurrent > LLMMaxConcurrent {
		return fmt.Errorf("capabilities.localLlm.maxConcurrent %d is out of range", l.MaxConcurrent)
	}
	return nil
}

func validRequestID(id string) error {
	if id == "" || len(id) > LLMMaxRequestIDLen {
		return fmt.Errorf("llm requestId must be 1 to %d bytes", LLMMaxRequestIDLen)
	}
	return nil
}
