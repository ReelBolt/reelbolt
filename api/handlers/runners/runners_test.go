package runners

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"encoding/base64"
	"encoding/json"
	"log"
	"net/http"
	"net/http/httptest"
	"net/textproto"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

// ---- fakes ----

type sentMail struct {
	to   string
	tmpl services.TemplateName
	data services.MailData
}

// fakeStore is an in-memory RunnerStore + MembershipLookup that mimics the two
// database-level rules the flow leans on: one live pairing per user code, and one
// device per public key.
type fakeStore struct {
	mu        sync.Mutex
	pairings  []*models.RunnerPairingRequest
	devices   []*models.RunnerDevice
	orgs      map[uuid.UUID]*services.ActiveMembership
	owners    map[uuid.UUID][]string
	now       time.Time
	published []services.RunnerDeviceRevokedEvent
	mail      []sentMail

	// Built once on the first request: the signature replay cache lives on the
	// handler, so a fresh handler per request would make it untestable.
	handler *Handler
	public  *mux.Router
	authed  *mux.Router
}

func newFake() *fakeStore {
	return &fakeStore{
		orgs:   map[uuid.UUID]*services.ActiveMembership{},
		owners: map[uuid.UUID][]string{},
		now:    time.Date(2026, 10, 8, 12, 0, 0, 0, time.UTC),
	}
}

func uniqueViolation() error { return &pgconn.PgError{Code: "23505"} }

func (f *fakeStore) addOrg(id uuid.UUID, name, kind, role string) {
	f.orgs[id] = &services.ActiveMembership{OrgID: id, OrgName: name, OrgKind: kind, Role: role}
}

func (f *fakeStore) GetMembership(_ context.Context, _, orgID uuid.UUID) (*services.ActiveMembership, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if m, ok := f.orgs[orgID]; ok {
		cp := *m
		return &cp, nil
	}
	return nil, nil
}

func (f *fakeStore) SweepExpiredPairingRequests(_ context.Context, now time.Time) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, p := range f.pairings {
		if p.Status == models.RunnerPairingPending && !now.Before(p.ExpiresAt) {
			p.Status = models.RunnerPairingExpired
		}
	}
	return nil
}

func (f *fakeStore) CreatePairingRequest(_ context.Context, req *models.RunnerPairingRequest) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, p := range f.pairings {
		if p.DeviceCodeHash == req.DeviceCodeHash {
			return uniqueViolation()
		}
		if p.UserCode == req.UserCode && p.Status == models.RunnerPairingPending {
			return uniqueViolation()
		}
	}
	f.pairings = append(f.pairings, req)
	return nil
}

func (f *fakeStore) FindPairingRequestByDeviceCodeHash(_ context.Context, hash string) (*models.RunnerPairingRequest, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, p := range f.pairings {
		if p.DeviceCodeHash == hash {
			return p, nil
		}
	}
	return nil, nil
}

func (f *fakeStore) FindPairingRequestByUserCode(_ context.Context, code string) (*models.RunnerPairingRequest, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, p := range f.pairings {
		if p.UserCode == code && p.Status == models.RunnerPairingPending {
			return p, nil
		}
	}
	return nil, nil
}

func (f *fakeStore) TouchPairingPoll(_ context.Context, id uuid.UUID, now time.Time) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, p := range f.pairings {
		if p.ID == id {
			at := now
			p.LastPolledAt = &at
		}
	}
	return nil
}

func (f *fakeStore) MarkPairingDenied(_ context.Context, id uuid.UUID, now time.Time) (bool, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, p := range f.pairings {
		if p.ID == id && p.Status == models.RunnerPairingPending {
			at := now
			p.Status = models.RunnerPairingDenied
			p.DeniedAt = &at
			return true, nil
		}
	}
	return false, nil
}

func (f *fakeStore) MarkPairingConsumed(_ context.Context, id uuid.UUID, now time.Time) (bool, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, p := range f.pairings {
		if p.ID == id && p.Status == models.RunnerPairingApproved && p.ConsumedAt == nil {
			at := now
			p.ConsumedAt = &at
			return true, nil
		}
	}
	return false, nil
}

func (f *fakeStore) ApprovePairing(_ context.Context, req *models.RunnerPairingRequest, device *models.RunnerDevice, now time.Time) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, d := range f.devices {
		if d.PublicKeyFingerprint != nil && *d.PublicKeyFingerprint == *device.PublicKeyFingerprint {
			return services.ErrKeyAlreadyPaired
		}
	}
	for _, p := range f.pairings {
		if p.ID != req.ID {
			continue
		}
		if p.Status != models.RunnerPairingPending {
			return services.ErrPairingNotPending
		}
		at := now
		p.Status = models.RunnerPairingApproved
		p.ApprovedAt = &at
		p.ApprovedByUserID = device.PairedByUserID
		p.ApprovedOrganizationID = device.OrganizationID
		p.ApprovedDeviceID = &device.ID
		f.devices = append(f.devices, device)
		return nil
	}
	return services.ErrPairingNotPending
}

func (f *fakeStore) CountPairStartsFromIP(ip string, since time.Time) (int, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	n := 0
	for _, p := range f.pairings {
		if p.RequestIP != nil && *p.RequestIP == ip && !p.CreatedAt.Before(since) {
			n++
		}
	}
	return n, nil
}

func (f *fakeStore) ListActiveDevices(_ context.Context, orgID uuid.UUID) ([]models.RunnerDevice, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	var out []models.RunnerDevice
	for _, d := range f.devices {
		if d.OrganizationID != nil && *d.OrganizationID == orgID && d.RevokedAt == nil {
			out = append(out, *d)
		}
	}
	return out, nil
}

