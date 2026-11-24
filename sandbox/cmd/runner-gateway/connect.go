package main

import (
	"context"
	"errors"
	"log"
	"net/http"
	"sync/atomic"
	"time"

	"github.com/coder/websocket"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// handleConnect is the endpoint a runner dials: wss://runner.<domain>/v1/connect.
//
// One goroutine per connection is the whole concurrency model, which is what
// makes thousands of mostly idle desktop sockets cheap. The sequence is the one
// the specification fixes: upgrade, hello within 10 s, welcome or a refusal,
// then the message loop.
func (g *gateway) handleConnect(w http.ResponseWriter, r *http.Request) {
	ws, err := websocket.Accept(w, r, &websocket.AcceptOptions{Subprotocols: []string{protocol.Subprotocol}})
	if err != nil {
		log.Printf("websocket upgrade from %s failed: %v", r.RemoteAddr, err)
		return
	}
	defer func() { _ = ws.CloseNow() }()
	ws.SetReadLimit(protocol.ReadLimitBytes)

	remote := remoteAddr(r)
	if got := ws.Subprotocol(); got != protocol.Subprotocol {
		g.metrics.handshakes.WithLabelValues("bad_subprotocol").Inc()
		g.metrics.countClose(int(protocol.CloseUnsupportedVersion))
		log.Printf("refused connection from %s: subprotocol %q, want %q", remote, got, protocol.Subprotocol)
		_ = ws.Close(websocket.StatusCode(protocol.CloseUnsupportedVersion),
			"subprotocol "+protocol.Subprotocol+" is required")
		return
	}

	// r.Context() lives as long as this handler, which is the connection.
	ctx := r.Context()
	if g.draining.Load() {
		g.refuseConnection(ws, remote, refuse(protocol.CloseGoingAway, "draining", "gateway is draining for shutdown"))
		return
	}

	helloEnv, refusal, closed := g.readHello(ctx, ws, remote)
	if closed {
		// The hello watchdog already closed the connection with its code.
		return
	}
	if refusal != nil {
		g.refuseConnection(ws, remote, refusal)
		return
	}

	c, refusal := g.handshake(ctx, helloEnv, ws, g.gatewayHosts(r), remote, g.now().UTC())
	if refusal != nil {
		g.refuseConnection(ws, remote, refusal)
		return
	}

	if previous := g.registry.add(c); previous != nil {
		log.Printf("%s: superseded by a newer connection from the same runner", previous.logTag)
		g.metrics.countClose(int(protocol.CloseSuperseded))
		// Closed from another goroutine: coder/websocket's Close waits up to five
		// seconds for the peer's close frame (and for its reader to unwind), and
		// this connection must not wait on the one it just replaced — a runner
		// whose socket is half-dead would otherwise delay every reconnect by five
		// seconds.
		go previous.closeWith(protocol.CloseSuperseded, "superseded by a newer connection")
	}
	g.updateConnectedGauges()
	defer func() {
		g.registry.remove(c)
		// Requests relayed through this socket cannot be answered any more.
		g.llm.failConn(c)
		g.updateConnectedGauges()
	}()

	g.metrics.handshakes.WithLabelValues("ok").Inc()
	g.metrics.countMessage(string(protocol.TypeHello), true)
	log.Printf("%s: connected kind=%s org=%s slots=%d container=%s agent=%s",
		c.logTag, c.kind, orNone(c.orgID), c.caps.Slots, c.caps.Container, c.caps.AgentVersion)

	g.touch(ctx, c)
	hello, _ := protocol.DecodePayload[protocol.HelloPayload](helloEnv)
	if err := g.sendWelcome(ctx, c, hello); err != nil {
		log.Printf("%s: sending welcome failed: %v", c.logTag, err)
		return
	}
	g.serve(ctx, c)
}

// readHello reads the connection's first message, which must be hello.
//
// The 10-second limit is enforced by a watchdog rather than by the read's own
// deadline: a reader that times out has already failed the connection, so the
// peer would see an abrupt EOF instead of the 4400 the specification requires.
// closed reports that the watchdog fired and the connection is already gone.
func (g *gateway) readHello(ctx context.Context, ws *websocket.Conn, remote string) (protocol.Envelope, *handshakeError, bool) {
	var timedOut atomic.Bool
	stop := watchdog(g.cfg.HelloTimeout, func() {
		timedOut.Store(true)
		g.metrics.handshakes.WithLabelValues("hello_timeout").Inc()
		g.metrics.handshakeTimeouts.Inc()
		g.metrics.countClose(int(protocol.CloseUnsupportedVersion))
		log.Printf("no hello from %s within %s", remote, g.cfg.HelloTimeout)
		_ = ws.Close(websocket.StatusCode(protocol.CloseUnsupportedVersion), "no hello within "+g.cfg.HelloTimeout.String())
	})
	// A longer read deadline is only a backstop, so the watchdog stays the
	// deadline that is actually observed.
	readCtx, cancel := context.WithTimeout(ctx, 3*g.cfg.HelloTimeout)
	typ, data, err := ws.Read(readCtx)
	cancel()
	stop()
	if timedOut.Load() {
		return protocol.Envelope{}, nil, true
	}
	if err != nil {
		if ctx.Err() != nil {
			return protocol.Envelope{}, refuse(protocol.CloseGoingAway, "draining", "gateway is shutting down"), false
		}
		log.Printf("connection from %s lost before hello: %v", remote, err)
		return protocol.Envelope{}, refuse(protocol.CloseNormal, "disconnected", "connection closed before hello"), false
	}
	if typ != websocket.MessageText {
		return protocol.Envelope{}, refuse(protocol.CloseUnsupportedVersion, "binary_frame",
			"binary frames are not part of the protocol"), false
	}
	env, err := protocol.Decode(data)
	if err != nil {
		if errors.Is(err, protocol.ErrUnknownType) {
			return protocol.Envelope{}, refuse(protocol.CloseUnsupportedVersion, "malformed_hello",
				"the first message must be hello, got %q", env.Type), false
		}
		if errors.Is(err, protocol.ErrTooLarge) {
			return protocol.Envelope{}, refuse(protocol.CloseMessageTooLarge, "too_large", "%v", err), false
		}
		return protocol.Envelope{}, refuse(protocol.CloseUnsupportedVersion, "malformed_hello", "%v", err), false
	}
	if env.Type != protocol.TypeHello {
		return protocol.Envelope{}, refuse(protocol.CloseUnsupportedVersion, "malformed_hello",
			"the first message must be hello, got %q", env.Type), false
	}
	if env.V != protocol.Version || env.ID == "" {
		return protocol.Envelope{}, refuse(protocol.CloseUnsupportedVersion, "malformed_hello",
			"hello must carry the version and an id"), false
	}
	return env, nil, false
}

// sendWelcome answers the hello and lists the jobs the gateway still holds for
// the runner.
//
// D5b resumes: a job whose lease has not expired and whose epoch matches is kept
// and listed in welcome.resumed, and every claimed job the gateway does not
// resume is answered with cancel{reason:"lease_lost"} so the runner stops it.
// D5a deliberately answered every claimed job with a cancel because it held no
// job state; this is the rule the specification states.
func (g *gateway) sendWelcome(ctx context.Context, c *conn, hello protocol.HelloPayload) error {
	var orgID *string
	if c.orgID != "" {
		org := c.orgID
		orgID = &org
	}
	resumed, notResumed := g.resumeWork(ctx, c, hello)
	payload := protocol.WelcomePayload{
		RunnerID:        c.runnerID,
		Kind:            c.kind,
		OrgID:           orgID,
		ProtocolVersion: c.protocolVersion,
		HeartbeatSec:    g.cfg.HeartbeatSec,
		DeadAfterSec:    g.cfg.DeadAfterSec,
		MaxInlineBytes:  protocol.MaxPayloadBytes,
		GatewayInstance: g.cfg.GatewayInstance,
		ServerTime:      g.now().UTC().Format(time.RFC3339),
		Resumed:         resumed,
		MinAgentVersion: g.cfg.MinAgentVersion,
	}
	if err := c.sendPayload(ctx, protocol.TypeWelcome, payload); err != nil {
		return err
	}
	g.metrics.countMessage(string(protocol.TypeWelcome), false)

	// Only now, once welcome has said which jobs survive, are the rest told to
	// stop.
	for _, jobID := range notResumed {
		env, err := protocol.NewJob(protocol.TypeCancel, jobID, "",
			protocol.CancelPayload{Reason: protocol.CancelLeaseLost, GraceSec: int(g.cfg.cancelGrace() / time.Second)})
		if err != nil {
			return err
		}
		if err := c.send(ctx, env); err != nil {
			return err
		}
		g.metrics.countMessage(string(protocol.TypeCancel), false)
	}
	// And a resumed job whose last op never came back is sent that op again, so
	// the engine call that is still waiting can be answered. The worker's op cache
	// is what makes the repeat safe ("Idempotency": a repeated opId returns the
	// cached op.response rather than executing the op twice).
	g.resendPendingOps(ctx, c, resumed)
	return nil
}

// serve is the connection's message loop.
//
// The read deadline is the liveness rule: a connection silent for
// deadAfterSec (45 s by default) is dead, and any message — not just
// heartbeat — pushes it back. No ticker is needed to enforce it.
func (g *gateway) serve(ctx context.Context, c *conn) {
	stop := make(chan struct{})
	defer close(stop)
	go g.watchLiveness(ctx, c, stop)

	// The read deadline is a backstop only — watchLiveness is what closes the
	// connection, with a code the runner can act on.
	backstop := 4 * g.cfg.deadAfter()
	for {
		readCtx, cancel := context.WithTimeout(ctx, backstop)
		typ, data, err := c.ws.Read(readCtx)
		cancel()
		if err != nil {
			g.reportReadError(ctx, c, err)
			return
		}
		c.markSeen(g.now().UTC())
		if typ != websocket.MessageText {
			log.Printf("%s: binary frame received, closing", c.logTag)
			g.metrics.countClose(int(protocol.CloseUnsupportedVersion))
			c.closeWith(protocol.CloseUnsupportedVersion, "binary frames are not part of the protocol")
			return
		}
		env, err := protocol.Decode(data)
		if errors.Is(err, protocol.ErrUnknownType) {
			// "A receiver that gets an unknown type MUST ignore the message."
			g.metrics.unexpected.WithLabelValues(string(env.Type)).Inc()
			continue
		}
		if err != nil {
			log.Printf("%s: malformed message: %v", c.logTag, err)
			g.metrics.countClose(int(protocol.CloseUnsupportedVersion))
			c.closeWith(protocol.CloseUnsupportedVersion, "malformed message")
			return
		}
		g.metrics.countMessage(string(env.Type), true)
		if stop := g.dispatch(ctx, c, env); stop {
			return
		}
	}
}

// dispatch handles one message. It returns true when the connection should be
// torn down.
func (g *gateway) dispatch(ctx context.Context, c *conn, env protocol.Envelope) bool {
	switch env.Type {
	case protocol.TypeHeartbeat:
		hb, err := protocol.DecodePayload[protocol.HeartbeatPayload](env)
		if err != nil {
			log.Printf("%s: heartbeat payload: %v", c.logTag, err)
			return false
		}
		if hb.FreeSlots != nil {
			c.setFreeSlots(*hb.FreeSlots)
		}
		c.setRunning(hb.Running)
		// The heartbeat's running list is authoritative for the slots the runner
		// holds, so the jobs accepted since the previous one are no longer
		// "pending in the runner's own count".
		c.clearSinceHeartbeat()
		g.touch(ctx, c)
		// "While running, every heartbeat from the runner lists its held jobs with
		// their epochs; each valid listing renews that job's lease to now plus 45
		// seconds. The gateway's heartbeat answer reports any lease it no longer
		// honours with ok:false."
		if err := c.sendPayload(ctx, protocol.TypeHeartbeat, protocol.HeartbeatPayload{
			ServerTime: g.now().UTC().Format(time.RFC3339),
			Leases:     g.renewLeases(ctx, c, hb.Running),
		}); err != nil {
			log.Printf("%s: heartbeat answer failed: %v", c.logTag, err)
			return true
		}
		g.metrics.countMessage(string(protocol.TypeHeartbeat), false)
		g.updateConnectedGauges()
		g.assignWork(ctx, c)

	case protocol.TypeReady:
		ready, err := protocol.DecodePayload[protocol.ReadyPayload](env)
		if err != nil {
			log.Printf("%s: ready payload: %v", c.logTag, err)
			return false
		}
		c.setFreeSlots(ready.FreeSlots)
		// "A runner sends ready{freeSlots}. The gateway claims an eligible job
		// transactionally ... and sends assign."
		g.assignWork(ctx, c)

	case protocol.TypeDrain:
		c.markDraining()

	case protocol.TypeBye:
		log.Printf("%s: runner said goodbye", c.logTag)
		c.closeWith(protocol.CloseNormal, "runner said goodbye")
		return true

	case protocol.TypeHello, protocol.TypeWelcome:
		// Out of order: hello already happened and welcome is the gateway's own
		// message. Ignore it rather than tear the connection down.

	case protocol.TypeLLMResponseStart, protocol.TypeLLMResponseChunk, protocol.TypeLLMResponseEnd, protocol.TypeLLMError:
		g.handleLLMMessage(c, env)

	case protocol.TypeLLMRequest, protocol.TypeLLMCancel:
		g.metrics.unexpected.WithLabelValues(string(env.Type)).Inc()

	case protocol.TypeAssign, protocol.TypeCancel, protocol.TypeOpRequest:
		// Runner to gateway directions that do not exist: the gateway sends these.
		g.metrics.unexpected.WithLabelValues(string(env.Type)).Inc()

	// Job-scoped runner messages. Every one of them is fenced: a stale epoch or
	// an invalid token is rejected and the runner is told to stop the job.
	case protocol.TypeAccept:
		g.handleAccept(ctx, c, env)

	case protocol.TypeReject:
		g.handleReject(ctx, c, env)

	case protocol.TypeProgress:
		g.handleProgress(ctx, c, env)

	case protocol.TypeOpResponse:
		g.handleOpResponse(ctx, c, env)

	case protocol.TypeComplete:
		g.handleComplete(ctx, c, env)

	case protocol.TypeFail:
		g.handleFail(ctx, c, env)

	default:
		g.metrics.unexpected.WithLabelValues(string(env.Type)).Inc()
	}
	return false
}

// reportReadError logs how a connection ended. It does not close anything: the
// liveness watchdog, the sweeper and the shutdown path each close with their own
// code, and an already-failed reader cannot deliver one.
func (g *gateway) reportReadError(ctx context.Context, c *conn, err error) {
	if ctx.Err() != nil {
		// Shutdown or a deliberate close from the sweeper: that path sets its own
		// code and reason.
		return
	}
	switch code := websocket.CloseStatus(err); code {
	case websocket.StatusNormalClosure, websocket.StatusGoingAway:
		log.Printf("%s: peer closed the connection (%d)", c.logTag, code)
	case -1:
		log.Printf("%s: read failed: %v", c.logTag, err)
	default:
		log.Printf("%s: connection lost (%d): %v", c.logTag, code, err)
	}
}

func orNone(v string) string {
	if v == "" {
		return "-"
	}
	return v
}
