package main

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/coder/websocket"
	"github.com/gorilla/mux"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// ── a device registry that is not Postgres ───────────────────────────────────

type fakeStore struct {
	mu         sync.Mutex
	devices    map[string]Device
	pools      map[string]string
	touches    map[string]int
	touched    []protocol.Capabilities
	touchErr   error
	lookupErr  error
	revokedErr error
}

func newFakeStore() *fakeStore {
	return &fakeStore{
		devices: map[string]Device{},
		pools:   map[string]string{},
		touches: map[string]int{},
	}
}

func (f *fakeStore) add(device Device) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.devices[device.ID] = device
}

func (f *fakeStore) addPoolWorker(id, name string) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.devices[id] = Device{ID: id, Kind: protocol.KindCloudPool, Name: name}
	f.pools[name] = id
}

func (f *fakeStore) setOrganization(id, orgID string) {
	f.mu.Lock()
	defer f.mu.Unlock()
	device := f.devices[id]
	device.OrganizationID = orgID
	f.devices[id] = device
}

func (f *fakeStore) revoke(id string) {
	f.mu.Lock()
	defer f.mu.Unlock()
	device, ok := f.devices[id]
	if !ok {
		return
	}
	device.RevokedAt = time.Now().UTC()
	f.devices[id] = device
}

func (f *fakeStore) touchCount(id string) int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.touches[id]
}

func (f *fakeStore) lastCapabilities() (protocol.Capabilities, bool) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if len(f.touched) == 0 {
		return protocol.Capabilities{}, false
	}
	return f.touched[len(f.touched)-1], true
}

func (f *fakeStore) LookupDevice(_ context.Context, deviceID string) (Device, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.lookupErr != nil {
		return Device{}, f.lookupErr
	}
	device, ok := f.devices[deviceID]
	if !ok || device.Kind != protocol.KindDesktop {
		return Device{}, ErrDeviceNotFound
	}
	return device, nil
}

func (f *fakeStore) LookupPoolWorker(_ context.Context, workerName string) (Device, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.lookupErr != nil {
		return Device{}, f.lookupErr
	}
	id, ok := f.pools[workerName]
	if !ok {
		return Device{}, ErrDeviceNotFound
	}
	return f.devices[id], nil
}

func (f *fakeStore) Touch(_ context.Context, deviceID string, _ time.Time, caps protocol.Capabilities) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.touchErr != nil {
		return f.touchErr
	}
	f.touches[deviceID]++
	f.touched = append(f.touched, caps)
	return nil
}

func (f *fakeStore) RevokedAmong(_ context.Context, deviceIDs []string) (map[string]struct{}, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.revokedErr != nil {
		return nil, f.revokedErr
	}
	out := map[string]struct{}{}
	for _, id := range deviceIDs {
		if device, ok := f.devices[id]; ok && device.Revoked() {
			out[id] = struct{}{}
		}
	}
	return out, nil
}

func (f *fakeStore) Ping(context.Context) error { return nil }

// ── harness ─────────────────────────────────────────────────────────────────

type testGateway struct {
	gateway *gateway
	store   *fakeStore
	// switches is the fake platform_settings behind the gateway's kill-switch
	// facade, so a test can flip a switch or break the store.
	switches *fakeSwitchStore
	server   *httptest.Server

	// clients are every connection the test dialled. They are closed abruptly
	// before the server is torn down: a coder/websocket client only answers a
	// close frame while it is reading, so an idle one would make every teardown
	// wait out the close handshake.
	mu      sync.Mutex
	clients []*websocket.Conn
}

func newTestGateway(t *testing.T, tweak func(*appConfig)) *testGateway {
	t.Helper()
	cfg := appConfig{
		Port:               "0",
		DatabaseURL:        "unused-in-tests",
		GatewayInstance:    "10.0.3.17",
		HeartbeatSec:       protocol.HeartbeatSec,
		DeadAfterSec:       protocol.DeadAfterSec,
		HelloTimeout:       2 * time.Second,
		ClockSkew:          protocol.HelloSkewSec * time.Second,
		NonceTTL:           protocol.NonceTTLSeconds * time.Second,
		RevocationInterval: time.Hour, // off unless a test shortens it
		LastSeenInterval:   0,         // always due, so a touch is observable immediately
	}
	if tweak != nil {
		tweak(&cfg)
	}
	store := newFakeStore()
	g := newGateway(cfg, store)
	// The kill switches are injected explicitly. A gateway with no facade at all
	// fails closed, so a fixture that left it out would make every test run
	// against a paused platform rather than against the thing it means to test.
	switches := newFakeSwitchStore()
	g.attachSwitches(newPlatformSwitches(switches, g.metrics))
	g.start(context.Background())

	router := mux.NewRouter()
	g.RegisterRoutes(router)
	server := httptest.NewServer(router)
	tg := &testGateway{gateway: g, store: store, switches: switches, server: server}
	t.Cleanup(func() {
		tg.mu.Lock()
		clients := tg.clients
		tg.clients = nil
		tg.mu.Unlock()
		for _, ws := range clients {
			_ = ws.CloseNow()
		}
		server.Close()
		g.shutdown()
	})
	return tg
}

// wsURL is the endpoint a runner dials.
func (tg *testGateway) wsURL() string {
	return "ws" + strings.TrimPrefix(tg.server.URL, "http") + protocol.ConnectPath
}