func (f *fakeStore) FindDeviceByID(_ context.Context, id uuid.UUID) (*models.RunnerDevice, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, d := range f.devices {
		if d.ID == id {
			return d, nil
		}
	}
	return nil, nil
}

func (f *fakeStore) FindDeviceByFingerprint(_ context.Context, fingerprint string) (*models.RunnerDevice, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, d := range f.devices {
		if d.PublicKeyFingerprint != nil && *d.PublicKeyFingerprint == fingerprint && d.RevokedAt == nil {
			return d, nil
		}
	}
	return nil, nil
}

func (f *fakeStore) RevokeDevice(_ context.Context, id, byUser uuid.UUID, now time.Time) (bool, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, d := range f.devices {
		if d.ID == id && d.RevokedAt == nil {
			at := now
			d.RevokedAt = &at
			d.RevokedByUserID = &byUser
			return true, nil
		}
	}
	return false, nil
}

func (f *fakeStore) ListOrgOwnerEmails(_ context.Context, orgID uuid.UUID) ([]string, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.owners[orgID], nil
}

// fakeMailer records instead of sending; SendTemplate never fails here so the
// notification path is observable without SMTP.
type fakeMailer struct{ f *fakeStore }

func (m fakeMailer) SendTemplate(to string, tmpl services.TemplateName, data services.MailData) error {
	m.f.mu.Lock()
	defer m.f.mu.Unlock()
	m.f.mail = append(m.f.mail, sentMail{to: to, tmpl: tmpl, data: data})
	return nil
}

// newHandler builds the handler under test with every collaborator faked.
func (f *fakeStore) newHandler() *Handler {
	return &Handler{
		Store:      f,
		Lookup:     f,
		Mailer:     func() services.Mailer { return fakeMailer{f} },
		Now:        func() time.Time { return f.now },
		BaseURL:    func() string { return "https://app.example" },
		GatewayURL: func() string { return "wss://runner.example/v1/connect" },
		Nonces:     newNonceCache(func() time.Time { return f.now }),
		Publish: func(evt services.RunnerDeviceRevokedEvent) error {
			f.published = append(f.published, evt)
			return nil
		},
	}
}

// routers builds the two surfaces once and reuses them, so the handler's signature
// replay cache behaves as it does in production.
func (f *fakeStore) routers() (*mux.Router, *mux.Router) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.handler == nil {
		h := f.newHandler()
		pub, auth := mux.NewRouter(), mux.NewRouter()
		h.RegisterPublicRoutes(pub)
		h.RegisterRoutes(auth)
		f.handler, f.public, f.authed = h, pub, auth
	}
	return f.public, f.authed
}

// do runs one request through the real routes. A nil caller exercises the
// unauthenticated surface (no UserContext in the request).
func (f *fakeStore) do(as *middleware.UserContext, method, path, body string) *httptest.ResponseRecorder {
	pub, auth := f.routers()

	router := pub
	if as != nil {
		router = auth
	}
	ctx := context.Background()
	if as != nil {
		ctx = context.WithValue(ctx, middleware.UserContextKey, *as)
	}
	req := httptest.NewRequestWithContext(ctx, method, path, strings.NewReader(body))
	rec := httptest.NewRecorder()
	router.ServeHTTP(rec, req)
	return rec
}

// ---- helpers ----

type deviceKey struct {
	pubB64 string
	priv   ed25519.PrivateKey
}

func newDeviceKey(t *testing.T) deviceKey {
	t.Helper()
	pub, priv, err := ed25519.GenerateKey(nil)
	if err != nil {
		t.Fatalf("generate key: %v", err)
	}
	return deviceKey{pubB64: base64.StdEncoding.EncodeToString(pub), priv: priv}
}

func (k deviceKey) sign(deviceID, ts, nonce string) string {
	return base64.RawURLEncoding.EncodeToString(ed25519.Sign(k.priv, []byte(deviceID+"|"+ts+"|"+nonce)))
}

func startBody(k deviceKey, name string) string {
	b, _ := json.Marshal(PairStartRequest{
		PublicKey: k.pubB64, Name: name, Os: "linux", Arch: "amd64", Version: "1.0.0",
	})
	return string(b)
}

func decode[T any](t *testing.T, rec *httptest.ResponseRecorder) T {
	t.Helper()
	var v T
	if err := json.Unmarshal(rec.Body.Bytes(), &v); err != nil {
		t.Fatalf("decode %q: %v", rec.Body.String(), err)
	}
	return v
}

// start runs pair/start and returns the raw codes.
func (f *fakeStore) start(t *testing.T, k deviceKey, name string) PairStartResponse {
	t.Helper()
	rec := f.do(nil, "POST", "/api/v1/runners/pair/start", startBody(k, name))
	if rec.Code != http.StatusOK {
		t.Fatalf("start: status %d body %s", rec.Code, rec.Body.String())
	}
	return decode[PairStartResponse](t, rec)
}

func pollBody(deviceCode string) string {
	b, _ := json.Marshal(PairPollRequest{DeviceCode: deviceCode})
	return string(b)
}

func (f *fakeStore) poll(t *testing.T, deviceCode string) *httptest.ResponseRecorder {
	t.Helper()
	return f.do(nil, "POST", "/api/v1/runners/pair/poll", pollBody(deviceCode))
}

func errCode(t *testing.T, rec *httptest.ResponseRecorder) string {
	t.Helper()
	return decode[map[string]string](t, rec)["error"]
}

