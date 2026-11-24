package auth

// Guard-branch tests added by the C11 security review. handleToken and
// handleChangePassword reach the database a few statements in, and this package's
// fakes do not cover services.GetUserByEmail / services.GetUserByID, so the only
// branches reachable without a database are the ones asserted here: input validation
// and the missing-UserContext refusal. They are real behaviours, not coverage filler:
// the reset-password case pins the documented rule that a weak password must not burn
// the token, and handleToken's cases pin that a malformed request is a 400 rather than
// a 401 (the enumeration-sensitive path must not distinguish its inputs).
import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/google/uuid"
)

func send(handler http.HandlerFunc, target, body string) *httptest.ResponseRecorder {
	req := httptest.NewRequestWithContext(context.Background(), http.MethodPost, target, strings.NewReader(body))
	rec := httptest.NewRecorder()
	handler(rec, req)
	return rec
}

func sendAs(handler http.HandlerFunc, target, body string, id uuid.UUID) *httptest.ResponseRecorder {
	req := httptest.NewRequestWithContext(context.Background(), http.MethodPost, target, strings.NewReader(body))
	req = withUserContext(req, id)
	rec := httptest.NewRecorder()
	handler(rec, req)
	return rec
}

func TestHandleToken_RejectsMalformedBodiesBeforeTouchingTheStore(t *testing.T) {
	for _, tc := range []struct{ name, body string }{
		{"not json", "{"},
		{"empty object", "{}"},
		{"email without a password", "{\"email\":\"ada@example.com\"}"},
		{"password without an email", "{\"password\":\"hunter2hunter2\"}"},
	} {
		rec := send(handleToken, "/api/v1/auth/token", tc.body)
		if rec.Code != http.StatusBadRequest {
			t.Errorf("%s: got %d, want 400 (%s)", tc.name, rec.Code, rec.Body.String())
		}
	}
}

func TestHandleChangePassword_RequiresASession(t *testing.T) {
	rec := send(handleChangePassword, "/api/v1/auth/change-password", "{\"current_password\":\"a\",\"new_password\":\"b\"}")
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("got %d, want 401 without a UserContext", rec.Code)
	}
}

func TestHandleChangePassword_RejectsBadInputBeforeTheLookup(t *testing.T) {
	id := uuid.New()
	for _, tc := range []struct{ name, body string }{
		{"not json", "{"},
		{"missing both", "{}"},
		{"missing the new password", "{\"current_password\":\"hunter2hunter2\"}"},
		{"new password too short", "{\"current_password\":\"hunter2hunter2\",\"new_password\":\"short\"}"},
		{"both blank", "{\"current_password\":\"   \",\"new_password\":\"   \"}"},
	} {
		rec := sendAs(handleChangePassword, "/api/v1/auth/change-password", tc.body, id)
		if rec.Code != http.StatusBadRequest {
			t.Errorf("%s: got %d, want 400 (%s)", tc.name, rec.Code, rec.Body.String())
		}
	}
}

func TestHandleAccountDelete_RequiresASessionAndABody(t *testing.T) {
	if rec := send(handleAccountDelete, "/api/v1/auth/account/delete", "{\"password\":\"hunter2hunter2\"}"); rec.Code != http.StatusUnauthorized {
		t.Fatalf("without a UserContext: got %d, want 401", rec.Code)
	}

	// The body cases run against the harness because handleAccountDelete now reads
	// the account before it can tell the password path from the provider path
	// (C11 F-8). This account HAS a password, so an absent one is still the same
	// 400 it always was; an account with none is answered by
	// TestAccountDelete_PasswordAccountStillNeedsItsPassword and its siblings.
	_, user, _, _ := deletionHarness(t)
	for _, tc := range []struct{ name, body string }{
		{"not json", "{"},
		{"no password", "{}"},
		{"empty password", "{\"password\":\"\"}"},
	} {
		rec := sendAs(handleAccountDelete, "/api/v1/auth/account/delete", tc.body, user.ID)
		if rec.Code != http.StatusBadRequest {
			t.Errorf("%s: got %d, want 400 (%s)", tc.name, rec.Code, rec.Body.String())
		}
	}
}

