package auth

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"testing"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

// T-007 (forensics): every sign-in, switch-org and password change/reset leaves one structured
// auth_event line with the user id (when known), the client ip and the outcome, and never the
// address, the password or the token.
func assertAuthEvent(t *testing.T, out string, want ...string) {
	t.Helper()
	var line string
	for _, l := range strings.Split(out, "\n") {
		if strings.Contains(l, "auth_event ") {
			line = l
		}
	}
	if line == "" {
		t.Fatalf("no auth_event line in log: %q", out)
	}
	for _, w := range want {
		if !strings.Contains(line, w) {
			t.Fatalf("auth_event line %q lacks %q", line, w)
		}
	}
	if strings.Contains(out, "@") {
		t.Fatalf("log carries an address: %q", out)
	}
}

func TestAuthLog_TokenMalformedIsALoggedFailure(t *testing.T) {
	buf := captureAuthLog(t)
	req := httptest.NewRequestWithContext(context.Background(), http.MethodPost, "/api/v1/auth/token", strings.NewReader(`{"email":"ada@example.com"}`))
	req.Header.Set("X-Real-IP", "203.0.113.9")
	handleToken(httptest.NewRecorder(), req)
	assertAuthEvent(t, buf.String(), "event=login", "outcome=failure", `ip="203.0.113.9"`, `user="-"`, "reason=")
}

func TestAuthLog_PasswordLoginSuccessLogsUserAndSetsUidHeader(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)
	user := &models.ApplicationUser{ID: uuid.New(), Email: "a@b.co", EmailVerifiedAt: ptrTime(h.now)}
	h.store.users[user.ID] = user
	buf := captureAuthLog(t)
	req := httptest.NewRequestWithContext(context.Background(), "POST", "/api/v1/auth/token", nil)
	req.Header.Set("X-Real-IP", "203.0.113.9")
	rec := httptest.NewRecorder()
	finishPasswordLogin(rec, req, user)
	if rec.Code != 200 {
		t.Fatalf("got %d", rec.Code)
	}
	assertAuthEvent(t, buf.String(), "event=login", "outcome=success", `user="`+user.ID.String()+`"`, `ip="203.0.113.9"`)
	if got := rec.Header().Get("X-ReelBolt-Uid"); got != user.ID.String() {
		t.Fatalf("X-ReelBolt-Uid %q", got)
	}
}

func TestAuthLog_SwitchOrgSuccessAndFailure(t *testing.T) {
	org := uuid.New()
	var last uuid.UUID
	buf := captureAuthLog(t)
	m := &services.ActiveMembership{OrgID: org, OrgName: "Acme", OrgKind: models.OrgKindTeam, Role: models.OrgRoleAdmin}
	if rec := runSwitch(t, m, `{"organizationId":"`+org.String()+`"}`, &last); rec.Code != 200 {
		t.Fatalf("got %d", rec.Code)
	}
	assertAuthEvent(t, buf.String(), "event=switch_org", "outcome=success", "user=")
	buf.Reset()
	if rec := runSwitch(t, nil, `{"organizationId":"`+org.String()+`"}`, &last); rec.Code != 404 {
		t.Fatalf("got %d", rec.Code)
	}
	assertAuthEvent(t, buf.String(), "event=switch_org", "outcome=failure", `reason="not_member"`)
}

func TestAuthLog_ChangePasswordWithoutSessionIsAFailure(t *testing.T) {
	buf := captureAuthLog(t)
	send(handleChangePassword, "/api/v1/auth/change-password", `{"current_password":"a","new_password":"b"}`)
	assertAuthEvent(t, buf.String(), "event=change_password", failureOutcome, reasonField("unauthenticated"))
}

func TestAuthLog_ResetPasswordSuccessAndFailure(t *testing.T) {
	h := setup(t, true)
	registeredUser(t, h)
	post(handleForgotPassword, `{"email":"new@example.com"}`)
	raw := tokenFromMail(t, h.mails[0])
	buf := captureAuthLog(t)
	if rec := post(handleResetPassword, `{"token":"`+raw+`","newPassword":"a brand new secret"}`); rec.Code != 200 {
		t.Fatalf("got %d", rec.Code)
	}
	u, _ := h.store.FindUserByEmail("new@example.com")
	assertAuthEvent(t, buf.String(), "event=reset_password", "outcome=success", `user="`+u.ID.String()+`"`, `ip="203.0.113.9"`)
	if strings.Contains(buf.String(), raw) || strings.Contains(buf.String(), "brand new") {
		t.Fatal("log carries the token or the password")
	}
	buf.Reset()
	post(handleResetPassword, `{"token":"`+raw+`","newPassword":"another good one"}`)
	assertAuthEvent(t, buf.String(), "event=reset_password", failureOutcome, reasonField("invalid_token"))
}

func TestAuthLog_OAuthCallbackSuccessAndFailure(t *testing.T) {
	oauthSetup(t)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"123","email":"ada@example.com","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)
	buf := captureAuthLog(t)
	req := httptest.NewRequestWithContext(context.Background(), "GET", "/api/v1/auth/oauth/google/callback?code=c&state=st-1", nil)
	req.Header.Set("X-Real-IP", "203.0.113.9")
	req = mux.SetURLVars(req, map[string]string{"provider": "google"})
	req.AddCookie(&http.Cookie{Name: oauthStateCookie, Value: "google:st-1:verifier-abc"})
	rec := httptest.NewRecorder()
	handleOAuthCallback(rec, req)
	if rec.Code != http.StatusFound || strings.Contains(rec.Header().Get("Location"), "error=") {
		t.Fatalf("got %d %s", rec.Code, rec.Header().Get("Location"))
	}
	assertAuthEvent(t, buf.String(), "event=oauth_login", "outcome=success", `provider="google"`, `ip="203.0.113.9"`)
	if strings.Contains(buf.String(), `user="-"`) {
		t.Fatalf("success without user id: %s", buf)
	}

	buf.Reset()
	req = httptest.NewRequestWithContext(context.Background(), "GET", "/api/v1/auth/oauth/google/callback?error=access_denied&state=st-1", nil)
	req = mux.SetURLVars(req, map[string]string{"provider": "google"})
	handleOAuthCallback(httptest.NewRecorder(), req)
	assertAuthEvent(t, buf.String(), "event=oauth_login", failureOutcome, reasonField("oauth_failed"))
}

// Review finding (log injection): client-influenced values are quoted, one line per event.
func TestAuthLog_FieldsAreQuoted(t *testing.T) {
	buf := captureAuthLog(t)
	req := httptest.NewRequestWithContext(context.Background(), "POST", "/x", nil)
	req.Header.Set("X-Real-IP", "203.0.113.9")
	logAuthEvent(req, "login", "failure", uuid.Nil, "bad\nauth_event outcome=success", "provider", "g\noogle")
	out := strings.TrimSuffix(buf.String(), "\n")
	if strings.ContainsAny(out, "\r\n") {
		t.Fatalf("more than one line: %q", out)
	}
	for _, w := range []string{`ip="203.0.113.9"`, `reason="bad\nauth_event outcome=success"`, `provider="g\noogle"`} {
		if !strings.Contains(out, w) {
			t.Fatalf("%q lacks %q", out, w)
		}
	}
}

// reasonField is the quoted reason pair logAuthEvent writes.
func reasonField(word string) string { return "reason=" + strconv.Quote(word) }

// failureOutcome is the outcome pair of a refused attempt.
const failureOutcome = "outcome=failure"