func approveBody(orgID uuid.UUID) string {
	b, _ := json.Marshal(ApprovePairRequest{OrganizationID: orgID.String()})
	return string(b)
}

// approve runs approve for a user code and returns the device id.
func (f *fakeStore) approve(t *testing.T, owner middleware.UserContext, userCode string, orgID uuid.UUID) uuid.UUID {
	t.Helper()
	rec := f.do(&owner, "POST", "/api/v1/runners/pair/"+userCode+"/approve", approveBody(orgID))
	if rec.Code != http.StatusOK {
		t.Fatalf("approve: status %d body %s", rec.Code, rec.Body.String())
	}
	resp := decode[ApprovePairResponse](t, rec)
	id, err := uuid.Parse(resp.DeviceID)
	if err != nil {
		t.Fatalf("bad device id %q", resp.DeviceID)
	}
	return id
}

// pairOnce drives a full start -> approve -> poll and returns the device id, the
// key that mints tokens for it, and the codes the device was given.
func (f *fakeStore) pairOnce(t *testing.T, owner middleware.UserContext, orgID uuid.UUID, name string) (uuid.UUID, deviceKey, PairStartResponse) {
	t.Helper()
	k := newDeviceKey(t)
	started := f.start(t, k, name)
	deviceID := f.approve(t, owner, started.UserCode, orgID)

	rec := f.poll(t, started.DeviceCode)
	if rec.Code != http.StatusOK {
		t.Fatalf("poll after approve: status %d body %s", rec.Code, rec.Body.String())
	}
	if got := decode[PairPollResponse](t, rec).DeviceID; got != deviceID.String() {
		t.Fatalf("poll device id %q, want %q", got, deviceID)
	}
	return deviceID, k, started
}

func tokenBody(deviceID uuid.UUID, ts, nonce, sig string) string {
	b, _ := json.Marshal(RunnerTokenRequest{DeviceID: deviceID.String(), Ts: ts, Nonce: nonce, Sig: sig})
	return string(b)
}

const testNonce = "AAAAAAAAAAAAAAAAAAAAAA" // 16 zero bytes, base64url

func (f *fakeStore) mint(t *testing.T, id uuid.UUID, ts string, k deviceKey) *httptest.ResponseRecorder {
	t.Helper()
	return f.do(nil, "POST", "/api/v1/runners/token",
		tokenBody(id, ts, testNonce, k.sign(id.String(), ts, testNonce)))
}

// ---- tests ----

func TestPairStart_ReturnsCodesAndHidesTheDeviceCode(t *testing.T) {
	f := newFake()
	k := newDeviceKey(t)
	resp := f.start(t, k, "Ada's laptop")

	if resp.ExpiresIn != int(PairingTTL/time.Second) || resp.Interval != int(PairingInterval/time.Second) {
		t.Fatalf("expiresIn/interval = %d/%d", resp.ExpiresIn, resp.Interval)
	}
	if len(strings.ReplaceAll(resp.UserCode, "-", "")) != userCodeLength {
		t.Fatalf("userCode %q is not %d characters", resp.UserCode, userCodeLength)
	}
	if resp.VerificationURI != "https://app.example/app/runners/pair" {
		t.Fatalf("verificationUri %q", resp.VerificationURI)
	}
	// The stored row must not carry either secret in the clear.
	stored := f.pairings[0]
	if stored.DeviceCodeHash == resp.DeviceCode {
		t.Fatal("device code stored in the clear")
	}
	if stored.DeviceCodeHash != hashSecret(resp.DeviceCode) {
		t.Fatal("device code hash does not match")
	}
	if stored.UserCode != strings.ReplaceAll(resp.UserCode, "-", "") {
		t.Fatalf("stored user code %q", stored.UserCode)
	}
	if stored.Status != models.RunnerPairingPending {
		t.Fatalf("status %q", stored.Status)
	}
}

// D26: Windows x64 and Linux x64/arm64 only, no macOS anywhere.
func TestPairStart_RefusesUnsupportedPlatforms(t *testing.T) {
	cases := []struct{ os, arch string }{
		{"darwin", "arm64"},
		{"darwin", "amd64"},
		{"windows", "arm64"},
		{"linux", "386"},
		{"", ""},
	}
	for _, tc := range cases {
		f := newFake()
		k := newDeviceKey(t)
		body, _ := json.Marshal(PairStartRequest{
			PublicKey: k.pubB64, Name: "x", Os: tc.os, Arch: tc.arch, Version: "1",
		})
		rec := f.do(nil, "POST", "/api/v1/runners/pair/start", string(body))
		if rec.Code != http.StatusBadRequest || errCode(t, rec) != "unsupported_platform" {
			t.Fatalf("%s/%s: status %d body %s", tc.os, tc.arch, rec.Code, rec.Body.String())
		}
		if len(f.pairings) != 0 {
			t.Fatalf("%s/%s: a refused platform still opened a pairing", tc.os, tc.arch)
		}
	}
}

func TestPairStart_RefusesAMalformedKey(t *testing.T) {
	f := newFake()
	body, _ := json.Marshal(PairStartRequest{PublicKey: "bm90LWEta2V5", Name: "x", Os: "linux", Arch: "amd64"})
	rec := f.do(nil, "POST", "/api/v1/runners/pair/start", string(body))
	if rec.Code != http.StatusBadRequest || errCode(t, rec) != "invalid_public_key" {
		t.Fatalf("status %d body %s", rec.Code, rec.Body.String())
	}
}

