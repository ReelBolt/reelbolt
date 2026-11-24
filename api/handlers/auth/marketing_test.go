package auth

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

// useFakeLegalVersions points both session lookups at the in-memory store's
// newest ACCEPTED rows, like services.LatestTermsVersion/LatestPrivacyVersion.
func useFakeLegalVersions(t *testing.T, store *fakeStore) {
	t.Helper()
	old := sess
	latest := func(doc string) func(uuid.UUID) (string, error) {
		return func(id uuid.UUID) (string, error) {
			v, err := store.LatestAcceptances(id)
			if err != nil {
				return "", err
			}
			return v[doc], nil
		}
	}
	sess = sessionDeps{termsVersion: latest(models.LegalDocumentTerms), privacyVersion: latest(models.LegalDocumentPrivacy)}
	t.Cleanup(func() { sess = old })
}

func TestIssueSession_IncludesPrivacyVersion(t *testing.T) {
	h := setup(t, true)
	useFakeLegalVersions(t, h.store)
	// The deployment now serves a newer Privacy than the user accepted: the
	// session must carry what the user ACCEPTED, so the web gate can tell.
	serveLegalVersions(t, h.store, "2026-10", "2026-10-26")
	user := &models.ApplicationUser{ID: uuid.New(), Email: "ada@example.com"}
	h.store.acceptances = append(h.store.acceptances,
		models.LegalAcceptance{ID: uuid.New(), UserID: user.ID, Document: models.LegalDocumentTerms, Version: "2026-10"},
		models.LegalAcceptance{ID: uuid.New(), UserID: user.ID, Document: models.LegalDocumentPrivacy, Version: "2026-10"},
	)
	m := &services.ActiveMembership{OrgID: uuid.New(), OrgName: "Personal", OrgKind: models.OrgKindPersonal, Role: models.OrgRoleOwner}

	rec := httptest.NewRecorder()
	resp, err := issueSession(rec, user, m)
	if err != nil {
		t.Fatal(err)
	}
	if resp.PrivacyVersion != "2026-10" || resp.TermsVersion != "2026-10" {
		t.Fatalf("TokenResponse versions: terms %q privacy %q", resp.TermsVersion, resp.PrivacyVersion)
	}
	body, _ := json.Marshal(resp)
	if !strings.Contains(string(body), `"privacyVersion":"2026-10"`) {
		t.Fatalf("TokenResponse JSON: %s", body)
	}
	claims, err := services.ValidateToken(resp.AccessToken)
	if err != nil || claims.PrivacyVersion != "2026-10" {
		t.Fatalf("claim: %+v err %v", claims, err)
	}
	var cookie string
	for _, c := range rec.Result().Cookies() {
		if c.Name == "reelbolt_user" {
			cookie, _ = url.QueryUnescape(c.Value)
		}
	}
	var ui map[string]any
	if err := json.Unmarshal([]byte(cookie), &ui); err != nil || ui["privacyVersion"] != "2026-10" {
		t.Fatalf("cookie %s (%v)", cookie, err)
	}

	// A user with no Privacy acceptance: the field is absent everywhere, never "".
	other := &models.ApplicationUser{ID: uuid.New(), Email: "grace@example.com"}
	rec = httptest.NewRecorder()
	resp, _ = issueSession(rec, other, m)
	body, _ = json.Marshal(resp)
	if strings.Contains(string(body), "privacyVersion") {
		t.Fatalf("absent privacy version must be omitted: %s", body)
	}
	for _, c := range rec.Result().Cookies() {
		if v, _ := url.QueryUnescape(c.Value); c.Name == "reelbolt_user" && strings.Contains(v, "privacyVersion") {
			t.Fatalf("cookie must omit privacyVersion: %s", v)
		}
	}
}

func withMarketing(extra string) string {
	return strings.TrimSuffix(goodSignup, "}") + extra + "}"
}

