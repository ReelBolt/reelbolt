package oauth

import (
	"encoding/json"
	"html"
	"io"
	"log"
	"net/http"
	"net/url"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

// The OAuth 2.1 authorization server for MCP clients (docs/design/mcp-oauth.md §4).
//
// It is deliberately NOT a second identity provider. The access token it mints is the ordinary
// ReelBolt JWT plus a `scope` claim — same issuer, audience and HS256 signing key as
// services.GenerateToken — so the Inference API accepts it through the validator it already runs and
// re-reads membership from the database exactly as it does for a dashboard session. What this package
// adds is the browser consent and the per-client, rotatable credential that a CLI client can hold
// without a password.
//
// Paths live under /oauth (and the RFC 8414 well-known path) on purpose:
// /api/v1/auth/oauth/{provider} is the existing Google/GitHub *sign-in client* surface and is not
// touched by anything here.

const (
	// oauthConsentTTL is how long a rendered consent page may be submitted. It bounds the window a
	// signed consent handle is valid for; the authorization request it carries is already fixed.
	oauthConsentTTL = 10 * time.Minute

	// maxRequestedScopeLen caps the scope parameter. The granted scope that is actually stored is at
	// most "assistant.tools assistant.admin" (34 characters), so this only stops a client from posting
	// an unbounded string at the endpoint.
	maxRequestedScopeLen = 1024

	// maxClientNameLen matches oauth_clients.client_name (varchar(200)).
	maxClientNameLen = 200
	// maxRedirectURIsLen matches oauth_clients.redirect_uris' practical bound; a client needs one, and
	// the column is jsonb.
	maxRedirectURIs = 10
	// maxRedirectURILen matches oauth_clients.redirect_uris' elements and
	// oauth_authorization_codes.redirect_uri (varchar(2048)).
	maxRedirectURILen = 2048

	defaultClientName = "MCP client"
)

// deps are the authorization server's collaborators, replaceable in tests — the same shape the rest
// of this service uses (see handlers/auth). Nothing here holds per-call state.
type deps struct {
	store        OAuthStore
	getUser      func(uuid.UUID) (*models.ApplicationUser, error)
	membership   services.MembershipLookup
	termsVersion func(uuid.UUID) (string, error)
	// privacyVersion is the user's newest ACCEPTED Privacy version; the MCP token carries it
	// alongside termsVersion, exactly like a dashboard session.
	privacyVersion func(uuid.UUID) (string, error)
	now            func() time.Time
	// origin is the public origin the metadata document advertises. It reads config per call rather
	// than capturing it, so a test can point the document at its own host.
	origin func() string
}

var oauthDeps = deps{
	store:          GormOAuthStore{},
	getUser:        services.GetUserByID,
	membership:     services.GormMembershipLookup{},
	termsVersion:   services.LatestTermsVersion,
	privacyVersion: services.LatestPrivacyVersion,
	now:            time.Now,
	origin:         func() string { return config.Cfg.OAuthIssuer },
}

// RegisterRoutes mounts the authorization server's public surface: the metadata document, dynamic
// client registration, the token endpoint and revocation. None of these needs a browser session; the
// token endpoint authenticates the client, and /oauth/register is open by design (RFC 7591) while a
// registration can grant nothing on its own — a registration only becomes useful when a signed-in
// human approves it at /oauth/authorize.
func RegisterRoutes(router *mux.Router) {
	router.HandleFunc("/.well-known/oauth-authorization-server", handleMetadata).Methods("GET")
	// RequireJSON on the only endpoint here that takes a JSON body (C11 F-6's second CSRF layer): a
	// cross-site form post cannot mint a client.
	router.HandleFunc("/oauth/register", middleware.RequireJSON(handleRegister)).Methods("POST")
	// token/revoke take application/x-www-form-urlencoded, which RFC 6749 mandates — RequireJSON would
	// reject the only encoding a conforming client sends. They are safe without it: neither is
	// cookie-authenticated, so there is no ambient session for a cross-site request to borrow.
	router.HandleFunc("/oauth/token", handleToken).Methods("POST")
	router.HandleFunc("/oauth/revoke", handleRevoke).Methods("POST")
}

// RegisterAuthorizeRoutes mounts the browser-facing consent endpoints on a router that already
// carries AuthorizeAuthMiddleware.
func RegisterAuthorizeRoutes(router *mux.Router) {
	router.HandleFunc("/oauth/authorize", handleAuthorize).Methods("GET")
	router.HandleFunc("/oauth/authorize", handleAuthorizeDecision).Methods("POST")
}

// AuthorizeAuthMiddleware is the middleware RegisterAuthorizeRoutes expects. It is
// middleware.Auth with one behavioural difference: a request it cannot authenticate is not answered
// with a JSON 401. /oauth/authorize is a page a browser was sent to, so the only useful answer is a
// redirect to sign-in carrying the authorize URL as the return path (contract §4). Everything else —
// token validation, the session-invalidation check, the per-org membership re-check — is the single
// implementation, so this variant cannot drift from the gate every other route uses.
func AuthorizeAuthMiddleware() func(http.Handler) http.Handler {
	return middleware.AuthWithResponder(browserAuthResponder)
}

// browserAuthResponder turns a refused consent navigation into a redirect to the page that fixes it.
// A 503 stays a 503: "the database is unreachable" is not something signing in again resolves, and
// bouncing the user to a login page would hide a real outage.
func browserAuthResponder(w http.ResponseWriter, r *http.Request, status int, body string) {
	if status == http.StatusServiceUnavailable {
		middleware.DefaultUnauthorizedResponder(w, r, status, body)
		return
	}
	// An MCP-scoped token is not a session: refuse it plainly rather than bouncing to a page.
	if status == http.StatusForbidden && strings.Contains(body, middleware.MCPTokenScopeError) {
		middleware.DefaultUnauthorizedResponder(w, r, status, body)
		return
	}

	next := r.URL.RequestURI()
	// A password-change-required session cannot reach the consent screen either (the middleware
	// refuses it everywhere but the change-password endpoint), so send it where it can proceed.
	base := config.Cfg.PublicBaseURL + "/app/login"
	if status == http.StatusForbidden {
		base = config.Cfg.PublicBaseURL + "/app/change-password"
	}
	http.Redirect(w, r, base+"?next="+url.QueryEscape(next), http.StatusFound)
}

// ---------------------------------------------------------------------------
// GET /.well-known/oauth-authorization-server (RFC 8414)
// ---------------------------------------------------------------------------

// metadataDocument is the RFC 8414 document, exactly as contract §4 specifies it. It is a named type
// rather than a map so the field set cannot drift silently: a missing or renamed field is a compile
// error here, and a test asserts the whole decoded shape.
type metadataDocument struct {
	Issuer                            string   `json:"issuer"`
	AuthorizationEndpoint             string   `json:"authorization_endpoint"`
	TokenEndpoint                     string   `json:"token_endpoint"`
	RegistrationEndpoint              string   `json:"registration_endpoint"`
	RevocationEndpoint                string   `json:"revocation_endpoint"`
	ScopesSupported                   []string `json:"scopes_supported"`
	ResponseTypesSupported            []string `json:"response_types_supported"`
	GrantTypesSupported               []string `json:"grant_types_supported"`
	CodeChallengeMethodsSupported     []string `json:"code_challenge_methods_supported"`
	TokenEndpointAuthMethodsSupported []string `json:"token_endpoint_auth_methods_supported"`
}

func handleMetadata(w http.ResponseWriter, r *http.Request) {
	origin := oauthDeps.origin()
	writeJSON(w, http.StatusOK, metadataDocument{
		Issuer:                            origin,
		AuthorizationEndpoint:             origin + "/oauth/authorize",
		TokenEndpoint:                     origin + "/oauth/token",
		RegistrationEndpoint:              origin + "/oauth/register",
		RevocationEndpoint:                origin + "/oauth/revoke",
		ScopesSupported:                   services.SupportedOAuthScopes(),
		ResponseTypesSupported:            []string{"code"},
		GrantTypesSupported:               []string{"authorization_code", "refresh_token"},
		CodeChallengeMethodsSupported:     []string{"S256"},
		TokenEndpointAuthMethodsSupported: []string{"none", "client_secret_post"},
	})
}

// ---------------------------------------------------------------------------
// POST /oauth/register (RFC 7591)
// ---------------------------------------------------------------------------

type registerRequest struct {
	ClientName              string   `json:"client_name"`
	RedirectURIs            []string `json:"redirect_uris"`
	GrantTypes              []string `json:"grant_types"`
	ResponseTypes           []string `json:"response_types"`
	TokenEndpointAuthMethod string   `json:"token_endpoint_auth_method"`
	Scope                   string   `json:"scope"`
}

type registerResponse struct {
	ClientID                string   `json:"client_id"`
	ClientSecret            string   `json:"client_secret,omitempty"`
	ClientIDIssuedAt        int64    `json:"client_id_issued_at"`
	ClientName              string   `json:"client_name"`
	RedirectURIs            []string `json:"redirect_uris"`
	GrantTypes              []string `json:"grant_types"`
	ResponseTypes           []string `json:"response_types"`
	TokenEndpointAuthMethod string   `json:"token_endpoint_auth_method"`
}

var (
	supportedGrantTypes    = []string{"authorization_code", "refresh_token"}
	supportedResponseTypes = []string{"code"}
)

// oauthError writes the RFC 6749 §5.2 error body. error_description is deliberately generic wherever
// a caller could otherwise learn something it did not already know.
func oauthError(w http.ResponseWriter, status int, code, description string) {
	writeJSON(w, status, map[string]string{"error": code, "error_description": description})
}

func writeJSON(w http.ResponseWriter, status int, body any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(body)
}

// handleRegister is RFC 7591 dynamic client registration. Public clients (token_endpoint_auth_method
// "none", PKCE only) are the primary case; a confidential client gets a generated secret, returned
// exactly once and stored only as a hash.
//
// Redirect URIs are matched exactly by /oauth/authorize, never by prefix or wildcard, and the
// registration itself refuses a URI that is not an absolute https URL (or an http loopback address,
// which RFC 8252 blesses for a native app's in-process callback). A registration with no redirect URI
// is refused outright: without one there is nowhere to send a code, and accepting it would create a
// client whose only possible use is a redirect the server would have to invent.
func handleRegister(w http.ResponseWriter, r *http.Request) {
	var req registerRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 64<<10)).Decode(&req); err != nil {
		oauthError(w, http.StatusBadRequest, "invalid_client_metadata", "The registration request body must be a JSON object.")
		return
	}

	redirectURIs, ok := normalizeRedirectURIs(req.RedirectURIs)
	if !ok {
		oauthError(w, http.StatusBadRequest, "invalid_redirect_uri",
			"redirect_uris must be a non-empty list of absolute URLs, https or an http loopback address, without a fragment.")
		return
	}

	grantTypes, ok := normalizeSubset(req.GrantTypes, supportedGrantTypes, []string{"authorization_code", "refresh_token"})
	if !ok {
		oauthError(w, http.StatusBadRequest, "invalid_client_metadata", "Unsupported grant_types.")
		return
	}
	responseTypes, ok := normalizeSubset(req.ResponseTypes, supportedResponseTypes, []string{"code"})
	if !ok {
		oauthError(w, http.StatusBadRequest, "invalid_client_metadata", "Unsupported response_types.")
		return
	}
	// A client that may not use the authorization_code grant can never reach /oauth/authorize, which
	// is the only way this server issues anything; accepting it would register a dead client.
	if !contains(grantTypes, "authorization_code") {
		oauthError(w, http.StatusBadRequest, "invalid_client_metadata",
			"The authorization_code grant type is required: it is the only way this server issues tokens.")
		return
	}

	authMethod := strings.TrimSpace(req.TokenEndpointAuthMethod)
	if authMethod == "" {
		authMethod = "none"
	}
	if authMethod != "none" && authMethod != "client_secret_post" {
		oauthError(w, http.StatusBadRequest, "invalid_client_metadata",
			`token_endpoint_auth_method must be "none" or "client_secret_post".`)
		return
	}

	clientName := strings.TrimSpace(req.ClientName)
	if clientName == "" {
		clientName = defaultClientName
	}
	if len(clientName) > maxClientNameLen {
		clientName = clientName[:maxClientNameLen]
	}

	clientID, err := services.NewOAuthSecret()
	if err != nil {
		log.Printf("oauth register: generate client id: %v", err)
		oauthError(w, http.StatusInternalServerError, "server_error", "Could not register the client.")
		return
	}

	client := &models.OAuthClient{
		ID:                      uuid.New(),
		ClientID:                clientID,
		ClientName:              clientName,
		RedirectURIs:            models.StringList(redirectURIs),
		GrantTypes:              models.StringList(grantTypes),
		ResponseTypes:           models.StringList(responseTypes),
		TokenEndpointAuthMethod: authMethod,
		CreatedAt:               oauthDeps.now().UTC(),
	}

	plainSecret := ""
	if authMethod == "client_secret_post" {
		secret, serr := services.NewOAuthSecret()
		if serr != nil {
			log.Printf("oauth register: generate client secret: %v", serr)
			oauthError(w, http.StatusInternalServerError, "server_error", "Could not register the client.")
			return
		}
		hashed := services.HashOAuthSecret(secret)
		client.ClientSecretHash = &hashed
		plainSecret = secret
	}

	if err := oauthDeps.store.CreateClient(r.Context(), client); err != nil {
		log.Printf("oauth register: persist client: %v", err)
		oauthError(w, http.StatusInternalServerError, "server_error", "Could not register the client.")
		return
	}

	writeJSON(w, http.StatusCreated, registerResponse{
		ClientID:                client.ClientID,
		ClientSecret:            plainSecret,
		ClientIDIssuedAt:        client.CreatedAt.Unix(),
		ClientName:              client.ClientName,
		RedirectURIs:            redirectURIs,
		GrantTypes:              grantTypes,
		ResponseTypes:           responseTypes,
		TokenEndpointAuthMethod: authMethod,
	})
}

