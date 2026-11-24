package auth

import (
	"encoding/json"
	"errors"
	"log"
	"net/http"
	"strings"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

// hashPassword is the password hash the reset flow pays for; a variable so a test can see
// whether a request reached it.
var hashPassword = services.HashPassword

// handleForgotPassword mails a one-hour reset link when the address belongs to
// an account. It always answers the same 202 so it cannot enumerate accounts.
func handleForgotPassword(w http.ResponseWriter, r *http.Request) {
	var req ResendVerificationRequest // same shape: {email}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&req); err != nil {
		writeJSONError(w, http.StatusBadRequest, "invalid request body")
		return
	}
	if !acct.challengePassed(w, r, req.TurnstileToken) {
		return
	}
	email := normalizeEmail(req.Email)
	if user, err := acct.store.FindUserByEmail(email); err != nil {
		log.Printf("forgot-password lookup: %v", err)
	} else if user != nil && !models.IsServiceAccount(user) {
		reqLoc := services.LocaleFromRequest(r)
		acct.runAsync(func() { acct.mailReset(user, reqLoc) })
	}
	writeAccepted(w)
}

// handleResetPassword redeems a reset token. It does not log the user in: it
// sets the new password and kills every outstanding session.
func handleResetPassword(w http.ResponseWriter, r *http.Request) {
	var req ResetPasswordRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&req); err != nil || strings.TrimSpace(req.Token) == "" {
		logAuthEvent(r, "reset_password", "failure", uuid.Nil, "bad_request")
		writeJSONError(w, http.StatusBadRequest, "invalid or expired token")
		return
	}
	// Validate the password first so a weak one does not burn the token.
	if err := services.ValidateNewPassword(req.NewPassword); err != nil {
		writeJSONError(w, http.StatusBadRequest, err.Error())
		return
	}
	// Check the token before paying for a password hash: an unauthenticated caller with a
	// made-up token must not cost a bcrypt (API-GO-06). The consume below stays the authority.
	tokenHash := services.HashToken(strings.TrimSpace(req.Token))
	now := acct.now().UTC()
	if usable, err := acct.store.ResetTokenUsable(tokenHash, now); err != nil || !usable {
		if err != nil {
			log.Printf("reset password: %v", err)
		}
		logAuthEvent(r, "reset_password", "failure", uuid.Nil, "invalid_token")
		writeJSONError(w, http.StatusBadRequest, "invalid or expired token")
		return
	}
	hash, err := hashPassword(req.NewPassword)
	if err != nil {
		writeJSONError(w, http.StatusInternalServerError, "failed to reset password")
		return
	}
	user, err := acct.store.ConsumeResetPassword(tokenHash, hash, now)
	if err != nil {
		if !errors.Is(err, services.ErrInvalidToken) {
			log.Printf("reset password: %v", err)
		}
		logAuthEvent(r, "reset_password", "failure", uuid.Nil, "invalid_token")
		writeJSONError(w, http.StatusBadRequest, "invalid or expired token")
		return
	}
	middleware.InvalidateSession(user.ID)
	authSuccess(w, r, "reset_password", user.ID)

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]string{"message": "password updated; sign in with your new password"})
}
