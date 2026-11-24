package services

import (
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
	"gorm.io/gorm/clause"
)

// Admin-requested email change with a pending address (issue #1207,
// the admin-email-pending-address design).
//
// The pending change is an unused EmailChange user_tokens row: pending_email is the requested
// address, sent_to_email the CURRENT address the confirm link was mailed to. Nothing is written to
// application_users.email, and nothing is mailed to the pending address, until the owner of the
// current (verified) mailbox confirms. A partial unique index allows one open row per user.
//
// Lock order is always user_tokens rows first, then the application_users row, in every writer
// here, matching verify/reset consume (which UPDATE their token, then the user). Confirm first locks
// EVERY unused token of the user (it revokes them all), so a concurrent verify or reset consume
// waits on its token row before it can reach the user row, and no cycle can form.

const (
	// EmailChangeTTL is how long a confirm link stays valid.
	EmailChangeTTL = 24 * time.Hour
	// EmailChangeMaxPerTarget EmailChange tokens per user per EmailChangeTargetWindow.
	EmailChangeMaxPerTarget = 3
	EmailChangeTargetWindow = 24 * time.Hour
	// EmailChangeMaxPerAdmin request audit rows per admin per EmailChangeAdminWindow.
	EmailChangeMaxPerAdmin = 20
	EmailChangeAdminWindow = time.Hour

	// Where an email-change cancel came from (the audit row's {via}).
	EmailChangeViaLink  = "link"
	EmailChangeViaUser  = "user"
	EmailChangeViaAdmin = "admin"
)

var (
	// ErrEmailChangeCurrentUnverified: the current address is not verified, so a confirm from it
	// would prove nothing.
	ErrEmailChangeCurrentUnverified = errors.New("current email address is not verified")
	// ErrEmailUnchanged: the new address equals the current one (case-insensitive).
	ErrEmailUnchanged = errors.New("new email equals the current one")
	// ErrEmailInUse: another account already has the address (case-insensitive).
	ErrEmailInUse = errors.New("email address already in use")
	// ErrEmailChangeRateLimited: a per-target or per-admin limit was hit.
	ErrEmailChangeRateLimited = errors.New("email change rate limited")
)

// EmailChangeRequested is the result of RequestEmailChange: what the confirm mail needs.
type EmailChangeRequested struct {
	// User is the target as it was read under the row lock (Email is the current address).
	User         models.ApplicationUser
	PendingEmail string
	ExpiresAt    time.Time
	// RawToken goes in the mail's link only; just its sha256 is stored.
	RawToken string
}

// EmailChangeConfirmed is the result of ConfirmEmailChange.
type EmailChangeConfirmed struct {
	// User is the updated row: Email is the new address, EmailVerifiedAt is nil.
	User     models.ApplicationUser
	OldEmail string
}

// EmailChangePreview is what the public confirm page shows before the buttons.
type EmailChangePreview struct {
	CurrentEmail string
	NewEmail     string
	ExpiresAt    time.Time
}

// PendingEmailChange is an open, unexpired request.
type PendingEmailChange struct {
	NewEmail    string
	RequestedAt time.Time
	ExpiresAt   time.Time
}

func normEmail(e string) string { return strings.ToLower(strings.TrimSpace(e)) }

// lockOpenTokens locks the user's unused token rows (of purpose, or all when purpose is ""), in
// id order, before any user row lock is taken.
func lockOpenTokens(tx *gorm.DB, userID uuid.UUID, purpose string) error {
	q := tx.Model(&models.UserToken{}).Clauses(clause.Locking{Strength: "UPDATE"}).
		Where("user_id = ? AND used_at IS NULL", userID)
	if purpose != "" {
		q = q.Where("purpose = ?", purpose)
	}
	var ids []uuid.UUID
	return q.Order("id").Pluck("id", &ids).Error
}