// normalizeRedirectURIs validates and deduplicates the registered redirect URIs.
func normalizeRedirectURIs(in []string) ([]string, bool) {
	if len(in) == 0 || len(in) > maxRedirectURIs {
		return nil, false
	}

	seen := make(map[string]bool, len(in))
	out := make([]string, 0, len(in))
	for _, raw := range in {
		uri := strings.TrimSpace(raw)
		if uri == "" || len(uri) > maxRedirectURILen {
			return nil, false
		}
		if !isValidRedirectURI(uri) {
			return nil, false
		}
		if seen[uri] {
			continue
		}
		seen[uri] = true
		out = append(out, uri)
	}
	return out, len(out) > 0
}

// isValidRedirectURI accepts an absolute https URL, or an http URL on a loopback host (RFC 8252's
// native-app redirect). A fragment is refused: RFC 6749 §3.1.2 forbids it, and it would let a client
// hide part of the redirect from the server. A wildcard or prefix is not a thing this function could
// express — matching is a string equality check at authorize time.
func isValidRedirectURI(uri string) bool {
	if strings.Contains(uri, "#") {
		return false
	}
	u, err := url.Parse(uri)
	if err != nil || !u.IsAbs() || u.Host == "" {
		return false
	}
	switch strings.ToLower(u.Scheme) {
	case "https":
		return true
	case "http":
		switch strings.ToLower(u.Hostname()) {
		case "localhost", "127.0.0.1", "::1":
			return true
		}
	}
	return false
}

