package auth

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/handlers/orgs"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

// fakeOrgs implements only the OrgStore methods the invitation flows use; the
// embedded nil interface panics loudly if anything else is called.
type fakeOrgs struct {
	services.OrgStore
	mu      sync.Mutex
	org     models.Organization
	invites map[string]*models.OrganizationInvite // by token hash
	members map[uuid.UUID]string
	users   func() map[uuid.UUID]*models.ApplicationUser
}

func (f *fakeOrgs) FindInviteByTokenHash(_ context.Context, h string) (*models.OrganizationInvite, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if inv, ok := f.invites[h]; ok {
		c := *inv
		return &c, nil
	}
	return nil, nil
}

func (f *fakeOrgs) GetOrganization(context.Context, uuid.UUID) (*models.Organization, error) {
	if f.org.DeletedAt != nil {
		return nil, nil
	}
	o := f.org
	return &o, nil
}

func (f *fakeOrgs) GetUserIdentity(_ context.Context, id uuid.UUID) (string, string, error) {
	if u, ok := f.users()[id]; ok {
		return u.Email, u.DisplayName, nil
	}
	return "", "", context.Canceled
}

func (f *fakeOrgs) GetMemberRole(_ context.Context, _, u uuid.UUID) (string, bool, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	r, ok := f.members[u]
	return r, ok, nil
}

func (f *fakeOrgs) acceptTx(inv *models.OrganizationInvite, u uuid.UUID, now time.Time) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	stored := f.invites[inv.TokenHash]
	if stored.AcceptedAt != nil || stored.RevokedAt != nil {
		return context.Canceled
	}
	stored.AcceptedAt = &now
	if _, ok := f.members[u]; !ok {
		f.members[u] = inv.Role
	}
	return nil
}

func (f *fakeOrgs) AcceptInvite(_ context.Context, inv *models.OrganizationInvite, u uuid.UUID, now time.Time) error {
	return f.acceptTx(inv, u, now)
}

// RegisterInvitedUser makes the fake account store behave like the real one:
// user + invite acceptance together.
func (f *fakeStore) RegisterInvitedUser(u *models.ApplicationUser, a []models.LegalAcceptance, inv *models.OrganizationInvite, optIn *services.MarketingOptIn) error {
	if err := f.invOrgs.acceptTx(inv, u.ID, *u.EmailVerifiedAt); err != nil {
		return err
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	f.stampPersonalOrg(u)
	c := *u
	f.users[u.ID] = &c
	f.acceptances = append(f.acceptances, a...)
	f.applyOptIn(u, optIn)
	return nil
}

type seatRecorder struct {
	mu   sync.Mutex
	orgs []uuid.UUID
}

func (s *seatRecorder) SeatsChanged(_ context.Context, id uuid.UUID) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.orgs = append(s.orgs, id)
	return nil
}

type inviteEnv struct {
	*harness
	fo     *fakeOrgs
	seats  *seatRecorder
	router *mux.Router
	token  string
	inv    *models.OrganizationInvite
	owner  *models.ApplicationUser
}

