package auth

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
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
	"github.com/vecchiotom/reelbolt/telemetry"
)

// errOAuthRateLimited is the per-IP account cap refusing a new OAuth account.
var errOAuthRateLimited = errors.New("too many accounts from this network")

// errOAuthAccountUnverified is the refusal C11 F-1 asked for by ruling rather
// than by patch: the provider verified the mailbox, but the account already
// holding that address never verified itself, so it may be one an attacker
// created with the victim's address and a password of their own. Linking it
// would sign the visitor into that account — and, because such an account is
// left on the password path on purpose, keep the attacker's password working.
// The visitor is told to verify the address themselves instead; no account is
// created either, since the unique index on application_users.email does not
// allow a second row for the address.
var errOAuthAccountUnverified = errors.New("existing account has not verified its address")

// errOAuthSignupClosed refuses to CREATE an account on a deployment whose self-serve signup is off.
// Google and GitHub are another way in to the same accounts, not a way around invite-only: with
// SIGNUP_ENABLED unset, an address that already has a verified account still signs in, and an
// address with none is told to ask for an invite, exactly as POST /api/v1/auth/signup answers.
var errOAuthSignupClosed = errors.New("signup is closed and no account exists for this address")

// OAuth sign-in (C10): GET /api/v1/auth/oauth/{provider}/start and .../callback.
//
// Only an address the provider reports as verified can sign in. An account that
// already exists for that address is linked ONLY when that account has verified
// itself: linking an account that never confirmed its address would sign the
// visitor into a row somebody else created and still holds the password to
// (C11 F-1, the ruling). A new account is created already verified and with its
// Personal workspace, and either way a first login with no recorded acceptance
// is sent to /app/accept-terms rather than the dashboard.
//
// The same round trip, when it is started by an authenticated deletion request,
// is also the confirmation path an account with no password has for deleting
// itself (C11 F-8): it re-proves control of the very identity the account was
// created from, which is what re-entering a password proves for the others.
//
// A provider is offered only when its client id and secret are configured;
// otherwise both endpoints answer 404, exactly like a disabled signup. The
// endpoints themselves are package constants: no environment variable can point
// a client secret at another host.

const (
	oauthStateCookie = "reelbolt_oauth_state"
	oauthStateTTL    = 10 * time.Minute
	// oauthHTTPTimeout bounds the token exchange and the user-info calls.
	oauthHTTPTimeout = 10 * time.Second
)

// oauthProvider is one configured provider: where its endpoints live, what to
// ask for, and how to read the answer. The credential is not held here (it is
// read from config per request), and the whole table is a package variable so
// tests can point a provider at a local httptest server.
type oauthProvider struct {
	name        string
	authURL     string
	tokenURL    string
	userInfoURL string
	// emailsURL is GitHub's extra call for the address list; empty for Google,
	// whose user-info response states email_verified directly.
	emailsURL string
	scope     string
	usePKCE   bool
	// decode turns the user-info (and emails, when fetched) responses into an
	// identity. It must fail closed on anything it does not understand.
	decode func(userInfo, emails []byte) (oauthIdentity, error)
}

// oauthIdentity is the only thing taken from a provider.
type oauthIdentity struct {
	Subject       string
	Email         string
	DisplayName   string
	EmailVerified bool
}

var oauthProviders = map[string]*oauthProvider{
	"google": {
		name:        "google",
		authURL:     "https://accounts.google.com/o/oauth2/v2/auth",
		tokenURL:    "https://oauth2.googleapis.com/token",
		userInfoURL: "https://openidconnect.googleapis.com/v1/userinfo",
		scope:       "openid email profile",
		usePKCE:     true,
		decode:      decodeGoogleIdentity,
	},
	"github": {
		name:        "github",
		authURL:     "https://github.com/login/oauth/authorize",
		tokenURL:    "https://github.com/login/oauth/access_token",
		userInfoURL: "https://api.github.com/user",
		emailsURL:   "https://api.github.com/user/emails",
		scope:       "read:user user:email",
		// GitHub OAuth Apps accept the PKCE parameters and ignore the challenge
		// when the app does not use it, so sending them is safe on both arms.
		usePKCE: true,
		decode:  decodeGitHubIdentity,
	},
}