func TestSignup_MarketingOptIn_WritesConsentAndEventInTx(t *testing.T) {
	h := setup(t, true)
	req := httptest.NewRequestWithContext(t.Context(), "POST", "/x", strings.NewReader(withMarketing(`,"marketingOptIn":true,"marketingWordingVersion":"`+services.MarketingWordingVersion+`"`)))
	req.Header.Set("X-Real-IP", "203.0.113.9")
	req.Header.Set("User-Agent", "TestAgent/1.0")
	rec := httptest.NewRecorder()
	handleSignup(rec, req)
	if rec.Code != 202 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	u, _ := h.store.FindUserByEmail("new@example.com")
	// One RegisterUser call carried the opt-in: the real store writes the
	// consent and its event in that call's transaction.
	if len(h.store.registerOptIns) != 1 || h.store.registerOptIns[0] == nil {
		t.Fatalf("RegisterUser must carry the opt-in: %+v", h.store.registerOptIns)
	}
	in := h.store.registerOptIns[0]
	if in.WordingVersion != services.MarketingWordingVersion || in.IP == nil || *in.IP != "203.0.113.9" || in.UserAgent != "TestAgent/1.0" || !in.At.Equal(h.now) {
		t.Fatalf("opt-in: %+v", in)
	}
	c := h.store.consents["new@example.com"]
	if c == nil || c.UserID == nil || *c.UserID != u.ID || c.Status != models.MarketingConsentStatusOptedIn ||
		c.Source != models.MarketingConsentSourceSignup || c.ConfirmedAt != nil {
		t.Fatalf("consent: %+v", c)
	}
	if len(h.store.consentEvents) != 1 || h.store.consentEvents[0].Action != models.MarketingConsentActionGranted {
		t.Fatalf("events: %+v", h.store.consentEvents)
	}
}

func TestSignup_NoOptIn_WritesNoConsent(t *testing.T) {
	h := setup(t, true)
	// An unticked box may still send the version the form displayed.
	if rec := post(handleSignup, withMarketing(`,"marketingOptIn":false,"marketingWordingVersion":"anything"`)); rec.Code != 202 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if len(h.store.registerOptIns) != 1 || h.store.registerOptIns[0] != nil {
		t.Fatalf("no opt-in expected: %+v", h.store.registerOptIns)
	}
	if len(h.store.consents) != 0 || len(h.store.consentEvents) != 0 {
		t.Fatalf("no consent expected: %+v %+v", h.store.consents, h.store.consentEvents)
	}
}

func TestSignup_StaleMarketingWording_400(t *testing.T) {
	for name, extra := range map[string]string{
		"stale":   `,"marketingOptIn":true,"marketingWordingVersion":"2025-01-marketing-v0"`,
		"missing": `,"marketingOptIn":true`,
	} {
		t.Run(name, func(t *testing.T) {
			h := setup(t, true)
			rec := post(handleSignup, withMarketing(extra))
			if rec.Code != http.StatusBadRequest {
				t.Fatalf("got %d: %s", rec.Code, rec.Body)
			}
			var body map[string]string
			_ = json.Unmarshal(rec.Body.Bytes(), &body)
			if body["error"] != "invalid_marketing_wording" {
				t.Fatalf("body: %s", rec.Body)
			}
			if len(h.store.users) != 0 || len(h.store.consents) != 0 {
				t.Fatal("a refused signup writes nothing")
			}
		})
	}
}

func TestVerifyEmail_ConfirmsPendingOptIn(t *testing.T) {
	h := setup(t, true)
	post(handleSignup, withMarketing(`,"marketingOptIn":true,"marketingWordingVersion":"`+services.MarketingWordingVersion+`"`))
	if c := h.store.consents["new@example.com"]; c == nil || c.ConfirmedAt != nil {
		t.Fatalf("before verification the opt-in is unconfirmed: %+v", c)
	}
	h.now = h.now.Add(time.Hour)
	if rec := post(handleVerifyEmail, `{"token":"`+tokenFromMail(t, h.mails[0])+`"}`); rec.Code != 200 {
		t.Fatalf("verify got %d: %s", rec.Code, rec.Body)
	}
	c := h.store.consents["new@example.com"]
	if c.ConfirmedAt == nil || !c.ConfirmedAt.Equal(h.now) {
		t.Fatalf("confirmed_at: %+v", c.ConfirmedAt)
	}
	if n := len(h.store.consentEvents); n != 2 || h.store.consentEvents[1].Action != models.MarketingConsentActionConfirmed {
		t.Fatalf("events: %+v", h.store.consentEvents)
	}
}