// dialedHost is the hostname the hello signature binds, exactly as a runner
// derives it from the URL it dialled.
func (tg *testGateway) dialedHost(t *testing.T) string {
	t.Helper()
	return protocol.NormalizeGatewayHost(strings.TrimPrefix(tg.server.URL, "http://"))
}

func (tg *testGateway) dial(t *testing.T) *websocket.Conn {
	t.Helper()
	ws, err := tg.dialWith(t, []string{protocol.Subprotocol})
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	return ws
}

func (tg *testGateway) dialWith(t *testing.T, subprotocols []string) (*websocket.Conn, error) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	ws, resp, err := websocket.Dial(ctx, tg.wsURL(), &websocket.DialOptions{Subprotocols: subprotocols})
	// coder/websocket returns a response whose Body is nil on the success path.
	if resp != nil && resp.Body != nil {
		_ = resp.Body.Close()
	}
	if err == nil {
		tg.mu.Lock()
		tg.clients = append(tg.clients, ws)
		tg.mu.Unlock()
	}
	return ws, err
}

// metricValue scrapes /metrics and returns the value on an exact metric line.
func (tg *testGateway) metricValue(t *testing.T, line string) float64 {
	t.Helper()
	resp := httpGet(t, tg.server.URL+"/metrics")
	defer func() { _ = resp.Body.Close() }() // best-effort teardown
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatalf("read /metrics: %v", err)
	}
	value, ok := parseMetricLine(string(body), line)
	if !ok {
		return 0
	}
	return value
}

// expectNoMessage asserts that nothing arrives on the socket within the window.
// coder/websocket bounds a read by its context, so a caller does this once the
// window has elapsed and moves on: any error at all — including the deadline,
// which gives up this client's connection — counts as "nothing arrived".
func expectNoMessage(t *testing.T, ws *websocket.Conn, within time.Duration) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), within)
	defer cancel()
	if _, data, err := ws.Read(ctx); err == nil {
		t.Fatalf("the runner received %s, want nothing", data)
	}
}

// ── runners ─────────────────────────────────────────────────────────────────

type runner struct {
	deviceID string
	priv     ed25519.PrivateKey
	orgID    string
	ws       *websocket.Conn
}

// newRunner registers a paired desktop device in the fake registry.
func (tg *testGateway) newRunner(t *testing.T, orgID string) *runner {
	t.Helper()
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}
	tg.store.mu.Lock()
	deviceID := deviceIDFor(len(tg.store.devices) + 1)
	tg.store.mu.Unlock()
	tg.store.add(Device{
		ID: deviceID, Kind: protocol.KindDesktop, OrganizationID: orgID, Name: "fixture-desktop",
		PublicKey: protocol.EncodeDevicePublicKey(pub),
	})
	return &runner{deviceID: deviceID, priv: priv, orgID: orgID}
}

// deviceIDFor builds a deterministic uuid so tests read like the fixtures.
func deviceIDFor(n int) string {
	return fmt.Sprintf("c3a1f0de-2b4c-4d8e-9a10-%012d", n)
}

func defaultCapabilities() protocol.Capabilities {
	digest := "sha256:" + strings.Repeat("a", 64)
	return protocol.Capabilities{
		ProtocolVersion:    protocol.ProtocolVersion,
		AgentVersion:       "0.1.0",
		OS:                 "linux",
		Arch:               "amd64",
		Slots:              2,
		Container:          protocol.ContainerDocker,
		ContainerMode:      "rootless",
		RuntimeImageDigest: &digest,
		FfmpegVersion:      "7.1",
		FontsetVersion:     "1",
		JobTypes:           protocol.JobTypes(),
		MaxOutputBytes:     1 << 32,
	}
}

func newNonce(t *testing.T) string {
	t.Helper()
	buf := make([]byte, 24)
	if _, err := rand.Read(buf); err != nil {
		t.Fatalf("rand: %v", err)
	}
	return base64.RawURLEncoding.EncodeToString(buf)
}

// desktopHello builds a signed hello. tweak runs before the signature is made,
// so a test can move the timestamp or swap a capability and stay consistent.
func desktopHello(t *testing.T, r *runner, host string, tweak func(*protocol.HelloPayload)) protocol.Envelope {
	t.Helper()
	payload := protocol.HelloPayload{
		ProtocolVersion: protocol.ProtocolVersion,
		Auth: protocol.HelloAuth{
			Kind:     protocol.AuthKindDevice,
			DeviceID: r.deviceID,
			TS:       time.Now().UTC().Format(time.RFC3339),
			Nonce:    newNonce(t),
		},
		Capabilities: defaultCapabilities(),
	}
	if tweak != nil {
		tweak(&payload)
	}
	payload.Auth.Sig = protocol.SignHello(r.priv, payload.Auth.DeviceID, payload.Auth.TS, payload.Auth.Nonce, host)
	env, err := protocol.New(protocol.TypeHello, payload)
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	return env
}

func poolHello(t *testing.T, credential, workerName string) protocol.Envelope {
	t.Helper()
	env, err := protocol.New(protocol.TypeHello, protocol.HelloPayload{
		ProtocolVersion: protocol.ProtocolVersion,
		Auth:            protocol.HelloAuth{Kind: protocol.AuthKindPool, Credential: credential, WorkerName: workerName},
		Capabilities:    defaultCapabilities(),
	})
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	return env
}

