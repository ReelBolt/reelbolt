package auth

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/models"
)

// acceptTermsCall posts to the handler as an authenticated caller.
func acceptTermsCall(t *testing.T, userID uuid.UUID, body string) *httptest.ResponseRecorder {
	t.Helper()
	req := httptest.NewRequestWithContext(context.Background(), "POST", "/api/v1/auth/accept-terms", strings.NewReader(body))
	req = withUserContext(req, userID)
	rec := httptest.NewRecorder()
	handleAcceptTerms(rec, req)
	return rec
}

// seedUser puts one user in the fake store with no legal acceptance.
func seedUser(h *harness, terms *string) *models.ApplicationUser {
	id := uuid.New()
	u := &models.ApplicationUser{ID: id, Email: "ada@example.com", DisplayName: "Ada", AuthProvider: "Google"}
	h.store.users[id] = u
	if terms != nil {
		h.store.acceptances = append(h.store.acceptances,
			models.LegalAcceptance{ID: uuid.New(), UserID: id, Document: models.LegalDocumentTerms, Version: *terms})
	}
	return u
}

const goodAcceptTerms = `{"acceptTerms":true,"termsVersion":"2026-11","privacyVersion":"2026-11"}`

func TestAcceptTerms_RecordsRowsAndReissuesSession(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)
	// F-7: the acceptance is pinned to what the deployment serves, so the
	// submitted version has to be the served one.
	serveLegalVersions(t, h.store, "2026-11", "2026-11")
	u := seedUser(h, nil)

	rec := acceptTermsCall(t, u.ID, goodAcceptTerms)
	if rec.Code != http.StatusOK {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}

	// Both documents are recorded, for this user, with the submitted version.
	got := map[string]string{}
	for _, a := range h.store.acceptances {
		if a.UserID == u.ID {
			got[a.Document] = a.Version
		}
	}
	if got[models.LegalDocumentTerms] != "2026-11" || got[models.LegalDocumentPrivacy] != "2026-11" {
		t.Fatalf("acceptances: %+v", got)
	}
	// The IP is kept for the record.
	for _, a := range h.store.acceptances {
		if a.UserID == u.ID && (a.IP == nil) {
			t.Fatal("the acceptance must record the client IP")
		}
	}

	// The session is re-issued with the version now recorded, or the proxy gate
	// would send the caller back to this page.
	var resp TokenResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &resp); err != nil {
		t.Fatal(err)
	}
	raw, _ := url.QueryUnescape(cookieMap(rec)["reelbolt_user"].Value)
	if !strings.Contains(raw, `"termsVersion":"2026-11"`) {
		t.Fatalf("user cookie %s", raw)
	}
}

func TestAcceptTerms_IsIdempotent(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)
	serveLegalVersions(t, h.store, "2026-11", "2026-11")
	u := seedUser(h, nil)

	for i := 0; i < 2; i++ {
		if rec := acceptTermsCall(t, u.ID, goodAcceptTerms); rec.Code != http.StatusOK {
			t.Fatalf("post %d: %d %s", i, rec.Code, rec.Body)
		}
	}
	n := 0
	for _, a := range h.store.acceptances {
		if a.UserID == u.ID {
			n++
		}
	}
	if n != 2 {
		t.Fatalf("re-posting the same acceptance wrote %d rows, want 2 (one per document)", n)
	}
}

func TestAcceptTerms_RecordsANewVersionOnTopOfAnOlderOne(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)
	// The user accepted 2026-10; the deployment now serves 2026-11.
	serveLegalVersions(t, h.store, "2026-11", "2026-11")
	old := "2026-10"
	u := seedUser(h, &old)

	if rec := acceptTermsCall(t, u.ID, goodAcceptTerms); rec.Code != http.StatusOK {
		t.Fatalf("got %d", rec.Code)
	}
	// The old row is kept as part of the record; the new version is an
	// additional row, and the session reports the newest one.
	terms := 0
	for _, a := range h.store.acceptances {
		if a.UserID == u.ID && a.Document == models.LegalDocumentTerms {
			terms++
		}
	}
	if terms != 2 {
		t.Fatalf("want the 2026-10 row kept plus a new one, got %d terms rows", terms)
	}
	var resp TokenResponse
	if err := json.Unmarshal(acceptTermsCall(t, u.ID, goodAcceptTerms).Body.Bytes(), &resp); err != nil {
		t.Fatal(err)
	}
	if resp.TermsVersion != "2026-11" {
		t.Fatalf("termsVersion %q", resp.TermsVersion)
	}
}

func TestAcceptTerms_Validation(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)
	u := seedUser(h, nil)

	cases := map[string]struct {
		body string
		want int
	}{
		// Refused before the deployment's own versions are read.
		"not accepted":   {`{"acceptTerms":false,"termsVersion":"2026-11","privacyVersion":"2026-11"}`, http.StatusBadRequest},
		"malformed body": {"{", http.StatusBadRequest},
		// A body that does not NAME what the deployment serves is a conflict
		// rather than a malformed request (F-7). The 33-character case is here
		// because the old rule was "non-empty and at most 32 characters"; it is
		// now just another string that is not the served version.
		"empty terms":    {`{"acceptTerms":true,"termsVersion":"","privacyVersion":"2026-11"}`, http.StatusConflict},
		"empty privacy":  {`{"acceptTerms":true,"termsVersion":"2026-11"}`, http.StatusConflict},
		"terms too long": {`{"acceptTerms":true,"termsVersion":"` + strings.Repeat("v", 33) + `","privacyVersion":"2026-11"}`, http.StatusConflict},
	}
	for name, tc := range cases {
		if rec := acceptTermsCall(t, u.ID, tc.body); rec.Code != tc.want {
			t.Errorf("%s: got %d, want %d (%s)", name, rec.Code, tc.want, rec.Body)
		}
	}
	if len(h.store.acceptances) != 0 {
		t.Fatalf("a refused request wrote %d rows", len(h.store.acceptances))
	}
}

