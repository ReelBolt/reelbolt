package auth

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
	"golang.org/x/crypto/bcrypt"
)

// TestMain pins writeSession's terms-version lookup to "" (never accepted).
// The production lookup reads the database, and these tests run without one, so
// the default has to be the fake. A test that wants a recorded version
// overrides sess through useFakeTermsVersion.
func TestMain(m *testing.M) {
	sess = sessionDeps{termsVersion: func(uuid.UUID) (string, error) { return "", nil }}
	os.Exit(m.Run())
}

// useFakeTermsVersion points the session lookup at the in-memory store, so a
// recorded acceptance shows up in the cookie and the claim like the real one.
func useFakeTermsVersion(t *testing.T, store *fakeStore) {
	t.Helper()
	old := sess
	sess = sessionDeps{termsVersion: func(id uuid.UUID) (string, error) {
		v, err := store.LatestAcceptances(id)
		if err != nil {
			return "", err
		}
		return v[models.LegalDocumentTerms], nil
	}}
	t.Cleanup(func() { sess = old })
}

// The new AccountStore methods, as the in-memory store implements them.

func (f *fakeStore) RegisterOAuthUser(u *models.ApplicationUser) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.stampPersonalOrg(u)
	c := *u
	f.users[u.ID] = &c
	return nil
}

func (f *fakeStore) MarkEmailVerified(id uuid.UUID, at time.Time) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if u, ok := f.users[id]; ok && u.EmailVerifiedAt == nil {
		u.EmailVerifiedAt = &at
	}
	return nil
}

func (f *fakeStore) LatestAcceptances(userID uuid.UUID) (map[string]string, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := map[string]string{}
	for _, a := range f.acceptances {
		if a.UserID == userID {
			out[a.Document] = a.Version
		}
	}
	return out, nil
}

// CurrentLegalVersions answers with what the deployment claims to serve (F-7).
func (f *fakeStore) CurrentLegalVersions() (services.LegalVersions, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.legalVersionsErr != nil {
		return services.LegalVersions{}, f.legalVersionsErr
	}
	return f.legalVersions, nil
}

func (f *fakeStore) RecordAcceptances(list []models.LegalAcceptance) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, a := range list {
		already := false
		for _, old := range f.acceptances {
			if old.UserID == a.UserID && old.Document == a.Document && old.Version == a.Version {
				already = true
				break
			}
		}
		if !already {
			f.acceptances = append(f.acceptances, a)
		}
	}
	return nil
}

// fakeProvider is a local stand-in for Google's/GitHub's endpoints. There are
// no provider credentials on this host, so the whole flow is exercised against
// this: the same HTTP contract the real endpoints speak, minus the account.
type fakeProvider struct {
	server      *httptest.Server
	code        string
	userInfo    string
	emails      string
	tokenBody   string
	tokenStatus int

	gotVerifier string
	gotRedirect string
	gotForm     url.Values
}

func newFakeProvider(t *testing.T) *fakeProvider {
	t.Helper()
	f := &fakeProvider{
		code:      "the-code",
		tokenBody: `{"access_token":"tok","token_type":"Bearer"}`,
	}
	mux := http.NewServeMux()
	mux.HandleFunc("/token", func(w http.ResponseWriter, r *http.Request) {
		_ = r.ParseForm()
		f.gotForm = r.PostForm
		f.gotVerifier = r.PostForm.Get("code_verifier")
		f.gotRedirect = r.PostForm.Get("redirect_uri")
		w.Header().Set("Content-Type", "application/json")
		if f.tokenStatus != 0 && f.tokenStatus != http.StatusOK {
			w.WriteHeader(f.tokenStatus)
			_, _ = w.Write([]byte(`{"error":"invalid_grant"}`))
			return
		}
		_, _ = w.Write([]byte(f.tokenBody))
	})
	mux.HandleFunc("/userinfo", func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer tok" {
			w.WriteHeader(http.StatusUnauthorized)
			return
		}
		_, _ = w.Write([]byte(f.userInfo))
	})
	mux.HandleFunc("/emails", func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(f.emails))
	})
	f.server = httptest.NewServer(mux)
	t.Cleanup(f.server.Close)
	return f
}

// installGoogle points the google provider at the fake, keeping every other
// entry (and restoring the table afterwards).
func installGoogle(t *testing.T, f *fakeProvider) {
	t.Helper()
	old := oauthProviders
	next := map[string]*oauthProvider{"github": old["github"]}
	next["google"] = &oauthProvider{
		name:        "google",
		authURL:     f.server.URL + "/authorize",
		tokenURL:    f.server.URL + "/token",
		userInfoURL: f.server.URL + "/userinfo",
		scope:       "openid email profile",
		usePKCE:     true,
		decode:      decodeGoogleIdentity,
	}
	oauthProviders = next
	t.Cleanup(func() { oauthProviders = old })
}

// oauthSetup is setup() plus working OAuth credentials.
func oauthSetup(t *testing.T) *harness {
	t.Helper()
	h := setup(t, true)
	old := config.Cfg
	config.Cfg.OAuthGoogleClientID = "google-client"
	config.Cfg.OAuthGoogleClientSecret = "google-secret"
	config.Cfg.OAuthGitHubClientID = "github-client"
	config.Cfg.OAuthGitHubClientSecret = "github-secret"
	t.Cleanup(func() { config.Cfg = old })
	return h
}

