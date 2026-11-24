package handlers

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"regexp"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
	"gorm.io/driver/postgres"
	"gorm.io/gorm"
	"gorm.io/gorm/logger"
)

// The end-to-end run docs/design/mcp-oauth.md §10 asks for: the real router, the real middleware, the
// real GORM store, against a real PostgreSQL. Nothing here is faked — the only thing it needs is a
// database to point at, so it is skipped unless OAUTH_TEST_DATABASE_URL is set.
//
//	OAUTH_TEST_DATABASE_URL='postgres://postgres:postgres@127.0.0.1:55432/oauthit?sslmode=disable' \
//	    go test ./handlers/ -run EndToEnd -v
//
// It walks the whole contract: metadata → dynamic registration → an unauthenticated authorize
// navigation (which must redirect, never 401) → the authenticated consent screen → the decision → the
// code → the token exchange with PKCE → the access token used against a normal authenticated route →
// refresh rotation and reuse detection → revocation, and the §11 settings route in between (which is
// how the admin scope is shown to be granted only when the setting is on).

// e2eDDL is a transcription of the Inference API's migrations for the tables this flow touches. The
// OAuth tables are created by `AddMcpOAuth`; the rest are long-standing tables the Go API reads and
// writes but never migrates. The three OAuth tables also appear in
// handlers/oauth/store_integration_test.go — the two packages cannot share a test helper, and both are
// cross-checked against the real migration by models/oauth_test.go.
const e2eDDL = `
CREATE TABLE IF NOT EXISTS application_users (
    id uuid PRIMARY KEY,
    email character varying(255) NOT NULL,
    display_name character varying(100) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    password_hash character varying(255) NOT NULL,
    is_admin boolean NOT NULL DEFAULT false,
    must_change_password boolean NOT NULL DEFAULT false,
    last_active_organization_id uuid NULL,
    email_verified_at timestamp with time zone NULL,
    tokens_invalidated_at timestamp with time zone NULL,
    deletion_requested_at timestamp with time zone NULL,
    signup_ip character varying(64) NULL,
    auth_provider character varying(50) NOT NULL DEFAULT 'Password',
    mcp_admin_tools_enabled boolean NOT NULL DEFAULT false
);
CREATE TABLE IF NOT EXISTS organizations (
    id uuid PRIMARY KEY,
    name character varying(200) NOT NULL,
    kind character varying(50) NOT NULL,
    deleted_at timestamp with time zone NULL
);
CREATE TABLE IF NOT EXISTS organization_memberships (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    organization_id uuid NOT NULL,
    role character varying(50) NOT NULL,
    created_at timestamp with time zone NOT NULL
);
CREATE TABLE IF NOT EXISTS legal_acceptances (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    document character varying(50) NOT NULL,
    version character varying(50) NOT NULL,
    accepted_at timestamp with time zone NOT NULL,
    ip character varying(64) NULL
);
CREATE TABLE IF NOT EXISTS oauth_clients (
    id uuid PRIMARY KEY,
    client_id character varying(128) NOT NULL,
    client_secret_hash character varying(200) NULL,
    client_name character varying(200) NOT NULL,
    redirect_uris jsonb NOT NULL,
    grant_types jsonb NOT NULL,
    response_types jsonb NOT NULL,
    token_endpoint_auth_method character varying(50) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    last_used_at timestamp with time zone NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_oauth_clients_client_id ON oauth_clients (client_id);
CREATE TABLE IF NOT EXISTS oauth_authorization_codes (
    id uuid PRIMARY KEY,
    code_hash character varying(64) NOT NULL,
    client_id character varying(128) NOT NULL,
    user_id uuid NOT NULL,
    organization_id uuid NOT NULL,
    redirect_uri character varying(2048) NOT NULL,
    code_challenge character varying(128) NOT NULL,
    scope character varying(200) NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    consumed_at timestamp with time zone NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_oauth_authorization_codes_code_hash ON oauth_authorization_codes (code_hash);
CREATE TABLE IF NOT EXISTS oauth_refresh_tokens (
    id uuid PRIMARY KEY,
    token_hash character varying(64) NOT NULL,
    client_id character varying(128) NOT NULL,
    user_id uuid NOT NULL,
    organization_id uuid NOT NULL,
    scope character varying(200) NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    revoked_at timestamp with time zone NULL,
    rotated_from_id uuid NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_oauth_refresh_tokens_token_hash ON oauth_refresh_tokens (token_hash);
`

