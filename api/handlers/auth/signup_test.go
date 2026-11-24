package auth

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

// fakeStore is an in-memory services.AccountStore with the same token
// semantics as the GORM implementation (single use, TTL, supersession).
type fakeStore struct {
	mu          sync.Mutex
	users       map[uuid.UUID]*models.ApplicationUser
	tokens      []*models.UserToken
	acceptances []models.LegalAcceptance
	invOrgs     *fakeOrgs // set by the invitation tests
	// legalVersions is what CurrentLegalVersions answers with: the versions of
	// the legal documents the deployment serves (F-7). Defaults to 2026-10 for
	// both, which is what the pre-F-7 signup bodies in these tests name.
	legalVersions services.LegalVersions
	// legalVersionsErr, when set, makes the read fail like legal_documents
	// having no row (or the database being unreachable).
	legalVersionsErr error
	// consents and consentEvents mirror marketing_consents and
	// marketing_consent_events; registerOptIns records the opt-in each
	// RegisterUser/RegisterInvitedUser call carried (the real store writes it in
	// that call's transaction).
	consents       map[string]*models.MarketingConsent
	consentEvents  []models.MarketingConsentEvent
	registerOptIns []*services.MarketingOptIn
	// noPersonalOrg makes the Register* methods leave LastActiveOrganizationID unset (see
	// stampPersonalOrg).
	noPersonalOrg bool
}

func newFakeStore() *fakeStore {
	return &fakeStore{
		users:         map[uuid.UUID]*models.ApplicationUser{},
		consents:      map[string]*models.MarketingConsent{},
		legalVersions: services.LegalVersions{Terms: "2026-10", Privacy: "2026-10"},
	}
}

// serveLegalVersions changes what the deployment claims to be serving, so a test
// can submit a version that is not it.
func serveLegalVersions(t *testing.T, store *fakeStore, terms, privacy string) {
	t.Helper()
	store.legalVersions = services.LegalVersions{Terms: terms, Privacy: privacy}
}

// breakLegalVersions makes the current-version read fail.
func breakLegalVersions(t *testing.T, store *fakeStore, err error) {
	t.Helper()
	store.legalVersionsErr = err
}

func (f *fakeStore) FindUserByEmail(email string) (*models.ApplicationUser, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, u := range f.users {
		if u.Email == email {
			c := *u
			return &c, nil
		}
	}
	return nil, nil
}

func (f *fakeStore) FindUserByID(id uuid.UUID) (*models.ApplicationUser, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if u, ok := f.users[id]; ok {
		c := *u
		return &c, nil
	}
	return nil, nil
}

// stampPersonalOrg does what the real stores' tenancy provisioning does to the user they are handed:
// it points LastActiveOrganizationID at the Personal workspace the same transaction created. The
// handlers read it back for the alpha.signup row. Off (noPersonalOrg) it leaves the user as it came,
// which is what "the Personal organization is not known" looks like to a handler.
func (f *fakeStore) stampPersonalOrg(u *models.ApplicationUser) {
	if f.noPersonalOrg {
		return
	}
	id := uuid.New()
	u.LastActiveOrganizationID = &id
}

func (f *fakeStore) RegisterUser(u *models.ApplicationUser, a []models.LegalAcceptance, t *models.UserToken, optIn *services.MarketingOptIn) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.stampPersonalOrg(u)
	c := *u
	f.users[u.ID] = &c
	f.acceptances = append(f.acceptances, a...)
	f.tokens = append(f.tokens, t)
	f.applyOptIn(u, optIn)
	return nil
}

// applyOptIn is the in-memory twin of the real store's in-transaction consent
// upsert, built on the same pure services.SignupMarketingConsent. Caller holds mu.
func (f *fakeStore) applyOptIn(u *models.ApplicationUser, optIn *services.MarketingOptIn) {
	f.registerOptIns = append(f.registerOptIns, optIn)
	if optIn == nil {
		return
	}
	row, events := services.SignupMarketingConsent(f.consents[u.Email], u, *optIn)
	f.consents[row.Email] = &row
	f.consentEvents = append(f.consentEvents, events...)
}