// authProviderName is the value written to application_users.auth_provider.
func (p *oauthProvider) authProviderName() string {
	switch p.name {
	case "google":
		return "Google"
	case "github":
		return "GitHub"
	}
	return p.name
}

// credentials reports the configured client id/secret and whether the provider
// is switched on at all.
func (p *oauthProvider) credentials() (id, secret string, ok bool) {
	switch p.name {
	case "google":
		id, secret = config.Cfg.OAuthGoogleClientID, config.Cfg.OAuthGoogleClientSecret
	case "github":
		id, secret = config.Cfg.OAuthGitHubClientID, config.Cfg.OAuthGitHubClientSecret
	}
	return id, secret, id != "" && secret != ""
}

func (p *oauthProvider) redirectURI() string {
	return config.Cfg.PublicBaseURL + "/api/v1/auth/oauth/" + p.name + "/callback"
}

// ConfiguredOAuthProviders lists, in display order, the providers whose client id and secret are
// both set. It is what the sign-in pages offer a button for, so a button exists exactly when the
// endpoints behind it answer.
func ConfiguredOAuthProviders() []string {
	out := []string{}
	for _, name := range []string{"google", "github"} {
		if p, ok := oauthProviders[name]; ok {
			if _, _, configured := p.credentials(); configured {
				out = append(out, name)
			}
		}
	}
	return out
}

// handleOAuthProviders is GET /api/v1/auth/oauth/providers: the providers this deployment can sign
// in with, and whether a provider sign-in may create a NEW account (it may only when self-serve
// signup is on). Public and credential-free: it names providers, never an id or a secret.
func handleOAuthProviders(w http.ResponseWriter, _ *http.Request) {
	w.Header().Set("Cache-Control", "no-store")
	writeJSON(w, http.StatusOK, map[string]any{
		"providers":      ConfiguredOAuthProviders(),
		"createsAccount": config.Cfg.SignupEnabled,
	})
}

func oauthProviderFrom(name string) (*oauthProvider, bool) {
	p, ok := oauthProviders[strings.ToLower(strings.TrimSpace(name))]
	if !ok {
		return nil, false
	}
	if _, _, configured := p.credentials(); !configured {
		return nil, false
	}
	return p, true
}

// newOAuthState returns the CSRF state and the PKCE code verifier. Both are 32
// random bytes; the verifier is base64url (43 characters), which is the shape
// RFC 7636 requires.
func newOAuthState() (state, verifier string, err error) {
	st := make([]byte, 32)
	if _, err := rand.Read(st); err != nil {
		return "", "", fmt.Errorf("generate oauth state: %w", err)
	}
	vf := make([]byte, 32)
	if _, err := rand.Read(vf); err != nil {
		return "", "", fmt.Errorf("generate pkce verifier: %w", err)
	}
	return hex.EncodeToString(st), base64.RawURLEncoding.EncodeToString(vf), nil
}

func pkceChallenge(verifier string) string {
	sum := sha256.Sum256([]byte(verifier))
	return base64.RawURLEncoding.EncodeToString(sum[:])
}

// handleOAuthStart redirects to the provider and leaves the state cookie the
// callback checks. The cookie carries the provider as well as the state, so a
// response minted for one provider cannot be replayed at another.
//
// It accepts `for` (the landing page's persona), utm_source, utm_medium,
// utm_campaign, utm_content and utm_term, and `lp` (the page of ours the visit
// began on). What survives sanitising rides the state parameter to the callback
// (oauth_state.go); nothing else in the query is read, and none of it can refuse
// the redirect.
func handleOAuthStart(w http.ResponseWriter, r *http.Request) {
	p, ok := oauthProviderFrom(mux.Vars(r)["provider"])
	if !ok {
		http.NotFound(w, r)
		return
	}
	carry := oauthCarryFromQuery(r.URL.Query())
	authorizeURL, err := beginOAuthFlow(w, p, nil, carry)
	if err != nil {
		log.Printf("oauth start: %v", err)
		oauthFail(w, r, "oauth_failed", carry)
		return
	}
	http.Redirect(w, r, authorizeURL, http.StatusFound)
}