func oauthCall(t *testing.T, handler http.HandlerFunc, provider, query, stateCookie string) *httptest.ResponseRecorder {
	t.Helper()
	req := httptest.NewRequestWithContext(context.Background(), "GET", "/api/v1/auth/oauth/"+provider+"/callback"+query, nil)
	req.Header.Set("X-Real-IP", "203.0.113.9")
	req = mux.SetURLVars(req, map[string]string{"provider": provider})
	if stateCookie != "" {
		req.AddCookie(&http.Cookie{Name: oauthStateCookie, Value: stateCookie})
	}
	rec := httptest.NewRecorder()
	handler(rec, req)
	return rec
}

func cookieMap(rec *httptest.ResponseRecorder) map[string]*http.Cookie {
	out := map[string]*http.Cookie{}
	for _, c := range rec.Result().Cookies() {
		out[c.Name] = c
	}
	return out
}

func TestOAuthStart_RedirectsWithPKCEAndSetsStateCookie(t *testing.T) {
	oauthSetup(t)
	f := newFakeProvider(t)
	installGoogle(t, f)

	req := httptest.NewRequestWithContext(context.Background(), "GET", "/api/v1/auth/oauth/google/start", nil)
	req = mux.SetURLVars(req, map[string]string{"provider": "google"})
	rec := httptest.NewRecorder()
	handleOAuthStart(rec, req)

	if rec.Code != http.StatusFound {
		t.Fatalf("got %d", rec.Code)
	}
	loc, err := url.Parse(rec.Header().Get("Location"))
	if err != nil || !strings.HasPrefix(loc.String(), f.server.URL+"/authorize") {
		t.Fatalf("location %q err %v", rec.Header().Get("Location"), err)
	}
	q := loc.Query()
	if q.Get("client_id") != "google-client" || q.Get("state") == "" || q.Get("code_challenge_method") != "S256" {
		t.Fatalf("authorize query: %v", q)
	}
	if q.Get("redirect_uri") != "https://reelbolt.test/api/v1/auth/oauth/google/callback" {
		t.Fatalf("redirect_uri %q", q.Get("redirect_uri"))
	}

	c := cookieMap(rec)[oauthStateCookie]
	if c == nil || !c.HttpOnly || c.SameSite != http.SameSiteLaxMode {
		t.Fatalf("state cookie: %+v", c)
	}
	parts := strings.SplitN(c.Value, ":", 3)
	if len(parts) != 3 || parts[0] != "google" {
		t.Fatalf("state cookie value %q", c.Value)
	}
	// The challenge in the URL must be the S256 of the verifier in the cookie.
	if q.Get("code_challenge") != pkceChallenge(parts[2]) {
		t.Fatal("code_challenge does not match the stored verifier")
	}
}

func TestOAuthStart_UnconfiguredProviderIs404(t *testing.T) {
	setup(t, true) // no OAuth credentials
	for _, path := range []string{"google", "github"} {
		req := httptest.NewRequestWithContext(context.Background(), "GET", "/api/v1/auth/oauth/"+path+"/start", nil)
		req = mux.SetURLVars(req, map[string]string{"provider": path})
		rec := httptest.NewRecorder()
		handleOAuthStart(rec, req)
		if rec.Code != http.StatusNotFound {
			t.Fatalf("%s: got %d", path, rec.Code)
		}
	}
	// An unknown provider is the same 404.
	req := httptest.NewRequestWithContext(context.Background(), "GET", "/api/v1/auth/oauth/facebook/start", nil)
	req = mux.SetURLVars(req, map[string]string{"provider": "facebook"})
	rec := httptest.NewRecorder()
	handleOAuthStart(rec, req)
	if rec.Code != http.StatusNotFound {
		t.Fatalf("unknown provider: got %d", rec.Code)
	}
}

// assertPlaceholderHash proves a stored hash is a real, unusable bcrypt hash:
// bcrypt can parse it, it is not the hash of a password anyone could type (or
// of the empty string), and two calls are not reusable across accounts.
func assertPlaceholderHash(t *testing.T, hash string) {
	t.Helper()
	if hash == "" {
		t.Fatal("the stored hash must not be empty: an empty hash lets bcrypt return before any work, re-opening the timing oracle")
	}
	// bcrypt.Cost parses the hash; it errors on anything that is not a valid
	// bcrypt hash (empty or < 60 bytes, wrong prefix) before doing any work.
	// A real placeholder hash parses with the library's default cost.
	if cost, err := bcrypt.Cost([]byte(hash)); err != nil {
		t.Fatalf("the stored hash is not a valid bcrypt hash: %v", err)
	} else if cost != bcrypt.DefaultCost {
		t.Fatalf("placeholder hash cost is %d, want %d (DefaultCost, matching the login path)", cost, bcrypt.DefaultCost)
	}
	// Not a valid password: no guessable value can match it.
	for _, guess := range []string{"", "password", "password123", "123456", "changeme", "admin", "admin123"} {
		if bcrypt.CompareHashAndPassword([]byte(hash), []byte(guess)) == nil {
			t.Fatalf("the placeholder hash matches the guessable password %q", guess)
		}
	}
	// Two accounts do not share the same hash (random per account).
	again, err := services.GeneratePlaceholderPasswordHash()
	if err != nil {
		t.Fatalf("generate placeholder: %v", err)
	}
	if again == hash {
		t.Fatal("two placeholder hashes are identical: the value must be random per account, not a reusable literal")
	}
}

