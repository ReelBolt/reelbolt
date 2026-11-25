package oauth

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"html"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"reflect"
	"regexp"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

// RFC 7636 Appendix B, so every PKCE assertion in the flow tests is against the specification's own
// vector rather than against this package's idea of the formula.
const (
	testVerifier  = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
	testChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"

	testClientSecret = "a-confidential-client-secret"
	testRedirectURI  = "https://claude.example/callback"
)

// ---------------------------------------------------------------------------
// Fakes
// ---------------------------------------------------------------------------

type fakeStore struct {
	clients map[string]*models.OAuthClient
	codes   map[string]*models.OAuthAuthorizationCode
	refresh map[string]*models.OAuthRefreshToken
}

func newFakeStore() *fakeStore {
	return &fakeStore{
		clients: map[string]*models.OAuthClient{},
		codes:   map[string]*models.OAuthAuthorizationCode{},
		refresh: map[string]*models.OAuthRefreshToken{},
	}
}

func (f *fakeStore) CreateClient(_ context.Context, c *models.OAuthClient) error {
	f.clients[c.ClientID] = c
	return nil
}

func (f *fakeStore) FindClientByClientID(_ context.Context, id string) (*models.OAuthClient, error) {
	return f.clients[id], nil
}

func (f *fakeStore) TouchClientUsed(_ context.Context, id string, at time.Time) error {
	if c := f.clients[id]; c != nil {
		c.LastUsedAt = &at
	}
	return nil
}

func (f *fakeStore) CreateCode(_ context.Context, c *models.OAuthAuthorizationCode) error {
	f.codes[c.CodeHash] = c
	return nil
}

func (f *fakeStore) FindCodeByHash(_ context.Context, hash string) (*models.OAuthAuthorizationCode, error) {
	return f.codes[hash], nil
}

func (f *fakeStore) ConsumeCode(_ context.Context, id uuid.UUID, at time.Time) (bool, error) {
	for _, c := range f.codes {
		if c.ID != id {
			continue
		}
		if c.ConsumedAt != nil {
			return false, nil
		}
		consumed := at
		c.ConsumedAt = &consumed
		return true, nil
	}
	return false, nil
}

func (f *fakeStore) CreateRefreshToken(_ context.Context, t *models.OAuthRefreshToken) error {
	f.refresh[t.TokenHash] = t
	return nil
}

func (f *fakeStore) FindRefreshTokenByHash(_ context.Context, hash string) (*models.OAuthRefreshToken, error) {
	return f.refresh[hash], nil
}

func (f *fakeStore) RotateRefreshToken(_ context.Context, id uuid.UUID, at time.Time) (bool, error) {
	for _, r := range f.refresh {
		if r.ID != id {
			continue
		}
		if r.RevokedAt != nil {
			return false, nil
		}
		revoked := at
		r.RevokedAt = &revoked
		return true, nil
	}
	return false, nil
}

func (f *fakeStore) RevokeGrant(_ context.Context, clientID string, userID uuid.UUID, at time.Time) error {
	for _, r := range f.refresh {
		if r.ClientID == clientID && r.UserID == userID && r.RevokedAt == nil {
			revoked := at
			r.RevokedAt = &revoked
		}
	}
	return nil
}

// ---------------------------------------------------------------------------
// Harness
// ---------------------------------------------------------------------------

type harness struct {
	store *fakeStore
	user  *models.ApplicationUser
	orgID uuid.UUID

	// noMembership makes the membership lookup answer "not a member". It is how the consent page's
	// degraded path (a workspace whose name cannot be read) is exercised: the org id still comes from
	// the verified session, so only the NAMING is unavailable.
	noMembership bool

	now time.Time
}

// newHarness installs the fakes and a fixed clock. The config is restored on cleanup, so a test can
// never leak an origin or signing key into another one.
func newHarness(t *testing.T) *harness {
	t.Helper()

	oldCfg, oldDeps := config.Cfg, oauthDeps
	t.Cleanup(func() {
		config.Cfg = oldCfg
		oauthDeps = oldDeps
	})

	config.Cfg.PublicBaseURL = "https://reelbolt.test"
	config.Cfg.OAuthIssuer = "https://reelbolt.test"
	config.Cfg.JWTIssuer = "reelbolt-api"
	config.Cfg.JWTAudience = "reelbolt-inference"
	config.Cfg.JWTSigningKey = strings.Repeat("k", 32)
	config.Cfg.CookieSecure = true

	verified := time.Date(2026, 10, 1, 12, 0, 0, 0, time.UTC)
	h := &harness{
		store: newFakeStore(),
		user: &models.ApplicationUser{
			ID:              uuid.New(),
			Email:           "ada@example.com",
			DisplayName:     "Ada",
			EmailVerifiedAt: &verified,
		},
		orgID: uuid.New(),
		// The clock starts at the REAL current time, not a hardcoded date. These tests mint real
		// ReelBolt JWTs and then check them with services.ValidateToken, which reads the real clock, so
		// a fixed base date makes every one of those assertions fail once the token's lifetime has
		// elapsed since that date: with the previous fixed 2026-10-11T09:00Z base this suite went red
		// at 10:00 UTC that day and could never go green again. Tests still advance the clock
		// relatively (h.now = h.now.Add(...)), so expiry behaviour is unchanged.
		now: time.Now().UTC().Truncate(time.Second),
	}

	oauthDeps = deps{
		store: h.store,
		getUser: func(id uuid.UUID) (*models.ApplicationUser, error) {
			if id != h.user.ID {
				return nil, nil
			}
			return h.user, nil
		},
		membership:     h,
		termsVersion:   func(uuid.UUID) (string, error) { return "2026-01-01", nil },
		privacyVersion: func(uuid.UUID) (string, error) { return "2026-01-02", nil },
		now:            func() time.Time { return h.now },
		origin:         func() string { return config.Cfg.OAuthIssuer },
	}
	return h
}

// GetMembership makes the harness the MembershipLookup: the one user is a member of the one org.
func (h *harness) GetMembership(_ context.Context, userID, orgID uuid.UUID) (*services.ActiveMembership, error) {
	if h.noMembership {
		return nil, nil
	}
	if userID != h.user.ID || orgID != h.orgID {
		return nil, nil
	}
	return &services.ActiveMembership{
		OrgID:   orgID,
		OrgName: "Ada's workspace",
		OrgKind: "Personal",
		Role:    "Owner",
	}, nil
}

func (h *harness) userContext() middleware.UserContext {
	return middleware.UserContext{
		UserID:         h.user.ID.String(),
		Email:          h.user.Email,
		OrganizationID: h.orgID,
		OrgRole:        "Owner",
	}
}

func (h *harness) registerClient(method string, redirectURIs ...string) *models.OAuthClient {
	if len(redirectURIs) == 0 {
		redirectURIs = []string{testRedirectURI}
	}
	client := &models.OAuthClient{
		ID:                      uuid.New(),
		ClientID:                "client-" + uuid.NewString(),
		ClientName:              "Claude Code",
		RedirectURIs:            models.StringList(redirectURIs),
		GrantTypes:              models.StringList{"authorization_code", "refresh_token"},
		ResponseTypes:           models.StringList{"code"},
		TokenEndpointAuthMethod: method,
		CreatedAt:               h.now,
	}
	if method == "client_secret_post" {
		hash := services.HashOAuthSecret(testClientSecret)
		client.ClientSecretHash = &hash
	}
	h.store.clients[client.ClientID] = client
	return client
}

// request builds a request with the signed-in user already in its context, which is what the auth
// middleware would have done. Form bodies are urlencoded unless a content type says otherwise.
func (h *harness) request(method, target string, form url.Values, contentType string) *http.Request {
	var body io.Reader
	if form != nil {
		body = strings.NewReader(form.Encode())
	}
	req := httptest.NewRequestWithContext(context.Background(), method, target, body)
	if contentType == "" && form != nil {
		contentType = "application/x-www-form-urlencoded"
	}
	if contentType != "" {
		req.Header.Set("Content-Type", contentType)
	}
	ctx := context.WithValue(req.Context(), middleware.UserContextKey, h.userContext())
	return req.WithContext(ctx)
}

// anonymous is the same helper without a session, for the authorize fallback and the register/token
// endpoints (which are not cookie-authenticated at all).
func (h *harness) anonymous(method, target string, form url.Values, contentType string) *http.Request {
	var body io.Reader
	if form != nil {
		body = strings.NewReader(form.Encode())
	}
	req := httptest.NewRequestWithContext(context.Background(), method, target, body)
	if contentType == "" && form != nil {
		contentType = "application/x-www-form-urlencoded"
	}
	if contentType != "" {
		req.Header.Set("Content-Type", contentType)
	}
	return req
}

var consentValuePattern = regexp.MustCompile(`name="consent" value="([^"]+)"`)

func consentFromPage(t *testing.T, body string) string {
	t.Helper()
	m := consentValuePattern.FindStringSubmatch(body)
	if m == nil {
		t.Fatalf("the consent page carried no sealed consent value:\n%s", body)
	}
	return m[1]
}