// normalizeSubset defaults an absent list and refuses any member outside allowed, preserving the
// caller's order.
func normalizeSubset(in, allowed, fallback []string) ([]string, bool) {
	if len(in) == 0 {
		return append([]string(nil), fallback...), true
	}
	out := make([]string, 0, len(in))
	for _, v := range in {
		if !contains(allowed, v) {
			return nil, false
		}
		if !contains(out, v) {
			out = append(out, v)
		}
	}
	return out, true
}

func contains(list []string, v string) bool {
	for _, item := range list {
		if item == v {
			return true
		}
	}
	return false
}

// ---------------------------------------------------------------------------
// GET /oauth/authorize — validate, then render the consent screen
// ---------------------------------------------------------------------------

func handleAuthorize(w http.ResponseWriter, r *http.Request) {
	uc, ok := middleware.GetUserContext(r)
	if !ok {
		// Unreachable through the router (the middleware guarantees the context), but a handler that
		// assumed it and panicked would be an outage rather than a redirect.
		redirectToSignIn(w, r)
		return
	}

	q := r.URL.Query()
	clientID := strings.TrimSpace(q.Get("client_id"))
	redirectURI := q.Get("redirect_uri")
	state := q.Get("state")
	responseType := q.Get("response_type")
	challenge := q.Get("code_challenge")
	method := q.Get("code_challenge_method")
	requestedScope := q.Get("scope")

	client, err := oauthDeps.store.FindClientByClientID(r.Context(), clientID)
	if err != nil {
		log.Printf("oauth authorize: client lookup: %v", err)
		http.Error(w, "authorization server unavailable", http.StatusServiceUnavailable)
		return
	}
	if client == nil {
		// An unknown client has no trustworthy redirect target — redirecting to the one it supplied
		// is exactly the open redirect this endpoint must never be. Answer on this page instead.
		authorizeRefusalPage(w, http.StatusBadRequest,
			"Unknown application", "This application is not registered with ReelBolt. Ask it to register again.")
		return
	}
	if !clientAllowsRedirectURI(client, redirectURI) {
		// Same reasoning: the redirect URI did not match, so it is not a destination we may use.
		authorizeRefusalPage(w, http.StatusBadRequest,
			"Invalid redirect address", "The redirect address this application asked for does not match the one it registered.")
		return
	}

	// From here on the redirect target is trusted, so RFC 6749 §4.1.2.1 says these failures go back
	// to the client as an error redirect rather than being rendered here.
	if !contains(client.GrantTypes, "authorization_code") {
		redirectOAuthError(w, r, redirectURI, "unauthorized_client", "This application may not use the authorization code flow.", state)
		return
	}
	if responseType != "code" {
		redirectOAuthError(w, r, redirectURI, "unsupported_response_type", "Only response_type=code is supported.", state)
		return
	}
	if method != "S256" {
		redirectOAuthError(w, r, redirectURI, "invalid_request", "code_challenge_method must be S256.", state)
		return
	}
	if !services.ValidPKCEChallenge(challenge) {
		redirectOAuthError(w, r, redirectURI, "invalid_request", "code_challenge is required and must be a base64url string of 43 to 128 characters.", state)
		return
	}
	if len(requestedScope) > maxRequestedScopeLen {
		redirectOAuthError(w, r, redirectURI, "invalid_scope", "The requested scope is too long.", state)
		return
	}

	userID, perr := uuid.Parse(uc.UserID)
	if perr != nil {
		log.Printf("oauth authorize: unparseable subject %q in an authenticated context", uc.UserID)
		http.Error(w, "authorization server unavailable", http.StatusServiceUnavailable)
		return
	}
	user, err := oauthDeps.getUser(userID)
	if err != nil || user == nil {
		log.Printf("oauth authorize: load user %s: %v", userID, err)
		http.Error(w, "authorization server unavailable", http.StatusServiceUnavailable)
		return
	}

	// The scopes the consent screen shows, and the only ones the handle can later produce. Admin is
	// offered only when the user has switched it on; anything else the client listed is dropped and
	// never appears as granted.
	granted := services.GrantOAuthScopes(strings.Fields(requestedScope), user.McpAdminToolsEnabled)

	req := consentRequest{
		UserID:              uc.UserID,
		ExpiresAt:           oauthDeps.now().Add(oauthConsentTTL).Unix(),
		ClientID:            client.ClientID,
		ClientName:          client.ClientName,
		RedirectURI:         redirectURI,
		ResponseType:        responseType,
		RequestedScope:      requestedScope,
		GrantedScope:        services.FormatOAuthScopes(granted),
		State:               state,
		CodeChallenge:       challenge,
		CodeChallengeMethod: method,
	}

	sealed, serr := sealConsent(req)
	if serr != nil {
		log.Printf("oauth authorize: seal consent: %v", serr)
		http.Error(w, "authorization server unavailable", http.StatusServiceUnavailable)
		return
	}

	// The token this consent produces is scoped to exactly ONE organization, so the page says which
	// one. Someone who belongs to more than one workspace has to be able to see that they are
	// connecting the right one (docs/design/mcp-oauth.md §3). Best effort: the org id comes from the
	// verified session, so a name we cannot read degrades to a neutral label rather than blocking the
	// authorization.
	org := consentOrg{Name: "Your workspace"}
	if uc.OrganizationID != uuid.Nil {
		m, merr := oauthDeps.membership.GetMembership(r.Context(), userID, uc.OrganizationID)
		switch {
		case merr != nil:
			log.Printf("oauth authorize: membership lookup for %s in %s: %v", userID, uc.OrganizationID, merr)
		case m != nil && m.OrgName != "":
			org = consentOrg{Name: m.OrgName, Kind: m.OrgKind, Role: m.Role}
		}
	}

	renderConsent(w, req, sealed, user.Email, org,
		granted, requestedScopeIncludesAdmin(requestedScope))
}

