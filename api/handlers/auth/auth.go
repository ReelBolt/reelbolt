package auth

import (
	"context"
	"encoding/json"
	"errors"
	"log"
	"net/http"
	"net/url"
	"strconv"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
	"golang.org/x/crypto/bcrypt"
)

// dummyPasswordHash is a bcrypt hash (cost 10, matching services.ValidatePassword)
// of a fixed, unused string. It is compared against on the "user not found" path
// in handleToken so response timing does not reveal whether an email is registered.
const dummyPasswordHash = "$2a$10$tcmcRV6q2Y9.pdr6kt44bOGqK6HzH4MRqhVGPVl322/WyrGWM7etC"

// RegisterAuthRoutes mounts the public account endpoints. Every POST here takes
// a JSON body, so each is wrapped in middleware.RequireJSON: a cross-site
// `fetch` can send text/plain without a CORS preflight, application/json it
// cannot, and that is the second CSRF layer next to the SameSite=Lax session
// cookies (C11 F-6). The two GETs below carry no body and are left alone.
func RegisterAuthRoutes(router *mux.Router) {
	router.HandleFunc("/api/v1/auth/token", middleware.RequireJSON(handleToken)).Methods("POST")
	router.HandleFunc("/api/v1/auth/signup", middleware.RequireJSON(handleSignup)).Methods("POST")
	router.HandleFunc("/api/v1/auth/verify-email", middleware.RequireJSON(handleVerifyEmail)).Methods("POST")
	router.HandleFunc("/api/v1/auth/resend-verification", middleware.RequireJSON(handleResendVerification)).Methods("POST")
	router.HandleFunc("/api/v1/auth/forgot-password", middleware.RequireJSON(handleForgotPassword)).Methods("POST")
	router.HandleFunc("/api/v1/auth/reset-password", middleware.RequireJSON(handleResetPassword)).Methods("POST")
	router.HandleFunc("/api/v1/auth/invitations/{token}", handleInvitationPreview).Methods("GET")
	// Admin-requested email change (issue #1207): the token only ever travels in the JSON body.
	router.HandleFunc("/api/v1/auth/email-change/preview", middleware.RequireJSON(handleEmailChangePreview)).Methods("POST")
	router.HandleFunc("/api/v1/auth/email-change/confirm", middleware.RequireJSON(handleEmailChangeConfirm)).Methods("POST")
	router.HandleFunc("/api/v1/auth/email-change/cancel", middleware.RequireJSON(handleEmailChangeCancel)).Methods("POST")
	// OAuth sign-in (C10). Public, like the rest of the account endpoints: the
	// visitor has no session yet. Both answer 404 for a provider that is not
	// configured.
	router.HandleFunc("/api/v1/auth/oauth/providers", handleOAuthProviders).Methods("GET")
	router.HandleFunc("/api/v1/auth/oauth/{provider}/start", handleOAuthStart).Methods("GET")
	router.HandleFunc("/api/v1/auth/oauth/{provider}/callback", handleOAuthCallback).Methods("GET")
}

// RegisterAuthenticatedRoutes mounts the state-changing account endpoints. Every
// one takes a JSON body and is wrapped in middleware.RequireJSON (C11 F-6); the
// middleware sits in front of the handler and behind middleware.Auth, which the
// caller's router applies. The one GET (the caller's settings) carries no body
// and is left alone.
func RegisterAuthenticatedRoutes(router *mux.Router) {
	router.HandleFunc("/api/v1/auth/change-password", middleware.RequireJSON(handleChangePassword)).Methods("POST")
	// Records a legal acceptance for the signed-in caller (C10).
	router.HandleFunc("/api/v1/auth/accept-terms", middleware.RequireJSON(handleAcceptTerms)).Methods("POST")

	router.HandleFunc("/api/v1/auth/account/delete", middleware.RequireJSON(handleAccountDelete)).Methods("POST")
	router.HandleFunc("/api/v1/auth/switch-org", middleware.RequireJSON(handleSwitchOrg)).Methods("POST")
	router.HandleFunc("/api/v1/auth/invitations/{token}/accept", middleware.RequireJSON(handleInvitationAccept)).Methods("POST")

	// The caller's own settings (docs/design/mcp-oauth.md §11). Self only: both handlers read the user
	// from the session and neither accepts an id, so there is no way to name somebody else's setting.
	router.HandleFunc("/api/v1/auth/settings", handleGetSettings).Methods("GET")
	router.HandleFunc("/api/v1/auth/settings", middleware.RequireJSON(handlePatchSettings)).Methods("PATCH")
	// Cancels the caller's own pending email change (issue #1207). No body.
	router.HandleFunc("/api/v1/auth/email-change", handleEmailChangeCancelSelf).Methods("DELETE")
}

