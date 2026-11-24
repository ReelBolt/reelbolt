package auth

import (
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net/http"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

// AccountDeletionGraceDays is the window between a deletion request and the purge (C0's state
// machine: 30 days, cancelled by signing in). The purging side is the Inference API's
// AccountPurgeService, whose own Account:Purge:GraceDays setting is what actually schedules the
// work; this constant is what the request tells the user, and the two are meant to agree.
const AccountDeletionGraceDays = 30

// providerConfirmationStart is what a started provider confirmation hands the caller: which
// provider to name, and the authorization URL the browser has to visit. Provider is "" when the
// account's provider is not configured in this deployment.
type providerConfirmationStart struct {
	Provider string
	URL      string
}

// errDeletionSoleOwner is what scheduleAccountDeletion returns when the account is the only owner
// of a team that still has other members. The concrete error is always *soleOwnerError, so a caller
// can name the team; the sentinel is what errors.As/Is match on.
var errDeletionSoleOwner = errors.New("sole owner of a team with other members")

// soleOwnerError carries the name of the team that blocks the deletion.
type soleOwnerError struct{ organization string }

func (e *soleOwnerError) Error() string { return "sole owner of " + e.organization }
func (e *soleOwnerError) Unwrap() error { return errDeletionSoleOwner }

// errSoleOwnerCheck wraps a failure of the sole-owner lookup itself: the check is unavailable, which
// is a 503, and not the 500 a failed write gets.
var errSoleOwnerCheck = errors.New("sole-owner check unavailable")

// deletionDeps are the collaborators of handleAccountDelete, replaceable in tests.
type deletionDeps struct {
	store             services.AccountDeletionStore
	getUser           func(uuid.UUID) (*models.ApplicationUser, error)
	invalidateSession func(uuid.UUID)
	sendMail          func(to string, tmpl services.TemplateName, data services.MailData) error
	// runAsync detaches mail delivery from the response, exactly as signup does.
	runAsync func(func())
	now      func() time.Time
	// providerConfirmation starts the round trip that confirms a deletion for an account with no
	// password to re-enter (C11 F-8): it answers with the provider to name and the authorization URL
	// the browser has to visit, or with an empty Provider when that provider is not configured here.
	providerConfirmation func(w http.ResponseWriter, user *models.ApplicationUser) (providerConfirmationStart, error)
}

var deletion = deletionDeps{
	store:             services.GormAccountDeletionStore{},
	getUser:           services.GetUserByID,
	invalidateSession: middleware.InvalidateSession,
	sendMail: func(to string, tmpl services.TemplateName, data services.MailData) error {
		return services.DefaultMailer().SendTemplate(to, tmpl, data)
	},
	runAsync:             func(f func()) { go f() },
	now:                  time.Now,
	providerConfirmation: beginProviderConfirmation,
}

// handleAccountDelete (C9b) schedules the account for deletion after confirming that the caller
// controls it.
//
// The confirmation is a password when the account has one (a stolen session must never be enough for
// an irreversible action), and the provider round trip when it has none (C11 F-8): an account
// created by Google or GitHub has no password to re-enter, and before this the flow answered every
// such request with a flat 401 and no alternative at all, which made the deletion right unusable for
// exactly the accounts C10 added. The caller can then either post the password, or ask for the
// provider confirmation and complete it — a round trip that re-authenticates the identity the
// account was built on, and so proves control of the account just as the password does.
//
// It refuses — 409 — when the caller is the only Owner of a Team organization that still has other
// members, because deleting the account would leave that team unmanageable; the caller has to hand
// the team over first. Everything else follows C0: the flag starts the grace period, the session is
// killed immediately (tokens_invalidated_at), and the user is told by mail how to change their mind.
func handleAccountDelete(w http.ResponseWriter, r *http.Request) {
	uc, ok := middleware.GetUserContext(r)
	if !ok {
		http.Error(w, `{"error":"unauthorized"}`, http.StatusUnauthorized)
		return
	}

	var req DeleteAccountRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&req); err != nil {
		http.Error(w, `{"error":"invalid request body"}`, http.StatusBadRequest)
		return
	}

	userID, err := uuid.Parse(uc.UserID)
	if err != nil {
		http.Error(w, `{"error":"invalid user ID"}`, http.StatusInternalServerError)
		return
	}

	user, err := deletion.getUser(userID)
	if err != nil || user == nil {
		http.Error(w, `{"error":"user not found"}`, http.StatusNotFound)
		return
	}

	// Re-entering the password is the point of the flow: a stolen session must not be enough.
	if req.Password != "" {
		if !services.ValidatePassword(user, req.Password) {
			http.Error(w, `{"error":"password is incorrect"}`, http.StatusUnauthorized)
			return
		}
		purgeDate, err := scheduleAccountDeletion(w, r, user)
		if err != nil {
			writeDeletionFailure(w, userID, err)
			return
		}
		writeDeletionScheduled(w, purgeDate)
		return
	}

	// No password in the body. An account that signs in with a provider has none to give, so it is
	// offered the provider round trip instead; a password account gets the same 400 it always did.
	if !accountSignsInWithAProvider(user) {
		http.Error(w, `{"error":"password is required"}`, http.StatusBadRequest)
		return
	}
	// Refuse before arming anything: a confirmation that cannot succeed is worse than none.
	if blockingOrg, err := soleOwnerBlock(r, userID); err != nil {
		log.Printf("account delete: sole-owner check for %s: %v", userID, err)
		http.Error(w, `{"error":"service unavailable"}`, http.StatusServiceUnavailable)
		return
	} else if blockingOrg != "" {
		writeSoleOwnerRefusal(w, blockingOrg)
		return
	}

	start, err := deletion.providerConfirmation(w, user)
	if err != nil {
		log.Printf("account delete: provider confirmation for %s: %v", userID, err)
		http.Error(w, `{"error":"failed to start the confirmation"}`, http.StatusInternalServerError)
		return
	}
	if start.Provider == "" {
		// The account's provider is not configured here, so there is nothing to re-authenticate
		// against. Say what does work rather than leaving a dead end.
		writeJSON(w, http.StatusConflict, DeletionConfirmationRequired{
			Error: "confirmation_unavailable",
			Message: "This account signs in with " + displayAuthProvider(user.AuthProvider) +
				", which is not configured on this deployment. Use \"forgot password\" to set a password for " +
				user.Email + ", then confirm the deletion with it.",
		})
		return
	}
	writeJSON(w, http.StatusConflict, DeletionConfirmationRequired{
		Error:      "provider_confirmation_required",
		Provider:   start.Provider,
		ConfirmURL: start.URL,
		Message: "Nothing has been deleted yet. Confirm it by signing in with " +
			displayAuthProvider(user.AuthProvider) + ": the sign-in proves the account is yours, and the " +
			"deletion is scheduled when it completes.",
	})
}