// lockUser reads a non-service user FOR UPDATE; gorm.ErrRecordNotFound when there is none.
func lockUser(tx *gorm.DB, id uuid.UUID) (*models.ApplicationUser, error) {
	var u models.ApplicationUser
	if err := tx.Clauses(clause.Locking{Strength: "UPDATE"}).
		First(&u, "id = ? AND is_service_account = ?", id, false).Error; err != nil {
		return nil, err
	}
	return &u, nil
}

// emailTaken reports whether a user other than self has email (case-insensitive).
func emailTaken(tx *gorm.DB, email string, self uuid.UUID) (bool, error) {
	var n int64
	err := tx.Model(&models.ApplicationUser{}).
		Where("lower(email) = ? AND id <> ?", normEmail(email), self).Count(&n).Error
	return n > 0, err
}

// RequestEmailChange records an admin's request to move userID to newEmail. In one transaction it
// locks the user, checks the preconditions and the durable rate limits, supersedes any open
// request, inserts the EmailChange token (sent_to_email = the current address) and writes the
// user.email_change.request audit row; an audit failure writes nothing. The caller mails the
// confirm link to the CURRENT address after commit, never to the pending one.
func RequestEmailChange(userID uuid.UUID, newEmail string, actor AdminActor, now time.Time) (*EmailChangeRequested, error) {
	pending := normEmail(newEmail)
	var out *EmailChangeRequested
	err := database.DB.Transaction(func(tx *gorm.DB) error {
		if err := lockOpenTokens(tx, userID, models.UserTokenEmailChange); err != nil {
			return err
		}
		user, err := lockUser(tx, userID)
		if err != nil {
			return err
		}
		if user.EmailVerifiedAt == nil {
			return ErrEmailChangeCurrentUnverified
		}
		if SameEmail(user.Email, pending) {
			return ErrEmailUnchanged
		}
		if taken, err := emailTaken(tx, pending, user.ID); err != nil {
			return err
		} else if taken {
			return ErrEmailInUse
		}
		var perTarget int64
		if err := tx.Model(&models.UserToken{}).
			Where("user_id = ? AND purpose = ? AND created_at >= ?", user.ID, models.UserTokenEmailChange, now.Add(-EmailChangeTargetWindow)).
			Count(&perTarget).Error; err != nil {
			return err
		}
		if perTarget >= EmailChangeMaxPerTarget {
			return ErrEmailChangeRateLimited
		}
		if actor.UserID != nil {
			// Serialize this admin's concurrent requests so the count below cannot be raced past.
			if err := tx.Exec(`SELECT pg_advisory_xact_lock(hashtext(?))`, "email_change_admin:"+actor.UserID.String()).Error; err != nil {
				return err
			}
			var perAdmin int64
			if err := tx.Raw(`SELECT count(*) FROM admin_audit_events WHERE actor_user_id = ? AND action = ? AND created_at >= ?`,
				*actor.UserID, AdminActionUserEmailChangeRequest, now.Add(-EmailChangeAdminWindow)).
				Scan(&perAdmin).Error; err != nil {
				return err
			}
			if perAdmin >= EmailChangeMaxPerAdmin {
				return ErrEmailChangeRateLimited
			}
		}
		// One open request per user (partial unique index): retire the previous one first,
		// expired or not.
		if err := supersedeTokens(tx, user.ID, models.UserTokenEmailChange, now); err != nil {
			return err
		}
		tok, raw, err := NewToken(user.ID, user.Email, models.UserTokenEmailChange, EmailChangeTTL, now)
		if err != nil {
			return err
		}
		tok.PendingEmail = &pending
		tok.RequestedByUserID = actor.UserID
		if err := tx.Create(tok).Error; err != nil {
			return fmt.Errorf("insert email change token: %w", err)
		}
		if err := insertAdminAudit(tx, actor, user.ID, AdminActionUserEmailChangeRequest,
			map[string]FieldChange{"email": {Old: user.Email, New: pending}}); err != nil {
			return err
		}
		out = &EmailChangeRequested{User: *user, PendingEmail: pending, ExpiresAt: tok.ExpiresAt, RawToken: raw}
		return nil
	})
	if err != nil {
		return nil, err
	}
	return out, nil
}

