package services

import (
	"errors"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
)

// ResetTokenUsable reports whether a ResetPassword token is unused and unexpired. It reads
// only: ConsumeResetPassword re-checks and consumes in its own transaction.
func (GormAccountStore) ResetTokenUsable(tokenHash string, now time.Time) (bool, error) {
	var n int64
	err := database.DB.Model(&models.UserToken{}).
		Where("token_hash = ? AND purpose = ? AND used_at IS NULL AND expires_at > ?",
			tokenHash, models.UserTokenResetPassword, now).
		Count(&n).Error
	return n > 0, err
}

// ConsumeResetPassword consumes a ResetPassword token and applies the new
// password in one transaction: hash set, MustChangePassword cleared,
// TokensInvalidatedAt = now (every outstanding JWT dies), and every other
// outstanding reset token superseded.
func (GormAccountStore) ConsumeResetPassword(tokenHash, passwordHash string, now time.Time) (*models.ApplicationUser, error) {
	var user models.ApplicationUser
	err := database.DB.Transaction(func(tx *gorm.DB) error {
		t, err := consume(tx, models.UserTokenResetPassword, tokenHash, now)
		if err != nil {
			return err
		}
		if err := tx.First(&user, "id = ?", t.UserID).Error; err != nil {
			return ErrInvalidToken
		}
		if err := tx.Model(&user).Updates(map[string]interface{}{
			"password_hash":         passwordHash,
			"must_change_password":  false,
			"tokens_invalidated_at": now,
		}).Error; err != nil {
			return err
		}
		user.PasswordHash = passwordHash
		user.MustChangePassword = false
		user.TokensInvalidatedAt = &now
		return supersedeTokens(tx, user.ID, models.UserTokenResetPassword, now)
	})
	if err != nil {
		return nil, err
	}
	return &user, nil
}

// SessionState is what the auth middleware needs to decide whether a token
// issued at some time is still honoured.
type SessionState struct {
	Exists              bool
	TokensInvalidatedAt *time.Time
	// IsServiceAccount: the token's subject is an API-key service account, which never holds a session.
	IsServiceAccount bool
}

// SessionLookup reads SessionState for a user.
type SessionLookup interface {
	GetSessionState(userID uuid.UUID) (SessionState, error)
}

// GormSessionLookup is the production SessionLookup.
type GormSessionLookup struct{}

func (GormSessionLookup) GetSessionState(userID uuid.UUID) (SessionState, error) {
	var u models.ApplicationUser
	err := database.DB.Select("id", "tokens_invalidated_at", "is_service_account").First(&u, "id = ?", userID).Error
	if errors.Is(err, gorm.ErrRecordNotFound) {
		return SessionState{}, nil
	}
	if err != nil {
		return SessionState{}, err
	}
	return SessionState{Exists: true, TokensInvalidatedAt: u.TokensInvalidatedAt, IsServiceAccount: u.IsServiceAccount}, nil
}

// TokenIssuedBeforeInvalidation reports whether a token issued at iat predates
// the user's invalidation instant. Both sides are compared in whole seconds
// (JWT iat has no sub-second part), so a token minted in the same second as
// the reset, i.e. right after it, is honoured. A token with no iat fails
// closed once an invalidation exists.
func TokenIssuedBeforeInvalidation(iat *time.Time, invalidatedAt *time.Time) bool {
	if invalidatedAt == nil {
		return false
	}
	if iat == nil {
		return true
	}
	return iat.Unix() < invalidatedAt.Unix()
}