func TestPairStart_RateLimitsPerIP(t *testing.T) {
	f := newFake()
	for i := 0; i < middleware.RunnerPairStartMaxPerIPHour; i++ {
		f.start(t, newDeviceKey(t), "box")
	}
	rec := f.do(nil, "POST", "/api/v1/runners/pair/start", startBody(newDeviceKey(t), "one too many"))
	if rec.Code != http.StatusTooManyRequests {
		t.Fatalf("status %d body %s", rec.Code, rec.Body.String())
	}
	if len(f.pairings) != middleware.RunnerPairStartMaxPerIPHour {
		t.Fatalf("stored %d pairings, want %d", len(f.pairings), middleware.RunnerPairStartMaxPerIPHour)
	}
}

func TestPoll_PendingThenSlowDownThenPending(t *testing.T) {
	f := newFake()
	started := f.start(t, newDeviceKey(t), "box")

	rec := f.poll(t, started.DeviceCode)
	if rec.Code != http.StatusBadRequest || errCode(t, rec) != "authorization_pending" {
		t.Fatalf("first poll: status %d body %s", rec.Code, rec.Body.String())
	}
	// Polling again inside the advertised interval is refused, and must not move
	// the clock the interval is measured from.
	rec = f.poll(t, started.DeviceCode)
	if rec.Code != http.StatusBadRequest || errCode(t, rec) != "slow_down" {
		t.Fatalf("second poll: status %d body %s", rec.Code, rec.Body.String())
	}
	rec = f.poll(t, started.DeviceCode)
	if errCode(t, rec) != "slow_down" {
		t.Fatalf("third poll should still be slow_down, got %s", rec.Body.String())
	}

	f.now = f.now.Add(PairingInterval + time.Second)
	rec = f.poll(t, started.DeviceCode)
	if errCode(t, rec) != "authorization_pending" {
		t.Fatalf("after the interval: status %d body %s", rec.Code, rec.Body.String())
	}
}

func TestPoll_ExpiresAfterTheWindow(t *testing.T) {
	f := newFake()
	started := f.start(t, newDeviceKey(t), "box")

	f.now = f.now.Add(PairingTTL + time.Second)
	rec := f.poll(t, started.DeviceCode)
	if rec.Code != http.StatusBadRequest || errCode(t, rec) != "expired_token" {
		t.Fatalf("status %d body %s", rec.Code, rec.Body.String())
	}
	if f.pairings[0].Status != models.RunnerPairingExpired {
		t.Fatalf("status %q, want Expired", f.pairings[0].Status)
	}
	// An unknown device code answers exactly like a spent one.
	rec = f.poll(t, "this-is-not-a-code")
	if rec.Code != http.StatusBadRequest || errCode(t, rec) != "expired_token" {
		t.Fatalf("unknown code: status %d body %s", rec.Code, rec.Body.String())
	}
}

func TestPoll_CodeIsReusedAfterApproval_AndRefused(t *testing.T) {
	f := newFake()
	org := uuid.New()
	f.addOrg(org, "Personal", models.OrgKindPersonal, models.OrgRoleOwner)
	owner := middleware.UserContext{UserID: uuid.NewString(), OrganizationID: org, OrgRole: models.OrgRoleOwner}

	deviceID, _, started := f.pairOnce(t, owner, org, "box")

	// Single-use: the same device code cannot be redeemed twice.
	rec := f.poll(t, started.DeviceCode)
	if rec.Code != http.StatusBadRequest || errCode(t, rec) != "expired_token" {
		t.Fatalf("second delivery: status %d body %s", rec.Code, rec.Body.String())
	}
	if f.pairings[0].ConsumedAt == nil {
		t.Fatal("expected the request to be marked consumed")
	}

	// The user code is spent too: it no longer resolves for approval.
	rec = f.do(&owner, "GET", "/api/v1/runners/pair/"+started.UserCode, "")
	if rec.Code != http.StatusNotFound {
		t.Fatalf("get after approval: status %d body %s", rec.Code, rec.Body.String())
	}
	// And the device it created is listed once.
	rec = f.do(&owner, "GET", "/api/v1/runners", "")
	list := decode[[]DeviceResponse](t, rec)
	if len(list) != 1 || list[0].ID != deviceID.String() {
		t.Fatalf("list = %s", rec.Body.String())
	}
}

func TestPoll_DeniedCodeAnswersAccessDenied(t *testing.T) {
	f := newFake()
	owner := middleware.UserContext{UserID: uuid.NewString()}
	started := f.start(t, newDeviceKey(t), "box")

	rec := f.do(&owner, "POST", "/api/v1/runners/pair/"+started.UserCode+"/deny", "")
	if rec.Code != http.StatusNoContent {
		t.Fatalf("deny: status %d body %s", rec.Code, rec.Body.String())
	}
	rec = f.poll(t, started.DeviceCode)
	if rec.Code != http.StatusBadRequest || errCode(t, rec) != "access_denied" {
		t.Fatalf("status %d body %s", rec.Code, rec.Body.String())
	}
}

func TestApprove_MemberOfATeamOrgIs403(t *testing.T) {
	f := newFake()
	org := uuid.New()
	f.addOrg(org, "Acme", models.OrgKindTeam, models.OrgRoleMember)
	member := middleware.UserContext{UserID: uuid.NewString(), OrgRole: models.OrgRoleMember}
	started := f.start(t, newDeviceKey(t), "attacker box")

	rec := f.do(&member, "POST", "/api/v1/runners/pair/"+started.UserCode+"/approve", approveBody(org))
	if rec.Code != http.StatusForbidden {
		t.Fatalf("status %d body %s", rec.Code, rec.Body.String())
	}
	if len(f.devices) != 0 {
		t.Fatal("a refused approval still created a device")
	}
	if f.pairings[0].Status != models.RunnerPairingPending {
		t.Fatalf("a refused approval changed the request to %q", f.pairings[0].Status)
	}
}

