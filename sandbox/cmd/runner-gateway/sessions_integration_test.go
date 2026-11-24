package main

import (
	"bytes"
	"context"
	"encoding/json"
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

// These are D5c's acceptance tests: they drive the real internal API over HTTP
// against two REAL gateway replicas sharing one throwaway Postgres, with a real
// WebSocket runner executing the ops. Set RUNNER_GATEWAY_TEST_DATABASE_URL (see
// queue_integration_test.go for the container command) to run them.

// ── two replicas ────────────────────────────────────────────────────────────

// replicaAddress points a harness at ITS OWN httptest address, which is what a
// real deployment gets from the downward API (a pod IP). It must be set before
// anything is claimed, because the address is copied into
// runner_sessions.gateway_instance at assignment.
func replicaAddress(t *testing.T, qg *queueGateway) string {
	t.Helper()
	address := strings.TrimPrefix(qg.server.URL, "http://")
	qg.gateway.cfg.GatewayInstance = address
	return address
}

// secondReplica starts a second gateway over the SAME database and the same
// harness schema: two servers, two in-memory registries, two op tables, one
// queue. It is what "a two-replica test proxies an op correctly" needs, and it
// deliberately does not re-create the schema (newQueueGateway truncates, which
// would wipe the first replica's fixture).
func secondReplica(t *testing.T, qg *queueGateway) *queueGateway {
	t.Helper()
	cfg := qg.gateway.cfg
	cfg.GatewayInstance = "" // set to this replica's own address below
	g := newGateway(cfg, qg.devices)
	g.attachQueue(qg.jobs, qg.gateway.tokens)
	g.start(context.Background())

	router := mux.NewRouter()
	g.RegisterRoutes(router)
	server := httptest.NewServer(router)
	g.cfg.GatewayInstance = strings.TrimPrefix(server.URL, "http://")
	t.Cleanup(func() {
		server.Close()
		g.shutdown()
	})
	return &queueGateway{
		testGateway: &testGateway{gateway: g, server: server},
		jobs:        qg.jobs, devices: qg.devices, pool: qg.pool, client: server.Client(),
	}
}

// ── a runner that answers ops ───────────────────────────────────────────────

// fakeSessionRunner is a runner socket with a worker behind it: it answers
// op.request from an injected function, records what it received, and records
// the cancels it is told to act on. It owns the socket's reads from the moment
// it starts, so a test must finish reading messages directly before calling it.
type fakeSessionRunner struct {
	ws    *websocket.Conn
	token string
	epoch int64

	mu      sync.Mutex
	ops     []protocol.OpRequestPayload
	cancels []string

	// answer decides what to reply with. Returning false simulates an op that
	// never finishes, which is what every timeout and cancellation test needs.
	answer func(protocol.OpRequestPayload) (protocol.OpResponsePayload, bool)

	done chan struct{}
}

func startFakeRunner(t *testing.T, ws *websocket.Conn, token string, epoch int64, answer func(protocol.OpRequestPayload) (protocol.OpResponsePayload, bool)) *fakeSessionRunner {
	t.Helper()
	f := &fakeSessionRunner{ws: ws, token: token, epoch: epoch, answer: answer, done: make(chan struct{})}
	go f.loop()
	t.Cleanup(func() {
		_ = ws.CloseNow()
		select {
		case <-f.done:
		case <-time.After(2 * time.Second):
		}
	})
	return f
}

func (f *fakeSessionRunner) loop() {
	defer close(f.done)
	for {
		ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
		_, data, err := f.ws.Read(ctx)
		cancel()
		if err != nil {
			return
		}
		env, err := protocol.Decode(data)
		if err != nil {
			return
		}
		switch env.Type {
		case protocol.TypeOpRequest:
			payload, err := protocol.DecodePayload[protocol.OpRequestPayload](env)
			if err != nil {
				continue
			}
			f.mu.Lock()
			f.ops = append(f.ops, payload)
			f.mu.Unlock()
			out, ok := f.answer(payload)
			if !ok {
				continue
			}
			if out.LeaseEpoch == 0 {
				out.LeaseEpoch = f.epoch
			}
			if out.JobToken == "" {
				out.JobToken = f.token
			}
			reply, err := protocol.NewJob(protocol.TypeOpResponse, env.JobID, env.SessionID, out)
			if err != nil {
				continue
			}
			raw, err := reply.Encode()
			if err != nil {
				continue
			}
			writeCtx, writeCancel := context.WithTimeout(context.Background(), 5*time.Second)
			err = f.ws.Write(writeCtx, websocket.MessageText, raw)
			writeCancel()
			if err != nil {
				return
			}
		case protocol.TypeCancel:
			payload, err := protocol.DecodePayload[protocol.CancelPayload](env)
			if err != nil {
				continue
			}
			f.mu.Lock()
			f.cancels = append(f.cancels, payload.Reason)
			f.mu.Unlock()
		}
	}
}

func (f *fakeSessionRunner) receivedOps() []protocol.OpRequestPayload {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]protocol.OpRequestPayload(nil), f.ops...)
}