func newInviteEnv(t *testing.T) *inviteEnv {
	t.Helper()
	h := setup(t, true)
	oldDeps, oldSeats := deps, services.SeatChanges
	t.Cleanup(func() { deps, services.SeatChanges = oldDeps, oldSeats })

	owner := &models.ApplicationUser{ID: uuid.New(), Email: "owner@acme.test", DisplayName: "Olive Owner"}
	h.store.users[owner.ID] = owner

	orgID := uuid.New()
	fo := &fakeOrgs{
		org:     models.Organization{ID: orgID, Name: "Acme Team", Kind: models.OrgKindTeam},
		invites: map[string]*models.OrganizationInvite{},
		members: map[uuid.UUID]string{owner.ID: models.OrgRoleOwner},
		users:   func() map[uuid.UUID]*models.ApplicationUser { return h.store.users },
	}
	h.store.invOrgs = fo
	acct.orgs = fo

	token := "tok_" + uuid.NewString()
	inv := &models.OrganizationInvite{
		ID: uuid.New(), OrganizationID: orgID, Email: "new@example.com", Role: models.OrgRoleMember,
		TokenHash: orgs.HashInviteToken(token), InvitedByUserID: &owner.ID,
		CreatedAt: h.now, ExpiresAt: h.now.Add(7 * 24 * time.Hour),
	}
	fo.invites[inv.TokenHash] = inv

	seats := &seatRecorder{}
	services.SeatChanges = seats

	deps = switchDeps{
		lookup: lookupFunc(func(_ context.Context, u, o uuid.UUID) (*services.ActiveMembership, error) {
			fo.mu.Lock()
			defer fo.mu.Unlock()
			role, ok := fo.members[u]
			if !ok {
				return nil, nil
			}
			return &services.ActiveMembership{OrgID: o, OrgName: fo.org.Name, OrgKind: models.OrgKindTeam, Role: role}, nil
		}),
		getUser: func(id uuid.UUID) (*models.ApplicationUser, error) {
			u, err := h.store.FindUserByID(id)
			if u == nil {
				return nil, context.Canceled
			}
			return u, err
		},
		setLastActive: func(context.Context, uuid.UUID, uuid.UUID) error { return nil },
	}

	r := mux.NewRouter()
	RegisterAuthRoutes(r)
	sub := r.PathPrefix("").Subrouter()
	sub.Use(func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
			id := req.Header.Get("X-Test-User")
			if id == "" {
				http.Error(w, "unauthorized", http.StatusUnauthorized)
				return
			}
			u, _ := h.store.FindUserByID(uuid.MustParse(id))
			ctx := context.WithValue(req.Context(), middleware.UserContextKey,
				middleware.UserContext{UserID: id, Email: u.Email})
			next.ServeHTTP(w, req.WithContext(ctx))
		})
	})
	RegisterAuthenticatedRoutes(sub)
	return &inviteEnv{harness: h, fo: fo, seats: seats, router: r, token: token, inv: inv, owner: owner}
}

type lookupFunc func(context.Context, uuid.UUID, uuid.UUID) (*services.ActiveMembership, error)

func (f lookupFunc) GetMembership(c context.Context, u, o uuid.UUID) (*services.ActiveMembership, error) {
	return f(c, u, o)
}

func (e *inviteEnv) do(method, path, body, asUser string) *httptest.ResponseRecorder {
	req := httptest.NewRequestWithContext(context.Background(), method, path, strings.NewReader(body))
	req.Header.Set("X-Real-IP", "203.0.113.9")
	// The routes are behind middleware.RequireJSON (C11 F-6), so the harness speaks
	// the way every first-party caller does: the web dashboard's apiFetch
	// (web/lib/api/client.ts) sets this header on every request it makes.
	req.Header.Set("Content-Type", "application/json")
	if asUser != "" {
		req.Header.Set("X-Test-User", asUser)
	}
	rec := httptest.NewRecorder()
	e.router.ServeHTTP(rec, req)
	return rec
}

// existingUser registers an account for the invited address (verified).
func (e *inviteEnv) existingUser(email string) *models.ApplicationUser {
	now := e.now
	u := &models.ApplicationUser{ID: uuid.New(), Email: email, DisplayName: "Existing", EmailVerifiedAt: &now}
	e.store.users[u.ID] = u
	return u
}

func inviteSignupBody(email, token string) string {
	return `{"email":"` + email + `","password":"correct horse","acceptTerms":true,"termsVersion":"2026-10","privacyVersion":"2026-10","inviteToken":"` + token + `"}`
}

// ---- preview ----