// accountSignsInWithAProvider reports whether the account's credential is a provider identity rather
// than a password. auth_provider holds the account's ORIGIN and is deliberately never rewritten by a
// link (C10), so "Password" (and the empty default) is the password path.
func accountSignsInWithAProvider(user *models.ApplicationUser) bool {
	p := strings.TrimSpace(user.AuthProvider)
	return p != "" && !strings.EqualFold(p, "Password")
}

// displayAuthProvider names the provider for a message; an unknown value is shown as it is stored.
func displayAuthProvider(name string) string {
	switch strings.ToLower(strings.TrimSpace(name)) {
	case "google":
		return "Google"
	case "github":
		return "GitHub"
	}
	return strings.TrimSpace(name)
}

// soleOwnerBlock returns the name of a Team organization the user owns alone while other people are
// still members of it, or "" when the account may be deleted.
func soleOwnerBlock(r *http.Request, userID uuid.UUID) (string, error) {
	return deletion.store.SoleOwnerTeamWithMembers(r.Context(), userID)
}

// scheduleAccountDeletion is the destructive half of a deletion request, shared by the password path
// and the provider-confirmation round trip so both do exactly the same thing: refuse a sole-owner
// team, stamp the request, kill every session and mail the grace period. It never returns without
// having either refused or scheduled.
func scheduleAccountDeletion(w http.ResponseWriter, r *http.Request, user *models.ApplicationUser) (time.Time, error) {
	blockingOrg, err := soleOwnerBlock(r, user.ID)
	if err != nil {
		return time.Time{}, fmt.Errorf("%w: %w", errSoleOwnerCheck, err)
	}
	if blockingOrg != "" {
		return time.Time{}, &soleOwnerError{organization: blockingOrg}
	}

	now := deletion.now().UTC()
	if err := deletion.store.RequestDeletion(r.Context(), user.ID, now); err != nil {
		return time.Time{}, err
	}

	// Every session dies with the request: the token this call carried included.
	deletion.invalidateSession(user.ID)
	clearSessionCookies(w)

	purgeDate := services.DeletionDate(now, AccountDeletionGraceDays)
	// LinkBaseURL is the origin this side builds its own mail links from (F-11),
	// so the host the mailer validates against is identical to the host in the link.
	link := config.Cfg.LinkBaseURL + "/app/login"
	to, name, loc := user.Email, user.DisplayName, mailLocale(user, "")
	deletion.runAsync(func() {
		if err := deletion.sendMail(to, services.TemplateDeletionSchedule, services.MailData{
			Name:         name,
			Link:         link,
			DeletionDate: services.FormatMailDate(purgeDate, loc),
			Locale:       loc,
		}); err != nil {
			log.Printf("account delete: %s mail to %s failed: %s", services.TemplateDeletionSchedule, services.MailLogRef(to), services.SanitizeMailError(err))
		}
	})
	return purgeDate, nil
}

