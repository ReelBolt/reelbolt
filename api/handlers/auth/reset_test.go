package auth

import (
	"strings"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
	"golang.org/x/crypto/bcrypt"
)

func (f *fakeStore) ResetTokenUsable(hash string, now time.Time) (bool, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, t := range f.tokens {
		if t.TokenHash == hash && t.Purpose == models.UserTokenResetPassword {
			return t.UsedAt == nil && t.ExpiresAt.After(now), nil
		}
	}
	return false, nil
}

func (f *fakeStore) ConsumeResetPassword(hash, passwordHash string, now time.Time) (*models.ApplicationUser, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	t, err := f.consume(models.UserTokenResetPassword, hash, now)
	if err != nil {
		return nil, err
	}
	u := f.users[t.UserID]
	u.PasswordHash = passwordHash
	u.MustChangePassword = false
	u.TokensInvalidatedAt = &now
	for _, o := range f.tokens {
		if o.UserID == u.ID && o.Purpose == models.UserTokenResetPassword && o.UsedAt == nil {
			o.UsedAt = &now
		}
	}
	c := *u
	return &c, nil
}

// registeredUser signs up and verifies an account, returning its email.
func registeredUser(t *testing.T, h *harness) {
	t.Helper()
	post(handleSignup, goodSignup)
	post(handleVerifyEmail, `{"token":"`+tokenFromMail(t, h.mails[0])+`"}`)
	h.mails = nil
}

func TestForgotPassword_SameResponseForKnownAndUnknown(t *testing.T) {
	h := setup(t, true)
	registeredUser(t, h)

	known := post(handleForgotPassword, `{"email":"New@Example.com"}`)
	unknown := post(handleForgotPassword, `{"email":"ghost@example.com"}`)
	if known.Code != 202 || unknown.Code != 202 || known.Body.String() != unknown.Body.String() {
		t.Fatalf("%d %q vs %d %q", known.Code, known.Body, unknown.Code, unknown.Body)
	}
	if len(h.mails) != 1 || h.mails[0].tmpl != services.TemplateResetPassword || h.mails[0].to != "new@example.com" {
		t.Fatalf("mails: %+v", h.mails)
	}
	if !strings.HasPrefix(h.mails[0].data.Link, "https://reelbolt.test/app/reset-password?token=") {
		t.Fatalf("link %s", h.mails[0].data.Link)
	}
	if h.mails[0].data.ExpiresIn != "1 hour" {
		t.Fatalf("expires %q", h.mails[0].data.ExpiresIn)
	}
}

func TestForgotPassword_WorksWithSignupDisabled(t *testing.T) {
	h := setup(t, true)
	registeredUser(t, h)
	// Self-hosted installs (signup off) still get self-serve reset.
	config.Cfg.SignupEnabled = false
	if rec := post(handleForgotPassword, `{"email":"new@example.com"}`); rec.Code != 202 || len(h.mails) != 1 {
		t.Fatalf("code %d mails %d", rec.Code, len(h.mails))
	}
}

func TestResetPassword_SetsPasswordInvalidatesSessionsAndIsSingleUse(t *testing.T) {
	h := setup(t, true)
	registeredUser(t, h)
	post(handleForgotPassword, `{"email":"new@example.com"}`)
	raw := tokenFromMail(t, h.mails[0])

	rec := post(handleResetPassword, `{"token":"`+raw+`","newPassword":"a brand new secret"}`)
	if rec.Code != 200 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if len(rec.Result().Cookies()) != 0 {
		t.Fatal("reset must not log the user in")
	}
	u, _ := h.store.FindUserByEmail("new@example.com")
	if bcrypt.CompareHashAndPassword([]byte(u.PasswordHash), []byte("a brand new secret")) != nil {
		t.Fatal("password not updated")
	}
	if u.TokensInvalidatedAt == nil || !u.TokensInvalidatedAt.Equal(h.now) {
		t.Fatalf("TokensInvalidatedAt %v", u.TokensInvalidatedAt)
	}

	if rec := post(handleResetPassword, `{"token":"`+raw+`","newPassword":"another good one"}`); rec.Code != 400 {
		t.Fatalf("reused token got %d", rec.Code)
	}
}

