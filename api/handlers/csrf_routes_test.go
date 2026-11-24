package handlers

import (
	"context"
	"net/http"
	"net/http/httptest"
	"regexp"
	"strings"
	"testing"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
)

// routerAsPlatformAdmin registers the real routes with the session check replaced by one that
// admits every request as a platform admin, so what is under test is what sits BEHIND auth.
func routerAsPlatformAdmin(t *testing.T) *mux.Router {
	t.Helper()
	prevAuth, prevRouter := authMiddleware, Router
	authMiddleware = func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			uc := middleware.UserContext{
				UserID: uuid.NewString(), OrganizationID: uuid.New(), OrgRole: models.OrgRoleOwner,
				PlatformAdmin: true, IsAdmin: true,
			}
			next.ServeHTTP(w, r.WithContext(context.WithValue(r.Context(), middleware.UserContextKey, uc)))
		})
	}
	t.Cleanup(func() { authMiddleware, Router = prevAuth, prevRouter })
	NewHandler()
	RegisterHandlers()
	return Router
}

var pathVar = regexp.MustCompile(`\{[^}]+\}`)

// AUTHZ-GO-1 / API-GO-05 / AUTHN-04: every state-changing route of the authenticated and admin
// surfaces refuses a body that is not JSON, which is what a same-site form post or a
// preflight-free fetch can send. The routes are taken from the router, so one added later on
// these surfaces is covered without touching this test.
func TestSessionRoutes_RefuseAWriteThatIsNotJSON(t *testing.T) {
	router := routerAsPlatformAdmin(t)

	// The device's half of pairing is unauthenticated and signed; it is not a session surface.
	public := map[string]bool{
		"/api/v1/runners/pair/start": true,
		"/api/v1/runners/pair/poll":  true,
		"/api/v1/runners/token":      true,
	}
	guarded := []string{"/api/v1/orgs", "/api/v1/admin/", "/api/v1/runners", "/api/v1/notifications"}

	checked := 0
	err := router.Walk(func(route *mux.Route, _ *mux.Router, _ []*mux.Route) error {
		tmpl, err := route.GetPathTemplate()
		if err != nil {
			return err
		}
		methods, _ := route.GetMethods()
		covered := false
		for _, p := range guarded {
			covered = covered || strings.HasPrefix(tmpl, p)
		}
		if !covered || public[tmpl] {
			return nil
		}
		for _, method := range methods {
			if method == http.MethodGet || method == http.MethodHead {
				continue
			}
			target := pathVar.ReplaceAllString(tmpl, "x")
			req := httptest.NewRequestWithContext(context.Background(), method, target,
				strings.NewReader(`{"email":"mallory@example.com","displayName":"M","isPlatformAdmin":true}`))
			req.Header.Set("Content-Type", "text/plain")
			rec := httptest.NewRecorder()
			router.ServeHTTP(rec, req)
			if rec.Code != http.StatusUnsupportedMediaType {
				t.Errorf("%s %s: got %d, want 415 (%s)", method, tmpl, rec.Code, rec.Body.String())
			}
			checked++
		}
		return nil
	})
	if err != nil {
		t.Fatalf("walking the router: %v", err)
	}
	// orgs: 7 writes, admin users: 3, runner approve/deny/revoke: 3, notification preferences: 1.
	if checked < 14 {
		t.Fatalf("only %d state-changing routes were checked; the route list moved", checked)
	}
}

// What legitimate callers send keeps reaching the handler: a JSON body, and a write with no
// body at all (DELETE, and the bodiless POSTs). Both answers below come from the handler
// itself (a malformed id), never from the content-type layer.
func TestSessionRoutes_JSONAndBodilessWritesReachTheHandler(t *testing.T) {
	router := routerAsPlatformAdmin(t)

	for _, tc := range []struct {
		name, method, target, contentType, body string
	}{
		{"json", http.MethodPut, "/api/v1/admin/users/not-a-uuid", "application/json", `{"displayName":"A"}`},
		{"json with charset", http.MethodPut, "/api/v1/admin/users/not-a-uuid", "application/json; charset=utf-8", `{"displayName":"A"}`},
		{"no body", http.MethodDelete, "/api/v1/admin/users/not-a-uuid", "", ""},
	} {
		var body *strings.Reader
		req := httptest.NewRequestWithContext(context.Background(), tc.method, tc.target, http.NoBody)
		if tc.body != "" {
			body = strings.NewReader(tc.body)
			req = httptest.NewRequestWithContext(context.Background(), tc.method, tc.target, body)
			req.Header.Set("Content-Type", tc.contentType)
		}
		rec := httptest.NewRecorder()
		router.ServeHTTP(rec, req)
		if rec.Code != http.StatusBadRequest || !strings.Contains(rec.Body.String(), "invalid user ID") {
			t.Errorf("%s: got %d %s, want the handler's own 400", tc.name, rec.Code, rec.Body.String())
		}
	}
}

// A read is never subject to the content-type rule, whatever it carries.
func TestSessionRoutes_ReadsAreNotSubjectToTheContentTypeRule(t *testing.T) {
	router := routerAsPlatformAdmin(t)
	req := httptest.NewRequestWithContext(context.Background(), http.MethodGet, "/api/v1/admin/users/not-a-uuid", strings.NewReader("x"))
	req.Header.Set("Content-Type", "text/plain")
	rec := httptest.NewRecorder()
	router.ServeHTTP(rec, req)
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("got %d %s, want the handler's own 400", rec.Code, rec.Body.String())
	}
}