// authorizeAndGetCode runs the full browser leg: GET /oauth/authorize to render the page, then POST
// the consent the page carried. It returns the authorization code the browser would have been
// redirected with. Every scope/consent test goes through this path, so nothing about the flow is
// simulated by calling sealConsent directly.
func (h *harness) authorizeAndGetCode(t *testing.T, client *models.OAuthClient, redirectURI, scope string) string {
	t.Helper()

	q := url.Values{
		"client_id":             {client.ClientID},
		"redirect_uri":          {redirectURI},
		"response_type":         {"code"},
		"scope":                 {scope},
		"state":                 {"state-123"},
		"code_challenge":        {testChallenge},
		"code_challenge_method": {"S256"},
	}

	rec := httptest.NewRecorder()
	handleAuthorize(rec, h.request(http.MethodGet, "/oauth/authorize?"+q.Encode(), nil, ""))
	if rec.Code != http.StatusOK {
		t.Fatalf("authorize GET status = %d, want 200; body: %s", rec.Code, rec.Body.String())
	}
	sealed := consentFromPage(t, rec.Body.String())

	rec = httptest.NewRecorder()
	handleAuthorizeDecision(rec, h.request(http.MethodPost, "/oauth/authorize",
		url.Values{"consent": {sealed}, "decision": {"allow"}}, ""))

	if rec.Code != http.StatusFound {
		t.Fatalf("authorize POST status = %d, want 302; body: %s", rec.Code, rec.Body.String())
	}
	location := rec.Header().Get("Location")
	code := queryParam(t, location, "code")
	if code == "" {
		t.Fatalf("no code in redirect %q", location)
	}
	return code
}

func queryParam(t *testing.T, rawURL, key string) string {
	t.Helper()
	u, err := url.Parse(rawURL)
	if err != nil {
		t.Fatalf("parse %q: %v", rawURL, err)
	}
	return u.Query().Get(key)
}

func tokenForm(client *models.OAuthClient, extra url.Values) url.Values {
	form := url.Values{
		"grant_type":    {"authorization_code"},
		"client_id":     {client.ClientID},
		"code":          {""},
		"redirect_uri":  {testRedirectURI},
		"code_verifier": {testVerifier},
	}
	for k, v := range extra {
		form[k] = v
	}
	return form
}

func decodeTokenResponse(t *testing.T, body string) tokenResponse {
	t.Helper()
	var resp tokenResponse
	if err := json.Unmarshal([]byte(body), &resp); err != nil {
		t.Fatalf("decode token response %q: %v", body, err)
	}
	return resp
}

func decodeOAuthError(t *testing.T, body string) string {
	t.Helper()
	var payload map[string]string
	if err := json.Unmarshal([]byte(body), &payload); err != nil {
		t.Fatalf("decode error response %q: %v", body, err)
	}
	return payload["error"]
}

// ---------------------------------------------------------------------------
// Metadata (contract §4)
// ---------------------------------------------------------------------------

func TestMetadataDocumentMatchesTheContract(t *testing.T) {
	newHarness(t)

	rec := httptest.NewRecorder()
	handleMetadata(rec, httptest.NewRequestWithContext(context.Background(), http.MethodGet, "/.well-known/oauth-authorization-server", nil))

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rec.Code)
	}

	var got map[string]any
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatalf("decode metadata: %v", err)
	}

	// Exactly the contract's document, with no extra key: RFC 8414 readers tolerate extensions, but
	// this document is an interface with the MCP client and the bridge, so an added field is a change
	// someone must make deliberately.
	want := map[string]any{
		"issuer":                                "https://reelbolt.test",
		"authorization_endpoint":                "https://reelbolt.test/oauth/authorize",
		"token_endpoint":                        "https://reelbolt.test/oauth/token",
		"registration_endpoint":                 "https://reelbolt.test/oauth/register",
		"revocation_endpoint":                   "https://reelbolt.test/oauth/revoke",
		"scopes_supported":                      []any{"assistant.tools", "assistant.admin", "runs.watch"},
		"response_types_supported":              []any{"code"},
		"grant_types_supported":                 []any{"authorization_code", "refresh_token"},
		"code_challenge_methods_supported":      []any{"S256"},
		"token_endpoint_auth_methods_supported": []any{"none", "client_secret_post"},
	}

	if !reflect.DeepEqual(got, want) {
		t.Fatalf("metadata document mismatch\n got: %#v\nwant: %#v", got, want)
	}
	if len(got) != len(want) {
		t.Fatalf("metadata has %d keys, want %d", len(got), len(want))
	}
}

func TestMetadataUsesTheConfiguredOrigin(t *testing.T) {
	newHarness(t)
	config.Cfg.OAuthIssuer = "https://api.reelbolt.example"

	rec := httptest.NewRecorder()
	handleMetadata(rec, httptest.NewRequestWithContext(context.Background(), http.MethodGet, "/.well-known/oauth-authorization-server", nil))

	var got metadataDocument
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatalf("decode metadata: %v", err)
	}
	if got.Issuer != "https://api.reelbolt.example" || got.TokenEndpoint != "https://api.reelbolt.example/oauth/token" {
		t.Fatalf("the document must follow config, got issuer %q token %q", got.Issuer, got.TokenEndpoint)
	}
}

// ---------------------------------------------------------------------------
// Registration (RFC 7591)
// ---------------------------------------------------------------------------

func TestRegisterTable(t *testing.T) {
	tests := []struct {
		name       string
		body       string
		wantStatus int
		wantError  string
		// assertions on the successful shape
		wantSecret bool
		wantMethod string
	}{
		{
			name:       "public client with one https redirect URI",
			body:       `{"client_name":"Claude Code","redirect_uris":["https://claude.example/callback"]}`,
			wantStatus: http.StatusCreated,
			wantMethod: "none",
		},
		{
			name:       "public client with a loopback redirect URI",
			body:       `{"client_name":"CLI","redirect_uris":["http://127.0.0.1:7654/callback","http://localhost:9000/cb"]}`,
			wantStatus: http.StatusCreated,
			wantMethod: "none",
		},
		{
			name:       "confidential client gets a secret",
			body:       `{"client_name":"Server","redirect_uris":["https://claude.example/callback"],"token_endpoint_auth_method":"client_secret_post"}`,
			wantStatus: http.StatusCreated,
			wantSecret: true,
			wantMethod: "client_secret_post",
		},
		{
			name:       "no redirect URI is refused",
			body:       `{"client_name":"Claude Code","redirect_uris":[]}`,
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_redirect_uri",
		},
		{
			name:       "redirect_uris absent is refused",
			body:       `{"client_name":"Claude Code"}`,
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_redirect_uri",
		},
		{
			name:       "relative redirect URI is refused",
			body:       `{"redirect_uris":["/callback"]}`,
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_redirect_uri",
		},
		{
			name:       "plain http on a public host is refused",
			body:       `{"redirect_uris":["http://claude.example/callback"]}`,
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_redirect_uri",
		},
		{
			name:       "a redirect URI with a fragment is refused",
			body:       `{"redirect_uris":["https://claude.example/callback#frag"]}`,
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_redirect_uri",
		},
		{
			name:       "unsupported grant type is refused",
			body:       `{"redirect_uris":["https://claude.example/callback"],"grant_types":["client_credentials"]}`,
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_client_metadata",
		},
		{
			name:       "a registration without authorization_code is refused",
			body:       `{"redirect_uris":["https://claude.example/callback"],"grant_types":["refresh_token"]}`,
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_client_metadata",
		},
		{
			name:       "unsupported response type is refused",
			body:       `{"redirect_uris":["https://claude.example/callback"],"response_types":["token"]}`,
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_client_metadata",
		},
		{
			name:       "unsupported token endpoint auth method is refused",
			body:       `{"redirect_uris":["https://claude.example/callback"],"token_endpoint_auth_method":"client_secret_basic"}`,
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_client_metadata",
		},
		{
			name:       "a malformed body is refused",
			body:       `not json`,
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_client_metadata",
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			h := newHarness(t)

			rec := httptest.NewRecorder()
			handleRegister(rec, h.rawJSONRequest(http.MethodPost, "/oauth/register", tc.body))

			if rec.Code != tc.wantStatus {
				t.Fatalf("status = %d, want %d; body: %s", rec.Code, tc.wantStatus, rec.Body.String())
			}

			if tc.wantError != "" {
				if got := decodeOAuthError(t, rec.Body.String()); got != tc.wantError {
					t.Fatalf("error = %q, want %q", got, tc.wantError)
				}
				if len(h.store.clients) != 0 {
					t.Fatalf("a refused registration must persist nothing, got %d clients", len(h.store.clients))
				}
				return
			}

			var resp registerResponse
			if err := json.Unmarshal(rec.Body.Bytes(), &resp); err != nil {
				t.Fatalf("decode register response: %v", err)
			}
			if resp.ClientID == "" {
				t.Fatal("a created client must have a client_id")
			}
			if resp.ClientIDIssuedAt == 0 {
				t.Fatal("client_id_issued_at must be set")
			}
			if resp.TokenEndpointAuthMethod != tc.wantMethod {
				t.Fatalf("token_endpoint_auth_method = %q, want %q", resp.TokenEndpointAuthMethod, tc.wantMethod)
			}
			if (resp.ClientSecret != "") != tc.wantSecret {
				t.Fatalf("client_secret present = %v, want %v", resp.ClientSecret != "", tc.wantSecret)
			}

			stored := h.store.clients[resp.ClientID]
			if stored == nil {
				t.Fatal("the client was not persisted")
			}
			if stored.ClientSecretHash != nil && *stored.ClientSecretHash == resp.ClientSecret {
				t.Fatal("the secret must be stored hashed, never in the clear")
			}
			if len(resp.RedirectURIs) == 0 {
				t.Fatal("redirect_uris must be echoed")
			}
		})
	}
}