func TestOAuthCallback_NewAccountCarriesARealUnusableBcryptHash(t *testing.T) {
	h := oauthSetup(t)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"999","email":"Grace@Example.COM","email_verified":true,"name":"Grace Hopper"}`
	installGoogle(t, f)

	rec := oauthCall(t, handleOAuthCallback, "google",
		"?code=the-code&state=st-ph", "google:st-ph:verifier-ph")
	if rec.Code != http.StatusFound {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}

	u, _ := h.store.FindUserByEmail("grace@example.com")
	if u == nil {
		t.Fatal("account not created")
	}
	assertPlaceholderHash(t, u.PasswordHash)
}

func TestOAuthCallback_NewUserIsCreatedVerifiedAndSentToAcceptTerms(t *testing.T) {
	h := oauthSetup(t)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"123","email":"Ada@Example.COM","email_verified":true,"name":"Ada Lovelace"}`
	installGoogle(t, f)

	rec := oauthCall(t, handleOAuthCallback, "google",
		"?code=the-code&state=st-1", "google:st-1:verifier-abc")
	if rec.Code != http.StatusFound {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	// First login: no acceptance, so consent is captured on the accept-terms page.
	if got := rec.Header().Get("Location"); got != "https://reelbolt.test/app/accept-terms" {
		t.Fatalf("location %q", got)
	}
	// PKCE verifier went out with the exchange.
	if f.gotVerifier != "verifier-abc" {
		t.Fatalf("code_verifier %q", f.gotVerifier)
	}
	if f.gotRedirect != "https://reelbolt.test/api/v1/auth/oauth/google/callback" {
		t.Fatalf("redirect_uri %q", f.gotRedirect)
	}

	u, _ := h.store.FindUserByEmail("ada@example.com")
	if u == nil {
		t.Fatal("account not created")
	}
	if u.EmailVerifiedAt == nil || u.AuthProvider != "Google" {
		t.Fatalf("user %+v", u)
	}
	assertPlaceholderHash(t, u.PasswordHash)
	if u.DisplayName != "Ada Lovelace" {
		t.Fatalf("display name %q", u.DisplayName)
	}

	cookies := cookieMap(rec)
	if cookies["reelbolt_token"] == nil || cookies["reelbolt_user"] == nil {
		t.Fatal("session cookies missing")
	}
	if cookies[oauthStateCookie] == nil || cookies[oauthStateCookie].MaxAge >= 0 {
		t.Fatal("the state cookie must be cleared")
	}
	// Cookies only: the flow answers with a redirect, so no token JSON is
	// written (a handler that wrote a 200 first could not redirect at all).
	if strings.Contains(rec.Body.String(), "accessToken") {
		t.Fatalf("a token body was written before the redirect: %q", rec.Body.String())
	}
}

// C11 F-1, the ruling: an account that never verified its own address is NOT
// linked. The fixture is the pre-hijack — somebody signs up first with the
// victim's address, unverified, with a password of their own — and the victim's
// Google sign-in must not land in that account.
func TestOAuthCallback_UnverifiedExistingAccountIsNotLinked(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"1","email":"ada@example.com","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)

	// An unverified password account for the same address, already accepted at signup.
	id := uuid.New()
	h.store.users[id] = &models.ApplicationUser{
		ID: id, Email: "ada@example.com", DisplayName: "Ada", AuthProvider: "Password",
		PasswordHash: "the hash the first signer-up chose",
	}
	h.store.acceptances = append(h.store.acceptances,
		models.LegalAcceptance{ID: uuid.New(), UserID: id, Document: models.LegalDocumentTerms, Version: "2026-10"},
		models.LegalAcceptance{ID: uuid.New(), UserID: id, Document: models.LegalDocumentPrivacy, Version: "2026-10"},
	)

	rec := oauthCall(t, handleOAuthCallback, "google", "?code=the-code&state=st-2", "google:st-2:v")
	// A closed reason code, and one that tells the visitor what to do: the address
	// they just proved at the provider already has an account that has not
	// verified itself. Only the mailbox's owner can reach this answer.
	if got := rec.Header().Get("Location"); got != "https://reelbolt.test/app/login?error=oauth_account_unverified" {
		t.Fatalf("location %q", got)
	}
	if len(h.store.users) != 1 {
		t.Fatalf("a second account was created for the address: %d", len(h.store.users))
	}
	u := h.store.users[id]
	if u.EmailVerifiedAt != nil {
		t.Fatal("a link that did not happen must not stamp the account verified")
	}
	if u.AuthProvider != "Password" {
		t.Fatalf("the account's origin must be untouched: %q", u.AuthProvider)
	}
	if u.PasswordHash != "the hash the first signer-up chose" {
		t.Fatalf("the account must be left exactly as it was: %q", u.PasswordHash)
	}
	// The whole point: the victim is not signed into the attacker's account.
	if c := cookieMap(rec)["reelbolt_token"]; c != nil {
		t.Fatal("a session was issued for the unverified account")
	}
	if c := cookieMap(rec)["reelbolt_user"]; c != nil {
		t.Fatal("the readable session cookie was set for the unverified account")
	}
}

