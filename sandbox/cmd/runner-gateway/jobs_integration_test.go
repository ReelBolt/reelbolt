package main

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"testing"
	"time"

	"github.com/coder/websocket"
	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/jackc/pgx/v5/pgxpool"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// These tests drive the whole D5b surface against a THROWAWAY Postgres: a real
// WebSocket runner, the real queue, the real leases and the real internal API.
// They run when RUNNER_GATEWAY_TEST_DATABASE_URL points at a database that may be
// destroyed (see queue_integration_test.go for the container command).

// queueGateway is the D5a test harness with a real queue behind it.
type queueGateway struct {
	*testGateway
	jobs     *PostgresJobStore
	devices  *PostgresDeviceStore
	settings *PostgresSwitchStore
	pool     *pgxpool.Pool
	client   *http.Client
	// clock drives the kill-switch cache, so a test can age it past its TTL
	// instead of sleeping out the window.
	clock *testClock
}

func newQueueGateway(t *testing.T, tweak func(*appConfig)) *queueGateway {
	t.Helper()
	url := os.Getenv("RUNNER_GATEWAY_TEST_DATABASE_URL")
	if url == "" {
		t.Skip("set RUNNER_GATEWAY_TEST_DATABASE_URL to run the gateway integration tests")
	}
	pool := queueTestDatabase(t)
	ctx := context.Background()

	cfg := appConfig{
		Port:               "0",
		DatabaseURL:        url,
		GatewayInstance:    "10.0.3.17",
		InternalToken:      "internal-test-token",
		HeartbeatSec:       protocol.HeartbeatSec,
		DeadAfterSec:       protocol.DeadAfterSec,
		HelloTimeout:       2 * time.Second,
		ClockSkew:          protocol.HelloSkewSec * time.Second,
		NonceTTL:           protocol.NonceTTLSeconds * time.Second,
		RevocationInterval: 0, // off: a test drives the sweep directly
		LastSeenInterval:   0, // always due, so a touch is observable immediately
		LeaseSec:           protocol.LeaseSec,
		AcceptTimeoutSec:   protocol.AcceptTimeoutSec,
		CancelGraceSec:     protocol.CancelGraceSec,
		ReaperInterval:     0, // off: a test calls the reaper as its own clock
		AssignInterval:     0, // off: ready and heartbeat drive assignment
	}
	if tweak != nil {
		tweak(&cfg)
	}

	devices, err := NewPostgresDeviceStore(ctx, url, 0)
	if err != nil {
		t.Fatalf("device store: %v", err)
	}
	jobs := &PostgresJobStore{pool: pool}
	tokens, err := newJobTokenSigner([]byte("0123456789abcdef0123456789abcdef"))
	if err != nil {
		t.Fatalf("token signer: %v", err)
	}
	g := newGateway(cfg, devices)
	g.attachQueue(jobs, tokens)
	// As main wires it. On a fixture whose inference tables are absent the
	// lookup fails and the assignment simply carries no display, which is
	// itself the rule display.go promises.
	g.attachDisplay(jobs)
	// The kill switches read the real platform_settings table in this database,
	// on a clock the test drives: flipping a switch mid-test then takes effect
	// without sleeping out the ten-second cache window.
	settings := &PostgresSwitchStore{pool: pool}
	clock := newTestClock()
	switches := newPlatformSwitches(settings, g.metrics)
	switches.now = clock.Now
	g.attachSwitches(switches)
	// The background loops (the revocation sweeper in particular) are part of what
	// is under test; the reaper and the assigner are turned off through the
	// config, so the tests drive them as their own clock.
	g.start(context.Background())

	router := mux.NewRouter()
	g.RegisterRoutes(router)
	server := httptest.NewServer(router)
	tg := &testGateway{gateway: g, server: server}
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
	return &queueGateway{
		testGateway: tg, jobs: jobs, devices: devices, settings: settings,
		pool: pool, client: server.Client(), clock: clock,
	}
}

// newQueueRunner registers a desktop device in the real registry.
func (qg *queueGateway) newQueueRunner(t *testing.T, orgID string) *runner {
	t.Helper()
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("generate key: %v", err)
	}
	deviceID := uuid.NewString()
	insertRunner(t, qg.pool, deviceID, orgID, protocol.KindDesktop, "queue-fixture-"+deviceID[:8],
		protocol.EncodeDevicePublicKey(pub))
	return &runner{deviceID: deviceID, priv: priv, orgID: orgID}
}

// createJob posts a job through the internal API, exactly as the engine will.
func (qg *queueGateway) createJob(t *testing.T, body map[string]any) jobView {
	t.Helper()
	raw, err := json.Marshal(body)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	req, err := http.NewRequestWithContext(context.Background(), http.MethodPost, qg.server.URL+"/internal/v1/jobs", bytes.NewReader(raw))
	if err != nil {
		t.Fatalf("request: %v", err)
	}
	req.Header.Set("Authorization", "Bearer internal-test-token")
	resp, err := qg.client.Do(req)
	if err != nil {
		t.Fatalf("POST /internal/v1/jobs: %v", err)
	}
	defer func() { _ = resp.Body.Close() }() // best-effort teardown
	if resp.StatusCode/100 != 2 {
		t.Fatalf("POST /internal/v1/jobs = %d", resp.StatusCode)
	}
	var view jobView
	if err := json.NewDecoder(resp.Body).Decode(&view); err != nil {
		t.Fatalf("decode job view: %v", err)
	}
	return view
}