// rawJSONRequest builds a request whose body is the exact JSON the test supplied.
func (h *harness) rawJSONRequest(method, target, body string) *http.Request {
	req := httptest.NewRequestWithContext(context.Background(), method, target, strings.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	return req
}

// ---------------------------------------------------------------------------
// Authorize: parameter validation
// ---------------------------------------------------------------------------

func TestAuthorizeRefusesUnknownClientWithoutRedirecting(t *testing.T) {
	h := newHarness(t)

	q := url.Values{
		"client_id":             {"no-such-client"},
		"redirect_uri":          {testRedirectURI},
		"response_type":         {"code"},
		"code_challenge":        {testChallenge},
		"code_challenge_method": {"S256"},
		"state":                 {"s"},
	}

	rec := httptest.NewRecorder()
	handleAuthorize(rec, h.request(http.MethodGet, "/oauth/authorize?"+q.Encode(), nil, ""))

	if rec.Code != http.StatusBadRequest {
		t.Fatalf("status = %d, want 400", rec.Code)
	}
	if loc := rec.Header().Get("Location"); loc != "" {
		t.Fatalf("an unknown client must not be redirected anywhere, got %q", loc)
	}
}

func TestAuthorizeRefusesMismatchedRedirectURIWithoutRedirecting(t *testing.T) {
	h := newHarness(t)
	client := h.registerClient("none", testRedirectURI)

	q := url.Values{
		"client_id":             {client.ClientID},
		"redirect_uri":          {"https://attacker.example/callback"},
		"response_type":         {"code"},
		"code_challenge":        {testChallenge},
		"code_challenge_method": {"S256"},
	}

	rec := httptest.NewRecorder()
	handleAuthorize(rec, h.request(http.MethodGet, "/oauth/authorize?"+q.Encode(), nil, ""))

	if rec.Code != http.StatusBadRequest {
		t.Fatalf("status = %d, want 400", rec.Code)
	}
	if loc := rec.Header().Get("Location"); loc != "" {
		t.Fatalf("a mismatched redirect_uri must not be redirected to, got %q", loc)
	}
}

func TestAuthorizeRedirectURIMustMatchExactly(t *testing.T) {
	h := newHarness(t)
	client := h.registerClient("none", "https://claude.example/callback")

	// Every one of these is a prefix, a suffix, a case variant or a host-suffix lookalike of the one
	// registered URI. None may be accepted.
	nearMisses := []string{
		"https://claude.example/callback/",
		"https://claude.example/callback?extra=1",
		"https://claude.example/Callback",
		"https://claude.example",
		"https://claude.example/callback/../callback",
		"https://claude.example.evil.test/callback",
		"https://claude.example/callback#frag",
		"//claude.example/callback",
		"https://claude.example:443/callback",
	}

	for _, candidate := range nearMisses {
		t.Run(candidate, func(t *testing.T) {
			if clientAllowsRedirectURI(client, candidate) {
				t.Fatalf("%q must not match the registered %q — matching is exact equality", candidate, "https://claude.example/callback")
			}
		})
	}

	if !clientAllowsRedirectURI(client, "https://claude.example/callback") {
		t.Fatal("the exact registered URI must match")
	}
}

func TestAuthorizePKCERequirementsTable(t *testing.T) {
	tests := []struct {
		name          string
		challenge     string
		method        string
		omitChallenge bool
		responseType  string
		wantError     string
		wantStatus    int
	}{
		{name: "valid", challenge: testChallenge, method: "S256", responseType: "code", wantStatus: http.StatusOK},
		{name: "missing challenge", omitChallenge: true, method: "S256", responseType: "code", wantError: "invalid_request"},
		{name: "plain method is refused", challenge: testChallenge, method: "plain", responseType: "code", wantError: "invalid_request"},
		{name: "missing method is refused", challenge: testChallenge, method: "", responseType: "code", wantError: "invalid_request"},
		{name: "short challenge is refused", challenge: "abc", method: "S256", responseType: "code", wantError: "invalid_request"},
		{name: "unsupported response type", challenge: testChallenge, method: "S256", responseType: "token", wantError: "unsupported_response_type"},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			h := newHarness(t)
			client := h.registerClient("none")

			q := url.Values{
				"client_id":             {client.ClientID},
				"redirect_uri":          {testRedirectURI},
				"response_type":         {tc.responseType},
				"state":                 {"state-xyz"},
				"code_challenge_method": {tc.method},
			}
			if !tc.omitChallenge {
				q.Set("code_challenge", tc.challenge)
			}

			rec := httptest.NewRecorder()
			handleAuthorize(rec, h.request(http.MethodGet, "/oauth/authorize?"+q.Encode(), nil, ""))

			if tc.wantStatus == http.StatusOK {
				if rec.Code != http.StatusOK {
					t.Fatalf("status = %d, want 200; body: %s", rec.Code, rec.Body.String())
				}
				if !strings.Contains(rec.Body.String(), client.ClientName) {
					t.Fatal("the consent page must name the client")
				}
				return
			}

			if rec.Code != http.StatusBadRequest {
				t.Fatalf("status = %d, want 400 refusal page (no redirect before consent)", rec.Code)
			}
			if loc := rec.Header().Get("Location"); loc != "" {
				t.Fatalf("a pre-consent error must not redirect, got Location %q", loc)
			}
			if len(h.store.codes) != 0 {
				t.Fatal("a refused authorization must issue no code")
			}
		})
	}
}

// ---------------------------------------------------------------------------
// Scopes
// ---------------------------------------------------------------------------

func TestAuthorizeGrantsAdminOnlyWhenTheSettingIsOn(t *testing.T) {
	tests := []struct {
		name             string
		adminEnabled     bool
		requestedScope   string
		wantGranted      string
		wantAdminVisible bool
	}{
		{
			name:           "setting off, admin requested",
			adminEnabled:   false,
			requestedScope: "assistant.tools assistant.admin",
			wantGranted:    "assistant.tools",
		},
		{
			name:             "setting on, admin requested",
			adminEnabled:     true,
			requestedScope:   "assistant.tools assistant.admin",
			wantGranted:      "assistant.tools assistant.admin",
			wantAdminVisible: true,
		},
		{
			name:           "setting on, admin not requested",
			adminEnabled:   true,
			requestedScope: "assistant.tools",
			wantGranted:    "assistant.tools",
		},
		{
			name:           "unknown scopes are dropped",
			adminEnabled:   true,
			requestedScope: "assistant.tools openid email profile",
			wantGranted:    "assistant.tools",
		},
		{
			name:           "no scope at all still grants tools",
			adminEnabled:   false,
			requestedScope: "",
			wantGranted:    "assistant.tools",
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			h := newHarness(t)
			h.user.McpAdminToolsEnabled = tc.adminEnabled
			client := h.registerClient("none")

			q := url.Values{
				"client_id":             {client.ClientID},
				"redirect_uri":          {testRedirectURI},
				"response_type":         {"code"},
				"scope":                 {tc.requestedScope},
				"code_challenge":        {testChallenge},
				"code_challenge_method": {"S256"},
			}

			rec := httptest.NewRecorder()
			handleAuthorize(rec, h.request(http.MethodGet, "/oauth/authorize?"+q.Encode(), nil, ""))
			if rec.Code != http.StatusOK {
				t.Fatalf("status = %d, want 200", rec.Code)
			}

			sealed := consentFromPage(t, rec.Body.String())
			req, ok := openConsent(sealed, h.now)
			if !ok {
				t.Fatal("the page's consent handle must verify")
			}
			if req.GrantedScope != tc.wantGranted {
				t.Fatalf("granted scope = %q, want %q", req.GrantedScope, tc.wantGranted)
			}

			body := rec.Body.String()
			if tc.wantAdminVisible {
				if !strings.Contains(body, services.OAuthScopeAssistantAdmin) {
					t.Fatal("the page must name assistant.admin when it is being granted")
				}
				if !strings.Contains(body, "administrative access") {
					t.Fatal("the page must say plainly that admin access is included")
				}
			} else if strings.Contains(body, services.OAuthScopeAssistantAdmin) {
				// Deliberately markup-independent: when the scope is not granted it must appear NOWHERE
				// on the page, and the "also asked for" notice names administrative access in words
				// rather than by its scope string.
				t.Fatal("the page must not present assistant.admin as granted when it is not")
			}
		})
	}
}

