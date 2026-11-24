package admin

import (
	"encoding/json"
	"errors"
	"log"
	"net/http"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
	"gorm.io/gorm"
)

// maxBodyBytes caps the admin user bodies (a few short fields), like the sibling decoders.
const maxBodyBytes = 16 << 10

// The account operations and the cache eviction that must follow them; variables so tests can
// replace them.
var (
	getUserByID    = services.GetUserByID
	resetPassword  = services.ResetPassword
	updateUser     = services.UpdateUser
	deleteUser     = services.DeleteUser
	invalidateUser = middleware.InvalidateUser

	requestEmailChange  = services.RequestEmailChange
	cancelEmailChange   = services.CancelEmailChange
	pendingEmailChanges = services.PendingEmailChanges
	// countRecentTokens backs the per-user EmailTokenLimiter (all purposes, 3 per hour).
	countRecentTokens middleware.TokenCounter = services.GormAccountStore{}
	// sendMail delivers a templated mail; mail goes out after the response.
	sendMail = func(to string, tmpl services.TemplateName, data services.MailData) error {
		return services.DefaultMailer().SendTemplate(to, tmpl, data)
	}
	runAsync = func(f func()) { go f() }
	now      = time.Now
)

func RegisterAdminRoutes(router *mux.Router) {
	router.HandleFunc("/users", handleCreateUser).Methods("POST")
	router.HandleFunc("/users", handleListUsers).Methods("GET")
	router.HandleFunc("/users/{id}", handleGetUser).Methods("GET")
	router.HandleFunc("/users/{id}", handleUpdateUser).Methods("PUT")
	router.HandleFunc("/users/{id}", handleDeleteUser).Methods("DELETE")
	router.HandleFunc("/users/{id}/email-change", middleware.RequireJSON(handleRequestEmailChange)).Methods("POST")
	router.HandleFunc("/users/{id}/email-change", handleCancelEmailChange).Methods("DELETE")
}

// reservedServiceEmail reports whether email belongs to the reserved service-account domain.
// Shared by create and update so the same addresses are refused in both.
func reservedServiceEmail(email string) bool {
	return strings.HasSuffix(strings.ToLower(strings.TrimSpace(email)), models.ServiceAccountEmailDomain)
}

// adminActor is the acting admin, client IP (the rate limiter's helper) and user agent of r.
func adminActor(r *http.Request) services.AdminActor {
	a := services.AdminActor{ClientIP: middleware.ClientIP(r), UserAgent: r.UserAgent()}
	if uc, ok := middleware.GetUserContext(r); ok {
		if id, err := uuid.Parse(uc.UserID); err == nil {
			a.UserID = &id
		}
	}
	return a
}

// writeAdminError answers status with a stable machine code and a human message.
func writeAdminError(w http.ResponseWriter, status int, code, msg string) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(map[string]string{"code": code, "error": msg})
}

// writeEmailChangeRequiresUser refuses an admin email change.
func writeEmailChangeRequiresUser(w http.ResponseWriter) {
	writeAdminError(w, http.StatusConflict, "EMAIL_CHANGE_REQUIRES_USER",
		"an admin cannot set a user's email directly; use \"Change email\" (POST /api/v1/admin/users/{id}/email-change), which the user confirms from their current address")
}

func handleCreateUser(w http.ResponseWriter, r *http.Request) {
	var req CreateUserRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, maxBodyBytes)).Decode(&req); err != nil {
		http.Error(w, `{"error":"invalid request body"}`, http.StatusBadRequest)
		return
	}

	if req.Email == "" || req.DisplayName == "" {
		http.Error(w, `{"error":"email and displayName are required"}`, http.StatusBadRequest)
		return
	}

	if reservedServiceEmail(req.Email) {
		http.Error(w, `{"error":"this email domain is reserved"}`, http.StatusBadRequest)
		return
	}

	user, otp, err := services.CreateUser(req.Email, req.DisplayName, req.IsAdmin || req.IsPlatformAdmin, adminActor(r))
	if err != nil {
		log.Printf("failed to create user %s: %s", services.MailLogRef(req.Email), services.SanitizeMailError(err))
		http.Error(w, `{"error":"failed to create user"}`, http.StatusInternalServerError)
		return
	}

	go services.SendWelcomeEmail(user.Email, user.DisplayName, otp)

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusCreated)
	_ = json.NewEncoder(w).Encode(CreateUserResponse{
		User:              toUserResponse(user),
		TemporaryPassword: otp,
	})
}

