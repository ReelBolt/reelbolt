package agent

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"sync"
	"time"

	"github.com/coder/websocket"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// Transport is one established socket. It is an interface so the connection
// loop, the handshake and every reconnect path can be driven by a fake in
// tests without a network.
type Transport interface {
	// Read returns the next text frame.
	Read(ctx context.Context) ([]byte, error)
	// Write sends one text frame.
	Write(ctx context.Context, frame []byte) error
	// Subprotocol reports the negotiated subprotocol, which MUST be
	// protocol.Subprotocol.
	Subprotocol() string
	// Close sends a close frame with the given code.
	Close(code protocol.CloseCode, reason string) error
}

// Dialer opens a Transport. Nil in Config means WebSocketDialer.
type Dialer interface {
	Dial(ctx context.Context, url string) (Transport, error)
}

// WebSocketDialer dials the gateway over a real WebSocket. It is the only place
// in the library that touches the network stack, and it only ever dials out: a
// runner MUST NOT listen on any port.
type WebSocketDialer struct {
	// HTTPClient is passed to the WebSocket handshake. Nil means the default.
	HTTPClient *http.Client
}

// Dial implements Dialer.
func (d *WebSocketDialer) Dial(ctx context.Context, url string) (Transport, error) {
	opts := &websocket.DialOptions{Subprotocols: []string{protocol.Subprotocol}}
	if d.HTTPClient != nil {
		opts.HTTPClient = d.HTTPClient
	}
	ws, _, err := websocket.Dial(ctx, url, opts) //nolint:bodyclose // the library consumes and closes the upgrade response body
	if err != nil {
		return nil, err
	}
	// The read limit is the payload limit plus 64 KiB, as the specification
	// requires of both ends.
	ws.SetReadLimit(protocol.ReadLimitBytes)
	return &wsTransport{ws: ws}, nil
}

type wsTransport struct{ ws *websocket.Conn }

func (t *wsTransport) Read(ctx context.Context) ([]byte, error) {
	typ, data, err := t.ws.Read(ctx)
	if err != nil {
		return nil, err
	}
	if typ != websocket.MessageText {
		return nil, errors.New("agent: binary frame received; the protocol is text only")
	}
	return data, nil
}

func (t *wsTransport) Write(ctx context.Context, frame []byte) error {
	return t.ws.Write(ctx, websocket.MessageText, frame)
}

func (t *wsTransport) Subprotocol() string { return t.ws.Subprotocol() }

func (t *wsTransport) Close(code protocol.CloseCode, reason string) error {
	return t.ws.Close(websocket.StatusCode(code), clampReason(reason))
}

// clampReason keeps the close reason inside the 123-byte limit RFC 6455 puts on
// a close frame.
func clampReason(reason string) string {
	const limit = 123
	if len(reason) <= limit {
		return reason
	}
	return reason[:limit]
}

// closeStatus extracts the WebSocket close code from a read error. -1 means the
// error carries no close frame (a transport failure, a cancelled read).
func closeStatus(err error) protocol.CloseCode {
	// Both forms are checked: coder/websocket returns a CloseError by value
	// (wrapped in the reader's own error), and a fake or a wrapper of ours may
	// carry a pointer to one. Missing either would turn every refusal into
	// "connection lost" and make the runner retry against a gateway that has
	// already said no.
	var ptr *websocket.CloseError
	if errors.As(err, &ptr) {
		return protocol.CloseCode(ptr.Code)
	}
	if code := websocket.CloseStatus(err); code != -1 {
		return protocol.CloseCode(code)
	}
	return 0
}

// connection is one handshaken connection: a write mutex (the heartbeat
// goroutine, the read loop and every job goroutine all send) and the limits the
// gateway negotiated.
type connection struct {
	t Transport

	mu      sync.Mutex
	closed  bool
	welcome protocol.WelcomePayload

	writeTimeout time.Duration
	heartbeat    time.Duration
	// deadAfter is welcome.deadAfterSec: how long the connection may be silent
	// in both directions before it is dead.
	deadAfter time.Duration
	// goodbye is the reason of a bye the gateway sent, and byeWait is how long
	// the close frame that follows it is awaited.
	goodbye string
	byeWait time.Duration
}

// send builds a connection-scoped envelope and writes it.
func (c *connection) send(typ protocol.MessageType, payload any) error {
	env, err := protocol.New(typ, payload)
	if err != nil {
		return err
	}
	return c.sendEnvelope(env)
}

func (c *connection) sendEnvelope(env protocol.Envelope) error {
	frame, err := env.Encode()
	if err != nil {
		return err
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.closed {
		return ErrNotConnected
	}
	ctx, cancel := context.WithTimeout(context.Background(), c.writeTimeout)
	defer cancel()
	return c.t.Write(ctx, frame)
}

// sendJob builds a job- or session-scoped envelope and writes it.
func (c *connection) sendJob(typ protocol.MessageType, jobID, sessionID string, payload any) error {
	env, err := protocol.NewJob(typ, jobID, sessionID, payload)
	if err != nil {
		return err
	}
	return c.sendEnvelope(env)
}

func (c *connection) close(code protocol.CloseCode, reason string) {
	c.mu.Lock()
	if c.closed {
		c.mu.Unlock()
		return
	}
	c.closed = true
	c.mu.Unlock()
	_ = c.t.Close(code, reason)
}

// closeNow drops the socket without a close handshake. Used on the deferred
// path of a connection that has already ended.
func (c *connection) closeNow() {
	c.mu.Lock()
	already := c.closed
	c.closed = true
	c.mu.Unlock()
	if !already {
		_ = c.t.Close(protocol.CloseNormal, "connection ended")
	}
}

func (c *connection) setGoodbye(reason string) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.goodbye == "" {
		c.goodbye = reason
	}
}

func (c *connection) goodbyeReason() string {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.goodbye
}

// goodbyeError turns a goodbye the gateway never followed with a close frame
// into the error the reconnect loop acts on. "revoked" is final — the device
// may not be here any more — and "shutdown" is the gateway going away, which is
// exactly what reconnecting is for.
func (c *connection) goodbyeError() error {
	reason := c.goodbyeReason()
	c.close(protocol.CloseNormal, "gateway said goodbye: "+reason)
	switch reason {
	case protocol.CancelRevoked:
		return terminal(fmt.Errorf("%w: the gateway revoked this device (bye: %s)", ErrUnauthenticated, reason))
	default:
		return fmt.Errorf("%w: the gateway said goodbye (%s) and closed without a code", ErrRefused, reason)
	}
}

// ErrNotConnected is returned when a message is built for a connection that has
// already gone. Callers treat it as "this message did not reach the gateway"
// and never as a job failure: a reconnecting runner is expected to drop
// messages, which is exactly what resume and the op cache exist for.
var ErrNotConnected = errors.New("agent: not connected")

// heartbeatFromWelcome picks the heartbeat interval from welcome, falling back
// to the configured one when the gateway sent something unusable.
func heartbeatFromWelcome(w protocol.WelcomePayload, fallback time.Duration) time.Duration {
	if w.HeartbeatSec <= 0 {
		return fallback
	}
	d := time.Duration(w.HeartbeatSec) * time.Second
	if d > time.Minute {
		d = time.Minute
	}
	return d
}

var _ = fmt.Sprintf