// The consent page must say WHICH workspace is being connected. The token it produces is scoped to
// exactly one organization, so someone who belongs to more than one has to be able to see that they
// are approving the right one (docs/design/mcp-oauth.md §3).
func TestConsentPageNamesTheWorkspaceBeingAuthorized(t *testing.T) {
	h := newHarness(t)
	client := h.registerClient("none")

	rec := httptest.NewRecorder()
	handleAuthorize(rec, h.request(http.MethodGet, "/oauth/authorize?"+authorizeQuery(client), nil, ""))
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rec.Code)
	}

	// Unescaped on purpose: html/template writes an apostrophe as &#39;, and these assertions are about
	// the text a person reads, not about the escaping that carries it.
	body := html.UnescapeString(rec.Body.String())
	if !strings.Contains(body, "Ada's workspace") {
		t.Fatal("the consent page must name the workspace the token will be scoped to")
	}
	if !strings.Contains(body, "Personal workspace") {
		t.Fatal("the consent page must say what kind of workspace it is")
	}
	if !strings.Contains(body, "Owner") {
		t.Fatal("the consent page must name the caller's role in that workspace")
	}
	if !strings.Contains(body, h.user.Email) {
		t.Fatal("the consent page must name the account it is authorizing as")
	}
}

// When the workspace cannot be read the page must not invent one: it degrades to a neutral label and
// an explicitly unknown role. The org id still comes from the verified session, so this is a naming
// gap, never an authorization one.
func TestConsentPageDegradesHonestlyWhenTheWorkspaceCannotBeRead(t *testing.T) {
	h := newHarness(t)
	// The session still names an organization, but the lookup cannot resolve it.
	h.noMembership = true
	client := h.registerClient("none")

	rec := httptest.NewRecorder()
	handleAuthorize(rec, h.request(http.MethodGet, "/oauth/authorize?"+authorizeQuery(client), nil, ""))
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200 (an unreadable name must not block consent)", rec.Code)
	}

	body := html.UnescapeString(rec.Body.String())
	if !strings.Contains(body, "Your workspace") {
		t.Fatal("an unreadable workspace must still render, with a neutral label")
	}
	if strings.Contains(body, "Ada's workspace") {
		t.Fatal("the page must not claim a workspace name it could not read")
	}
	if !strings.Contains(body, "unknown") {
		t.Fatal("the page must say the role is unknown rather than showing an empty role")
	}
}

// authorizeQuery is the minimum valid authorization request, used by the consent-page tests.
func authorizeQuery(client *models.OAuthClient) string {
	return url.Values{
		"client_id":             {client.ClientID},
		"redirect_uri":          {testRedirectURI},
		"response_type":         {"code"},
		"scope":                 {services.OAuthScopeAssistantTools},
		"code_challenge":        {testChallenge},
		"code_challenge_method": {"S256"},
	}.Encode()
}

func TestConsentCanOnlyEverShrinkWhatWasShown(t *testing.T) {
	h := newHarness(t)
	h.user.McpAdminToolsEnabled = true
	client := h.registerClient("none")

	// The page is rendered with admin on, so it shows (and seals) admin.
	q := url.Values{
		"client_id":             {client.ClientID},
		"redirect_uri":          {testRedirectURI},
		"response_type":         {"code"},
		"scope":                 {"assistant.tools assistant.admin"},
		"code_challenge":        {testChallenge},
		"code_challenge_method": {"S256"},
	}
	rec := httptest.NewRecorder()
	handleAuthorize(rec, h.request(http.MethodGet, "/oauth/authorize?"+q.Encode(), nil, ""))
	sealed := consentFromPage(t, rec.Body.String())
	if req, ok := openConsent(sealed, h.now); !ok || req.GrantedScope != "assistant.tools assistant.admin" {
		t.Fatalf("precondition failed: the sealed request must show admin, got %q", req.GrantedScope)
	}

	// The user switches the setting off between the page and the button.
	h.user.McpAdminToolsEnabled = false

	rec = httptest.NewRecorder()
	handleAuthorizeDecision(rec, h.request(http.MethodPost, "/oauth/authorize",
		url.Values{"consent": {sealed}, "decision": {"allow"}}, ""))
	if rec.Code != http.StatusFound {
		t.Fatalf("status = %d, want 302", rec.Code)
	}

	if len(h.store.codes) != 1 {
		t.Fatalf("want exactly one issued code, got %d", len(h.store.codes))
	}
	for _, code := range h.store.codes {
		if code.Scope != "assistant.tools" {
			t.Fatalf("code scope = %q, want %q — a setting switched off must not still grant admin", code.Scope, "assistant.tools")
		}
	}
}

func TestConsentDenialReturnsAccessDenied(t *testing.T) {
	h := newHarness(t)
	client := h.registerClient("none")

	q := url.Values{
		"client_id":             {client.ClientID},
		"redirect_uri":          {testRedirectURI},
		"response_type":         {"code"},
		"state":                 {"state-abc"},
		"code_challenge":        {testChallenge},
		"code_challenge_method": {"S256"},
	}
	rec := httptest.NewRecorder()
	handleAuthorize(rec, h.request(http.MethodGet, "/oauth/authorize?"+q.Encode(), nil, ""))
	sealed := consentFromPage(t, rec.Body.String())

	rec = httptest.NewRecorder()
	handleAuthorizeDecision(rec, h.request(http.MethodPost, "/oauth/authorize",
		url.Values{"consent": {sealed}, "decision": {"deny"}}, ""))

	if rec.Code != http.StatusFound {
		t.Fatalf("status = %d, want 302", rec.Code)
	}
	location := rec.Header().Get("Location")
	if got := queryParam(t, location, "error"); got != "access_denied" {
		t.Fatalf("error = %q, want access_denied", got)
	}
	if got := queryParam(t, location, "state"); got != "state-abc" {
		t.Fatalf("state = %q, want state-abc", got)
	}
	if len(h.store.codes) != 0 {
		t.Fatal("a denied consent must issue no code")
	}
}

func TestConsentHandleIsBoundToTheUserAndCannotBeForged(t *testing.T) {
	h := newHarness(t)
	client := h.registerClient("none")

	q := url.Values{
		"client_id":             {client.ClientID},
		"redirect_uri":          {testRedirectURI},
		"response_type":         {"code"},
		"code_challenge":        {testChallenge},
		"code_challenge_method": {"S256"},
	}
	rec := httptest.NewRecorder()
	handleAuthorize(rec, h.request(http.MethodGet, "/oauth/authorize?"+q.Encode(), nil, ""))
	sealed := consentFromPage(t, rec.Body.String())

	tests := []struct {
		name       string
		consent    string
		wantStatus int
	}{
		{name: "the handle as issued", consent: sealed, wantStatus: http.StatusFound},
		{name: "empty handle", consent: "", wantStatus: http.StatusBadRequest},
		{name: "tampered payload", consent: "x" + sealed, wantStatus: http.StatusBadRequest},
		{name: "signature stripped", consent: strings.SplitN(sealed, ".", 2)[0], wantStatus: http.StatusBadRequest},
		{name: "signature replaced", consent: sealed + "AAAA", wantStatus: http.StatusBadRequest},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			store := newFakeStore()
			store.clients[client.ClientID] = client
			oauthDeps.store = store

			rec := httptest.NewRecorder()
			handleAuthorizeDecision(rec, h.request(http.MethodPost, "/oauth/authorize",
				url.Values{"consent": {tc.consent}, "decision": {"allow"}}, ""))
			if rec.Code != tc.wantStatus {
				t.Fatalf("status = %d, want %d", rec.Code, tc.wantStatus)
			}
		})
	}
}

func TestConsentHandleFromAnotherUserIsRefused(t *testing.T) {
	h := newHarness(t)
	client := h.registerClient("none")

	q := url.Values{
		"client_id":             {client.ClientID},
		"redirect_uri":          {testRedirectURI},
		"response_type":         {"code"},
		"code_challenge":        {testChallenge},
		"code_challenge_method": {"S256"},
	}
	rec := httptest.NewRecorder()
	handleAuthorize(rec, h.request(http.MethodGet, "/oauth/authorize?"+q.Encode(), nil, ""))
	sealed := consentFromPage(t, rec.Body.String())

	// A different signed-in user replays the handle.
	other := middleware.UserContext{
		UserID:         uuid.NewString(),
		Email:          "mallory@example.com",
		OrganizationID: h.orgID,
		OrgRole:        "Owner",
	}
	req := httptest.NewRequestWithContext(context.Background(), http.MethodPost, "/oauth/authorize",
		strings.NewReader(url.Values{"consent": {sealed}, "decision": {"allow"}}.Encode()))
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	req = req.WithContext(context.WithValue(req.Context(), middleware.UserContextKey, other))

	rec = httptest.NewRecorder()
	handleAuthorizeDecision(rec, req)

	if rec.Code != http.StatusForbidden {
		t.Fatalf("status = %d, want 403: the handle is bound to the user it was minted for", rec.Code)
	}
	if len(h.store.codes) != 0 {
		t.Fatal("a replayed handle must issue no code")
	}
}