func (qg *queueGateway) getJob(t *testing.T, jobID, query string) (jobView, int) {
	t.Helper()
	req, err := http.NewRequestWithContext(context.Background(), http.MethodGet, qg.server.URL+"/internal/v1/jobs/"+jobID+query, nil)
	if err != nil {
		t.Fatalf("request: %v", err)
	}
	req.Header.Set("Authorization", "Bearer internal-test-token")
	resp, err := qg.client.Do(req)
	if err != nil {
		t.Fatalf("GET job: %v", err)
	}
	defer func() { _ = resp.Body.Close() }() // best-effort teardown
	var view jobView
	_ = json.NewDecoder(resp.Body).Decode(&view)
	return view, resp.StatusCode
}

// ── the full happy path ─────────────────────────────────────────────────────

func TestQueueAssignAcceptHeartbeatComplete(t *testing.T) {
	qg := newQueueGateway(t, nil)
	ctx := context.Background()
	r := qg.newQueueRunner(t, testOrgA)

	view := qg.createJob(t, map[string]any{
		"orgId": testOrgA, "executionId": testExecA, "stepId": testStepID,
		"jobType": protocol.JobVideoCompile, "target": targetOrgRunner,
		"purpose": "video.compile",
		"spec":    map[string]any{"executionId": testExecA, "stepId": testStepID, "mode": "Reencode"},
	})
	if !view.Created || view.Status != jobQueued {
		t.Fatalf("created job = %+v", view)
	}

	ws, welcome := qg.handshake(t, r)
	if welcome.RunnerID != r.deviceID {
		t.Fatalf("welcome.runnerId = %q", welcome.RunnerID)
	}

	// ready{freeSlots} is what makes the gateway claim work for this runner.
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
	assignEnv := readEnvelope(t, ws)
	if assignEnv.Type != protocol.TypeAssign {
		t.Fatalf("first message after ready is %s, want assign", assignEnv.Type)
	}
	if assignEnv.JobID != view.JobID {
		t.Fatalf("assigned %s, want %s", assignEnv.JobID, view.JobID)
	}
	assign := readPayload[protocol.AssignPayload](t, assignEnv)
	if assign.JobType != protocol.JobVideoCompile || assign.LeaseEpoch != 1 || assign.Attempt != 1 {
		t.Errorf("assign = %+v", assign)
	}
	if assign.JobToken == "" {
		t.Fatal("assign carries no job token")
	}
	if assign.OrgID != testOrgA {
		t.Errorf("assign.orgId = %q, want %q", assign.OrgID, testOrgA)
	}
	if assign.Limits.MaxOutputBytes == 0 || assign.Limits.IdleTimeoutSec == 0 {
		t.Errorf("assign.limits = %+v", assign.Limits)
	}
	var spec protocol.VideoCompileSpec
	if err := assign.SpecInto(&spec); err != nil {
		t.Fatalf("assign.spec: %v", err)
	}
	if spec.ExecutionID != testExecA || spec.StepID != testStepID || spec.Mode != "Reencode" {
		t.Errorf("assign.spec = %+v", spec)
	}

	// The job is Assigned at epoch 1, owned by this runner, with a session row on
	// this replica (D5c routes ops with it).
	job, err := qg.jobs.GetJob(ctx, view.JobID)
	if err != nil {
		t.Fatalf("GetJob: %v", err)
	}
	if job.Status != jobAssigned || job.LeaseEpoch != 1 || job.LeaseOwnerRunnerID != r.deviceID {
		t.Fatalf("job = %+v", job)
	}
	if job.GatewayInstance != "10.0.3.17" {
		t.Errorf("gateway_instance = %q", job.GatewayInstance)
	}
	var sessionRunner, sessionInstance string
	var closedAt *time.Time
	err = qg.pool.QueryRow(ctx,
		`SELECT runner_id::text, gateway_instance, closed_at FROM runner_sessions WHERE job_id = $1::uuid`,
		view.JobID).Scan(&sessionRunner, &sessionInstance, &closedAt)
	if err != nil {
		t.Fatalf("read the session: %v", err)
	}
	if sessionRunner != r.deviceID || sessionInstance != "10.0.3.17" || closedAt != nil {
		t.Errorf("session runner=%q instance=%q closed=%v", sessionRunner, sessionInstance, closedAt)
	}

	// accept moves it to Running.
	send(t, ws, jobEnvelope(t, protocol.TypeAccept, view.JobID, protocol.AcceptPayload{
		LeaseEpoch: assign.LeaseEpoch, JobToken: assign.JobToken,
	}))
	waitForStatus(t, qg, view.JobID, jobRunning)
	if got := qg.jobFor(t, view.JobID).Status; got != jobRunning {
		t.Fatalf("status = %q, want Running", got)
	}

	// A heartbeat listing the job renews its lease and comes back ok:true.
	before := qg.jobFor(t, view.JobID).LeaseExpiresAt
	held := protocol.JobLease{JobID: view.JobID, LeaseEpoch: assign.LeaseEpoch}
	send(t, ws, mustEnvelope(t, protocol.TypeHeartbeat, protocol.HeartbeatPayload{
		FreeSlots: intPtr(0), Running: []protocol.JobLease{held},
	}))
	answer := readEnvelope(t, ws)
	hb := readPayload[protocol.HeartbeatPayload](t, answer)
	if len(hb.Leases) != 1 || !hb.Leases[0].OK {
		t.Fatalf("heartbeat.leases = %+v, want ok:true for the held job", hb.Leases)
	}
	after := qg.jobFor(t, view.JobID).LeaseExpiresAt
	if !after.After(before) {
		t.Errorf("lease_expires_at did not advance: %v -> %v", before, after)
	}

	// complete ends it.
	send(t, ws, jobEnvelope(t, protocol.TypeComplete, view.JobID, protocol.CompletePayload{
		LeaseEpoch: assign.LeaseEpoch, JobToken: assign.JobToken,
		Outcome: protocol.OutcomeSucceeded, Result: json.RawMessage(`{"stagingKey":"runner-staging/x"}`),
	}))
	waitForStatus(t, qg, view.JobID, jobSucceeded)
	final := qg.jobFor(t, view.JobID)
	if final.CompletedAt.IsZero() {
		t.Error("a succeeded job has no completed_at")
	}
	if final.ResultJSON == "" {
		t.Error("the result was not recorded")
	}
	// The session is closed with the job.
	var closed *time.Time
	if err := qg.pool.QueryRow(ctx,
		`SELECT closed_at FROM runner_sessions WHERE job_id = $1::uuid`, view.JobID).Scan(&closed); err != nil {
		t.Fatalf("read the session: %v", err)
	}
	if closed == nil {
		t.Error("the session was left open after the job succeeded")
	}

	// The engine's long-poll sees the terminal state.
	polled, status := qg.getJob(t, view.JobID, "?wait=1s")
	if status != http.StatusOK || polled.Status != jobSucceeded {
		t.Errorf("long-poll = %d %+v", status, polled)
	}
}