func TestAcceptTerms_UnauthenticatedIs401(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)

	req := httptest.NewRequestWithContext(context.Background(), "POST", "/api/v1/auth/accept-terms", strings.NewReader(goodAcceptTerms))
	rec := httptest.NewRecorder()
	handleAcceptTerms(rec, req)

	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("got %d", rec.Code)
	}
	if len(h.store.acceptances) != 0 {
		t.Fatal("an unauthenticated request must write nothing")
	}
}

// acceptancesOf returns the user's recorded acceptances of one document.
func acceptancesOf(h *harness, userID uuid.UUID, document string) []models.LegalAcceptance {
	var out []models.LegalAcceptance
	for _, a := range h.store.acceptances {
		if a.UserID == userID && a.Document == document {
			out = append(out, a)
		}
	}
	return out
}

// S-23: the privacy-only Continue is an acknowledgement of the new Privacy Policy. It records the
// Privacy row only — never a Terms acceptance — and re-issues the session like a full acceptance.
func TestAcceptTerms_PrivacyAcknowledgement_RecordsOnlyPrivacy(t *testing.T) {
	h := oauthSetup(t)
	useFakeLegalVersions(t, h.store)
	serveLegalVersions(t, h.store, "2026-11", "2026-12")
	terms := "2026-11"
	u := seedUser(h, &terms)

	rec := acceptTermsCall(t, u.ID, `{"privacyAcknowledgement":true,"privacyVersion":"2026-12"}`)
	if rec.Code != http.StatusOK {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if got := acceptancesOf(h, u.ID, models.LegalDocumentTerms); len(got) != 1 || got[0].Version != "2026-11" {
		t.Fatalf("an acknowledgement must not record a Terms acceptance: %+v", got)
	}
	if got := acceptancesOf(h, u.ID, models.LegalDocumentPrivacy); len(got) != 1 || got[0].Version != "2026-12" || got[0].IP == nil {
		t.Fatalf("privacy acknowledgement: %+v", got)
	}
	raw, _ := url.QueryUnescape(cookieMap(rec)["reelbolt_user"].Value)
	if !strings.Contains(raw, `"privacyVersion":"2026-12"`) || !strings.Contains(raw, `"termsVersion":"2026-11"`) {
		t.Fatalf("user cookie %s", raw)
	}

	// Even a body that also says acceptTerms:true records no Terms row through the flag.
	serveLegalVersions(t, h.store, "2026-11", "2027-01")
	rec = acceptTermsCall(t, u.ID, `{"privacyAcknowledgement":true,"acceptTerms":true,"termsVersion":"2026-11","privacyVersion":"2027-01"}`)
	if rec.Code != http.StatusOK {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if got := acceptancesOf(h, u.ID, models.LegalDocumentTerms); len(got) != 1 {
		t.Fatalf("terms rows: %+v", got)
	}
}

// S-23: the acknowledgement path cannot stand in for accepting the Terms. Unless the user's latest
// accepted Terms already IS the served one, it is refused with the same 409 body as a version mismatch,
// and nothing is written.
func TestAcceptTerms_PrivacyAcknowledgement_RefusedWhenTermsAreNotCurrent(t *testing.T) {
	h := oauthSetup(t)
	useFakeLegalVersions(t, h.store)
	serveLegalVersions(t, h.store, "2026-12", "2026-12")

	stale := "2026-11"
	for name, terms := range map[string]*string{"stale terms": &stale, "no terms": nil} {
		u := seedUser(h, terms)
		rec := acceptTermsCall(t, u.ID, `{"privacyAcknowledgement":true,"privacyVersion":"2026-12"}`)
		if rec.Code != http.StatusConflict {
			t.Fatalf("%s: got %d: %s", name, rec.Code, rec.Body)
		}
		var body map[string]string
		if err := json.Unmarshal(rec.Body.Bytes(), &body); err != nil || body["currentTermsVersion"] != "2026-12" || body["currentPrivacyVersion"] != "2026-12" {
			t.Fatalf("%s: body %s", name, rec.Body)
		}
		if got := acceptancesOf(h, u.ID, models.LegalDocumentPrivacy); len(got) != 0 {
			t.Fatalf("%s: a refused acknowledgement wrote %+v", name, got)
		}
		if got := acceptancesOf(h, u.ID, models.LegalDocumentTerms); terms != nil && (len(got) != 1 || got[0].Version != stale) {
			t.Fatalf("%s: terms rows %+v", name, got)
		}
	}

	// A privacy version the deployment does not serve is the usual mismatch.
	current := "2026-12"
	u := seedUser(h, &current)
	if rec := acceptTermsCall(t, u.ID, `{"privacyAcknowledgement":true,"privacyVersion":"2026-11"}`); rec.Code != http.StatusConflict {
		t.Fatalf("stale privacy version: got %d", rec.Code)
	}
	if got := acceptancesOf(h, u.ID, models.LegalDocumentPrivacy); len(got) != 0 {
		t.Fatalf("a refused acknowledgement wrote %+v", got)
	}
}