// logAuthEvent writes the one structured line a sign-in, switch-org or password change/reset
// leaves (forensics, T-007): event, outcome (success|failure), the user id ("-" when not known),
// the client ip as the rate limiter sees it, and a fixed reason word on failure. Never an
// address, a password or a token. extra is an optional key=value pair (provider, org): key is a
// constant, value is quoted. Every value is quoted (strconv.Quote) so no client-influenced byte
// (CR, LF, space) can forge a field or a whole line; event and outcome are constants.
func logAuthEvent(r *http.Request, event, outcome string, userID uuid.UUID, reason string, extra ...string) {
	user := "-"
	if userID != uuid.Nil {
		user = userID.String()
	}
	ip := middleware.ClientIP(r)
	if ip == "" {
		ip = "-"
	}
	line := "auth_event event=" + event + " outcome=" + outcome + " user=" + strconv.Quote(user) + " ip=" + strconv.Quote(ip)
	if reason != "" {
		line += " reason=" + strconv.Quote(reason)
	}
	if len(extra) == 2 {
		line += " " + extra[0] + "=" + strconv.Quote(extra[1])
	}
	log.Print(line)
}

// authSuccess logs a successful event and marks the response with the user id for nginx's log.
func authSuccess(w http.ResponseWriter, r *http.Request, event string, userID uuid.UUID, extra ...string) {
	middleware.SetUserIDHeader(w, userID.String())
	logAuthEvent(r, event, "success", userID, "", extra...)
}

func handleToken(w http.ResponseWriter, r *http.Request) {
	var req TokenRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&req); err != nil {
		logAuthEvent(r, "login", "failure", uuid.Nil, "bad_request")
		http.Error(w, `{"error":"invalid request body"}`, http.StatusBadRequest)
		return
	}

	if req.Email == "" || req.Password == "" {
		logAuthEvent(r, "login", "failure", uuid.Nil, "bad_request")
		http.Error(w, `{"error":"email and password are required"}`, http.StatusBadRequest)
		return
	}

	user, err := services.GetUserByEmail(req.Email)
	failedID, reason := uuid.Nil, "unknown_account"
	if err == nil && models.IsServiceAccount(user) {
		// A service account (API key) never signs in: same 401 and the same bcrypt work as a
		// wrong password, so neither the body nor the timing discloses it exists.
		err = errors.New("service account")
		failedID, reason = user.ID, "service_account"
	}
	if err != nil {
		// Perform a dummy bcrypt comparison so the response timing for an unknown
		// email matches the timing for a known email with a wrong password.
		_ = bcrypt.CompareHashAndPassword([]byte(dummyPasswordHash), []byte(req.Password))
		logAuthEvent(r, "login", "failure", failedID, reason)
		http.Error(w, `{"error":"invalid credentials"}`, http.StatusUnauthorized)
		return
	}

	if !services.ValidatePassword(user, req.Password) {
		logAuthEvent(r, "login", "failure", user.ID, "bad_password")
		http.Error(w, `{"error":"invalid credentials"}`, http.StatusUnauthorized)
		return
	}

	finishPasswordLogin(w, r, user)
}

