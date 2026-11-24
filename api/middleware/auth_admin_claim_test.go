package middleware

import (
	"context"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/golang-jwt/jwt/v5"
	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/services"
)

// forge signs claims with the real key: what an attacker holding any valid
// signing path (or a stale legacy token) could present.
func forge(t *testing.T, method jwt.SigningMethod, c services.TokenClaims) string {
	t.Helper()
	tok, err := jwt.NewWithClaims(method, c).SignedString([]byte(config.Cfg.JWTSigningKey))
	if err != nil {
		t.Fatal(err)
	}
	return tok
}

func baseClaims(userID uuid.UUID) services.TokenClaims {
	now := time.Now()
	return services.TokenClaims{
		UserID: userID.String(),
		Email:  "a@b.c",
		RegisteredClaims: jwt.RegisteredClaims{
			Issuer:    config.Cfg.JWTIssuer,
			Audience:  jwt.ClaimStrings{config.Cfg.JWTAudience},
			Subject:   userID.String(),
			ExpiresAt: jwt.NewNumericDate(now.Add(time.Hour)),
			IssuedAt:  jwt.NewNumericDate(now),
		},
	}
}

// callAdmin runs tok through Auth then PlatformAdmin, like /api/v1/admin/users.
func callAdmin(a *AuthMiddleware, tok string) int {
	h := a.Handler(PlatformAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {})))
	req := httptest.NewRequestWithContext(context.Background(), "GET", "/api/v1/admin/users", nil)
	req.Header.Set("Authorization", "Bearer "+tok)
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, req)
	return rec.Code
}

func TestAuth_NoOrgTokenAdminClaimIsIgnored(t *testing.T) {
	setup(t)
	c := baseClaims(uuid.New())
	c.PlatformAdmin, c.IsAdmin = true, true
	if code := callAdmin(NewAuthMiddleware(&fakeLookup{}, time.Second), forge(t, jwt.SigningMethodHS256, c)); code != http.StatusForbidden {
		t.Fatalf("forged no-org admin claim got %d, want 403", code)
	}
	c.PlatformAdmin = false // legacy isAdmin-only shape
	if code := callAdmin(NewAuthMiddleware(&fakeLookup{}, time.Second), forge(t, jwt.SigningMethodHS256, c)); code != http.StatusForbidden {
		t.Fatalf("legacy isAdmin claim got %d, want 403", code)
	}
}

func TestAuth_TokenWithoutExpIs401(t *testing.T) {
	setup(t)
	c := baseClaims(uuid.New())
	c.ExpiresAt = nil
	rec, _ := call(NewAuthMiddleware(&fakeLookup{}, time.Second), forge(t, jwt.SigningMethodHS256, c))
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("no-exp token got %d, want 401", rec.Code)
	}
}

func TestAuth_TokenWithoutIatIs401(t *testing.T) {
	setup(t)
	c := baseClaims(uuid.New())
	c.IssuedAt = nil
	rec, _ := call(NewAuthMiddleware(&fakeLookup{}, time.Second), forge(t, jwt.SigningMethodHS256, c))
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("no-iat token got %d, want 401", rec.Code)
	}
}

func TestAuth_NonHS256Is401(t *testing.T) {
	setup(t)
	for _, m := range []jwt.SigningMethod{jwt.SigningMethodHS384, jwt.SigningMethodHS512} {
		rec, _ := call(NewAuthMiddleware(&fakeLookup{}, time.Second), forge(t, m, baseClaims(uuid.New())))
		if rec.Code != http.StatusUnauthorized {
			t.Fatalf("%s token got %d, want 401", m.Alg(), rec.Code)
		}
	}
	none, err := jwt.NewWithClaims(jwt.SigningMethodNone, baseClaims(uuid.New())).SignedString(jwt.UnsafeAllowNoneSignatureType)
	if err != nil {
		t.Fatal(err)
	}
	if rec, _ := call(NewAuthMiddleware(&fakeLookup{}, time.Second), none); rec.Code != http.StatusUnauthorized {
		t.Fatalf("alg=none got %d, want 401", rec.Code)
	}
}

