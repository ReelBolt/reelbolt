package main

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"errors"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// These tests put the two halves of the protocol together: the worker library
// (D4b) dials the real gateway (D5a) over a real WebSocket, and the two ends
// have to agree on the handshake, on liveness and on every refusal.
//
// The job path is not here. D5a's gateway is a skeleton with no job queue —
// D5b owns assigning, leasing and fencing — so a runner cannot be sent work
// through it yet. Everything a runner and a gateway can do today is exercised
// end to end here, and the job path is covered against a scripted gateway in
// pkg/agent.

// recordingLogger captures what the agent writes, so a test can assert on the
// runner's side of the conversation.
type recordingLogger struct {
	mu  sync.Mutex
	buf strings.Builder
}

func (l *recordingLogger) Printf(format string, args ...any) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.buf.WriteString(format)
	l.buf.WriteString(" ")
	for _, a := range args {
		l.buf.WriteString(strings.TrimSpace(describe(a)))
		l.buf.WriteString(" ")
	}
	l.buf.WriteString(newline)
}

func (l *recordingLogger) String() string {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.buf.String()
}

const newline = string(rune(10))

func describe(v any) string {
	switch t := v.(type) {
	case string:
		return t
	case error:
		return t.Error()
	case time.Duration:
		return t.String()
	case bool:
		if t {
			return "true"
		}
		return "false"
	default:
		return ""
	}
}

// noSessions is a factory that is never asked for a session: nothing assigns
// work through the skeleton gateway.
type noSessions struct{ mu sync.Mutex }

func (f *noSessions) New(ctx context.Context, job *agent.Job) (agent.Session, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return nil, errors.New("no job was expected")
}

func testCapabilities() protocol.Capabilities {
	digest := "sha256:" + strings.Repeat("a", 64)
	return protocol.Capabilities{
		ProtocolVersion:    protocol.ProtocolVersion,
		AgentVersion:       "0.1.0-test",
		OS:                 "linux",
		Arch:               "amd64",
		Slots:              2,
		Container:          protocol.ContainerDocker,
		ContainerMode:      "rootless",
		RuntimeImageDigest: &digest,
		FfmpegVersion:      "7.1",
		FontsetVersion:     "1",
		JobTypes:           protocol.JobTypes(),
		MaxOutputBytes:     1 << 30,
	}
}

// pairedDevice registers a device with a real Ed25519 public key, exactly as
// the pairing flow would.
func pairedDevice(t *testing.T, tg *testGateway) (string, ed25519.PrivateKey) {
	t.Helper()
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("generate a device key: %v", err)
	}
	id := "c3a1f0de-2b4c-4d8e-9a10-6e5f7b8c9d01"
	tg.store.add(Device{
		ID:        id,
		Kind:      protocol.KindDesktop,
		Name:      "a test laptop",
		PublicKey: protocol.EncodeDevicePublicKey(pub),
	})
	tg.store.setOrganization(id, "5e2b1c90-7a3d-4f68-8b21-0c9d4e6f1a22")
	return id, priv
}

// runnerHandle carries a Run result once, so a test that has already read it
// and the cleanup that runs afterwards do not race for it.
type runnerHandle struct {
	mu      sync.Mutex
	settled chan struct{}
	err     error
	done    bool
}

func (h *runnerHandle) settle(err error) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.done {
		return
	}
	h.err, h.done = err, true
	close(h.settled)
}

// wait reports Run's return value and whether it returned in time.
func (h *runnerHandle) wait(timeout time.Duration) (error, bool) {
	select {
	case <-h.settled:
		h.mu.Lock()
		defer h.mu.Unlock()
		return h.err, true
	case <-time.After(timeout):
		return nil, false
	}
}

// settledNow reports whether Run has already returned.
func (h *runnerHandle) settledNow() bool {
	select {
	case <-h.settled:
		return true
	default:
		return false
	}
}

func startRunner(t *testing.T, tg *testGateway, deviceID string, priv ed25519.PrivateKey) (*agent.Agent, *recordingLogger, *runnerHandle) {
	t.Helper()
	logger := &recordingLogger{}
	a, err := agent.New(agent.Config{
		GatewayURL:   tg.wsURL(),
		Identity:     agent.Identity{DeviceID: deviceID, PrivateKey: priv},
		Capabilities: testCapabilities(),
		Sessions:     &noSessions{},
		Logger:       logger,
		ScratchRoot:  t.TempDir(),
		Heartbeat:    100 * time.Millisecond,
		HelloTimeout: 3 * time.Second,
		Backoff:      agent.Backoff{Base: 5 * time.Millisecond, Max: 20 * time.Millisecond, ResetAfter: time.Hour},
	})
	if err != nil {
		t.Fatalf("agent.New: %v", err)
	}
	handle := &runnerHandle{settled: make(chan struct{})}
	ctx, cancel := context.WithCancel(context.Background())
	go func() { handle.settle(a.Run(ctx)) }()
	t.Cleanup(func() {
		cancel()
		if _, ok := handle.wait(5 * time.Second); !ok {
			t.Error("the runner did not stop")
		}
	})
	return a, logger, handle
}

func waitForCondition(t *testing.T, what string, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(5 * time.Millisecond)
	}
	t.Fatalf("timed out waiting for %s", what)
}