func TestInviteSignup_MarketingOptIn_IsConfirmedAtOnce(t *testing.T) {
	e := newInviteEnv(t)
	body := strings.TrimSuffix(inviteSignupBody("new@example.com", e.token), "}") +
		`,"marketingOptIn":true,"marketingWordingVersion":"` + services.MarketingWordingVersion + `"}`
	if rec := e.do("POST", "/api/v1/auth/signup", body, ""); rec.Code != 200 {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	c := e.store.consents["new@example.com"]
	if c == nil || c.ConfirmedAt == nil || c.Status != models.MarketingConsentStatusOptedIn {
		t.Fatalf("an invited signup's address is proven, so the opt-in is confirmed: %+v", c)
	}
}

// S-32: the verification mail of a signup that opted in says verifying also confirms the subscription.
func TestSignup_MarketingOptIn_VerifyMailMentionsTheSubscription(t *testing.T) {
	h := setup(t, true)
	if rec := post(handleSignup, withMarketing(`,"marketingOptIn":true,"marketingWordingVersion":"`+services.MarketingWordingVersion+`"`)); rec.Code != 202 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if len(h.mails) != 1 || h.mails[0].tmpl != services.TemplateVerifyEmail || !h.mails[0].data.MarketingOptIn {
		t.Fatalf("mails: %+v", h.mails)
	}

	h = setup(t, true)
	if rec := post(handleSignup, goodSignup); rec.Code != 202 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if len(h.mails) != 1 || h.mails[0].data.MarketingOptIn {
		t.Fatalf("a signup without an opt-in: %+v", h.mails)
	}
}

// S-32: a resent verify mail carries the marketing notice while the signup's opt-in is pending, since
// verifying through it confirms the subscription too; without an opt-in it does not.
func TestResendVerification_CarriesTheMarketingNoticeWhileAnOptInIsPending(t *testing.T) {
	h := setup(t, true)
	post(handleSignup, withMarketing(`,"marketingOptIn":true,"marketingWordingVersion":"`+services.MarketingWordingVersion+`"`))
	h.mails = nil
	if rec := post(handleResendVerification, `{"email":"new@example.com"}`); rec.Code != 202 {
		t.Fatalf("got %d", rec.Code)
	}
	if len(h.mails) != 1 || h.mails[0].tmpl != services.TemplateVerifyEmail || !h.mails[0].data.MarketingOptIn {
		t.Fatalf("resent mail without the marketing notice: %+v", h.mails)
	}

	h = setup(t, true)
	post(handleSignup, goodSignup)
	h.mails = nil
	post(handleResendVerification, `{"email":"new@example.com"}`)
	if len(h.mails) != 1 || h.mails[0].data.MarketingOptIn {
		t.Fatalf("a resend without an opt-in: %+v", h.mails)
	}
}

// S-32: the verify mail a duplicate signup triggers for an unverified account carries the notice too.
func TestSignup_DuplicateOfUnverifiedOptIn_VerifyMailCarriesTheMarketingNotice(t *testing.T) {
	h := setup(t, true)
	body := withMarketing(`,"marketingOptIn":true,"marketingWordingVersion":"` + services.MarketingWordingVersion + `"`)
	post(handleSignup, body)
	h.mails = nil
	if rec := post(handleSignup, goodSignup); rec.Code != 202 {
		t.Fatalf("got %d", rec.Code)
	}
	if len(h.mails) != 1 || h.mails[0].tmpl != services.TemplateVerifyEmail || !h.mails[0].data.MarketingOptIn {
		t.Fatalf("duplicate-signup mail without the marketing notice: %+v", h.mails)
	}
}

// S-18 through the handlers: a signup for a Withdrawn address leaves it Withdrawn (its verify mail
// still announces the subscription), and verifying the address opts it back in.
func TestSignup_WithdrawnAddress_StaysWithdrawnUntilVerified(t *testing.T) {
	h := setup(t, true)
	withdrawn := h.now.Add(-time.Hour)
	h.store.consents["new@example.com"] = &models.MarketingConsent{ID: uuid.New(), Email: "new@example.com",
		Status: models.MarketingConsentStatusWithdrawn, Source: models.MarketingConsentSourceSiteFooter, WordingVersion: "old",
		WithdrawnAt: &withdrawn, UpdatedAt: withdrawn}
	post(handleSignup, withMarketing(`,"marketingOptIn":true,"marketingWordingVersion":"`+services.MarketingWordingVersion+`"`))
	c := h.store.consents["new@example.com"]
	if c.Status != models.MarketingConsentStatusWithdrawn || c.UserID != nil || c.ResubscribeRequestedAt == nil {
		t.Fatalf("after an unverified signup: %+v", c)
	}
	if len(h.mails) != 1 || !h.mails[0].data.MarketingOptIn {
		t.Fatalf("mails: %+v", h.mails)
	}
	h.now = h.now.Add(time.Minute)
	if rec := post(handleVerifyEmail, `{"token":"`+tokenFromMail(t, h.mails[0])+`"}`); rec.Code != 200 {
		t.Fatalf("verify got %d: %s", rec.Code, rec.Body)
	}
	c = h.store.consents["new@example.com"]
	u, _ := h.store.FindUserByEmail("new@example.com")
	if c.Status != models.MarketingConsentStatusOptedIn || c.ConfirmedAt == nil || c.WithdrawnAt != nil || c.UserID == nil || *c.UserID != u.ID {
		t.Fatalf("after verify: %+v", c)
	}
}