// The other half of the ruling: linking a VERIFIED account still works, and
// still changes nothing but the sign-in itself.
func TestOAuthCallback_VerifiedExistingAccountIsLinked(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"1","email":"ada@example.com","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)

	verifiedAt := h.now.Add(-time.Hour)
	id := uuid.New()
	h.store.users[id] = &models.ApplicationUser{
		ID: id, Email: "ada@example.com", DisplayName: "Ada", AuthProvider: "Password",
		PasswordHash: "the hash the owner chose", EmailVerifiedAt: &verifiedAt,
	}
	h.store.acceptances = append(h.store.acceptances,
		models.LegalAcceptance{ID: uuid.New(), UserID: id, Document: models.LegalDocumentTerms, Version: "2026-10"},
		models.LegalAcceptance{ID: uuid.New(), UserID: id, Document: models.LegalDocumentPrivacy, Version: "2026-10"},
	)

	rec := oauthCall(t, handleOAuthCallback, "google", "?code=the-code&state=st-3", "google:st-3:v")
	if got := rec.Header().Get("Location"); got != "https://reelbolt.test/app/dashboard" {
		t.Fatalf("location %q", got)
	}
	if len(h.store.users) != 1 {
		t.Fatalf("a second account was created: %d", len(h.store.users))
	}
	u := h.store.users[id]
	if u.EmailVerifiedAt == nil || !u.EmailVerifiedAt.Equal(verifiedAt) {
		t.Fatalf("linking must leave the account's own verification instant alone: %v", u.EmailVerifiedAt)
	}
	if u.AuthProvider != "Password" {
		t.Fatalf("linking must not rewrite the account's origin: %q", u.AuthProvider)
	}
	c := cookieMap(rec)["reelbolt_user"]
	if c == nil {
		t.Fatal("the session cookie is missing")
	}
	raw, _ := url.QueryUnescape(c.Value)
	if !strings.Contains(raw, `"termsVersion":"2026-10"`) {
		t.Fatalf("user cookie %s", raw)
	}
}

func TestOAuthCallback_UnverifiedEmailIsRefused(t *testing.T) {
	h := oauthSetup(t)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"1","email":"ada@example.com","email_verified":false}`
	installGoogle(t, f)

	rec := oauthCall(t, handleOAuthCallback, "google", "?code=the-code&state=s", "google:s:v")
	if got := rec.Header().Get("Location"); got != "https://reelbolt.test/app/login?error=oauth_email_unverified" {
		t.Fatalf("location %q", got)
	}
	if len(h.store.users) != 0 {
		t.Fatal("no account may be created from an unverified address")
	}
	if c := cookieMap(rec)["reelbolt_token"]; c != nil {
		t.Fatal("no session may be issued")
	}
}

func TestOAuthCallback_StateMismatchIsRefused(t *testing.T) {
	h := oauthSetup(t)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"1","email":"ada@example.com","email_verified":true}`
	installGoogle(t, f)

	cases := map[string]string{
		"no cookie":       "",
		"other state":     "google:other:v",
		"other provider":  "github:st:v",
		"no provider tag": "st:v",
	}
	for name, cookie := range cases {
		rec := oauthCall(t, handleOAuthCallback, "google", "?code=the-code&state=st", cookie)
		if rec.Code != http.StatusFound || !strings.Contains(rec.Header().Get("Location"), "error=oauth_failed") {
			t.Fatalf("%s: %d %q", name, rec.Code, rec.Header().Get("Location"))
		}
	}
	if len(h.store.users) != 0 {
		t.Fatal("no account may be created without a matching state")
	}
}

func TestOAuthCallback_TokenEndpointErrorIsRefused(t *testing.T) {
	h := oauthSetup(t)
	f := newFakeProvider(t)
	f.tokenStatus = http.StatusBadRequest
	installGoogle(t, f)

	rec := oauthCall(t, handleOAuthCallback, "google", "?code=the-code&state=st", "google:st:v")
	if !strings.Contains(rec.Header().Get("Location"), "error=oauth_failed") {
		t.Fatalf("location %q", rec.Header().Get("Location"))
	}
	if len(h.store.users) != 0 {
		t.Fatal("no account may be created when the exchange fails")
	}
}