func TestApprove_AdminMayApproveIntoATeamOrg_AndOwnersAreTold(t *testing.T) {
	f := newFake()
	org := uuid.New()
	f.addOrg(org, "Acme", models.OrgKindTeam, models.OrgRoleAdmin)
	f.owners[org] = []string{"owner@acme.test"}
	admin := middleware.UserContext{UserID: uuid.NewString(), OrgRole: models.OrgRoleAdmin}

	started := f.start(t, newDeviceKey(t), "Ada's laptop")
	f.approve(t, admin, started.UserCode, org)

	if len(f.mail) != 1 || f.mail[0].to != "owner@acme.test" ||
		f.mail[0].tmpl != services.TemplateRunnerPaired || f.mail[0].data.DeviceName != "Ada's laptop" {
		t.Fatalf("owner notification = %+v", f.mail)
	}
	if f.mail[0].data.OrgName != "Acme" {
		t.Fatalf("notification org = %q", f.mail[0].data.OrgName)
	}
}

// D5: an organization the approver is not in is 404, never 403.
func TestApprove_ForAnotherOrganizationIs404(t *testing.T) {
	f := newFake()
	mine, other := uuid.New(), uuid.New()
	f.addOrg(mine, "Mine", models.OrgKindTeam, models.OrgRoleOwner)
	f.addOrg(other, "Theirs", models.OrgKindTeam, models.OrgRoleOwner)
	// The lookup only knows this user's own org, so "other" is not a membership.
	f.orgs[other] = nil
	delete(f.orgs, other)
	owner := middleware.UserContext{UserID: uuid.NewString(), OrgRole: models.OrgRoleOwner}
	started := f.start(t, newDeviceKey(t), "box")

	rec := f.do(&owner, "POST", "/api/v1/runners/pair/"+started.UserCode+"/approve", approveBody(other))
	if rec.Code != http.StatusNotFound {
		t.Fatalf("status %d body %s", rec.Code, rec.Body.String())
	}
}

// AUTHZ-GO-6: the Platform org is not a workspace. Its Owner (a platform admin) naming it gets
// the same 404 as a non-member, as orgs.scope and switch-org answer, and no device is created.
func TestApprove_ThePlatformOrgIs404(t *testing.T) {
	f := newFake()
	platform := uuid.New()
	f.addOrg(platform, "Platform", models.OrgKindPlatform, models.OrgRoleOwner)
	admin := middleware.UserContext{UserID: uuid.NewString(), OrgRole: models.OrgRoleOwner, PlatformAdmin: true}
	started := f.start(t, newDeviceKey(t), "box")

	rec := f.do(&admin, "POST", "/api/v1/runners/pair/"+started.UserCode+"/approve", approveBody(platform))
	if rec.Code != http.StatusNotFound {
		t.Fatalf("status %d body %s", rec.Code, rec.Body.String())
	}
	if len(f.devices) != 0 {
		t.Fatal("a device was paired into the Platform org")
	}
	if f.pairings[0].Status != models.RunnerPairingPending {
		t.Fatalf("a refused approval changed the request to %q", f.pairings[0].Status)
	}
}

func TestApprove_TheSameKeyCannotPairTwice(t *testing.T) {
	f := newFake()
	org := uuid.New()
	f.addOrg(org, "Personal", models.OrgKindPersonal, models.OrgRoleOwner)
	owner := middleware.UserContext{UserID: uuid.NewString(), OrgRole: models.OrgRoleOwner}
	k := newDeviceKey(t)

	first := f.start(t, k, "box")
	f.approve(t, owner, first.UserCode, org)

	// A second pairing with the same key is refused before a code is ever shown.
	rec := f.do(nil, "POST", "/api/v1/runners/pair/start", startBody(k, "box again"))
	if rec.Code != http.StatusConflict || errCode(t, rec) != "already_paired" {
		t.Fatalf("status %d body %s", rec.Code, rec.Body.String())
	}
}

func TestGetPair_ShowsTheDeviceAndRefusesAMalformedCode(t *testing.T) {
	f := newFake()
	viewer := middleware.UserContext{UserID: uuid.NewString()}
	started := f.start(t, newDeviceKey(t), "Ada's laptop")

	rec := f.do(&viewer, "GET", "/api/v1/runners/pair/"+started.UserCode, "")
	if rec.Code != http.StatusOK {
		t.Fatalf("status %d body %s", rec.Code, rec.Body.String())
	}
	got := decode[PairApprovalResponse](t, rec)
	if got.Name != "Ada's laptop" || got.Os != "linux" || got.Arch != "amd64" || got.Version != "1.0.0" {
		t.Fatalf("view = %+v", got)
	}
	if got.RequestedFromIP == "" {
		t.Fatal("expected the requesting address to be reported")
	}
	// Lower case without the dash is the same code; anything else is simply absent.
	rec = f.do(&viewer, "GET", "/api/v1/runners/pair/"+strings.ToLower(strings.ReplaceAll(started.UserCode, "-", "")), "")
	if rec.Code != http.StatusOK {
		t.Fatalf("normalized lookup: status %d body %s", rec.Code, rec.Body.String())
	}
	for _, bad := range []string{"ZZZZ-ZZZZ", "1234", "ABCD-EFG0"} {
		rec = f.do(&viewer, "GET", "/api/v1/runners/pair/"+bad, "")
		if rec.Code != http.StatusNotFound {
			t.Fatalf("%s: status %d body %s", bad, rec.Code, rec.Body.String())
		}
	}
}