// writeDeletionFailure renders a failed scheduleAccountDeletion on the JSON endpoint.
func writeDeletionFailure(w http.ResponseWriter, userID uuid.UUID, err error) {
	var blocked *soleOwnerError
	switch {
	case errors.As(err, &blocked):
		writeSoleOwnerRefusal(w, blocked.organization)
	case errors.Is(err, errSoleOwnerCheck):
		log.Printf("account delete: sole-owner check for %s: %v", userID, err)
		http.Error(w, `{"error":"service unavailable"}`, http.StatusServiceUnavailable)
	default:
		log.Printf("account delete: request for %s: %v", userID, err)
		http.Error(w, `{"error":"failed to schedule the deletion"}`, http.StatusInternalServerError)
	}
}

// writeSoleOwnerRefusal is the 409 that names the team standing in the way.
func writeSoleOwnerRefusal(w http.ResponseWriter, organization string) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusConflict)
	_ = json.NewEncoder(w).Encode(map[string]string{
		"error":        "sole_owner",
		"organization": organization,
		"message":      "You are the only owner of a team that still has other members. Transfer ownership or remove the other members first.",
	})
}

// writeDeletionScheduled is the 202 that says the grace period has started.
func writeDeletionScheduled(w http.ResponseWriter, purgeDate time.Time) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusAccepted)
	_ = json.NewEncoder(w).Encode(map[string]any{
		"status":            "deletion_scheduled",
		"deletionDate":      purgeDate.Format(time.RFC3339),
		"graceDays":         AccountDeletionGraceDays,
		"cancelBySigningIn": config.Cfg.PublicBaseURL + "/app/login",
	})
}

// clearSessionCookies drops both session cookies, so the browser that made the request is signed
// out rather than left holding a token the server has just refused.
func clearSessionCookies(w http.ResponseWriter) {
	for _, name := range []string{"reelbolt_token", "reelbolt_user"} {
		http.SetCookie(w, &http.Cookie{
			Name:     name,
			Value:    "",
			Path:     "/",
			MaxAge:   -1,
			HttpOnly: name == "reelbolt_token",
			Secure:   config.Cfg.CookieSecure,
			SameSite: http.SameSiteLaxMode,
		})
	}
}

// cancelPendingDeletion clears a deletion request when the owner signs in during the grace period
// (C0: "logging in again within 30 days cancels the deletion"). It returns true when a request was
// actually pending, and never fails the login: a deletion flag that could not be cleared is worth a
// log line, not a locked-out user.
func cancelPendingDeletion(r *http.Request, user *models.ApplicationUser) bool {
	if user == nil || user.DeletionRequestedAt == nil {
		return false
	}

	cleared, err := deletion.store.ClearDeletionRequest(r.Context(), user.ID)
	if err != nil {
		log.Printf("login: clearing the pending deletion of %s failed: %v", user.ID, err)
		return false
	}
	if cleared {
		user.DeletionRequestedAt = nil
		log.Printf("login: the pending deletion of %s was cancelled by signing in", user.ID)
	}
	return cleared
}