func TestInvitationPreview(t *testing.T) {
	e := newInviteEnv(t)
	rec := e.do("GET", "/api/v1/auth/invitations/"+e.token, "", "")
	if rec.Code != 200 {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	var p InvitationPreviewResponse
	_ = json.Unmarshal(rec.Body.Bytes(), &p)
	if p.OrganizationName != "Acme Team" || p.InviterName != "Olive Owner" || p.Role != "Member" || p.Email != "new@example.com" || p.AccountExists {
		t.Fatalf("preview: %+v", p)
	}
	if strings.Contains(rec.Body.String(), e.token) || strings.Contains(rec.Body.String(), e.inv.TokenHash) {
		t.Fatal("preview must not echo the token or its hash")
	}

	e.existingUser("new@example.com")
	_ = json.Unmarshal(e.do("GET", "/api/v1/auth/invitations/"+e.token, "", "").Body.Bytes(), &p)
	if !p.AccountExists {
		t.Fatal("accountExists must be true once the address has an account")
	}
}

func TestInvitationPreview_Failures(t *testing.T) {
	e := newInviteEnv(t)
	if rec := e.do("GET", "/api/v1/auth/invitations/garbage", "", ""); rec.Code != 404 {
		t.Fatalf("unknown got %d", rec.Code)
	}
	e.h().now = e.now.Add(7*24*time.Hour + time.Second)
	if rec := e.do("GET", "/api/v1/auth/invitations/"+e.token, "", ""); rec.Code != 410 {
		t.Fatalf("expired got %d", rec.Code)
	}
	e.h().now = e.now.Add(-7*24*time.Hour - time.Second)

	revoked := e.fo.invites[e.inv.TokenHash]
	at := e.now
	revoked.RevokedAt = &at
	if rec := e.do("GET", "/api/v1/auth/invitations/"+e.token, "", ""); rec.Code != 404 {
		t.Fatalf("revoked got %d", rec.Code)
	}
	revoked.RevokedAt = nil
	revoked.AcceptedAt = &at
	if rec := e.do("GET", "/api/v1/auth/invitations/"+e.token, "", ""); rec.Code != 404 {
		t.Fatalf("accepted got %d", rec.Code)
	}
	revoked.AcceptedAt = nil
	e.fo.org.Kind = models.OrgKindPersonal
	if rec := e.do("GET", "/api/v1/auth/invitations/"+e.token, "", ""); rec.Code != 404 {
		t.Fatalf("non-team org got %d", rec.Code)
	}
}

func (e *inviteEnv) h() *harness { return e.harness }

// ---- accept (existing user) ----

func TestInvitationAccept_JoinsAndReissuesSession(t *testing.T) {
	e := newInviteEnv(t)
	u := e.existingUser("new@example.com")

	rec := e.do("POST", "/api/v1/auth/invitations/"+e.token+"/accept", "", u.ID.String())
	if rec.Code != 200 {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	var resp TokenResponse
	_ = json.Unmarshal(rec.Body.Bytes(), &resp)
	if resp.Org != e.fo.org.ID.String() || resp.OrgRole != "Member" || resp.OrgKind != models.OrgKindTeam || resp.AccessToken == "" {
		t.Fatalf("session: %+v", resp)
	}
	if e.fo.members[u.ID] != "Member" {
		t.Fatal("membership not created")
	}
	if len(e.seats.orgs) != 1 || e.seats.orgs[0] != e.fo.org.ID {
		t.Fatalf("seat hook: %v", e.seats.orgs)
	}
	cookies := rec.Result().Cookies()
	if len(cookies) != 2 {
		t.Fatalf("expected both session cookies, got %d", len(cookies))
	}
}

func TestInvitationAccept_Idempotent(t *testing.T) {
	e := newInviteEnv(t)
	u := e.existingUser("new@example.com")
	path := "/api/v1/auth/invitations/" + e.token + "/accept"

	if rec := e.do("POST", path, "", u.ID.String()); rec.Code != 200 {
		t.Fatalf("first %d", rec.Code)
	}
	rec := e.do("POST", path, "", u.ID.String())
	if rec.Code != 200 {
		t.Fatalf("second accept must be idempotent, got %d %s", rec.Code, rec.Body)
	}
	if len(e.seats.orgs) != 1 {
		t.Fatalf("a repeat accept must not change seats again: %v", e.seats.orgs)
	}
	// A different user cannot use the already-consumed invite.
	other := e.existingUser("someone@example.com")
	if rec := e.do("POST", path, "", other.ID.String()); rec.Code != 403 {
		t.Fatalf("other user got %d", rec.Code)
	}
}

func TestInvitationAccept_EmailMismatchIs403AndNothingChanges(t *testing.T) {
	e := newInviteEnv(t)
	stranger := e.existingUser("stranger@example.com")
	rec := e.do("POST", "/api/v1/auth/invitations/"+e.token+"/accept", "", stranger.ID.String())
	if rec.Code != 403 {
		t.Fatalf("got %d", rec.Code)
	}
	if _, joined := e.fo.members[stranger.ID]; joined || e.fo.invites[e.inv.TokenHash].AcceptedAt != nil || len(e.seats.orgs) != 0 {
		t.Fatal("a mismatched accept must change nothing")
	}
}

func TestInvitationAccept_ExpiredIs410(t *testing.T) {
	e := newInviteEnv(t)
	u := e.existingUser("new@example.com")
	e.h().now = e.now.Add(7*24*time.Hour + time.Second)
	if rec := e.do("POST", "/api/v1/auth/invitations/"+e.token+"/accept", "", u.ID.String()); rec.Code != 410 {
		t.Fatalf("got %d", rec.Code)
	}
	if _, joined := e.fo.members[u.ID]; joined {
		t.Fatal("expired invite must not create a membership")
	}
}

func TestInvitationAccept_UnknownIs404AndRequiresAuth(t *testing.T) {
	e := newInviteEnv(t)
	u := e.existingUser("new@example.com")
	if rec := e.do("POST", "/api/v1/auth/invitations/garbage/accept", "", u.ID.String()); rec.Code != 404 {
		t.Fatalf("got %d", rec.Code)
	}
	if rec := e.do("POST", "/api/v1/auth/invitations/"+e.token+"/accept", "", ""); rec.Code != 401 {
		t.Fatalf("unauthenticated got %d", rec.Code)
	}
}

// ---- new-user path ----

func TestInviteSignup_MarksVerifiedJoinsOrgAndLogsIn(t *testing.T) {
	e := newInviteEnv(t)
	rec := e.do("POST", "/api/v1/auth/signup", inviteSignupBody("New@Example.com", e.token), "")
	if rec.Code != 200 {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	u, _ := e.store.FindUserByEmail("new@example.com")
	if u == nil || u.EmailVerifiedAt == nil {
		t.Fatalf("invite token must count as email verification: %+v", u)
	}
	if e.fo.members[u.ID] != "Member" || e.fo.invites[e.inv.TokenHash].AcceptedAt == nil {
		t.Fatal("new user must join the org and consume the invite")
	}
	if len(e.seats.orgs) != 1 || e.seats.orgs[0] != e.fo.org.ID {
		t.Fatalf("seat hook: %v", e.seats.orgs)
	}
	for _, m := range e.mails {
		if m.tmpl == services.TemplateVerifyEmail {
			t.Fatal("no verification mail for an invited signup")
		}
	}
	var resp TokenResponse
	_ = json.Unmarshal(rec.Body.Bytes(), &resp)
	if resp.AccessToken == "" || !resp.EmailVerified {
		t.Fatalf("session: %+v", resp)
	}
	if len(e.store.acceptances) != 2 {
		t.Fatalf("legal acceptances: %d", len(e.store.acceptances))
	}
	if len(e.store.tokens) != 0 {
		t.Fatal("no verify token is needed")
	}
}

func TestInviteSignup_Failures(t *testing.T) {
	e := newInviteEnv(t)
	post := func(email, tok string) *httptest.ResponseRecorder {
		return e.do("POST", "/api/v1/auth/signup", inviteSignupBody(email, tok), "")
	}
	if rec := post("other@example.com", e.token); rec.Code != 403 {
		t.Fatalf("email mismatch got %d", rec.Code)
	}
	if rec := post("new@example.com", "garbage"); rec.Code != 404 {
		t.Fatalf("unknown token got %d", rec.Code)
	}
	if len(e.store.users) != 1 { // only the owner
		t.Fatalf("failed attempts must create nothing, users=%d", len(e.store.users))
	}

	e.existingUser("new@example.com")
	if rec := post("new@example.com", e.token); rec.Code != 409 {
		t.Fatalf("existing account got %d", rec.Code)
	}
	delete(e.store.users, func() uuid.UUID {
		for id, u := range e.store.users {
			if u.Email == "new@example.com" {
				return id
			}
		}
		return uuid.Nil
	}())

	e.h().now = e.now.Add(7*24*time.Hour + time.Second)
	if rec := post("new@example.com", e.token); rec.Code != 410 {
		t.Fatalf("expired got %d", rec.Code)
	}
}

func TestInviteSignup_SkipsDisposableCheck(t *testing.T) {
	e := newInviteEnv(t)
	e.inv.Email = "member@mailinator.com"
	e.fo.invites[e.inv.TokenHash].Email = "member@mailinator.com"
	if rec := e.do("POST", "/api/v1/auth/signup", inviteSignupBody("member@mailinator.com", e.token), ""); rec.Code != 200 {
		t.Fatalf("a team admin's chosen address must pass, got %d %s", rec.Code, rec.Body)
	}
}

func TestInviteSignup_TokenCannotBeReused(t *testing.T) {
	e := newInviteEnv(t)
	if rec := e.do("POST", "/api/v1/auth/signup", inviteSignupBody("new@example.com", e.token), ""); rec.Code != 200 {
		t.Fatalf("first got %d", rec.Code)
	}
	// The same token again: the invite is consumed (404), not an account oracle.
	if rec := e.do("POST", "/api/v1/auth/signup", inviteSignupBody("new@example.com", e.token), ""); rec.Code != 404 {
		t.Fatalf("reuse got %d", rec.Code)
	}
}