// oauthIntent is what a started flow is for: nil for a sign-in, or the account a
// deletion confirmation may delete (C11 F-8).
type oauthIntent struct {
	deleteAccount uuid.UUID
}

// beginOAuthFlow builds the authorization URL and leaves the state cookie the
// callback checks (provider, state, PKCE verifier, and the deletion intent when
// there is one). The intent is only ever set from an authenticated request that
// already re-proved the caller's session — the start endpoint itself is a public
// GET that any cross-site navigation can reach, and a navigation must not be
// able to arm a deletion.
//
// carry is what a sign-in started from a landing page takes through the round
// trip. It goes into the state PARAMETER behind the random part, never into the
// cookie: the cookie keeps the random part alone, which is all the callback
// compares. A deletion confirmation carries nothing.
func beginOAuthFlow(w http.ResponseWriter, p *oauthProvider, intention *oauthIntent, carry signupMarketing) (string, error) {
	clientID, _, _ := p.credentials()

	state, verifier, err := newOAuthState()
	if err != nil {
		return "", err
	}
	value := p.name + ":" + state + ":" + verifier
	if intention != nil {
		value += ":" + oauthIntentDelete + ":" + intention.deleteAccount.String()
	}
	http.SetCookie(w, &http.Cookie{
		Name:     oauthStateCookie,
		Value:    value,
		Path:     "/",
		MaxAge:   int(oauthStateTTL.Seconds()),
		HttpOnly: true,
		Secure:   config.Cfg.CookieSecure,
		SameSite: http.SameSiteLaxMode,
	})

	stateParam := state
	if suffix := encodeOAuthCarry(carry); suffix != "" {
		stateParam += oauthCarrySeparator + suffix
	}
	q := url.Values{
		"client_id":     {clientID},
		"redirect_uri":  {p.redirectURI()},
		"response_type": {"code"},
		"scope":         {p.scope},
		"state":         {stateParam},
	}
	if p.usePKCE {
		q.Set("code_challenge", pkceChallenge(verifier))
		q.Set("code_challenge_method", "S256")
	}
	return p.authURL + "?" + q.Encode(), nil
}

// oauthIntentDelete is the marker in the state cookie that says this flow
// confirms an account deletion rather than signing somebody in.
const oauthIntentDelete = "delete"

// beginProviderConfirmation starts the deletion-confirming round trip for an
// account whose provider is configured here. An empty result means the account's
// provider is not configured in this deployment (Google turned off, say), which
// the caller answers with the password route instead.
func beginProviderConfirmation(w http.ResponseWriter, user *models.ApplicationUser) (providerConfirmationStart, error) {
	p, ok := oauthProviderFrom(user.AuthProvider)
	if !ok {
		return providerConfirmationStart{}, nil
	}
	authorizeURL, err := beginOAuthFlow(w, p, &oauthIntent{deleteAccount: user.ID}, signupMarketing{})
	if err != nil {
		return providerConfirmationStart{}, err
	}
	return providerConfirmationStart{Provider: p.name, URL: authorizeURL}, nil
}