// connectHello dials and sends a hello without waiting for the answer.
func (tg *testGateway) connectHello(t *testing.T, env protocol.Envelope) *websocket.Conn {
	t.Helper()
	ws := tg.dial(t)
	send(t, ws, env)
	return ws
}

// handshake dials, sends hello and returns the welcome.
func (tg *testGateway) handshake(t *testing.T, r *runner) (*websocket.Conn, protocol.WelcomePayload) {
	t.Helper()
	ws := tg.connectHello(t, desktopHello(t, r, tg.dialedHost(t), nil))
	env := readEnvelope(t, ws)
	if env.Type != protocol.TypeWelcome {
		t.Fatalf("first answer is %s, want welcome", env.Type)
	}
	welcome, err := protocol.DecodePayload[protocol.WelcomePayload](env)
	if err != nil {
		t.Fatalf("welcome payload: %v", err)
	}
	r.ws = ws
	return ws, welcome
}

// ── message helpers ─────────────────────────────────────────────────────────

func encode(t *testing.T, env protocol.Envelope) []byte {
	t.Helper()
	raw, err := env.Encode()
	if err != nil {
		t.Fatalf("Encode: %v", err)
	}
	return raw
}

func send(t *testing.T, ws *websocket.Conn, env protocol.Envelope) {
	t.Helper()
	sendRaw(t, ws, websocket.MessageText, encode(t, env))
}

func sendRaw(t *testing.T, ws *websocket.Conn, typ websocket.MessageType, raw []byte) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := ws.Write(ctx, typ, raw); err != nil {
		t.Fatalf("write: %v", err)
	}
}

func readEnvelope(t *testing.T, ws *websocket.Conn) protocol.Envelope {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	_, data, err := ws.Read(ctx)
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	env, err := protocol.Decode(data)
	if err != nil {
		t.Fatalf("decode %s: %v", data, err)
	}
	return env
}

func readPayload[T any](t *testing.T, env protocol.Envelope) T {
	t.Helper()
	payload, err := protocol.DecodePayload[T](env)
	if err != nil {
		t.Fatalf("%s payload: %v", env.Type, err)
	}
	return payload
}

// readUntilClose drains messages until the peer closes and returns the close
// code.
func readUntilClose(t *testing.T, ws *websocket.Conn) ([]protocol.Envelope, protocol.CloseCode) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	var seen []protocol.Envelope
	for {
		_, data, err := ws.Read(ctx)
		if err != nil {
			code := websocket.CloseStatus(err)
			if code == -1 {
				t.Fatalf("connection ended without a close code: %v", err)
			}
			return seen, protocol.CloseCode(code)
		}
		env, err := protocol.Decode(data)
		if err != nil {
			t.Fatalf("decode %s: %v", data, err)
		}
		seen = append(seen, env)
	}
}

func mustEnvelope(t *testing.T, ty protocol.MessageType, payload any) protocol.Envelope {
	t.Helper()
	env, err := protocol.New(ty, payload)
	if err != nil {
		t.Fatalf("New(%s): %v", ty, err)
	}
	return env
}

func typesOf(envs []protocol.Envelope) []string {
	out := make([]string, 0, len(envs))
	for _, env := range envs {
		out = append(out, string(env.Type))
	}
	return out
}

// ── tests ───────────────────────────────────────────────────────────────────

const testOrgID = "5e2b1c90-7a3d-4f68-8b21-0c9d4e6f1a22"

func TestDesktopHandshakeSendsWelcome(t *testing.T) {
	tg := newTestGateway(t, nil)
	r := tg.newRunner(t, testOrgID)

	_, welcome := tg.handshake(t, r)

	if welcome.RunnerID != r.deviceID {
		t.Errorf("welcome.runnerId = %q, want the device id %q", welcome.RunnerID, r.deviceID)
	}
	if welcome.Kind != protocol.KindDesktop {
		t.Errorf("welcome.kind = %q", welcome.Kind)
	}
	if welcome.OrgID == nil || *welcome.OrgID != r.orgID {
		t.Errorf("welcome.orgId = %v, want %q", welcome.OrgID, r.orgID)
	}
	if welcome.ProtocolVersion != protocol.ProtocolVersion {
		t.Errorf("welcome.protocolVersion = %q", welcome.ProtocolVersion)
	}
	if welcome.HeartbeatSec != protocol.HeartbeatSec || welcome.DeadAfterSec != protocol.DeadAfterSec {
		t.Errorf("welcome timings = %d/%d, want %d/%d",
			welcome.HeartbeatSec, welcome.DeadAfterSec, protocol.HeartbeatSec, protocol.DeadAfterSec)
	}
	if welcome.MaxInlineBytes != protocol.MaxPayloadBytes {
		t.Errorf("welcome.maxInlineBytes = %d, want %d", welcome.MaxInlineBytes, protocol.MaxPayloadBytes)
	}
	if welcome.GatewayInstance != "10.0.3.17" {
		t.Errorf("welcome.gatewayInstance = %q", welcome.GatewayInstance)
	}
	if _, err := time.Parse(time.RFC3339, welcome.ServerTime); err != nil {
		t.Errorf("welcome.serverTime %q is not RFC 3339: %v", welcome.ServerTime, err)
	}
	if welcome.Resumed == nil || len(welcome.Resumed) != 0 {
		t.Errorf("welcome.resumed = %v, want an empty array", welcome.Resumed)
	}
	if got := tg.gateway.registry.len(); got != 1 {
		t.Errorf("registry holds %d runners, want 1", got)
	}
	if got := tg.metricValue(t, `reelbolt_runner_connected{kind="Desktop"}`); got != 1 {
		t.Errorf("reelbolt_runner_connected{kind=Desktop} = %v, want 1", got)
	}
	if tg.store.touchCount(r.deviceID) == 0 {
		t.Error("the handshake must record last_seen_at")
	}
	caps, ok := tg.store.lastCapabilities()
	if !ok || caps.Slots != 2 || caps.AgentVersion != "0.1.0" {
		t.Errorf("stored capabilities = %+v", caps)
	}
}

