package auth

import (
	"errors"
	"log"
	"net/http"
	"strings"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/handlers/orgs"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
	"github.com/vecchiotom/reelbolt/telemetry"
)

// Team invite acceptance (C8). Invite creation, listing and revocation belong
// to the organizations API (A4); the existing-user accept logic lives in
// orgs.AcceptInvite and is delegated to here, not copied. This file adds the
// public preview, the authenticated accept-by-path endpoint, and the new-user
// path through signup.

func writeInviteError(w http.ResponseWriter, err error) {
	var ae *orgs.AcceptError
	if errors.As(err, &ae) {
		writeJSONError(w, ae.Status, ae.Message)
		return
	}
	log.Printf("invitation: %v", err)
	writeJSONError(w, http.StatusInternalServerError, "failed to process invitation")
}

// handleInvitationPreview shows what an invite is for so the web page can ask
// the visitor to sign in or create the account. The token in the path is the
// credential. Unknown, revoked and used tokens are the same 404, an expired
// one is 410. The invited email is returned so signup can prefill it;
// accountExists tells the page whether to offer sign-in or signup.
func handleInvitationPreview(w http.ResponseWriter, r *http.Request) {
	inv, org, err := orgs.LookupInvite(r.Context(), acct.orgs, mux.Vars(r)["token"])
	if err == nil {
		err = orgs.CheckInviteUsable(inv, acct.now().UTC())
	}
	if err != nil {
		writeInviteError(w, err)
		return
	}

	inviter := ""
	if inv.InvitedByUserID != nil {
		if _, name, err := acct.orgs.GetUserIdentity(r.Context(), *inv.InvitedByUserID); err == nil {
			inviter = name
		}
	}
	existing, err := acct.store.FindUserByEmail(normalizeEmail(inv.Email))
	if err != nil {
		log.Printf("invitation preview lookup: %v", err)
		writeJSONError(w, http.StatusServiceUnavailable, "service unavailable")
		return
	}
	writeJSON(w, http.StatusOK, InvitationPreviewResponse{
		OrganizationName: org.Name,
		InviterName:      inviter,
		Role:             inv.Role,
		Email:            inv.Email,
		ExpiresAt:        inv.ExpiresAt,
		AccountExists:    existing != nil,
	})
}

// handleInvitationAccept lets a signed-in user whose email matches the invite
// join the organization, then re-issues their session for that organization
// (cookies included), so the client needs no separate switch-org call.
// Accepting again as an existing member is idempotent (200, nothing changes).
func handleInvitationAccept(w http.ResponseWriter, r *http.Request) {
	uc, ok := middleware.GetUserContext(r)
	if !ok {
		writeJSONError(w, http.StatusUnauthorized, "unauthorized")
		return
	}
	userID, err := uuid.Parse(uc.UserID)
	if err != nil {
		writeJSONError(w, http.StatusUnauthorized, "unauthorized")
		return
	}

	res, err := orgs.AcceptInvite(r.Context(), acct.orgs, mux.Vars(r)["token"], userID, acct.now().UTC(), true)
	if err != nil {
		writeInviteError(w, err)
		return
	}
	// The user may hold a cached "not a member" entry for this org.
	middleware.InvalidateMembership(userID, res.Org.ID)

	m, err := deps.lookup.GetMembership(r.Context(), userID, res.Org.ID)
	if err != nil || m == nil {
		log.Printf("invitation accept: membership lookup after join: %v", err)
		writeJSONError(w, http.StatusServiceUnavailable, "service unavailable")
		return
	}
	user, err := deps.getUser(userID)
	if err != nil {
		writeJSONError(w, http.StatusNotFound, "user not found")
		return
	}
	if err := deps.setLastActive(r.Context(), userID, res.Org.ID); err != nil {
		log.Printf("invitation accept: %v", err)
		writeJSONError(w, http.StatusInternalServerError, "failed to switch organization")
		return
	}
	writeSession(w, user, m)
}

// signupViaInvite is handleSignup's path for a request carrying inviteToken.
// The invite proves the visitor controls the invited address, so the account is
// created already verified (no verification mail, and no disposable-domain or
// MX check: a team admin chose this address), the invite is accepted in the
// same transaction, and the session is issued straight away like verify-email.
// The new user still gets their Personal workspace; the trial grant follows the
// usual verified-owner rule (C6). An address that already has an account must
// sign in and accept instead (409).
// current is the legal versions this deployment serves, already matched against
// the submission by handleSignup; the acceptance rows are pinned to it (F-7).
func signupViaInvite(w http.ResponseWriter, r *http.Request, req SignupRequest, email, displayName string, current services.LegalVersions) {
	inv, _, err := orgs.LookupInvite(r.Context(), acct.orgs, req.InviteToken)
	if err == nil {
		err = orgs.CheckInviteUsable(inv, acct.now().UTC())
	}
	if err != nil {
		writeInviteError(w, err)
		return
	}
	if !strings.EqualFold(email, strings.TrimSpace(inv.Email)) {
		writeJSONError(w, http.StatusForbidden, "this invitation was sent to a different email address")
		return
	}
	existing, err := acct.store.FindUserByEmail(email)
	if err != nil {
		log.Printf("invite signup lookup: %v", err)
		writeJSONError(w, http.StatusServiceUnavailable, "service unavailable")
		return
	}
	if existing != nil {
		writeJSONError(w, http.StatusConflict, "an account with this email already exists; sign in to accept the invitation")
		return
	}

	hash, err := services.HashPassword(req.Password)
	if err != nil {
		writeJSONError(w, http.StatusInternalServerError, "failed to create account")
		return
	}
	now := acct.now().UTC()
	ip := clientIP(r)
	user := &models.ApplicationUser{
		ID: uuid.New(), Email: email, DisplayName: displayName, CreatedAt: now,
		PasswordHash: hash, SignupIP: ip, AuthProvider: "Password", EmailVerifiedAt: &now,
		Locale: requestLocalePtr(r, req.Locale),
	}
	acceptances := legalAcceptances(user.ID, now, ip, current)
	if err := acct.store.RegisterInvitedUser(user, acceptances, inv, marketingOptIn(r, req, now, ip)); err != nil {
		// Most likely the invite was consumed or the address registered a moment ago.
		log.Printf("invite signup register: %v", err)
		writeJSONError(w, http.StatusConflict, "this invitation can no longer be used")
		return
	}
	// alpha-launch T-018: an invite signup creates an account too, so it is the same funnel step.
	recordFunnelEvent(r.Context(), telemetry.FunnelEventSignup)
	recordSignupProductEvent(user.ID, user.LastActiveOrganizationID)
	// Same persona and attribution rules as a password signup. The persona is written before the
	// session is answered, because the welcome page this session opens reads it straight away.
	mk := req.marketing()
	recordSignupPersona(user.ID, mk.persona, "")
	recordSignupAttribution(w, r, user.ID, mk.carried)
	services.NotifySeatsChanged(r.Context(), inv.OrganizationID)

	m, err := acct.resolveMembership(r.Context(), user)
	if err != nil {
		log.Printf("resolve active organization for %s: %v", user.ID, err)
		writeJSONError(w, http.StatusForbidden, "no workspace available for this account")
		return
	}
	acct.runAsync(func() {
		if err := acct.sendMail(user.Email, services.TemplateWelcome, services.MailData{
			Name: user.DisplayName, Link: config.Cfg.LinkBaseURL + "/app/login", Locale: mailLocale(user, ""),
		}); err != nil {
			log.Printf("send welcome mail to %s: %s", user.ID, services.SanitizeMailError(err))
		}
	})
	writeSession(w, user, m)
}
