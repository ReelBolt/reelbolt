package auth

import (
	"encoding/json"
	"errors"
	"log"
	"net/http"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
	"gorm.io/gorm"
)

// The user's side of an admin-requested email change (issue #1207). The confirm link carries the
// token in its URL fragment, which never reaches a server; the page POSTs it in a JSON body to
// preview, confirm or cancel. The token is never read from a query string or a path.

// EmailChangeTokenRequest is the body of the three public routes.
type EmailChangeTokenRequest struct {
	Token string `json:"token"`
}

// EmailChangePreviewResponse is what the confirm page shows before its buttons.
type EmailChangePreviewResponse struct {
	CurrentEmailMasked string    `json:"currentEmailMasked"`
	NewEmail           string    `json:"newEmail"`
	ExpiresAt          time.Time `json:"expiresAt"`
}

// PendingEmailChangeResponse is the settings view of an open request.
type PendingEmailChangeResponse struct {
	NewEmail  string    `json:"newEmail"`
	ExpiresAt time.Time `json:"expiresAt"`
}

// emailChangeDeps are the collaborators, replaceable in tests.
type emailChangeDeps struct {
	preview        func(tokenHash string, now time.Time) (*services.EmailChangePreview, error)
	confirm        func(tokenHash string, actor services.AdminActor, now time.Time) (*services.EmailChangeConfirmed, error)
	cancelByToken  func(tokenHash string, actor services.AdminActor, now time.Time) error
	cancel         func(userID uuid.UUID, via string, actor services.AdminActor, now time.Time) error
	invalidateUser func(uuid.UUID)
	// mailVerification issues a VerifyEmail token bound to the user's (new) address and mails it.
	mailVerification func(user *models.ApplicationUser)
}

var emailChange = emailChangeDeps{
	preview:        services.PreviewEmailChange,
	confirm:        services.ConfirmEmailChange,
	cancelByToken:  services.CancelEmailChangeByToken,
	cancel:         services.CancelEmailChange,
	invalidateUser: middleware.InvalidateUser,
	// Once per successful confirm, already bounded by the request limits: not the per-user limiter,
	// which would silently drop it and leave the new address with no verify link.
	mailVerification: func(user *models.ApplicationUser) { acct.issueVerification(user, "") },
}

// writeEmailChangeError answers status with a stable machine code, like the admin routes.
func writeEmailChangeError(w http.ResponseWriter, status int, code, msg string) {
	writeJSON(w, status, map[string]string{"code": code, "error": msg})
}

// writeEmailChangeTokenInvalid is the one answer for every token failure: unknown, used, expired,
// another purpose, or sent to an address that is no longer the account's. It says nothing about
// any account.
func writeEmailChangeTokenInvalid(w http.ResponseWriter) {
	writeEmailChangeError(w, http.StatusBadRequest, "EMAIL_CHANGE_TOKEN_INVALID", "this link is invalid or has expired")
}

// readEmailChangeToken decodes {token} from the JSON body and returns its hash, or answers the
// uniform 400.
func readEmailChangeToken(w http.ResponseWriter, r *http.Request) (string, bool) {
	var req EmailChangeTokenRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&req); err != nil || strings.TrimSpace(req.Token) == "" {
		writeEmailChangeTokenInvalid(w)
		return "", false
	}
	return services.HashToken(strings.TrimSpace(req.Token)), true
}

// requestActor is the client IP and user agent of r; the services fill in the acting user.
func requestActor(r *http.Request) services.AdminActor {
	return services.AdminActor{ClientIP: middleware.ClientIP(r), UserAgent: r.UserAgent()}
}

// maskEmail keeps the first character of the local part and the whole domain: "a***@example.com".
func maskEmail(e string) string {
	local, domain, ok := strings.Cut(e, "@")
	if !ok || local == "" {
		return "***"
	}
	r := []rune(local)
	return string(r[0]) + "***@" + domain
}

