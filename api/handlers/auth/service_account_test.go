package auth

import (
	"database/sql/driver"
	"net/http"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"

	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/testutil/fakesql"
)

func serviceAccountUser(email string) *models.ApplicationUser {
	now := time.Now().UTC()
	return &models.ApplicationUser{
		ID: uuid.New(), Email: email, DisplayName: "ci key", IsServiceAccount: true,
		EmailVerifiedAt: &now, PasswordHash: dummyPasswordHash,
	}
}

func TestServiceAccount_LoginIsTheSame401AsAWrongPassword(t *testing.T) {
	const body = `{"email":"apikey+abc@service.invalid","password":"hunter2hunter2"}`
	// No database row at all: this is the baseline the body must equal.
	fakesql.Install(t, nil)
	wrong := send(handleToken, "/api/v1/auth/token", `{"email":"ghost@example.com","password":"hunter2hunter2"}`)

	// A database that hands back the service-account row regardless of the filter: the handler
	// itself must still refuse with the identical answer.
	cols := []string{"id", "email", "display_name", "password_hash", "is_service_account"}
	var sawFilter bool
	db := fakesql.Install(t, func(q string, _ []driver.Value) ([]string, [][]driver.Value) {
		if strings.Contains(q, "application_users") {
			sawFilter = strings.Contains(q, "is_service_account")
			return cols, [][]driver.Value{{uuid.NewString(), "apikey+abc@service.invalid", "ci key", dummyPasswordHash, true}}
		}
		return nil, nil
	})
	rec := send(handleToken, "/api/v1/auth/token", body)
	if rec.Code != http.StatusUnauthorized || rec.Body.String() != wrong.Body.String() {
		t.Fatalf("got %d %q, want %d %q", rec.Code, rec.Body.String(), wrong.Code, wrong.Body.String())
	}
	if !sawFilter || !db.Contains("is_service_account") {
		t.Fatal("the user lookup does not exclude service accounts")
	}
	if len(rec.Result().Cookies()) != 0 {
		t.Fatal("a session cookie was set")
	}
}

func TestServiceAccount_ForgotPasswordAndResendSendNothing(t *testing.T) {
	h := setup(t, true)
	u := serviceAccountUser("apikey+abc@service.invalid")
	u.EmailVerifiedAt = nil
	h.store.users[u.ID] = u

	for name, call := range map[string]func() int{
		"forgot": func() int { return post(handleForgotPassword, `{"email":"apikey+abc@service.invalid"}`).Code },
		"resend": func() int { return post(handleResendVerification, `{"email":"apikey+abc@service.invalid"}`).Code },
	} {
		if code := call(); code != http.StatusAccepted {
			t.Errorf("%s: got %d, want the enumeration-safe 202", name, code)
		}
	}
	if len(h.mails) != 0 {
		t.Fatalf("mail was sent to a service account: %+v", h.mails)
	}
}

func TestServiceAccount_SignupWithReservedDomainIsRefused(t *testing.T) {
	h := setup(t, true)
	body := strings.Replace(goodSignup, "New@Example.COM", "apikey+abc@service.invalid", 1)
	rec := post(handleSignup, body)
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if len(h.store.users) != 0 || len(h.mails) != 0 {
		t.Fatal("an account or mail was created")
	}
}

func TestServiceAccount_OAuthRefusesAndNeverLinks(t *testing.T) {
	for name, email := range map[string]string{
		"reserved domain":      "apikey+abc@service.invalid",
		"existing service row": "key-owner@example.com",
	} {
		t.Run(name, func(t *testing.T) {
			h := oauthSetup(t)
			u := serviceAccountUser(email)
			h.store.users[u.ID] = u
			f := newFakeProvider(t)
			f.userInfo = `{"sub":"123","email":"` + email + `","email_verified":true,"name":"X"}`
			installGoogle(t, f)

			rec := oauthCall(t, handleOAuthCallback, "google", "?code=c&state=st-1", "google:st-1:verifier-abc")
			loc := rec.Header().Get("Location")
			if rec.Code != http.StatusFound || !strings.Contains(loc, "error=oauth_") {
				t.Fatalf("got %d to %q", rec.Code, loc)
			}
			if cookieMap(rec)["reelbolt_token"] != nil {
				t.Fatal("a session was issued to a service account")
			}
			if len(h.store.users) != 1 {
				t.Fatalf("an account was created or replaced: %d users", len(h.store.users))
			}
		})
	}
}
