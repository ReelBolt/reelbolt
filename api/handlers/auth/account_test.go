package auth

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
	"golang.org/x/crypto/bcrypt"
)

// fakeDeletionStore records what the handler asked of the account-deletion store.
type fakeDeletionStore struct {
	soleOwnerTeam string
	soleOwnerErr  error

	requested    []time.Time
	requestErr   error
	clearCalls   int
	clearPending bool
}

func (f *fakeDeletionStore) SoleOwnerTeamWithMembers(context.Context, uuid.UUID) (string, error) {
	return f.soleOwnerTeam, f.soleOwnerErr
}

func (f *fakeDeletionStore) RequestDeletion(_ context.Context, _ uuid.UUID, now time.Time) error {
	if f.requestErr != nil {
		return f.requestErr
	}
	f.requested = append(f.requested, now)
	return nil
}

func (f *fakeDeletionStore) ClearDeletionRequest(context.Context, uuid.UUID) (bool, error) {
	f.clearCalls++
	return f.clearPending, nil
}

func (f *fakeDeletionStore) UserExists(string) (bool, error) { return true, nil }

// deletionHarness installs the deletion fakes and returns them with the signed-in user.
func deletionHarness(t *testing.T) (*fakeDeletionStore, *models.ApplicationUser, *[]sentMail, *[]uuid.UUID) {
	t.Helper()
	oldCfg, oldDeletion := config.Cfg, deletion
	config.Cfg.PublicBaseURL = "https://reelbolt.test"
	config.Cfg.LinkBaseURL = "https://reelbolt.test"
	config.Cfg.CookieSecure = true
	t.Cleanup(func() { config.Cfg, deletion = oldCfg, oldDeletion })

	hash, err := bcrypt.GenerateFromPassword([]byte("correct horse battery"), bcrypt.DefaultCost)
	if err != nil {
		t.Fatal(err)
	}
	user := &models.ApplicationUser{ID: uuid.New(), Email: "ada@example.com", DisplayName: "Ada", PasswordHash: string(hash)}

	store := &fakeDeletionStore{}
	mails := &[]sentMail{}
	invalidated := &[]uuid.UUID{}
	now := time.Date(2026, 10, 7, 12, 0, 0, 0, time.UTC)

	deletion = deletionDeps{
		store:   store,
		getUser: func(uuid.UUID) (*models.ApplicationUser, error) { return user, nil },
		invalidateSession: func(id uuid.UUID) {
			*invalidated = append(*invalidated, id)
		},
		sendMail: func(to string, tmpl services.TemplateName, data services.MailData) error {
			*mails = append(*mails, sentMail{to, tmpl, data})
			return nil
		},
		runAsync: func(f func()) { f() },
		now:      func() time.Time { return now },
		// The provider confirmation an account with no password is offered
		// (C11 F-8). Tests that exercise that branch replace it (or assert it was
		// never reached).
		providerConfirmation: func(http.ResponseWriter, *models.ApplicationUser) (providerConfirmationStart, error) {
			return providerConfirmationStart{Provider: "google", URL: "https://accounts.google.com/o/oauth2/v2/auth?state=s"}, nil
		},
	}
	return store, user, mails, invalidated
}

func deleteAccount(t *testing.T, user *models.ApplicationUser, body string) *httptest.ResponseRecorder {
	t.Helper()
	req := httptest.NewRequestWithContext(context.Background(), "POST", "/api/v1/auth/account/delete", strings.NewReader(body))
	req = req.WithContext(context.WithValue(req.Context(), middleware.UserContextKey,
		middleware.UserContext{UserID: user.ID.String(), Email: user.Email}))
	rec := httptest.NewRecorder()
	handleAccountDelete(rec, req)
	return rec
}