// clientAllowsRedirectURI is the whole redirect-URI rule: exact string equality against a registered
// value. There is no prefix, suffix, wildcard or normalisation anywhere in this file, and that is the
// point — a prefix rule is how a registered https://app.example.com/cb becomes a code collector for
// https://app.example.com.cb.attacker.test/.
func clientAllowsRedirectURI(client *models.OAuthClient, candidate string) bool {
	if candidate == "" {
		return false
	}
	for _, registered := range client.RedirectURIs {
		if registered == candidate {
			return true
		}
	}
	return false
}

func requestedScopeIncludesAdmin(requested string) bool {
	for _, s := range strings.Fields(requested) {
		if s == services.OAuthScopeAssistantAdmin {
			return true
		}
	}
	return false
}

// redirectToSignIn is the fallback when there is no usable session. It sends the browser to the app's
// login page with the authorize URL as the return path; the web client continues the flow there.
func redirectToSignIn(w http.ResponseWriter, r *http.Request) {
	http.Redirect(w, r,
		config.Cfg.PublicBaseURL+"/app/login?next="+url.QueryEscape(r.URL.RequestURI()),
		http.StatusFound)
}

// redirectOAuthError sends an RFC 6749 error back to the client. state is echoed when the client sent
// one, so the client can match the response to its request.
func redirectOAuthError(w http.ResponseWriter, r *http.Request, redirectURI, code, description, state string) {
	http.Redirect(w, r, appendQuery(redirectURI, url.Values{
		"error":             {code},
		"error_description": {description},
		"state":             stateOrEmpty(state),
	}), http.StatusFound)
}