// finishPasswordLogin is the half of a password sign-in that runs once the password has been
// checked: it cancels a pending deletion, resolves the active workspace and writes the session.
//
// C9b: signing in during the deletion grace period cancels the pending deletion (C0's state
// machine). The flag is cleared before the session is issued, so the user comes back to a working
// account rather than to a workspace that is scheduled to disappear, and the response says so
// (deletionCancelled) so the sign-in page can tell them their account is no longer leaving.
func finishPasswordLogin(w http.ResponseWriter, r *http.Request, user *models.ApplicationUser) {
	cancelled := cancelPendingDeletion(r, user)

	m, err := acct.resolveMembership(r.Context(), user)
	if err != nil {
		log.Printf("resolve active organization for %s: %v", user.ID, err)
		logAuthEvent(r, "login", "failure", user.ID, "no_workspace")
		http.Error(w, `{"error":"no workspace available for this account"}`, http.StatusForbidden)
		return
	}

	resp, err := issueSession(w, user, m)
	if err != nil {
		logAuthEvent(r, "login", "failure", user.ID, "session_error")
		http.Error(w, `{"error":"failed to generate token"}`, http.StatusInternalServerError)
		return
	}
	authSuccess(w, r, "login", user.ID)
	resp.DeletionCancelled = cancelled
	recordLoginProductEvent(user.ID, "password")
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(resp)
}

// sessionDeps are the collaborators of writeSession, replaceable in tests.
// termsVersion is the user's newest accepted Terms version ("" when they have
// accepted none); a read failure is logged and treated as "" rather than
// failing the login. privacyVersion is the same for the Privacy Policy: the
// newest ACCEPTED version, never the served one.
type sessionDeps struct {
	termsVersion   func(userID uuid.UUID) (string, error)
	privacyVersion func(userID uuid.UUID) (string, error)
}

var sess = sessionDeps{termsVersion: services.LatestTermsVersion, privacyVersion: services.LatestPrivacyVersion}

// writeSession issues a token for (user, active membership), sets the session
// cookies and writes the token response. Shared by login, switch-org,
// verification, invite acceptance and every re-issue.
func writeSession(w http.ResponseWriter, user *models.ApplicationUser, m *services.ActiveMembership) bool {
	resp, err := issueSession(w, user, m)
	if err != nil {
		http.Error(w, `{"error":"failed to generate token"}`, http.StatusInternalServerError)
		return false
	}
	middleware.SetUserIDHeader(w, user.ID.String())
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(resp)
	return true
}

// issueSession is writeSession without the response body: it issues the token,
// sets both cookies and returns the payload the JSON response would carry. The
// OAuth callback (C10) uses it because it answers with a redirect, and a
// handler that has already written a 200 cannot redirect afterwards.
func issueSession(w http.ResponseWriter, user *models.ApplicationUser, m *services.ActiveMembership) (TokenResponse, error) {
	if models.IsServiceAccount(user) {
		return TokenResponse{}, errors.New("service accounts never get a session")
	}
	termsVersion := ""
	if sess.termsVersion != nil {
		v, err := sess.termsVersion(user.ID)
		if err != nil {
			// A session with no terms claim is a session the accept-terms gate
			// leaves alone (see web/proxy.ts); a read error must not fail a login.
			log.Printf("terms version lookup for %s: %v", user.ID, err)
		}
		termsVersion = v
	}
	privacyVersion := ""
	if sess.privacyVersion != nil {
		v, err := sess.privacyVersion(user.ID)
		if err != nil {
			// Same rule as the terms claim: absent means the gate leaves it alone.
			log.Printf("privacy version lookup for %s: %v", user.ID, err)
		}
		privacyVersion = v
	}

	token, err := services.GenerateTokenWithLegal(user, m, termsVersion, privacyVersion)
	if err != nil {
		return TokenResponse{}, err
	}

	// Set httpOnly cookie with the JWT token
	http.SetCookie(w, &http.Cookie{
		Name:     "reelbolt_token",
		Value:    token,
		Path:     "/",
		MaxAge:   86400,
		HttpOnly: true,
		Secure:   config.Cfg.CookieSecure,
		SameSite: http.SameSiteLaxMode,
	})

	// Set readable cookie with user info for frontend UI state
	http.SetCookie(w, &http.Cookie{
		Name:     "reelbolt_user",
		Value:    url.QueryEscape(userCookieValue(user, m, termsVersion, privacyVersion)),
		Path:     "/",
		MaxAge:   86400,
		HttpOnly: false,
		Secure:   config.Cfg.CookieSecure,
		SameSite: http.SameSiteLaxMode,
	})

	return TokenResponse{
		AccessToken:        token,
		TokenType:          "Bearer",
		ExpiresIn:          86400,
		Email:              user.Email,
		DisplayName:        user.DisplayName,
		Locale:             services.UserLocale(user.Locale),
		IsAdmin:            user.IsAdmin,
		IsPlatformAdmin:    user.IsAdmin,
		MustChangePassword: user.MustChangePassword,
		EmailVerified:      user.EmailVerifiedAt != nil,
		Org:                m.OrgID.String(),
		OrgName:            m.OrgName,
		OrgKind:            m.OrgKind,
		OrgRole:            m.Role,
		TermsVersion:       termsVersion,
		PrivacyVersion:     privacyVersion,
	}, nil
}