func TestAccountDelete_SchedulesDeletionKillsSessionsAndMails(t *testing.T) {
	store, user, mails, invalidated := deletionHarness(t)

	rec := deleteAccount(t, user, `{"password":"correct horse battery"}`)
	if rec.Code != 202 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
	}

	if len(store.requested) != 1 {
		t.Fatalf("RequestDeletion called %d times", len(store.requested))
	}
	if len(*invalidated) != 1 || (*invalidated)[0] != user.ID {
		t.Fatalf("session not invalidated: %v", *invalidated)
	}

	// Both cookies are dropped: the caller must not be left holding a token the server now refuses.
	dropped := map[string]bool{}
	for _, c := range rec.Result().Cookies() {
		if c.MaxAge < 0 {
			dropped[c.Name] = true
		}
	}
	if !dropped["reelbolt_token"] || !dropped["reelbolt_user"] {
		t.Fatalf("cookies not cleared: %v", dropped)
	}

	if len(*mails) != 1 || (*mails)[0].tmpl != services.TemplateDeletionSchedule || (*mails)[0].to != user.Email {
		t.Fatalf("mails: %+v", *mails)
	}
	mail := (*mails)[0]
	if mail.data.Link != "https://reelbolt.test/app/login" {
		t.Fatalf("link %q", mail.data.Link)
	}
	if mail.data.DeletionDate != "6 November 2026" {
		t.Fatalf("deletion date %q", mail.data.DeletionDate)
	}

	var body map[string]any
	_ = json.Unmarshal(rec.Body.Bytes(), &body)
	if body["status"] != "deletion_scheduled" || body["graceDays"] != float64(AccountDeletionGraceDays) {
		t.Fatalf("body %v", body)
	}
}

func TestAccountDelete_RequiresThePassword(t *testing.T) {
	store, user, mails, invalidated := deletionHarness(t)

	for _, body := range []string{`{"password":"wrong password here"}`, `{}`} {
		rec := deleteAccount(t, user, body)
		if rec.Code != 401 && rec.Code != 400 {
			t.Fatalf("got %d for %s", rec.Code, body)
		}
	}

	if len(store.requested) != 0 {
		t.Fatal("a wrong password scheduled a deletion")
	}
	if len(*invalidated) != 0 || len(*mails) != 0 {
		t.Fatal("a wrong password killed the session or sent mail")
	}
}

func TestAccountDelete_RefusesASoleOwnerTeamWithOtherMembers(t *testing.T) {
	store, user, mails, invalidated := deletionHarness(t)
	store.soleOwnerTeam = "Acme Team"

	rec := deleteAccount(t, user, `{"password":"correct horse battery"}`)
	if rec.Code != 409 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
	}

	var body map[string]string
	_ = json.Unmarshal(rec.Body.Bytes(), &body)
	if body["error"] != "sole_owner" || body["organization"] != "Acme Team" {
		t.Fatalf("body %v", body)
	}

	if len(store.requested) != 0 {
		t.Fatal("the blocked request still scheduled a deletion")
	}
	if len(*invalidated) != 0 || len(*mails) != 0 {
		t.Fatal("the blocked request killed the session or sent mail")
	}
}

func TestAccountDelete_RequiresASession(t *testing.T) {
	deletionHarness(t)
	req := httptest.NewRequestWithContext(context.Background(), "POST", "/api/v1/auth/account/delete", strings.NewReader(`{"password":"x"}`))
	rec := httptest.NewRecorder()
	handleAccountDelete(rec, req)
	if rec.Code != 401 {
		t.Fatalf("got %d", rec.Code)
	}
}

func TestAccountDelete_StoreFailureDoesNotClaimSuccess(t *testing.T) {
	store, user, mails, invalidated := deletionHarness(t)
	store.requestErr = errors.New("boom")

	if rec := deleteAccount(t, user, `{"password":"correct horse battery"}`); rec.Code != 500 {
		t.Fatalf("got %d", rec.Code)
	}
	if len(*invalidated) != 0 || len(*mails) != 0 {
		t.Fatal("a failed request killed the session or sent mail")
	}
}