func (f *fakeSessionRunner) cancelReasons() []string {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]string(nil), f.cancels...)
}

// opEcho answers every op with a fixed result.
func opEcho(result string) func(protocol.OpRequestPayload) (protocol.OpResponsePayload, bool) {
	return func(p protocol.OpRequestPayload) (protocol.OpResponsePayload, bool) {
		return protocol.OpResponsePayload{OpID: p.OpID, OK: true, Result: json.RawMessage(result)}, true
	}
}

// opHangs never answers.
func opHangs(protocol.OpRequestPayload) (protocol.OpResponsePayload, bool) {
	return protocol.OpResponsePayload{}, false
}

// ── fixtures ────────────────────────────────────────────────────────────────

// claimedSession creates a job, connects a runner, has it accept, and returns
// everything the engine needs to address the session.
type claimedSession struct {
	jobID     string
	sessionID string
	token     string
	epoch     int64
	ws        *websocket.Conn
}

func claimSession(t *testing.T, qg *queueGateway, r *runner, jobType string) claimedSession {
	t.Helper()
	view := qg.createJob(t, map[string]any{
		"orgId": testOrgA, "executionId": testExecA, "stepId": testStepID,
		"jobType": jobType, "target": targetOrgRunner, "purpose": jobType,
		"spec": map[string]any{"executionId": testExecA, "stepId": testStepID, "mode": "Reencode"},
	})
	ws, _ := qg.handshake(t, r)
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
	assignEnv := readEnvelope(t, ws)
	if assignEnv.Type != protocol.TypeAssign {
		t.Fatalf("got %s, want assign", assignEnv.Type)
	}
	assign := readPayload[protocol.AssignPayload](t, assignEnv)
	send(t, ws, jobEnvelope(t, protocol.TypeAccept, view.JobID, protocol.AcceptPayload{
		LeaseEpoch: assign.LeaseEpoch, JobToken: assign.JobToken,
	}))
	waitForStatus(t, qg, view.JobID, jobRunning)

	// The engine learns the session id from the job view's long-poll, which is
	// the only place it is published.
	assigned, status := qg.getJob(t, view.JobID, "")
	if status != http.StatusOK {
		t.Fatalf("GET job = %d", status)
	}
	if assigned.SessionID == "" {
		t.Fatal("the assigned job carries no sessionId")
	}
	return claimedSession{
		jobID: view.JobID, sessionID: assigned.SessionID,
		token: assign.JobToken, epoch: assign.LeaseEpoch, ws: ws,
	}
}

// postOp drives POST /internal/v1/sessions/{id}/ops.
func postOp(t *testing.T, qg *queueGateway, sessionID, body string) (int, map[string]any, http.Header) {
	t.Helper()
	req, err := http.NewRequestWithContext(context.Background(), http.MethodPost,
		qg.server.URL+"/internal/v1/sessions/"+sessionID+"/ops", strings.NewReader(body))
	if err != nil {
		t.Fatalf("build request: %v", err)
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", "Bearer internal-test-token")
	resp, err := qg.client.Do(req)
	if err != nil {
		t.Fatalf("POST session op: %v", err)
	}
	defer resp.Body.Close()
	raw, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatalf("read body: %v", err)
	}
	decoded := map[string]any{}
	if len(bytes.TrimSpace(raw)) > 0 {
		if err := json.Unmarshal(raw, &decoded); err != nil {
			t.Fatalf("decode %q: %v", raw, err)
		}
	}
	return resp.StatusCode, decoded, resp.Header
}

func getScaling(t *testing.T, qg *queueGateway) (int, scalingResponse) {
	t.Helper()
	req, err := http.NewRequestWithContext(context.Background(), http.MethodGet, qg.server.URL+"/internal/v1/scaling", nil)
	if err != nil {
		t.Fatalf("build request: %v", err)
	}
	req.Header.Set("Authorization", "Bearer internal-test-token")
	resp, err := qg.client.Do(req)
	if err != nil {
		t.Fatalf("GET scaling: %v", err)
	}
	defer resp.Body.Close()
	var out scalingResponse
	if err := json.NewDecoder(resp.Body).Decode(&out); err != nil {
		t.Fatalf("decode scaling: %v", err)
	}
	return resp.StatusCode, out
}

// ── proxying across replicas ────────────────────────────────────────────────