// A runner connects, is authenticated by its device key, advertises what it can
// do, and the gateway records it.
func TestRunnerHandshakesWithTheGateway(t *testing.T) {
	tg := newTestGateway(t, nil)
	deviceID, priv := pairedDevice(t, tg)
	_, logger, handle := startRunner(t, tg, deviceID, priv)

	waitForCondition(t, "the gateway to see the runner", func() bool {
		return len(tg.gateway.registry.snapshot()) == 1
	})
	var caps protocol.Capabilities
	waitForCondition(t, "the gateway to store the runner's capabilities", func() bool {
		c, ok := tg.store.lastCapabilities()
		if !ok {
			return false
		}
		caps = c
		return true
	})
	if !caps.Supports(protocol.JobRemotionWorkspace) || caps.Slots != 2 || caps.Container != protocol.ContainerDocker {
		t.Fatalf("the stored capabilities are wrong: %+v", caps)
	}
	if tg.store.touchCount(deviceID) == 0 {
		t.Fatal("the gateway never recorded last_seen_at for the device")
	}

	// The runner announced what it can take.
	waitForCondition(t, "the runner's free slots to reach the registry", func() bool {
		for _, c := range tg.gateway.registry.snapshot() {
			if c.snapshot().FreeSlots == 2 {
				return true
			}
		}
		return false
	})

	// And it is still connected after several heartbeats: the renewal is
	// answered, not merely tolerated.
	time.Sleep(400 * time.Millisecond)
	if handle.settledNow() {
		err, _ := handle.wait(0)
		t.Fatalf("the connection did not survive its heartbeats: %v%s%s", err, newline, logger.String())
	}
}

// The gateway's hello rules are seen from the runner's side: a device the
// registry does not know is refused, and the runner treats that as final rather
// than hammering the gateway.
func TestUnknownDeviceIsRefusedFinally(t *testing.T) {
	tg := newTestGateway(t, nil)
	_, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("generate a key: %v", err)
	}
	_, logger, handle := startRunner(t, tg, "9b2f7c1e-0000-4000-8000-000000000000", priv)

	runErr, ok := handle.wait(5 * time.Second)
	if !ok {
		t.Fatalf("the runner kept retrying against a refusal it cannot fix: %s", logger.String())
	}
	if !errors.Is(runErr, agent.ErrUnauthenticated) {
		t.Fatalf("Run returned %v, want ErrUnauthenticated", runErr)
	}
}

// Revoking a device reaches the socket: the gateway says goodbye and closes
// with 4401, and the runner stops.
func TestRevokedDeviceEndsTheConnection(t *testing.T) {
	tg := newTestGateway(t, func(cfg *appConfig) {
		cfg.RevocationInterval = 50 * time.Millisecond
	})
	deviceID, priv := pairedDevice(t, tg)
	_, logger, handle := startRunner(t, tg, deviceID, priv)
	waitForCondition(t, "the handshake", func() bool { return len(tg.gateway.registry.snapshot()) == 1 })

	tg.store.revoke(deviceID)
	runErr, ok := handle.wait(5 * time.Second)
	if !ok {
		t.Fatalf("the runner kept the connection after its device was revoked: %s", logger.String())
	}
	if !errors.Is(runErr, agent.ErrUnauthenticated) {
		t.Fatalf("Run returned %v, want ErrUnauthenticated", runErr)
	}
}

// A second connection from the same device wins, and the displaced runner stops
// instead of fighting for the socket.
func TestSecondConnectionSupersedesTheFirst(t *testing.T) {
	tg := newTestGateway(t, nil)
	deviceID, priv := pairedDevice(t, tg)
	_, firstLog, first := startRunner(t, tg, deviceID, priv)
	waitForCondition(t, "the first handshake", func() bool { return len(tg.gateway.registry.snapshot()) == 1 })

	_, _, second := startRunner(t, tg, deviceID, priv)
	runErr, ok := first.wait(5 * time.Second)
	if !ok {
		t.Fatalf("the displaced runner kept its connection: %s", firstLog.String())
	}
	if !errors.Is(runErr, agent.ErrSuperseded) {
		t.Fatalf("the displaced runner returned %v, want ErrSuperseded", runErr)
	}
	// The newer connection owns the socket.
	waitForCondition(t, "one live connection", func() bool { return len(tg.gateway.registry.snapshot()) == 1 })
	waitForCondition(t, "the newer connection to be the live one", func() bool {
		for _, c := range tg.gateway.registry.snapshot() {
			if c.snapshot().FreeSlots == 2 {
				return true
			}
		}
		return false
	})
	if second.settledNow() {
		err, _ := second.wait(0)
		t.Fatalf("the newer runner was displaced instead: %v", err)
	}
}

// A gateway shutting down sends bye{shutdown} and closes with 1001. That is not
// a refusal, so the runner comes back.
func TestGatewayShutdownIsRetried(t *testing.T) {
	tg := newTestGateway(t, nil)
	deviceID, priv := pairedDevice(t, tg)
	_, logger, handle := startRunner(t, tg, deviceID, priv)
	waitForCondition(t, "the handshake", func() bool { return len(tg.gateway.registry.snapshot()) == 1 })

	tg.gateway.shutdown()
	waitForCondition(t, "the runner to see the goodbye", func() bool {
		return strings.Contains(logger.String(), "gateway said goodbye")
	})
	if err, settled := handle.wait(300 * time.Millisecond); settled {
		t.Fatalf("a shutdown was treated as final: %v", err)
	}
	if !strings.Contains(logger.String(), "reconnecting") {
		t.Fatalf("the runner never tried to reconnect: %s", logger.String())
	}
}
