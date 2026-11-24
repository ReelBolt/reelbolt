package daemon

import (
	"context"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/governor"
)

// The gateway's remote control of a runner rides the one message the protocol
// already has for it: drain, which is scoped to the connection and is defined
// for BOTH directions. Its reason is an open string, so the reason is what
// carries the meaning.
//
// Nothing on the gateway side sends these yet — a dashboard pause button is a
// later WP — and no protocol change is needed for it to: drain is already in the
// published contract, the worker agent already stops accepting assignments when
// it receives one, and reject.paused_by_user is already one of the protocol's
// own reject reasons. What was missing was a runner that could tell a pause
// apart from the gateway going away, which is what this file is.
const (
	// ControlPauseReason asks this runner to stop taking work.
	ControlPauseReason = protocol.RejectPausedByUser
	// ControlResumeReason releases a pause. It is the runner's own value: the
	// protocol names no resume reason because until this WP nothing could
	// resume.
	ControlResumeReason = "resumed_by_user"
)

// controlDialer wraps the WebSocket dialer and watches the frames the worker
// agent reads for the gateway's control messages.
//
// It is a decorator rather than a rewrite: the agent's Transport interface is a
// public injection point (Config.Dialer), the bytes are returned unmodified,
// and a frame that is not a drain is not even parsed twice — protocol.Decode is
// what the library would have done with it anyway. The reason it is needed at
// all is that the library surfaces the fact that it is draining (Outdated)
// without surfacing WHY, and a runner that treated every drain as a pause would
// pause itself for good the first time the gateway deployed.
type controlDialer struct {
	inner   agent.Dialer
	onDrain func(reason string)
}

// Dial implements agent.Dialer.
func (d *controlDialer) Dial(ctx context.Context, url string) (agent.Transport, error) {
	t, err := d.inner.Dial(ctx, url)
	if err != nil {
		return nil, err
	}
	return &controlTransport{inner: t, onDrain: d.onDrain}, nil
}

// controlTransport passes frames through and reports a drain's reason.
type controlTransport struct {
	inner   agent.Transport
	onDrain func(reason string)
}

// Read implements agent.Transport.
func (t *controlTransport) Read(ctx context.Context) ([]byte, error) {
	frame, err := t.inner.Read(ctx)
	if err == nil && len(frame) > 0 && t.onDrain != nil {
		t.inspect(frame)
	}
	return frame, err
}

// Write implements agent.Transport.
func (t *controlTransport) Write(ctx context.Context, frame []byte) error {
	return t.inner.Write(ctx, frame)
}

// Subprotocol implements agent.Transport.
func (t *controlTransport) Subprotocol() string { return t.inner.Subprotocol() }

// Close implements agent.Transport.
func (t *controlTransport) Close(code protocol.CloseCode, reason string) error {
	return t.inner.Close(code, reason)
}

// inspect reports the reason of a drain frame. Anything else — including a
// frame this build cannot parse — is left to the library, which has its own
// rules for malformed and unknown messages.
func (t *controlTransport) inspect(frame []byte) {
	env, err := protocol.Decode(frame)
	if err != nil || env.Type != protocol.TypeDrain {
		return
	}
	payload, err := protocol.DecodePayload[protocol.DrainPayload](env)
	if err != nil {
		return
	}
	t.onDrain(payload.Reason)
}

// onGatewayControl applies a control message the gateway sent.
//
// It runs on the agent's read loop, so it never blocks: Pause and Resume are
// cheap, and the reconnect signal is a non-blocking send to a buffered channel.
func (d *Daemon) onGatewayControl(reason string) {
	switch reason {
	case ControlPauseReason:
		d.logf("the gateway paused this runner")
		d.gov.Pause(governor.SourceRemote, "paused from the ReelBolt dashboard")
	case ControlResumeReason:
		d.logf("the gateway resumed this runner")
		d.gov.Resume()
		// The worker agent cannot clear its own draining state, so a runner
		// that was drained has to reconnect to take work again. The signal is
		// what makes serve() rebuild it.
		select {
		case d.reconnect <- struct{}{}:
		default:
		}
	}
}
