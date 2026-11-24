// Package protocol holds the runner protocol wire types defined by
// docs/runner-protocol.md: the envelope, the fifteen message types with their
// payloads, the protocol's string constants and the `hello` signature
// construction.
//
// It is the one source of protocol types for the Go side — the gateway in
// sandbox/cmd/runner-gateway, the worker library (D4b) and, through that
// library, the desktop app (Area E). It performs no I/O and holds no policy
// beyond the structural rules the specification states, so both ends of a
// connection can be built from it without either learning the other's
// internals.
//
// Wire field names are camelCase and every payload struct names them
// explicitly: those tags are the contract, not an implementation detail.
package protocol

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"

	"github.com/google/uuid"
)

// Envelope-level constants from "Transport and envelope".
const (
	// Version is the envelope major version this package speaks.
	Version = 1
	// Subprotocol is the WebSocket subprotocol of the runner endpoint.
	Subprotocol = "reelbolt.runner.v1"
	// ConnectPath is the endpoint a runner dials.
	ConnectPath = "/v1/connect"
	// ProtocolVersion is the "major.minor" protocol version in hello and welcome.
	ProtocolVersion = "1.0"
	// MaxPayloadBytes is the largest serialized payload that may travel inline.
	MaxPayloadBytes = 4 << 20 // 4 MiB
	// ReadLimitBytes is what a receiver sets on its socket: the payload limit
	// plus 64 KiB. Exceeding it closes the connection with code 1009.
	ReadLimitBytes = MaxPayloadBytes + 64<<10
)

// MessageType is one of the message types in the catalogue.
type MessageType string

// The message catalogue. "R to G" is runner to gateway, "G to R" the reverse.
const (
	TypeHello      MessageType = "hello"       // R to G, connection
	TypeWelcome    MessageType = "welcome"     // G to R, connection
	TypeHeartbeat  MessageType = "heartbeat"   // both, connection
	TypeReady      MessageType = "ready"       // R to G, connection
	TypeAssign     MessageType = "assign"      // G to R, job
	TypeAccept     MessageType = "accept"      // R to G, job
	TypeReject     MessageType = "reject"      // R to G, job
	TypeProgress   MessageType = "progress"    // R to G, job
	TypeOpRequest  MessageType = "op.request"  // G to R, session
	TypeOpResponse MessageType = "op.response" // R to G, session
	TypeComplete   MessageType = "complete"    // R to G, job
	TypeFail       MessageType = "fail"        // R to G, job
	TypeCancel     MessageType = "cancel"      // G to R, job
	TypeDrain      MessageType = "drain"       // both, connection
	TypeBye        MessageType = "bye"         // both, connection

	// Local-LLM relay messages (additive, protocol 1.x). They are connection
	// scoped: they carry a requestId in the payload instead of a jobId. A peer
	// that predates them ignores them as unknown types.
	TypeLLMRequest       MessageType = "llm.request"        // G to R
	TypeLLMResponseStart MessageType = "llm.response.start" // R to G
	TypeLLMResponseChunk MessageType = "llm.response.chunk" // R to G
	TypeLLMResponseEnd   MessageType = "llm.response.end"   // R to G
	TypeLLMError         MessageType = "llm.error"          // R to G
	TypeLLMCancel        MessageType = "llm.cancel"         // G to R
)

// messageTypes is the catalogue in the specification's order.
var messageTypes = [...]MessageType{
	TypeHello, TypeWelcome, TypeHeartbeat, TypeReady, TypeAssign, TypeAccept,
	TypeReject, TypeProgress, TypeOpRequest, TypeOpResponse, TypeComplete,
	TypeFail, TypeCancel, TypeDrain, TypeBye,
	TypeLLMRequest, TypeLLMResponseStart, TypeLLMResponseChunk, TypeLLMResponseEnd,
	TypeLLMError, TypeLLMCancel,
}

// MessageTypes returns every type in the catalogue. The slice is a copy: callers
// may hold or reorder it without affecting the package.
func MessageTypes() []MessageType {
	out := make([]MessageType, len(messageTypes))
	copy(out, messageTypes[:])
	return out
}

// Known reports whether t is a type in the catalogue. An unknown type is not an
// error on the wire: a receiver MUST ignore the message, which is how new
// message types are added without a new major version.
func (t MessageType) Known() bool {
	for _, k := range messageTypes {
		if k == t {
			return true
		}
	}
	return false
}

// Scope is how much context a message carries.
type Scope int

const (
	// ScopeConnection messages carry no jobId or sessionId.
	ScopeConnection Scope = iota
	// ScopeJob messages carry jobId.
	ScopeJob
	// ScopeSession messages carry jobId and sessionId.
	ScopeSession
)

// Scope reports the message's scope. An unknown type is treated as
// connection-scoped so that ignoring it stays safe.
func (t MessageType) Scope() Scope {
	switch t {
	case TypeHello, TypeWelcome, TypeHeartbeat, TypeReady, TypeDrain, TypeBye:
		return ScopeConnection
	case TypeOpRequest, TypeOpResponse:
		return ScopeSession
	case TypeAssign, TypeAccept, TypeReject, TypeProgress, TypeComplete, TypeFail, TypeCancel:
		return ScopeJob
	}
	return ScopeConnection
}