// TestSessionOpProxiesAcrossReplicas is the acceptance test for "a two-replica
// test proxies an op correctly". The runner is connected to replica A, so the
// session row names A; the engine sends the op to replica B, which must forward
// it over the headless service to A, wait for the runner's op.response and hand
// the result back unchanged.
func TestSessionOpProxiesAcrossReplicas(t *testing.T) {
	qg := newQueueGateway(t, nil)
	addressA := replicaAddress(t, qg)
	replicaB := secondReplica(t, qg)

	r := qg.newQueueRunner(t, testOrgA)
	session := claimSession(t, qg, r, protocol.JobVideoCompile)

	// A's address is what the routing seam holds.
	stored, err := qg.jobs.SessionByID(context.Background(), session.sessionID)
	if err != nil {
		t.Fatalf("SessionByID: %v", err)
	}
	if stored.GatewayInstance != addressA {
		t.Fatalf("gateway_instance = %q, want replica A (%q)", stored.GatewayInstance, addressA)
	}
	if replicaB.gateway.cfg.GatewayInstance == "" || replicaB.gateway.cfg.GatewayInstance == addressA {
		t.Fatalf("the second replica did not get an address of its own: %q", replicaB.gateway.cfg.GatewayInstance)
	}

	runner := startFakeRunner(t, session.ws, session.token, session.epoch,
		opEcho(`{"bytes":12,"sha256":"abc"}`))

	// The engine talks to B, which does not hold the socket.
	status, body, header := postOp(t, replicaB, session.sessionID,
		`{"opId":"op-proxy-1","op":"writeFile","args":{"path":"src/a.ts","content":"x"},"timeoutSec":10}`)
	if status != http.StatusOK {
		t.Fatalf("proxied op = %d %v", status, body)
	}
	if body["ok"] != true {
		t.Fatalf("proxied op body = %v, want ok", body)
	}
	result, _ := body["result"].(map[string]any)
	if result["sha256"] != "abc" {
		t.Errorf("result = %v, want the runner's own result", body["result"])
	}
	if body["opId"] != "op-proxy-1" {
		t.Errorf("opId = %v", body["opId"])
	}
	// The answer says which pod actually served it, which is the difference
	// between "the proxy worked" and "B invented an answer".
	if got := header.Get(proxiedToHeader); got != addressA {
		t.Errorf("%s = %q, want %q", proxiedToHeader, got, addressA)
	}
	// And the op really reached the runner, with the engine's own opId.
	ops := runner.receivedOps()
	if len(ops) != 1 || ops[0].OpID != "op-proxy-1" || ops[0].Op != "writeFile" {
		t.Fatalf("the runner received %+v", ops)
	}
	if ops[0].TimeoutSec != 10 {
		t.Errorf("op.request.timeoutSec = %d, want the engine's 10", ops[0].TimeoutSec)
	}
	if !strings.Contains(string(ops[0].Args), "src/a.ts") {
		t.Errorf("op.request.args = %s, want the engine's arguments verbatim", ops[0].Args)
	}

	// The session's last_op_at is recorded, which is what the idle rule reads.
	stored, err = qg.jobs.SessionByID(context.Background(), session.sessionID)
	if err != nil {
		t.Fatalf("SessionByID: %v", err)
	}
	if stored.LastOpAt.IsZero() {
		t.Error("last_op_at was not recorded for a completed op")
	}
}

// TestSessionOpServedOnTheOwningReplicaNeedsNoProxy pins the other half: an op
// sent to the replica holding the socket never leaves it, and one sent to a
// replica that holds NEITHER the socket nor the session row does not invent an
// answer.
func TestSessionOpIsServedLocallyAndRefusedWhenNobodyHoldsIt(t *testing.T) {
	qg := newQueueGateway(t, nil)
	addressA := replicaAddress(t, qg)
	replicaB := secondReplica(t, qg)

	r := qg.newQueueRunner(t, testOrgA)
	session := claimSession(t, qg, r, protocol.JobVideoCompile)
	startFakeRunner(t, session.ws, session.token, session.epoch, opEcho(`{"bytes":1}`))

	status, body, header := postOp(t, qg, session.sessionID, `{"op":"status"}`)
	if status != http.StatusOK {
		t.Fatalf("local op = %d %v", status, body)
	}
	if got := header.Get(proxiedToHeader); got != "" {
		t.Errorf("a locally served op reported %s = %q", proxiedToHeader, got)
	}
	if body["servedBy"] != addressA {
		t.Errorf("servedBy = %v, want %q", body["servedBy"], addressA)
	}

	// B knows the session exists and names A, so it forwards; A is reachable but
	// holds no connection for the runner any more once the socket is gone. The
	// honest answer is 410 session_lost, not a fabricated success.
	_ = session.ws.CloseNow()
	waitFor(t, func() bool { return qg.gateway.registry.get(r.deviceID) == nil }, "the runner to be unregistered")
	status, body, _ = postOp(t, replicaB, session.sessionID, `{"op":"status"}`)
	if status != http.StatusGone {
		t.Fatalf("op with no live runner = %d %v, want 410", status, body)
	}
	if body["error"] != "session_lost" {
		t.Errorf("error = %v, want session_lost", body["error"])
	}

	// An id that addresses nothing at all is a 404, never a 410: the difference
	// is what tells the engine whether the session ever existed.
	status, body, _ = postOp(t, qg, "0f0f0f0f-0000-4000-8000-000000000000", `{"op":"status"}`)
	if status != http.StatusNotFound || body["error"] != "session_not_found" {
		t.Errorf("unknown session = %d %v, want 404 session_not_found", status, body)
	}
}