// ── fencing over the wire ───────────────────────────────────────────────────

func TestQueueStaleEpochIsRejectedOverTheWire(t *testing.T) {
	qg := newQueueGateway(t, nil)
	r := qg.newQueueRunner(t, testOrgA)
	view := qg.createJob(t, map[string]any{
		"orgId": testOrgA, "executionId": testExecA,
		"jobType": protocol.JobVideoCompile, "target": targetOrgRunner,
	})
	ws, _ := qg.handshake(t, r)
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
	assign := readPayload[protocol.AssignPayload](t, readEnvelope(t, ws))
	send(t, ws, jobEnvelope(t, protocol.TypeAccept, view.JobID, protocol.AcceptPayload{
		LeaseEpoch: assign.LeaseEpoch, JobToken: assign.JobToken,
	}))
	waitForStatus(t, qg, view.JobID, jobRunning)

	// A forged token is refused.
	send(t, ws, jobEnvelope(t, protocol.TypeComplete, view.JobID, protocol.CompletePayload{
		LeaseEpoch: assign.LeaseEpoch, JobToken: "FIXTURE.job.token", Outcome: protocol.OutcomeSucceeded,
	}))
	expectCancel(t, ws, protocol.CancelLeaseLost)

	// So is a stale epoch.
	send(t, ws, jobEnvelope(t, protocol.TypeFail, view.JobID, protocol.FailPayload{
		LeaseEpoch: assign.LeaseEpoch - 1, JobToken: assign.JobToken,
		Class: protocol.FailRunner, Code: "E_STALE", Message: "stale", Retryable: true,
	}))
	expectCancel(t, ws, protocol.CancelLeaseLost)

	// The job is untouched, so the real owner can still finish it.
	job := qg.jobFor(t, view.JobID)
	if job.Status != jobRunning || job.LeaseEpoch != 1 {
		t.Fatalf("a fenced message changed the job: %+v", job)
	}
	send(t, ws, jobEnvelope(t, protocol.TypeComplete, view.JobID, protocol.CompletePayload{
		LeaseEpoch: assign.LeaseEpoch, JobToken: assign.JobToken, Outcome: protocol.OutcomeSucceeded,
	}))
	waitForStatus(t, qg, view.JobID, jobSucceeded)
}

// ── routing over the wire ───────────────────────────────────────────────────

func TestQueueRoutingNeverCrossAssigns(t *testing.T) {
	qg := newQueueGateway(t, nil)
	r := qg.newQueueRunner(t, testOrgA)

	// A job for another organisation, and a CloudPool job.
	otherOrg := qg.createJob(t, map[string]any{
		"orgId": testOrgB, "executionId": testExecB,
		"jobType": protocol.JobVideoCompile, "target": targetOrgRunner,
	})
	poolJob := qg.createJob(t, map[string]any{
		"orgId": testOrgB, "executionId": testExecB,
		"jobType": protocol.JobVideoCompile, "target": targetCloudPool,
	})

	ws, _ := qg.handshake(t, r)
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 2}))
	// Nothing eligible: the runner must not be handed another organisation's job
	// or a pool job.
	time.Sleep(150 * time.Millisecond)
	if status := qg.jobFor(t, otherOrg.JobID).Status; status != jobQueued {
		t.Fatalf("an org-B job is %q on an org-A runner", status)
	}
	if status := qg.jobFor(t, poolJob.JobID).Status; status != jobQueued {
		t.Fatalf("a CloudPool job is %q on a desktop runner", status)
	}

	// Its own organisation's job is assigned straight away.
	mine := qg.createJob(t, map[string]any{
		"orgId": testOrgA, "executionId": testExecA,
		"jobType": protocol.JobVideoCompile, "target": targetOrgRunner,
	})
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 2}))
	env := readEnvelope(t, ws)
	if env.Type != protocol.TypeAssign || env.JobID != mine.JobID {
		t.Fatalf("assigned %s/%s, want an assign of %s", env.Type, env.JobID, mine.JobID)
	}
}

// ── cancellation within the grace window ────────────────────────────────────