// RequiresJobID reports whether the message type is scoped to a job (or to a
// session, which is a job's session).
func (t MessageType) RequiresJobID() bool { return t.Scope() != ScopeConnection }

// RequiresSessionID reports whether the message type is scoped to a session.
func (t MessageType) RequiresSessionID() bool { return t.Scope() == ScopeSession }

// Envelope is one message: the whole of what travels in a WebSocket text frame.
//
// Payload is carried as raw JSON because its shape depends on Type. Use
// DecodePayload or TypedPayload to reach a typed payload struct; use New to
// build one.
type Envelope struct {
	V         int             `json:"v"`
	Type      MessageType     `json:"type"`
	ID        string          `json:"id"`
	JobID     string          `json:"jobId,omitempty"`
	SessionID string          `json:"sessionId,omitempty"`
	Payload   json.RawMessage `json:"payload"`
}

// Decode errors. Callers distinguish them because the specification treats them
// differently: an unknown message type is ignored, a malformed envelope or an
// unsupported version closes the connection.
var (
	// ErrMalformed marks an envelope that is not a JSON object with the
	// required fields, or a payload that is not a JSON object.
	ErrMalformed = errors.New("protocol: malformed envelope")
	// ErrUnsupportedVersion marks an envelope whose major version is not
	// Version. Close with 4400.
	ErrUnsupportedVersion = errors.New("protocol: unsupported envelope version")
	// ErrUnknownType marks a message whose type is not in the catalogue. Ignore
	// it; it is not a fatal error.
	ErrUnknownType = errors.New("protocol: unknown message type")
	// ErrTooLarge marks a frame or payload past the size limits.
	ErrTooLarge = errors.New("protocol: message exceeds the size limit")
)

// Decode parses one wire frame.
//
// Unknown fields are ignored, as the specification requires. An unknown message
// type returns the decoded envelope together with ErrUnknownType, so a receiver
// can log what it ignored; every other error means the frame is unusable.
func Decode(raw []byte) (Envelope, error) {
	var env Envelope
	if len(raw) == 0 {
		return env, fmt.Errorf("%w: empty frame", ErrMalformed)
	}
	if len(raw) > ReadLimitBytes {
		return env, fmt.Errorf("%w: frame is %d bytes, read limit is %d", ErrTooLarge, len(raw), ReadLimitBytes)
	}
	if err := json.Unmarshal(raw, &env); err != nil {
		return env, fmt.Errorf("%w: %w", ErrMalformed, err)
	}
	if env.V != Version {
		return env, fmt.Errorf("%w: v=%d, this peer speaks %d", ErrUnsupportedVersion, env.V, Version)
	}
	if env.ID == "" {
		return env, fmt.Errorf("%w: missing id", ErrMalformed)
	}
	payload := bytes.TrimSpace(env.Payload)
	if len(payload) == 0 {
		return env, fmt.Errorf("%w: missing payload", ErrMalformed)
	}
	if payload[0] != '{' {
		return env, fmt.Errorf("%w: payload must be a JSON object", ErrMalformed)
	}
	if len(env.Payload) > MaxPayloadBytes {
		return env, fmt.Errorf("%w: payload is %d bytes, limit is %d", ErrTooLarge, len(env.Payload), MaxPayloadBytes)
	}
	if !env.Type.Known() {
		return env, fmt.Errorf("%w: %q", ErrUnknownType, env.Type)
	}
	return env, nil
}

// Validate applies the scope rules: an id, a payload and the jobId/sessionId a
// type requires. Decode checks the shape; Validate checks that the message
// carries the context its type promises.
func (e Envelope) Validate() error {
	if e.V != Version {
		return fmt.Errorf("%w: v=%d", ErrUnsupportedVersion, e.V)
	}
	if e.ID == "" {
		return fmt.Errorf("%w: missing id", ErrMalformed)
	}
	if !e.Type.Known() {
		return fmt.Errorf("%w: %q", ErrUnknownType, e.Type)
	}
	if len(bytes.TrimSpace(e.Payload)) == 0 {
		return fmt.Errorf("%w: missing payload", ErrMalformed)
	}
	if e.Type.RequiresJobID() && e.JobID == "" {
		return fmt.Errorf("%w: %s needs jobId", ErrMalformed, e.Type)
	}
	if e.Type.RequiresSessionID() && e.SessionID == "" {
		return fmt.Errorf("%w: %s needs sessionId", ErrMalformed, e.Type)
	}
	return nil
}

// Encode serializes the envelope. A missing version becomes Version and a
// missing payload becomes {} — the specification requires the payload field to
// be present as an object, and an empty body is written as {} rather than null.
func (e Envelope) Encode() ([]byte, error) {
	if len(e.Payload) > MaxPayloadBytes {
		return nil, fmt.Errorf("%w: payload is %d bytes, limit is %d", ErrTooLarge, len(e.Payload), MaxPayloadBytes)
	}
	return json.Marshal(e)
}