func TestResetPassword_ExpiresAfterOneHour(t *testing.T) {
	h := setup(t, true)
	registeredUser(t, h)
	post(handleForgotPassword, `{"email":"new@example.com"}`)
	raw := tokenFromMail(t, h.mails[0])

	h.now = h.now.Add(time.Hour + time.Second)
	if rec := post(handleResetPassword, `{"token":"`+raw+`","newPassword":"a brand new secret"}`); rec.Code != 400 {
		t.Fatalf("expired token got %d", rec.Code)
	}
	// ...but just inside the TTL it still works.
	h.now = h.now.Add(-2 * time.Second)
	if rec := post(handleResetPassword, `{"token":"`+raw+`","newPassword":"a brand new secret"}`); rec.Code != 200 {
		t.Fatalf("in-TTL token got %d", rec.Code)
	}
}

func TestResetPassword_WeakPasswordDoesNotBurnTheToken(t *testing.T) {
	h := setup(t, true)
	registeredUser(t, h)
	post(handleForgotPassword, `{"email":"new@example.com"}`)
	raw := tokenFromMail(t, h.mails[0])

	if rec := post(handleResetPassword, `{"token":"`+raw+`","newPassword":"short"}`); rec.Code != 400 {
		t.Fatalf("got %d", rec.Code)
	}
	if rec := post(handleResetPassword, `{"token":"`+raw+`","newPassword":"long enough now"}`); rec.Code != 200 {
		t.Fatalf("token was burned: %d", rec.Code)
	}
}

func TestResetPassword_NewerLinkSupersedesOlder(t *testing.T) {
	h := setup(t, true)
	registeredUser(t, h)
	post(handleForgotPassword, `{"email":"new@example.com"}`)
	post(handleForgotPassword, `{"email":"new@example.com"}`)
	first, second := tokenFromMail(t, h.mails[0]), tokenFromMail(t, h.mails[1])

	if rec := post(handleResetPassword, `{"token":"`+first+`","newPassword":"long enough now"}`); rec.Code != 400 {
		t.Fatalf("older link got %d", rec.Code)
	}
	if rec := post(handleResetPassword, `{"token":"`+second+`","newPassword":"long enough now"}`); rec.Code != 200 {
		t.Fatalf("newest link got %d", rec.Code)
	}
}

func TestResetToken_IsNotAVerificationToken(t *testing.T) {
	h := setup(t, true)
	registeredUser(t, h)
	post(handleForgotPassword, `{"email":"new@example.com"}`)
	raw := tokenFromMail(t, h.mails[0])
	if rec := post(handleVerifyEmail, `{"token":"`+raw+`"}`); rec.Code != 400 {
		t.Fatalf("purposes must not be interchangeable: %d", rec.Code)
	}
}

// API-GO-06: a request with a made-up, used or expired token is refused before the new
// password is hashed, so an unauthenticated caller cannot buy a bcrypt per request.
func TestResetPassword_ChecksTheTokenBeforeHashing(t *testing.T) {
	h := setup(t, true)
	registeredUser(t, h)
	post(handleForgotPassword, `{"email":"new@example.com"}`)
	raw := tokenFromMail(t, h.mails[0])

	hashes := 0
	prev := hashPassword
	hashPassword = func(p string) (string, error) { hashes++; return prev(p) }
	t.Cleanup(func() { hashPassword = prev })

	if rec := post(handleResetPassword, `{"token":"not-a-real-token","newPassword":"a brand new secret"}`); rec.Code != 400 {
		t.Fatalf("made-up token got %d", rec.Code)
	}
	h.now = h.now.Add(2 * time.Hour)
	if rec := post(handleResetPassword, `{"token":"`+raw+`","newPassword":"a brand new secret"}`); rec.Code != 400 {
		t.Fatalf("expired token got %d", rec.Code)
	}
	h.now = h.now.Add(-2 * time.Hour)
	if hashes != 0 {
		t.Fatalf("hashed %d passwords for tokens that cannot be redeemed", hashes)
	}

	if rec := post(handleResetPassword, `{"token":"`+raw+`","newPassword":"a brand new secret"}`); rec.Code != 200 {
		t.Fatalf("valid token got %d", rec.Code)
	}
	if hashes != 1 {
		t.Fatalf("valid reset hashed %d times, want 1", hashes)
	}
	if rec := post(handleResetPassword, `{"token":"`+raw+`","newPassword":"another good one"}`); rec.Code != 400 || hashes != 1 {
		t.Fatalf("used token: status %d, hashes %d", rec.Code, hashes)
	}
}
