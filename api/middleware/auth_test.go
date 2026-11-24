package middleware

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

type fakeLookup struct {
	info  *services.ActiveMembership
	err   error
	calls int
}

func (f *fakeLookup) GetMembership(_ context.Context, _, _ uuid.UUID) (*services.ActiveMembership, error) {
	f.calls++
	return f.info, f.err
}

func setup(t *testing.T) (uuid.UUID, uuid.UUID, string) {
	t.Helper()
	old := config.Cfg
	config.Cfg.JWTSigningKey = "0123456789abcdef0123456789abcdef"
	config.Cfg.JWTIssuer = "reelbolt-api"
	config.Cfg.JWTAudience = "reelbolt-inference"
	t.Cleanup(func() { config.Cfg = old })

	user := &models.ApplicationUser{ID: uuid.New(), Email: "a@b.c", IsAdmin: false}
	org := uuid.New()
	tok, err := services.GenerateToken(user, &services.ActiveMembership{OrgID: org, Role: models.OrgRoleOwner})
	if err != nil {
		t.Fatal(err)
	}
	return user.ID, org, tok
}

func call(a *AuthMiddleware, tok string) (*httptest.ResponseRecorder, *UserContext) {
	var got *UserContext
	h := a.Handler(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		uc, _ := GetUserContext(r)
		got = &uc
	}))
	req := httptest.NewRequestWithContext(context.Background(), "GET", "/api/v1/workflows/stats", nil)
	req.Header.Set("Authorization", "Bearer "+tok)
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, req)
	return rec, got
}

func TestAuth_RevokedMembershipIs401(t *testing.T) {
	_, _, tok := setup(t)
	a := NewAuthMiddleware(&fakeLookup{info: nil}, time.Second)
	rec, uc := call(a, tok)
	if rec.Code != http.StatusUnauthorized || !strings.Contains(rec.Body.String(), "org_membership_revoked") {
		t.Fatalf("got %d %q", rec.Code, rec.Body.String())
	}
	if uc != nil {
		t.Fatal("handler must not run")
	}
}

func TestAuth_RoleComesFromDatabaseNotToken(t *testing.T) {
	_, org, tok := setup(t) // token says Owner
	a := NewAuthMiddleware(&fakeLookup{info: &services.ActiveMembership{OrgID: org, Role: models.OrgRoleMember}}, time.Second)
	rec, uc := call(a, tok)
	if rec.Code != 200 || uc == nil {
		t.Fatalf("got %d", rec.Code)
	}
	if uc.OrgRole != models.OrgRoleMember || uc.OrganizationID != org || uc.PlatformAdmin || uc.IsAdmin {
		t.Fatalf("context: %+v", uc)
	}
}

func TestAuth_CachesMembershipForTTL(t *testing.T) {
	_, org, tok := setup(t)
	f := &fakeLookup{info: &services.ActiveMembership{OrgID: org, Role: models.OrgRoleOwner}}
	a := NewAuthMiddleware(f, 30*time.Second)
	now := time.Now()
	a.now = func() time.Time { return now }

	call(a, tok)
	call(a, tok)
	if f.calls != 1 {
		t.Fatalf("expected 1 lookup within TTL, got %d", f.calls)
	}

	// Membership removed; still served from cache until the TTL passes.
	f.info = nil
	if rec, _ := call(a, tok); rec.Code != 200 {
		t.Fatalf("within TTL got %d", rec.Code)
	}
	now = now.Add(31 * time.Second)
	if rec, _ := call(a, tok); rec.Code != http.StatusUnauthorized {
		t.Fatalf("after TTL got %d", rec.Code)
	}
}

func TestAuth_LookupErrorFailsClosedAndIsNotCached(t *testing.T) {
	_, org, tok := setup(t)
	f := &fakeLookup{err: errors.New("db down")}
	a := NewAuthMiddleware(f, 30*time.Second)
	if rec, _ := call(a, tok); rec.Code != http.StatusServiceUnavailable {
		t.Fatalf("got %d", rec.Code)
	}
	f.err, f.info = nil, &services.ActiveMembership{OrgID: org, Role: models.OrgRoleOwner}
	if rec, _ := call(a, tok); rec.Code != 200 {
		t.Fatalf("recovery got %d", rec.Code)
	}
}

func TestAuth_InvalidateDropsCache(t *testing.T) {
	uid, org, tok := setup(t)
	f := &fakeLookup{info: &services.ActiveMembership{OrgID: org, Role: models.OrgRoleOwner}}
	a := NewAuthMiddleware(f, time.Hour)
	call(a, tok)
	f.info = nil
	a.Invalidate(uid, org)
	if rec, _ := call(a, tok); rec.Code != http.StatusUnauthorized {
		t.Fatalf("got %d", rec.Code)
	}
}

func TestPlatformAdmin_RequiresPlatformAdminNotOrgOwner(t *testing.T) {
	run := func(uc UserContext) int {
		h := PlatformAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {}))
		req := httptest.NewRequestWithContext(context.Background(), "GET", "/", nil)
		req = req.WithContext(context.WithValue(req.Context(), UserContextKey, uc))
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, req)
		return rec.Code
	}
	if c := run(UserContext{OrgRole: models.OrgRoleOwner}); c != http.StatusForbidden {
		t.Fatalf("org owner got %d", c)
	}
	if c := run(UserContext{PlatformAdmin: true}); c != 200 {
		t.Fatalf("platform admin got %d", c)
	}
}

func scopedToken(t *testing.T, scope string) string {
	t.Helper()
	setup(t)
	user := &models.ApplicationUser{ID: uuid.New(), Email: "a@b.c"}
	tok, _, err := services.GenerateOAuthAccessToken(user, &services.ActiveMembership{OrgID: uuid.New(), Role: models.OrgRoleOwner}, scope, "", "", time.Now())
	if err != nil {
		t.Fatal(err)
	}
	return tok
}

func TestAuth_ScopedTokenIs403AndTouchesNothing(t *testing.T) {
	for _, scope := range []string{"assistant.tools", "assistant.admin assistant.tools"} {
		tok := scopedToken(t, scope)
		f := &fakeLookup{info: &services.ActiveMembership{OrgID: uuid.New(), Role: models.OrgRoleOwner}}
		a := NewAuthMiddleware(f, time.Second)
		rec, uc := call(a, tok)
		if rec.Code != http.StatusForbidden || !strings.Contains(rec.Body.String(), "mcp_token_scope") {
			t.Fatalf("%q: got %d %q", scope, rec.Code, rec.Body.String())
		}
		if uc != nil {
			t.Fatalf("%q: handler must not run", scope)
		}
		if f.calls != 0 {
			t.Fatalf("%q: membership lookup must not be called, got %d", scope, f.calls)
		}
	}
}

func TestAuth_UnscopedSessionTokenStillPasses(t *testing.T) {
	_, org, tok := setup(t)
	a := NewAuthMiddleware(&fakeLookup{info: &services.ActiveMembership{OrgID: org, Role: models.OrgRoleOwner}}, time.Second)
	if rec, uc := call(a, tok); rec.Code != 200 || uc == nil {
		t.Fatalf("got %d", rec.Code)
	}
}