func TestQueueCancellationWithinGraceWindow(t *testing.T) {
	qg := newQueueGateway(t, nil)
	r := qg.newQueueRunner(t, testOrgA)
	view := qg.createJob(t, map[string]any{
		"orgId": testOrgA, "executionId": testExecA,
		"jobType": protocol.JobVideoCompile, "target": targetOrgRunner,
	})
	ws, _ := qg.handshake(t, r)
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
	assign := readPayload[protocol.AssignPayload](t, readEnvelope(t, ws))
	send(t, ws, jobEnvelope(t, protocol.TypeAccept, view.JobID, protocol.AcceptPayload{
		LeaseEpoch: assign.LeaseEpoch, JobToken: assign.JobToken,
	}))
	waitForStatus(t, qg, view.JobID, jobRunning)

	req, err := http.NewRequestWithContext(context.Background(), http.MethodDelete, qg.server.URL+"/internal/v1/jobs/"+view.JobID, nil)
	if err != nil {
		t.Fatalf("request: %v", err)
	}
	req.Header.Set("Authorization", "Bearer internal-test-token")
	resp, err := qg.client.Do(req)
	if err != nil {
		t.Fatalf("DELETE: %v", err)
	}
	defer func() { _ = resp.Body.Close() }() // best-effort teardown
	if resp.StatusCode != http.StatusAccepted {
		t.Fatalf("DELETE = %d, want 202", resp.StatusCode)
	}

	cancelEnv := readEnvelope(t, ws)
	if cancelEnv.Type != protocol.TypeCancel {
		t.Fatalf("got %s, want cancel", cancelEnv.Type)
	}
	cancel := readPayload[protocol.CancelPayload](t, cancelEnv)
	if cancel.Reason != protocol.CancelUser || cancel.GraceSec != protocol.CancelGraceSec {
		t.Errorf("cancel = %+v", cancel)
	}

	// The runner acknowledges inside the window: the job is Cancelled at once,
	// not after the grace window.
	send(t, ws, jobEnvelope(t, protocol.TypeComplete, view.JobID, protocol.CompletePayload{
		LeaseEpoch: assign.LeaseEpoch, JobToken: assign.JobToken, Outcome: protocol.OutcomeCancelled,
	}))
	waitForStatus(t, qg, view.JobID, jobCancelled)
	// And a later message with that epoch is discarded without a new cancel.
	send(t, ws, jobEnvelope(t, protocol.TypeComplete, view.JobID, protocol.CompletePayload{
		LeaseEpoch: assign.LeaseEpoch, JobToken: assign.JobToken, Outcome: protocol.OutcomeSucceeded,
	}))
	time.Sleep(100 * time.Millisecond)
	if status := qg.jobFor(t, view.JobID).Status; status != jobCancelled {
		t.Fatalf("status = %q, want the cancelled state kept", status)
	}
}

// ── resume ─────────────────────────────────────────────────────────────────

func TestQueueResumesAJobWhoseLeaseIsStillHeld(t *testing.T) {
	qg := newQueueGateway(t, nil)
	r := qg.newQueueRunner(t, testOrgA)
	view := qg.createJob(t, map[string]any{
		"orgId": testOrgA, "executionId": testExecA,
		"jobType": protocol.JobRemotionWorkspace, "target": targetOrgRunner,
	})
	ws, _ := qg.handshake(t, r)
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
	assign := readPayload[protocol.AssignPayload](t, readEnvelope(t, ws))

	// The laptop drops its socket without telling anyone and comes back inside
	// the 45-second lease, holding the job.
	_ = ws.CloseNow()

	ws2 := qg.connectHello(t, desktopHello(t, r, qg.dialedHost(t), func(p *protocol.HelloPayload) {
		p.Running = []protocol.JobLease{{JobID: view.JobID, LeaseEpoch: assign.LeaseEpoch}}
	}))
	welcome := readPayload[protocol.WelcomePayload](t, readEnvelope(t, ws2))
	if len(welcome.Resumed) != 1 || welcome.Resumed[0].JobID != view.JobID ||
		welcome.Resumed[0].LeaseEpoch != assign.LeaseEpoch {
		t.Fatalf("welcome.resumed = %+v, want the held job kept", welcome.Resumed)
	}
	if status := qg.jobFor(t, view.JobID).Status; status != jobAssigned {
		t.Errorf("status = %q, want the job still Assigned", status)
	}
}

func TestQueueDoesNotResumeAJobWhoseLeaseIsGone(t *testing.T) {
	qg := newQueueGateway(t, nil)
	ctx := context.Background()
	r := qg.newQueueRunner(t, testOrgA)
	view := qg.createJob(t, map[string]any{
		"orgId": testOrgA, "executionId": testExecA,
		"jobType": protocol.JobRemotionWorkspace, "target": targetOrgRunner,
	})
	ws, _ := qg.handshake(t, r)
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
	assign := readPayload[protocol.AssignPayload](t, readEnvelope(t, ws))
	_ = ws.CloseNow()

	// The lease runs out and the reaper takes the job back: a session job goes to
	// Expired, so there is nothing to resume.
	expired, err := qg.jobs.ExpireLeases(ctx, time.Now().UTC().Add(time.Duration(protocol.LeaseSec+5)*time.Second))
	if err != nil || len(expired) != 1 {
		t.Fatalf("ExpireLeases = %+v, %v", expired, err)
	}

	ws2 := qg.connectHello(t, desktopHello(t, r, qg.dialedHost(t), func(p *protocol.HelloPayload) {
		p.Running = []protocol.JobLease{{JobID: view.JobID, LeaseEpoch: assign.LeaseEpoch}}
	}))
	welcome := readPayload[protocol.WelcomePayload](t, readEnvelope(t, ws2))
	if len(welcome.Resumed) != 0 {
		t.Fatalf("welcome.resumed = %+v, want nothing", welcome.Resumed)
	}
	cancelEnv := readEnvelope(t, ws2)
	if cancelEnv.Type != protocol.TypeCancel || cancelEnv.JobID != view.JobID {
		t.Fatalf("got %s/%s, want cancel for %s", cancelEnv.Type, cancelEnv.JobID, view.JobID)
	}
	cancel := readPayload[protocol.CancelPayload](t, cancelEnv)
	if cancel.Reason != protocol.CancelLeaseLost {
		t.Errorf("cancel.reason = %q, want %q", cancel.Reason, protocol.CancelLeaseLost)
	}
}