func stateOrEmpty(state string) []string {
	if state == "" {
		return nil
	}
	return []string{state}
}

// appendQuery merges parameters into a validated redirect URI, preserving anything already in its
// query string (a client may register http://127.0.0.1:7654/callback?slot=1).
func appendQuery(uri string, params url.Values) string {
	for key, values := range params {
		if len(values) == 0 || (len(values) == 1 && values[0] == "") {
			params.Del(key)
		}
	}
	if len(params) == 0 {
		return uri
	}
	u, err := url.Parse(uri)
	if err != nil {
		return uri
	}
	merged := u.Query()
	for key, values := range params {
		merged.Set(key, values[0])
	}
	u.RawQuery = merged.Encode()
	return u.String()
}

// authorizeRefusalPage renders a plain refusal for the failures that must not redirect. It is HTML
// (not JSON) because the caller here is always a browser navigation.
func authorizeRefusalPage(w http.ResponseWriter, status int, title, message string) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Referrer-Policy", "no-referrer")
	w.WriteHeader(status)
	_, _ = w.Write([]byte("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
		"<meta name=\"robots\" content=\"noindex, nofollow\"><title>" + html.EscapeString(title) + " — ReelBolt</title></head>" +
		"<body style=\"font-family:system-ui,sans-serif;background:#0b0b0f;color:#f4f4f5;padding:2.5rem\">" +
		"<h1 style=\"font-size:1.25rem\">" + html.EscapeString(title) + "</h1>" +
		"<p style=\"color:#c9c9d4\">" + html.EscapeString(message) + "</p></body></html>"))
}

// ---------------------------------------------------------------------------
// POST /oauth/authorize — the consent decision
// ---------------------------------------------------------------------------

func handleAuthorizeDecision(w http.ResponseWriter, r *http.Request) {
	uc, ok := middleware.GetUserContext(r)
	if !ok {
		redirectToSignIn(w, r)
		return
	}
	if err := r.ParseForm(); err != nil {
		authorizeRefusalPage(w, http.StatusBadRequest, "Invalid request", "The consent form could not be read.")
		return
	}

	req, ok := openConsent(r.FormValue("consent"), oauthDeps.now())
	if !ok {
		authorizeRefusalPage(w, http.StatusBadRequest, "This request expired",
			"The authorization request is no longer valid. Return to the application and start again.")
		return
	}
	// The handle is bound to the user it was minted for, so approving on one account can never mint a
	// credential for another.
	if req.UserID != uc.UserID {
		authorizeRefusalPage(w, http.StatusForbidden, "Wrong account",
			"This authorization request belongs to a different account.")
		return
	}

	// Re-read the client: it cannot have changed its redirect URIs, but it can have been removed, and
	// a code must never be issued for a client that no longer exists.
	client, err := oauthDeps.store.FindClientByClientID(r.Context(), req.ClientID)
	if err != nil {
		log.Printf("oauth authorize: client lookup: %v", err)
		http.Error(w, "authorization server unavailable", http.StatusServiceUnavailable)
		return
	}
	if client == nil || !clientAllowsRedirectURI(client, req.RedirectURI) {
		authorizeRefusalPage(w, http.StatusBadRequest, "Unknown application",
			"This application is no longer registered with ReelBolt.")
		return
	}

	if r.FormValue("decision") != "allow" {
		redirectOAuthError(w, r, req.RedirectURI, "access_denied", "The user denied the request.", req.State)
		return
	}

	userID, perr := uuid.Parse(uc.UserID)
	if perr != nil {
		http.Error(w, "authorization server unavailable", http.StatusServiceUnavailable)
		return
	}
	user, err := oauthDeps.getUser(userID)
	if err != nil || user == nil {
		log.Printf("oauth authorize: load user %s: %v", userID, err)
		http.Error(w, "authorization server unavailable", http.StatusServiceUnavailable)
		return
	}

	// The granted set is the intersection of what the consent page SHOWED and what the account is
	// entitled to right now. so it can only ever shrink: a page that offered tools only cannot produce
	// an admin token, and an admin opt-in switched off between render and submit does not produce one
	// either. Never the union, which would grant something the user never saw.
	live := services.GrantOAuthScopes(strings.Fields(req.RequestedScope), user.McpAdminToolsEnabled)
	granted := intersectScopes(strings.Fields(req.GrantedScope), live)
	scope := services.FormatOAuthScopes(granted)

	if uc.OrganizationID == uuid.Nil {
		// The code row requires an organization, and more importantly a token with no active workspace
		// could not be attributed to one for metering. Refuse rather than mint one.
		redirectOAuthError(w, r, req.RedirectURI, "access_denied",
			"You do not have an active workspace to authorize this application in.", req.State)
		return
	}

	code, err := services.NewOAuthSecret()
	if err != nil {
		log.Printf("oauth authorize: generate code: %v", err)
		http.Error(w, "authorization server unavailable", http.StatusServiceUnavailable)
		return
	}

	row := &models.OAuthAuthorizationCode{
		ID:             uuid.New(),
		CodeHash:       services.HashOAuthSecret(code),
		ClientID:       client.ClientID,
		UserID:         userID,
		OrganizationID: uc.OrganizationID,
		RedirectURI:    req.RedirectURI,
		CodeChallenge:  req.CodeChallenge,
		Scope:          scope,
		ExpiresAt:      oauthDeps.now().Add(services.OAuthAuthorizationCodeTTL).UTC(),
	}
	if err := oauthDeps.store.CreateCode(r.Context(), row); err != nil {
		log.Printf("oauth authorize: persist code: %v", err)
		http.Error(w, "authorization server unavailable", http.StatusServiceUnavailable)
		return
	}

	http.Redirect(w, r, appendQuery(req.RedirectURI, url.Values{
		"code":  {code},
		"state": stateOrEmpty(req.State),
	}), http.StatusFound)
}