// handleOAuthCallback completes the flow: verify the state, exchange the code,
// read the identity, refuse an unverified address, then link or create.
func handleOAuthCallback(w http.ResponseWriter, r *http.Request) {
	p, ok := oauthProviderFrom(mux.Vars(r)["provider"])
	if !ok {
		http.NotFound(w, r)
		return
	}
	clientID, clientSecret, _ := p.credentials()
	// One answer for every failure from here on: the provider's own error, a
	// missing code, a stale or tampered state. None of them should tell a
	// visitor anything about accounts, and a browser navigation belongs back on
	// the login page rather than on a JSON body.
	clearOAuthStateCookie(w)
	provider := []string{"provider", p.name}
	// Every failure from here on is one auth_event line (T-007); the user is not known yet.
	fail := func(reason string, carry signupMarketing) {
		logAuthEvent(r, "oauth_login", "failure", uuid.Nil, reason, provider...)
		oauthFail(w, r, reason, carry)
	}

	// The state is the random part the cookie also holds, optionally followed by what the start
	// request carried (oauth_state.go). Only the random part is compared, exactly as before; the
	// suffix can never fail the sign-in. It is split off before anything can fail, the provider's
	// own error included (the provider echoes the state on a cancelled sign-in too): the carry is
	// recorded for a new account only, and every failure hands it back to the login page so the
	// email signup that often follows a cancelled provider sign-in still has it (oauthFail).
	state, carrySuffix := splitOAuthState(r.URL.Query().Get("state"))
	carry := decodeOAuthCarry(carrySuffix)
	// A deletion confirmation is not a sign-in from a landing page. Its state is minted with
	// nothing to carry, so whatever follows the random part was not written here: drop it.
	if _, deleting := oauthDeleteIntent(r, p.name, state); deleting {
		carry = signupMarketing{}
	}

	if perr := r.URL.Query().Get("error"); perr != "" {
		log.Printf("oauth %s: provider returned %q", p.name, perr)
		fail("oauth_failed", carry)
		return
	}
	code := strings.TrimSpace(r.URL.Query().Get("code"))
	if state == "" || code == "" || !oauthStateMatches(r, p.name, state) {
		log.Printf("oauth %s: state mismatch or missing code", p.name)
		fail("oauth_failed", carry)
		return
	}
	verifier := oauthVerifier(r, p.name, state)

	ctx, cancel := context.WithTimeout(r.Context(), oauthHTTPTimeout)
	defer cancel()

	token, err := exchangeOAuthCode(ctx, p, clientID, clientSecret, code, verifier)
	if err != nil {
		log.Printf("oauth %s token exchange: %v", p.name, err)
		fail("oauth_failed", carry)
		return
	}
	ident, err := fetchOAuthIdentity(ctx, p, token)
	if err != nil {
		log.Printf("oauth %s identity: %v", p.name, err)
		fail("oauth_failed", carry)
		return
	}
	if !ident.EmailVerified || !validOAuthEmail(ident.Email) {
		log.Printf("oauth %s: provider reported no verified address", p.name)
		fail("oauth_email_unverified", carry)
		return
	}

	// A flow started by an authenticated deletion request is not a sign-in: it
	// exists to re-prove control of the identity the account was created from,
	// and it ends by scheduling the deletion rather than by issuing a session
	// (C11 F-8).
	if deleteAccount, deleting := oauthDeleteIntent(r, p.name, state); deleting {
		handleOAuthDeletionConfirmation(w, r, p, ident, deleteAccount)
		return
	}

	// Link the identity to an existing account by its verified address, or
	// create one. The per-IP account cap is consulted inside, and only for a
	// NEW account: a signed-up user is never locked out of signing in (C5).
	user, err := resolveOAuthUser(w, r, p, ident, carry)
	if err != nil {
		log.Printf("oauth %s: link or create: %v", p.name, err)
		switch {
		case errors.Is(err, errOAuthRateLimited):
			fail("oauth_rate_limited", carry)
		case errors.Is(err, errOAuthSignupClosed):
			fail("oauth_no_account", carry)
		case errors.Is(err, errOAuthAccountUnverified):
			// C11 F-1: the address is taken by an account that has not verified
			// itself. Say so — the visitor has just proved, at the provider, that
			// the mailbox is theirs, so this tells only them anything.
			fail("oauth_account_unverified", carry)
		default:
			fail("oauth_failed", carry)
		}
		return
	}

	// C9b: signing in during the deletion grace period cancels the pending deletion, exactly as the
	// password sign-in does. An account created by a provider has no other way to sign in, so
	// without this its deletion could never be cancelled at all.
	deletionCancelled := cancelPendingDeletion(r, user)

	m, err := acct.resolveMembership(r.Context(), user)
	if err != nil {
		log.Printf("resolve active organization for %s: %v", user.ID, err)
		fail("oauth_no_workspace", carry)
		return
	}
	// The landing page reads ?deletion=cancelled to tell the user their account is staying.
	landingQuery := ""
	if deletionCancelled {
		landingQuery = "?deletion=cancelled"
	}
	// Cookies only: the callback answers with a redirect, so the JSON body
	// writeSession would emit has to be skipped.
	if _, err := issueSession(w, user, m); err != nil {
		log.Printf("oauth %s: issue session for %s: %v", p.name, user.ID, err)
		fail("oauth_failed", carry)
		return
	}
	authSuccess(w, r, "oauth_login", user.ID, provider...)
	recordLoginProductEvent(user.ID, p.name)

	// First login: there is no signup form to accept on, so consent is captured
	// on the accept-terms page, which now records it server-side.
	if sess.termsVersion != nil {
		terms, terr := sess.termsVersion(user.ID)
		if terr != nil {
			log.Printf("terms version lookup for %s: %v", user.ID, terr)
		}
		if terms == "" {
			http.Redirect(w, r, config.Cfg.PublicBaseURL+"/app/accept-terms"+landingQuery, http.StatusFound)
			return
		}
	}
	http.Redirect(w, r, config.Cfg.PublicBaseURL+"/app/dashboard"+landingQuery, http.StatusFound)
}

