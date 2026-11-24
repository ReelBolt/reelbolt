package models

import (
	"time"

	"github.com/google/uuid"
)

// UserToken purposes (stored by name, matching the Inference API's UserTokenPurpose enum).
const (
	UserTokenVerifyEmail   = "VerifyEmail"
	UserTokenResetPassword = "ResetPassword"
	UserTokenEmailChange   = "EmailChange"
)

// UserToken is a single-use emailed token. Only the hex SHA-256 of the raw token is stored.
type UserToken struct {
	ID        uuid.UUID  `gorm:"type:uuid;primaryKey"`
	UserID    uuid.UUID  `gorm:"column:user_id;type:uuid"`
	Purpose   string     `gorm:"column:purpose"`
	TokenHash string     `gorm:"column:token_hash;uniqueIndex"`
	CreatedAt time.Time  `gorm:"column:created_at"`
	ExpiresAt time.Time  `gorm:"column:expires_at"`
	UsedAt    *time.Time `gorm:"column:used_at"`
	// SentToEmail is the address the token was mailed to. A token whose non-null SentToEmail no
	// longer matches the user's current email is refused (issue #1207, T-009). Null on legacy rows.
	SentToEmail *string `gorm:"column:sent_to_email"`
	// PendingEmail is the requested new address of an EmailChange token; null for other purposes.
	PendingEmail *string `gorm:"column:pending_email"`
	// RequestedByUserID is the admin who requested an EmailChange; null when the user did.
	RequestedByUserID *uuid.UUID `gorm:"column:requested_by_user_id;type:uuid"`
}

func (UserToken) TableName() string {
	return "user_tokens"
}