// intersectScopes keeps the members of shown that are also in live, in shown's order.
func intersectScopes(shown, live []string) []string {
	allowed := make(map[string]bool, len(live))
	for _, s := range live {
		allowed[s] = true
	}
	out := make([]string, 0, len(shown))
	for _, s := range shown {
		if allowed[s] {
			out = append(out, s)
		}
	}
	return out
}

// ---------------------------------------------------------------------------
// POST /oauth/token
// ---------------------------------------------------------------------------

type tokenResponse struct {
	AccessToken  string `json:"access_token"`
	TokenType    string `json:"token_type"`
	ExpiresIn    int    `json:"expires_in"`
	RefreshToken string `json:"refresh_token"`
	Scope        string `json:"scope"`
}

// invalidClientBody is one answer for every client-authentication failure — an unknown client id and a
// wrong secret for a known client are byte-identical, so the endpoint is not an existence oracle.
const invalidClientBody = `{"error":"invalid_client","error_description":"Client authentication failed."}`

func handleToken(w http.ResponseWriter, r *http.Request) {
	params, err := tokenParams(r)
	if err != nil {
		oauthError(w, http.StatusBadRequest, "invalid_request", "The token request could not be read.")
		return
	}

	// RFC 6749 §5.1: a token response must not be stored by any intermediary.
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Pragma", "no-cache")

	switch params.Get("grant_type") {
	case "authorization_code":
		exchangeAuthorizationCode(w, r, params)
	case "refresh_token":
		exchangeRefreshToken(w, r, params)
	default:
		oauthError(w, http.StatusBadRequest, "unsupported_grant_type",
			"grant_type must be authorization_code or refresh_token.")
	}
}

// tokenParams reads the token request. RFC 6749 mandates application/x-www-form-urlencoded; a JSON
// body is also accepted because several MCP clients send one and rejecting it would be a
// compatibility failure with no security benefit (this endpoint is not cookie-authenticated).
func tokenParams(r *http.Request) (url.Values, error) {
	if strings.HasPrefix(r.Header.Get("Content-Type"), "application/json") {
		var body map[string]any
		if err := json.NewDecoder(io.LimitReader(r.Body, 64<<10)).Decode(&body); err != nil {
			return nil, err
		}
		values := url.Values{}
		for key, raw := range body {
			if s, ok := raw.(string); ok {
				values.Set(key, s)
			}
		}
		return values, nil
	}
	if err := r.ParseForm(); err != nil {
		return nil, err
	}
	return r.PostForm, nil
}

// authenticateClient resolves the client and, for a confidential one, verifies its secret. It returns
// nil (having already written the response) for any failure, so the callers cannot accidentally
// continue with an unauthenticated client.
func authenticateClient(w http.ResponseWriter, r *http.Request, params url.Values) *models.OAuthClient {
	clientID := strings.TrimSpace(params.Get("client_id"))
	if clientID == "" {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusUnauthorized)
		_, _ = w.Write([]byte(invalidClientBody))
		return nil
	}

	client, err := oauthDeps.store.FindClientByClientID(r.Context(), clientID)
	if err != nil {
		log.Printf("oauth token: client lookup: %v", err)
		oauthError(w, http.StatusInternalServerError, "server_error", "Client lookup failed.")
		return nil
	}
	// Unknown client and wrong secret take the same branch and produce the same bytes.
	if client == nil {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusUnauthorized)
		_, _ = w.Write([]byte(invalidClientBody))
		return nil
	}

	if client.TokenEndpointAuthMethod == "client_secret_post" {
		hashed := ""
		if client.ClientSecretHash != nil {
			hashed = *client.ClientSecretHash
		}
		if !services.VerifyOAuthSecret(hashed, params.Get("client_secret")) {
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusUnauthorized)
			_, _ = w.Write([]byte(invalidClientBody))
			return nil
		}
	}

	return client
}

