package middleware

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

type fakeSessions struct {
	state services.SessionState
	err   error
	calls int
}

func (f *fakeSessions) GetSessionState(uuid.UUID) (services.SessionState, error) {
	f.calls++
	return f.state, f.err
}

func authWithSessions(org uuid.UUID, s *fakeSessions, ttl time.Duration) *AuthMiddleware {
	return NewAuthMiddleware(&fakeLookup{info: &services.ActiveMembership{OrgID: org, Role: models.OrgRoleOwner}}, ttl).
		WithSessionLookup(s)
}

func TestSession_TokenIssuedBeforeInvalidationIs401(t *testing.T) {
	_, org, tok := setup(t) // iat = now
	invalidated := time.Now().Add(time.Minute)
	a := authWithSessions(org, &fakeSessions{state: services.SessionState{Exists: true, TokensInvalidatedAt: &invalidated}}, time.Second)
	rec, uc := call(a, tok)
	if rec.Code != http.StatusUnauthorized || uc != nil {
		t.Fatalf("got %d", rec.Code)
	}
}

func TestSession_TokenIssuedAfterInvalidationPasses(t *testing.T) {
	_, org, tok := setup(t)
	invalidated := time.Now().Add(-time.Minute)
	a := authWithSessions(org, &fakeSessions{state: services.SessionState{Exists: true, TokensInvalidatedAt: &invalidated}}, time.Second)
	if rec, _ := call(a, tok); rec.Code != 200 {
		t.Fatalf("got %d", rec.Code)
	}
}

func TestSession_NeverInvalidatedPasses_UnknownUserIs401(t *testing.T) {
	_, org, tok := setup(t)
	a := authWithSessions(org, &fakeSessions{state: services.SessionState{Exists: true}}, time.Second)
	if rec, _ := call(a, tok); rec.Code != 200 {
		t.Fatalf("got %d", rec.Code)
	}
	gone := authWithSessions(org, &fakeSessions{state: services.SessionState{Exists: false}}, time.Second)
	if rec, _ := call(gone, tok); rec.Code != http.StatusUnauthorized {
		t.Fatalf("deleted user got %d", rec.Code)
	}
}

func TestSession_CachedForTTL_InvalidateAppliesImmediately(t *testing.T) {
	uid, org, tok := setup(t)
	s := &fakeSessions{state: services.SessionState{Exists: true}}
	a := authWithSessions(org, s, 30*time.Second)
	now := time.Now()
	a.now = func() time.Time { return now }

	call(a, tok)
	call(a, tok)
	if s.calls != 1 {
		t.Fatalf("expected 1 lookup within TTL, got %d", s.calls)
	}

	invalidated := time.Now().Add(time.Minute)
	s.state = services.SessionState{Exists: true, TokensInvalidatedAt: &invalidated}
	if rec, _ := call(a, tok); rec.Code != 200 {
		t.Fatalf("served from cache within TTL, got %d", rec.Code)
	}
	a.InvalidateSession(uid)
	if rec, _ := call(a, tok); rec.Code != http.StatusUnauthorized {
		t.Fatalf("after InvalidateSession got %d", rec.Code)
	}
}

func TestSession_LookupErrorFailsClosedAndIsNotCached(t *testing.T) {
	_, org, tok := setup(t)
	s := &fakeSessions{err: errors.New("db down")}
	a := authWithSessions(org, s, 30*time.Second)
	if rec, _ := call(a, tok); rec.Code != http.StatusServiceUnavailable {
		t.Fatalf("got %d", rec.Code)
	}
	s.err, s.state = nil, services.SessionState{Exists: true}
	if rec, _ := call(a, tok); rec.Code != 200 {
		t.Fatalf("recovery got %d", rec.Code)
	}
}

func TestTokenIssuedBeforeInvalidation_SecondPrecision(t *testing.T) {
	inv := time.Date(2026, 10, 6, 12, 0, 0, 900_000_000, time.UTC)
	cases := []struct {
		name string
		iat  *time.Time
		want bool
	}{
		{"earlier second", ptr(time.Date(2026, 10, 6, 11, 59, 59, 0, time.UTC)), true},
		{"same second (issued right after the reset)", ptr(time.Date(2026, 10, 6, 12, 0, 0, 0, time.UTC)), false},
		{"later", ptr(time.Date(2026, 10, 6, 12, 0, 1, 0, time.UTC)), false},
		{"no iat fails closed", nil, true},
	}
	for _, c := range cases {
		if got := services.TokenIssuedBeforeInvalidation(c.iat, &inv); got != c.want {
			t.Errorf("%s: got %v", c.name, got)
		}
	}
	if services.TokenIssuedBeforeInvalidation(nil, nil) {
		t.Error("a never-invalidated user accepts any token")
	}
}

func ptr(t time.Time) *time.Time { return &t }

func TestSession_ServiceAccountTokenIs401(t *testing.T) {
	_, org, tok := setup(t)
	a := authWithSessions(org, &fakeSessions{state: services.SessionState{Exists: true, IsServiceAccount: true}}, time.Second)
	rec, uc := call(a, tok)
	if rec.Code != http.StatusUnauthorized || uc != nil {
		t.Fatalf("got %d", rec.Code)
	}
}

