package agent

import (
	"context"
	"errors"
	"fmt"
	"strconv"
	"strings"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// runOnce dials, handshakes and serves one connection. It returns when the
// connection ends for any reason; Run decides whether to reconnect.
func (a *Agent) runOnce(ctx context.Context) error {
	dialCtx, cancelDial := context.WithTimeout(ctx, a.cfg.DialTimeout)
	tr, err := a.cfg.Dialer.Dial(dialCtx, a.cfg.GatewayURL)
	cancelDial()
	if err != nil {
		return fmt.Errorf("dial %s: %w", RedactForLog(a.cfg.GatewayURL), err)
	}
	if got := tr.Subprotocol(); got != protocol.Subprotocol {
		// The subprotocol is not decoration: a gateway that did not negotiate
		// it is not speaking this protocol, and continuing would be guessing.
		_ = tr.Close(protocol.CloseUnsupportedVersion, "subprotocol "+protocol.Subprotocol+" is required")
		return terminal(fmt.Errorf("agent: gateway negotiated subprotocol %q, want %q", got, protocol.Subprotocol))
	}

	c := &connection{t: tr, writeTimeout: 10 * time.Second, heartbeat: a.cfg.Heartbeat}
	connCtx, cancelConn := context.WithCancel(ctx)
	defer cancelConn()
	defer c.closeNow()

	welcome, err := a.handshake(connCtx, c, cancelConn)
	if err != nil {
		return err
	}

	a.mu.Lock()
	a.conn = c
	a.connected = a.cfg.Now()
	a.mu.Unlock()
	defer func() {
		a.mu.Lock()
		if a.conn == c {
			a.conn = nil
		}
		a.mu.Unlock()
		c.close(protocol.CloseNormal, "connection ended")
		if a.cfg.LLM != nil {
			// Relayed requests rode this socket; their answers have nowhere to go.
			a.cfg.LLM.CancelAll()
		}
		// Held jobs keep running through a reconnect — a gateway deploy must
		// not kill a render — but not indefinitely: the gateway stops renewing
		// their leases the moment the socket goes, so past that window the job
		// is lost whatever the runner does.
		a.watchHeldWhileDisconnected()
	}()

	c.welcome = welcome
	c.heartbeat = heartbeatFromWelcome(welcome, a.cfg.Heartbeat)
	c.deadAfter = deadAfterFromWelcome(welcome)
	c.byeWait = a.cfg.ByeWait
	a.logf("connected as %s (runner %s, kind %s, heartbeat %s)", a.cfg.Identity.Kind(), welcome.RunnerID, welcome.Kind, c.heartbeat)
	if a.checkAgentVersion(welcome) {
		a.setDraining(true)
	}

	// Anything the gateway did not resume is lost: its lease has been, or is
	// about to be, given to another runner. The specification says a cancel
	// follows for each; stopping here is the same decision made sooner, and it
	// keeps a resumed connection's heartbeat truthful.
	a.reconcileHeld(welcome)

	stopHeartbeat := a.startHeartbeat(connCtx, c)
	defer stopHeartbeat()

	if err := a.sendReady(c); err != nil {
		return err
	}
	return a.readLoop(connCtx, c)
}

// handshake sends hello and waits for welcome. It is the only place the device
// key is used.
func (a *Agent) handshake(ctx context.Context, c *connection, cancelConn context.CancelFunc) (protocol.WelcomePayload, error) {
	nonce, err := newNonce(a.cfg.Rand)
	if err != nil {
		return protocol.WelcomePayload{}, err
	}
	now := a.cfg.Now().UTC()
	hello := protocol.HelloPayload{
		ProtocolVersion: protocol.ProtocolVersion,
		Auth:            a.helloAuth(now, nonce),
		Capabilities:    a.helloCapabilities(),
		Running:         a.heldLeases(),
	}
	env, err := protocol.New(protocol.TypeHello, hello)
	if err != nil {
		return protocol.WelcomePayload{}, err
	}
	if err := c.sendEnvelope(env); err != nil {
		return protocol.WelcomePayload{}, fmt.Errorf("send hello: %w", err)
	}

	// The 10-second hello window is the gateway's rule; a runner that waits
	// forever would hang a user's app on an unreachable gateway.
	watchdog := time.AfterFunc(a.cfg.HelloTimeout, func() {
		c.close(protocol.CloseUnsupportedVersion, "no welcome within "+a.cfg.HelloTimeout.String())
		cancelConn()
	})
	defer watchdog.Stop()

	for {
		frame, err := c.t.Read(ctx)
		if err != nil {
			return protocol.WelcomePayload{}, a.readError(ctx, err)
		}
		env, err := protocol.Decode(frame)
		if err != nil {
			if errors.Is(err, protocol.ErrUnknownType) {
				// A receiver MUST ignore an unknown type; before welcome that
				// cannot happen from a conforming gateway, so it is ignored
				// rather than fatal.
				continue
			}
			c.close(protocol.CloseUnsupportedVersion, "malformed message before welcome")
			return protocol.WelcomePayload{}, terminal(fmt.Errorf("agent: malformed frame during handshake: %w", err))
		}
		switch env.Type {
		case protocol.TypeWelcome:
			w, err := protocol.DecodePayload[protocol.WelcomePayload](env)
			if err != nil {
				c.close(protocol.CloseUnsupportedVersion, "malformed welcome")
				return protocol.WelcomePayload{}, terminal(fmt.Errorf("agent: welcome payload: %w", err))
			}
			return w, nil
		case protocol.TypeBye:
			// A refusal's bye names what was wrong before the close frame
			// arrives; the close code is what decides, so keep reading.
			bye, _ := protocol.DecodePayload[protocol.ByePayload](env)
			a.logf("gateway said goodbye: %s", bye.Reason)
		default:
			// "No other message is accepted before welcome."
			c.close(protocol.CloseUnsupportedVersion, "the first gateway message must be welcome")
			return protocol.WelcomePayload{}, terminal(fmt.Errorf("agent: got %s before welcome", env.Type))
		}
	}
}

// helloAuth builds the auth block for the configured identity. The nonce is
// single-use and the signature binds the gateway host, so a hostile gateway
// cannot replay this hello at another one.
func (a *Agent) helloAuth(now time.Time, nonce string) protocol.HelloAuth {
	if a.cfg.Identity.Kind() == protocol.AuthKindPool {
		return protocol.HelloAuth{
			Kind:       protocol.AuthKindPool,
			Credential: a.cfg.Identity.Credential,
			WorkerName: a.cfg.Identity.WorkerName,
		}
	}
	ts := now.Format(time.RFC3339)
	return protocol.HelloAuth{
		Kind:     protocol.AuthKindDevice,
		DeviceID: a.cfg.Identity.DeviceID,
		TS:       ts,
		Nonce:    nonce,
		Sig:      protocol.SignHello(a.cfg.Identity.PrivateKey, a.cfg.Identity.DeviceID, ts, nonce, a.cfg.GatewayHost),
	}
}

// readError turns a transport read failure into the error the loop acts on.
func (a *Agent) readError(ctx context.Context, err error) error {
	if ctx.Err() != nil {
		return ctx.Err()
	}
	if code := closeStatus(err); code != 0 {
		return errorForClose(code, err.Error())
	}
	return fmt.Errorf("agent: connection lost: %w", err)
}

// readLoop dispatches gateway messages until the connection ends.
//
// Each read carries a deadline of the dead window the gateway named in welcome,
// which is the specification's liveness rule seen from this side: a connection
// with no message from either side for that long is dead. Without it a socket
// that was black-holed rather than closed would leave the runner waiting
// forever, holding leases the gateway has already reaped.
func (a *Agent) readLoop(ctx context.Context, c *connection) error {
	for {
		deadline := c.deadAfter
		goodbye := c.goodbyeReason()
		if goodbye != "" {
			// A bye is followed by the close frame that explains it; the code
			// on that frame is what decides whether reconnecting is allowed, so
			// the runner waits briefly for it rather than guessing from the
			// goodbye alone.
			deadline = c.byeWait
		}
		readCtx, cancelRead := context.WithTimeout(ctx, deadline)
		frame, err := c.t.Read(readCtx)
		expired := readCtx.Err() != nil && ctx.Err() == nil
		cancelRead()
		if err != nil {
			if expired {
				if goodbye != "" {
					return c.goodbyeError()
				}
				c.close(protocol.CloseHeartbeatTimeout, "the gateway was silent for "+c.deadAfter.String())
				return fmt.Errorf("%w: the gateway sent nothing for %s", ErrRefused, c.deadAfter)
			}
			return a.readError(ctx, err)
		}
		env, err := protocol.Decode(frame)
		if err != nil {
			if errors.Is(err, protocol.ErrUnknownType) {
				// "A receiver that gets an unknown `type` MUST ignore the
				// message": that is how new message types ship without a new
				// major version.
				continue
			}
			c.close(protocol.CloseUnsupportedVersion, "malformed message")
			return terminal(fmt.Errorf("agent: malformed frame: %w", err))
		}
		a.dispatch(ctx, c, env)
	}
}

// dispatch handles one gateway message.
func (a *Agent) dispatch(ctx context.Context, c *connection, env protocol.Envelope) {
	switch env.Type {
	case protocol.TypeWelcome:
		// Already handled; a second welcome is not a reason to tear down.

	case protocol.TypeHeartbeat:
		a.handleHeartbeat(env)

	case protocol.TypeAssign:
		a.handleAssign(ctx, c, env)

	case protocol.TypeOpRequest:
		a.handleOpRequest(env)

	case protocol.TypeCancel:
		payload, err := protocol.DecodePayload[protocol.CancelPayload](env)
		if err != nil {
			a.logf("cancel payload for job %s is not usable: %v", env.JobID, err)
			return
		}
		a.handleCancel(env.JobID, payload)

	case protocol.TypeLLMRequest:
		a.handleLLMRequest(c, env)

	case protocol.TypeLLMCancel:
		a.handleLLMCancel(env)

	case protocol.TypeDrain:
		payload, _ := protocol.DecodePayload[protocol.DrainPayload](env)
		a.logf("gateway asked the runner to drain: %s", payload.Reason)
		a.setDraining(true)

	case protocol.TypeBye:
		payload, _ := protocol.DecodePayload[protocol.ByePayload](env)
		a.logf("gateway said goodbye: %s", payload.Reason)
		// Recorded, not acted on: the close frame that follows carries the code
		// that decides whether this runner may reconnect at all. readLoop waits
		// for it, for at most byeWait.
		c.setGoodbye(payload.Reason)

	case protocol.TypeReady, protocol.TypeAccept, protocol.TypeReject,
		protocol.TypeProgress, protocol.TypeComplete, protocol.TypeFail,
		protocol.TypeOpResponse, protocol.TypeHello,
		protocol.TypeLLMResponseStart, protocol.TypeLLMResponseChunk, protocol.TypeLLMResponseEnd, protocol.TypeLLMError:
		// Directions that do not exist: the runner sends these. Ignored rather
		// than fatal, since ignoring unknown traffic is the protocol's rule.
	}
}

// handleHeartbeat applies the gateway's lease verdicts. ok:false means the
// lease is lost and the runner MUST stop that job — the gateway may already
// have handed it to someone else, and a resumed result would be fenced out
// anyway.
func (a *Agent) handleHeartbeat(env protocol.Envelope) {
	payload, err := protocol.DecodePayload[protocol.HeartbeatPayload](env)
	if err != nil {
		return
	}
	if payload.ServerTime == "" && payload.Leases == nil {
		// The runner's own shape: nothing to apply.
		return
	}
	for _, lease := range payload.Leases {
		if lease.OK {
			continue
		}
		a.logf("lease for job %s was not renewed; stopping it", lease.JobID)
		a.stopJob(lease.JobID, protocol.CancelLeaseLost)
	}
}

// startHeartbeat runs the lease-renewal ticker. Every heartbeat carries the
// jobs the runner holds with their epochs: that listing is what renews each
// lease, so a heartbeat that fails to send is a job-loss event, not a log line.
//
// It does not tear the connection down itself. A failed write almost always
// means the socket is already gone and the reader is about to report why; a
// writer that cancelled the connection first would replace the gateway's close
// code — which is what decides whether reconnecting is even allowed — with a
// context cancellation. The reader's own deadline covers the case where the
// write succeeds into a black hole.
func (a *Agent) startHeartbeat(ctx context.Context, c *connection) func() {
	done := make(chan struct{})
	go func() {
		ticker := time.NewTicker(c.heartbeat)
		defer ticker.Stop()
		for {
			select {
			case <-done:
				return
			case <-ctx.Done():
				return
			case <-ticker.C:
				payload := protocol.HeartbeatPayload{
					FreeSlots: intPtr(a.freeSlots()),
					Running:   a.heldLeases(),
				}
				if err := c.send(protocol.TypeHeartbeat, payload); err != nil {
					if !errors.Is(err, ErrNotConnected) {
						a.logf("heartbeat failed: %v", err)
					}
					return
				}
			}
		}
	}()
	return func() { close(done) }
}

// heldLeases lists the jobs the runner still holds, for hello.running and for
// every heartbeat.
func (a *Agent) heldLeases() []protocol.JobLease {
	a.mu.Lock()
	defer a.mu.Unlock()
	out := make([]protocol.JobLease, 0, len(a.jobs))
	for _, js := range a.jobs {
		out = append(out, protocol.JobLease{JobID: js.job.JobID, LeaseEpoch: js.job.LeaseEpoch})
	}
	return out
}

// freeSlots is what the gateway assigns against: a runner with no free slot is
// sent no work however idle it looks.
func (a *Agent) freeSlots() int {
	a.mu.Lock()
	held := len(a.jobs)
	a.mu.Unlock()
	free := a.cfg.Capabilities.Slots - held
	if free < 0 {
		return 0
	}
	return free
}

func (a *Agent) sendReady(c *connection) error {
	if a.isDraining() {
		return nil
	}
	return c.send(protocol.TypeReady, protocol.ReadyPayload{FreeSlots: a.freeSlots()})
}

// sendReadyIfConnected announces free slots after a job ends, so the gateway
// does not have to wait for the next heartbeat to use them.
func (a *Agent) sendReadyIfConnected() {
	a.mu.Lock()
	c := a.conn
	draining := a.draining
	a.mu.Unlock()
	if c == nil || draining {
		return
	}
	if err := a.sendReady(c); err != nil && !errors.Is(err, ErrNotConnected) {
		a.logf("ready failed: %v", err)
	}
}

func (a *Agent) isDraining() bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.draining
}