// ── one op in flight ────────────────────────────────────────────────────────

func TestSessionOpInFlightIsRefused(t *testing.T) {
	qg := newQueueGateway(t, nil)
	r := qg.newQueueRunner(t, testOrgA)
	session := claimSession(t, qg, r, protocol.JobVideoCompile)

	// One op that never finishes. Its deadline is short so that tearing the
	// harness down does not have to wait out a render-sized timeout.
	startFakeRunner(t, session.ws, session.token, session.epoch, opHangs)
	first := make(chan int, 1)
	go func() {
		status, _, _ := postOp(t, qg, session.sessionID, `{"opId":"slow","op":"render","timeoutSec":3}`)
		first <- status
	}()
	waitFor(t, func() bool { return qg.gateway.ops.size() == 1 }, "the first op to be in flight")

	status, body, _ := postOp(t, qg, session.sessionID, `{"opId":"second","op":"status","timeoutSec":30}`)
	if status != http.StatusConflict {
		t.Fatalf("second concurrent op = %d %v, want 409", status, body)
	}
	if body["error"] != "op_in_flight" {
		t.Errorf("error = %v, want op_in_flight", body["error"])
	}
	if body["opId"] != "slow" {
		t.Errorf("the refusal names %v, want the op that is in flight", body["opId"])
	}
	select {
	case status := <-first:
		t.Fatalf("the first op returned %d while it should still be waiting", status)
	default:
	}
}

// ── timeouts ────────────────────────────────────────────────────────────────

func TestSessionOpTimesOutWhenTheRunnerDoesNotAnswer(t *testing.T) {
	qg := newQueueGateway(t, nil)
	r := qg.newQueueRunner(t, testOrgA)
	session := claimSession(t, qg, r, protocol.JobVideoCompile)
	startFakeRunner(t, session.ws, session.token, session.epoch, opHangs)

	started := time.Now()
	status, body, _ := postOp(t, qg, session.sessionID, `{"opId":"hang","op":"render","timeoutSec":1}`)
	elapsed := time.Since(started)
	if status != http.StatusGatewayTimeout {
		t.Fatalf("hanging op = %d %v, want 504", status, body)
	}
	if body["error"] != "op_timeout" {
		t.Errorf("error = %v, want op_timeout", body["error"])
	}
	if elapsed > 10*time.Second {
		t.Errorf("the op took %s to time out, want about one second", elapsed)
	}
	// The session is free again: the entry is released by the timeout itself.
	if n := qg.gateway.ops.size(); n != 0 {
		t.Errorf("ops in flight after a timeout = %d, want 0", n)
	}
	// And it is counted, which is what an operator watches.
	if got := qg.metricValue(t, `reelbolt_runner_gateway_session_ops_total{result="timeout"}`); got != 1 {
		t.Errorf("timeout counter = %v, want 1", got)
	}
}

// ── cancellation ────────────────────────────────────────────────────────────