// findEmailChangeToken reads the EmailChange token with tokenHash, unlocked. Unknown or another
// purpose is ErrInvalidToken.
func findEmailChangeToken(tx *gorm.DB, tokenHash string) (*models.UserToken, error) {
	var t models.UserToken
	err := tx.First(&t, "token_hash = ? AND purpose = ?", tokenHash, models.UserTokenEmailChange).Error
	if errors.Is(err, gorm.ErrRecordNotFound) {
		return nil, ErrInvalidToken
	}
	if err != nil {
		return nil, err
	}
	return &t, nil
}

// lockEmailChangeToken locks the user, then the token row, and re-reads the token under the lock.
// It refuses (ErrInvalidToken) a missing user, a service account, a token without a pending
// address, and a token whose sent_to_email is not the user's current address. Used and expired
// are left to the caller (cancel is idempotent on them, confirm refuses them).
func lockEmailChangeToken(tx *gorm.DB, tokenHash string) (*models.UserToken, *models.ApplicationUser, error) {
	t, err := findEmailChangeToken(tx, tokenHash)
	if err != nil {
		return nil, nil, err
	}
	if err := lockOpenTokens(tx, t.UserID, ""); err != nil {
		return nil, nil, err
	}
	var locked models.UserToken
	if err := tx.Clauses(clause.Locking{Strength: "UPDATE"}).First(&locked, "id = ?", t.ID).Error; err != nil {
		if errors.Is(err, gorm.ErrRecordNotFound) {
			return nil, nil, ErrInvalidToken
		}
		return nil, nil, err
	}
	user, err := lockUser(tx, t.UserID)
	if errors.Is(err, gorm.ErrRecordNotFound) {
		return nil, nil, ErrInvalidToken
	}
	if err != nil {
		return nil, nil, err
	}
	if locked.PendingEmail == nil || locked.SentToEmail == nil || !SameEmail(*locked.SentToEmail, user.Email) {
		return nil, nil, ErrInvalidToken
	}
	return &locked, user, nil
}

// emailChangeStillValid is the one predicate confirm, preview and the pending lookups share
// (besides open/unexpired): the user's current address is still verified and is the address the
// link was sent to, and an admin-requested change is still backed by an active platform admin.
func emailChangeStillValid(tx *gorm.DB, t *models.UserToken, user *models.ApplicationUser) (bool, error) {
	if t.PendingEmail == nil || t.SentToEmail == nil || user.EmailVerifiedAt == nil || !SameEmail(*t.SentToEmail, user.Email) {
		return false, nil
	}
	if t.RequestedByUserID == nil {
		return true, nil
	}
	var n int64
	if err := tx.Model(&models.ApplicationUser{}).
		Where("id = ? AND is_admin = ? AND is_service_account = ?", *t.RequestedByUserID, true, false).
		Count(&n).Error; err != nil {
		return false, err
	}
	return n > 0, nil
}

func tokenOpen(t *models.UserToken, now time.Time) bool {
	return t.UsedAt == nil && t.ExpiresAt.After(now)
}