// admit passes one request through a and returns the SessionCheck its handler was given.
func admit(t *testing.T, a *AuthMiddleware, tok string) (*SessionCheck, int) {
	t.Helper()
	var check *SessionCheck
	h := a.Handler(http.HandlerFunc(func(_ http.ResponseWriter, r *http.Request) {
		check, _ = GetSessionCheck(r)
	}))
	req := httptest.NewRequestWithContext(context.Background(), "GET", "/api/v1/workflows/events", nil)
	req.Header.Set("Authorization", "Bearer "+tok)
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, req)
	return check, rec.Code
}

// AUTHZ-GO-2 / API-GO-04: the check a stream re-runs is the middleware's own, so it turns
// false for each thing the middleware would refuse: a session invalidated after connect, a
// revoked membership, a changed role, a deleted account.
func TestSessionCheck_FollowsWhatTheMiddlewareWouldDecide(t *testing.T) {
	_, org, tok := setup(t)
	owner := &services.ActiveMembership{OrgID: org, Role: models.OrgRoleOwner}

	for name, revoke := range map[string]func(l *fakeLookup, s *fakeSessions){
		"session invalidated": func(_ *fakeLookup, s *fakeSessions) {
			at := time.Now().Add(time.Minute)
			s.state.TokensInvalidatedAt = &at
		},
		"account deleted":    func(_ *fakeLookup, s *fakeSessions) { s.state.Exists = false },
		"membership revoked": func(l *fakeLookup, _ *fakeSessions) { l.info = nil },
		"role changed": func(l *fakeLookup, _ *fakeSessions) {
			l.info = &services.ActiveMembership{OrgID: org, Role: models.OrgRoleBilling}
		},
		"platform admin changed": func(l *fakeLookup, _ *fakeSessions) {
			l.info = &services.ActiveMembership{OrgID: org, Role: models.OrgRoleOwner, PlatformAdmin: true}
		},
	} {
		lookup := &fakeLookup{info: owner}
		sessions := &fakeSessions{state: services.SessionState{Exists: true}}
		// No cache, so each check reads the lookups as they are now.
		a := NewAuthMiddleware(lookup, 0).WithSessionLookup(sessions)

		check, code := admit(t, a, tok)
		if code != http.StatusOK || check == nil {
			t.Fatalf("%s: not admitted (%d)", name, code)
		}
		if check.Expires.IsZero() || time.Until(check.Expires) <= 0 {
			t.Fatalf("%s: Expires = %v, want the token's exp", name, check.Expires)
		}
		if !check.StillValid() {
			t.Fatalf("%s: an unchanged session must stay valid", name)
		}
		revoke(lookup, sessions)
		if check.StillValid() {
			t.Errorf("%s: the session check still passes", name)
		}
	}
}

// A lookup that fails says nothing about the session: the stream is kept and asked again.
func TestSessionCheck_LookupErrorKeepsTheStream(t *testing.T) {
	_, org, tok := setup(t)
	lookup := &fakeLookup{info: &services.ActiveMembership{OrgID: org, Role: models.OrgRoleOwner}}
	a := NewAuthMiddleware(lookup, 0).WithSessionLookup(&fakeSessions{state: services.SessionState{Exists: true}})
	check, _ := admit(t, a, tok)
	lookup.err = errors.New("db down")
	if !check.StillValid() {
		t.Fatal("a failed lookup closed the stream")
	}
	lookup.err, lookup.info = nil, nil
	if check.StillValid() {
		t.Fatal("the check did not recover and refuse")
	}
}

// AUTHZ-GO-5: InvalidateUser drops the user's session state and every membership, so an admin
// reset, deletion or platform-admin change applies at once on this replica.
func TestInvalidateUser_DropsSessionAndEveryMembership(t *testing.T) {
	user, org, tok := setup(t)
	lookup := &fakeLookup{info: &services.ActiveMembership{OrgID: org, Role: models.OrgRoleOwner, PlatformAdmin: true}}
	sessions := &fakeSessions{state: services.SessionState{Exists: true}}
	a := NewAuthMiddleware(lookup, time.Hour).WithSessionLookup(sessions)

	if _, uc := call(a, tok); uc == nil || !uc.PlatformAdmin {
		t.Fatal("setup: expected a platform admin")
	}
	// Demoted and reset in the database; the cache still says otherwise.
	lookup.info = &services.ActiveMembership{OrgID: org, Role: models.OrgRoleOwner}
	if _, uc := call(a, tok); uc == nil || !uc.PlatformAdmin {
		t.Fatal("setup: expected the cached membership to be served")
	}

	a.InvalidateUser(uuid.New()) // another user: nothing of this one is dropped
	if _, uc := call(a, tok); uc == nil || !uc.PlatformAdmin {
		t.Fatal("invalidating another user dropped this one's cache")
	}

	a.InvalidateUser(user)
	if _, uc := call(a, tok); uc == nil || uc.PlatformAdmin {
		t.Fatal("the demotion did not apply after InvalidateUser")
	}
	invalidated := time.Now().Add(time.Minute)
	sessions.state.TokensInvalidatedAt = &invalidated
	a.InvalidateUser(user)
	if rec, _ := call(a, tok); rec.Code != http.StatusUnauthorized {
		t.Fatalf("the reset did not apply after InvalidateUser: %d", rec.Code)
	}
}
