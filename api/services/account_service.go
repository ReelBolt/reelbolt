package services

import (
	"crypto/rand"
	"encoding/base64"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"golang.org/x/crypto/bcrypt"
	"gorm.io/gorm"
)

// ErrInvalidToken is returned for an unknown, expired, used or superseded
// token alike, so a caller cannot tell the cases apart.
var ErrInvalidToken = errors.New("invalid or expired token")

// AccountStore is the persistence the self-serve account flows need. The
// production implementation is GORM; handlers take it through a variable so
// the flows are testable without a database.
type AccountStore interface {
	// FindUserByEmail returns (nil, nil) when no user has the address.
	FindUserByEmail(email string) (*models.ApplicationUser, error)
	// FindUserByID returns (nil, nil) when the user does not exist.
	FindUserByID(id uuid.UUID) (*models.ApplicationUser, error)
	// RegisterUser atomically inserts the user, their workspaces
	// (ProvisionUserTenancy), the legal acceptances, the first token and, when
	// optIn is non-nil, the marketing consent row and its Granted event.
	RegisterUser(user *models.ApplicationUser, acceptances []models.LegalAcceptance, token *models.UserToken, optIn *MarketingOptIn) error
	// RegisterInvitedUser is RegisterUser for someone holding a valid team
	// invite: the user is created already verified (the invite token proved the
	// address), gets the same workspaces and legal acceptances, and the invite
	// is accepted (membership created) in the SAME transaction, so a lost race
	// for the invite leaves no half-made account. A non-nil optIn is recorded
	// in the same transaction, already confirmed (the address is proven).
	RegisterInvitedUser(user *models.ApplicationUser, acceptances []models.LegalAcceptance, inv *models.OrganizationInvite, optIn *MarketingOptIn) error
	// IssueToken stores a token, first marking every earlier unused token of
	// the same user and purpose as used (only the newest link works).
	IssueToken(token *models.UserToken) error
	// ConsumeVerifyEmail consumes a VerifyEmail token and sets EmailVerifiedAt
	// in one transaction, confirming the user's still-unconfirmed OptedIn
	// marketing consent (with a Confirmed event) in that same transaction. It
	// returns the (now verified) user.
	ConsumeVerifyEmail(tokenHash string, now time.Time) (*models.ApplicationUser, error)
	// MarketingOptInPending reports whether verifying email would confirm a marketing opt-in for
	// the user (their own unconfirmed opt-in, or their open signup request on the address's row),
	// so every verify mail for the account carries the marketing notice while it is (S-32).
	MarketingOptInPending(userID uuid.UUID, email string) (bool, error)
	// CountRecentTokens counts the emailed tokens (any purpose) issued to the
	// user at or after since. It backs the per-email limiter.
	CountRecentTokens(userID uuid.UUID, since time.Time) (int, error)
	// CountSignupsFromIP counts accounts whose signup_ip is ip, created at or
	// after since. It backs the per-IP signup cap.
	CountSignupsFromIP(ip string, since time.Time) (int, error)
	// ResetTokenUsable reports whether a ResetPassword token is unused and unexpired, without
	// consuming it. The reset handler asks before it pays for a password hash (API-GO-06);
	// ConsumeResetPassword stays the authority and re-checks in its own transaction.
	ResetTokenUsable(tokenHash string, now time.Time) (bool, error)
	// ConsumeResetPassword consumes a ResetPassword token and applies the new
	// password hash, clearing MustChangePassword and setting TokensInvalidatedAt.
	ConsumeResetPassword(tokenHash, passwordHash string, now time.Time) (*models.ApplicationUser, error)

	// RegisterOAuthUser inserts a user the provider already verified and
	// provisions their workspaces in one transaction (C10). No token row: the
	// provider proved the address, so there is nothing left to verify by email.
	RegisterOAuthUser(user *models.ApplicationUser) error
	// MarkEmailVerified sets email_verified_at when it is still NULL, which is
	// what linking an existing unverified password account to a verified
	// provider address is worth. An already-verified row is left alone.
	MarkEmailVerified(userID uuid.UUID, at time.Time) error
	// LatestAcceptances returns document -> version for the newest acceptance of
	// each legal document. A document never accepted is absent from the map.
	LatestAcceptances(userID uuid.UUID) (map[string]string, error)
	// RecordAcceptances appends acceptance rows, skipping any document whose
	// newest recorded version is already the one being recorded, so re-posting
	// the same form cannot pile up duplicate consent rows.
	RecordAcceptances(acceptances []models.LegalAcceptance) error
	// CurrentLegalVersions returns the versions of the legal documents this
	// deployment currently serves, read from legal_documents (F-7). It is what
	// an acceptance is pinned to; ErrLegalDocumentsUnavailable means the
	// deployment cannot state a version and the caller must record nothing.
	CurrentLegalVersions() (LegalVersions, error)
}