// MarshalJSON keeps the payload field an object (never null) and defaults a
// zero version, so an envelope built by hand still encodes to a valid message.
func (e Envelope) MarshalJSON() ([]byte, error) {
	type alias Envelope
	if e.V == 0 {
		e.V = Version
	}
	if len(bytes.TrimSpace(e.Payload)) == 0 {
		e.Payload = json.RawMessage(`{}`)
	}
	return json.Marshal(alias(e))
}

// New builds a connection-scoped envelope with a fresh message id and the
// payload marshalled.
func New(t MessageType, payload any) (Envelope, error) {
	return newEnvelope(t, "", "", payload)
}

// NewJob builds a job- or session-scoped envelope. sessionID is ignored for
// job-scoped types; pass an empty sessionID for a job without a session.
func NewJob(t MessageType, jobID, sessionID string, payload any) (Envelope, error) {
	if !t.RequiresJobID() {
		return Envelope{}, fmt.Errorf("protocol: %s is not job-scoped", t)
	}
	return newEnvelope(t, jobID, sessionID, payload)
}

func newEnvelope(t MessageType, jobID, sessionID string, payload any) (Envelope, error) {
	body := json.RawMessage(`{}`)
	if payload != nil {
		raw, err := json.Marshal(payload)
		if err != nil {
			return Envelope{}, fmt.Errorf("protocol: marshal %s payload: %w", t, err)
		}
		if len(raw) > MaxPayloadBytes {
			return Envelope{}, fmt.Errorf("%w: %s payload is %d bytes", ErrTooLarge, t, len(raw))
		}
		body = raw
	}
	env := Envelope{V: Version, Type: t, ID: uuid.NewString(), Payload: body}
	if t.RequiresJobID() {
		env.JobID = jobID
	}
	// A session id is carried whenever the caller supplied one, not only for
	// session-scoped types: assign is job-scoped by the catalogue's own table
	// and still names the session it opens (the reference fixture
	// assign.remotion-session.json carries one), and a worker that has to echo
	// sessionId on every op.response needs to have been told it at assign time.
	if sessionID != "" {
		env.SessionID = sessionID
	}
	return env, nil
}

// DecodePayload decodes e.Payload into T. Unknown fields are ignored.
func DecodePayload[T any](e Envelope) (T, error) {
	var v T
	if len(e.Payload) == 0 {
		return v, fmt.Errorf("%w: %s has no payload", ErrMalformed, e.Type)
	}
	if err := json.Unmarshal(e.Payload, &v); err != nil {
		return v, fmt.Errorf("%w: %s payload: %w", ErrMalformed, e.Type, err)
	}
	return v, nil
}

// TypedPayload decodes e.Payload into the payload struct for e.Type, returned as
// a pointer: *HelloPayload, *WelcomePayload and so on. It is the table-driven
// entry point — the fixture contract test uses it to round-trip every message
// type — and errors with ErrUnknownType for a type outside the catalogue.
func (e Envelope) TypedPayload() (any, error) {
	switch e.Type {
	case TypeHello:
		return typed[HelloPayload](e)
	case TypeWelcome:
		return typed[WelcomePayload](e)
	case TypeHeartbeat:
		return typed[HeartbeatPayload](e)
	case TypeReady:
		return typed[ReadyPayload](e)
	case TypeAssign:
		return typed[AssignPayload](e)
	case TypeAccept:
		return typed[AcceptPayload](e)
	case TypeReject:
		return typed[RejectPayload](e)
	case TypeProgress:
		return typed[ProgressPayload](e)
	case TypeOpRequest:
		return typed[OpRequestPayload](e)
	case TypeOpResponse:
		return typed[OpResponsePayload](e)
	case TypeComplete:
		return typed[CompletePayload](e)
	case TypeFail:
		return typed[FailPayload](e)
	case TypeCancel:
		return typed[CancelPayload](e)
	case TypeDrain:
		return typed[DrainPayload](e)
	case TypeBye:
		return typed[ByePayload](e)
	case TypeLLMRequest:
		return typed[LLMRequestPayload](e)
	case TypeLLMResponseStart:
		return typed[LLMResponseStartPayload](e)
	case TypeLLMResponseChunk:
		return typed[LLMResponseChunkPayload](e)
	case TypeLLMResponseEnd:
		return typed[LLMResponseEndPayload](e)
	case TypeLLMError:
		return typed[LLMErrorPayload](e)
	case TypeLLMCancel:
		return typed[LLMCancelPayload](e)
	}
	return nil, fmt.Errorf("%w: %q", ErrUnknownType, e.Type)
}

func typed[T any](e Envelope) (any, error) {
	v, err := DecodePayload[T](e)
	if err != nil {
		return nil, err
	}
	return &v, nil
}