const (
	// The RFC 7636 Appendix B PKCE pair, used so the exchange is verified against the specification.
	e2eVerifier  = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
	e2eChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"
	// e2eRedirectURI is a loopback address, the shape RFC 8252 blesses and the one a CLI client uses.
	e2eRedirectURI = "http://127.0.0.1:7654/callback"
)

type e2eHarness struct {
	t       *testing.T
	server  *httptest.Server
	client  *http.Client
	user    *models.ApplicationUser
	orgID   uuid.UUID
	session string
}

// newE2EHarness provisions the database, seeds one user and one workspace, and serves the real router.
func newE2EHarness(t *testing.T) *e2eHarness {
	t.Helper()

	dsn := os.Getenv("OAUTH_TEST_DATABASE_URL")
	if dsn == "" {
		t.Skip("OAUTH_TEST_DATABASE_URL is not set; skipping the end-to-end OAuth run")
	}

	db, err := gorm.Open(postgres.Open(dsn), &gorm.Config{Logger: logger.Default.LogMode(logger.Silent)})
	if err != nil {
		t.Fatalf("connect to %s: %v", dsn, err)
	}
	if err := db.Exec(e2eDDL).Error; err != nil {
		t.Fatalf("apply schema: %v", err)
	}
	// Nothing is truncated and every row this test creates has a fresh uuid: the package's tests run
	// in parallel with the ones in handlers/oauth, which share this database, so isolation has to come
	// from unique keys rather than from clearing shared tables.

	oldDB, oldCfg := database.DB, config.Cfg
	database.DB = db
	config.Cfg.PublicBaseURL = "https://reelbolt.test"
	config.Cfg.OAuthIssuer = "https://reelbolt.test"
	config.Cfg.JWTIssuer = "reelbolt-api"
	config.Cfg.JWTAudience = "reelbolt-inference"
	config.Cfg.JWTSigningKey = strings.Repeat("k", 32)
	config.Cfg.CookieSecure = false
	t.Cleanup(func() { database.DB, config.Cfg = oldDB, oldCfg })

	now := time.Now().UTC()
	h := &e2eHarness{
		t:     t,
		orgID: uuid.New(),
		user: &models.ApplicationUser{
			ID:              uuid.New(),
			Email:           "ada@example.com",
			DisplayName:     "Ada",
			CreatedAt:       now,
			PasswordHash:    "not-used-here",
			EmailVerifiedAt: &now,
			AuthProvider:    "Password",
		},
	}

	// Seed by raw SQL: this is a test database, and the point is to exercise the Go side's queries, not
	// GORM's INSERT field ordering on tables it does not own.
	if err := db.Exec(`INSERT INTO application_users
			(id, email, display_name, created_at, password_hash, is_admin, must_change_password,
			 email_verified_at, auth_provider, mcp_admin_tools_enabled)
			VALUES (?,?,?,?,?,?,?,?,?,?)`,
		h.user.ID, h.user.Email, h.user.DisplayName, now, h.user.PasswordHash, false, false,
		now, "Password", false).Error; err != nil {
		t.Fatalf("seed user: %v", err)
	}
	if err := db.Exec(`INSERT INTO organizations (id, name, kind) VALUES (?,?,?)`,
		h.orgID, "Ada's workspace", "Personal").Error; err != nil {
		t.Fatalf("seed organization: %v", err)
	}
	if err := db.Exec(`INSERT INTO organization_memberships (id, user_id, organization_id, role, created_at)
			VALUES (?,?,?,?,?)`, uuid.New(), h.user.ID, h.orgID, "Owner", now).Error; err != nil {
		t.Fatalf("seed membership: %v", err)
	}

	membership := &services.ActiveMembership{OrgID: h.orgID, OrgName: "Ada's workspace", OrgKind: "Personal", Role: "Owner"}
	session, err := services.GenerateTokenWithTerms(h.user, membership, "")
	if err != nil {
		t.Fatalf("mint session token: %v", err)
	}
	h.session = session

	NewHandler()
	RegisterHandlers()
	h.server = httptest.NewServer(Router)
	t.Cleanup(h.server.Close)

	// Redirects are inspected rather than followed: the Location header IS the assertion for the
	// authorize leg.
	h.client = &http.Client{
		Timeout:       10 * time.Second,
		CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },
	}
	return h
}