func (f *fakeStore) IssueToken(t *models.UserToken) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, o := range f.tokens {
		if o.UserID == t.UserID && o.Purpose == t.Purpose && o.UsedAt == nil {
			used := t.CreatedAt
			o.UsedAt = &used
		}
	}
	f.tokens = append(f.tokens, t)
	return nil
}

func (f *fakeStore) consume(purpose, hash string, now time.Time) (*models.UserToken, error) {
	for _, t := range f.tokens {
		if t.TokenHash == hash && t.Purpose == purpose {
			if t.UsedAt != nil || !t.ExpiresAt.After(now) {
				return nil, services.ErrInvalidToken
			}
			t.UsedAt = &now
			return t, nil
		}
	}
	return nil, services.ErrInvalidToken
}

func (f *fakeStore) ConsumeVerifyEmail(hash string, now time.Time) (*models.ApplicationUser, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	t, err := f.consume(models.UserTokenVerifyEmail, hash, now)
	if err != nil {
		return nil, err
	}
	u := f.users[t.UserID]
	if u.EmailVerifiedAt == nil {
		u.EmailVerifiedAt = &now
	}
	// Like the real store (S-18, S-19): the row for the address being verified, confirmed when it is
	// the user's own opt-in or carries the user's signup request.
	if consent := f.consents[normalizeEmail(u.Email)]; consent != nil {
		f.consentEvents = append(f.consentEvents,
			services.ConfirmSignupOptInOnVerify(consent, f.latestSignupRequest(consent.ID, u.ID), u.ID, now)...)
	}
	c := *u
	return &c, nil
}

// latestSignupRequest is the newest Requested event a Signup by userID recorded on the row. Caller
// holds mu.
func (f *fakeStore) latestSignupRequest(consentID, userID uuid.UUID) *models.MarketingConsentEvent {
	var latest *models.MarketingConsentEvent
	for i := range f.consentEvents {
		ev := &f.consentEvents[i]
		if ev.ConsentID == consentID && ev.UserID != nil && *ev.UserID == userID &&
			ev.Action == models.MarketingConsentActionRequested && ev.Source == models.MarketingConsentSourceSignup &&
			(latest == nil || ev.OccurredAt.After(latest.OccurredAt)) {
			latest = ev
		}
	}
	if latest == nil {
		return nil
	}
	c := *latest
	return &c
}

func (f *fakeStore) MarketingOptInPending(userID uuid.UUID, email string) (bool, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	consent := f.consents[normalizeEmail(email)]
	if consent == nil {
		return false, nil
	}
	return services.SignupOptInPending(consent, f.latestSignupRequest(consent.ID, userID), userID), nil
}

type sentMail struct {
	to   string
	tmpl services.TemplateName
	data services.MailData
}

type harness struct {
	store *fakeStore
	mails []sentMail
	now   time.Time
}

// setup installs fakes into the package dependencies and a Cloud-style config.
func setup(t *testing.T, signupEnabled bool) *harness {
	t.Helper()
	oldCfg, oldAcct := config.Cfg, acct
	config.Cfg.JWTSigningKey = "0123456789abcdef0123456789abcdef"
	config.Cfg.JWTIssuer = "reelbolt-api"
	config.Cfg.JWTAudience = "reelbolt-inference"
	config.Cfg.PublicBaseURL = "https://reelbolt.test"
	config.Cfg.LinkBaseURL = "https://reelbolt.test"
	config.Cfg.SignupEnabled = signupEnabled
	t.Cleanup(func() { config.Cfg, acct = oldCfg, oldAcct })

	h := &harness{store: newFakeStore(), now: time.Date(2026, 10, 6, 12, 0, 0, 0, time.UTC)}
	acct = accountDeps{
		store: h.store,
		sendMail: func(to string, tmpl services.TemplateName, data services.MailData) error {
			h.mails = append(h.mails, sentMail{to, tmpl, data})
			return nil
		},
		resolveMembership: func(_ context.Context, u *models.ApplicationUser) (*services.ActiveMembership, error) {
			return &services.ActiveMembership{OrgID: uuid.New(), OrgName: "Personal", OrgKind: models.OrgKindPersonal, Role: models.OrgRoleOwner}, nil
		},
		runAsync:        func(f func()) { f() },
		now:             func() time.Time { return h.now },
		verifyChallenge: func(context.Context, string, string) (bool, error) { return true, nil },
		checkMX:         func(context.Context, string) error { return nil },
	}
	config.Cfg.AbuseBlockDisposable = true
	return h
}