func TestMintToken_SucceedsAndIsReplayProtected(t *testing.T) {
	f := newFake()
	org := uuid.New()
	f.addOrg(org, "Personal", models.OrgKindPersonal, models.OrgRoleOwner)
	owner := middleware.UserContext{UserID: uuid.NewString(), OrgRole: models.OrgRoleOwner}
	deviceID, k, _ := f.pairOnce(t, owner, org, "box")

	ts := f.now.Format(time.RFC3339)
	rec := f.mint(t, deviceID, ts, k)
	if rec.Code != http.StatusOK {
		t.Fatalf("status %d body %s", rec.Code, rec.Body.String())
	}
	resp := decode[RunnerTokenResponse](t, rec)
	if resp.Token == "" || resp.OrganizationID != org.String() || resp.TokenType != "Bearer" {
		t.Fatalf("response = %+v", resp)
	}
	if resp.ExpiresIn <= 0 || resp.ExpiresIn > int(services.RunnerTokenTTL/time.Second) {
		t.Fatalf("expiresIn = %d", resp.ExpiresIn)
	}

	// The same signature cannot be presented twice inside the skew window.
	rec = f.mint(t, deviceID, ts, k)
	if rec.Code != http.StatusUnauthorized || errCode(t, rec) != "replayed_nonce" {
		t.Fatalf("replay: status %d body %s", rec.Code, rec.Body.String())
	}
}

func TestMintToken_RejectsBadSignaturesSkewAndNonces(t *testing.T) {
	f := newFake()
	org := uuid.New()
	f.addOrg(org, "Personal", models.OrgKindPersonal, models.OrgRoleOwner)
	owner := middleware.UserContext{UserID: uuid.NewString(), OrgRole: models.OrgRoleOwner}
	deviceID, k, _ := f.pairOnce(t, owner, org, "box")
	ts := f.now.Format(time.RFC3339)

	// Signed by a different key.
	other := newDeviceKey(t)
	rec := f.mint(t, deviceID, ts, other)
	if rec.Code != http.StatusUnauthorized || errCode(t, rec) != "invalid_signature" {
		t.Fatalf("wrong key: status %d body %s", rec.Code, rec.Body.String())
	}

	// Signed over a different device id than the one the body carries.
	nonce := "BBBBBBBBBBBBBBBBBBBBBB"
	sig := k.sign(uuid.NewString(), ts, nonce)
	rec = f.do(nil, "POST", "/api/v1/runners/token", tokenBody(deviceID, ts, nonce, sig))
	if rec.Code != http.StatusUnauthorized || errCode(t, rec) != "invalid_signature" {
		t.Fatalf("wrong subject: status %d body %s", rec.Code, rec.Body.String())
	}

	// Outside the skew window.
	far := f.now.Add(-2 * PairingSkew).Format(time.RFC3339)
	rec = f.do(nil, "POST", "/api/v1/runners/token",
		tokenBody(deviceID, far, "CCCCCCCCCCCCCCCCCCCCCC", k.sign(deviceID.String(), far, "CCCCCCCCCCCCCCCCCCCCCC")))
	if rec.Code != http.StatusUnauthorized || errCode(t, rec) != "clock_skew" {
		t.Fatalf("skew: status %d body %s", rec.Code, rec.Body.String())
	}

	// A nonce shorter than the protocol's 16 bytes.
	short := "AAAA"
	rec = f.do(nil, "POST", "/api/v1/runners/token",
		tokenBody(deviceID, ts, short, k.sign(deviceID.String(), ts, short)))
	if rec.Code != http.StatusBadRequest || errCode(t, rec) != "invalid_nonce" {
		t.Fatalf("short nonce: status %d body %s", rec.Code, rec.Body.String())
	}

	// An unknown device, refused identically to a revoked one.
	unknown := uuid.New()
	rec = f.do(nil, "POST", "/api/v1/runners/token",
		tokenBody(unknown, ts, "DDDDDDDDDDDDDDDDDDDDDD", k.sign(unknown.String(), ts, "DDDDDDDDDDDDDDDDDDDDDD")))
	if rec.Code != http.StatusUnauthorized || errCode(t, rec) != "invalid_device" {
		t.Fatalf("unknown device: status %d body %s", rec.Code, rec.Body.String())
	}
}

func TestMintToken_RevokedDeviceIsRefused(t *testing.T) {
	f := newFake()
	org := uuid.New()
	f.addOrg(org, "Personal", models.OrgKindPersonal, models.OrgRoleOwner)
	ownerUC := middleware.UserContext{
		UserID: uuid.NewString(), OrganizationID: org, OrgRole: models.OrgRoleOwner,
	}
	deviceID, k, _ := f.pairOnce(t, ownerUC, org, "box")

	rec := f.do(&ownerUC, "DELETE", "/api/v1/runners/"+deviceID.String(), "")
	if rec.Code != http.StatusOK {
		t.Fatalf("revoke: status %d body %s", rec.Code, rec.Body.String())
	}
	if len(f.published) != 1 || f.published[0].DeviceID != deviceID.String() {
		t.Fatalf("published = %+v", f.published)
	}

	rec = f.mint(t, deviceID, f.now.Format(time.RFC3339), k)
	if rec.Code != http.StatusUnauthorized || errCode(t, rec) != "invalid_device" {
		t.Fatalf("revoked mint: status %d body %s", rec.Code, rec.Body.String())
	}

	// Revocation is immediate in the listing too.
	rec = f.do(&ownerUC, "GET", "/api/v1/runners", "")
	if list := decode[[]DeviceResponse](t, rec); len(list) != 0 {
		t.Fatalf("revoked device still listed: %s", rec.Body.String())
	}

	// Revoking again is idempotent and publishes nothing further.
	rec = f.do(&ownerUC, "DELETE", "/api/v1/runners/"+deviceID.String(), "")
	if rec.Code != http.StatusOK || !decode[RevokeResponse](t, rec).AlreadyRevoked {
		t.Fatalf("second revoke: status %d body %s", rec.Code, rec.Body.String())
	}
	if len(f.published) != 1 {
		t.Fatalf("second revoke published again: %+v", f.published)
	}
}