type fakeAdmins struct {
	admin bool
	calls *int
}

func (f fakeAdmins) IsPlatformAdmin(_ context.Context, _ uuid.UUID) (bool, error) {
	if f.calls != nil {
		*f.calls++
	}
	return f.admin, nil
}

func TestAuth_NoOrgAdminClaimIgnoredWhenDatabaseSaysNo(t *testing.T) {
	setup(t)
	c := baseClaims(uuid.New())
	c.PlatformAdmin, c.IsAdmin = true, true
	a := NewAuthMiddleware(&fakeLookup{}, time.Second).WithPlatformAdminLookup(fakeAdmins{admin: false})
	if code := callAdmin(a, forge(t, jwt.SigningMethodHS256, c)); code != http.StatusForbidden {
		t.Fatalf("got %d, want 403", code)
	}
}

func TestAuth_RealAdminPassesWithAndWithoutOrg(t *testing.T) {
	setup(t)
	user, org := uuid.New(), uuid.New()

	// Org token: authority comes from the membership row (u.is_admin), not the claim.
	withOrg := baseClaims(user)
	withOrg.Org = org.String()
	a := NewAuthMiddleware(&fakeLookup{info: &services.ActiveMembership{OrgID: org, Role: "member", PlatformAdmin: true}}, time.Second)
	if code := callAdmin(a, forge(t, jwt.SigningMethodHS256, withOrg)); code != 200 {
		t.Fatalf("admin with org got %d", code)
	}

	// No-org token, claim false: authority still comes from the database, cached.
	calls := 0
	a = NewAuthMiddleware(&fakeLookup{}, time.Minute).WithPlatformAdminLookup(fakeAdmins{admin: true, calls: &calls})
	tok := forge(t, jwt.SigningMethodHS256, baseClaims(user))
	for i := 0; i < 2; i++ {
		if code := callAdmin(a, tok); code != 200 {
			t.Fatalf("admin without org got %d", code)
		}
	}
	if calls != 1 {
		t.Fatalf("admin lookup should be cached, got %d calls", calls)
	}
}

func TestAuth_OrgTokenAdminClaimIgnored(t *testing.T) {
	setup(t)
	user, org := uuid.New(), uuid.New()
	c := baseClaims(user)
	c.Org = org.String()
	c.PlatformAdmin, c.IsAdmin = true, true
	a := NewAuthMiddleware(&fakeLookup{info: &services.ActiveMembership{OrgID: org, Role: "owner"}}, time.Second)
	if code := callAdmin(a, forge(t, jwt.SigningMethodHS256, c)); code != http.StatusForbidden {
		t.Fatalf("got %d, want 403", code)
	}
}

// switchableAdmins answers whatever the database currently says.
type switchableAdmins struct{ admin *bool }

func (s switchableAdmins) IsPlatformAdmin(_ context.Context, _ uuid.UUID) (bool, error) {
	return *s.admin, nil
}

// A demotion (the admin handler's is_admin change calls InvalidateUser) removes platform
// authority on this replica at once, not after the 30s admin-lookup cache TTL.
func TestAuth_DemotedAdminLosesAdminImmediatelyAfterInvalidateUser(t *testing.T) {
	setup(t)
	user := uuid.New()
	isAdmin := true
	a := NewAuthMiddleware(&fakeLookup{}, time.Hour).WithPlatformAdminLookup(switchableAdmins{admin: &isAdmin})
	tok := forge(t, jwt.SigningMethodHS256, baseClaims(user))
	if code := callAdmin(a, tok); code != http.StatusOK {
		t.Fatalf("admin before demotion got %d", code)
	}

	isAdmin = false // the UPDATE application_users SET is_admin = false
	if code := callAdmin(a, tok); code != http.StatusOK {
		t.Fatalf("without eviction the cached flag should still answer 200, got %d", code)
	}
	a.InvalidateUser(user)
	if code := callAdmin(a, tok); code != http.StatusForbidden {
		t.Fatalf("demoted admin after InvalidateUser got %d, want 403", code)
	}
}