func TestHandshakePoolWorker(t *testing.T) {
	tg := newTestGateway(t, func(cfg *appConfig) {
		cfg.PoolCredentials = []string{"pool-secret"}
	})
	workerID := "11111111-1111-1111-1111-111111111111"
	tg.store.addPoolWorker(workerID, "pool-worker-fixture-01")

	ws := tg.connectHello(t, poolHello(t, "pool-secret", "pool-worker-fixture-01"))
	env := readEnvelope(t, ws)
	welcome := readPayload[protocol.WelcomePayload](t, env)

	if welcome.Kind != protocol.KindCloudPool {
		t.Errorf("welcome.kind = %q, want %q", welcome.Kind, protocol.KindCloudPool)
	}
	// orgId must be null, not an empty string: a pool worker belongs to no
	// organisation.
	if !strings.Contains(string(encode(t, env)), `"orgId":null`) {
		t.Errorf("pool welcome must carry orgId null: %s", encode(t, env))
	}
	if welcome.RunnerID != workerID {
		t.Errorf("welcome.runnerId = %q, want %q", welcome.RunnerID, workerID)
	}
}

func TestHelloRunningIsCancelledNotResumed(t *testing.T) {
	tg := newTestGateway(t, nil)
	r := tg.newRunner(t, testOrgID)
	held := protocol.JobLease{JobID: "7d1c2e0a-5b7e-4a43-9d57-3f0c1a9b2e11", LeaseEpoch: 3}

	ws := tg.connectHello(t, desktopHello(t, r, tg.dialedHost(t), func(p *protocol.HelloPayload) {
		p.Running = []protocol.JobLease{held}
	}))
	welcome := readPayload[protocol.WelcomePayload](t, readEnvelope(t, ws))
	// This harness runs the gateway with no queue attached, so there is no job
	// row to resume against and every claimed job is refused. With a queue, the
	// decision is the epoch and lease check in resumeWork (see the integration
	// tests, which drive both outcomes).
	if len(welcome.Resumed) != 0 {
		t.Fatalf("welcome.resumed = %v, want nothing resumed without a queue", welcome.Resumed)
	}
	cancelEnv := readEnvelope(t, ws)
	if cancelEnv.Type != protocol.TypeCancel || cancelEnv.JobID != held.JobID {
		t.Fatalf("expected a cancel for the claimed job, got %s jobId=%q", cancelEnv.Type, cancelEnv.JobID)
	}
	cancel := readPayload[protocol.CancelPayload](t, cancelEnv)
	if cancel.Reason != protocol.CancelLeaseLost || cancel.GraceSec != protocol.CancelGraceSec {
		t.Errorf("cancel = %+v", cancel)
	}
}

func TestHeartbeatIsAnsweredAndRecorded(t *testing.T) {
	tg := newTestGateway(t, nil)
	r := tg.newRunner(t, testOrgID)
	ws, _ := tg.handshake(t, r)

	zero := 0
	held := protocol.JobLease{JobID: "7d1c2e0a-5b7e-4a43-9d57-3f0c1a9b2e11", LeaseEpoch: 2}
	send(t, ws, mustEnvelope(t, protocol.TypeHeartbeat, protocol.HeartbeatPayload{
		FreeSlots: &zero, Running: []protocol.JobLease{held},
	}))

	answer := readEnvelope(t, ws)
	if answer.Type != protocol.TypeHeartbeat {
		t.Fatalf("answer is %s, want heartbeat", answer.Type)
	}
	hb := readPayload[protocol.HeartbeatPayload](t, answer)
	if hb.ServerTime == "" {
		t.Error("the gateway heartbeat carries no serverTime")
	}
	if hb.FreeSlots != nil {
		t.Error("a gateway heartbeat must not carry freeSlots")
	}
	// D5b: the answer reports every lease the gateway still honours. This harness
	// has no queue, so the job the runner claims to hold is not one the gateway
	// can confirm, and ok:false is the answer that makes the runner stop it —
	// never a silent "keep working".
	if len(hb.Leases) != 1 || hb.Leases[0].JobID != held.JobID || hb.Leases[0].OK {
		t.Errorf("heartbeat.leases = %v, want the claimed job reported ok:false", hb.Leases)
	}

	c := tg.gateway.registry.get(r.deviceID)
	if c == nil {
		t.Fatal("the runner is not in the registry")
	}
	state := c.snapshot()
	if state.FreeSlots != 0 {
		t.Errorf("freeSlots = %d, want 0", state.FreeSlots)
	}
	if state.Running[held.JobID] != held.LeaseEpoch {
		t.Errorf("running = %v, want %s at epoch %d", state.Running, held.JobID, held.LeaseEpoch)
	}
	if tg.store.touchCount(r.deviceID) == 0 {
		t.Error("a heartbeat must refresh last_seen_at")
	}
}