// D5: a device in another organization is 404, never 403.
func TestRevoke_AnotherOrganizationsDeviceIs404(t *testing.T) {
	f := newFake()
	mine, theirs := uuid.New(), uuid.New()
	f.addOrg(mine, "Mine", models.OrgKindTeam, models.OrgRoleOwner)
	f.addOrg(theirs, "Theirs", models.OrgKindTeam, models.OrgRoleOwner)
	theirOwner := middleware.UserContext{UserID: uuid.NewString(), OrganizationID: theirs, OrgRole: models.OrgRoleOwner}
	deviceID, _, _ := f.pairOnce(t, theirOwner, theirs, "their box")

	me := middleware.UserContext{UserID: uuid.NewString(), OrganizationID: mine, OrgRole: models.OrgRoleOwner}
	rec := f.do(&me, "DELETE", "/api/v1/runners/"+deviceID.String(), "")
	if rec.Code != http.StatusNotFound {
		t.Fatalf("cross-org revoke: status %d body %s", rec.Code, rec.Body.String())
	}
	// And an id that does not exist answers the same way.
	rec = f.do(&me, "DELETE", "/api/v1/runners/"+uuid.NewString(), "")
	if rec.Code != http.StatusNotFound {
		t.Fatalf("unknown device: status %d body %s", rec.Code, rec.Body.String())
	}
	if len(f.published) != 0 {
		t.Fatal("a refused revocation published an event")
	}
}

func TestRevoke_ThePairerMayRevokeEvenAsAMember(t *testing.T) {
	f := newFake()
	org := uuid.New()
	f.addOrg(org, "Acme", models.OrgKindTeam, models.OrgRoleMember)
	memberID := uuid.New()
	member := middleware.UserContext{UserID: memberID.String(), OrganizationID: org, OrgRole: models.OrgRoleMember}

	// A Member cannot approve into a Team org, so seed the device the way an
	// approved pairing would have: same key, same org, paired by this member.
	k := newDeviceKey(t)
	started := f.start(t, k, "member box")
	admin := middleware.UserContext{UserID: uuid.NewString(), OrgRole: models.OrgRoleAdmin}
	f.addOrg(org, "Acme", models.OrgKindTeam, models.OrgRoleAdmin)
	deviceID := f.approve(t, admin, started.UserCode, org)

	f.addOrg(org, "Acme", models.OrgKindTeam, models.OrgRoleMember)
	rec := f.do(&member, "DELETE", "/api/v1/runners/"+deviceID.String(), "")
	if rec.Code != http.StatusForbidden {
		t.Fatalf("a member who did not pair it: status %d body %s", rec.Code, rec.Body.String())
	}

	// Now the same device, paired by this member: allowed.
	f.devices[0].PairedByUserID = &memberID
	rec = f.do(&member, "DELETE", "/api/v1/runners/"+deviceID.String(), "")
	if rec.Code != http.StatusOK {
		t.Fatalf("the pairer: status %d body %s", rec.Code, rec.Body.String())
	}
}

func TestListRunners_IsScopedToTheActiveOrganization(t *testing.T) {
	f := newFake()
	orgA, orgB := uuid.New(), uuid.New()
	ownerA := middleware.UserContext{UserID: uuid.NewString(), OrganizationID: orgA, OrgRole: models.OrgRoleOwner}
	ownerB := middleware.UserContext{UserID: uuid.NewString(), OrganizationID: orgB, OrgRole: models.OrgRoleOwner}
	f.addOrg(orgA, "A", models.OrgKindTeam, models.OrgRoleOwner)
	f.addOrg(orgB, "B", models.OrgKindTeam, models.OrgRoleOwner)

	f.pairOnce(t, ownerA, orgA, "a box")
	f.pairOnce(t, ownerB, orgB, "b box")

	rec := f.do(&ownerA, "GET", "/api/v1/runners", "")
	list := decode[[]DeviceResponse](t, rec)
	if len(list) != 1 || list[0].Name != "a box" {
		t.Fatalf("org A list = %s", rec.Body.String())
	}

	// A legacy token with no active organization has no runner list to show.
	legacy := middleware.UserContext{UserID: uuid.NewString()}
	rec = f.do(&legacy, "GET", "/api/v1/runners", "")
	if rec.Code != http.StatusForbidden {
		t.Fatalf("status %d body %s", rec.Code, rec.Body.String())
	}
}

func TestNormalizeUserCode_RejectsConfusableCharacters(t *testing.T) {
	if _, ok := NormalizeUserCode("ABCD-EFGH"); !ok {
		t.Fatal("ABCD-EFGH should be valid")
	}
	if got, _ := NormalizeUserCode(" abcd efgh "); got != "ABCDEFGH" {
		t.Fatalf("normalized to %q", got)
	}
	// 0/O and 1/I are not in the alphabet, so a transcribed code fails rather than
	// silently resolving to a different request.
	for _, bad := range []string{"ABCD-EFG0", "ABCD-EFG1", "ABCD-EFGO", "ABCD-EFGI", "ABCD", "ABCD-EFGHI"} {
		if _, ok := NormalizeUserCode(bad); ok {
			t.Fatalf("%q should be rejected", bad)
		}
	}
}