// exchangeAuthorizationCode redeems a single-use code: exact client match, exact redirect_uri match,
// PKCE S256 verification, and a conditional consume so two concurrent redemptions cannot both win.
func exchangeAuthorizationCode(w http.ResponseWriter, r *http.Request, params url.Values) {
	now := oauthDeps.now()

	client := authenticateClient(w, r, params)
	if client == nil {
		return
	}
	if !contains(client.GrantTypes, "authorization_code") {
		oauthError(w, http.StatusBadRequest, "unauthorized_client", "This client may not use the authorization code grant.")
		return
	}

	code := params.Get("code")
	if code == "" {
		oauthError(w, http.StatusBadRequest, "invalid_grant", "The authorization code is invalid or expired.")
		return
	}

	row, err := oauthDeps.store.FindCodeByHash(r.Context(), services.HashOAuthSecret(code))
	if err != nil {
		log.Printf("oauth token: code lookup: %v", err)
		oauthError(w, http.StatusInternalServerError, "server_error", "Code lookup failed.")
		return
	}
	// One answer for: no such code, another client's code, an expired code, a redirect_uri that does
	// not match, a bad verifier and a replayed code. They differ only in the server's log, never in the
	// response — which is what keeps the endpoint from confirming that a guessed code ever existed.
	if row == nil || row.ClientID != client.ClientID {
		log.Printf("oauth token: refused authorization_code for client %s", client.ClientID)
		oauthError(w, http.StatusBadRequest, "invalid_grant", "The authorization code is invalid or expired.")
		return
	}
	if row.ConsumedAt != nil {
		log.Printf("oauth token: refused REPLAYED authorization code %s for client %s", row.ID, client.ClientID)
		oauthError(w, http.StatusBadRequest, "invalid_grant", "The authorization code is invalid or expired.")
		return
	}
	if now.After(row.ExpiresAt) {
		oauthError(w, http.StatusBadRequest, "invalid_grant", "The authorization code is invalid or expired.")
		return
	}
	if params.Get("redirect_uri") != row.RedirectURI {
		log.Printf("oauth token: redirect_uri mismatch for code %s", row.ID)
		oauthError(w, http.StatusBadRequest, "invalid_grant", "The authorization code is invalid or expired.")
		return
	}
	if !services.VerifyPKCES256(row.CodeChallenge, params.Get("code_verifier")) {
		log.Printf("oauth token: PKCE verification failed for code %s", row.ID)
		oauthError(w, http.StatusBadRequest, "invalid_grant", "The authorization code is invalid or expired.")
		return
	}

	consumed, err := oauthDeps.store.ConsumeCode(r.Context(), row.ID, now.UTC())
	if err != nil {
		log.Printf("oauth token: consume code: %v", err)
		oauthError(w, http.StatusInternalServerError, "server_error", "Code lookup failed.")
		return
	}
	if !consumed {
		// Another request consumed it between the read and the update.
		log.Printf("oauth token: code %s consumed concurrently", row.ID)
		oauthError(w, http.StatusBadRequest, "invalid_grant", "The authorization code is invalid or expired.")
		return
	}

	issueTokens(w, r, client, row.UserID, row.OrganizationID, row.Scope, now, nil)
}

// exchangeRefreshToken rotates a refresh token. Presenting a token that was already rotated is treated
// as compromise and revokes the whole grant (contract §4).
func exchangeRefreshToken(w http.ResponseWriter, r *http.Request, params url.Values) {
	now := oauthDeps.now()

	client := authenticateClient(w, r, params)
	if client == nil {
		return
	}
	if !contains(client.GrantTypes, "refresh_token") {
		oauthError(w, http.StatusBadRequest, "unauthorized_client", "This client may not use the refresh_token grant.")
		return
	}

	raw := params.Get("refresh_token")
	if raw == "" {
		oauthError(w, http.StatusBadRequest, "invalid_grant", "The refresh token is invalid or expired.")
		return
	}

	row, err := oauthDeps.store.FindRefreshTokenByHash(r.Context(), services.HashOAuthSecret(raw))
	if err != nil {
		log.Printf("oauth token: refresh lookup: %v", err)
		oauthError(w, http.StatusInternalServerError, "server_error", "Refresh token lookup failed.")
		return
	}
	if row == nil || row.ClientID != client.ClientID {
		log.Printf("oauth token: refused refresh_token for client %s", client.ClientID)
		oauthError(w, http.StatusBadRequest, "invalid_grant", "The refresh token is invalid or expired.")
		return
	}
	if row.RevokedAt != nil {
		// Reuse of a rotated token. The only safe reading is that the token leaked, so the entire
		// grant dies — every live refresh token for this (client, user) — rather than just this one.
		log.Printf("oauth token: REFRESH REUSE for client %s user %s — revoking the grant", client.ClientID, row.UserID)
		if rerr := oauthDeps.store.RevokeGrant(r.Context(), client.ClientID, row.UserID, now.UTC()); rerr != nil {
			log.Printf("oauth token: revoke reused grant: %v", rerr)
		}
		oauthError(w, http.StatusBadRequest, "invalid_grant", "The refresh token is invalid or expired.")
		return
	}
	if now.After(row.ExpiresAt) {
		oauthError(w, http.StatusBadRequest, "invalid_grant", "The refresh token is invalid or expired.")
		return
	}

	rotated, err := oauthDeps.store.RotateRefreshToken(r.Context(), row.ID, now.UTC())
	if err != nil {
		log.Printf("oauth token: rotate refresh token: %v", err)
		oauthError(w, http.StatusInternalServerError, "server_error", "Refresh token lookup failed.")
		return
	}
	if !rotated {
		// Lost a race with another rotation of the same token: the grant is compromised by the same
		// reasoning as above.
		log.Printf("oauth token: concurrent rotation of refresh token %s — revoking the grant", row.ID)
		if rerr := oauthDeps.store.RevokeGrant(r.Context(), client.ClientID, row.UserID, now.UTC()); rerr != nil {
			log.Printf("oauth token: revoke raced grant: %v", rerr)
		}
		oauthError(w, http.StatusBadRequest, "invalid_grant", "The refresh token is invalid or expired.")
		return
	}

	issueTokens(w, r, client, row.UserID, row.OrganizationID, row.Scope, now, &row.ID)
}

