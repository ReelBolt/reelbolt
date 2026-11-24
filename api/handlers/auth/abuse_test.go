package auth

import (
	"context"
	"errors"
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

func (f *fakeStore) CountRecentTokens(userID uuid.UUID, since time.Time) (int, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	n := 0
	for _, t := range f.tokens {
		if t.UserID == userID && !t.CreatedAt.Before(since) {
			n++
		}
	}
	return n, nil
}

func (f *fakeStore) CountSignupsFromIP(ip string, since time.Time) (int, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	n := 0
	for _, u := range f.users {
		if u.SignupIP != nil && *u.SignupIP == ip && !u.CreatedAt.Before(since) {
			n++
		}
	}
	return n, nil
}

func signupBody(email string) string {
	return `{"email":"` + email + `","password":"correct horse","acceptTerms":true,"termsVersion":"2026-10","privacyVersion":"2026-10"}`
}

func postFrom(handler http.HandlerFunc, body, ip string) *httptest.ResponseRecorder {
	req := httptest.NewRequestWithContext(context.Background(), "POST", "/x", strings.NewReader(body))
	req.Header.Set("X-Real-IP", ip)
	rec := httptest.NewRecorder()
	handler(rec, req)
	return rec
}

func TestSignup_DisposableDomainRejected(t *testing.T) {
	h := setup(t, true)
	config.Cfg.AbuseBlockDisposable = true
	rec := post(handleSignup, signupBody("bot@mailinator.com"))
	if rec.Code != 400 || !strings.Contains(rec.Body.String(), "permanent email") {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	if len(h.store.users) != 0 || len(h.mails) != 0 {
		t.Fatal("nothing may be created or mailed")
	}
	// A subdomain of a listed domain is caught too, and a normal domain passes.
	if rec := post(handleSignup, signupBody("bot@x.mailinator.com")); rec.Code != 400 {
		t.Fatalf("subdomain got %d", rec.Code)
	}
	if rec := post(handleSignup, signupBody("real@example.com")); rec.Code != 202 {
		t.Fatalf("real got %d", rec.Code)
	}
	// Opt-out switch.
	config.Cfg.AbuseBlockDisposable = false
	if rec := postFrom(handleSignup, signupBody("bot@mailinator.com"), "198.51.100.1"); rec.Code != 202 {
		t.Fatalf("blocklist off got %d", rec.Code)
	}
}

func TestSignup_PerIPCaps(t *testing.T) {
	h := setup(t, true)
	for i := 0; i < 3; i++ {
		if rec := postFrom(handleSignup, signupBody("u"+string(rune('a'+i))+"@example.com"), "203.0.113.50"); rec.Code != 202 {
			t.Fatalf("signup %d got %d", i, rec.Code)
		}
	}
	rec := postFrom(handleSignup, signupBody("ud@example.com"), "203.0.113.50")
	if rec.Code != 429 || rec.Header().Get("Retry-After") == "" {
		t.Fatalf("4th in the hour: %d", rec.Code)
	}
	// Another address is unaffected.
	if rec := postFrom(handleSignup, signupBody("ue@example.com"), "203.0.113.51"); rec.Code != 202 {
		t.Fatalf("other ip got %d", rec.Code)
	}
	// An hour later the hourly window has cleared, but the daily cap (10) still counts.
	h.now = h.now.Add(61 * time.Minute)
	for i := 0; i < 3; i++ {
		if rec := postFrom(handleSignup, signupBody("v"+string(rune('a'+i))+"@example.com"), "203.0.113.50"); rec.Code != 202 {
			t.Fatalf("second hour signup %d got %d", i, rec.Code)
		}
		h.now = h.now.Add(61 * time.Minute)
	}
	if rec := postFrom(handleSignup, signupBody("zz@example.com"), "203.0.113.50"); rec.Code != 202 {
		t.Fatalf("7th within a day (hour clear) got %d", rec.Code)
	}
}

func TestSignup_DailyCap(t *testing.T) {
	h := setup(t, true)
	ip := "203.0.113.60"
	for i := 0; i < 10; i++ {
		if rec := postFrom(handleSignup, signupBody(strings.Repeat("a", i+1)+"@example.com"), ip); rec.Code != 202 {
			t.Fatalf("signup %d got %d", i, rec.Code)
		}
		h.now = h.now.Add(61 * time.Minute) // clear the hourly window each time
	}
	if rec := postFrom(handleSignup, signupBody("over@example.com"), ip); rec.Code != 429 {
		t.Fatalf("11th in 24h got %d", rec.Code)
	}
	h.now = h.now.Add(24 * time.Hour)
	if rec := postFrom(handleSignup, signupBody("later@example.com"), ip); rec.Code != 202 {
		t.Fatalf("next day got %d", rec.Code)
	}
}

func TestSignup_MXCheck(t *testing.T) {
	setup(t, true)
	config.Cfg.AbuseMXCheck = true
	acct.checkMX = func(_ context.Context, email string) error {
		if strings.HasSuffix(email, "@nomail.invalid") {
			return services.ErrNoMailServer
		}
		return nil
	}
	if rec := post(handleSignup, signupBody("a@nomail.invalid")); rec.Code != 400 {
		t.Fatalf("no MX got %d", rec.Code)
	}
	if rec := post(handleSignup, signupBody("a@example.com")); rec.Code != 202 {
		t.Fatalf("with MX got %d", rec.Code)
	}
}

func TestChallenge_RequiredWhenConfigured(t *testing.T) {
	h := setup(t, true)
	acct.verifyChallenge = func(_ context.Context, token, _ string) (bool, error) {
		if token == "" {
			return false, nil
		}
		if token == "boom" {
			return false, errors.New("cloudflare unreachable")
		}
		return token == "good", nil
	}
	if rec := post(handleSignup, signupBody("a@example.com")); rec.Code != 400 {
		t.Fatalf("missing token got %d", rec.Code)
	}
	if rec := post(handleSignup, strings.Replace(signupBody("a@example.com"), `"acceptTerms"`, `"turnstileToken":"boom","acceptTerms"`, 1)); rec.Code != 400 {
		t.Fatalf("verifier error must fail closed, got %d", rec.Code)
	}
	if rec := post(handleSignup, strings.Replace(signupBody("a@example.com"), `"acceptTerms"`, `"turnstileToken":"good","acceptTerms"`, 1)); rec.Code != 202 {
		t.Fatalf("good token got %d", rec.Code)
	}
	if len(h.store.users) != 1 {
		t.Fatalf("users: %d", len(h.store.users))
	}
	if rec := post(handleForgotPassword, `{"email":"a@example.com"}`); rec.Code != 400 {
		t.Fatalf("forgot without token got %d", rec.Code)
	}
	if rec := post(handleResendVerification, `{"email":"a@example.com","turnstileToken":"good"}`); rec.Code != 202 {
		t.Fatalf("resend with token got %d", rec.Code)
	}
}

func TestPerEmailLimit_ThreeMailsPerHourThenSilentSuppression(t *testing.T) {
	h := setup(t, true)
	post(handleSignup, goodSignup) // token 1 (+ its mail)
	h.mails = nil

	for i := 0; i < 5; i++ {
		rec := post(handleResendVerification, `{"email":"new@example.com"}`)
		if rec.Code != 202 { // always the uniform answer, even when suppressed
			t.Fatalf("resend %d got %d", i, rec.Code)
		}
	}
	if len(h.mails) != 2 { // signup issued 1 of the 3 allowed this hour
		t.Fatalf("expected 2 further mails, got %d", len(h.mails))
	}
	// The cap is shared by purposes: a reset request is suppressed too.
	registeredAfter := len(h.mails)
	post(handleForgotPassword, `{"email":"new@example.com"}`)
	if len(h.mails) != registeredAfter {
		t.Fatal("reset mail must be suppressed once 3 tokens were issued this hour")
	}
	// An hour later the window has rolled.
	h.now = h.now.Add(61 * time.Minute)
	post(handleResendVerification, `{"email":"new@example.com"}`)
	if len(h.mails) != registeredAfter+1 {
		t.Fatal("mail must flow again after the window")
	}
}

func TestPerEmailLimit_UnknownEmailLooksIdentical(t *testing.T) {
	setup(t, true)
	a := post(handleResendVerification, `{"email":"ghost@example.com"}`)
	post(handleSignup, goodSignup)
	for i := 0; i < 4; i++ {
		post(handleResendVerification, `{"email":"new@example.com"}`)
	}
	b := post(handleResendVerification, `{"email":"new@example.com"}`) // suppressed
	if a.Code != b.Code || a.Body.String() != b.Body.String() {
		t.Fatalf("%d %q vs %d %q", a.Code, a.Body, b.Code, b.Body)
	}
}

var _ = models.UserTokenVerifyEmail