func TestOAuthCallback_NewAccountRespectsThePerIPCap(t *testing.T) {
	h := oauthSetup(t)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"1","email":"ada@example.com","email_verified":true}`
	installGoogle(t, f)

	// Three accounts already from this network in the last hour.
	ip := "203.0.113.9"
	for i := 0; i < 3; i++ {
		id := uuid.New()
		h.store.users[id] = &models.ApplicationUser{
			ID: id, Email: fmt.Sprintf("x%d@y.z", i), SignupIP: &ip, CreatedAt: h.now.Add(-time.Minute),
		}
	}
	rec := oauthCall(t, handleOAuthCallback, "google", "?code=the-code&state=st", "google:st:v")
	if got := rec.Header().Get("Location"); got != "https://reelbolt.test/app/login?error=oauth_rate_limited" {
		t.Fatalf("location %q", got)
	}
}

func TestOAuthCallback_GitHubUsesTheVerifiedEmailList(t *testing.T) {
	h := oauthSetup(t)
	f := newFakeProvider(t)
	f.userInfo = `{"id":42,"login":"octocat","name":"Mona"}`
	f.emails = `[{"email":"unverified@example.com","primary":false,"verified":false},{"email":"mona@example.com","primary":true,"verified":true}]`

	old := oauthProviders
	oauthProviders = map[string]*oauthProvider{"google": old["google"]}
	oauthProviders["github"] = &oauthProvider{
		name: "github", authURL: f.server.URL + "/authorize", tokenURL: f.server.URL + "/token",
		userInfoURL: f.server.URL + "/userinfo", emailsURL: f.server.URL + "/emails",
		scope: "read:user user:email", usePKCE: true, decode: decodeGitHubIdentity,
	}
	t.Cleanup(func() { oauthProviders = old })

	rec := oauthCall(t, handleOAuthCallback, "github", "?code=the-code&state=st", "github:st:v")
	u, _ := h.store.FindUserByEmail("mona@example.com")
	if u == nil || u.AuthProvider != "GitHub" {
		t.Fatalf("user %+v (location %q)", u, rec.Header().Get("Location"))
	}
	if u.DisplayName != "Mona" {
		t.Fatalf("display name %q", u.DisplayName)
	}
}

func TestOAuthCallback_GitHubWithoutAVerifiedEmailIsRefused(t *testing.T) {
	h := oauthSetup(t)
	f := newFakeProvider(t)
	f.userInfo = `{"id":42,"login":"octocat"}`
	f.emails = `[{"email":"mona@example.com","primary":true,"verified":false}]`

	old := oauthProviders
	oauthProviders = map[string]*oauthProvider{"google": old["google"]}
	oauthProviders["github"] = &oauthProvider{
		name: "github", authURL: f.server.URL + "/authorize", tokenURL: f.server.URL + "/token",
		userInfoURL: f.server.URL + "/userinfo", emailsURL: f.server.URL + "/emails",
		scope: "read:user user:email", usePKCE: true, decode: decodeGitHubIdentity,
	}
	t.Cleanup(func() { oauthProviders = old })

	rec := oauthCall(t, handleOAuthCallback, "github", "?code=the-code&state=st", "github:st:v")
	if !strings.Contains(rec.Header().Get("Location"), "error=oauth_email_unverified") {
		t.Fatalf("location %q", rec.Header().Get("Location"))
	}
	if len(h.store.users) != 0 {
		t.Fatal("an unverified GitHub address must not create an account")
	}
}

// oauthDeletionHarness points the deletion dependencies at the same in-memory
// store the OAuth flow uses, so the F-8 round trip can be followed end to end
// without a database.
func oauthDeletionHarness(t *testing.T, h *harness) (*fakeDeletionStore, *[]sentMail, *[]uuid.UUID) {
	t.Helper()
	old := deletion
	store := &fakeDeletionStore{}
	mails := &[]sentMail{}
	invalidated := &[]uuid.UUID{}
	deletion = deletionDeps{
		store: store,
		getUser: func(id uuid.UUID) (*models.ApplicationUser, error) {
			return h.store.FindUserByID(id)
		},
		invalidateSession: func(id uuid.UUID) { *invalidated = append(*invalidated, id) },
		sendMail: func(to string, tmpl services.TemplateName, data services.MailData) error {
			*mails = append(*mails, sentMail{to, tmpl, data})
			return nil
		},
		runAsync:             func(f func()) { f() },
		now:                  func() time.Time { return h.now },
		providerConfirmation: beginProviderConfirmation,
	}
	t.Cleanup(func() { deletion = old })
	return store, mails, invalidated
}

// oauthOnlyUser is an account C10 creates: no password a user can type, and
// auth_provider naming the provider that owns it.
func oauthOnlyUser(t *testing.T, h *harness) *models.ApplicationUser {
	t.Helper()
	hash, err := services.GeneratePlaceholderPasswordHash()
	if err != nil {
		t.Fatalf("placeholder hash: %v", err)
	}
	verified := h.now
	u := &models.ApplicationUser{
		ID: uuid.New(), Email: "ada@example.com", DisplayName: "Ada", AuthProvider: "Google",
		PasswordHash: hash, EmailVerifiedAt: &verified,
	}
	h.store.users[u.ID] = u
	return u
}

// C11 F-8: an account created by a provider has no password to re-enter, so it
// confirms an irreversible deletion by re-authenticating the identity the account
// was built on. The POST deletes nothing: it hands back the provider round trip,
// and the round trip's callback — once the provider has authenticated the
// account's OWN address — schedules the deletion.
func TestAccountDelete_OAuthOnlyAccountConfirmsWithTheProvider(t *testing.T) {
	h := oauthSetup(t)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"1","email":"Ada@Example.com","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)
	store, mails, invalidated := oauthDeletionHarness(t, h)
	user := oauthOnlyUser(t, h)

	rec := deleteAccount(t, user, `{}`)
	if rec.Code != http.StatusConflict {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	var body DeletionConfirmationRequired
	if err := json.Unmarshal(rec.Body.Bytes(), &body); err != nil {
		t.Fatal(err)
	}
	if body.Error != "provider_confirmation_required" || body.Provider != "google" || body.ConfirmURL == "" {
		t.Fatalf("body %+v", body)
	}
	// Nothing irreversible may have happened yet.
	if len(store.requested) != 0 || len(*mails) != 0 || len(*invalidated) != 0 {
		t.Fatalf("nothing may be scheduled by the request: requested=%d mails=%d invalidated=%v",
			len(store.requested), len(*mails), *invalidated)
	}

	// The state cookie that carries the intent is set by THIS authenticated POST.
	// That is what makes a round trip on a plain navigation safe: no cross-site
	// request can plant the cookie (SameSite=Lax plus the JSON-only layer), and a
	// navigation that carries no intent is an ordinary sign-in.
	c := cookieMap(rec)[oauthStateCookie]
	if c == nil {
		t.Fatal("the confirmation round trip needs its state cookie")
	}
	parts := strings.SplitN(c.Value, ":", 5)
	if len(parts) != 5 || parts[0] != "google" || parts[3] != oauthIntentDelete || parts[4] != user.ID.String() {
		t.Fatalf("state cookie %q", c.Value)
	}

	u, err := url.Parse(body.ConfirmURL)
	if err != nil {
		t.Fatal(err)
	}
	state := u.Query().Get("state")
	if state == "" || state != parts[1] {
		t.Fatalf("confirm url %q does not carry the cookie's state %q", body.ConfirmURL, parts[1])
	}

	// The browser goes to the provider and comes back.
	rec2 := oauthCall(t, handleOAuthCallback, "google", "?code=the-code&state="+state, c.Value)
	// The purge date travels with the redirect so the sign-in page can name it.
	wantDate := url.QueryEscape(services.DeletionDate(h.now.UTC(), AccountDeletionGraceDays).Format(time.RFC3339))
	if got := rec2.Header().Get("Location"); got != "https://reelbolt.test/app/login?deletion=scheduled&date="+wantDate {
		t.Fatalf("location %q", got)
	}
	if len(store.requested) != 1 {
		t.Fatalf("RequestDeletion calls: %d", len(store.requested))
	}
	if len(*invalidated) != 1 || (*invalidated)[0] != user.ID {
		t.Fatalf("sessions not invalidated: %v", *invalidated)
	}
	if len(*mails) != 1 || (*mails)[0].tmpl != services.TemplateDeletionSchedule || (*mails)[0].to != user.Email {
		t.Fatalf("mails %+v", *mails)
	}
	dropped := map[string]bool{}
	for _, ck := range rec2.Result().Cookies() {
		if ck.MaxAge < 0 {
			dropped[ck.Name] = true
		}
	}
	if !dropped["reelbolt_token"] || !dropped["reelbolt_user"] {
		t.Fatalf("session cookies not cleared: %v", dropped)
	}
}

// The stolen-session case: a session alone must not be enough, and neither is a
// round trip that authenticates SOMEBODY ELSE. The intent is bound to one
// account, and the identity the provider returns has to be that account's own
// address.
func TestAccountDelete_ProviderConfirmationRefusesADifferentIdentity(t *testing.T) {
	h := oauthSetup(t)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"2","email":"mallory@example.com","email_verified":true}`
	installGoogle(t, f)
	store, mails, invalidated := oauthDeletionHarness(t, h)
	user := oauthOnlyUser(t, h)

	rec := deleteAccount(t, user, `{}`)
	if rec.Code != http.StatusConflict {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	c := cookieMap(rec)[oauthStateCookie]
	if c == nil {
		t.Fatal("no state cookie")
	}
	state := strings.SplitN(c.Value, ":", 5)[1]

	rec2 := oauthCall(t, handleOAuthCallback, "google", "?code=the-code&state="+state, c.Value)
	if got := rec2.Header().Get("Location"); !strings.Contains(got, "error=oauth_failed") {
		t.Fatalf("location %q", got)
	}
	if len(store.requested) != 0 || len(*mails) != 0 || len(*invalidated) != 0 {
		t.Fatal("an account must not be deleted on somebody else's sign-in")
	}
	if u, _ := h.store.FindUserByID(user.ID); u == nil {
		t.Fatal("the account was touched")
	}
}