func handleListUsers(w http.ResponseWriter, r *http.Request) {
	users, err := services.ListUsers()
	if err != nil {
		http.Error(w, `{"error":"failed to list users"}`, http.StatusInternalServerError)
		return
	}

	resp := make([]UserResponse, len(users))
	ids := make([]uuid.UUID, len(users))
	for i := range users {
		resp[i] = toUserResponse(&users[i])
		ids[i] = users[i].ID
	}
	pending, err := pendingEmailChanges(ids, now().UTC())
	if err != nil {
		log.Printf("admin list users: pending email changes: %v", err)
		http.Error(w, `{"error":"failed to list users"}`, http.StatusInternalServerError)
		return
	}
	for i := range resp {
		resp[i].PendingEmailChange = toPendingResponse(pending, resp[i].ID)
	}

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(resp)
}

func handleGetUser(w http.ResponseWriter, r *http.Request) {
	id, err := uuid.Parse(mux.Vars(r)["id"])
	if err != nil {
		http.Error(w, `{"error":"invalid user ID"}`, http.StatusBadRequest)
		return
	}

	user, err := getUserByID(id)
	if err != nil {
		http.Error(w, `{"error":"user not found"}`, http.StatusNotFound)
		return
	}

	writeUserWithPending(w, user)
}

// toPendingResponse is id's entry in pending, or nil.
func toPendingResponse(pending map[uuid.UUID]services.PendingEmailChange, id uuid.UUID) *PendingEmailChangeResponse {
	p, ok := pending[id]
	if !ok {
		return nil
	}
	return &PendingEmailChangeResponse{NewEmail: p.NewEmail, RequestedAt: p.RequestedAt, ExpiresAt: p.ExpiresAt}
}

// writeUserWithPending answers one user with its open email change request.
func writeUserWithPending(w http.ResponseWriter, user *models.ApplicationUser) {
	resp := toUserResponse(user)
	pending, err := pendingEmailChanges([]uuid.UUID{user.ID}, now().UTC())
	if err != nil {
		log.Printf("admin get user %s: pending email change: %v", user.ID, err)
		http.Error(w, `{"error":"failed to read user"}`, http.StatusInternalServerError)
		return
	}
	resp.PendingEmailChange = toPendingResponse(pending, user.ID)
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(resp)
}