func TestReadyUpdatesFreeSlots(t *testing.T) {
	tg := newTestGateway(t, nil)
	r := tg.newRunner(t, testOrgID)
	ws, _ := tg.handshake(t, r)

	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 3}))
	waitFor(t, func() bool {
		c := tg.gateway.registry.get(r.deviceID)
		return c != nil && c.snapshot().FreeSlots == 3
	}, "ready did not update the runner's free slots")
}

func TestDrainMarksTheRunnerDraining(t *testing.T) {
	tg := newTestGateway(t, nil)
	r := tg.newRunner(t, testOrgID)
	ws, _ := tg.handshake(t, r)

	send(t, ws, mustEnvelope(t, protocol.TypeDrain, protocol.DrainPayload{Reason: "sigterm", DeadlineSec: 120}))
	waitFor(t, func() bool {
		c := tg.gateway.registry.get(r.deviceID)
		return c != nil && c.snapshot().Draining
	}, "drain did not mark the runner as draining")
}

func TestSupersededConnectionIsClosed4409(t *testing.T) {
	tg := newTestGateway(t, nil)
	r := tg.newRunner(t, testOrgID)

	first, _ := tg.handshake(t, r)
	second, welcome := tg.handshake(t, r)
	if welcome.RunnerID != r.deviceID {
		t.Fatalf("second connection got runnerId %q", welcome.RunnerID)
	}
	if _, code := readUntilClose(t, first); code != protocol.CloseSuperseded {
		t.Fatalf("the superseded connection closed with %d, want %d", code, protocol.CloseSuperseded)
	}
	if got := tg.gateway.registry.len(); got != 1 {
		t.Fatalf("registry holds %d runners, want 1", got)
	}
	// The replacement is still usable.
	send(t, second, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
}

func TestLivenessClosesAfterTheDeadWindow(t *testing.T) {
	tg := newTestGateway(t, func(cfg *appConfig) { cfg.DeadAfterSec = 1 })
	r := tg.newRunner(t, testOrgID)
	ws, _ := tg.handshake(t, r)

	// Nothing else is sent: 1 s after its last message the connection must be
	// closed with the heartbeat-timeout code.
	started := time.Now()
	_, code := readUntilClose(t, ws)
	if code != protocol.CloseHeartbeatTimeout {
		t.Fatalf("close code = %d, want %d", code, protocol.CloseHeartbeatTimeout)
	}
	if elapsed := time.Since(started); elapsed < 900*time.Millisecond {
		t.Errorf("closed after %s, before the dead window elapsed", elapsed)
	}
	if tg.metricValue(t, "reelbolt_runner_gateway_heartbeat_timeouts_total") != 1 {
		t.Error("the heartbeat timeout was not counted")
	}
}

func TestRevocationSweeperDropsARunner(t *testing.T) {
	tg := newTestGateway(t, func(cfg *appConfig) { cfg.RevocationInterval = 25 * time.Millisecond })
	r := tg.newRunner(t, testOrgID)
	ws, _ := tg.handshake(t, r)

	tg.store.revoke(r.deviceID)

	seen, code := readUntilClose(t, ws)
	if code != protocol.CloseUnauthenticated {
		t.Fatalf("close code = %d, want %d", code, protocol.CloseUnauthenticated)
	}
	foundBye := false
	for _, env := range seen {
		if env.Type == protocol.TypeBye && readPayload[protocol.ByePayload](t, env).Reason == protocol.CancelRevoked {
			foundBye = true
		}
	}
	if !foundBye {
		t.Errorf("no bye(revoked) before the close, saw %v", typesOf(seen))
	}
	// The close is delivered from the sweeper's goroutine, so the connection's
	// own handler may still be unwinding when the client sees it.
	waitFor(t, func() bool { return tg.gateway.registry.len() == 0 }, "the revoked runner stayed in the registry")
}

func TestUnknownMessageTypeIsIgnored(t *testing.T) {
	tg := newTestGateway(t, nil)
	r := tg.newRunner(t, testOrgID)
	ws, _ := tg.handshake(t, r)

	raw, err := json.Marshal(map[string]any{
		"v": 1, "type": "future.thing", "id": "01J9ZQ0000000000000000Z001", "payload": map[string]any{},
	})
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	sendRaw(t, ws, websocket.MessageText, raw)

	// The connection survives, so a heartbeat still gets its answer.
	send(t, ws, mustEnvelope(t, protocol.TypeHeartbeat, protocol.HeartbeatPayload{}))
	if answer := readEnvelope(t, ws); answer.Type != protocol.TypeHeartbeat {
		t.Fatalf("answer is %s, want heartbeat", answer.Type)
	}
	if got := tg.metricValue(t, `reelbolt_runner_gateway_unexpected_messages_total{type="future.thing"}`); got != 1 {
		t.Errorf("unexpected-message counter = %v, want 1", got)
	}
}

func TestByeFromRunnerClosesNormally(t *testing.T) {
	tg := newTestGateway(t, nil)
	r := tg.newRunner(t, testOrgID)
	ws, _ := tg.handshake(t, r)

	send(t, ws, mustEnvelope(t, protocol.TypeBye, protocol.ByePayload{Reason: "shutdown"}))
	if _, code := readUntilClose(t, ws); code != protocol.CloseNormal {
		t.Fatalf("close code = %d, want %d", code, protocol.CloseNormal)
	}
}

func TestRefusals(t *testing.T) {
	type refusal struct {
		name   string
		metric string
		code   protocol.CloseCode
		// prepare runs after the runner exists and before the frame is built.
		prepare func(tg *testGateway, r *runner)
		frame   func(t *testing.T, tg *testGateway, r *runner, host string) []byte
	}

	signedHello := func(t *testing.T, tg *testGateway, r *runner, host string) []byte {
		return encode(t, desktopHello(t, r, host, nil))
	}

	cases := []refusal{
		{
			name: "bad signature", metric: "bad_signature", code: protocol.CloseUnauthenticated,
			frame: func(t *testing.T, tg *testGateway, r *runner, host string) []byte {
				_, other, err := ed25519.GenerateKey(rand.Reader)
				if err != nil {
					t.Fatalf("GenerateKey: %v", err)
				}
				return encode(t, desktopHello(t, &runner{deviceID: r.deviceID, priv: other}, host, nil))
			},
		},
		{
			name: "unknown device", metric: "unknown_device", code: protocol.CloseUnauthenticated,
			frame: func(t *testing.T, tg *testGateway, r *runner, host string) []byte {
				unknown := &runner{deviceID: "11111111-2222-3333-4444-555555555555", priv: r.priv}
				return encode(t, desktopHello(t, unknown, host, nil))
			},
		},
		{
			name: "revoked device", metric: "revoked_device", code: protocol.CloseUnauthenticated,
			prepare: func(tg *testGateway, r *runner) { tg.store.revoke(r.deviceID) },
			frame:   signedHello,
		},
		{
			name: "clock skew", metric: "clock_skew", code: protocol.CloseUnauthenticated,
			frame: func(t *testing.T, tg *testGateway, r *runner, host string) []byte {
				return encode(t, desktopHello(t, r, host, func(p *protocol.HelloPayload) {
					p.Auth.TS = time.Now().UTC().Add(-10 * time.Minute).Format(time.RFC3339)
				}))
			},
		},
		{
			name: "device without an organisation", metric: "no_organization", code: protocol.CloseForbidden,
			prepare: func(tg *testGateway, r *runner) { tg.store.setOrganization(r.deviceID, "") },
			frame:   signedHello,
		},
		{
			name: "unsupported protocol version", metric: "unsupported_version", code: protocol.CloseUnsupportedVersion,
			frame: func(t *testing.T, tg *testGateway, r *runner, host string) []byte {
				return encode(t, desktopHello(t, r, host, func(p *protocol.HelloPayload) {
					p.ProtocolVersion = "2.0"
					p.Capabilities.ProtocolVersion = "2.0"
				}))
			},
		},
		{
			name: "container-less runner advertising remotion", metric: "malformed_hello", code: protocol.CloseUnsupportedVersion,
			frame: func(t *testing.T, tg *testGateway, r *runner, host string) []byte {
				return encode(t, desktopHello(t, r, host, func(p *protocol.HelloPayload) {
					p.Capabilities.Container = protocol.ContainerNone
					p.Capabilities.RuntimeImageDigest = nil
				}))
			},
		},
		{
			name: "hello payload that does not bind", metric: "malformed_hello", code: protocol.CloseUnsupportedVersion,
			frame: func(t *testing.T, tg *testGateway, r *runner, host string) []byte {
				env := desktopHello(t, r, host, nil)
				env.Payload = json.RawMessage("[]")
				return encode(t, env)
			},
		},
		{
			name: "not hello first", metric: "malformed_hello", code: protocol.CloseUnsupportedVersion,
			frame: func(t *testing.T, tg *testGateway, r *runner, host string) []byte {
				return encode(t, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
			},
		},
		{
			name: "binary frame first", metric: "binary_frame", code: protocol.CloseUnsupportedVersion,
			frame: func(t *testing.T, tg *testGateway, r *runner, host string) []byte { return []byte("binary") },
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			tg := newTestGateway(t, nil)
			r := tg.newRunner(t, testOrgID)
			host := tg.dialedHost(t)
			if tc.prepare != nil {
				tc.prepare(tg, r)
			}
			frame := tc.frame(t, tg, r, host)

			ws := tg.dial(t)
			frameType := websocket.MessageText
			if tc.name == "binary frame first" {
				frameType = websocket.MessageBinary
			}
			sendRaw(t, ws, frameType, frame)
			if _, code := readUntilClose(t, ws); code != tc.code {
				t.Fatalf("close code = %d, want %d", code, tc.code)
			}
			want := `reelbolt_runner_gateway_handshakes_total{result="` + tc.metric + `"}`
			if got := tg.metricValue(t, want); got != 1 {
				t.Errorf("%s = %v, want 1", want, got)
			}
		})
	}
}

func TestReplayedNonceIsRefused(t *testing.T) {
	tg := newTestGateway(t, nil)
	r := tg.newRunner(t, testOrgID)

	// One hello, sent twice: a captured hello must not be replayable even though
	// its signature is perfectly valid.
	env := desktopHello(t, r, tg.dialedHost(t), nil)

	first := tg.connectHello(t, env)
	if got := readEnvelope(t, first).Type; got != protocol.TypeWelcome {
		t.Fatalf("first connection answered %s, want welcome", got)
	}

	second := tg.connectHello(t, env)
	if _, code := readUntilClose(t, second); code != protocol.CloseUnauthenticated {
		t.Fatalf("replayed hello closed with %d, want %d", code, protocol.CloseUnauthenticated)
	}
	if got := tg.metricValue(t, "reelbolt_runner_gateway_nonce_replays_total"); got != 1 {
		t.Errorf("replay counter = %v, want 1", got)
	}
}

func TestPoolRefusals(t *testing.T) {
	cases := map[string]struct {
		credential string
		worker     string
		metric     string
	}{
		"wrong credential": {"nope", "pool-worker-fixture-01", "bad_pool_credential"},
		"unknown worker":   {"pool-secret", "pool-worker-that-was-never-enrolled", "unknown_pool_worker"},
	}
	for name, tc := range cases {
		t.Run(name, func(t *testing.T) {
			tg := newTestGateway(t, func(cfg *appConfig) { cfg.PoolCredentials = []string{"pool-secret"} })
			tg.store.addPoolWorker("11111111-1111-1111-1111-111111111111", "pool-worker-fixture-01")

			ws := tg.connectHello(t, poolHello(t, tc.credential, tc.worker))
			if _, code := readUntilClose(t, ws); code != protocol.CloseUnauthenticated {
				t.Fatalf("close code = %d, want %d", code, protocol.CloseUnauthenticated)
			}
			want := `reelbolt_runner_gateway_handshakes_total{result="` + tc.metric + `"}`
			if got := tg.metricValue(t, want); got != 1 {
				t.Errorf("%s = %v, want 1", want, got)
			}
		})
	}
}

func TestNoPoolCredentialConfiguredRefusesPoolWorkers(t *testing.T) {
	tg := newTestGateway(t, nil)
	tg.store.addPoolWorker("11111111-1111-1111-1111-111111111111", "pool-worker-fixture-01")

	ws := tg.connectHello(t, poolHello(t, "pool-secret", "pool-worker-fixture-01"))
	if _, code := readUntilClose(t, ws); code != protocol.CloseUnauthenticated {
		t.Fatalf("close code = %d, want %d", code, protocol.CloseUnauthenticated)
	}
}

func TestHelloTimeoutCloses4400(t *testing.T) {
	tg := newTestGateway(t, func(cfg *appConfig) { cfg.HelloTimeout = 150 * time.Millisecond })
	ws := tg.dial(t)

	if _, code := readUntilClose(t, ws); code != protocol.CloseUnsupportedVersion {
		t.Fatalf("close code = %d, want %d", code, protocol.CloseUnsupportedVersion)
	}
	if got := tg.metricValue(t, "reelbolt_runner_gateway_handshake_timeouts_total"); got != 1 {
		t.Errorf("handshake timeout counter = %v, want 1", got)
	}
}

func TestConnectionWithoutTheSubprotocolIsClosed(t *testing.T) {
	tg := newTestGateway(t, nil)
	ws, err := tg.dialWith(t, []string{"something.else"})
	if err != nil {
		// coder/websocket refuses a dial whose subprotocol the server did not
		// echo, which is also a correct outcome.
		return
	}
	defer func() { _ = ws.CloseNow() }()
	if _, code := readUntilClose(t, ws); code != protocol.CloseUnsupportedVersion {
		t.Fatalf("close code = %d, want %d", code, protocol.CloseUnsupportedVersion)
	}
}

func TestHealthzAndMetrics(t *testing.T) {
	tg := newTestGateway(t, nil)
	r := tg.newRunner(t, testOrgID)

	resp := httpGet(t, tg.server.URL+"/healthz")
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("/healthz status = %d", resp.StatusCode)
	}
	var health map[string]any
	if err := json.NewDecoder(resp.Body).Decode(&health); err != nil {
		t.Fatalf("health body: %v", err)
	}
	_ = resp.Body.Close() // best-effort teardown
	if health["status"] != "ok" {
		t.Errorf("health = %v", health)
	}
	if health["gatewayInstance"] != "10.0.3.17" {
		t.Errorf("gatewayInstance = %v", health["gatewayInstance"])
	}

	tg.handshake(t, r)

	metricsResp := httpGet(t, tg.server.URL+"/metrics")
	defer func() { _ = metricsResp.Body.Close() }() // best-effort teardown
	body, _ := io.ReadAll(metricsResp.Body)
	for _, want := range []string{
		`reelbolt_runner_connected{kind="Desktop"}`,
		`reelbolt_runner_gateway_handshakes_total{result="ok"}`,
		`reelbolt_runner_gateway_messages_total{direction="in",type="hello"}`,
		"go_goroutines",
	} {
		if !strings.Contains(string(body), want) {
			t.Errorf("/metrics does not expose %s", want)
		}
	}
}