// C9b: signing in with a provider during the grace period cancels a pending deletion, exactly as
// a password sign-in does — an account created by a provider has no other way to sign in — and the
// landing page is told so.
func TestOAuthCallback_SigningInCancelsAPendingDeletion(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"1","email":"ada@example.com","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)
	store, _, _ := oauthDeletionHarness(t, h)
	store.clearPending = true

	user := oauthOnlyUser(t, h)
	requested := h.now.Add(-48 * time.Hour)
	user.DeletionRequestedAt = &requested
	h.store.acceptances = append(h.store.acceptances,
		models.LegalAcceptance{ID: uuid.New(), UserID: user.ID, Document: models.LegalDocumentTerms, Version: "2026-10"},
		models.LegalAcceptance{ID: uuid.New(), UserID: user.ID, Document: models.LegalDocumentPrivacy, Version: "2026-10"},
	)

	rec := oauthCall(t, handleOAuthCallback, "google", "?code=the-code&state=st-9", "google:st-9:v")
	if got := rec.Header().Get("Location"); got != "https://reelbolt.test/app/dashboard?deletion=cancelled" {
		t.Fatalf("location %q", got)
	}
	if store.clearCalls != 1 {
		t.Fatalf("ClearDeletionRequest calls: %d", store.clearCalls)
	}
	if cookieMap(rec)["reelbolt_token"] == nil {
		t.Fatal("the sign-in must still issue a session")
	}
}

