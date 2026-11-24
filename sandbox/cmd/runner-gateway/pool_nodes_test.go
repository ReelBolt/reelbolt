package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

const testPoolKey = "0123456789abcdef0123456789abcdef-key"

// fakePoolStore is an in-memory PoolNodeStore and WakerStore. Enrolling writes
// into the gateway test's device fake so a node can then complete a handshake.
type fakePoolStore struct {
	mu      sync.Mutex
	devices *fakeStore
	ids     map[string]string
	revoked map[string]bool

	waker    *WakerRecord
	wakerErr error
}

func newFakePoolStore(devices *fakeStore) *fakePoolStore {
	return &fakePoolStore{devices: devices, ids: map[string]string{}, revoked: map[string]bool{}}
}

func (f *fakePoolStore) Enroll(_ context.Context, name string) (string, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.revoked[name] {
		return f.ids[name], ErrNodeRevoked
	}
	if id, ok := f.ids[name]; ok {
		return id, nil
	}
	id := fmt.Sprintf("22222222-2222-2222-2222-%012d", len(f.ids)+1)
	f.ids[name] = id
	f.devices.addPoolWorker(id, name)
	return id, nil
}

func (f *fakePoolStore) Revoke(_ context.Context, name string) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.revoked[name] = true
	if id, ok := f.ids[name]; ok {
		f.devices.revoke(id)
	}
	return nil
}

func (f *fakePoolStore) PutWaker(_ context.Context, healthy bool, d wakerDetail) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.wakerErr != nil {
		return f.wakerErr
	}
	now := time.Now()
	f.waker = &WakerRecord{Healthy: healthy, Detail: d, UpdatedAt: now, ServerNow: now}
	return nil
}

func (f *fakePoolStore) GetWaker(context.Context) (WakerRecord, bool, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.wakerErr != nil {
		return WakerRecord{}, false, f.wakerErr
	}
	if f.waker == nil {
		return WakerRecord{}, false, nil
	}
	return *f.waker, true, nil
}

func newPoolTestGateway(t *testing.T, withKey bool) (*testGateway, *fakePoolStore) {
	t.Helper()
	tg := newTestGateway(t, func(cfg *appConfig) {
		cfg.InternalToken = "internal-secret"
		cfg.PoolCredentials = []string{"static-secret"}
	})
	ps := newFakePoolStore(tg.store)
	if withKey {
		tg.gateway.attachPool([]byte(testPoolKey), ps, ps)
	} else {
		tg.gateway.attachPool(nil, ps, ps)
	}
	return tg, ps
}

func (tg *testGateway) poolCall(t *testing.T, method, path string, body any) (int, []byte) {
	t.Helper()
	var rd *bytes.Reader
	switch b := body.(type) {
	case string:
		rd = bytes.NewReader([]byte(b))
	case nil:
		rd = bytes.NewReader(nil)
	default:
		raw, err := json.Marshal(b)
		if err != nil {
			t.Fatal(err)
		}
		rd = bytes.NewReader(raw)
	}
	req, err := http.NewRequestWithContext(context.Background(), method, tg.server.URL+path, rd)
	if err != nil {
		t.Fatal(err)
	}
	req.Header.Set("Authorization", "Bearer internal-secret")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = resp.Body.Close() }()
	var buf bytes.Buffer
	_, _ = buf.ReadFrom(resp.Body)
	return resp.StatusCode, buf.Bytes()
}

func TestNodeTokenMintVerify(t *testing.T) {
	key := []byte(testPoolKey)
	now := time.Now()
	token, err := mintNodeToken(key, "rb-pool-a1", now.Add(time.Hour))
	if err != nil {
		t.Fatal(err)
	}
	if !strings.HasPrefix(token, "rbn1.") {
		t.Fatalf("token prefix: %q", token[:6])
	}
	if err := verifyNodeToken(key, token, "rb-pool-a1", now); err != nil {
		t.Fatalf("valid token refused: %v", err)
	}
	if verifyNodeToken(key, token, "rb-pool-a2", now) == nil {
		t.Error("wrong name accepted")
	}
	if verifyNodeToken(key, token, "rb-pool-a1", now.Add(2*time.Hour)) == nil {
		t.Error("expired token accepted")
	}
	if verifyNodeToken([]byte("a-different-key-of-thirty-two-bytes!!"), token, "rb-pool-a1", now) == nil {
		t.Error("token verified under another key")
	}
	if verifyNodeToken(nil, token, "rb-pool-a1", now) == nil {
		t.Error("token verified with no key")
	}
	// Tamper: swap the payload for another name, keep the signature.
	other, _ := mintNodeToken(key, "rb-pool-evil", now.Add(time.Hour))
	forged := "rbn1." + strings.Split(strings.TrimPrefix(other, "rbn1."), ".")[0] + "." + strings.Split(token, ".")[2]
	if verifyNodeToken(key, forged, "rb-pool-evil", now) == nil {
		t.Error("forged payload accepted")
	}
	for _, bad := range []string{"", "rbn1.", "rbn1.x", "rbn1.a.b.c", "rbn1.!!.!!", "other.x.y"} {
		if verifyNodeToken(key, bad, "rb-pool-a1", now) == nil {
			t.Errorf("garbage %q accepted", bad)
		}
	}
}