func TestShutdownClosesEveryRunner(t *testing.T) {
	tg := newTestGateway(t, nil)
	r := tg.newRunner(t, testOrgID)
	ws, _ := tg.handshake(t, r)

	done := make(chan struct{})
	go func() {
		defer close(done)
		tg.gateway.shutdown()
	}()

	seen, code := readUntilClose(t, ws)
	if code != protocol.CloseGoingAway {
		t.Fatalf("close code = %d, want %d", code, protocol.CloseGoingAway)
	}
	foundBye := false
	for _, env := range seen {
		if env.Type == protocol.TypeBye {
			foundBye = true
		}
	}
	if !foundBye {
		t.Errorf("the gateway must say goodbye before a shutdown close, saw %v", typesOf(seen))
	}
	<-done

	resp := httpGet(t, tg.server.URL+"/healthz")
	defer func() { _ = resp.Body.Close() }() // best-effort teardown
	if resp.StatusCode != http.StatusServiceUnavailable {
		t.Errorf("/healthz while draining = %d, want 503", resp.StatusCode)
	}
}

func TestNonceCacheSweepsExpiredEntries(t *testing.T) {
	cache := newNonceCache(50 * time.Millisecond)
	now := time.Now()
	if ok, saturated := cache.claim("d", "n1", now); !ok || saturated {
		t.Fatal("the first claim must succeed")
	}
	if ok, _ := cache.claim("d", "n1", now); ok {
		t.Fatal("a repeated (device, nonce) pair must be refused")
	}
	if cache.size() != 1 {
		t.Fatalf("cache size = %d", cache.size())
	}
	if ok, _ := cache.claim("d", "n2", now.Add(time.Second)); !ok {
		t.Fatal("a fresh nonce must be accepted")
	}
	if cache.size() != 1 {
		t.Fatalf("expired entries were not swept: size = %d", cache.size())
	}
}

