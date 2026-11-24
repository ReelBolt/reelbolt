package auth

// C11 F-6, at the wiring and not only in the middleware: the JSON-only layer is
// only a defence if it is actually on the routes. These tests build the two
// routers this package registers and send the exact cross-site shape the finding
// describes — a fetch with Content-Type: text/plain and a JSON body, which no
// browser preflights — and then prove the same request with the content type
// every first-party caller sends still reaches its handler.
//
// Nothing here needs a database: the refusal happens before the handler runs, and
// the positive cases stop at a 401/400 the handler raises before its first store
// call.

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/gorilla/mux"
)

func registeredAuthRouter() *mux.Router {
	router := mux.NewRouter()
	RegisterAuthRoutes(router)
	RegisterAuthenticatedRoutes(router)
	return router
}

func TestAuthRoutes_RefuseABodyThatIsNotJSON(t *testing.T) {
	router := registeredAuthRouter()

	for _, target := range []string{
		"/api/v1/auth/token",
		"/api/v1/auth/signup",
		"/api/v1/auth/reset-password",
		"/api/v1/auth/change-password",
		"/api/v1/auth/accept-terms",
		"/api/v1/auth/account/delete",
		"/api/v1/auth/switch-org",
		"/api/v1/auth/invitations/tok/accept",
	} {
		req := httptest.NewRequestWithContext(context.Background(), http.MethodPost, target, strings.NewReader(`{"password":"hunter2hunter2"}`))
		// What a cross-site fetch may send without a CORS preflight.
		req.Header.Set("Content-Type", "text/plain")
		rec := httptest.NewRecorder()
		router.ServeHTTP(rec, req)
		if rec.Code != http.StatusUnsupportedMediaType {
			t.Errorf("%s: got %d, want 415 (%s)", target, rec.Code, rec.Body.String())
		}
		if !strings.Contains(rec.Body.String(), "application/json") {
			t.Errorf("%s: the refusal must name what is accepted: %s", target, rec.Body.String())
		}
	}
}

func TestAuthRoutes_AcceptTheContentTypeFirstPartyCallersSend(t *testing.T) {
	router := registeredAuthRouter()

	// The web dashboard's apiFetch and every script in infra/ send exactly this.
	for _, tc := range []struct {
		name        string
		target      string
		body        string
		contentType string
		wantStatus  int
	}{
		{"login", "/api/v1/auth/token", `{"password":"hunter2hunter2"}`, "application/json", http.StatusBadRequest},
		{"login with charset", "/api/v1/auth/token", `{"password":"hunter2hunter2"}`, "application/json; charset=utf-8", http.StatusBadRequest},
		{"delete without a session", "/api/v1/auth/account/delete", `{"password":"hunter2hunter2"}`, "application/json", http.StatusUnauthorized},
	} {
		t.Run(tc.name, func(t *testing.T) {
			req := httptest.NewRequestWithContext(context.Background(), http.MethodPost, tc.target, strings.NewReader(tc.body))
			req.Header.Set("Content-Type", tc.contentType)
			rec := httptest.NewRecorder()
			router.ServeHTTP(rec, req)
			// Reaching the handler at all is the point: 415 would mean the layer is
			// rejecting its own clients, and these two statuses are the handlers'
			// own answers (a missing email, a missing session).
			if rec.Code != tc.wantStatus {
				t.Fatalf("got %d, want %d (%s)", rec.Code, tc.wantStatus, rec.Body.String())
			}
		})
	}
}