// resolveOAuthUser links the identity to an existing account by its verified
// email, or creates a new, already-verified account with its own workspace.
// carry is the persona, campaign parameters and landing path the start request
// took through the round trip; it is read in the new-account branch only, so signing in to or
// linking an existing account writes none of it.
func resolveOAuthUser(w http.ResponseWriter, r *http.Request, p *oauthProvider, ident oauthIdentity, carry signupMarketing) (*models.ApplicationUser, error) {
	email := normalizeEmail(ident.Email)
	now := acct.now().UTC()

	// A service-account address (API keys) is never linked to and never created from a provider.
	if strings.HasSuffix(email, models.ServiceAccountEmailDomain) {
		return nil, errors.New("reserved email domain")
	}

	existing, err := acct.store.FindUserByEmail(email)
	if err != nil {
		return nil, err
	}
	if existing != nil {
		if models.IsServiceAccount(existing) {
			// Defence in depth (the store already hides them): never link a provider identity to one.
			return nil, errors.New("service account")
		}
		// C11 F-1, as ruled: an account that has never verified its own address is
		// NOT linked. The provider proved the mailbox, but the row the visitor
		// would land in is one somebody else may have created from that address —
		// with a password of their choosing and, because the account keeps its
		// origin, a password that keeps working. Signing the visitor in there
		// would hand them everything the visitor then puts in "their" workspace.
		// They are told to verify the address themselves (or reset the password,
		// which proves the same mailbox) and come back; no second account is made,
		// and none could be: application_users.email is unique.
		if existing.EmailVerifiedAt == nil {
			return nil, errOAuthAccountUnverified
		}
		// A verified account is linked: the provider proved the same mailbox that
		// account already proved. The provider column keeps the account's origin,
		// and nothing about the account is rewritten.
		return existing, nil
	}

	if !config.Cfg.SignupEnabled {
		return nil, errOAuthSignupClosed
	}

	if ipp := clientIP(r); ipp != nil && !middleware.NewSignupIPLimiter(acct.store).Allow(*ipp, now) {
		return nil, errOAuthRateLimited
	}

	// A real, unusable bcrypt hash: a fresh random value no user can type, so
	// the login path does the same ~55 ms of work for this account as for a
	// password account. An empty hash would make bcrypt return before any work
	// and answer in nanoseconds — a timing oracle for (a) whether the address
	// exists and (b) that it is OAuth-only (C11 F-3). It also means the
	// account-delete re-verification (C9b) still answers with a real compare.
	placeholderHash, err := services.GeneratePlaceholderPasswordHash()
	if err != nil {
		return nil, fmt.Errorf("placeholder password hash: %w", err)
	}

	user := &models.ApplicationUser{
		ID:              uuid.New(),
		Email:           email,
		DisplayName:     oauthDisplayName(ident, email),
		CreatedAt:       now,
		PasswordHash:    placeholderHash,
		EmailVerifiedAt: &now,
		SignupIP:        clientIP(r),
		AuthProvider:    p.authProviderName(),
		Locale:          requestLocalePtr(r),
	}
	if err := acct.store.RegisterOAuthUser(user); err != nil {
		// Two callbacks for the same new address race on the unique index; the
		// loser signs in to the account the winner created.
		again, ferr := acct.store.FindUserByEmail(email)
		if ferr != nil || again == nil {
			return nil, err
		}
		return again, nil
	}
	// alpha-launch T-018: a first OAuth sign-in creates the account, so it is the same funnel step
	// as a password signup. Reporting only, and never on the race-loser branch above.
	recordFunnelEvent(r.Context(), telemetry.FunnelEventSignup)
	recordSignupProductEvent(user.ID, user.LastActiveOrganizationID)
	// Same persona and attribution rules as a password signup. The persona is written before the
	// redirect, because the page the new session lands on reads it.
	recordSignupPersona(user.ID, carry.persona, "")
	recordSignupAttribution(w, r, user.ID, carry.carried)
	return user, nil
}