// userCookieValue is the JSON in the readable reelbolt_user cookie (UI state
// only, never trusted). isAdmin stays as the alias of isPlatformAdmin.
// termsVersion is the accepted Terms version, omitted when there is none: the
// proxy gate reads an absent field as "this session predates the gate" and an
// empty string would read as "stale" (C7-UI; the field is what makes
// web/proxy.ts's redirect live). privacyVersion follows the same rule.
func userCookieValue(user *models.ApplicationUser, m *services.ActiveMembership, termsVersion, privacyVersion string) string {
	ui := map[string]any{
		"email":              user.Email,
		"displayName":        user.DisplayName,
		"isAdmin":            user.IsAdmin,
		"isPlatformAdmin":    user.IsAdmin,
		"mustChangePassword": user.MustChangePassword,
		"emailVerified":      user.EmailVerifiedAt != nil,
		"org":                m.OrgID.String(),
		"orgName":            m.OrgName,
		"orgKind":            m.OrgKind,
		"orgRole":            m.Role,
	}
	if loc := services.UserLocale(user.Locale); loc != "" {
		ui["locale"] = loc
	}
	if termsVersion != "" {
		ui["termsVersion"] = termsVersion
	}
	if privacyVersion != "" {
		ui["privacyVersion"] = privacyVersion
	}
	b, _ := json.Marshal(ui)
	return string(b)
}

// switchDeps are the collaborators of handleSwitchOrg, replaceable in tests.
type switchDeps struct {
	lookup        services.MembershipLookup
	getUser       func(uuid.UUID) (*models.ApplicationUser, error)
	setLastActive func(ctx context.Context, userID, orgID uuid.UUID) error
}

var deps = switchDeps{
	lookup:        services.GormMembershipLookup{},
	getUser:       services.GetUserByID,
	setLastActive: services.SetLastActiveOrganization,
}

