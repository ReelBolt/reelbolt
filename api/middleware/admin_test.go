package middleware

import (
	"bytes"
	"context"
	"log"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

func captureLog(t *testing.T) *bytes.Buffer {
	t.Helper()
	var buf bytes.Buffer
	old := log.Writer()
	log.SetOutput(&buf)
	t.Cleanup(func() { log.SetOutput(old) })
	return &buf
}

func adminReq(uc *UserContext) *http.Request {
	req := httptest.NewRequestWithContext(context.Background(), "DELETE", "/api/v1/admin/users/abc?token=secret", nil)
	if uc != nil {
		req = req.WithContext(context.WithValue(req.Context(), UserContextKey, *uc))
	}
	return req
}

// T-007: every request PlatformAdmin lets through is logged with the user, method, path (no
// query) and the handler's status; every refusal with the user and the reason.
func TestPlatformAdmin_LogsAllowedRequestWithStatus(t *testing.T) {
	buf := captureLog(t)
	h := PlatformAdmin(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(http.StatusNoContent) }))
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, adminReq(&UserContext{UserID: "u-1", PlatformAdmin: true}))
	if rec.Code != 204 {
		t.Fatalf("got %d", rec.Code)
	}
	out := buf.String()
	for _, w := range []string{"admin_request", "outcome=allowed", `user="u-1"`, `method="DELETE"`, `path="/api/v1/admin/users/abc"`, "status=204"} {
		if !strings.Contains(out, w) {
			t.Fatalf("log %q lacks %q", out, w)
		}
	}
	if strings.Contains(out, "secret") {
		t.Fatalf("query string logged: %q", out)
	}
}

func TestPlatformAdmin_ImplicitOKIsLoggedAs200(t *testing.T) {
	buf := captureLog(t)
	h := PlatformAdmin(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { _, _ = w.Write([]byte("{}")) }))
	h.ServeHTTP(httptest.NewRecorder(), adminReq(&UserContext{UserID: "u-1", PlatformAdmin: true}))
	if !strings.Contains(buf.String(), "status=200") {
		t.Fatalf("log %q", buf)
	}
}

func TestPlatformAdmin_LogsRefusals(t *testing.T) {
	for _, tc := range []struct {
		uc   *UserContext
		want []string
	}{
		{&UserContext{UserID: "u-2"}, []string{`user="u-2"`, `reason="not_platform_admin"`}},
		{nil, []string{`user="-"`, `reason="no_session"`}},
	} {
		buf := captureLog(t)
		called := false
		h := PlatformAdmin(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { called = true }))
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, adminReq(tc.uc))
		if rec.Code != 403 || called {
			t.Fatalf("got %d called=%v", rec.Code, called)
		}
		out := buf.String()
		for _, w := range append(tc.want, "admin_request", "outcome=refused", `method="DELETE"`, `path="/api/v1/admin/users/abc"`) {
			if !strings.Contains(out, w) {
				t.Fatalf("log %q lacks %q", out, w)
			}
		}
		if strings.Contains(out, "secret") {
			t.Fatalf("query string logged: %q", out)
		}
	}
}

func TestSetUserIDHeader(t *testing.T) {
	rec := httptest.NewRecorder()
	SetUserIDHeader(rec, "u-9")
	if rec.Header().Get(UserIDHeader) != "u-9" || UserIDHeader != "X-ReelBolt-Uid" {
		t.Fatalf("header %q", rec.Header())
	}
}

// The authenticated user id rides out on X-ReelBolt-Uid so nginx can log it (and strip it).
func TestAuth_SetsUserIDHeader(t *testing.T) {
	uid, org, tok := setup(t)
	a := NewAuthMiddleware(&fakeLookup{info: &services.ActiveMembership{OrgID: org, Role: models.OrgRoleMember}}, time.Second)
	rec, _ := call(a, tok)
	if rec.Code != 200 || rec.Header().Get(UserIDHeader) != uid.String() {
		t.Fatalf("got %d header %q", rec.Code, rec.Header().Get(UserIDHeader))
	}
}

// Review finding (log injection): a decoded path with CR, LF and spaces must not forge a line.
func TestPlatformAdmin_PathCannotForgeLogLines(t *testing.T) {
	for _, admin := range []bool{true, false} {
		buf := captureLog(t)
		req := httptest.NewRequestWithContext(context.Background(), "GET",
			"/api/v1/admin/x%0Aadmin_request%20outcome=allowed%20user=evil%0Dz", nil)
		req = req.WithContext(context.WithValue(req.Context(), UserContextKey, UserContext{UserID: "u-1", PlatformAdmin: admin}))
		PlatformAdmin(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {})).ServeHTTP(httptest.NewRecorder(), req)
		out := strings.TrimSuffix(buf.String(), "\n")
		if strings.ContainsAny(out, "\r\n") {
			t.Fatalf("admin=%v: more than one log line: %q", admin, out)
		}
		if !strings.Contains(out, `path="/api/v1/admin/x%0Aadmin_request%20outcome=allowed%20user=evil%0Dz"`) {
			t.Fatalf("admin=%v: path not quoted/escaped: %q", admin, out)
		}
	}
}