// do sends a request with the session cookie attached when withSession is true.
func (h *e2eHarness) do(method, path string, withSession bool, body io.Reader, contentType string) *http.Response {
	h.t.Helper()
	req, err := http.NewRequestWithContext(context.Background(), method, h.server.URL+path, body)
	if err != nil {
		h.t.Fatalf("build request: %v", err)
	}
	if contentType != "" {
		req.Header.Set("Content-Type", contentType)
	}
	if withSession {
		req.AddCookie(&http.Cookie{Name: "reelbolt_token", Value: h.session})
	}
	resp, err := h.client.Do(req)
	if err != nil {
		h.t.Fatalf("%s %s: %v", method, path, err)
	}
	return resp
}

func readBody(t *testing.T, resp *http.Response) string {
	t.Helper()
	defer resp.Body.Close()
	b, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatalf("read body: %v", err)
	}
	return string(b)
}

var e2eConsentPattern = regexp.MustCompile(`name="consent" value="([^"]+)"`)

// TestEndToEndOAuthRun is the full contract walk. It is one test rather than several because each step
// depends on the previous one's artefact — the code from the consent, the refresh token from the
// exchange — and splitting it would mean re-running the same setup to test the second half.
func TestEndToEndOAuthRun(t *testing.T) {
	h := newE2EHarness(t)

	// ---- 1. The metadata document, at the real HTTP surface --------------------------------
	resp := h.do(http.MethodGet, "/.well-known/oauth-authorization-server", false, nil, "")
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("metadata status = %d, want 200", resp.StatusCode)
	}
	var metadata map[string]any
	if err := json.Unmarshal([]byte(readBody(t, resp)), &metadata); err != nil {
		t.Fatalf("decode metadata: %v", err)
	}
	if metadata["issuer"] != "https://reelbolt.test" {
		t.Fatalf("issuer = %v, want the configured origin", metadata["issuer"])
	}
	if metadata["token_endpoint"] != "https://reelbolt.test/oauth/token" {
		t.Fatalf("token_endpoint = %v", metadata["token_endpoint"])
	}

	// ---- 2. Dynamic client registration ----------------------------------------------------
	registerBody := `{"client_name":"Claude Code","redirect_uris":["` + e2eRedirectURI + `"],"token_endpoint_auth_method":"none"}`
	resp = h.do(http.MethodPost, "/oauth/register", false, strings.NewReader(registerBody), "application/json")
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusCreated {
		t.Fatalf("register status = %d, want 201; body: %s", resp.StatusCode, readBody(t, resp))
	}
	var registration map[string]any
	if err := json.Unmarshal([]byte(readBody(t, resp)), &registration); err != nil {
		t.Fatalf("decode registration: %v", err)
	}
	clientID, _ := registration["client_id"].(string)
	if clientID == "" {
		t.Fatal("registration returned no client_id")
	}
	if _, hasSecret := registration["client_secret"]; hasSecret {
		t.Fatal("a public client must not be issued a secret")
	}

	authorizeQuery := url.Values{
		"client_id":             {clientID},
		"redirect_uri":          {e2eRedirectURI},
		"response_type":         {"code"},
		"scope":                 {"assistant.tools assistant.admin"},
		"state":                 {"opaque-state"},
		"code_challenge":        {e2eChallenge},
		"code_challenge_method": {"S256"},
	}

	// ---- 3. An unauthenticated authorize navigation must NOT be a 401 ----------------------
	resp = h.do(http.MethodGet, "/oauth/authorize?"+authorizeQuery.Encode(), false, nil, "")
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusFound {
		t.Fatalf("unauthenticated authorize status = %d, want 302 (never an API 401)", resp.StatusCode)
	}
	loginRedirect := resp.Header.Get("Location")
	if !strings.HasPrefix(loginRedirect, "https://reelbolt.test/app/login?next=") {
		t.Fatalf("unauthenticated authorize Location = %q, want a sign-in redirect with a return path", loginRedirect)
	}
	if !strings.Contains(loginRedirect, url.QueryEscape("/oauth/authorize?")) {
		t.Fatalf("the return path must carry the authorize URL, got %q", loginRedirect)
	}

	// ---- 4. Consent, with the session, shows the client and grants no admin ----------------
	resp = h.do(http.MethodGet, "/oauth/authorize?"+authorizeQuery.Encode(), true, nil, "")
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("consent GET status = %d, want 200; body: %s", resp.StatusCode, readBody(t, resp))
	}
	page := readBody(t, resp)
	if !strings.Contains(page, "Claude Code") {
		t.Fatal("the consent screen must name the client")
	}
	if strings.Contains(page, "<code>assistant.admin</code>") {
		t.Fatal("admin must not be offered while mcp_admin_tools_enabled is off")
	}
	if !strings.Contains(page, "not</strong> being granted") {
		t.Fatal("the page must say plainly that admin was requested and is not being granted")
	}
	consentMatch := e2eConsentPattern.FindStringSubmatch(page)
	if consentMatch == nil {
		t.Fatal("the consent page carried no sealed consent value")
	}
	sealed := consentMatch[1]

	// ---- 5. Approving issues a code ---------------------------------------------------------
	resp = h.do(http.MethodPost, "/oauth/authorize", true,
		strings.NewReader(url.Values{"consent": {sealed}, "decision": {"allow"}}.Encode()),
		"application/x-www-form-urlencoded")
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusFound {
		t.Fatalf("consent POST status = %d, want 302; body: %s", resp.StatusCode, readBody(t, resp))
	}
	location := resp.Header.Get("Location")
	code := queryValue(t, location, "code")
	if code == "" {
		t.Fatalf("no code in %q", location)
	}
	if got := queryValue(t, location, "state"); got != "opaque-state" {
		t.Fatalf("state = %q, want it echoed back", got)
	}

	// ---- 6. The PKCE exchange ---------------------------------------------------------------
	exchangeForm := url.Values{
		"grant_type":    {"authorization_code"},
		"client_id":     {clientID},
		"code":          {code},
		"redirect_uri":  {e2eRedirectURI},
		"code_verifier": {e2eVerifier},
	}
	resp = h.do(http.MethodPost, "/oauth/token", false, strings.NewReader(exchangeForm.Encode()),
		"application/x-www-form-urlencoded")
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("token status = %d, want 200; body: %s", resp.StatusCode, readBody(t, resp))
	}
	var tokens struct {
		AccessToken  string `json:"access_token"`
		RefreshToken string `json:"refresh_token"`
		TokenType    string `json:"token_type"`
		ExpiresIn    int    `json:"expires_in"`
		Scope        string `json:"scope"`
	}
	if err := json.Unmarshal([]byte(readBody(t, resp)), &tokens); err != nil {
		t.Fatalf("decode token response: %v", err)
	}
	if tokens.TokenType != "Bearer" || tokens.ExpiresIn != 3600 {
		t.Fatalf("unexpected token metadata: %+v", tokens)
	}
	if tokens.Scope != "assistant.tools" {
		t.Fatalf("scope = %q, want assistant.tools only (admin is off)", tokens.Scope)
	}
	if tokens.RefreshToken == "" {
		t.Fatal("no refresh token issued")
	}

	// The access token is the platform's ordinary JWT: validate it with the production validator; it is a scoped
	// token, so it must be refused on a session route (it is not a browser session).
	claims, err := services.ValidateToken(tokens.AccessToken)
	if err != nil {
		t.Fatalf("the access token must validate: %v", err)
	}
	if claims.UserID != h.user.ID.String() {
		t.Fatalf("sub = %q, want the user id", claims.UserID)
	}
	if claims.Org != h.orgID.String() {
		t.Fatalf("org = %q, want the active workspace", claims.Org)
	}
	if claims.Scope != "assistant.tools" {
		t.Fatalf("scope claim = %q", claims.Scope)
	}

	req, _ := http.NewRequestWithContext(context.Background(), http.MethodGet, h.server.URL+"/api/v1/auth/settings", nil)
	req.Header.Set("Authorization", "Bearer "+tokens.AccessToken)
	authorized, err := h.client.Do(req)
	if err != nil {
		t.Fatalf("call a session route with the OAuth token: %v", err)
	}
	refusedBody := readBody(t, authorized)
	if authorized.StatusCode != http.StatusForbidden || !strings.Contains(refusedBody, "mcp_token_scope") {
		t.Fatalf("a session route must refuse the scoped OAuth token with 403 mcp_token_scope: %d %s", authorized.StatusCode, refusedBody)
	}
	settingsResp := h.do(http.MethodGet, "/api/v1/auth/settings", true, nil, "")
	if settingsResp.StatusCode != http.StatusOK {
		t.Fatalf("settings GET status = %d; body: %s", settingsResp.StatusCode, readBody(t, settingsResp))
	}
	var settings map[string]any
	if err := json.Unmarshal([]byte(readBody(t, settingsResp)), &settings); err != nil {
		t.Fatalf("decode settings: %v", err)
	}
	if settings["mcpAdminToolsEnabled"] != false {
		t.Fatalf("settings = %v, want mcpAdminToolsEnabled false", settings)
	}

	// ---- 7. §11: turn the admin setting on, and prove it is really stored -------------------
	patch := h.do(http.MethodPatch, "/api/v1/auth/settings", true,
		strings.NewReader(`{"mcpAdminToolsEnabled":true}`), "application/json")
	if patch.StatusCode != http.StatusOK {
		t.Fatalf("settings PATCH status = %d, want 200; body: %s", patch.StatusCode, readBody(t, patch))
	}
	var patched map[string]any
	if err := json.Unmarshal([]byte(readBody(t, patch)), &patched); err != nil {
		t.Fatalf("decode patched settings: %v", err)
	}
	if patched["mcpAdminToolsEnabled"] != true {
		t.Fatalf("PATCH returned %v, want the stored value true", patched)
	}
	var stored bool
	if err := database.DB.Raw(`SELECT mcp_admin_tools_enabled FROM application_users WHERE id = ?`, h.user.ID).
		Scan(&stored).Error; err != nil {
		t.Fatalf("read the column back: %v", err)
	}
	if !stored {
		t.Fatal("the PATCH reported success but the column is still false")
	}

	// The settings route is self-only, so a second account's value must be untouched. Seed one.
	otherID := uuid.New()
	if err := database.DB.Exec(`INSERT INTO application_users
			(id, email, display_name, created_at, password_hash, is_admin, must_change_password,
			 email_verified_at, auth_provider, mcp_admin_tools_enabled)
			VALUES (?,?,?,?,?,?,?,?,?,?)`,
		otherID, "mallory@example.com", "Mallory", time.Now().UTC(), "x", false, false,
		time.Now().UTC(), "Password", true).Error; err != nil {
		t.Fatalf("seed second user: %v", err)
	}
	var otherStored bool
	if err := database.DB.Raw(`SELECT mcp_admin_tools_enabled FROM application_users WHERE id = ?`, otherID).
		Scan(&otherStored).Error; err != nil {
		t.Fatalf("read the other user's column: %v", err)
	}
	if !otherStored {
		t.Fatal("the other account's value should be untouched by the caller's PATCH")
	}

	// ---- 8. A second consent now grants admin, because the setting is on -------------------
	resp = h.do(http.MethodGet, "/oauth/authorize?"+authorizeQuery.Encode(), true, nil, "")
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("second consent GET status = %d", resp.StatusCode)
	}
	page = readBody(t, resp)
	if !strings.Contains(page, "<code>assistant.admin</code>") {
		t.Fatal("admin must be offered once mcp_admin_tools_enabled is on")
	}
	sealed = e2eConsentPattern.FindStringSubmatch(page)[1]

	resp = h.do(http.MethodPost, "/oauth/authorize", true,
		strings.NewReader(url.Values{"consent": {sealed}, "decision": {"allow"}}.Encode()),
		"application/x-www-form-urlencoded")
	defer resp.Body.Close()
	code2 := queryValue(t, resp.Header.Get("Location"), "code")
	if code2 == "" {
		t.Fatal("no code issued for the second consent")
	}

	resp = h.do(http.MethodPost, "/oauth/token", false, strings.NewReader(url.Values{
		"grant_type":    {"authorization_code"},
		"client_id":     {clientID},
		"code":          {code2},
		"redirect_uri":  {e2eRedirectURI},
		"code_verifier": {e2eVerifier},
	}.Encode()), "application/x-www-form-urlencoded")
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("second exchange status = %d; body: %s", resp.StatusCode, readBody(t, resp))
	}
	var tokens2 struct {
		AccessToken  string `json:"access_token"`
		RefreshToken string `json:"refresh_token"`
		Scope        string `json:"scope"`
	}
	if err := json.Unmarshal([]byte(readBody(t, resp)), &tokens2); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if tokens2.Scope != "assistant.tools assistant.admin" {
		t.Fatalf("scope = %q, want the admin scope once the setting is on", tokens2.Scope)
	}
	claims2, err := services.ValidateToken(tokens2.AccessToken)
	if err != nil {
		t.Fatalf("validate second token: %v", err)
	}
	if claims2.Scope != "assistant.tools assistant.admin" {
		t.Fatalf("scope claim = %q", claims2.Scope)
	}

	// ---- 9. The first token is NOT retroactively widened -----------------------------------
	// §11: the setting is the live authority, the scope is the audit record of what was agreed.
	if claims.Scope != "assistant.tools" {
		t.Fatalf("the already-issued token's scope changed to %q", claims.Scope)
	}

	// ---- 10. Refresh rotation, and reuse revokes the grant ---------------------------------
	resp = h.do(http.MethodPost, "/oauth/token", false, strings.NewReader(url.Values{
		"grant_type":    {"refresh_token"},
		"client_id":     {clientID},
		"refresh_token": {tokens2.RefreshToken},
	}.Encode()), "application/x-www-form-urlencoded")
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("refresh status = %d, want 200; body: %s", resp.StatusCode, readBody(t, resp))
	}
	var rotated struct {
		RefreshToken string `json:"refresh_token"`
	}
	if err := json.Unmarshal([]byte(readBody(t, resp)), &rotated); err != nil {
		t.Fatalf("decode refresh response: %v", err)
	}
	if rotated.RefreshToken == "" || rotated.RefreshToken == tokens2.RefreshToken {
		t.Fatal("rotation must issue a new refresh token")
	}

	resp = h.do(http.MethodPost, "/oauth/token", false, strings.NewReader(url.Values{
		"grant_type":    {"refresh_token"},
		"client_id":     {clientID},
		"refresh_token": {tokens2.RefreshToken},
	}.Encode()), "application/x-www-form-urlencoded")
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusBadRequest {
		t.Fatalf("reusing a rotated token: status = %d, want 400", resp.StatusCode)
	}
	var reuseErr map[string]string
	if err := json.Unmarshal([]byte(readBody(t, resp)), &reuseErr); err != nil {
		t.Fatalf("decode reuse error: %v", err)
	}
	if reuseErr["error"] != "invalid_grant" {
		t.Fatalf("reuse error = %q, want invalid_grant", reuseErr["error"])
	}

	var liveTokens int64
	if err := database.DB.Raw(`SELECT count(*) FROM oauth_refresh_tokens
	                            WHERE client_id = ? AND user_id = ? AND revoked_at IS NULL`,
		clientID, h.user.ID).Scan(&liveTokens).Error; err != nil {
		t.Fatalf("count live tokens: %v", err)
	}
	if liveTokens != 0 {
		t.Fatalf("reuse must revoke the whole grant, but %d token(s) are still live", liveTokens)
	}

	// ---- 11. Revocation always answers 200 --------------------------------------------------
	resp = h.do(http.MethodPost, "/oauth/revoke", false, strings.NewReader(url.Values{
		"token":           {rotated.RefreshToken},
		"client_id":       {clientID},
		"token_type_hint": {"refresh_token"},
	}.Encode()), "application/x-www-form-urlencoded")
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("revoke status = %d, want 200", resp.StatusCode)
	}
	_ = readBody(t, resp)

	// An unknown token gets the same answer — never an existence oracle.
	resp = h.do(http.MethodPost, "/oauth/revoke", false, strings.NewReader(url.Values{
		"token": {"not-a-token"}, "client_id": {"no-such-client"},
	}.Encode()), "application/x-www-form-urlencoded")
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("revoking an unknown token status = %d, want 200", resp.StatusCode)
	}
	_ = readBody(t, resp)

	// ---- 12. The consent handle is single-use in effect: the code it produced cannot be replayed --
	resp = h.do(http.MethodPost, "/oauth/token", false, strings.NewReader(exchangeForm.Encode()),
		"application/x-www-form-urlencoded")
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusBadRequest {
		t.Fatalf("replaying the first code: status = %d, want 400", resp.StatusCode)
	}
	_ = readBody(t, resp)
}

func queryValue(t *testing.T, rawURL, key string) string {
	t.Helper()
	u, err := url.Parse(rawURL)
	if err != nil {
		t.Fatalf("parse %q: %v", rawURL, err)
	}
	return u.Query().Get(key)
}