// handleSwitchOrg re-issues the session for another organization the caller
// belongs to. A non-member (or unknown/Platform org) gets 404, never 403, so
// organization existence does not leak (decision D5).
func handleSwitchOrg(w http.ResponseWriter, r *http.Request) {
	uc, ok := middleware.GetUserContext(r)
	if !ok {
		logAuthEvent(r, "switch_org", "failure", uuid.Nil, "unauthenticated")
		http.Error(w, `{"error":"unauthorized"}`, http.StatusUnauthorized)
		return
	}

	var req SwitchOrgRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&req); err != nil {
		http.Error(w, `{"error":"invalid request body"}`, http.StatusBadRequest)
		return
	}
	orgID, err := uuid.Parse(req.OrganizationID)
	if err != nil {
		http.Error(w, `{"error":"organizationId must be a uuid"}`, http.StatusBadRequest)
		return
	}
	userID, err := uuid.Parse(uc.UserID)
	if err != nil {
		logAuthEvent(r, "switch_org", "failure", uuid.Nil, "bad_user_id")
		http.Error(w, `{"error":"invalid user ID"}`, http.StatusInternalServerError)
		return
	}
	org := []string{"org", orgID.String()}

	m, err := deps.lookup.GetMembership(r.Context(), userID, orgID)
	if err != nil {
		log.Printf("switch-org membership lookup: %v", err)
		logAuthEvent(r, "switch_org", "failure", userID, "lookup_error", org...)
		http.Error(w, `{"error":"service unavailable"}`, http.StatusServiceUnavailable)
		return
	}
	// The Platform org is not a workspace: nobody can make it their active org.
	if m == nil || m.OrgKind == models.OrgKindPlatform {
		logAuthEvent(r, "switch_org", "failure", userID, "not_member", org...)
		http.Error(w, `{"error":"organization not found"}`, http.StatusNotFound)
		return
	}

	user, err := deps.getUser(userID)
	if err != nil {
		logAuthEvent(r, "switch_org", "failure", userID, "user_not_found", org...)
		http.Error(w, `{"error":"user not found"}`, http.StatusNotFound)
		return
	}
	if err := deps.setLastActive(r.Context(), userID, orgID); err != nil {
		log.Printf("switch-org: %v", err)
		logAuthEvent(r, "switch_org", "failure", userID, "store_error", org...)
		http.Error(w, `{"error":"failed to switch organization"}`, http.StatusInternalServerError)
		return
	}

	if writeSession(w, user, m) {
		logAuthEvent(r, "switch_org", "success", userID, "", org...)
	} else {
		logAuthEvent(r, "switch_org", "failure", userID, "session_error", org...)
	}
}

func handleChangePassword(w http.ResponseWriter, r *http.Request) {
	uc, ok := middleware.GetUserContext(r)
	if !ok {
		logAuthEvent(r, "change_password", "failure", uuid.Nil, "unauthenticated")
		http.Error(w, `{"error":"unauthorized"}`, http.StatusUnauthorized)
		return
	}

	var req ChangePasswordRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&req); err != nil {
		http.Error(w, `{"error":"invalid request body"}`, http.StatusBadRequest)
		return
	}

	if req.CurrentPassword == "" || req.NewPassword == "" {
		http.Error(w, `{"error":"current_password and new_password are required"}`, http.StatusBadRequest)
		return
	}

	if err := services.ValidateNewPassword(req.NewPassword); err != nil {
		writeJSONError(w, http.StatusBadRequest, err.Error())
		return
	}

	userID, err := uuid.Parse(uc.UserID)
	if err != nil {
		logAuthEvent(r, "change_password", "failure", uuid.Nil, "bad_user_id")
		http.Error(w, `{"error":"invalid user ID"}`, http.StatusInternalServerError)
		return
	}

	user, err := services.GetUserByID(userID)
	if err != nil {
		logAuthEvent(r, "change_password", "failure", userID, "user_not_found")
		http.Error(w, `{"error":"user not found"}`, http.StatusNotFound)
		return
	}

	if !services.ValidatePassword(user, req.CurrentPassword) {
		logAuthEvent(r, "change_password", "failure", userID, "bad_password")
		http.Error(w, `{"error":"current password is incorrect"}`, http.StatusUnauthorized)
		return
	}

	if err := services.ChangePassword(userID, req.NewPassword); err != nil {
		logAuthEvent(r, "change_password", "failure", userID, "store_error")
		http.Error(w, `{"error":"failed to change password"}`, http.StatusInternalServerError)
		return
	}

	// ChangePassword stamped tokens_invalidated_at, which just killed the token
	// this request carried (and every other session). Drop the cached state and
	// hand the caller a fresh session so the first-login flow keeps working.
	middleware.InvalidateSession(userID)
	authSuccess(w, r, "change_password", userID)
	updated, err := services.GetUserByID(userID)
	if err == nil {
		var m *services.ActiveMembership
		if m, err = services.ResolveActiveMembership(r.Context(), updated); err == nil {
			writeSession(w, updated, m)
			return
		}
	}
	log.Printf("change-password: reissue session for %s: %v", userID, err)
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]string{"message": "password changed successfully"})
}