// ConfirmEmailChange applies the change a valid token names. One transaction: the user and the
// token row are locked; the token must be EmailChange, unused, unexpired and sent to the user's
// current address; the new address must still be free. The user gets email = pending,
// email_verified_at = NULL, tokens_invalidated_at = now; EVERY unused token of the user (all
// purposes) is marked used; the user.email_change.confirm audit row is written with actor = the
// user. Every token failure is ErrInvalidToken. A taken address consumes the request and answers
// ErrEmailInUse with nothing else changed. The caller ends cached sessions and sends the mails.
func ConfirmEmailChange(tokenHash string, actor AdminActor, now time.Time) (*EmailChangeConfirmed, error) {
	var out *EmailChangeConfirmed
	inUse := false
	retired := false
	var tokenID uuid.UUID
	err := database.DB.Transaction(func(tx *gorm.DB) error {
		t, user, err := lockEmailChangeToken(tx, tokenHash)
		if err != nil {
			return err
		}
		if !tokenOpen(t, now) {
			return ErrInvalidToken
		}
		tokenID = t.ID
		// Under the user lock: the current address must still be verified, and an admin-requested
		// change must still be backed by an active platform admin (a revoked or deleted admin's
		// pending requests die with them). Either failure retires the request and nothing else.
		valid, err := emailChangeStillValid(tx, t, user)
		if err != nil {
			return err
		}
		if !valid {
			retired = true
			return tx.Model(&models.UserToken{}).Where("id = ? AND used_at IS NULL", t.ID).Update("used_at", now).Error
		}
		pending := normEmail(*t.PendingEmail)
		if taken, err := emailTaken(tx, pending, user.ID); err != nil {
			return err
		} else if taken {
			inUse = true
			return tx.Model(&models.UserToken{}).Where("id = ? AND used_at IS NULL", t.ID).Update("used_at", now).Error
		}
		old := user.Email
		if err := tx.Model(&models.ApplicationUser{}).Where("id = ?", user.ID).Updates(map[string]interface{}{
			"email":                 pending,
			"email_verified_at":     nil,
			"tokens_invalidated_at": now,
		}).Error; err != nil {
			return err
		}
		// Revoke every outstanding link of the user, this one included (verify, reset, change).
		res := tx.Model(&models.UserToken{}).Where("user_id = ? AND used_at IS NULL", user.ID).Update("used_at", now)
		if res.Error != nil {
			return res.Error
		}
		if res.RowsAffected < 1 {
			return ErrInvalidToken
		}
		self := user.ID
		actor.UserID = &self
		if err := insertAdminAudit(tx, actor, user.ID, AdminActionUserEmailChangeConfirm,
			map[string]FieldChange{"email": {Old: old, New: pending}}); err != nil {
			return err
		}
		user.Email = pending
		user.EmailVerifiedAt = nil
		user.TokensInvalidatedAt = &now
		out = &EmailChangeConfirmed{User: *user, OldEmail: old}
		return nil
	})
	if err != nil && IsUniqueViolation(err) {
		// Someone took the address between the check and the UPDATE: same answer as the check,
		// and the request is consumed all the same.
		if cerr := database.DB.Model(&models.UserToken{}).Where("id = ? AND used_at IS NULL", tokenID).
			Update("used_at", now).Error; cerr != nil {
			return nil, cerr
		}
		return nil, ErrEmailInUse
	}
	if err != nil {
		return nil, err
	}
	if retired {
		return nil, ErrInvalidToken
	}
	if inUse {
		return nil, ErrEmailInUse
	}
	return out, nil
}

// PreviewEmailChange reads what a valid token would do, writing nothing. Every failure that
// ConfirmEmailChange would refuse with ErrInvalidToken is ErrInvalidToken here too.
func PreviewEmailChange(tokenHash string, now time.Time) (*EmailChangePreview, error) {
	t, err := findEmailChangeToken(database.DB, tokenHash)
	if err != nil {
		return nil, err
	}
	if !tokenOpen(t, now) || t.PendingEmail == nil || t.SentToEmail == nil {
		return nil, ErrInvalidToken
	}
	var u models.ApplicationUser
	if err := database.DB.First(&u, "id = ? AND is_service_account = ?", t.UserID, false).Error; err != nil {
		if errors.Is(err, gorm.ErrRecordNotFound) {
			return nil, ErrInvalidToken
		}
		return nil, err
	}
	if valid, err := emailChangeStillValid(database.DB, t, &u); err != nil {
		return nil, err
	} else if !valid {
		return nil, ErrInvalidToken
	}
	return &EmailChangePreview{CurrentEmail: u.Email, NewEmail: *t.PendingEmail, ExpiresAt: t.ExpiresAt}, nil
}