func TestEnrollAndRevokeRoutes(t *testing.T) {
	tg, ps := newPoolTestGateway(t, true)

	code, body := tg.poolCall(t, http.MethodPost, "/internal/v1/pool/nodes", map[string]any{"name": "rb-pool-x1", "ttlSeconds": 10})
	if code != http.StatusCreated {
		t.Fatalf("enrol = %d %s", code, body)
	}
	var out struct{ Name, Token, ExpiresAt string }
	if err := json.Unmarshal(body, &out); err != nil {
		t.Fatal(err)
	}
	exp, err := time.Parse(time.RFC3339, out.ExpiresAt)
	if err != nil || time.Until(exp) > 90*time.Second {
		t.Errorf("ttl 10 must clamp to 60s, expiresAt=%s err=%v", out.ExpiresAt, err)
	}
	// Idempotent re-issue.
	if code, _ := tg.poolCall(t, http.MethodPost, "/internal/v1/pool/nodes", map[string]any{"name": "rb-pool-x1"}); code != http.StatusCreated {
		t.Fatalf("re-issue = %d", code)
	}

	// The token authenticates the node.
	ws := tg.connectHello(t, poolHello(t, out.Token, "rb-pool-x1"))
	welcome := readPayload[protocol.WelcomePayload](t, readEnvelope(t, ws))
	if welcome.Kind != protocol.KindCloudPool {
		t.Errorf("kind = %q", welcome.Kind)
	}
	// Same token, other name: refused.
	tg.store.addPoolWorker("33333333-3333-3333-3333-333333333333", "rb-pool-x2")
	_, closeCode := readUntilClose(t, tg.connectHello(t, poolHello(t, out.Token, "rb-pool-x2")))
	if closeCode != protocol.CloseUnauthenticated {
		t.Errorf("wrong-name close = %d", closeCode)
	}

	if code, _ := tg.poolCall(t, http.MethodDelete, "/internal/v1/pool/nodes/rb-pool-x1", nil); code != http.StatusNoContent {
		t.Fatalf("revoke = %d", code)
	}
	if code, _ := tg.poolCall(t, http.MethodDelete, "/internal/v1/pool/nodes/rb-pool-x1", nil); code != http.StatusNoContent {
		t.Fatalf("revoke twice = %d", code)
	}
	if code, body := tg.poolCall(t, http.MethodPost, "/internal/v1/pool/nodes", map[string]any{"name": "rb-pool-x1"}); code != http.StatusConflict || !strings.Contains(string(body), "node_revoked") {
		t.Fatalf("enrol after revoke = %d %s", code, body)
	}
	// Revoked row: the still-valid token is useless.
	_, closeCode = readUntilClose(t, tg.connectHello(t, poolHello(t, out.Token, "rb-pool-x1")))
	if closeCode != protocol.CloseUnauthenticated {
		t.Errorf("revoked close = %d", closeCode)
	}
	_ = ps
}

