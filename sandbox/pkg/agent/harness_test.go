package agent

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/coder/websocket"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// ---------------------------------------------------------------------------
// A fake gateway: a Dialer that hands the test one connection per dial, plus
// the two channels that carry frames in each direction. Everything the agent
// does over the wire is driven and observed through it, so no test needs a
// socket, a port or a server.
// ---------------------------------------------------------------------------

type fakeConn struct {
	in     chan []byte
	out    chan []byte
	closed chan struct{}

	mu       sync.Mutex
	code     protocol.CloseCode
	reason   string
	didClose bool
	written  [][]byte

	subprotocol string
}

func newFakeConn() *fakeConn {
	return &fakeConn{
		in:          make(chan []byte, 64),
		out:         make(chan []byte, 256),
		closed:      make(chan struct{}),
		subprotocol: protocol.Subprotocol,
	}
}

func (c *fakeConn) Read(ctx context.Context) ([]byte, error) {
	select {
	case frame := <-c.in:
		return frame, nil
	case <-c.closed:
		c.mu.Lock()
		defer c.mu.Unlock()
		return nil, &websocket.CloseError{Code: websocket.StatusCode(c.code), Reason: c.reason}
	case <-ctx.Done():
		return nil, ctx.Err()
	}
}

func (c *fakeConn) Write(ctx context.Context, frame []byte) error {
	select {
	case <-c.closed:
		return errors.New("fake: connection closed")
	default:
	}
	select {
	case c.out <- frame:
		c.mu.Lock()
		c.written = append(c.written, frame)
		c.mu.Unlock()
		return nil
	case <-c.closed:
		return errors.New("fake: connection closed")
	case <-ctx.Done():
		return ctx.Err()
	}
}

// framesWritten returns every frame the agent has written on this connection.
func (c *fakeConn) framesWritten() [][]byte {
	c.mu.Lock()
	defer c.mu.Unlock()
	return append([][]byte(nil), c.written...)
}

func (c *fakeConn) Subprotocol() string { return c.subprotocol }

func (c *fakeConn) Close(code protocol.CloseCode, reason string) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.didClose {
		return nil
	}
	c.didClose, c.code, c.reason = true, code, reason
	close(c.closed)
	return nil
}

// closedWith reports the close code the agent used, or 0 if it never closed.
func (c *fakeConn) closedWith() protocol.CloseCode {
	c.mu.Lock()
	defer c.mu.Unlock()
	if !c.didClose {
		return 0
	}
	return c.code
}

// gatewayFrame is one frame the agent wrote.
type gatewayFrame struct {
	env protocol.Envelope
	raw []byte
}

type fakeGateway struct {
	dials chan *fakeConn
	mu    sync.Mutex
	count int
	err   error
}

func newFakeGateway() *fakeGateway {
	return &fakeGateway{dials: make(chan *fakeConn, 8)}
}

func (g *fakeGateway) Dial(ctx context.Context, url string) (Transport, error) {
	g.mu.Lock()
	if g.err != nil {
		err := g.err
		g.mu.Unlock()
		return nil, err
	}
	g.count++
	g.mu.Unlock()
	c := newFakeConn()
	g.dials <- c
	return c, nil
}

func (g *fakeGateway) dialsCount() int {
	g.mu.Lock()
	defer g.mu.Unlock()
	return g.count
}

// awaitDial waits for the next connection attempt.
func (g *fakeGateway) awaitDial(t *testing.T) *fakeConn {
	t.Helper()
	select {
	case c := <-g.dials:
		return c
	case <-time.After(3 * time.Second):
		t.Fatal("the agent never dialled")
		return nil
	}
}

func (c *fakeConn) send(t *testing.T, typ protocol.MessageType, payload any) {
	t.Helper()
	env, err := protocol.New(typ, payload)
	if err != nil {
		t.Fatalf("build %s: %v", typ, err)
	}
	raw, err := env.Encode()
	if err != nil {
		t.Fatalf("encode %s: %v", typ, err)
	}
	select {
	case c.in <- raw:
	case <-time.After(3 * time.Second):
		t.Fatalf("sending %s to the agent timed out", typ)
	}
}

func (c *fakeConn) sendJob(t *testing.T, typ protocol.MessageType, jobID, sessionID string, payload any) {
	t.Helper()
	env, err := protocol.NewJob(typ, jobID, sessionID, payload)
	if err != nil {
		t.Fatalf("build %s: %v", typ, err)
	}
	raw, err := env.Encode()
	if err != nil {
		t.Fatalf("encode %s: %v", typ, err)
	}
	select {
	case c.in <- raw:
	case <-time.After(3 * time.Second):
		t.Fatalf("sending %s to the agent timed out", typ)
	}
}

func (c *fakeConn) sendRaw(t *testing.T, raw string) {
	t.Helper()
	select {
	case c.in <- []byte(raw):
	case <-time.After(3 * time.Second):
		t.Fatal("sending a raw frame to the agent timed out")
	}
}