// ── the accept watchdog ────────────────────────────────────────────────────

func TestQueueReleasesAJobNobodyAccepted(t *testing.T) {
	qg := newQueueGateway(t, func(cfg *appConfig) {
		// The window is a second so the test does not wait ten.
		cfg.AcceptTimeoutSec = 1
	})
	r := qg.newQueueRunner(t, testOrgA)
	view := qg.createJob(t, map[string]any{
		"orgId": testOrgA, "executionId": testExecA,
		"jobType": protocol.JobVideoCompile, "target": targetOrgRunner,
	})
	ws, _ := qg.handshake(t, r)
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
	if env := readEnvelope(t, ws); env.Type != protocol.TypeAssign {
		t.Fatalf("got %s, want assign", env.Type)
	}
	// No accept: the gateway treats the silence as a reject and releases the
	// lease, without consuming an attempt.
	waitForStatus(t, qg, view.JobID, jobQueued)
	job := qg.jobFor(t, view.JobID)
	if job.Attempt != 0 {
		t.Errorf("attempt = %d, want 0 after an unanswered assignment", job.Attempt)
	}
	if job.LeaseOwnerRunnerID != "" {
		t.Errorf("the lease is still held by %q", job.LeaseOwnerRunnerID)
	}
}

// ── the internal API ────────────────────────────────────────────────────────

func TestInternalAPIRequiresTheToken(t *testing.T) {
	qg := newQueueGateway(t, nil)
	body := []byte(`{"orgId":"` + testOrgA + `","executionId":"` + testExecA +
		`","jobType":"video.compile","target":"OrgRunner"}`)

	req, _ := http.NewRequestWithContext(context.Background(), http.MethodPost, qg.server.URL+"/internal/v1/jobs", bytes.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	resp, err := qg.client.Do(req)
	if err != nil {
		t.Fatalf("POST: %v", err)
	}
	_ = resp.Body.Close() // best-effort teardown
	if resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("an unauthenticated POST = %d, want 401", resp.StatusCode)
	}

	req, _ = http.NewRequestWithContext(context.Background(), http.MethodPost, qg.server.URL+"/internal/v1/jobs", bytes.NewReader(body))
	req.Header.Set("Authorization", "Bearer wrong-token")
	resp, err = qg.client.Do(req)
	if err != nil {
		t.Fatalf("POST: %v", err)
	}
	_ = resp.Body.Close() // best-effort teardown
	if resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("a wrongly authenticated POST = %d, want 401", resp.StatusCode)
	}

	// A gateway with no configured token refuses the whole surface.
	closed := newQueueGateway(t, func(cfg *appConfig) { cfg.InternalToken = "" })
	req, _ = http.NewRequestWithContext(context.Background(), http.MethodPost, closed.server.URL+"/internal/v1/jobs", bytes.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	resp, err = closed.client.Do(req)
	if err != nil {
		t.Fatalf("POST: %v", err)
	}
	_ = resp.Body.Close() // best-effort teardown
	if resp.StatusCode != http.StatusServiceUnavailable {
		t.Fatalf("POST with no token configured = %d, want 503", resp.StatusCode)
	}
}

func TestInternalAPIDuplicateIdempotencyKeyReturnsTheSameJob(t *testing.T) {
	qg := newQueueGateway(t, nil)
	key := IdempotencyKey(testExecA, testStepID, 3, "video.compile")
	body := map[string]any{
		"idempotencyKey": key, "orgId": testOrgA, "executionId": testExecA, "stepId": testStepID,
		"jobType": protocol.JobVideoCompile, "target": targetOrgRunner,
	}
	first := qg.createJob(t, body)
	second := qg.createJob(t, body)
	if first.JobID != second.JobID {
		t.Fatalf("two POSTs with one key created %s and %s", first.JobID, second.JobID)
	}
	if !first.Created || second.Created {
		t.Errorf("created flags = %v then %v, want true then false", first.Created, second.Created)
	}
	var count int
	if err := qg.pool.QueryRow(context.Background(),
		"SELECT count(*) FROM runner_jobs WHERE idempotency_key = $1", key).Scan(&count); err != nil {
		t.Fatalf("count: %v", err)
	}
	if count != 1 {
		t.Errorf("%d rows carry the key, want 1", count)
	}
}