func TestEnrollValidation(t *testing.T) {
	tg, _ := newPoolTestGateway(t, true)
	for _, body := range []any{
		map[string]any{"name": "evil"},
		map[string]any{"name": "rb-pool-UP"},
		map[string]any{"name": "rb-pool-"},
		map[string]any{"name": "rb-pool-" + strings.Repeat("a", 41)},
		map[string]any{"name": "rb-pool-ok", "extra": 1},
		`{"name":"rb-pool-ok"} {"x":1}`,
		`not json`,
		`{"name":"rb-pool-ok","pad":"` + strings.Repeat("a", 5000) + `"}`,
	} {
		if code, _ := tg.poolCall(t, http.MethodPost, "/internal/v1/pool/nodes", body); code != http.StatusBadRequest {
			t.Errorf("body %v = %d, want 400", body, code)
		}
	}
	if code, _ := tg.poolCall(t, http.MethodDelete, "/internal/v1/pool/nodes/bad_name", nil); code != http.StatusBadRequest {
		t.Errorf("bad delete name = %d", code)
	}
	if code, _ := tg.poolCall(t, http.MethodPut, "/internal/v1/pool/waker", map[string]any{"healthy": true, "nodes": -1}); code != http.StatusBadRequest {
		t.Errorf("negative nodes = %d", code)
	}
}

func TestPoolRoutesNeedInternalToken(t *testing.T) {
	tg, _ := newPoolTestGateway(t, true)
	req, _ := http.NewRequestWithContext(context.Background(), http.MethodPost, tg.server.URL+"/internal/v1/pool/nodes", strings.NewReader(`{"name":"rb-pool-a"}`))
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	_ = resp.Body.Close()
	if resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("no token = %d", resp.StatusCode)
	}
}

func TestPoolDisabledWithoutKey(t *testing.T) {
	tg, _ := newPoolTestGateway(t, false)
	for _, c := range []struct{ method, path string }{
		{http.MethodPost, "/internal/v1/pool/nodes"},
		{http.MethodDelete, "/internal/v1/pool/nodes/rb-pool-a"},
		{http.MethodPut, "/internal/v1/pool/waker"},
	} {
		code, body := tg.poolCall(t, c.method, c.path, `{"name":"rb-pool-a","healthy":true}`)
		if code != http.StatusServiceUnavailable || !strings.Contains(string(body), "pool_tokens_disabled") {
			t.Errorf("%s %s = %d %s", c.method, c.path, code, body)
		}
	}
	// An rbn1. credential minted elsewhere is refused with no key.
	token, _ := mintNodeToken([]byte(testPoolKey), "rb-pool-a", time.Now().Add(time.Hour))
	tg.store.addPoolWorker("44444444-4444-4444-4444-444444444444", "rb-pool-a")
	_, code := readUntilClose(t, tg.connectHello(t, poolHello(t, token, "rb-pool-a")))
	if code != protocol.CloseUnauthenticated {
		t.Errorf("key unset close = %d", code)
	}
}

func TestStaticPoolCredentialStillWorks(t *testing.T) {
	tg, _ := newPoolTestGateway(t, true)
	tg.store.addPoolWorker("55555555-5555-5555-5555-555555555555", "pool-worker-fixture-01")
	ws := tg.connectHello(t, poolHello(t, "static-secret", "pool-worker-fixture-01"))
	if w := readPayload[protocol.WelcomePayload](t, readEnvelope(t, ws)); w.Kind != protocol.KindCloudPool {
		t.Errorf("kind = %q", w.Kind)
	}
}

func TestExpiredTokenRefusedAtHandshake(t *testing.T) {
	tg, _ := newPoolTestGateway(t, true)
	tg.store.addPoolWorker("66666666-6666-6666-6666-666666666666", "rb-pool-old")
	token, _ := mintNodeToken([]byte(testPoolKey), "rb-pool-old", time.Now().Add(-time.Minute))
	if _, code := readUntilClose(t, tg.connectHello(t, poolHello(t, token, "rb-pool-old"))); code != protocol.CloseUnauthenticated {
		t.Errorf("expired close = %d", code)
	}
}

func TestWakerFreshness(t *testing.T) {
	at := time.Now()
	mk := func(age time.Duration, healthy, capped bool) poolView {
		return poolViewFrom(WakerRecord{Healthy: healthy, Detail: wakerDetail{Reason: "r", Capped: capped},
			UpdatedAt: at, ServerNow: at.Add(age)})
	}
	if v := mk(44*time.Second, true, false); !v.WakerHealthy {
		t.Error("44 s must be healthy")
	}
	if v := mk(46*time.Second, true, false); v.WakerHealthy {
		t.Error("46 s must not be healthy")
	}
	if v := mk(time.Second, false, false); v.WakerHealthy {
		t.Error("unhealthy heartbeat counted healthy")
	}
	if v := mk(time.Second, true, true); v.WakerHealthy || !v.Capped {
		t.Errorf("capped must not be healthy: %+v", v)
	}
	if v := mk(44*time.Second, true, false); v.WakerAgeSeconds == nil || *v.WakerAgeSeconds != 44 {
		t.Errorf("age = %v", v.WakerAgeSeconds)
	}
}