func TestExpiredConsentHandleIsRefused(t *testing.T) {
	h := newHarness(t)
	client := h.registerClient("none")

	q := url.Values{
		"client_id":             {client.ClientID},
		"redirect_uri":          {testRedirectURI},
		"response_type":         {"code"},
		"code_challenge":        {testChallenge},
		"code_challenge_method": {"S256"},
	}
	rec := httptest.NewRecorder()
	handleAuthorize(rec, h.request(http.MethodGet, "/oauth/authorize?"+q.Encode(), nil, ""))
	sealed := consentFromPage(t, rec.Body.String())

	h.now = h.now.Add(oauthConsentTTL + time.Minute)

	rec = httptest.NewRecorder()
	handleAuthorizeDecision(rec, h.request(http.MethodPost, "/oauth/authorize",
		url.Values{"consent": {sealed}, "decision": {"allow"}}, ""))
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("status = %d, want 400 for an expired consent handle", rec.Code)
	}
}

// ---------------------------------------------------------------------------
// Token: authorization_code
// ---------------------------------------------------------------------------

func TestAuthorizationCodeHappyPath(t *testing.T) {
	h := newHarness(t)
	client := h.registerClient("none")

	code := h.authorizeAndGetCode(t, client, testRedirectURI, "assistant.tools")

	rec := httptest.NewRecorder()
	handleToken(rec, h.anonymous(http.MethodPost, "/oauth/token", tokenForm(client, url.Values{
		"code": {code},
	}), ""))

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200; body: %s", rec.Code, rec.Body.String())
	}
	if got := rec.Header().Get("Cache-Control"); got != "no-store" {
		t.Fatalf("Cache-Control = %q, want no-store (RFC 6749 §5.1)", got)
	}

	resp := decodeTokenResponse(t, rec.Body.String())
	if resp.TokenType != "Bearer" {
		t.Fatalf("token_type = %q, want Bearer", resp.TokenType)
	}
	if resp.ExpiresIn != 3600 {
		t.Fatalf("expires_in = %d, want 3600", resp.ExpiresIn)
	}
	if resp.Scope != "assistant.tools" {
		t.Fatalf("scope = %q, want assistant.tools", resp.Scope)
	}
	if resp.RefreshToken == "" {
		t.Fatal("a refresh token must be issued")
	}

	// The access token is the ordinary ReelBolt JWT plus the scope claim: validate it with the
	// production validator, because that is what the Inference API will do.
	claims, err := services.ValidateToken(resp.AccessToken)
	if err != nil {
		t.Fatalf("the access token must validate with services.ValidateToken: %v", err)
	}
	if claims.UserID != h.user.ID.String() {
		t.Fatalf("sub = %q, want %q", claims.UserID, h.user.ID.String())
	}
	if claims.Org != h.orgID.String() {
		t.Fatalf("org = %q, want %q", claims.Org, h.orgID.String())
	}
	if claims.OrgRole != "Owner" {
		t.Fatalf("orgRole = %q, want Owner", claims.OrgRole)
	}
	if claims.Scope != "assistant.tools" {
		t.Fatalf("scope claim = %q, want assistant.tools", claims.Scope)
	}
	if claims.Issuer != config.Cfg.JWTIssuer || len(claims.Audience) != 1 || claims.Audience[0] != config.Cfg.JWTAudience {
		t.Fatalf("issuer/audience must be the platform's, got %q / %v", claims.Issuer, claims.Audience)
	}
	if claims.ExpiresAt == nil || claims.ExpiresAt.Sub(claims.IssuedAt.Time) != time.Hour {
		t.Fatalf("the access token must live one hour")
	}
}

// The MCP access token carries both accepted legal versions, like a dashboard session.
func TestAuthorizationCode_AccessTokenCarriesBothLegalVersions(t *testing.T) {
	h := newHarness(t)
	client := h.registerClient("none")
	code := h.authorizeAndGetCode(t, client, testRedirectURI, "assistant.tools")

	rec := httptest.NewRecorder()
	handleToken(rec, h.anonymous(http.MethodPost, "/oauth/token", tokenForm(client, url.Values{
		"code": {code},
	}), ""))
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d; body: %s", rec.Code, rec.Body.String())
	}
	claims, err := services.ValidateToken(decodeTokenResponse(t, rec.Body.String()).AccessToken)
	if err != nil {
		t.Fatal(err)
	}
	if claims.TermsVersion != "2026-01-01" || claims.PrivacyVersion != "2026-01-02" {
		t.Fatalf("terms %q privacy %q", claims.TermsVersion, claims.PrivacyVersion)
	}
}

func TestAuthorizationCodeHappyPathWithAdminScope(t *testing.T) {
	h := newHarness(t)
	h.user.McpAdminToolsEnabled = true
	client := h.registerClient("none")

	code := h.authorizeAndGetCode(t, client, testRedirectURI, "assistant.tools assistant.admin")

	rec := httptest.NewRecorder()
	handleToken(rec, h.anonymous(http.MethodPost, "/oauth/token", tokenForm(client, url.Values{"code": {code}}), ""))
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200; body: %s", rec.Code, rec.Body.String())
	}

	resp := decodeTokenResponse(t, rec.Body.String())
	if resp.Scope != "assistant.tools assistant.admin" {
		t.Fatalf("scope = %q, want assistant.tools assistant.admin", resp.Scope)
	}
	claims, err := services.ValidateToken(resp.AccessToken)
	if err != nil {
		t.Fatalf("validate: %v", err)
	}
	if claims.Scope != "assistant.tools assistant.admin" {
		t.Fatalf("scope claim = %q", claims.Scope)
	}
}

func TestAuthorizationCodeExchangeRefusalsTable(t *testing.T) {
	tests := []struct {
		name         string
		clientMethod string
		mutate       func(h *harness, client *models.OAuthClient, form url.Values)
		wantStatus   int
		wantError    string
		mutateBefore func(h *harness, client *models.OAuthClient, code string)
	}{
		{
			name: "wrong code_verifier",
			mutate: func(_ *harness, _ *models.OAuthClient, f url.Values) {
				f.Set("code_verifier", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
			},
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_grant",
		},
		{
			name:       "absent code_verifier",
			mutate:     func(_ *harness, _ *models.OAuthClient, f url.Values) { f.Del("code_verifier") },
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_grant",
		},
		{
			name:       "plain-style verifier equal to the challenge",
			mutate:     func(_ *harness, _ *models.OAuthClient, f url.Values) { f.Set("code_verifier", testChallenge) },
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_grant",
		},
		{
			name: "redirect_uri mismatch",
			mutate: func(_ *harness, _ *models.OAuthClient, f url.Values) {
				f.Set("redirect_uri", "https://claude.example/other")
			},
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_grant",
		},
		{
			name:       "absent redirect_uri",
			mutate:     func(_ *harness, _ *models.OAuthClient, f url.Values) { f.Del("redirect_uri") },
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_grant",
		},
		{
			name:       "unknown code",
			mutate:     func(_ *harness, _ *models.OAuthClient, f url.Values) { f.Set("code", "not-a-real-code") },
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_grant",
		},
		{
			name:       "absent code",
			mutate:     func(_ *harness, _ *models.OAuthClient, f url.Values) { f.Del("code") },
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_grant",
		},
		{
			name:       "unknown client",
			mutate:     func(_ *harness, _ *models.OAuthClient, f url.Values) { f.Set("client_id", "some-other-client") },
			wantStatus: http.StatusUnauthorized,
			wantError:  "invalid_client",
		},
		{
			name: "expired code",
			mutateBefore: func(h *harness, _ *models.OAuthClient, _ string) {
				h.now = h.now.Add(services.OAuthAuthorizationCodeTTL + time.Minute)
			},
			mutate:     func(*harness, *models.OAuthClient, url.Values) {},
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_grant",
		},
		{
			name: "replayed code",
			mutateBefore: func(h *harness, client *models.OAuthClient, code string) {
				// Redeem once, successfully, before the request under test.
				rec := httptest.NewRecorder()
				handleToken(rec, h.anonymous(http.MethodPost, "/oauth/token", tokenForm(client, url.Values{"code": {code}}), ""))
				if rec.Code != http.StatusOK {
					t.Fatalf("first redemption must succeed, got %d: %s", rec.Code, rec.Body.String())
				}
			},
			mutate:     func(*harness, *models.OAuthClient, url.Values) {},
			wantStatus: http.StatusBadRequest,
			wantError:  "invalid_grant",
		},
		{
			name:         "confidential client with a wrong secret",
			clientMethod: "client_secret_post",
			mutate:       func(_ *harness, _ *models.OAuthClient, f url.Values) { f.Set("client_secret", "wrong") },
			wantStatus:   http.StatusUnauthorized,
			wantError:    "invalid_client",
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			h := newHarness(t)
			method := tc.clientMethod
			if method == "" {
				method = "none"
			}
			client := h.registerClient(method)
			code := h.authorizeAndGetCode(t, client, testRedirectURI, "assistant.tools")

			if tc.mutateBefore != nil {
				tc.mutateBefore(h, client, code)
			}

			form := tokenForm(client, url.Values{"code": {code}})
			tc.mutate(h, client, form)

			rec := httptest.NewRecorder()
			handleToken(rec, h.anonymous(http.MethodPost, "/oauth/token", form, ""))

			if rec.Code != tc.wantStatus {
				t.Fatalf("status = %d, want %d; body: %s", rec.Code, tc.wantStatus, rec.Body.String())
			}
			if got := decodeOAuthError(t, rec.Body.String()); got != tc.wantError {
				t.Fatalf("error = %q, want %q", got, tc.wantError)
			}
		})
	}
}