func handleUpdateUser(w http.ResponseWriter, r *http.Request) {
	id, err := uuid.Parse(mux.Vars(r)["id"])
	if err != nil {
		http.Error(w, `{"error":"invalid user ID"}`, http.StatusBadRequest)
		return
	}

	var req UpdateUserRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, maxBodyBytes)).Decode(&req); err != nil {
		http.Error(w, `{"error":"invalid request body"}`, http.StatusBadRequest)
		return
	}

	// Validate the body before touching anything: an update must not mint an address in the
	// reserved service-account domain (same check as create).
	if req.Email != nil && reservedServiceEmail(*req.Email) {
		http.Error(w, `{"error":"this email domain is reserved"}`, http.StatusBadRequest)
		return
	}

	// Resolve the target before doing anything. GetUserByID hides service accounts, so an
	// API-key user answers 404 here and a password reset can never write to it.
	current, err := getUserByID(id)
	if err != nil {
		http.Error(w, `{"error":"user not found"}`, http.StatusNotFound)
		return
	}

	// An admin never changes a user's email. Pointing it at a mailbox the admin controls and then
	// using a public flow (verify-email signs the user in; forgot-password mails a reset link)
	// would hand over the account. An unchanged email passes: the admin form always sends it.
	// Checked before anything is written; UpdateUser re-checks against its own read.
	if req.Email != nil && *req.Email != current.Email {
		writeEmailChangeRequiresUser(w)
		return
	}

	// A one-time password is only ever mailed to a verified address.
	if req.ResetPassword {
		if current.EmailVerifiedAt == nil {
			http.Error(w, `{"error":"the user's email address is not verified"}`, http.StatusConflict)
			return
		}
	}

	actor := adminActor(r)

	// Handle password reset first if requested
	if req.ResetPassword {
		// The UPDATE is guarded on this exact verified address, and only that address is mailed.
		otp, err := resetPassword(id, current.Email, actor)
		if errors.Is(err, services.ErrResetPreconditionChanged) {
			http.Error(w, `{"error":"the user's email address changed or is not verified; retry"}`, http.StatusConflict)
			return
		}
		if err != nil {
			http.Error(w, `{"error":"failed to reset password"}`, http.StatusInternalServerError)
			return
		}
		// The reset stamped tokens_invalidated_at: apply it on this replica now, not after the cache TTL.
		invalidateUser(id)

		go services.SendWelcomeEmail(current.Email, current.DisplayName, otp)
	}

	// Build update fields
	fields := make(map[string]interface{})
	if req.Email != nil {
		fields["email"] = *req.Email
	}
	if req.DisplayName != nil {
		fields["display_name"] = *req.DisplayName
	}
	if req.IsPlatformAdmin != nil {
		fields["is_admin"] = *req.IsPlatformAdmin
	} else if req.IsAdmin != nil {
		fields["is_admin"] = *req.IsAdmin
	}

	var user *models.ApplicationUser
	if len(fields) > 0 {
		res, err := updateUser(id, fields, actor)
		if err != nil {
			if errors.Is(err, gorm.ErrRecordNotFound) {
				http.Error(w, `{"error":"user not found"}`, http.StatusNotFound)
				return
			}
			if errors.Is(err, services.ErrEmailChangeRequiresUser) {
				writeEmailChangeRequiresUser(w)
				return
			}
			http.Error(w, `{"error":"failed to update user"}`, http.StatusInternalServerError)
			return
		}
		// Platform-admin authority is read from the cached membership, and an is_admin or email
		// change ends the user's sessions: drop the caches so either applies on this replica now.
		if _, changed := fields["is_admin"]; changed || res.SessionsEnded {
			invalidateUser(id)
		}
		user = res.User
	} else {
		user, err = getUserByID(id)
		if err != nil {
			http.Error(w, `{"error":"user not found"}`, http.StatusNotFound)
			return
		}
	}

	resp := toUserResponse(user)
	// The PUT is already applied: a failed pending lookup answers null rather than failing it.
	if pending, err := pendingEmailChanges([]uuid.UUID{user.ID}, now().UTC()); err != nil {
		log.Printf("admin update user %s: pending email change: %v", user.ID, err)
	} else {
		resp.PendingEmailChange = toPendingResponse(pending, user.ID)
	}
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(resp)
}

func handleDeleteUser(w http.ResponseWriter, r *http.Request) {
	id, err := uuid.Parse(mux.Vars(r)["id"])
	if err != nil {
		http.Error(w, `{"error":"invalid user ID"}`, http.StatusBadRequest)
		return
	}

	if err := deleteUser(id, adminActor(r)); err != nil {
		if errors.Is(err, services.ErrAccountUnavailable) {
			http.Error(w, `{"error":"This account cannot be deleted right now."}`, http.StatusConflict)
			return
		}
		if errors.Is(err, gorm.ErrRecordNotFound) {
			http.Error(w, `{"error":"user not found"}`, http.StatusNotFound)
			return
		}
		http.Error(w, `{"error":"failed to delete user"}`, http.StatusInternalServerError)
		return
	}

	// The account is gone: its cached session and memberships must not outlive it.
	invalidateUser(id)

	w.WriteHeader(http.StatusNoContent)
}