func TestInternalAPIReportsOnlineRunners(t *testing.T) {
	qg := newQueueGateway(t, nil)
	r := qg.newQueueRunner(t, testOrgA)
	ws, _ := qg.handshake(t, r)
	defer func() { _ = ws.CloseNow() }()

	get := func(query string) runnersResponse {
		t.Helper()
		req, err := http.NewRequestWithContext(context.Background(), http.MethodGet, qg.server.URL+"/internal/v1/runners"+query, nil)
		if err != nil {
			t.Fatalf("request: %v", err)
		}
		req.Header.Set("Authorization", "Bearer internal-test-token")
		resp, err := qg.client.Do(req)
		if err != nil {
			t.Fatalf("GET runners: %v", err)
		}
		defer func() { _ = resp.Body.Close() }() // best-effort teardown
		var out runnersResponse
		if err := json.NewDecoder(resp.Body).Decode(&out); err != nil {
			t.Fatalf("decode: %v", err)
		}
		return out
	}

	if got := get("?org=" + testOrgA); got.Online != 1 || got.Runners[0].RunnerID != r.deviceID {
		t.Fatalf("online for its own org = %+v", got)
	}
	// Another organisation sees nothing, and so does a capability query the
	// runner cannot satisfy.
	if got := get("?org=" + testOrgB); got.Online != 0 {
		t.Errorf("online for another org = %+v", got)
	}
	if got := get("?caps=" + protocol.JobRemotionWorkspace + "," + protocol.JobVideoAnalyzeEx); got.Online != 1 {
		t.Errorf("a capability query the runner satisfies reported %d", got.Online)
	}
	if got := get("?caps=" + testOneShot); got.Online != 0 {
		t.Errorf("a capability query the runner cannot satisfy reported %d", got.Online)
	}
}

// ── revocation ─────────────────────────────────────────────────────────────

func TestRevocationCancelsTheDeviceJobsAndClosesTheSocket(t *testing.T) {
	qg := newQueueGateway(t, func(cfg *appConfig) { cfg.RevocationInterval = 20 * time.Millisecond })
	ctx := context.Background()
	r := qg.newQueueRunner(t, testOrgA)
	view := qg.createJob(t, map[string]any{
		"orgId": testOrgA, "executionId": testExecA,
		"jobType": protocol.JobRemotionWorkspace, "target": targetOrgRunner,
	})
	ws, _ := qg.handshake(t, r)
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
	assign := readPayload[protocol.AssignPayload](t, readEnvelope(t, ws))
	send(t, ws, jobEnvelope(t, protocol.TypeAccept, view.JobID, protocol.AcceptPayload{
		LeaseEpoch: assign.LeaseEpoch, JobToken: assign.JobToken,
	}))
	waitForStatus(t, qg, view.JobID, jobRunning)

	// The device is revoked in the registry; the sweeper must notice within its
	// interval, cancel the job, say goodbye and close with 4401.
	if _, err := qg.pool.Exec(ctx,
		"UPDATE runner_devices SET revoked_at = now() WHERE id = $1::uuid", r.deviceID); err != nil {
		t.Fatalf("revoke: %v", err)
	}

	seen, code := readUntilClose(t, ws)
	if code != protocol.CloseUnauthenticated {
		t.Fatalf("close code = %d, want 4401", code)
	}
	var sawCancel, sawBye bool
	for _, env := range seen {
		switch env.Type {
		case protocol.TypeCancel:
			if env.JobID != view.JobID {
				t.Errorf("cancel for %s, want %s", env.JobID, view.JobID)
				continue
			}
			if payload := readPayload[protocol.CancelPayload](t, env); payload.Reason != protocol.CancelRevoked {
				t.Errorf("cancel.reason = %q, want %q", payload.Reason, protocol.CancelRevoked)
			}
			sawCancel = true
		case protocol.TypeBye:
			sawBye = true
		}
	}
	if !sawCancel {
		t.Error("no cancel was sent to the revoked device")
	}
	if !sawBye {
		t.Error("no bye was sent to the revoked device")
	}
	// A session job whose device was revoked is Expired, and its lease is gone.
	// The assertions wait for the state rather than assume it is already
	// committed: the close frame is sent after the jobs are disposed of, but the
	// writes are made by a goroutine this connection does not own.
	waitForStatus(t, qg, view.JobID, jobExpired)
	job := qg.jobFor(t, view.JobID)
	if job.Status != jobExpired {
		t.Errorf("status = %q, want Expired", job.Status)
	}
	if job.LeaseOwnerRunnerID != "" {
		t.Errorf("the revoked device still owns the job")
	}
}

// ── the RunnerDeviceRevoked event ──────────────────────────────────────────

func TestDecodeRevocation(t *testing.T) {
	deviceID := uuid.NewString()
	wrapped := fmt.Sprintf(
		`{"messageId":"x","messageType":["urn:message:%s"],"message":{"deviceId":%q,"organizationId":%q,"revokedAt":"2026-10-08T09:00:00Z"}}`,
		exchangeRunnerDeviceRevoked, deviceID, testOrgA)
	event, err := decodeRevocation([]byte(wrapped))
	if err != nil {
		t.Fatalf("decode wrapped: %v", err)
	}
	if event.DeviceID != deviceID || event.OrganizationID != testOrgA {
		t.Errorf("event = %+v", event)
	}

	// A bare payload is accepted too: a producer that is not MassTransit must not
	// have to imitate the envelope.
	bare := fmt.Sprintf(`{"deviceId":%q,"organizationId":%q}`, deviceID, testOrgA)
	if event, err := decodeRevocation([]byte(bare)); err != nil || event.DeviceID != deviceID {
		t.Errorf("decode bare = %+v, %v", event, err)
	}

	// Anything without a usable device id is refused rather than acted on.
	for _, body := range []string{
		`{"messageId":"x","message":{"deviceId":"not-a-uuid"}}`,
		`{}`,
		`not json`,
	} {
		if _, err := decodeRevocation([]byte(body)); err == nil {
			t.Errorf("decodeRevocation(%q) was accepted", body)
		}
	}
}