func oauthDisplayName(ident oauthIdentity, email string) string {
	name := strings.TrimSpace(ident.DisplayName)
	if name == "" {
		name = strings.SplitN(email, "@", 2)[0]
	}
	if len(name) > 100 {
		name = name[:100]
	}
	return name
}

// validOAuthEmail is deliberately loose: the provider is the authority on the
// address, and this only rejects something that is not an address at all.
func validOAuthEmail(email string) bool {
	_, err := services.ValidateRecipient(normalizeEmail(email))
	return err == nil
}

func clearOAuthStateCookie(w http.ResponseWriter) {
	http.SetCookie(w, &http.Cookie{
		Name:     oauthStateCookie,
		Value:    "",
		Path:     "/",
		MaxAge:   -1,
		HttpOnly: true,
		Secure:   config.Cfg.CookieSecure,
		SameSite: http.SameSiteLaxMode,
	})
}

// oauthStateParts splits the state cookie: provider, state, PKCE verifier, and —
// on a deletion-confirming flow (C11 F-8) — the intent marker and the account id.
func oauthStateParts(r *http.Request) []string {
	c, err := r.Cookie(oauthStateCookie)
	if err != nil || c.Value == "" {
		return nil
	}
	parts := strings.SplitN(c.Value, ":", 5)
	if len(parts) < 3 || parts[0] == "" || parts[1] == "" {
		return nil
	}
	return parts
}

// oauthStateMatches compares the state in the URL with the one in the cookie in
// constant time, and checks the cookie belongs to this provider.
func oauthStateMatches(r *http.Request, provider, state string) bool {
	parts := oauthStateParts(r)
	if parts == nil || parts[0] != provider {
		return false
	}
	return subtle.ConstantTimeCompare([]byte(parts[1]), []byte(state)) == 1
}

// oauthVerifier returns the PKCE verifier stored alongside the state, or "".
func oauthVerifier(r *http.Request, provider, state string) string {
	parts := oauthStateParts(r)
	if parts == nil || parts[0] != provider {
		return ""
	}
	return parts[2]
}

// oauthDeleteIntent reports the account a started flow is allowed to delete, and
// whether the flow is a deletion confirmation at all. The state in the URL must
// match the cookie's, exactly as for a sign-in: the marker on its own proves
// nothing.
func oauthDeleteIntent(r *http.Request, provider, state string) (uuid.UUID, bool) {
	parts := oauthStateParts(r)
	if parts == nil || len(parts) != 5 || parts[0] != provider || parts[3] != oauthIntentDelete {
		return uuid.Nil, false
	}
	if subtle.ConstantTimeCompare([]byte(parts[1]), []byte(state)) != 1 {
		return uuid.Nil, false
	}
	id, err := uuid.Parse(parts[4])
	if err != nil {
		return uuid.Nil, false
	}
	return id, true
}