// handleRequestEmailChange answers POST /admin/users/{id}/email-change {newEmail}: 202
// {pendingEmail, expiresAt}. The change is NOT applied: a confirm link goes to the user's CURRENT,
// verified address, and only its owner can confirm (issue #1207). Nothing is mailed to the new
// address. Refusals: 404 missing user or service account; 409 EMAIL_CHANGE_CURRENT_UNVERIFIED;
// 400 EMAIL_INVALID; 409 EMAIL_UNCHANGED; 409 EMAIL_IN_USE; 429 EMAIL_CHANGE_RATE_LIMITED.
func handleRequestEmailChange(w http.ResponseWriter, r *http.Request) {
	id, err := uuid.Parse(mux.Vars(r)["id"])
	if err != nil {
		http.Error(w, `{"error":"invalid user ID"}`, http.StatusBadRequest)
		return
	}
	var req RequestEmailChangeRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, maxBodyBytes)).Decode(&req); err != nil {
		http.Error(w, `{"error":"invalid request body"}`, http.StatusBadRequest)
		return
	}
	newEmail := strings.ToLower(strings.TrimSpace(req.NewEmail))
	if _, err := services.ValidateRecipient(newEmail); err != nil || newEmail == "" || reservedServiceEmail(newEmail) {
		writeAdminError(w, http.StatusBadRequest, "EMAIL_INVALID", "a valid email address is required")
		return
	}
	at := now().UTC()
	// The per-user limiter on emailed tokens (all purposes) also bounds these mails.
	if !middleware.NewEmailTokenLimiter(countRecentTokens).Allow(id, at) {
		writeEmailChangeRateLimited(w)
		return
	}
	res, err := requestEmailChange(id, newEmail, adminActor(r), at)
	switch {
	case err == nil:
	case errors.Is(err, gorm.ErrRecordNotFound):
		http.Error(w, `{"error":"user not found"}`, http.StatusNotFound)
		return
	case errors.Is(err, services.ErrEmailChangeCurrentUnverified):
		writeAdminError(w, http.StatusConflict, "EMAIL_CHANGE_CURRENT_UNVERIFIED",
			"the user's current email address is not verified, so it cannot confirm a change")
		return
	case errors.Is(err, services.ErrEmailUnchanged):
		writeAdminError(w, http.StatusConflict, "EMAIL_UNCHANGED", "the new address is the current one")
		return
	case errors.Is(err, services.ErrEmailInUse), services.IsUniqueViolation(err):
		writeAdminError(w, http.StatusConflict, "EMAIL_IN_USE", "the new address is already used by another account")
		return
	case errors.Is(err, services.ErrEmailChangeRateLimited):
		writeEmailChangeRateLimited(w)
		return
	default:
		log.Printf("admin email change request for %s: %v", id, err)
		http.Error(w, `{"error":"failed to request the email change"}`, http.StatusInternalServerError)
		return
	}
	// After commit, to the CURRENT address only.
	user := res.User
	link := config.Cfg.LinkBaseURL + "/app/email-change#token=" + res.RawToken
	pendingEmail := res.PendingEmail
	runAsync(func() {
		loc := ""
		if user.Locale != nil {
			loc = *user.Locale
		}
		if err := sendMail(user.Email, services.TemplateEmailChangeConfirm, services.MailData{
			Name: user.DisplayName, Link: link, NewEmail: pendingEmail, ExpiresIn: "24 hours", Locale: loc,
		}); err != nil {
			log.Printf("send email change confirm for %s: %s", user.ID, services.SanitizeMailError(err))
		}
	})
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusAccepted)
	_ = json.NewEncoder(w).Encode(RequestEmailChangeResponse{PendingEmail: res.PendingEmail, ExpiresAt: res.ExpiresAt})
}

func writeEmailChangeRateLimited(w http.ResponseWriter) {
	w.Header().Set("Retry-After", "3600")
	writeAdminError(w, http.StatusTooManyRequests, "EMAIL_CHANGE_RATE_LIMITED", "too many email change requests; try again later")
}

// handleCancelEmailChange answers DELETE /admin/users/{id}/email-change: 204 whether or not a
// request was open (idempotent); 404 for a missing user or a service account.
func handleCancelEmailChange(w http.ResponseWriter, r *http.Request) {
	id, err := uuid.Parse(mux.Vars(r)["id"])
	if err != nil {
		http.Error(w, `{"error":"invalid user ID"}`, http.StatusBadRequest)
		return
	}
	err = cancelEmailChange(id, services.EmailChangeViaAdmin, adminActor(r), now().UTC())
	if errors.Is(err, gorm.ErrRecordNotFound) {
		http.Error(w, `{"error":"user not found"}`, http.StatusNotFound)
		return
	}
	if err != nil {
		log.Printf("admin email change cancel for %s: %v", id, err)
		http.Error(w, `{"error":"failed to cancel the email change"}`, http.StatusInternalServerError)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}