// C11 F-8. An account with no password gets a real confirmation path instead of a
// flat 401: the endpoint refuses, schedules nothing, and hands back the provider
// round trip that proves the caller controls the identity the account was built on.
func TestAccountDelete_ProviderAccountIsOfferedTheConfirmationPath(t *testing.T) {
	store, user, mails, invalidated := deletionHarness(t)
	user.AuthProvider = "Google"
	started := 0
	deletion.providerConfirmation = func(_ http.ResponseWriter, got *models.ApplicationUser) (providerConfirmationStart, error) {
		started++
		if got.ID != user.ID {
			t.Errorf("confirmation started for %v, want %v", got.ID, user.ID)
		}
		return providerConfirmationStart{Provider: "google", URL: "https://accounts.google.com/o/oauth2/v2/auth?state=s&code_challenge=x"}, nil
	}

	rec := deleteAccount(t, user, `{}`)
	if rec.Code != http.StatusConflict {
		t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
	}
	var body DeletionConfirmationRequired
	if err := json.Unmarshal(rec.Body.Bytes(), &body); err != nil {
		t.Fatal(err)
	}
	if body.Error != "provider_confirmation_required" || body.Provider != "google" {
		t.Fatalf("body %+v", body)
	}
	if body.ConfirmURL != "https://accounts.google.com/o/oauth2/v2/auth?state=s&code_challenge=x" {
		t.Fatalf("confirmUrl %q", body.ConfirmURL)
	}
	if body.Message == "" {
		t.Fatal("the refusal must say what happened and what to do")
	}
	if started != 1 {
		t.Fatalf("confirmation started %d times", started)
	}
	// Nothing irreversible may have happened: no request, no mail, no session killed.
	if len(store.requested) != 0 {
		t.Fatal("the confirmation request scheduled a deletion")
	}
	if len(*mails) != 0 || len(*invalidated) != 0 {
		t.Fatal("the confirmation request mailed or killed the session")
	}
}

// The alternative path must not weaken the password one: an account that HAS a
// password is refused a wrong one exactly as before, and is never offered the
// provider round trip as a way around it.
func TestAccountDelete_AProviderAccountStillChecksAPasswordThatIsGiven(t *testing.T) {
	store, user, mails, invalidated := deletionHarness(t)
	user.AuthProvider = "Google"
	deletion.providerConfirmation = func(http.ResponseWriter, *models.ApplicationUser) (providerConfirmationStart, error) {
		t.Error("the provider round trip was offered despite a password being given")
		return providerConfirmationStart{}, nil
	}

	rec := deleteAccount(t, user, `{"password":"not the password"}`)
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
	}
	if len(store.requested) != 0 || len(*mails) != 0 || len(*invalidated) != 0 {
		t.Fatal("a wrong password scheduled something")
	}

	// And the password still schedules the deletion.
	rec = deleteAccount(t, user, `{"password":"correct horse battery"}`)
	if rec.Code != http.StatusAccepted {
		t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
	}
	if len(store.requested) != 1 {
		t.Fatal("the password path no longer schedules the deletion")
	}
}

// An account whose provider is not configured here (Google switched off, say) has
// no dead end either: it is told how to set a password and confirm with it.
func TestAccountDelete_SaysHowToConfirmWhenTheProviderIsNotConfigured(t *testing.T) {
	store, user, mails, invalidated := deletionHarness(t)
	user.AuthProvider = "Google"
	deletion.providerConfirmation = func(http.ResponseWriter, *models.ApplicationUser) (providerConfirmationStart, error) {
		return providerConfirmationStart{}, nil
	}

	rec := deleteAccount(t, user, `{}`)
	if rec.Code != http.StatusConflict {
		t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
	}
	var body DeletionConfirmationRequired
	if err := json.Unmarshal(rec.Body.Bytes(), &body); err != nil {
		t.Fatal(err)
	}
	if body.Error != "confirmation_unavailable" || body.ConfirmURL != "" {
		t.Fatalf("body %+v", body)
	}
	if !strings.Contains(body.Message, "forgot password") || !strings.Contains(body.Message, user.Email) {
		t.Fatalf("the message must say what does work: %q", body.Message)
	}
	if len(store.requested) != 0 || len(*mails) != 0 || len(*invalidated) != 0 {
		t.Fatal("an unavailable confirmation scheduled something")
	}
}