func TestNonceCacheSaturationIsRefused(t *testing.T) {
	cache := newNonceCache(time.Hour)
	now := time.Now()
	for i := 0; i < maxTrackedNonces; i++ {
		if ok, _ := cache.claim("d", fmt.Sprintf("n%d", i), now); !ok {
			t.Fatalf("claim %d failed before the cache was full", i)
		}
	}
	if ok, saturated := cache.claim("d", "overflow", now); ok || !saturated {
		t.Fatal("a full cache of live entries must refuse the handshake rather than evict")
	}
}

// httpGet is the tests' only HTTP call: noctx wants a request carrying a
// context, and the cancel runs at the end of the test so the body stays readable.
func httpGet(t *testing.T, url string) *http.Response {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	t.Cleanup(cancel)
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		t.Fatalf("build request for %s: %v", url, err)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("GET %s: %v", url, err)
	}
	return resp
}

func waitFor(t *testing.T, condition func() bool, message string) {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		if condition() {
			return
		}
		time.Sleep(5 * time.Millisecond)
	}
	t.Fatal(message)
}

// ── the platform kill switches (F11) ────────────────────────────────────────

// TestHandshakeRefusesADisabledOrganization: 4403 is the code the protocol table
// reserves for a disabled organisation, and pkg/agent treats it as TERMINAL, so
// the runner does not reconnect into a loop. That is exactly why it is only sent
// for a switch the gateway actually read — see the next test.
func TestHandshakeRefusesADisabledOrganization(t *testing.T) {
	tg := newTestGateway(t, nil)
	r := tg.newRunner(t, testOrgID)
	tg.switches.serve(switchesFrom(switchRow{
		Key: settingsKeyOrgPrefix + testOrgID, Paused: true,
	}))

	ws := tg.connectHello(t, desktopHello(t, r, tg.dialedHost(t), nil))
	_, code := readUntilClose(t, ws)
	if code != protocol.CloseForbidden {
		t.Fatalf("close code = %d, want 4403", code)
	}
	if got := tg.metricValue(t, `reelbolt_runner_gateway_handshakes_total{result="organization_disabled"}`); got != 1 {
		t.Errorf("handshakes{result=organization_disabled} = %v, want 1", got)
	}
	if got := tg.gateway.registry.len(); got != 0 {
		t.Errorf("registry holds %d runners, want 0: a refused hello must not register", got)
	}
}