// A sign-in with nothing pending says nothing about a deletion.
func TestOAuthCallback_NoPendingDeletionLeavesTheLandingPageAlone(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"1","email":"ada@example.com","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)
	store, _, _ := oauthDeletionHarness(t, h)
	user := oauthOnlyUser(t, h)
	h.store.acceptances = append(h.store.acceptances,
		models.LegalAcceptance{ID: uuid.New(), UserID: user.ID, Document: models.LegalDocumentTerms, Version: "2026-10"},
	)

	rec := oauthCall(t, handleOAuthCallback, "google", "?code=the-code&state=st-10", "google:st-10:v")
	if got := rec.Header().Get("Location"); got != "https://reelbolt.test/app/dashboard" {
		t.Fatalf("location %q", got)
	}
	if store.clearCalls != 0 {
		t.Fatalf("ClearDeletionRequest must not be called with nothing pending: %d", store.clearCalls)
	}
}

// The password sign-in reports a cancelled deletion in its response, and only then.
func TestFinishPasswordLogin_ReportsACancelledDeletion(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)
	store, _, _ := oauthDeletionHarness(t, h)
	store.clearPending = true

	requested := h.now.Add(-time.Hour)
	user := &models.ApplicationUser{ID: uuid.New(), Email: "a@b.co", EmailVerifiedAt: ptrTime(h.now), DeletionRequestedAt: &requested}
	h.store.users[user.ID] = user

	req := httptest.NewRequestWithContext(context.Background(), "POST", "/api/v1/auth/token", nil)
	rec := httptest.NewRecorder()
	finishPasswordLogin(rec, req, user)
	if rec.Code != http.StatusOK {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	var resp TokenResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &resp); err != nil {
		t.Fatal(err)
	}
	if !resp.DeletionCancelled || store.clearCalls != 1 {
		t.Fatalf("deletionCancelled=%v clearCalls=%d", resp.DeletionCancelled, store.clearCalls)
	}

	clean := &models.ApplicationUser{ID: uuid.New(), Email: "c@d.co", EmailVerifiedAt: ptrTime(h.now)}
	h.store.users[clean.ID] = clean
	rec2 := httptest.NewRecorder()
	finishPasswordLogin(rec2, req, clean)
	if strings.Contains(rec2.Body.String(), "deletionCancelled") {
		t.Fatalf("an ordinary sign-in must not mention a deletion: %s", rec2.Body)
	}
}

func TestWriteSession_CarriesTermsVersionWhenThereIsOne(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)
	user := &models.ApplicationUser{ID: uuid.New(), Email: "a@b.co", EmailVerifiedAt: ptrTime(time.Now())}
	h.store.users[user.ID] = user
	h.store.acceptances = append(h.store.acceptances, models.LegalAcceptance{
		ID: uuid.New(), UserID: user.ID, Document: models.LegalDocumentTerms, Version: "2026-11",
	})

	m := &services.ActiveMembership{OrgID: uuid.New(), OrgName: "Personal", OrgKind: models.OrgKindPersonal, Role: models.OrgRoleOwner}
	rec := httptest.NewRecorder()
	writeSession(rec, user, m)

	var resp TokenResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &resp); err != nil {
		t.Fatal(err)
	}
	if resp.TermsVersion != "2026-11" {
		t.Fatalf("response termsVersion %q", resp.TermsVersion)
	}
	claims, err := services.ValidateToken(resp.AccessToken)
	if err != nil || claims.TermsVersion != "2026-11" {
		t.Fatalf("claims %+v err %v", claims, err)
	}
	raw, _ := url.QueryUnescape(cookieMap(rec)["reelbolt_user"].Value)
	if !strings.Contains(raw, `"termsVersion":"2026-11"`) {
		t.Fatalf("cookie %s", raw)
	}
}

func TestWriteSession_OmitsTermsVersionWhenNoneWasAccepted(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)
	user := &models.ApplicationUser{ID: uuid.New(), Email: "a@b.co"}
	h.store.users[user.ID] = user

	rec := httptest.NewRecorder()
	writeSession(rec, user, &services.ActiveMembership{OrgID: uuid.New(), Role: models.OrgRoleOwner})

	raw, _ := url.QueryUnescape(cookieMap(rec)["reelbolt_user"].Value)
	if strings.Contains(raw, "termsVersion") {
		t.Fatalf("an absent version must be omitted, not empty: %s", raw)
	}
	var resp TokenResponse
	_ = json.Unmarshal(rec.Body.Bytes(), &resp)
	if resp.TermsVersion != "" {
		t.Fatalf("response %q", resp.TermsVersion)
	}
}

func ptrTime(t time.Time) *time.Time { return &t }

// The middleware context the accept-terms handler reads.
func withUserContext(req *http.Request, id uuid.UUID) *http.Request {
	return req.WithContext(context.WithValue(req.Context(), middleware.UserContextKey,
		middleware.UserContext{UserID: id.String(), Email: "a@b.co"}))
}