// The sole-owner refusal is decided BEFORE the confirmation is handed out: a round
// trip that could only end in a refusal must never be started.
func TestAccountDelete_RefusesTheSoleOwnerBeforeOfferingAConfirmation(t *testing.T) {
	store, user, mails, invalidated := deletionHarness(t)
	user.AuthProvider = "Google"
	store.soleOwnerTeam = "Acme Team"
	deletion.providerConfirmation = func(http.ResponseWriter, *models.ApplicationUser) (providerConfirmationStart, error) {
		t.Error("a confirmation was offered for an account that may not be deleted yet")
		return providerConfirmationStart{}, nil
	}

	rec := deleteAccount(t, user, `{}`)
	if rec.Code != http.StatusConflict {
		t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
	}
	var body map[string]string
	_ = json.Unmarshal(rec.Body.Bytes(), &body)
	if body["error"] != "sole_owner" || body["organization"] != "Acme Team" {
		t.Fatalf("body %v", body)
	}
	if len(store.requested) != 0 || len(*mails) != 0 || len(*invalidated) != 0 {
		t.Fatal("a blocked request scheduled something")
	}
}

// A password account is untouched by all of this: no password in the body is the
// same 400 it always was, and the provider round trip is never mentioned.
func TestAccountDelete_PasswordAccountStillNeedsItsPassword(t *testing.T) {
	store, user, mails, invalidated := deletionHarness(t)
	deletion.providerConfirmation = func(http.ResponseWriter, *models.ApplicationUser) (providerConfirmationStart, error) {
		t.Error("a password account was offered the provider round trip")
		return providerConfirmationStart{}, nil
	}

	for _, body := range []string{`{}`, `{"password":""}`} {
		rec := deleteAccount(t, user, body)
		if rec.Code != http.StatusBadRequest {
			t.Fatalf("%s: got %d: %s", body, rec.Code, rec.Body.String())
		}
	}
	if len(store.requested) != 0 || len(*mails) != 0 || len(*invalidated) != 0 {
		t.Fatal("a body without a password scheduled something")
	}
}

func TestCancelPendingDeletion_ClearsTheFlagOnLogin(t *testing.T) {
	store, user, _, _ := deletionHarness(t)
	requested := time.Date(2026, 10, 1, 0, 0, 0, 0, time.UTC)
	user.DeletionRequestedAt = &requested
	store.clearPending = true

	req := httptest.NewRequestWithContext(context.Background(), "POST", "/api/v1/auth/token", nil)
	if !cancelPendingDeletion(req, user) {
		t.Fatal("a pending deletion was not reported as cancelled")
	}
	if user.DeletionRequestedAt != nil {
		t.Fatal("the in-memory user still carries the deletion flag")
	}
	if store.clearCalls != 1 {
		t.Fatalf("ClearDeletionRequest calls: %d", store.clearCalls)
	}

	// A user with no pending request is not touched at all.
	clean := &models.ApplicationUser{ID: uuid.New()}
	if cancelPendingDeletion(req, clean) || store.clearCalls != 1 {
		t.Fatal("a login without a pending deletion called the store")
	}
}

func TestDeletionDate_AddsTheGracePeriodInUTC(t *testing.T) {
	now := time.Date(2026, 1, 31, 23, 30, 0, 0, time.UTC)
	got := services.DeletionDate(now, AccountDeletionGraceDays)
	want := time.Date(2026, 3, 2, 23, 30, 0, 0, time.UTC)
	if !got.Equal(want) {
		t.Fatalf("got %s want %s", got, want)
	}
	if got.Location() != time.UTC {
		t.Fatalf("not UTC: %s", got.Location())
	}
}