// issueTokens mints the access token and the next refresh token. rotatedFrom is nil for the
// authorization_code grant (a first issue) and the revoked row's id for a rotation.
//
// The membership is re-read at this moment, not carried from consent: a user removed from the
// organization between approving and redeeming has no grant to redeem, and the access token is never
// minted against a workspace they no longer belong to.
func issueTokens(
	w http.ResponseWriter,
	r *http.Request,
	client *models.OAuthClient,
	userID, orgID uuid.UUID,
	scope string,
	now time.Time,
	rotatedFrom *uuid.UUID,
) {
	m, err := oauthDeps.membership.GetMembership(r.Context(), userID, orgID)
	if err != nil {
		log.Printf("oauth token: membership lookup for %s/%s: %v", userID, orgID, err)
		oauthError(w, http.StatusInternalServerError, "server_error", "Membership lookup failed.")
		return
	}
	if m == nil {
		log.Printf("oauth token: user %s is no longer a member of %s", userID, orgID)
		oauthError(w, http.StatusBadRequest, "invalid_grant", "The authorization is no longer valid.")
		return
	}

	user, err := oauthDeps.getUser(userID)
	if err != nil || user == nil {
		log.Printf("oauth token: load user %s: %v", userID, err)
		oauthError(w, http.StatusInternalServerError, "server_error", "User lookup failed.")
		return
	}

	termsVersion := ""
	if oauthDeps.termsVersion != nil {
		v, terr := oauthDeps.termsVersion(userID)
		if terr != nil {
			// A missing terms claim only means the accept-terms gate leaves this session alone; it
			// must not fail a token exchange, exactly as it must not fail a login.
			log.Printf("oauth token: terms version for %s: %v", userID, terr)
		}
		termsVersion = v
	}
	privacyVersion := ""
	if oauthDeps.privacyVersion != nil {
		v, perr := oauthDeps.privacyVersion(userID)
		if perr != nil {
			// Same rule as the terms claim: an absent claim leaves the session alone.
			log.Printf("oauth token: privacy version for %s: %v", userID, perr)
		}
		privacyVersion = v
	}

	accessToken, expiresAt, err := services.GenerateOAuthAccessToken(user, m, scope, termsVersion, privacyVersion, now)
	if err != nil {
		log.Printf("oauth token: mint access token: %v", err)
		oauthError(w, http.StatusInternalServerError, "server_error", "Could not issue a token.")
		return
	}

	refreshToken, err := services.NewOAuthSecret()
	if err != nil {
		log.Printf("oauth token: generate refresh token: %v", err)
		oauthError(w, http.StatusInternalServerError, "server_error", "Could not issue a token.")
		return
	}
	refreshRow := &models.OAuthRefreshToken{
		ID:             uuid.New(),
		TokenHash:      services.HashOAuthSecret(refreshToken),
		ClientID:       client.ClientID,
		UserID:         userID,
		OrganizationID: orgID,
		Scope:          scope,
		ExpiresAt:      now.UTC().Add(services.OAuthRefreshTokenTTL),
		RotatedFromID:  rotatedFrom,
	}
	if err := oauthDeps.store.CreateRefreshToken(r.Context(), refreshRow); err != nil {
		log.Printf("oauth token: persist refresh token: %v", err)
		oauthError(w, http.StatusInternalServerError, "server_error", "Could not issue a token.")
		return
	}

	// Best-effort: a failure to stamp last_used_at must never fail a token that was issued.
	if err := oauthDeps.store.TouchClientUsed(r.Context(), client.ClientID, now.UTC()); err != nil {
		log.Printf("oauth token: stamp client last_used_at: %v", err)
	}

	writeJSON(w, http.StatusOK, tokenResponse{
		AccessToken:  accessToken,
		TokenType:    "Bearer",
		ExpiresIn:    int(expiresAt.Sub(now).Seconds()),
		RefreshToken: refreshToken,
		Scope:        scope,
	})
}

// ---------------------------------------------------------------------------
// POST /oauth/revoke (RFC 7009)
// ---------------------------------------------------------------------------

// handleRevoke revokes a refresh token for the presented client.
//
// It answers 200 to everything, including a malformed request and an unknown client. RFC 7009 §2.2
// says an invalid token must produce 200 precisely so the endpoint is not an oracle; extending that to
// the client check costs an attacker only a failed revocation they could not perform anyway, and
// removes the last corner where a response could confirm that a guessed client id exists.
//
// A JWT access token cannot be revoked — it is signed and stateless, and that is deliberate (the
// access token is the platform's ordinary JWT). Revoking the grant's refresh tokens is what makes the
// credential stop renewing, and the access token then dies on its own within the hour.
func handleRevoke(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseForm(); err != nil {
		w.WriteHeader(http.StatusOK)
		return
	}

	clientID := strings.TrimSpace(r.PostForm.Get("client_id"))
	raw := r.PostForm.Get("token")
	if clientID == "" || raw == "" {
		w.WriteHeader(http.StatusOK)
		return
	}

	row, err := oauthDeps.store.FindRefreshTokenByHash(r.Context(), services.HashOAuthSecret(raw))
	if err != nil {
		// Answering 200 keeps the endpoint uniformly opaque; the failure is visible in the log.
		log.Printf("oauth revoke: token lookup: %v", err)
		w.WriteHeader(http.StatusOK)
		return
	}
	if row == nil || row.ClientID != clientID {
		w.WriteHeader(http.StatusOK)
		return
	}

	if err := oauthDeps.store.RevokeGrant(r.Context(), row.ClientID, row.UserID, oauthDeps.now().UTC()); err != nil {
		log.Printf("oauth revoke: revoke grant: %v", err)
	}
	w.WriteHeader(http.StatusOK)
}