// nextOf reads frames until one of the wanted type arrives.
func (c *fakeConn) nextOf(t *testing.T, want protocol.MessageType) gatewayFrame {
	t.Helper()
	deadline := time.After(3 * time.Second)
	for {
		select {
		case raw := <-c.out:
			env, err := protocol.Decode(raw)
			if err != nil {
				t.Fatalf("the agent wrote a frame that does not decode: %v", err)
			}
			if env.Type == want {
				return gatewayFrame{env: env, raw: raw}
			}
		case <-deadline:
			t.Fatalf("no %s arrived", want)
			return gatewayFrame{}
		}
	}
}

// noFrameOf asserts that nothing of that type arrives within the window.
func (c *fakeConn) noFrameOf(t *testing.T, unwanted protocol.MessageType, window time.Duration) {
	t.Helper()
	timer := time.After(window)
	for {
		select {
		case raw := <-c.out:
			env, err := protocol.Decode(raw)
			if err != nil {
				continue
			}
			if env.Type == unwanted {
				t.Fatalf("unexpected %s: %s", unwanted, raw)
			}
		case <-timer:
			return
		}
	}
}

// handshake completes a welcome so a test can get to the message loop.
func (c *fakeConn) handshake(t *testing.T) protocol.HelloPayload {
	t.Helper()
	frame := c.nextOf(t, protocol.TypeHello)
	hello, err := protocol.DecodePayload[protocol.HelloPayload](frame.env)
	if err != nil {
		t.Fatalf("hello payload: %v", err)
	}
	c.send(t, protocol.TypeWelcome, protocol.WelcomePayload{
		RunnerID:        "runner-1",
		Kind:            protocol.KindDesktop,
		ProtocolVersion: protocol.ProtocolVersion,
		HeartbeatSec:    1,
		DeadAfterSec:    protocol.DeadAfterSec,
		MaxInlineBytes:  protocol.MaxPayloadBytes,
		GatewayInstance: "gw-1",
		ServerTime:      time.Now().UTC().Format(time.RFC3339),
		Resumed:         []protocol.JobLease{},
	})
	return hello
}

// ---------------------------------------------------------------------------
// Test sessions
// ---------------------------------------------------------------------------

type fakeSession struct {
	mu      sync.Mutex
	ops     []string
	closed  int
	done    chan struct{}
	handle  func(ctx context.Context, op string, args json.RawMessage) (any, *protocol.OpError)
	onClose func()
	started chan struct{}
}

func newFakeSession() *fakeSession {
	return &fakeSession{done: make(chan struct{}), started: make(chan struct{})}
}

func (s *fakeSession) Handle(ctx context.Context, op string, args json.RawMessage) (any, *protocol.OpError) {
	s.mu.Lock()
	s.ops = append(s.ops, op)
	handler := s.handle
	s.mu.Unlock()
	if handler != nil {
		return handler(ctx, op, args)
	}
	return map[string]any{"op": op}, nil
}

func (s *fakeSession) Close(ctx context.Context) {
	s.mu.Lock()
	s.closed++
	s.mu.Unlock()
	if s.onClose != nil {
		s.onClose()
	}
	select {
	case <-s.done:
	default:
		close(s.done)
	}
}

func (s *fakeSession) opList() []string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]string(nil), s.ops...)
}

func (s *fakeSession) closeCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.closed
}

// fakeFactory hands out sessions and records the jobs it was asked to build.
type fakeFactory struct {
	mu       sync.Mutex
	sessions []*fakeSession
	jobs     []*Job
	err      error
}

func (f *fakeFactory) New(ctx context.Context, job *Job) (Session, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.jobs = append(f.jobs, job)
	if f.err != nil {
		return nil, f.err
	}
	s := newFakeSession()
	f.sessions = append(f.sessions, s)
	return s, nil
}

func (f *fakeFactory) last() *fakeSession {
	f.mu.Lock()
	defer f.mu.Unlock()
	if len(f.sessions) == 0 {
		return nil
	}
	return f.sessions[len(f.sessions)-1]
}

func (f *fakeFactory) sessionCount() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return len(f.sessions)
}

// ---------------------------------------------------------------------------
// A test agent
// ---------------------------------------------------------------------------

type recordingLogger struct {
	mu  sync.Mutex
	buf bytes.Buffer
}

func (l *recordingLogger) Printf(format string, args ...any) {
	l.mu.Lock()
	defer l.mu.Unlock()
	fmt.Fprintf(&l.buf, format, args...)
	l.buf.WriteString("\n")
}

func (l *recordingLogger) String() string {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.buf.String()
}

type testAgent struct {
	*Agent
	t        *testing.T
	gw       *fakeGateway
	log      *recordingLogger
	factory  *fakeFactory
	cancel   context.CancelFunc
	done     chan error
	key      ed25519.PrivateKey
	consumed atomic.Bool
}