func post(handler http.HandlerFunc, body string) *httptest.ResponseRecorder {
	req := httptest.NewRequestWithContext(context.Background(), "POST", "/x", strings.NewReader(body))
	req.Header.Set("X-Real-IP", "203.0.113.9")
	rec := httptest.NewRecorder()
	handler(rec, req)
	return rec
}

const goodSignup = `{"email":"  New@Example.COM ","password":"correct horse","displayName":"Ada","acceptTerms":true,"termsVersion":"2026-10","privacyVersion":"2026-10"}`

// tokenFromMail extracts the raw token from the link of the last mail.
func tokenFromMail(t *testing.T, m sentMail) string {
	t.Helper()
	u, err := url.Parse(m.data.Link)
	if err != nil || u.Query().Get("token") == "" {
		t.Fatalf("no token in link %q", m.data.Link)
	}
	return u.Query().Get("token")
}

func TestSignup_DisabledIs404(t *testing.T) {
	setup(t, false)
	if rec := post(handleSignup, goodSignup); rec.Code != 404 {
		t.Fatalf("got %d", rec.Code)
	}
	if rec := post(handleResendVerification, `{"email":"a@b.co"}`); rec.Code != 404 {
		t.Fatalf("resend got %d", rec.Code)
	}
}

func TestSignup_HappyPath_StoresUnverifiedUserAcceptancesAndHashedToken(t *testing.T) {
	h := setup(t, true)
	rec := post(handleSignup, goodSignup)
	if rec.Code != 202 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	u, _ := h.store.FindUserByEmail("new@example.com")
	if u == nil {
		t.Fatal("email must be lower-cased and trimmed")
	}
	if u.EmailVerifiedAt != nil || u.PasswordHash == "" || u.PasswordHash == "correct horse" {
		t.Fatalf("user: %+v", u)
	}
	if u.SignupIP == nil || *u.SignupIP != "203.0.113.9" {
		t.Fatalf("signup ip: %v", u.SignupIP)
	}
	if len(h.store.acceptances) != 2 || h.store.acceptances[0].Document != models.LegalDocumentTerms || h.store.acceptances[1].Document != models.LegalDocumentPrivacy {
		t.Fatalf("acceptances: %+v", h.store.acceptances)
	}
	if len(h.mails) != 1 || h.mails[0].tmpl != services.TemplateVerifyEmail || h.mails[0].to != "new@example.com" {
		t.Fatalf("mails: %+v", h.mails)
	}
	raw := tokenFromMail(t, h.mails[0])
	if !strings.HasPrefix(h.mails[0].data.Link, "https://reelbolt.test/app/verify-email?token=") {
		t.Fatalf("link: %s", h.mails[0].data.Link)
	}
	stored := h.store.tokens[0]
	if stored.TokenHash == raw || stored.TokenHash != services.HashToken(raw) {
		t.Fatal("only the SHA-256 of the token may be stored")
	}
	if got := stored.ExpiresAt.Sub(stored.CreatedAt); got != 24*time.Hour {
		t.Fatalf("verify TTL %v", got)
	}
}