// handleOAuthDeletionConfirmation finishes a deletion-confirming round trip
// (C11 F-8). The provider has just authenticated the identity the account is
// built on, which is the alternative to re-entering a password: an account
// created by Google or GitHub has no password to re-enter, and a session alone
// is not proof of control for an irreversible action. The identity's verified
// address must be the account's own, so a stolen session cannot talk the flow
// into deleting somebody else's account.
func handleOAuthDeletionConfirmation(w http.ResponseWriter, r *http.Request, p *oauthProvider, ident oauthIdentity, userID uuid.UUID) {
	user, err := deletion.getUser(userID)
	if err != nil || user == nil {
		log.Printf("oauth %s: deletion confirmation for an unknown account %s: %v", p.name, userID, err)
		oauthFail(w, r, "oauth_failed", signupMarketing{})
		return
	}
	if normalizeEmail(user.Email) != normalizeEmail(ident.Email) {
		log.Printf("oauth %s: deletion confirmation refused for %s: the provider authenticated a different address", p.name, userID)
		oauthFail(w, r, "oauth_failed", signupMarketing{})
		return
	}

	purgeDate, err := scheduleAccountDeletion(w, r, user)
	if err != nil {
		var blocked *soleOwnerError
		if errors.As(err, &blocked) {
			log.Printf("oauth %s: deletion confirmation for %s refused: sole owner of %s", p.name, userID, blocked.organization)
			oauthFail(w, r, "oauth_delete_blocked", signupMarketing{})
			return
		}
		log.Printf("oauth %s: deletion request for %s: %v", p.name, userID, err)
		oauthFail(w, r, "oauth_failed", signupMarketing{})
		return
	}
	log.Printf("oauth %s: account %s confirmed its own deletion", p.name, userID)
	// The date travels with the redirect so the sign-in page can name it, as the JSON path's
	// deletionDate does.
	http.Redirect(w, r, config.Cfg.PublicBaseURL+"/app/login?deletion=scheduled&date="+
		url.QueryEscape(purgeDate.Format(time.RFC3339)), http.StatusFound)
}

// oauthFail sends the browser back to the web login page with a reason code it
// can show. The reason set is fixed, and every code in it is reachable only by
// somebody who has already proved, at the provider, that they hold the mailbox
// in question: none of them tells a stranger whether an address has an account.
// oauth_account_unverified (C11 F-1) is the one that names a state of an account,
// and it is answered only to the person who just authenticated that address.
//
// carry is what the sign-in took through the round trip (the landing page's persona, the campaign
// parameters and the landing path). The login page gets it back as `for`, utm_* and `lp` next to
// `error`, sanitised (oauthCarryLoginQuery), so the person can still sign up another way without
// losing it. A deletion confirmation passes an empty carry.
func oauthFail(w http.ResponseWriter, r *http.Request, reason string, carry signupMarketing) {
	q := url.Values{"error": {reason}}
	oauthCarryLoginQuery(q, carry)
	// Encode sorts by key, so `error` stays first and an empty carry gives the URL it always was.
	http.Redirect(w, r, config.Cfg.PublicBaseURL+"/app/login?"+q.Encode(), http.StatusFound)
}

// exchangeOAuthCode trades the authorization code for an access token. A
// provider that answers 200 with an {"error": ...} body (GitHub does) is a
// failure like any other.
func exchangeOAuthCode(ctx context.Context, p *oauthProvider, clientID, clientSecret, code, verifier string) (string, error) {
	form := url.Values{
		"client_id":     {clientID},
		"client_secret": {clientSecret},
		"code":          {code},
		"grant_type":    {"authorization_code"},
		"redirect_uri":  {p.redirectURI()},
	}
	if p.usePKCE && verifier != "" {
		form.Set("code_verifier", verifier)
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, p.tokenURL, strings.NewReader(form.Encode()))
	if err != nil {
		return "", err
	}
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	req.Header.Set("Accept", "application/json")

	var body oauthTokenResponse
	if err := oauthDoJSON(req, &body); err != nil {
		return "", err
	}
	if body.Error != "" {
		return "", fmt.Errorf("token endpoint: %s", body.Error)
	}
	if body.AccessToken == "" {
		return "", fmt.Errorf("token endpoint returned no access token")
	}
	return body.AccessToken, nil
}