func TestHandleAcceptTerms_RejectsAnythingThatIsNotAnAcceptance(t *testing.T) {
	if rec := send(handleAcceptTerms, "/api/v1/auth/accept-terms", goodAcceptTerms); rec.Code != http.StatusUnauthorized {
		t.Fatalf("without a UserContext: got %d, want 401", rec.Code)
	}

	// The cases below reach the deployment's own legal versions, so the fake
	// store has to be installed rather than the production one (F-7). A body
	// that does not name the served version is a 409, never a 400: it is a
	// well-formed request about a document that moved underneath it.
	h := setup(t, false)
	serveLegalVersions(t, h.store, "2026-11", "2026-11")

	id := uuid.New()
	tooWide := "{\"acceptTerms\":true,\"termsVersion\":\"" + strings.Repeat("v", 33) + "\",\"privacyVersion\":\"2026-11\"}"
	for _, tc := range []struct {
		name string
		body string
		want int
	}{
		{"not json", "{", http.StatusBadRequest},
		{"not accepted", "{\"acceptTerms\":false,\"termsVersion\":\"2026-11\",\"privacyVersion\":\"2026-11\"}", http.StatusBadRequest},
		{"no versions", "{\"acceptTerms\":true}", http.StatusConflict},
		{"blank versions", "{\"acceptTerms\":true,\"termsVersion\":\"  \",\"privacyVersion\":\"  \"}", http.StatusConflict},
		{"version wider than the column", tooWide, http.StatusConflict},
	} {
		rec := sendAs(handleAcceptTerms, "/api/v1/auth/accept-terms", tc.body, id)
		if rec.Code != tc.want {
			t.Errorf("%s: got %d, want %d (%s)", tc.name, rec.Code, tc.want, rec.Body.String())
		}
	}
}

func TestHandleInvitationAccept_RequiresASession(t *testing.T) {
	rec := send(handleInvitationAccept, "/api/v1/auth/invitations/tok/accept", "")
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("got %d, want 401 without a UserContext", rec.Code)
	}
}

func TestHandleVerifyEmail_RejectsAnEmptyToken(t *testing.T) {
	for _, body := range []string{"{", "{}", "{\"token\":\"   \"}"} {
		if rec := send(handleVerifyEmail, "/api/v1/auth/verify-email", body); rec.Code != http.StatusBadRequest {
			t.Errorf("%q: got %d, want 400", body, rec.Code)
		}
	}
}

func TestHandleResetPassword_ValidatesThePasswordBeforeBurningTheToken(t *testing.T) {
	for _, tc := range []struct{ name, body string }{
		{"not json", "{"},
		{"no token", "{\"new_password\":\"hunter2hunter2\"}"},
		{"weak password", "{\"token\":\"tok\",\"new_password\":\"short\"}"},
	} {
		rec := send(handleResetPassword, "/api/v1/auth/reset-password", tc.body)
		if rec.Code != http.StatusBadRequest {
			t.Errorf("%s: got %d, want 400 (%s)", tc.name, rec.Code, rec.Body.String())
		}
	}
}

func TestClientIP_PrefersTheProxyHeaderAndRejectsWhatIsNotAnAddress(t *testing.T) {
	for _, tc := range []struct {
		name        string
		realIP      string
		forwarded   string
		remoteAddr  string
		want        string
		wantNothing bool
	}{
		{name: "X-Real-IP wins over X-Forwarded-For", realIP: "203.0.113.9", forwarded: "198.51.100.7", remoteAddr: "10.0.0.1:5555", want: "203.0.113.9"},
		{name: "first X-Forwarded-For entry when X-Real-IP is absent", forwarded: "198.51.100.7, 10.0.0.1", remoteAddr: "10.0.0.1:5555", want: "198.51.100.7"},
		{name: "RemoteAddr when both headers are absent", remoteAddr: "192.0.2.4:5555", want: "192.0.2.4"},
		{name: "a non-address yields nothing", realIP: "not-an-ip", wantNothing: true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			req := httptest.NewRequestWithContext(context.Background(), http.MethodPost, "/api/v1/auth/signup", nil)
			req.RemoteAddr = tc.remoteAddr
			if tc.realIP != "" {
				req.Header.Set("X-Real-IP", tc.realIP)
			}
			if tc.forwarded != "" {
				req.Header.Set("X-Forwarded-For", tc.forwarded)
			}
			got := clientIP(req)
			if tc.wantNothing {
				if got != nil {
					t.Fatalf("got %q, want nil", *got)
				}
				return
			}
			if got == nil || *got != tc.want {
				t.Fatalf("got %v, want %q", got, tc.want)
			}
		})
	}
}