func TestSignup_Validation(t *testing.T) {
	setup(t, true)
	cases := map[string]struct {
		body string
		want int
	}{
		"short password": {`{"email":"a@b.co","password":"123456789","acceptTerms":true,"termsVersion":"1","privacyVersion":"1"}`, 400},
		"bad email":      {`{"email":"nope","password":"correct horse","acceptTerms":true,"termsVersion":"1","privacyVersion":"1"}`, 400},
		"no terms":       {`{"email":"a@b.co","password":"correct horse","acceptTerms":false,"termsVersion":"1","privacyVersion":"1"}`, 400},
		// No version at all is not a malformed body, it is a body that names
		// nothing the deployment serves (F-7) — see
		// legal_versions_test.go for the mismatch cases proper.
		"no versions": {`{"email":"a@b.co","password":"correct horse","acceptTerms":true}`, 409},
	}
	for name, tc := range cases {
		if rec := post(handleSignup, tc.body); rec.Code != tc.want {
			t.Errorf("%s: got %d, want %d (%s)", name, rec.Code, tc.want, rec.Body)
		}
	}
}

func TestSignup_DuplicateEmail_SameResponseAndNoSecondUser(t *testing.T) {
	h := setup(t, true)
	first := post(handleSignup, goodSignup)
	second := post(handleSignup, goodSignup)
	if first.Code != 202 || second.Code != 202 || first.Body.String() != second.Body.String() {
		t.Fatalf("responses differ: %d %q vs %d %q", first.Code, first.Body, second.Code, second.Body)
	}
	if len(h.store.users) != 1 {
		t.Fatalf("users: %d", len(h.store.users))
	}
	// The existing, still-unverified account is mailed a fresh link; the older one is superseded.
	if len(h.mails) != 2 || h.mails[1].tmpl != services.TemplateVerifyEmail {
		t.Fatalf("mails: %+v", h.mails)
	}
	oldTok := tokenFromMail(t, h.mails[0])
	if rec := post(handleVerifyEmail, `{"token":"`+oldTok+`"}`); rec.Code != 400 {
		t.Fatalf("superseded token accepted: %d", rec.Code)
	}
}

func TestSignup_DuplicateOfVerifiedAccount_MailsResetLink(t *testing.T) {
	h := setup(t, true)
	post(handleSignup, goodSignup)
	post(handleVerifyEmail, `{"token":"`+tokenFromMail(t, h.mails[0])+`"}`)
	h.mails = nil
	if rec := post(handleSignup, goodSignup); rec.Code != 202 {
		t.Fatalf("got %d", rec.Code)
	}
	if len(h.mails) != 1 || h.mails[0].tmpl != services.TemplateResetPassword {
		t.Fatalf("mails: %+v", h.mails)
	}
}

func TestVerifyEmail_LogsInAndIsSingleUse(t *testing.T) {
	h := setup(t, true)
	post(handleSignup, goodSignup)
	raw := tokenFromMail(t, h.mails[0])

	rec := post(handleVerifyEmail, `{"token":"`+raw+`"}`)
	if rec.Code != 200 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	var resp TokenResponse
	_ = json.Unmarshal(rec.Body.Bytes(), &resp)
	claims, err := services.ValidateToken(resp.AccessToken)
	if err != nil || !claims.EmailVerified || claims.Email != "new@example.com" {
		t.Fatalf("claims %+v err %v", claims, err)
	}
	cookies := map[string]*http.Cookie{}
	for _, c := range rec.Result().Cookies() {
		cookies[c.Name] = c
	}
	if c := cookies["reelbolt_token"]; c == nil || !c.HttpOnly || c.Value != resp.AccessToken {
		t.Fatalf("token cookie %+v", c)
	}
	raw2, _ := url.QueryUnescape(cookies["reelbolt_user"].Value)
	if !strings.Contains(raw2, `"emailVerified":true`) {
		t.Fatalf("user cookie %s", raw2)
	}
	last := h.mails[len(h.mails)-1]
	if last.tmpl != services.TemplateWelcome {
		t.Fatalf("welcome mail missing: %+v", h.mails)
	}

	if rec := post(handleVerifyEmail, `{"token":"`+raw+`"}`); rec.Code != 400 {
		t.Fatalf("reused token got %d", rec.Code)
	}
}