// handleEmailChangePreview answers POST /api/v1/auth/email-change/preview {token}. Read only.
func handleEmailChangePreview(w http.ResponseWriter, r *http.Request) {
	hash, ok := readEmailChangeToken(w, r)
	if !ok {
		return
	}
	p, err := emailChange.preview(hash, acct.now().UTC())
	if err != nil {
		if !errors.Is(err, services.ErrInvalidToken) {
			log.Printf("email change preview: %v", err)
		}
		writeEmailChangeTokenInvalid(w)
		return
	}
	writeJSON(w, http.StatusOK, EmailChangePreviewResponse{
		CurrentEmailMasked: maskEmail(p.CurrentEmail), NewEmail: p.NewEmail, ExpiresAt: p.ExpiresAt,
	})
}

// handleEmailChangeConfirm answers POST /api/v1/auth/email-change/confirm {token}: 204 on success,
// the uniform 400 for any token failure, 409 EMAIL_IN_USE when the address was taken meanwhile
// (the request is consumed). It issues no session. After commit: every cached session of the user
// ends on this replica, the old address gets a notice, and the new one a verify link bound to it.
func handleEmailChangeConfirm(w http.ResponseWriter, r *http.Request) {
	hash, ok := readEmailChangeToken(w, r)
	if !ok {
		return
	}
	res, err := emailChange.confirm(hash, requestActor(r), acct.now().UTC())
	if errors.Is(err, services.ErrEmailInUse) {
		writeEmailChangeError(w, http.StatusConflict, "EMAIL_IN_USE", "the new address is already used by another account")
		return
	}
	if err != nil {
		if !errors.Is(err, services.ErrInvalidToken) {
			log.Printf("email change confirm: %v", err)
		}
		writeEmailChangeTokenInvalid(w)
		return
	}
	emailChange.invalidateUser(res.User.ID)
	user := res.User
	oldEmail := res.OldEmail
	acct.runAsync(func() {
		if err := acct.sendMail(oldEmail, services.TemplateEmailChangeDone, services.MailData{
			Name: user.DisplayName, NewEmail: user.Email, Locale: mailLocale(&user, ""),
		}); err != nil {
			log.Printf("send email change notice for %s: %s", user.ID, services.SanitizeMailError(err))
		}
		emailChange.mailVerification(&user)
	})
	w.WriteHeader(http.StatusNoContent)
}

// handleEmailChangeCancel answers POST /api/v1/auth/email-change/cancel {token}: 204, also when the
// request was already used, cancelled or expired; the uniform 400 for a token that names nothing.
func handleEmailChangeCancel(w http.ResponseWriter, r *http.Request) {
	hash, ok := readEmailChangeToken(w, r)
	if !ok {
		return
	}
	if err := emailChange.cancelByToken(hash, requestActor(r), acct.now().UTC()); err != nil {
		if !errors.Is(err, services.ErrInvalidToken) {
			log.Printf("email change cancel: %v", err)
		}
		writeEmailChangeTokenInvalid(w)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// handleEmailChangeCancelSelf answers the signed-in DELETE /api/v1/auth/email-change: the caller
// cancels their own open request. 204 whether or not one was open.
func handleEmailChangeCancelSelf(w http.ResponseWriter, r *http.Request) {
	userID, ok := settingsUserID(w, r)
	if !ok {
		return
	}
	actor := requestActor(r)
	actor.UserID = &userID
	if err := emailChange.cancel(userID, services.EmailChangeViaUser, actor, acct.now().UTC()); err != nil {
		if errors.Is(err, gorm.ErrRecordNotFound) {
			http.Error(w, `{"error":"user not found"}`, http.StatusNotFound)
			return
		}
		log.Printf("email change cancel by %s: %v", userID, err)
		http.Error(w, `{"error":"could not cancel the email change"}`, http.StatusInternalServerError)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}