func (a *Agent) setDraining(v bool) {
	a.mu.Lock()
	a.draining = v
	a.mu.Unlock()
}

// checkAgentVersion applies welcome.minAgentVersion. A runner older than the
// minimum must not take jobs, so it stops accepting them and says why; the
// desktop app surfaces the message (E5b), this library only refuses the work.
func (a *Agent) checkAgentVersion(w protocol.WelcomePayload) bool {
	if w.MinAgentVersion == "" {
		return false
	}
	if versionAtLeast(a.cfg.Capabilities.AgentVersion, w.MinAgentVersion) {
		return false
	}
	a.logf("this runner is version %s but the gateway requires at least %s: update before taking jobs",
		a.cfg.Capabilities.AgentVersion, w.MinAgentVersion)
	return true
}

// Outdated reports whether the last welcome said this agent is too old to take
// jobs.
func (a *Agent) Outdated() bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.draining
}

// versionAtLeast compares dotted numeric versions. A version that does not
// parse is treated as older than any that does, which errs towards telling the
// user to update.
func versionAtLeast(have, want string) bool {
	h, ok1 := parseDots(have)
	w, ok2 := parseDots(want)
	if !ok1 || !ok2 {
		return have >= want
	}
	for i := 0; i < len(h) || i < len(w); i++ {
		var hv, wv int
		if i < len(h) {
			hv = h[i]
		}
		if i < len(w) {
			wv = w[i]
		}
		if hv != wv {
			return hv > wv
		}
	}
	return true
}

func parseDots(v string) ([]int, bool) {
	v = strings.TrimSpace(strings.TrimPrefix(v, "v"))
	if v == "" {
		return nil, false
	}
	parts := strings.Split(v, ".")
	out := make([]int, 0, len(parts))
	for _, p := range parts {
		n, err := strconv.Atoi(p)
		if err != nil {
			return nil, false
		}
		out = append(out, n)
	}
	return out, true
}

// deadAfterFromWelcome reads welcome.deadAfterSec, the window after which a
// silent connection is dead. An unusable value falls back to the protocol's.
func deadAfterFromWelcome(w protocol.WelcomePayload) time.Duration {
	if w.DeadAfterSec <= 0 {
		return protocol.DeadAfterSec * time.Second
	}
	return time.Duration(w.DeadAfterSec) * time.Second
}

func intPtr(v int) *int { return &v }