// HashPassword bcrypts a password at the default cost.
func HashPassword(p string) (string, error) {
	h, err := bcrypt.GenerateFromPassword([]byte(p), bcrypt.DefaultCost)
	if err != nil {
		return "", fmt.Errorf("hash password: %w", err)
	}
	return string(h), nil
}

// GeneratePlaceholderPasswordHash bcrypts a fresh cryptographically random value
// (32 bytes of crypto/rand, base64url) so an account that has no usable local
// password still carries a real hash. The work is what matters, not the value:
// a bcrypt hash of any string costs the same ~55 ms to check, and a random 256
// bit value cannot be typed by a user, guessed, or reused across accounts.
//
// It exists to close the login timing oracle (C11 F-3): an account whose hash
// is empty makes bcrypt.CompareHashAndPassword return ErrHashTooShort before any
// work, so the password path answers in nanoseconds while a real account answers
// in milliseconds — disclosing both that the address exists and that it is
// OAuth-only. A placeholder hash erases the difference.
func GeneratePlaceholderPasswordHash() (string, error) {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return "", fmt.Errorf("generate placeholder password: %w", err)
	}
	return HashPassword(base64.RawURLEncoding.EncodeToString(b))
}

// NewToken builds a UserToken row and returns it with the raw value for the email. sentTo is the
// address the mail goes to: it is stored as sent_to_email, and the token is refused once the user's
// email no longer matches it (T-009).
func NewToken(userID uuid.UUID, sentTo, purpose string, ttl time.Duration, now time.Time) (*models.UserToken, string, error) {
	raw, hash, err := NewRawToken()
	if err != nil {
		return nil, "", err
	}
	to := sentTo
	return &models.UserToken{
		ID: uuid.New(), UserID: userID, Purpose: purpose, TokenHash: hash,
		CreatedAt: now, ExpiresAt: now.Add(ttl), SentToEmail: &to,
	}, raw, nil
}

// SameEmail compares two addresses the way every email-change check does: trimmed, lower-case.
// application_users.email's unique index is case-sensitive, so a plain == is not enough.
func SameEmail(a, b string) bool {
	return strings.EqualFold(strings.TrimSpace(a), strings.TrimSpace(b))
}

// GormAccountStore is the production AccountStore.
type GormAccountStore struct{}

func (GormAccountStore) FindUserByEmail(email string) (*models.ApplicationUser, error) {
	var u models.ApplicationUser
	err := database.DB.First(&u, "email = ? AND is_service_account = ?", email, false).Error
	if errors.Is(err, gorm.ErrRecordNotFound) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &u, nil
}

func (GormAccountStore) FindUserByID(id uuid.UUID) (*models.ApplicationUser, error) {
	var u models.ApplicationUser
	err := database.DB.First(&u, "id = ? AND is_service_account = ?", id, false).Error
	if errors.Is(err, gorm.ErrRecordNotFound) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &u, nil
}

func (GormAccountStore) CountRecentTokens(userID uuid.UUID, since time.Time) (int, error) {
	var n int64
	err := database.DB.Model(&models.UserToken{}).
		Where("user_id = ? AND created_at >= ?", userID, since).Count(&n).Error
	return int(n), err
}

func (GormAccountStore) CountSignupsFromIP(ip string, since time.Time) (int, error) {
	var n int64
	err := database.DB.Model(&models.ApplicationUser{}).
		Where("signup_ip = ? AND created_at >= ?", ip, since).Count(&n).Error
	return int(n), err
}

func supersedeTokens(tx *gorm.DB, userID uuid.UUID, purpose string, now time.Time) error {
	return tx.Model(&models.UserToken{}).
		Where("user_id = ? AND purpose = ? AND used_at IS NULL", userID, purpose).
		Update("used_at", now).Error
}

func (GormAccountStore) RegisterUser(user *models.ApplicationUser, acceptances []models.LegalAcceptance, token *models.UserToken, optIn *MarketingOptIn) error {
	err := database.DB.Transaction(func(tx *gorm.DB) error {
		if err := tx.Create(user).Error; err != nil {
			return fmt.Errorf("create user: %w", err)
		}
		if err := ProvisionUserTenancy(tx, user); err != nil {
			return err
		}
		for i := range acceptances {
			if err := tx.Create(&acceptances[i]).Error; err != nil {
				return fmt.Errorf("record legal acceptance: %w", err)
			}
		}
		if err := writeSignupMarketingConsentTx(tx, user, optIn); err != nil {
			return err
		}
		return tx.Create(token).Error
	})
	if err != nil {
		return err
	}
	// After the transaction: the account and its Personal workspace are committed, and a lost
	// funnel row can no longer take the signup down with it.
	recordPersonalWorkspaceCreatedBestEffort(user)
	return nil
}