func newDeviceKey(t *testing.T) (string, ed25519.PrivateKey) {
	t.Helper()
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("generate a device key: %v", err)
	}
	return protocol.EncodeDevicePublicKey(pub), priv
}

type testOption func(*Config)

func withCapabilities(c protocol.Capabilities) testOption {
	return func(cfg *Config) { cfg.Capabilities = c }
}
func withFactory(f Factory) testOption { return func(cfg *Config) { cfg.Sessions = f } }
func withSlots(n int) testOption       { return func(cfg *Config) { cfg.Capabilities.Slots = n } }

// testCapabilities is a runner that can do everything, so a test that is not
// about capabilities is not silently about capabilities.
func testCapabilities(container bool) protocol.Capabilities {
	caps := protocol.Capabilities{
		ProtocolVersion: protocol.ProtocolVersion,
		AgentVersion:    "0.1.0-test",
		OS:              "linux",
		Arch:            "amd64",
		Slots:           1,
		Container:       protocol.ContainerNone,
		FfmpegVersion:   "7.1",
		FontsetVersion:  "1",
		JobTypes:        protocol.JobTypesWithoutContainer(),
		MaxOutputBytes:  1 << 30,
	}
	if container {
		digest := "sha256:" + strings.Repeat("a", 64)
		caps.Container = protocol.ContainerDocker
		caps.RuntimeImageDigest = &digest
		caps.JobTypes = protocol.JobTypes()
	}
	return caps
}

func startTestAgent(t *testing.T, opts ...testOption) *testAgent {
	t.Helper()
	keyID, priv := newDeviceKey(t)
	_ = keyID
	factory := &fakeFactory{}
	gw := newFakeGateway()
	logger := &recordingLogger{}
	cfg := Config{
		GatewayURL:   "wss://runner.example.invalid/v1/connect",
		Identity:     Identity{DeviceID: "c3a1f0de-2b4c-4d8e-9a10-6e5f7b8c9d01", PrivateKey: priv},
		Capabilities: testCapabilities(true),
		Sessions:     factory,
		Dialer:       gw,
		Logger:       logger,
		ScratchRoot:  t.TempDir(),
		Backoff:      Backoff{Base: 2 * time.Millisecond, Max: 8 * time.Millisecond, ResetAfter: time.Hour},
		Heartbeat:    50 * time.Millisecond,
		HelloTimeout: 2 * time.Second,
		CancelGrace:  500 * time.Millisecond,
		ByeWait:      200 * time.Millisecond,
	}
	for _, opt := range opts {
		opt(&cfg)
	}
	a, err := New(cfg)
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	ta := &testAgent{Agent: a, t: t, gw: gw, log: logger, factory: factory, cancel: cancel, done: make(chan error, 1), key: priv}
	go func() { ta.done <- a.Run(ctx) }()
	t.Cleanup(func() {
		cancel()
		if ta.consumed.Load() {
			return
		}
		select {
		case <-ta.done:
		case <-time.After(3 * time.Second):
			t.Error("the agent did not stop")
		}
	})
	return ta
}

// runErr waits for Run to return.
func (ta *testAgent) runErr(timeout time.Duration) error {
	select {
	case err := <-ta.done:
		ta.consumed.Store(true)
		return err
	case <-time.After(timeout):
		ta.t.Fatalf("Run did not return; agent log:\n%s", ta.log.String())
		return nil
	}
}

// assign builds an assign payload for a job.
func assignPayload(jobType string, epoch int64, sessionID string) protocol.AssignPayload {
	spec, _ := json.Marshal(protocol.RemotionWorkspaceSpec{
		ExecutionID: "9f8e7d6c-5b4a-4392-8170-a1b2c3d4e5f6",
		Template:    protocol.RemotionTemplate{Name: "remotion-default", Version: "1"},
	})
	if jobType == protocol.JobVideoCompile {
		spec, _ = json.Marshal(protocol.VideoCompileSpec{ExecutionID: "9f8e7d6c-5b4a-4392-8170-a1b2c3d4e5f6", StepID: "2c4e6a80-1b3d-4f57-9a79-8b6c4d2e0f13", Mode: protocol.CompileModeReencode})
	}
	return protocol.AssignPayload{
		JobType:    jobType,
		LeaseEpoch: epoch,
		JobToken:   "TEST.job.token",
		Attempt:    1,
		TimeoutSec: 3600,
		OrgID:      "5e2b1c90-7a3d-4f68-8b21-0c9d4e6f1a22",
		Limits:     protocol.AssignLimits{MaxOutputBytes: 1 << 30, IdleTimeoutSec: 1800},
		Spec:       spec,
	}
}

// waitFor polls until cond holds or the deadline passes.
func waitFor(t *testing.T, what string, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(time.Millisecond)
	}
	t.Fatalf("timed out waiting for %s", what)
}