// TestRevokedDeviceEventDropsTheSocket drives the event path end to end without a
// broker: it is exactly what the consumer does with a delivery.
func TestRevokedDeviceEventDropsTheSocket(t *testing.T) {
	qg := newQueueGateway(t, nil)
	r := qg.newQueueRunner(t, testOrgA)
	victim := qg.createJob(t, map[string]any{
		"orgId": testOrgA, "executionId": testExecA,
		"jobType": protocol.JobVideoCompile, "target": targetOrgRunner,
	})
	ws, _ := qg.handshake(t, r)
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
	assign := readPayload[protocol.AssignPayload](t, readEnvelope(t, ws))
	send(t, ws, jobEnvelope(t, protocol.TypeAccept, victim.JobID, protocol.AcceptPayload{
		LeaseEpoch: assign.LeaseEpoch, JobToken: assign.JobToken,
	}))
	waitForStatus(t, qg, victim.JobID, jobRunning)

	qg.gateway.handleRevokedDevice(context.Background(), r.deviceID, "event")

	seen, code := readUntilClose(t, ws)
	if code != protocol.CloseUnauthenticated {
		t.Fatalf("close code = %d, want 4401", code)
	}
	var sawCancel bool
	for _, env := range seen {
		if env.Type == protocol.TypeCancel {
			if payload := readPayload[protocol.CancelPayload](t, env); payload.Reason != protocol.CancelRevoked {
				t.Errorf("cancel.reason = %q, want %q", payload.Reason, protocol.CancelRevoked)
			}
			sawCancel = true
		}
	}
	if !sawCancel {
		t.Error("the event path sent no cancel")
	}
	waitForStatus(t, qg, victim.JobID, jobExpired)
	if status := qg.jobFor(t, victim.JobID).Status; status != jobExpired {
		t.Errorf("status = %q, want Expired", status)
	}
}

// ── helpers ─────────────────────────────────────────────────────────────────

func (qg *queueGateway) jobFor(t *testing.T, jobID string) Job {
	t.Helper()
	job, err := qg.jobs.GetJob(context.Background(), jobID)
	if err != nil {
		t.Fatalf("GetJob(%s): %v", jobID, err)
	}
	return job
}

// waitForStatus polls the job row until it reaches want. The gateway's
// transitions are asynchronous with respect to the socket, so a test that wants
// a state waits for it rather than assuming it is already written.
func waitForStatus(t *testing.T, qg *queueGateway, jobID, want string) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	var last string
	for time.Now().Before(deadline) {
		job, err := qg.jobs.GetJob(context.Background(), jobID)
		if err != nil {
			t.Fatalf("GetJob: %v", err)
		}
		if job.Status == want {
			return
		}
		last = job.Status
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("job %s stayed %q, want %q", jobID, last, want)
}

// jobEnvelope builds a job-scoped message: the envelope must carry jobId, which
// is what the fence checks the token's claims against.
func jobEnvelope(t *testing.T, ty protocol.MessageType, jobID string, payload any) protocol.Envelope {
	t.Helper()
	env, err := protocol.NewJob(ty, jobID, "", payload)
	if err != nil {
		t.Fatalf("NewJob(%s): %v", ty, err)
	}
	return env
}

// expectCancel reads the next message and requires it to be a cancel with the
// given reason.
func expectCancel(t *testing.T, ws *websocket.Conn, reason string) {
	t.Helper()
	env := readEnvelope(t, ws)
	if env.Type != protocol.TypeCancel {
		t.Fatalf("got %s, want cancel{reason:%s}", env.Type, reason)
	}
	payload := readPayload[protocol.CancelPayload](t, env)
	if payload.Reason != reason {
		t.Fatalf("cancel.reason = %q, want %q", payload.Reason, reason)
	}
}

func intPtr(n int) *int { return &n }

// ── the platform kill switches (F11) ────────────────────────────────────────