// TestHandshakeAcceptsAReaderItCannotJudge documents the ONE deliberate
// asymmetry of the kill switches. A store nobody can read produces no
// organisation decision, so the hello is accepted rather than answered with
// 4403 — refusing a reconnect loop because a settings table blipped would be a
// worse bug than the switch it protects. The runner that gets through is handed
// nothing: the claim gate refuses on the very same unreadable snapshot.
func TestHandshakeAcceptsAReaderItCannotJudge(t *testing.T) {
	tg := newTestGateway(t, nil)
	r := tg.newRunner(t, testOrgID)
	tg.switches.fail(errors.New("no such table: platform_settings"))

	_, welcome := tg.handshake(t, r)
	if welcome.RunnerID != r.deviceID {
		t.Fatalf("welcome.runnerId = %q, want %q", welcome.RunnerID, r.deviceID)
	}
	if got := tg.metricValue(t, `reelbolt_runner_gateway_handshakes_total{result="ok"}`); got != 1 {
		t.Errorf("handshakes{result=ok} = %v, want 1", got)
	}
	if got := tg.metricValue(t, `reelbolt_runner_gateway_handshakes_total{result="organization_disabled"}`); got != 0 {
		t.Errorf("handshakes{result=organization_disabled} = %v, want 0: the decision was invented", got)
	}
	if got := tg.metricValue(t, "reelbolt_platform_switches_unreadable_total"); got != 1 {
		t.Errorf("reelbolt_platform_switches_unreadable_total = %v, want 1", got)
	}
	// The other half of the rule, and the reason accepting is safe: the runner
	// this hello let in can never be assigned work.
	if _, allowed := tg.gateway.claimGate(context.Background()); allowed {
		t.Error("a runner accepted against an unreadable store was allowed to claim work")
	}
}