type oauthTokenResponse struct {
	AccessToken string `json:"access_token"`
	TokenType   string `json:"token_type"`
	Error       string `json:"error"`
	ErrorDesc   string `json:"error_description"`
}

// fetchOAuthIdentity calls the provider's user-info endpoint, plus GitHub's
// emails endpoint, and hands both bodies to the provider's decoder.
func fetchOAuthIdentity(ctx context.Context, p *oauthProvider, accessToken string) (oauthIdentity, error) {
	userInfo, err := oauthGet(ctx, p.userInfoURL, accessToken)
	if err != nil {
		return oauthIdentity{}, err
	}
	emails := []byte(nil)
	if p.emailsURL != "" {
		if emails, err = oauthGet(ctx, p.emailsURL, accessToken); err != nil {
			return oauthIdentity{}, err
		}
	}
	return p.decode(userInfo, emails)
}

func oauthGet(ctx context.Context, url, accessToken string) ([]byte, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Authorization", "Bearer "+accessToken)
	req.Header.Set("Accept", "application/json")

	resp, err := oauthHTTPClient().Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	b, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		return nil, err
	}
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("user info: status %d", resp.StatusCode)
	}
	return b, nil
}

func oauthDoJSON(req *http.Request, v any) error {
	resp, err := oauthHTTPClient().Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	b, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		return err
	}
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("status %d", resp.StatusCode)
	}
	if err := json.Unmarshal(b, v); err != nil {
		return fmt.Errorf("decode response: %w", err)
	}
	return nil
}

// oauthHTTPClient is a package variable so tests can keep the transport.
var oauthHTTPClient = func() *http.Client {
	return &http.Client{Timeout: oauthHTTPTimeout}
}

// decodeGoogleIdentity reads OpenID Connect userinfo. email_verified is
// required to be exactly true; anything else (missing, a string, false) fails
// closed.
func decodeGoogleIdentity(userInfo, _ []byte) (oauthIdentity, error) {
	var v struct {
		Sub           string `json:"sub"`
		Email         string `json:"email"`
		EmailVerified bool   `json:"email_verified"`
		Name          string `json:"name"`
	}
	if err := json.Unmarshal(userInfo, &v); err != nil {
		return oauthIdentity{}, fmt.Errorf("decode google userinfo: %w", err)
	}
	return oauthIdentity{
		Subject:       v.Sub,
		Email:         v.Email,
		DisplayName:   v.Name,
		EmailVerified: v.EmailVerified,
	}, nil
}

// decodeGitHubIdentity reads /user plus /user/emails. GitHub's /user may carry
// a null email and says nothing about verification, so the address is taken
// from the email list and only if that entry is verified.
func decodeGitHubIdentity(userInfo, emails []byte) (oauthIdentity, error) {
	var u struct {
		ID    int64  `json:"id"`
		Login string `json:"login"`
		Name  string `json:"name"`
	}
	if err := json.Unmarshal(userInfo, &u); err != nil {
		return oauthIdentity{}, fmt.Errorf("decode github user: %w", err)
	}
	var list []struct {
		Email    string `json:"email"`
		Primary  bool   `json:"primary"`
		Verified bool   `json:"verified"`
	}
	if len(emails) > 0 {
		if err := json.Unmarshal(emails, &list); err != nil {
			return oauthIdentity{}, fmt.Errorf("decode github emails: %w", err)
		}
	}

	ident := oauthIdentity{Subject: fmt.Sprintf("%d", u.ID), DisplayName: u.Name}
	for _, e := range list {
		if !e.Verified {
			continue
		}
		if e.Primary {
			return oauthIdentity{Subject: ident.Subject, DisplayName: ident.DisplayName, Email: e.Email, EmailVerified: true}, nil
		}
		if ident.Email == "" {
			ident.Email = e.Email
			ident.EmailVerified = true
		}
	}
	return ident, nil
}
