package auth

import (
	"encoding/json"
	"net/http"
	"net/url"
	"strings"
	"testing"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

// F-7. Before this, signup and POST /api/v1/auth/accept-terms took the accepted
// version straight from the request body: any non-empty string of at most 32
// characters was recorded in legal_acceptances, so a caller could record a
// version that was never served — or a future one — and web/proxy.ts then
// compared that same self-asserted value against a build-time constant. Every
// test below fails against that behaviour: the "never served" cases are exactly
// the requests the old rule accepted, and they are now refused with nothing
// written.

// acceptTermsBody builds an /accept-terms body naming a version pair.
func acceptTermsBody(terms, privacy string) string {
	return `{"acceptTerms":true,"termsVersion":"` + terms + `","privacyVersion":"` + privacy + `"}`
}

// signupBodyFor builds a signup body naming a version pair.
func signupBodyFor(email, terms, privacy string) string {
	return `{"email":"` + email + `","password":"correct horse","displayName":"Ada",` +
		`"acceptTerms":true,"termsVersion":"` + terms + `","privacyVersion":"` + privacy + `"}`
}

// recordedVersions is document -> version over the rows written for one user.
func recordedVersions(h *harness, userID uuid.UUID) map[string]string {
	got := map[string]string{}
	for _, a := range h.store.acceptances {
		if a.UserID == userID {
			got[a.Document] = a.Version
		}
	}
	return got
}

// ---- POST /api/v1/auth/accept-terms ----

func TestAcceptTerms_RefusesAVersionTheDeploymentIsNotServing(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)
	serveLegalVersions(t, h.store, "2026-11", "2026-11")
	u := seedUser(h, nil)

	// The page was rendered before the operator published 2026-11.
	rec := acceptTermsCall(t, u.ID, acceptTermsBody("2026-10", "2026-10"))
	if rec.Code != http.StatusConflict {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if len(h.store.acceptances) != 0 {
		t.Fatalf("a refused acceptance wrote %d rows", len(h.store.acceptances))
	}

	// The refusal names what IS served, so the page can re-render against it
	// rather than making the visitor guess.
	var body map[string]string
	if err := json.Unmarshal(rec.Body.Bytes(), &body); err != nil {
		t.Fatal(err)
	}
	if body["currentTermsVersion"] != "2026-11" || body["currentPrivacyVersion"] != "2026-11" {
		t.Fatalf("refusal body: %v", body)
	}

	// And no session was re-issued: a refused request must not leave the caller
	// holding a cookie that claims the stale version was accepted.
	for _, c := range rec.Result().Cookies() {
		if c.Name == "reelbolt_user" || c.Name == "reelbolt_token" {
			t.Fatalf("a refused acceptance re-issued the session: %s", c.Name)
		}
	}
}

func TestAcceptTerms_RefusesAVersionThatWasNeverServed(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)
	serveLegalVersions(t, h.store, "2026-10", "2026-10")
	u := seedUser(h, nil)

	// Well-formed under the old rule — non-empty and 14 characters — and the
	// old rule wrote it into legal_acceptances as proof of consent.
	rec := acceptTermsCall(t, u.ID, acceptTermsBody("2099-12-future", "2099-12-future"))
	if rec.Code != http.StatusConflict {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if len(h.store.acceptances) != 0 {
		t.Fatalf("a version that was never served was recorded: %+v", h.store.acceptances)
	}
}

func TestAcceptTerms_RecordsTheVersionTheDeploymentServes(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)
	serveLegalVersions(t, h.store, "2026-11", "2026-11")
	u := seedUser(h, nil)

	// The submission NAMES the served version (padded, so the comparison trims);
	// the ROW carries the deployment's own string, not the caller's bytes.
	rec := acceptTermsCall(t, u.ID, acceptTermsBody("  2026-11  ", "  2026-11  "))
	if rec.Code != http.StatusOK {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	got := recordedVersions(h, u.ID)
	if got[models.LegalDocumentTerms] != "2026-11" || got[models.LegalDocumentPrivacy] != "2026-11" {
		t.Fatalf("recorded acceptances: %+v", got)
	}
	if len(h.store.acceptances) != 2 {
		t.Fatalf("want one row per document, got %+v", h.store.acceptances)
	}

	raw, _ := url.QueryUnescape(cookieMap(rec)["reelbolt_user"].Value)
	if !strings.Contains(raw, `"termsVersion":"2026-11"`) {
		t.Fatalf("user cookie %s", raw)
	}
}

func TestAcceptTerms_WritesNothingWhenTheDeploymentCannotStateItsVersions(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)
	breakLegalVersions(t, h.store, services.ErrLegalDocumentsUnavailable)
	u := seedUser(h, nil)

	rec := acceptTermsCall(t, u.ID, goodAcceptTerms)
	if rec.Code != http.StatusServiceUnavailable {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if len(h.store.acceptances) != 0 {
		t.Fatalf("an unverifiable acceptance was recorded: %+v", h.store.acceptances)
	}
}

// ---- POST /api/v1/auth/signup ----

func TestSignup_RefusesAVersionTheDeploymentIsNotServing(t *testing.T) {
	h := setup(t, true)
	serveLegalVersions(t, h.store, "2026-11", "2026-11")

	rec := post(handleSignup, signupBodyFor("new@example.com", "2026-10", "2026-10"))
	if rec.Code != http.StatusConflict {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if len(h.store.users) != 0 {
		t.Fatalf("a refused signup created %d accounts", len(h.store.users))
	}
	if len(h.store.acceptances) != 0 || len(h.mails) != 0 {
		t.Fatalf("a refused signup had side effects: %+v %+v", h.store.acceptances, h.mails)
	}
}

func TestSignup_RefusesAVersionThatWasNeverServed(t *testing.T) {
	h := setup(t, true)
	serveLegalVersions(t, h.store, "2026-10", "2026-10")

	// 32 characters and non-empty: the whole of the old rule's validation, and
	// what a caller could use to make legal_acceptances say anything at all.
	rec := post(handleSignup, signupBodyFor("new@example.com", "zxcvbnmasdfghjklqwertyuiop123456", "2026-10"))
	if rec.Code != http.StatusConflict {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if len(h.store.users) != 0 || len(h.store.acceptances) != 0 {
		t.Fatalf("nothing may be written: %d users, %d acceptances", len(h.store.users), len(h.store.acceptances))
	}
}

func TestSignup_RecordsTheVersionTheDeploymentServes(t *testing.T) {
	h := setup(t, true)
	serveLegalVersions(t, h.store, "2026-11", "2026-11")

	rec := post(handleSignup, signupBodyFor("New@Example.com", " 2026-11 ", "2026-11"))
	if rec.Code != http.StatusAccepted {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if len(h.store.users) != 1 {
		t.Fatalf("want one account, got %d", len(h.store.users))
	}
	if len(h.store.acceptances) != 2 {
		t.Fatalf("want two acceptance rows, got %+v", h.store.acceptances)
	}
	for _, a := range h.store.acceptances {
		if a.Version != "2026-11" {
			t.Fatalf("recorded %q, want the deployment's own version", a.Version)
		}
	}
}

// ---- POST /api/v1/auth/signup with a team invite (the invite-signup path) ----

func TestInviteSignup_RefusesAVersionTheDeploymentIsNotServing(t *testing.T) {
	e := newInviteEnv(t)
	// The deployment published 2026-11 after the form was rendered; the invite
	// body still names 2026-10.
	serveLegalVersions(t, e.store, "2026-11", "2026-11")

	rec := e.do("POST", "/api/v1/auth/signup", inviteSignupBody("new@example.com", e.token), "")
	if rec.Code != http.StatusConflict {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	// Only the owner seeded by the fixture exists, and nothing was accepted.
	if len(e.store.users) != 1 {
		t.Fatalf("invite signup created an account: %d users", len(e.store.users))
	}
	if len(e.store.acceptances) != 0 {
		t.Fatalf("invite signup recorded %+v", e.store.acceptances)
	}
	if e.inv.AcceptedAt != nil {
		t.Fatal("the invitation was consumed by a refused signup")
	}
}