// Google and GitHub are another way into the same accounts, not a way around an invite-only
// deployment: with signup off, a provider sign-in for an address with no account creates nothing
// and says why.
func TestOAuthCallback_SignupClosedCreatesNoAccount(t *testing.T) {
	h := oauthSetup(t)
	config.Cfg.SignupEnabled = false
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"321","email":"new@example.com","email_verified":true,"name":"New Person"}`
	installGoogle(t, f)

	rec := oauthCall(t, handleOAuthCallback, "google",
		"?code=the-code&state=st-closed", "google:st-closed:verifier-closed")
	if rec.Code != http.StatusFound {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if got := rec.Header().Get("Location"); got != "https://reelbolt.test/app/login?error=oauth_no_account" {
		t.Fatalf("location %q", got)
	}
	if u, _ := h.store.FindUserByEmail("new@example.com"); u != nil {
		t.Fatal("an account was created while signup is closed")
	}
	if cookies := cookieMap(rec); cookies["reelbolt_token"] != nil {
		t.Fatal("a session was issued for an account that does not exist")
	}
}

// With signup off, an address that already has a verified account still signs in.
func TestOAuthCallback_SignupClosedStillSignsInAnExistingAccount(t *testing.T) {
	h := oauthSetup(t)
	config.Cfg.SignupEnabled = false
	verifiedAt := h.now.Add(-time.Hour)
	id := uuid.New()
	h.store.users[id] = &models.ApplicationUser{
		ID: id, Email: "member@example.com", DisplayName: "Member", AuthProvider: "Password",
		PasswordHash: "the hash the owner chose", EmailVerifiedAt: &verifiedAt,
	}
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"654","email":"member@example.com","email_verified":true,"name":"Member"}`
	installGoogle(t, f)

	rec := oauthCall(t, handleOAuthCallback, "google",
		"?code=the-code&state=st-open", "google:st-open:verifier-open")
	if rec.Code != http.StatusFound {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if loc := rec.Header().Get("Location"); strings.Contains(loc, "error=") {
		t.Fatalf("an existing verified account was refused: %q", loc)
	}
	if cookieMap(rec)["reelbolt_token"] == nil {
		t.Fatal("no session for the existing account")
	}
}

func TestOAuthProviders_ListsOnlyConfiguredProvidersAndTheSignupRule(t *testing.T) {
	read := func() map[string]any {
		t.Helper()
		rec := httptest.NewRecorder()
		handleOAuthProviders(rec, httptest.NewRequestWithContext(context.Background(), "GET", "/api/v1/auth/oauth/providers", nil))
		if rec.Code != http.StatusOK {
			t.Fatalf("got %d", rec.Code)
		}
		var body map[string]any
		if err := json.Unmarshal(rec.Body.Bytes(), &body); err != nil {
			t.Fatal(err)
		}
		return body
	}

	setup(t, false)
	if got := read()["providers"].([]any); len(got) != 0 {
		t.Fatalf("no credentials, yet providers %v", got)
	}

	oauthSetup(t)
	config.Cfg.OAuthGitHubClientSecret = "" // half-configured: not offered
	body := read()
	if got := body["providers"].([]any); len(got) != 1 || got[0] != "google" {
		t.Fatalf("providers %v", got)
	}
	if body["createsAccount"] != true {
		t.Fatalf("createsAccount %v with signup on", body["createsAccount"])
	}
	config.Cfg.SignupEnabled = false
	if read()["createsAccount"] != false {
		t.Fatal("createsAccount must follow SIGNUP_ENABLED")
	}
}

// funnel first-film fixes: a successful sign-in emits auth.login with a closed method token and
// nothing personal; a failed sign-in emits nothing.
func captureLoginEvents(t *testing.T) *[]string {
	t.Helper()
	var got []string
	prev := recordLoginProductEvent
	recordLoginProductEvent = func(_ uuid.UUID, detail string) { got = append(got, detail) }
	t.Cleanup(func() { recordLoginProductEvent = prev })
	return &got
}

func TestFinishPasswordLogin_EmitsAuthLoginPassword(t *testing.T) {
	h := oauthSetup(t)
	useFakeTermsVersion(t, h.store)
	got := captureLoginEvents(t)
	user := &models.ApplicationUser{ID: uuid.New(), Email: "a@b.co", EmailVerifiedAt: ptrTime(h.now)}
	h.store.users[user.ID] = user

	req := httptest.NewRequestWithContext(context.Background(), "POST", "/api/v1/auth/token", nil)
	finishPasswordLogin(httptest.NewRecorder(), req, user)
	if len(*got) != 1 || (*got)[0] != "password" {
		t.Fatalf("auth.login details = %v, want [password]", *got)
	}
}

func TestOAuthCallback_EmitsAuthLoginWithProvider(t *testing.T) {
	h := oauthSetup(t)
	_ = h
	got := captureLoginEvents(t)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"123","email":"Ada@Example.COM","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)
	rec := oauthCall(t, handleOAuthCallback, "google", "?code=the-code&state=st-1", "google:st-1:verifier-abc")
	if rec.Code != http.StatusFound {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if len(*got) != 1 || (*got)[0] != "google" {
		t.Fatalf("auth.login details = %v, want [google]", *got)
	}
}

func TestOAuthCallback_RefusedSignInEmitsNoAuthLogin(t *testing.T) {
	_ = oauthSetup(t)
	got := captureLoginEvents(t)
	rec := oauthCall(t, handleOAuthCallback, "google", "?code=c&state=wrong", "google:st-1:v")
	if rec.Code != http.StatusFound {
		t.Fatalf("got %d", rec.Code)
	}
	if len(*got) != 0 {
		t.Fatalf("a refused sign-in must emit nothing, got %v", *got)
	}
}