func TestWakerHeartbeatViaRunnersResponse(t *testing.T) {
	tg, ps := newPoolTestGateway(t, true)
	get := func() map[string]any {
		code, body := tg.poolCall(t, http.MethodGet, "/internal/v1/runners", nil)
		if code != http.StatusOK {
			t.Fatalf("runners = %d %s", code, body)
		}
		var out map[string]any
		if err := json.Unmarshal(body, &out); err != nil {
			t.Fatal(err)
		}
		pool, ok := out["pool"].(map[string]any)
		if !ok {
			t.Fatalf("runners response has no pool object: %s", body)
		}
		for _, k := range []string{"wakerHealthy", "wakerAgeSeconds", "reason", "capped"} {
			if _, ok := pool[k]; !ok {
				t.Errorf("pool.%s missing: %s", k, body)
			}
		}
		return pool
	}
	if p := get(); p["wakerHealthy"] != false || p["wakerAgeSeconds"] != nil {
		t.Errorf("no heartbeat: %v", p)
	}
	if code, body := tg.poolCall(t, http.MethodPut, "/internal/v1/pool/waker",
		map[string]any{"healthy": true, "reason": "ok", "nodes": 1, "maxNodes": 2, "capped": false, "spendTodayEur": 0.5, "instance": "default"}); code != http.StatusNoContent {
		t.Fatalf("put waker = %d %s", code, body)
	}
	if p := get(); p["wakerHealthy"] != true || p["reason"] != "ok" {
		t.Errorf("after heartbeat: %v", p)
	}
	tg.poolCall(t, http.MethodPut, "/internal/v1/pool/waker", map[string]any{"healthy": true, "capped": true})
	if p := get(); p["wakerHealthy"] != false || p["capped"] != true {
		t.Errorf("capped: %v", p)
	}
	ps.mu.Lock()
	ps.wakerErr = context.DeadlineExceeded
	ps.mu.Unlock()
	if p := get(); p["wakerHealthy"] != false {
		t.Errorf("store error must read as unhealthy: %v", p)
	}
	if code, _ := tg.poolCall(t, http.MethodPut, "/internal/v1/pool/waker", map[string]any{"healthy": true}); code != http.StatusInternalServerError {
		t.Errorf("store failure = %d", code)
	}
}

func TestSwitchesIgnoreWakerRow(t *testing.T) {
	snap := foldSwitchRows([]switchRow{{Key: "waker", Paused: true}, {Key: "compute", Paused: false}})
	if snap.ComputePaused() || snap.OrganizationDisabled("waker") {
		t.Errorf("the waker row leaked into the kill switches: %+v", snap)
	}
}

func TestPoolTokenKeyConfig(t *testing.T) {
	t.Setenv("RUNNER_POOL_TOKEN_KEY", "short")
	if _, err := loadConfig(); err == nil {
		t.Error("a short RUNNER_POOL_TOKEN_KEY must fail startup")
	}
	t.Setenv("RUNNER_POOL_TOKEN_KEY", testPoolKey)
	if cfg, err := loadConfig(); err != nil || cfg.PoolTokenKey != testPoolKey {
		t.Errorf("valid key: %v", err)
	}
}

// TestEnrollSQLMatchesWorkerEnroll fails when the gateway's copy of the enrol
// SQL drifts from sandbox/cmd/reelbolt-worker/enroll.go (package main, so it
// cannot be imported).
func TestEnrollSQLMatchesWorkerEnroll(t *testing.T) {
	raw, err := os.ReadFile("../reelbolt-worker/enroll.go")
	if err != nil {
		t.Fatalf("read enroll.go: %v", err)
	}
	norm := func(s string) string { return strings.Join(strings.Fields(s), " ") }
	source := norm(string(raw))
	for name, sql := range map[string]string{
		"lock": enrollLockSQL, "select": enrollSelectSQL, "insert": enrollInsertSQL, "update": enrollUpdateSQL,
	} {
		if !strings.Contains(source, norm(sql)) {
			t.Errorf("enroll.go no longer contains the %s SQL the gateway copied:\n%s", name, sql)
		}
	}
	if !strings.Contains(source, `"`+enrollLockKey+`"+name`) {
		t.Error("enroll.go advisory lock key drifted from the gateway's")
	}
}