// CancelEmailChangeByToken cancels the request a link names (the "this wasn't me" button). An
// unknown token, another purpose or a token not bound to the current address is ErrInvalidToken;
// an already used or expired one is a no-op (idempotent). The audit row's actor is the user.
func CancelEmailChangeByToken(tokenHash string, actor AdminActor, now time.Time) error {
	return database.DB.Transaction(func(tx *gorm.DB) error {
		t, user, err := lockEmailChangeToken(tx, tokenHash)
		if err != nil {
			return err
		}
		if !tokenOpen(t, now) {
			return nil
		}
		res := tx.Model(&models.UserToken{}).Where("id = ? AND used_at IS NULL", t.ID).Update("used_at", now)
		if res.Error != nil || res.RowsAffected == 0 {
			return res.Error
		}
		self := user.ID
		actor.UserID = &self
		return insertAdminAuditPayload(tx, actor, user.ID, AdminActionUserEmailChangeCancel, map[string]string{"via": EmailChangeViaLink})
	})
}

// CancelEmailChange cancels userID's open request, by the user (via "user") or an admin (via
// "admin"); actor is who did it. Idempotent: with nothing open it writes nothing. A missing user or
// a service account is gorm.ErrRecordNotFound.
func CancelEmailChange(userID uuid.UUID, via string, actor AdminActor, now time.Time) error {
	return database.DB.Transaction(func(tx *gorm.DB) error {
		if err := lockOpenTokens(tx, userID, models.UserTokenEmailChange); err != nil {
			return err
		}
		user, err := lockUser(tx, userID)
		if err != nil {
			return err
		}
		res := tx.Model(&models.UserToken{}).
			Where("user_id = ? AND purpose = ? AND used_at IS NULL", user.ID, models.UserTokenEmailChange).
			Update("used_at", now)
		if res.Error != nil || res.RowsAffected == 0 {
			return res.Error
		}
		return insertAdminAuditPayload(tx, actor, user.ID, AdminActionUserEmailChangeCancel, map[string]string{"via": via})
	})
}

// PendingEmailChanges returns the open, unexpired request of each of userIDs that has one.
func PendingEmailChanges(userIDs []uuid.UUID, now time.Time) (map[uuid.UUID]PendingEmailChange, error) {
	out := map[uuid.UUID]PendingEmailChange{}
	if len(userIDs) == 0 {
		return out, nil
	}
	if database.DB == nil {
		return nil, errors.New("database not initialized")
	}
	var rows []models.UserToken
	if err := database.DB.
		Where("user_id IN ? AND purpose = ? AND used_at IS NULL AND expires_at > ? AND pending_email IS NOT NULL",
			userIDs, models.UserTokenEmailChange, now).
		Find(&rows).Error; err != nil {
		return nil, err
	}
	users := map[uuid.UUID]*models.ApplicationUser{}
	if len(rows) > 0 {
		ids := make([]uuid.UUID, 0, len(rows))
		for _, t := range rows {
			ids = append(ids, t.UserID)
		}
		var us []models.ApplicationUser
		if err := database.DB.Where("id IN ? AND is_service_account = ?", ids, false).Find(&us).Error; err != nil {
			return nil, err
		}
		for i := range us {
			users[us[i].ID] = &us[i]
		}
	}
	for i := range rows {
		t := &rows[i]
		u, ok := users[t.UserID]
		if !ok {
			continue
		}
		if valid, err := emailChangeStillValid(database.DB, t, u); err != nil {
			return nil, err
		} else if !valid {
			continue
		}
		out[t.UserID] = PendingEmailChange{NewEmail: *t.PendingEmail, RequestedAt: t.CreatedAt, ExpiresAt: t.ExpiresAt}
	}
	return out, nil
}

// GetPendingEmailChange returns userID's open, unexpired request, or nil.
func GetPendingEmailChange(userID uuid.UUID, now time.Time) (*PendingEmailChange, error) {
	m, err := PendingEmailChanges([]uuid.UUID{userID}, now)
	if err != nil {
		return nil, err
	}
	if p, ok := m[userID]; ok {
		return &p, nil
	}
	return nil, nil
}