func (GormAccountStore) RegisterInvitedUser(user *models.ApplicationUser, acceptances []models.LegalAcceptance, inv *models.OrganizationInvite, optIn *MarketingOptIn) error {
	err := database.DB.Transaction(func(tx *gorm.DB) error {
		if err := tx.Create(user).Error; err != nil {
			return fmt.Errorf("create user: %w", err)
		}
		if err := ProvisionUserTenancy(tx, user); err != nil {
			return err
		}
		for i := range acceptances {
			if err := tx.Create(&acceptances[i]).Error; err != nil {
				return fmt.Errorf("record legal acceptance: %w", err)
			}
		}
		if err := writeSignupMarketingConsentTx(tx, user, optIn); err != nil {
			return err
		}
		return acceptInviteTx(tx, inv, user.ID, *user.EmailVerifiedAt)
	})
	if err != nil {
		return err
	}
	// The invite-signup route creates a Personal workspace *and* joins the team org, so it is both
	// funnel steps. Both writes are after the transaction, best-effort.
	recordPersonalWorkspaceCreatedBestEffort(user)
	RecordProductEventBestEffort(database.DB, user.ID, &inv.OrganizationID, ProductEventInviteAccepted)
	return nil
}

func (GormAccountStore) IssueToken(token *models.UserToken) error {
	return database.DB.Transaction(func(tx *gorm.DB) error {
		if err := supersedeTokens(tx, token.UserID, token.Purpose, token.CreatedAt); err != nil {
			return err
		}
		return tx.Create(token).Error
	})
}

// consume marks the token used if it is unused and unexpired, returning it.
// The conditional UPDATE makes concurrent redemption single-use.
func consume(tx *gorm.DB, purpose, tokenHash string, now time.Time) (*models.UserToken, error) {
	var t models.UserToken
	if err := tx.First(&t, "token_hash = ? AND purpose = ?", tokenHash, purpose).Error; err != nil {
		if errors.Is(err, gorm.ErrRecordNotFound) {
			return nil, ErrInvalidToken
		}
		return nil, err
	}
	// A token for a service account is never redeemable (no mail is ever sent to one).
	var svc int64
	if err := tx.Model(&models.ApplicationUser{}).
		Where("id = ? AND is_service_account = ?", t.UserID, true).Count(&svc).Error; err != nil {
		return nil, err
	}
	if svc > 0 {
		return nil, ErrInvalidToken
	}
	// T-009: a token is bound to the address it was mailed to. Once the user's email is another
	// one, a link sent to the old address proves nothing about the new one. A null sent_to_email
	// is a legacy row and stays valid until it expires.
	if t.SentToEmail != nil {
		var current []string
		if err := tx.Model(&models.ApplicationUser{}).Where("id = ?", t.UserID).Pluck("email", &current).Error; err != nil {
			return nil, err
		}
		if len(current) != 1 || !SameEmail(current[0], *t.SentToEmail) {
			return nil, ErrInvalidToken
		}
	}
	res := tx.Model(&models.UserToken{}).
		Where("id = ? AND used_at IS NULL AND expires_at > ?", t.ID, now).
		Update("used_at", now)
	if res.Error != nil {
		return nil, res.Error
	}
	if res.RowsAffected != 1 {
		return nil, ErrInvalidToken
	}
	return &t, nil
}

func (GormAccountStore) ConsumeVerifyEmail(tokenHash string, now time.Time) (*models.ApplicationUser, error) {
	var user models.ApplicationUser
	// Only the transition that actually verified the address is a funnel step; re-consuming a
	// token for an already-verified address is not a second one.
	verified := false
	err := database.DB.Transaction(func(tx *gorm.DB) error {
		t, err := consume(tx, models.UserTokenVerifyEmail, tokenHash, now)
		if err != nil {
			return err
		}
		if err := tx.First(&user, "id = ?", t.UserID).Error; err != nil {
			return ErrInvalidToken
		}
		if user.EmailVerifiedAt == nil {
			if err := tx.Model(&user).Update("email_verified_at", now).Error; err != nil {
				return err
			}
			user.EmailVerifiedAt = &now
			verified = true
		}
		return confirmMarketingConsentTx(tx, user.ID, user.Email, now)
	})
	if err != nil {
		return nil, err
	}
	if verified {
		// After the transaction: the address is verified, and a lost funnel row cannot un-verify it.
		RecordProductEventBestEffort(database.DB, user.ID, nil, ProductEventEmailVerified)
	}
	return &user, nil
}