// TestNoWorkIsAssignedWhileComputeIsPaused is F11's acceptance criterion seen
// from the runner's side: a paused platform hands out nothing, and the queued
// work is still there when the switch is released. The queue is durable, so this
// is a pause and not a loss, and the whole round trip is what proves it.
func TestNoWorkIsAssignedWhileComputeIsPaused(t *testing.T) {
	qg := newQueueGateway(t, nil)
	r := qg.newQueueRunner(t, testOrgA)
	view := qg.createJob(t, map[string]any{
		"orgId": testOrgA, "executionId": testExecA, "stepId": testStepID,
		"jobType": protocol.JobVideoCompile, "target": targetOrgRunner,
	})
	setPlatformSetting(t, qg.pool, settingsKeyCompute, true, nil)

	ws, _ := qg.handshake(t, r)
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
	expectNoMessage(t, ws, 500*time.Millisecond)

	if status := qg.jobFor(t, view.JobID).Status; status != jobQueued {
		t.Errorf("job status = %q, want Queued: pausing must not move or lose the work", status)
	}
	if got := qg.metricValue(t, `reelbolt_runner_gateway_claims_deferred_total{reason="compute_paused"}`); got < 1 {
		t.Errorf("claims_deferred{reason=compute_paused} = %v, want at least 1", got)
	}

	// Releasing the switch takes effect within the cache TTL. The TTL is moved
	// rather than waited out: the fixture's clock is the gateway's own seam.
	setPlatformSetting(t, qg.pool, settingsKeyCompute, false, nil)
	qg.clock.advance(switchCacheTTL)

	// A fresh connection, because the "nothing arrived" read above gave up this
	// client's socket by design.
	released, _ := qg.handshake(t, r)
	send(t, released, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
	assign := readEnvelope(t, released)
	if assign.Type != protocol.TypeAssign || assign.JobID != view.JobID {
		t.Fatalf("after the switch was released the runner got %s for %s, want assign for %s",
			assign.Type, assign.JobID, view.JobID)
	}
	waitForStatus(t, qg, view.JobID, jobAssigned)
}

// TestNoWorkIsAssignedWhileTheSettingsAreUnreadable is F11 rule 2 end to end. A
// missing table is one of the shapes the rule names, and it is the shape a
// deployment that has not run the migration presents — so it is the shape that
// must refuse work rather than assign it blind.
func TestNoWorkIsAssignedWhileTheSettingsAreUnreadable(t *testing.T) {
	qg := newQueueGateway(t, nil)
	r := qg.newQueueRunner(t, testOrgA)
	view := qg.createJob(t, map[string]any{
		"orgId": testOrgA, "executionId": testExecA, "stepId": testStepID,
		"jobType": protocol.JobVideoCompile, "target": targetOrgRunner,
	})
	if _, err := qg.pool.Exec(context.Background(), "DROP TABLE platform_settings"); err != nil {
		t.Fatalf("drop platform_settings: %v", err)
	}

	// The hello is still accepted: an unreadable store must not refuse a runner
	// it cannot judge (see the handshake note in switches.go).
	ws, welcome := qg.handshake(t, r)
	if welcome.RunnerID != r.deviceID {
		t.Fatalf("welcome.runnerId = %q, want %q", welcome.RunnerID, r.deviceID)
	}
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
	expectNoMessage(t, ws, 500*time.Millisecond)

	if status := qg.jobFor(t, view.JobID).Status; status != jobQueued {
		t.Errorf("job status = %q, want Queued", status)
	}
	if got := qg.metricValue(t, `reelbolt_runner_gateway_claims_deferred_total{reason="switches_unreadable"}`); got < 1 {
		t.Errorf("claims_deferred{reason=switches_unreadable} = %v, want at least 1", got)
	}
	if got := qg.metricValue(t, "reelbolt_platform_switches_unreadable_total"); got < 1 {
		t.Errorf("reelbolt_platform_switches_unreadable_total = %v, want at least 1", got)
	}
}

// TestHandshakeRefusesADisabledOrganizationFromTheDatabase drives the whole
// desktop refusal through a real platform_settings row, which is the path an
// operator flipping the switch actually exercises.
func TestHandshakeRefusesADisabledOrganizationFromTheDatabase(t *testing.T) {
	qg := newQueueGateway(t, nil)
	r := qg.newQueueRunner(t, testOrgA)
	setPlatformSetting(t, qg.pool, settingsKeyOrgPrefix+testOrgA, true, nil)

	ws := qg.connectHello(t, desktopHello(t, r, qg.dialedHost(t), nil))
	_, code := readUntilClose(t, ws)
	if code != protocol.CloseForbidden {
		t.Fatalf("close code = %d, want 4403", code)
	}
	if got := qg.metricValue(t, `reelbolt_runner_gateway_handshakes_total{result="organization_disabled"}`); got != 1 {
		t.Errorf("handshakes{result=organization_disabled} = %v, want 1", got)
	}
}

// TestPoolWorkerIsNotGivenADisabledOrganizationsJob is rule 4 over the wire, on
// the one connection kind that can see several organisations' work at once. The
// disabled organisation's job sorts first, so a driver that only checked the
// runner's own organisation — or nothing at all — would take it.
func TestPoolWorkerIsNotGivenADisabledOrganizationsJob(t *testing.T) {
	qg := newQueueGateway(t, func(cfg *appConfig) { cfg.PoolCredentials = []string{"pool-secret"} })
	const workerName = "pool-fixture-f11"
	insertRunner(t, qg.pool, "c3a1f0de-2b4c-4d8e-9a10-000000000301", "",
		protocol.KindCloudPool, workerName, "")

	blocked := createJob(t, qg.jobs, CreateJobParams{
		IdempotencyKey: "pool-disabled", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobVideoCompile, Target: targetCloudPool, Priority: 10,
	})
	allowed := createJob(t, qg.jobs, CreateJobParams{
		IdempotencyKey: "pool-enabled", OrganizationID: testOrgB, ExecutionID: testExecB,
		JobType: protocol.JobVideoCompile, Target: targetCloudPool,
	})
	setPlatformSetting(t, qg.pool, settingsKeyOrgPrefix+testOrgA, true, nil)

	ws := qg.connectHello(t, poolHello(t, "pool-secret", workerName))
	welcome := readEnvelope(t, ws)
	if welcome.Type != protocol.TypeWelcome {
		t.Fatalf("pool hello answered %s, want welcome: a disabled organisation disables work, not the pool", welcome.Type)
	}
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
	assign := readEnvelope(t, ws)
	if assign.Type != protocol.TypeAssign || assign.JobID != allowed.ID {
		t.Fatalf("the pool worker got %s for %s, want assign for the enabled organisation's %s",
			assign.Type, assign.JobID, allowed.ID)
	}
	if status := qg.jobFor(t, blocked.ID).Status; status != jobQueued {
		t.Errorf("the disabled organisation's job is %q, want Queued", status)
	}
}