func TestAuthorizationCodeCannotBeRedeemedTwice(t *testing.T) {
	h := newHarness(t)
	client := h.registerClient("none")
	code := h.authorizeAndGetCode(t, client, testRedirectURI, "assistant.tools")

	first := httptest.NewRecorder()
	handleToken(first, h.anonymous(http.MethodPost, "/oauth/token", tokenForm(client, url.Values{"code": {code}}), ""))
	if first.Code != http.StatusOK {
		t.Fatalf("first redemption status = %d, want 200: %s", first.Code, first.Body.String())
	}

	second := httptest.NewRecorder()
	handleToken(second, h.anonymous(http.MethodPost, "/oauth/token", tokenForm(client, url.Values{"code": {code}}), ""))

	if second.Code != http.StatusBadRequest {
		t.Fatalf("second redemption status = %d, want 400", second.Code)
	}
	if got := decodeOAuthError(t, second.Body.String()); got != "invalid_grant" {
		t.Fatalf("error = %q, want invalid_grant", got)
	}
}

func TestUnsupportedGrantType(t *testing.T) {
	h := newHarness(t)
	client := h.registerClient("none")

	rec := httptest.NewRecorder()
	handleToken(rec, h.anonymous(http.MethodPost, "/oauth/token",
		url.Values{"grant_type": {"client_credentials"}, "client_id": {client.ClientID}}, ""))

	if rec.Code != http.StatusBadRequest {
		t.Fatalf("status = %d, want 400", rec.Code)
	}
	if got := decodeOAuthError(t, rec.Body.String()); got != "unsupported_grant_type" {
		t.Fatalf("error = %q, want unsupported_grant_type", got)
	}
}

func TestUnknownClientNeverRevealsExistence(t *testing.T) {
	h := newHarness(t)
	confidential := h.registerClient("client_secret_post")

	// (a) A client id that does not exist.
	recA := httptest.NewRecorder()
	handleToken(recA, h.anonymous(http.MethodPost, "/oauth/token", url.Values{
		"grant_type": {"authorization_code"},
		"client_id":  {"definitely-not-a-client"},
		"code":       {"whatever"},
	}, ""))

	// (b) A client id that does exist, with the wrong secret.
	recB := httptest.NewRecorder()
	handleToken(recB, h.anonymous(http.MethodPost, "/oauth/token", url.Values{
		"grant_type":    {"authorization_code"},
		"client_id":     {confidential.ClientID},
		"client_secret": {"the-wrong-secret"},
		"code":          {"whatever"},
	}, ""))

	if recA.Code != http.StatusUnauthorized || recB.Code != http.StatusUnauthorized {
		t.Fatalf("both must be 401, got %d and %d", recA.Code, recB.Code)
	}
	if recA.Body.String() != recB.Body.String() {
		t.Fatalf("an unknown client and a wrong secret must be byte-identical:\nunknown: %s\nwrong secret: %s",
			recA.Body.String(), recB.Body.String())
	}

	// A missing client_id takes the same answer too.
	recC := httptest.NewRecorder()
	handleToken(recC, h.anonymous(http.MethodPost, "/oauth/token", url.Values{
		"grant_type": {"authorization_code"},
		"code":       {"whatever"},
	}, ""))
	if recC.Code != http.StatusUnauthorized || recC.Body.String() != recA.Body.String() {
		t.Fatalf("a missing client_id must take the same answer: %d %s", recC.Code, recC.Body.String())
	}

	// And an unknown CODE, for a real public client, is invalid_grant — never a distinct
	// "no such code" answer that would confirm a guessed value did not exist.
	public := h.registerClient("none")
	recD := httptest.NewRecorder()
	handleToken(recD, h.anonymous(http.MethodPost, "/oauth/token", url.Values{
		"grant_type":    {"authorization_code"},
		"client_id":     {public.ClientID},
		"code":          {"guessed-code"},
		"redirect_uri":  {testRedirectURI},
		"code_verifier": {testVerifier},
	}, ""))
	if recD.Code != http.StatusBadRequest || decodeOAuthError(t, recD.Body.String()) != "invalid_grant" {
		t.Fatalf("an unknown code must be a plain invalid_grant, got %d %s", recD.Code, recD.Body.String())
	}
}

// ---------------------------------------------------------------------------
// Token: refresh_token
// ---------------------------------------------------------------------------

func TestRefreshTokenRotationAndReuseRevokesTheGrant(t *testing.T) {
	h := newHarness(t)
	client := h.registerClient("none")

	// Consent → code → first token pair.
	code := h.authorizeAndGetCode(t, client, testRedirectURI, "assistant.tools")
	first := httptest.NewRecorder()
	handleToken(first, h.anonymous(http.MethodPost, "/oauth/token", tokenForm(client, url.Values{"code": {code}}), ""))
	if first.Code != http.StatusOK {
		t.Fatalf("first exchange status = %d: %s", first.Code, first.Body.String())
	}
	r1 := decodeTokenResponse(t, first.Body.String()).RefreshToken
	if r1 == "" {
		t.Fatal("no refresh token issued")
	}

	// Rotate: R1 → R2.
	second := httptest.NewRecorder()
	handleToken(second, h.anonymous(http.MethodPost, "/oauth/token", url.Values{
		"grant_type":    {"refresh_token"},
		"client_id":     {client.ClientID},
		"refresh_token": {r1},
	}, ""))
	if second.Code != http.StatusOK {
		t.Fatalf("rotation status = %d, want 200: %s", second.Code, second.Body.String())
	}
	r2 := decodeTokenResponse(t, second.Body.String()).RefreshToken
	if r2 == "" || r2 == r1 {
		t.Fatalf("rotation must issue a NEW refresh token (got %q, previous %q)", r2, r1)
	}

	// The rotated-from row is revoked, and the new one is a successor of it.
	storedR2 := h.store.refresh[services.HashOAuthSecret(r2)]
	if storedR2 == nil {
		t.Fatal("the rotated token was not persisted")
	}
	if storedR2.RotatedFromID == nil || *storedR2.RotatedFromID != h.store.refresh[services.HashOAuthSecret(r1)].ID {
		t.Fatal("the new refresh token must record the row it replaced")
	}
	if h.store.refresh[services.HashOAuthSecret(r1)].RevokedAt == nil {
		t.Fatal("the rotated-from refresh token must be revoked")
	}

	// Reuse of R1 revokes the whole grant.
	reuse := httptest.NewRecorder()
	handleToken(reuse, h.anonymous(http.MethodPost, "/oauth/token", url.Values{
		"grant_type":    {"refresh_token"},
		"client_id":     {client.ClientID},
		"refresh_token": {r1},
	}, ""))
	if reuse.Code != http.StatusBadRequest {
		t.Fatalf("reuse status = %d, want 400", reuse.Code)
	}
	if got := decodeOAuthError(t, reuse.Body.String()); got != "invalid_grant" {
		t.Fatalf("reuse error = %q, want invalid_grant", got)
	}

	// R2 — which was live a moment ago — must now be dead too.
	if h.store.refresh[services.HashOAuthSecret(r2)].RevokedAt == nil {
		t.Fatal("reusing a rotated token must revoke the whole grant, including the live successor")
	}
	after := httptest.NewRecorder()
	handleToken(after, h.anonymous(http.MethodPost, "/oauth/token", url.Values{
		"grant_type":    {"refresh_token"},
		"client_id":     {client.ClientID},
		"refresh_token": {r2},
	}, ""))
	if after.Code != http.StatusBadRequest {
		t.Fatalf("the revoked successor must be refused, got %d: %s", after.Code, after.Body.String())
	}
}