func TestFormatUserCode(t *testing.T) {
	if got := FormatUserCode("ABCDEFGH"); got != "ABCD-EFGH" {
		t.Fatalf("got %q", got)
	}
}

// The exchange name is the contract the Go runner gateway consumes; a rename here
// would silently stop the teardown of a revoked device's socket.
func TestRevocationEventCarriesTheDocumentedIdentity(t *testing.T) {
	f := newFake()
	org := uuid.New()
	f.addOrg(org, "Personal", models.OrgKindPersonal, models.OrgRoleOwner)
	revoker := uuid.New()
	owner := middleware.UserContext{UserID: revoker.String(), OrganizationID: org, OrgRole: models.OrgRoleOwner}
	deviceID, _, _ := f.pairOnce(t, owner, org, "box")

	f.do(&owner, "DELETE", "/api/v1/runners/"+deviceID.String(), "")
	if len(f.published) != 1 {
		t.Fatalf("published = %+v", f.published)
	}
	evt := f.published[0]
	if evt.DeviceID != deviceID.String() || evt.OrganizationID != org.String() ||
		evt.RevokedByUserID != revoker.String() || evt.RevokedAt.IsZero() {
		t.Fatalf("event = %+v", evt)
	}
}

// The public pairing routes are registered on the ROOT router before the catch-all
// authenticated subrouter (handlers.RegisterHandlers). mux takes the first match, so
// that order is what keeps pairing reachable without a session — and it is exactly
// the kind of wiring that breaks silently. This mirrors the production registration.
func TestPublicPairingRoutesAreNotSwallowedByTheAuthenticatedSubrouter(t *testing.T) {
	f := newFake()
	h := f.newHandler()

	root := mux.NewRouter()
	h.RegisterPublicRoutes(root)
	authed := root.PathPrefix("").Subrouter()
	authed.Use(func(http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
			fail(w, http.StatusUnauthorized, "missing or invalid authorization header")
		})
	})
	h.RegisterRoutes(authed)

	send := func(method, path, body string) *httptest.ResponseRecorder {
		req := httptest.NewRequestWithContext(context.Background(), method, path, strings.NewReader(body))
		rec := httptest.NewRecorder()
		root.ServeHTTP(rec, req)
		return rec
	}

	// No session at all: pairing still reaches its handler.
	rec := send("POST", "/api/v1/runners/pair/start", startBody(newDeviceKey(t), "box"))
	if rec.Code != http.StatusOK {
		t.Fatalf("pair/start without a session: status %d body %s", rec.Code, rec.Body.String())
	}
	rec = send("POST", "/api/v1/runners/pair/poll", pollBody("nope"))
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("pair/poll without a session: status %d body %s", rec.Code, rec.Body.String())
	}

	// The org-scoped surface must still be behind the middleware.
	for _, tc := range []struct{ method, path string }{
		{"GET", "/api/v1/runners"},
		{"GET", "/api/v1/runners/pair/ABCD-EFGH"},
		{"POST", "/api/v1/runners/pair/ABCD-EFGH/approve"},
		{"POST", "/api/v1/runners/pair/ABCD-EFGH/deny"},
		{"DELETE", "/api/v1/runners/" + uuid.NewString()},
	} {
		rec := send(tc.method, tc.path, "")
		if rec.Code != http.StatusUnauthorized {
			t.Fatalf("%s %s without a session: status %d body %s", tc.method, tc.path, rec.Code, rec.Body.String())
		}
	}
}

// config must not be needed for these tests to compile, but the gateway URL the
// handler reports is the one a deployment configures.
func TestGatewayURLIsReported(t *testing.T) {
	f := newFake()
	org := uuid.New()
	f.addOrg(org, "Personal", models.OrgKindPersonal, models.OrgRoleOwner)
	owner := middleware.UserContext{UserID: uuid.NewString(), OrganizationID: org, OrgRole: models.OrgRoleOwner}
	deviceID, k, _ := f.pairOnce(t, owner, org, "box")

	rec := f.mint(t, deviceID, f.now.Format(time.RFC3339), k)
	if got := decode[RunnerTokenResponse](t, rec).GatewayURL; got != "wss://runner.example/v1/connect" {
		t.Fatalf("gatewayUrl = %q", got)
	}
}

// failingMailer fails every send like an SMTP server rejecting the recipient.
type failingMailer struct{}

func (failingMailer) SendTemplate(string, services.TemplateName, services.MailData) error {
	return &textproto.Error{Code: 550, Msg: "5.1.1 <owner@victim.example>: user unknown"}
}

// F-4: a failed pairing notification is logged by reference with the SMTP error sanitised.
func TestPairingNotificationFailureLogsNoAddress(t *testing.T) {
	f := newFake()
	org := uuid.New()
	f.owners[org] = []string{"owner@victim.example"}
	h := f.newHandler()
	h.Mailer = func() services.Mailer { return failingMailer{} }
	var buf bytes.Buffer
	old := log.Writer()
	log.SetOutput(&buf)
	defer log.SetOutput(old)

	h.notifyOwners(context.Background(), org, "Acme", "laptop")
	if out := buf.String(); strings.Contains(out, "@") || !strings.Contains(out, "rcpt=") || !strings.Contains(out, "550") {
		t.Fatalf("log: %s", out)
	}
}