func TestVerifyEmail_ExpiredAndUnknownAreIndistinguishable(t *testing.T) {
	h := setup(t, true)
	post(handleSignup, goodSignup)
	raw := tokenFromMail(t, h.mails[0])

	h.now = h.now.Add(24*time.Hour + time.Second)
	expired := post(handleVerifyEmail, `{"token":"`+raw+`"}`)
	unknown := post(handleVerifyEmail, `{"token":"deadbeef"}`)
	if expired.Code != 400 || unknown.Code != 400 || expired.Body.String() != unknown.Body.String() {
		t.Fatalf("expired %d %q unknown %d %q", expired.Code, expired.Body, unknown.Code, unknown.Body)
	}
	u, _ := h.store.FindUserByEmail("new@example.com")
	if u.EmailVerifiedAt != nil {
		t.Fatal("expired token verified the account")
	}
}

func TestUnverifiedLogin_TokenCarriesEmailVerifiedFalse(t *testing.T) {
	h := setup(t, true)
	post(handleSignup, goodSignup)
	u, _ := h.store.FindUserByEmail("new@example.com")
	tok, err := services.GenerateToken(u, nil)
	if err != nil {
		t.Fatal(err)
	}
	claims, _ := services.ValidateToken(tok)
	if claims.EmailVerified {
		t.Fatal("unverified user must have emailVerified=false")
	}
}

func TestResendVerification_AlwaysAccepted(t *testing.T) {
	h := setup(t, true)
	post(handleSignup, goodSignup)
	h.mails = nil

	unknown := post(handleResendVerification, `{"email":"ghost@example.com"}`)
	known := post(handleResendVerification, `{"email":"NEW@example.com"}`)
	if unknown.Code != 202 || known.Code != 202 || unknown.Body.String() != known.Body.String() {
		t.Fatalf("%d/%d %q/%q", unknown.Code, known.Code, unknown.Body, known.Body)
	}
	if len(h.mails) != 1 || h.mails[0].to != "new@example.com" {
		t.Fatalf("mails: %+v", h.mails)
	}
}

func TestSignup_DisplayNameValidation(t *testing.T) {
	setup(t, true)
	tmpl := `{"email":"a@b.co","password":"correct horse","displayName":%q,"acceptTerms":true,"termsVersion":"2026-10","privacyVersion":"2026-10"}`
	for name, tc := range map[string]struct {
		in   string
		want int
	}{
		"100 emoji ok":  {strings.Repeat("😀", 100), 202},
		"101 emoji":     {strings.Repeat("😀", 101), 400},
		"zero width":    {"a​b", 400},
		"bidi override": {"a‮b", 400},
		"newline":       {"a\nb", 400},
		"nbsp":          {"a b", 400},
	} {
		if rec := post(handleSignup, fmt.Sprintf(tmpl, tc.in)); rec.Code != tc.want {
			t.Errorf("%s: got %d, want %d (%s)", name, rec.Code, tc.want, rec.Body)
		}
	}
}

func TestSignup_BlankNameFallbackIsSanitised(t *testing.T) {
	for name, tc := range map[string]struct{ local, want string }{
		"long local part": {strings.Repeat("a", 5000), strings.Repeat("a", 100)},
		"bidi and zwsp":   {"ev‮il​x", "evilx"},
		"only invisible":  {"​‮", "User"},
		"long multibyte":  {strings.Repeat("é", 300), strings.Repeat("é", 100)},
	} {
		if got := fallbackDisplayName(tc.local); got != tc.want {
			t.Errorf("%s: got %q, want %q", name, got, tc.want)
		}
	}
	h := setup(t, true)
	body := fmt.Sprintf(`{"email":"%s@example.com","password":"correct horse","acceptTerms":true,"termsVersion":"2026-10","privacyVersion":"2026-10"}`, strings.Repeat("b", 150))
	if rec := post(handleSignup, body); rec.Code != 202 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	u, _ := h.store.FindUserByEmail(strings.Repeat("b", 150) + "@example.com")
	if u == nil || len([]rune(u.DisplayName)) != 100 {
		t.Fatalf("stored name: %+v", u)
	}
}
