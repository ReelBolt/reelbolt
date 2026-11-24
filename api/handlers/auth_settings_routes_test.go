package handlers

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/gorilla/mux"
)

// The settings routes are self-only by construction: the handlers take the user from the session and
// there is no id in the path. These assertions keep it that way — a future `{userId}` parameter would
// create an administrative surface nobody asked for, and re-introduce the "who may change somebody
// else's setting?" question the design deliberately avoids.
func TestAuthSettingsRoutesAreRegisteredAndSelfOnly(t *testing.T) {
	NewHandler()
	RegisterHandlers()

	// A path registered once per method produces one route per method, so the methods must be merged
	// rather than overwritten — the GET and the PATCH are two routes with the same template.
	registered := map[string][]string{}
	if err := Router.Walk(func(route *mux.Route, _ *mux.Router, _ []*mux.Route) error {
		path, err := route.GetPathTemplate()
		if err != nil {
			return err
		}
		methods, err := route.GetMethods()
		if err != nil {
			methods = nil
		}
		registered[path] = append(registered[path], methods...)
		return nil
	}); err != nil {
		t.Fatalf("walking the router: %v", err)
	}

	methods, ok := registered["/api/v1/auth/settings"]
	if !ok {
		t.Fatal("/api/v1/auth/settings is not registered")
	}
	has := func(m string) bool {
		for _, got := range methods {
			if got == m {
				return true
			}
		}
		return false
	}
	if !has(http.MethodGet) {
		t.Error("GET /api/v1/auth/settings is not registered")
	}
	if !has(http.MethodPatch) {
		t.Error("PATCH /api/v1/auth/settings is not registered")
	}

	// No settings route may carry a path variable — that is the whole self-only guarantee.
	for path := range registered {
		if strings.HasPrefix(path, "/api/v1/auth/settings") && strings.Contains(path, "{") {
			t.Errorf("%s carries a path parameter; the settings routes must be self-only", path)
		}
	}
}

// TestAuthSettingsRoutesAreBehindAuth proves the routes are mounted on the authenticated surface and
// not left public: a request with no session must be refused before any handler runs. This needs no
// database — middleware.Auth rejects a missing token before it looks anything up.
func TestAuthSettingsRoutesAreBehindAuth(t *testing.T) {
	NewHandler()
	RegisterHandlers()

	tests := []struct {
		name   string
		method string
		body   string
	}{
		{name: "GET", method: http.MethodGet},
		{name: "PATCH", method: http.MethodPatch, body: `{"mcpAdminToolsEnabled":true}`},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			var reader *strings.Reader
			if tc.body != "" {
				reader = strings.NewReader(tc.body)
			} else {
				reader = strings.NewReader("")
			}
			req := httptest.NewRequestWithContext(context.Background(), tc.method, "/api/v1/auth/settings", reader)
			if tc.body != "" {
				req.Header.Set("Content-Type", "application/json")
			}
			rec := httptest.NewRecorder()
			Router.ServeHTTP(rec, req)

			if rec.Code != http.StatusUnauthorized {
				t.Fatalf("status = %d, want 401 without a session", rec.Code)
			}
		})
	}
}
