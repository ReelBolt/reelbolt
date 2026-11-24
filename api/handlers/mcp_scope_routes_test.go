package handlers

import (
	"context"
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

func scopedRouterToken(t *testing.T) string {
	t.Helper()
	return scopedRouterTokenWithScope(t, "assistant.admin assistant.tools")
}

func scopedRouterTokenWithScope(t *testing.T, scope string) string {
	t.Helper()
	old := config.Cfg
	config.Cfg.JWTSigningKey = "0123456789abcdef0123456789abcdef"
	config.Cfg.JWTIssuer = "reelbolt-api"
	config.Cfg.JWTAudience = "reelbolt-inference"
	config.Cfg.PublicBaseURL = "https://example.test"
	t.Cleanup(func() { config.Cfg = old })
	user := &models.ApplicationUser{ID: uuid.New(), Email: "a@b.c", IsAdmin: true}
	tok, _, err := services.GenerateOAuthAccessToken(user, &services.ActiveMembership{OrgID: uuid.New(), Role: models.OrgRoleOwner, PlatformAdmin: true}, scope, "", "", time.Now())
	if err != nil {
		t.Fatal(err)
	}
	return tok
}

func serveScoped(t *testing.T, method, path, body string) *httptest.ResponseRecorder {
	t.Helper()
	return serveScopedWith(t, scopedRouterToken(t), method, path, body)
}

func serveScopedWith(t *testing.T, tok, method, path, body string) *httptest.ResponseRecorder {
	t.Helper()
	NewHandler()
	RegisterHandlers()
	req := httptest.NewRequestWithContext(context.Background(), method, path, strings.NewReader(body))
	req.Header.Set("Authorization", "Bearer "+tok)
	if body != "" {
		req.Header.Set("Content-Type", "application/json")
	}
	rec := httptest.NewRecorder()
	Router.ServeHTTP(rec, req)
	return rec
}

func TestScopedTokenRefusedOnAuthenticatedRoutes(t *testing.T) {
	cases := []struct{ name, method, path, body string }{
		{"switch-org", http.MethodPost, "/api/v1/auth/switch-org", `{"organizationId":"` + uuid.NewString() + `"}`},
		{"settings patch", http.MethodPatch, "/api/v1/auth/settings", `{"mcp_admin_tools_enabled":true}`},
		{"admin users", http.MethodGet, "/api/v1/admin/users", ""},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			rec := serveScoped(t, c.method, c.path, c.body)
			if rec.Code != http.StatusForbidden || !strings.Contains(rec.Body.String(), "mcp_token_scope") {
				t.Fatalf("got %d %q", rec.Code, rec.Body.String())
			}
			if len(rec.Result().Cookies()) != 0 || strings.Contains(rec.Body.String(), "token\"") {
				t.Fatalf("no token/cookie may be minted: %v", rec.Header())
			}
		})
	}
}

func TestScopedTokenIsNotASessionOnOAuthAuthorize(t *testing.T) {
	for _, method := range []string{http.MethodGet, http.MethodPost} {
		rec := serveScoped(t, method, "/oauth/authorize?client_id=x&response_type=code", "")
		if rec.Code != http.StatusForbidden {
			t.Fatalf("%s: got %d, want 403", method, rec.Code)
		}
		if loc := rec.Header().Get("Location"); loc != "" {
			t.Fatalf("%s: must not redirect, got %q", method, loc)
		}
		if !strings.Contains(rec.Body.String(), "mcp_token_scope") {
			t.Fatalf("%s: body %q", method, rec.Body.String())
		}
	}
}

func TestRunsWatchTokenRefusedOnAuthenticatedRoutes(t *testing.T) {
	tok := scopedRouterTokenWithScope(t, "runs.watch")
	for _, c := range []struct{ name, method, path string }{
		{"settings", http.MethodGet, "/api/v1/auth/settings"},
		{"admin users", http.MethodGet, "/api/v1/admin/users"},
	} {
		t.Run(c.name, func(t *testing.T) {
			rec := serveScopedWith(t, tok, c.method, c.path, "")
			if rec.Code != http.StatusForbidden || !strings.Contains(rec.Body.String(), "mcp_token_scope") {
				t.Fatalf("got %d %q", rec.Code, rec.Body.String())
			}
		})
	}
}