// TestCancelReleasesAnInFlightOpWithinTheGraceWindow is the acceptance test for
// "cancel kills a fake runner's op within 10 s": the runner is told to stop, and
// the engine's blocked call is released immediately rather than being left to
// time out on a session that is being cancelled.
func TestCancelReleasesAnInFlightOpWithinTheGraceWindow(t *testing.T) {
	qg := newQueueGateway(t, nil)
	r := qg.newQueueRunner(t, testOrgA)
	session := claimSession(t, qg, r, protocol.JobVideoCompile)
	runner := startFakeRunner(t, session.ws, session.token, session.epoch, opHangs)

	type answer struct {
		status int
		body   map[string]any
	}
	released := make(chan answer, 1)
	go func() {
		status, body, _ := postOp(t, qg, session.sessionID, `{"opId":"long-render","op":"render","timeoutSec":120}`)
		released <- answer{status: status, body: body}
	}()
	waitFor(t, func() bool { return qg.gateway.ops.size() == 1 }, "the render op to be in flight")

	// The engine cancels the job.
	deadline := time.Now().Add(10 * time.Second)
	req, err := http.NewRequestWithContext(context.Background(), http.MethodDelete, qg.server.URL+"/internal/v1/jobs/"+session.jobID, nil)
	if err != nil {
		t.Fatalf("build request: %v", err)
	}
	req.Header.Set("Authorization", "Bearer internal-test-token")
	resp, err := qg.client.Do(req)
	if err != nil {
		t.Fatalf("DELETE job: %v", err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusAccepted {
		t.Fatalf("DELETE job = %d, want 202", resp.StatusCode)
	}

	select {
	case got := <-released:
		if elapsed := time.Since(deadline.Add(-10 * time.Second)); elapsed > 10*time.Second {
			t.Errorf("the op took %s to be released", elapsed)
		}
		if got.status != http.StatusConflict {
			t.Fatalf("a cancelled op = %d %v, want 409", got.status, got.body)
		}
		if got.body["error"] != "op_cancelled" || got.body["reason"] != protocol.CancelUser {
			t.Errorf("cancelled op body = %v, want op_cancelled/user", got.body)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("the in-flight op was not released within the 10-second cancellation window")
	}
	// The runner was told to stop, which is how the op actually dies on the
	// machine (the engine's release above is only the gateway's half).
	waitFor(t, func() bool { return len(runner.cancelReasons()) > 0 }, "the runner to be told to cancel")
	if reasons := runner.cancelReasons(); reasons[0] != protocol.CancelUser {
		t.Errorf("cancel reason = %q, want %q", reasons[0], protocol.CancelUser)
	}
	if n := qg.gateway.ops.size(); n != 0 {
		t.Errorf("ops in flight after a cancellation = %d, want 0", n)
	}
}

// ── resume ──────────────────────────────────────────────────────────────────

// TestResumeReSendsTheOpThatNeverCameBack is "Reconnect and resume": a runner
// that drops its socket mid-op and comes back inside its lease is sent the same
// op.request again, so the engine call that is still waiting can be answered.
// The worker's own op cache is what makes the repeat safe.
func TestResumeReSendsTheOpThatNeverCameBack(t *testing.T) {
	qg := newQueueGateway(t, nil)
	r := qg.newQueueRunner(t, testOrgA)
	session := claimSession(t, qg, r, protocol.JobVideoCompile)
	startFakeRunner(t, session.ws, session.token, session.epoch, opHangs)

	go func() {
		// The engine's call stays blocked; the answer comes from the resumed
		// socket below, so its result is discarded here.
		postOp(t, qg, session.sessionID, `{"opId":"survives-reconnect","op":"exec","timeoutSec":30}`)
	}()
	waitFor(t, func() bool { return qg.gateway.ops.size() == 1 }, "the op to be in flight")

	// The laptop's socket dies without a goodbye, and it reconnects holding the
	// job.
	_ = session.ws.CloseNow()
	waitFor(t, func() bool { return qg.gateway.registry.get(r.deviceID) == nil }, "the dead socket to be unregistered")

	ws2 := qg.connectHello(t, desktopHello(t, r, qg.dialedHost(t), func(p *protocol.HelloPayload) {
		p.Running = []protocol.JobLease{{JobID: session.jobID, LeaseEpoch: session.epoch}}
	}))
	welcome := readPayload[protocol.WelcomePayload](t, readEnvelope(t, ws2))
	if len(welcome.Resumed) != 1 || welcome.Resumed[0].JobID != session.jobID {
		t.Fatalf("welcome.resumed = %+v, want the held job", welcome.Resumed)
	}

	// The re-sent op.request arrives after welcome; the runner answers it.
	runner := startFakeRunner(t, ws2, session.token, session.epoch, opEcho(`{"exitCode":0}`))
	waitFor(t, func() bool { return len(runner.receivedOps()) > 0 }, "the op to be re-sent")
	resent := runner.receivedOps()[0]
	if resent.OpID != "survives-reconnect" || resent.Op != "exec" {
		t.Fatalf("the re-sent op = %+v, want the op that had no response", resent)
	}
	// The counter is what makes a resume legible in production.
	waitFor(t, func() bool {
		return qg.metricValue(t, `reelbolt_runner_gateway_session_ops_total{result="resent"}`) == 1
	}, "the re-send to be counted")
}

// ── the scaling signal ──────────────────────────────────────────────────────

// TestScalingSignalCountsWorkAndRefusesToCountUselessRunners is the acceptance
// test for "the metrics are exposed" plus the trap the connected count hides: a
// runner that advertises NO job types (no ffmpeg, say) still connects, and must
// not be reported as available capacity.
func TestScalingSignalCountsWorkAndRefusesToCountUselessRunners(t *testing.T) {
	// The harness builds its config by hand, so the one value this test reads is
	// set explicitly; its production default is asserted by
	// TestLoadConfigDefaultsForTheScalingSignal below.
	qg := newQueueGateway(t, func(cfg *appConfig) { cfg.PoolBufferSlots = 1 })
	replicaAddress(t, qg)

	// Two CloudPool jobs waiting for a slot nobody can take.
	for i := 0; i < 2; i++ {
		qg.createJob(t, map[string]any{
			"orgId": testOrgA, "executionId": testExecA, "stepId": testStepID,
			"jobType": protocol.JobVideoCompile, "target": targetCloudPool,
			"purpose": fmt.Sprintf("pool-%d", i),
		})
	}

	// A healthy desktop runner with two free slots, and a useless one that
	// connects with no job types at all.
	healthy := qg.newQueueRunner(t, testOrgA)
	ws, _ := qg.handshake(t, healthy)
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 2}))

	useless := qg.newQueueRunner(t, testOrgA)
	uselessWS := qg.connectHello(t, desktopHello(t, useless, qg.dialedHost(t), func(p *protocol.HelloPayload) {
		// The landmine: a runner with no ffmpeg and no container runtime
		// advertises nothing and connects anyway.
		p.Capabilities.JobTypes = nil
		p.Capabilities.Container = protocol.ContainerNone
		p.Capabilities.RuntimeImageDigest = nil
		p.Capabilities.FfmpegVersion = ""
		p.Capabilities.Slots = 4
	}))
	if env := readEnvelope(t, uselessWS); env.Type != protocol.TypeWelcome {
		t.Fatalf("the useless runner was refused: %s", env.Type)
	}
	waitFor(t, func() bool { return qg.gateway.registry.len() == 2 }, "both runners to be connected")

	status, scaling := getScaling(t, qg)
	if status != http.StatusOK {
		t.Fatalf("GET /internal/v1/scaling = %d", status)
	}
	// desired = queued + running + buffer, the plan's formula.
	if scaling.Queued != 2 || scaling.Running != 0 {
		t.Errorf("scaling = %+v, want 2 queued and 0 running", scaling)
	}
	if scaling.Buffer != 1 {
		t.Errorf("buffer = %d, want the configured 1", scaling.Buffer)
	}
	if scaling.DesiredCloudPoolSlots != int(scaling.Queued+scaling.Running)+scaling.Buffer {
		t.Errorf("desiredCloudPoolSlots = %d, want queued+running+buffer", scaling.DesiredCloudPoolSlots)
	}
	if scaling.ConnectedCloudPool != 0 {
		t.Errorf("connectedCloudPool = %d, want 0 (both runners are desktops)", scaling.ConnectedCloudPool)
	}
	if scaling.RunnersWithoutJobTypes != 1 {
		t.Errorf("runnersWithoutJobTypes = %d, want 1", scaling.RunnersWithoutJobTypes)
	}
	// The healthy runner's two free slots are capacity; the useless runner's four
	// are not, however free it says they are.
	if scaling.FreeSlotsOrgRunner != 2 {
		t.Errorf("freeSlotsOrgRunner = %d, want the healthy runner's 2 and nothing from the useless one",
			scaling.FreeSlotsOrgRunner)
	}

	// The gauge a scaler scrapes says the same thing, because both read the one
	// capacity function. (The gauge itself is D5b's registration; D5c gives it
	// this meaning.)
	qg.gateway.updateConnectedGauges()
	if got := qg.metricValue(t, `reelbolt_runner_free_slots{target="OrgRunner"}`); got != 2 {
		t.Errorf("reelbolt_runner_free_slots{target=OrgRunner} = %v, want 2", got)
	}
	if got := qg.metricValue(t, `reelbolt_runner_connected{kind="Desktop"}`); got != 2 {
		t.Errorf("reelbolt_runner_connected{kind=Desktop} = %v, want 2 (both are connected)", got)
	}
}