func TestRefreshTokenRefusalsTable(t *testing.T) {
	tests := []struct {
		name       string
		clientKey  func(client *models.OAuthClient) string
		secret     string
		token      func(issued string) string
		wantStatus int
		wantError  string
	}{
		{name: "unknown token", token: func(string) string { return "no-such-refresh-token" }, wantStatus: http.StatusBadRequest, wantError: "invalid_grant"},
		{name: "absent token", token: func(string) string { return "" }, wantStatus: http.StatusBadRequest, wantError: "invalid_grant"},
		{name: "another client's token", clientKey: func(*models.OAuthClient) string { return "someone-else" }, token: func(s string) string { return s }, wantStatus: http.StatusUnauthorized, wantError: "invalid_client"},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			h := newHarness(t)
			client := h.registerClient("none")

			code := h.authorizeAndGetCode(t, client, testRedirectURI, "assistant.tools")
			rec := httptest.NewRecorder()
			handleToken(rec, h.anonymous(http.MethodPost, "/oauth/token", tokenForm(client, url.Values{"code": {code}}), ""))
			if rec.Code != http.StatusOK {
				t.Fatalf("setup exchange failed: %d %s", rec.Code, rec.Body.String())
			}
			issued := decodeTokenResponse(t, rec.Body.String()).RefreshToken

			clientID := client.ClientID
			if tc.clientKey != nil {
				clientID = tc.clientKey(client)
			}

			form := url.Values{
				"grant_type":    {"refresh_token"},
				"client_id":     {clientID},
				"refresh_token": {tc.token(issued)},
			}
			if tc.secret != "" {
				form.Set("client_secret", tc.secret)
			}

			out := httptest.NewRecorder()
			handleToken(out, h.anonymous(http.MethodPost, "/oauth/token", form, ""))
			if out.Code != tc.wantStatus {
				t.Fatalf("status = %d, want %d; body: %s", out.Code, tc.wantStatus, out.Body.String())
			}
			if got := decodeOAuthError(t, out.Body.String()); got != tc.wantError {
				t.Fatalf("error = %q, want %q", got, tc.wantError)
			}
		})
	}
}

func TestRefreshRefusedWhenMembershipIsGone(t *testing.T) {
	h := newHarness(t)
	client := h.registerClient("none")

	code := h.authorizeAndGetCode(t, client, testRedirectURI, "assistant.tools")
	rec := httptest.NewRecorder()
	handleToken(rec, h.anonymous(http.MethodPost, "/oauth/token", tokenForm(client, url.Values{"code": {code}}), ""))
	if rec.Code != http.StatusOK {
		t.Fatalf("setup exchange failed: %d", rec.Code)
	}
	refresh := decodeTokenResponse(t, rec.Body.String()).RefreshToken

	// The user is removed from the workspace between consent and refresh.
	h.orgID = uuid.New()

	out := httptest.NewRecorder()
	handleToken(out, h.anonymous(http.MethodPost, "/oauth/token", url.Values{
		"grant_type":    {"refresh_token"},
		"client_id":     {client.ClientID},
		"refresh_token": {refresh},
	}, ""))
	if out.Code != http.StatusBadRequest {
		t.Fatalf("status = %d, want 400", out.Code)
	}
	if got := decodeOAuthError(t, out.Body.String()); got != "invalid_grant" {
		t.Fatalf("error = %q, want invalid_grant", got)
	}
}

func TestConfidentialClientMustPresentItsSecret(t *testing.T) {
	h := newHarness(t)
	client := h.registerClient("client_secret_post")

	code := h.authorizeAndGetCode(t, client, testRedirectURI, "assistant.tools")

	tests := []struct {
		name   string
		secret string
		want   int
	}{
		{name: "correct secret", secret: testClientSecret, want: http.StatusOK},
		{name: "wrong secret", secret: "nope", want: http.StatusUnauthorized},
		{name: "absent secret", secret: "", want: http.StatusUnauthorized},
	}

	for i, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			// Each attempt needs its own code: a successful exchange consumes it.
			current := code
			if i > 0 {
				current = h.authorizeAndGetCode(t, client, testRedirectURI, "assistant.tools")
			}
			form := tokenForm(client, url.Values{"code": {current}})
			if tc.secret != "" {
				form.Set("client_secret", tc.secret)
			}

			rec := httptest.NewRecorder()
			handleToken(rec, h.anonymous(http.MethodPost, "/oauth/token", form, ""))
			if rec.Code != tc.want {
				t.Fatalf("status = %d, want %d; body: %s", rec.Code, tc.want, rec.Body.String())
			}
		})
	}
}

// ---------------------------------------------------------------------------
// Revocation (RFC 7009)
// ---------------------------------------------------------------------------

func TestRevokeAlwaysAnswers200(t *testing.T) {
	h := newHarness(t)
	client := h.registerClient("none")

	code := h.authorizeAndGetCode(t, client, testRedirectURI, "assistant.tools")
	rec := httptest.NewRecorder()
	handleToken(rec, h.anonymous(http.MethodPost, "/oauth/token", tokenForm(client, url.Values{"code": {code}}), ""))
	issued := decodeTokenResponse(t, rec.Body.String()).RefreshToken

	tests := []struct {
		name string
		form url.Values
	}{
		{name: "a real refresh token", form: url.Values{"token": {issued}, "client_id": {client.ClientID}, "token_type_hint": {"refresh_token"}}},
		{name: "an unknown token", form: url.Values{"token": {"not-a-token"}, "client_id": {client.ClientID}}},
		{name: "an unknown client", form: url.Values{"token": {issued}, "client_id": {"no-such-client"}}},
		{name: "another client's token", form: url.Values{"token": {issued}, "client_id": {"someone-else"}}},
		{name: "no token at all", form: url.Values{"client_id": {client.ClientID}}},
		{name: "an empty request", form: url.Values{}},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			out := httptest.NewRecorder()
			handleRevoke(out, h.anonymous(http.MethodPost, "/oauth/revoke", tc.form, ""))
			if out.Code != http.StatusOK {
				t.Fatalf("status = %d, want 200 — revocation is never an oracle", out.Code)
			}
		})
	}
}

func TestRevokeKillsTheGrant(t *testing.T) {
	h := newHarness(t)
	client := h.registerClient("none")

	code := h.authorizeAndGetCode(t, client, testRedirectURI, "assistant.tools")
	rec := httptest.NewRecorder()
	handleToken(rec, h.anonymous(http.MethodPost, "/oauth/token", tokenForm(client, url.Values{"code": {code}}), ""))
	if rec.Code != http.StatusOK {
		t.Fatalf("setup exchange failed: %d", rec.Code)
	}
	issued := decodeTokenResponse(t, rec.Body.String()).RefreshToken

	out := httptest.NewRecorder()
	handleRevoke(out, h.anonymous(http.MethodPost, "/oauth/revoke",
		url.Values{"token": {issued}, "client_id": {client.ClientID}}, ""))
	if out.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", out.Code)
	}

	row := h.store.refresh[services.HashOAuthSecret(issued)]
	if row == nil || row.RevokedAt == nil {
		t.Fatal("the presented refresh token must be revoked")
	}

	// And the credential no longer renews.
	refresh := httptest.NewRecorder()
	handleToken(refresh, h.anonymous(http.MethodPost, "/oauth/token", url.Values{
		"grant_type":    {"refresh_token"},
		"client_id":     {client.ClientID},
		"refresh_token": {issued},
	}, ""))
	if refresh.Code != http.StatusBadRequest {
		t.Fatalf("a revoked refresh token must not renew, got %d: %s", refresh.Code, refresh.Body.String())
	}
}

// ---------------------------------------------------------------------------
// The browser-aware auth responder
// ---------------------------------------------------------------------------

func TestBrowserAuthResponderTable(t *testing.T) {
	tests := []struct {
		name         string
		status       int
		wantStatus   int
		wantLocation string
	}{
		{
			name:         "missing session redirects to sign-in with a return path",
			status:       http.StatusUnauthorized,
			wantStatus:   http.StatusFound,
			wantLocation: "https://reelbolt.test/app/login?next=" + url.QueryEscape("/oauth/authorize?client_id=x"),
		},
		{
			name:         "a revoked session also goes to sign-in",
			status:       http.StatusUnauthorized,
			wantStatus:   http.StatusFound,
			wantLocation: "https://reelbolt.test/app/login?next=" + url.QueryEscape("/oauth/authorize?client_id=x"),
		},
		{
			name:         "password change required goes to the change-password page",
			status:       http.StatusForbidden,
			wantStatus:   http.StatusFound,
			wantLocation: "https://reelbolt.test/app/change-password?next=" + url.QueryEscape("/oauth/authorize?client_id=x"),
		},
		{
			name:       "a database failure stays a 503 rather than pretending it is a login problem",
			status:     http.StatusServiceUnavailable,
			wantStatus: http.StatusServiceUnavailable,
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			newHarness(t)

			req := httptest.NewRequestWithContext(context.Background(), http.MethodGet, "/oauth/authorize?client_id=x", nil)
			rec := httptest.NewRecorder()
			browserAuthResponder(rec, req, tc.status, `{"error":"whatever"}`)

			if rec.Code != tc.wantStatus {
				t.Fatalf("status = %d, want %d", rec.Code, tc.wantStatus)
			}
			if tc.wantLocation != "" {
				if got := rec.Header().Get("Location"); got != tc.wantLocation {
					t.Fatalf("Location = %q, want %q", got, tc.wantLocation)
				}
			}
		})
	}
}