// TestDrainingRunnerIsNotCapacity pins the same rule for a runner that is going
// away: it is still connected and takes no new work.
func TestDrainingRunnerIsNotCapacity(t *testing.T) {
	qg := newQueueGateway(t, nil)
	replicaAddress(t, qg)
	r := qg.newQueueRunner(t, testOrgA)
	ws, _ := qg.handshake(t, r)
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 3}))
	waitFor(t, func() bool { return qg.gateway.registry.len() == 1 }, "the runner to connect")

	if _, scaling := getScaling(t, qg); scaling.FreeSlotsOrgRunner != 3 {
		t.Fatalf("freeSlotsOrgRunner = %d, want 3 before the drain", scaling.FreeSlotsOrgRunner)
	}
	send(t, ws, mustEnvelope(t, protocol.TypeDrain, protocol.DrainPayload{Reason: protocol.CancelShutdown}))
	waitFor(t, func() bool { return qg.gateway.runnerCapacity().draining == 1 }, "the drain to be recorded")

	_, scaling := getScaling(t, qg)
	if scaling.FreeSlotsOrgRunner != 0 {
		t.Errorf("freeSlotsOrgRunner = %d, want 0 for a draining runner", scaling.FreeSlotsOrgRunner)
	}
	if scaling.DrainingRunners != 1 {
		t.Errorf("drainingRunners = %d, want 1", scaling.DrainingRunners)
	}
}

// TestQueueDepthMetricIsPublished covers the third name the plan lists under
// D5c: it exists, and it is the queue's own count by target and job type.
func TestQueueDepthMetricIsPublished(t *testing.T) {
	qg := newQueueGateway(t, nil)
	replicaAddress(t, qg)
	qg.createJob(t, map[string]any{
		"orgId": testOrgA, "executionId": testExecA, "stepId": testStepID,
		"jobType": protocol.JobVideoCompile, "target": targetCloudPool, "purpose": "depth",
	})
	if _, err := qg.jobs.QueueDepth(context.Background()); err != nil {
		t.Fatalf("QueueDepth: %v", err)
	}
	qg.gateway.refreshQueueMetrics(context.Background())
	// The reaper is off in tests, so the gauge is refreshed directly here; in a
	// deployment it is republished on every reaper pass.
	if got := qg.metricValue(t,
		`reelbolt_runner_queue_depth{job_type="video.compile",target="CloudPool"}`); got != 1 {
		t.Errorf("reelbolt_runner_queue_depth{video.compile,CloudPool} = %v, want 1", got)
	}
}

// ── one open session per execution ──────────────────────────────────────────

// TestSecondSessionForOneExecutionIsToleratedNotFixed pins the schema rule the
// whole design has to live with: runner_sessions carries a PARTIAL
// UNIQUE(execution_id) WHERE closed_at IS NULL, so two concurrent sessions for
// one execution cannot both have a row. The gateway tolerates the violation
// (the second job still runs, with no session row of its own) and does NOT try
// to fix it — making the engine serialise sessions per execution is D6's.
//
// What this surface must not do is LIE about it: the second job advertises no
// sessionId, so nothing upstream can address a session that does not exist.
func TestSecondSessionForOneExecutionIsToleratedNotFixed(t *testing.T) {
	qg := newQueueGateway(t, nil)
	otherStep := "9f8e7d6c-5b4a-4392-8170-6f5e4d3c2b12"
	first := qg.createJob(t, map[string]any{
		"orgId": testOrgA, "executionId": testExecA, "stepId": testStepID,
		"jobType": protocol.JobVideoCompile, "target": targetOrgRunner, "purpose": "first",
	})
	second := qg.createJob(t, map[string]any{
		"orgId": testOrgA, "executionId": testExecA, "stepId": otherStep,
		"jobType": protocol.JobVideoCompile, "target": targetOrgRunner, "purpose": "second",
	})

	runners := []*runner{qg.newQueueRunner(t, testOrgA), qg.newQueueRunner(t, testOrgA)}
	sockets := make([]*websocket.Conn, 2)
	assigns := make([]protocol.AssignPayload, 2)
	for i, r := range runners {
		sockets[i], _ = qg.handshake(t, r)
		send(t, sockets[i], mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
		env := readEnvelope(t, sockets[i])
		if env.Type != protocol.TypeAssign {
			t.Fatalf("runner %d got %s, want assign", i, env.Type)
		}
		assigns[i] = readPayload[protocol.AssignPayload](t, env)
		send(t, sockets[i], jobEnvelope(t, protocol.TypeAccept, env.JobID, protocol.AcceptPayload{
			LeaseEpoch: assigns[i].LeaseEpoch, JobToken: assigns[i].JobToken,
		}))
		waitForStatus(t, qg, env.JobID, jobRunning)
	}
	// Assignment order is created_at, so the fixture's first job went first.
	assignedFirst, status := qg.getJob(t, first.JobID, "")
	if status != http.StatusOK {
		t.Fatalf("GET first = %d", status)
	}
	assignedSecond, status := qg.getJob(t, second.JobID, "")
	if status != http.StatusOK {
		t.Fatalf("GET second = %d", status)
	}
	if assignedFirst.SessionID == "" {
		t.Error("the first job of the execution advertises no session")
	}
	if assignedSecond.Status != jobRunning {
		t.Fatalf("the second job is %q, want Running: the schema conflict must not strand it", assignedSecond.Status)
	}

	var open int
	if err := qg.pool.QueryRow(context.Background(),
		"SELECT count(*) FROM runner_sessions WHERE execution_id = $1::uuid AND closed_at IS NULL",
		testExecA).Scan(&open); err != nil {
		t.Fatalf("count open sessions: %v", err)
	}
	if open != 1 {
		t.Fatalf("open sessions for one execution = %d, want exactly 1", open)
	}
	if assignedSecond.SessionID != "" {
		t.Errorf("the second job advertises session %q, but no such row exists", assignedSecond.SessionID)
	}
	// And an address that does not exist is refused rather than invented.
	reqStatus, body, _ := postOp(t, qg, "0f0f0f0f-0000-4000-8000-00000000000f", `{"op":"status"}`)
	if reqStatus != http.StatusNotFound {
		t.Errorf("an op for a session with no row = %d %v, want 404", reqStatus, body)
	}

	// The second job still completes normally: the conflict costs it a session
	// row, not its work.
	send(t, sockets[1], jobEnvelope(t, protocol.TypeComplete, second.JobID, protocol.CompletePayload{
		LeaseEpoch: assigns[1].LeaseEpoch, JobToken: assigns[1].JobToken,
		Outcome: protocol.OutcomeSucceeded,
	}))
	waitForStatus(t, qg, second.JobID, jobSucceeded)
}

// ── the internal API stays closed ───────────────────────────────────────────

func TestSessionOpAndScalingRequireTheToken(t *testing.T) {
	qg := newQueueGateway(t, nil)
	for _, path := range []string{"/internal/v1/scaling", "/internal/v1/sessions/0f0f0f0f-0000-4000-8000-000000000000/ops"} {
		method := http.MethodGet
		if strings.HasSuffix(path, "/ops") {
			method = http.MethodPost
		}
		req, err := http.NewRequestWithContext(context.Background(), method, qg.server.URL+path, strings.NewReader("{}"))
		if err != nil {
			t.Fatalf("build request: %v", err)
		}
		resp, err := qg.client.Do(req)
		if err != nil {
			t.Fatalf("%s %s: %v", method, path, err)
		}
		resp.Body.Close()
		if resp.StatusCode != http.StatusUnauthorized {
			t.Errorf("%s %s without a token = %d, want 401", method, path, resp.StatusCode)
		}
	}

	// With no token configured at all the surface is closed, not open.
	closed := newQueueGateway(t, func(cfg *appConfig) { cfg.InternalToken = "" })
	status, body, _ := postOp(t, closed, "0f0f0f0f-0000-4000-8000-000000000000", "{}")
	if status != http.StatusServiceUnavailable {
		t.Fatalf("an unconfigured internal API = %d %v, want 503", status, body)
	}
}

// TestScalingSignalCountsAJobWhoseWorkerIsGoneAsDemand: a job that was assigned once and is reused
// or retried must wake a node like a first-time job. Queued alone misses the case where the
// job is still leased to a worker that has disappeared (it reads as running, so the waker saw
// "0 queued job(s)" and created nothing). awaitingWorker counts it; a job a CONNECTED worker holds
// and a finished job are not demand.
func TestScalingSignalCountsAJobWhoseWorkerIsGoneAsDemand(t *testing.T) {
	qg := newQueueGateway(t, func(cfg *appConfig) { cfg.PoolBufferSlots = 0 })
	instance := replicaAddress(t, qg)
	ctx := context.Background()

	mk := func(purpose string) string {
		return qg.createJob(t, map[string]any{
			"orgId": testOrgA, "executionId": testExecA, "stepId": testStepID,
			"jobType": protocol.JobVideoCompile, "target": targetCloudPool, "purpose": purpose,
		}).JobID
	}
	lease := func(id, status, owner string, expiresIn time.Duration, attempt int) {
		t.Helper()
		_, err := qg.pool.Exec(ctx, `UPDATE runner_jobs SET status = $2, attempt = $3,
			lease_owner_runner_id = $4::uuid, gateway_instance = $5, lease_expires_at = $6 WHERE id = $1::uuid`,
			id, status, attempt, owner, instance, time.Now().UTC().Add(expiresIn))
		if err != nil {
			t.Fatalf("lease %s: %v", id, err)
		}
	}
	const gone = "00000000-0000-4000-8000-0000000000aa"
	fresh := qg.gateway.cfg.leaseDuration() // renewed just now: well inside the lease
	stale := qg.gateway.cfg.leaseDuration() - 2*orphanGrace

	// Reused at attempt 1, leased to a runner that is not connected and stopped renewing.
	lease(mk("orphan"), jobRunning, gone, stale, 1)
	// Leased to a gone runner but renewed a moment ago: a reconnecting runner, not demand yet.
	lease(mk("flap"), jobAssigned, gone, fresh, 1)
	// Retried after a failure: back in the queue at attempt 2.
	queued := mk("requeued")
	if _, err := qg.pool.Exec(ctx, "UPDATE runner_jobs SET attempt = 2 WHERE id = $1::uuid", queued); err != nil {
		t.Fatal(err)
	}
	// Finished: never demand.
	done := mk("done")
	lease(done, jobRunning, gone, stale, 1)
	if _, err := qg.pool.Exec(ctx, "UPDATE runner_jobs SET status = $2 WHERE id = $1::uuid", done, jobSucceeded); err != nil {
		t.Fatal(err)
	}

	status, scaling := getScaling(t, qg)
	if status != http.StatusOK {
		t.Fatalf("GET /internal/v1/scaling = %d", status)
	}
	if scaling.Queued != 1 || scaling.Running != 2 || scaling.Orphaned != 1 || scaling.AwaitingWorker != 2 {
		t.Errorf("scaling = %+v, want queued 1 (the retried job), running 2, orphaned 1 (the unrenewed lease), awaitingWorker 2", scaling)
	}
}