func TestAuthorizeWithoutASessionRedirectsInsteadOfReturning401(t *testing.T) {
	newHarness(t)

	// No user context at all: the handler's own fallback must redirect, never 401.
	rec := httptest.NewRecorder()
	handleAuthorize(rec, httptest.NewRequestWithContext(context.Background(), http.MethodGet, "/oauth/authorize?client_id=x", nil))

	if rec.Code != http.StatusFound {
		t.Fatalf("status = %d, want 302", rec.Code)
	}
	if loc := rec.Header().Get("Location"); !strings.Contains(loc, "/app/login?next=") {
		t.Fatalf("Location = %q, want a sign-in redirect with a return path", loc)
	}
}

// ---------------------------------------------------------------------------
// The redirect-URI backstop
// ---------------------------------------------------------------------------

func TestAppendQueryPreservesRegisteredQueryParameters(t *testing.T) {
	got := appendQuery("http://127.0.0.1:7654/callback?slot=1", url.Values{
		"code":  {"abc"},
		"state": {"s"},
	})
	want := "http://127.0.0.1:7654/callback?code=abc&slot=1&state=s"
	if got != want {
		t.Fatalf("appendQuery = %q, want %q", got, want)
	}
}

func TestConsentSealRoundTripRejectsTampering(t *testing.T) {
	h := newHarness(t)

	req := consentRequest{
		UserID:        "user-1",
		ExpiresAt:     h.now.Add(time.Minute).Unix(),
		ClientID:      "client-1",
		RedirectURI:   testRedirectURI,
		GrantedScope:  "assistant.tools",
		CodeChallenge: testChallenge,
	}
	sealed, err := sealConsent(req)
	if err != nil {
		t.Fatalf("seal: %v", err)
	}

	got, ok := openConsent(sealed, h.now)
	if !ok {
		t.Fatal("a freshly sealed request must verify")
	}
	if got.ClientID != req.ClientID || got.GrantedScope != req.GrantedScope {
		t.Fatalf("round trip changed the request: %#v", got)
	}

	if _, ok := openConsent(sealed, h.now.Add(oauthConsentTTL+time.Minute)); ok {
		t.Fatal("an expired handle must not verify")
	}
	// Base64url payloads are case-sensitive: a single flipped character must not verify.
	if _, ok := openConsent(strings.ToUpper(sealed), h.now); ok {
		t.Fatal("a tampered handle must not verify")
	}
}

// sha256Challenge is the S256 challenge for a verifier, computed independently of the production
// verifier so a test can assert the RFC vector really is what the handlers compare against.
func sha256Challenge(verifier string) string {
	sum := sha256.Sum256([]byte(verifier))
	return base64.RawURLEncoding.EncodeToString(sum[:])
}

func TestTestVectorMatchesTheChallengeFormula(t *testing.T) {
	if got := sha256Challenge(testVerifier); got != testChallenge {
		t.Fatalf("the RFC 7636 Appendix B vector is inconsistent: %q != %q", got, testChallenge)
	}
}

// A runs.watch-only consent shows the narrow description, grants exactly that scope, and the token
// and its refresh carry only that scope. Neither the admin opt-in nor a changed account setting between
// render and submit can widen it.
func TestRunsWatchConsentTokenAndRefresh(t *testing.T) {
	h := newHarness(t)
	h.user.McpAdminToolsEnabled = true
	client := h.registerClient("none")

	q := url.Values{
		"client_id":             {client.ClientID},
		"redirect_uri":          {testRedirectURI},
		"response_type":         {"code"},
		"scope":                 {"runs.watch"},
		"code_challenge":        {testChallenge},
		"code_challenge_method": {"S256"},
	}
	rec := httptest.NewRecorder()
	handleAuthorize(rec, h.request(http.MethodGet, "/oauth/authorize?"+q.Encode(), nil, ""))
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rec.Code)
	}
	page := html.UnescapeString(rec.Body.String())
	if !strings.Contains(page, "See whether your runs are waiting, running, finished or failed, and how far they have got. It cannot start runs, change anything or read your files.") {
		t.Fatal("the consent page must describe runs.watch in plain words")
	}
	if strings.Contains(page, "run workflows") || strings.Contains(page, "assistant.tools") || strings.Contains(page, "assistant.admin") {
		t.Fatal("the consent page must not mention the assistant scopes for a runs.watch consent")
	}
	sealed := consentFromPage(t, rec.Body.String())
	req, ok := openConsent(sealed, h.now)
	if !ok || req.GrantedScope != "runs.watch" {
		t.Fatalf("granted scope = %q, want runs.watch", req.GrantedScope)
	}

	code := h.authorizeAndGetCode(t, client, testRedirectURI, "runs.watch")
	first := httptest.NewRecorder()
	handleToken(first, h.anonymous(http.MethodPost, "/oauth/token", tokenForm(client, url.Values{"code": {code}}), ""))
	if first.Code != http.StatusOK {
		t.Fatalf("exchange status = %d: %s", first.Code, first.Body.String())
	}
	tok := decodeTokenResponse(t, first.Body.String())
	if tok.Scope != "runs.watch" {
		t.Fatalf("scope = %q, want runs.watch", tok.Scope)
	}
	claims, err := services.ValidateToken(tok.AccessToken)
	if err != nil {
		t.Fatalf("the access token must validate: %v", err)
	}
	if claims.Scope != "runs.watch" {
		t.Fatalf("scope claim = %q, want exactly runs.watch", claims.Scope)
	}

	// Refresh keeps the scope, even with the admin opt-in on.
	second := httptest.NewRecorder()
	handleToken(second, h.anonymous(http.MethodPost, "/oauth/token", url.Values{
		"grant_type":    {"refresh_token"},
		"client_id":     {client.ClientID},
		"refresh_token": {tok.RefreshToken},
	}, ""))
	if second.Code != http.StatusOK {
		t.Fatalf("refresh status = %d: %s", second.Code, second.Body.String())
	}
	refreshed := decodeTokenResponse(t, second.Body.String())
	if refreshed.Scope != "runs.watch" {
		t.Fatalf("refreshed scope = %q, want runs.watch", refreshed.Scope)
	}
	rc, err := services.ValidateToken(refreshed.AccessToken)
	if err != nil || rc.Scope != "runs.watch" {
		t.Fatalf("refreshed claim = %+v, err %v; want runs.watch", rc, err)
	}
}

// A tampered consent handle cannot be widened, and a handle that shows runs.watch re-intersects to
// runs.watch even when the admin opt-in flips between render and submit.
func TestRunsWatchConsentCannotBeWidened(t *testing.T) {
	h := newHarness(t)
	client := h.registerClient("none")

	req := consentRequest{
		UserID:              h.user.ID.String(),
		ExpiresAt:           h.now.Add(time.Minute).Unix(),
		ClientID:            client.ClientID,
		ClientName:          client.ClientName,
		RedirectURI:         testRedirectURI,
		ResponseType:        "code",
		RequestedScope:      "runs.watch",
		GrantedScope:        "runs.watch",
		CodeChallenge:       testChallenge,
		CodeChallengeMethod: "S256",
	}
	sealed, err := sealConsent(req)
	if err != nil {
		t.Fatal(err)
	}

	// Flipping the sealed value breaks the seal.
	tampered := []byte(sealed)
	tampered[len(tampered)/2] ^= 1
	rec := httptest.NewRecorder()
	handleAuthorizeDecision(rec, h.request(http.MethodPost, "/oauth/authorize",
		url.Values{"consent": {string(tampered)}, "decision": {"allow"}}, ""))
	if rec.Code == http.StatusFound && queryParam(t, rec.Header().Get("Location"), "code") != "" {
		t.Fatal("a tampered consent handle must not produce a code")
	}

	// A live admin opt-in does not add anything.
	h.user.McpAdminToolsEnabled = true
	rec = httptest.NewRecorder()
	handleAuthorizeDecision(rec, h.request(http.MethodPost, "/oauth/authorize",
		url.Values{"consent": {sealed}, "decision": {"allow"}}, ""))
	if rec.Code != http.StatusFound {
		t.Fatalf("status = %d, want 302; body: %s", rec.Code, rec.Body.String())
	}
	code := queryParam(t, rec.Header().Get("Location"), "code")
	row := h.store.codes[services.HashOAuthSecret(code)]
	if row == nil || row.Scope != "runs.watch" {
		t.Fatalf("stored code scope = %+v, want runs.watch", row)
	}
}
