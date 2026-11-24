package services

import (
	"context"
	"errors"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
)

// AccountDeletionStore is the persistence the account-deletion flow needs (C9b). It is an
// interface so the handler's branches — the sole-owner refusal, the grace-period cancellation —
// are testable without a database; GormAccountDeletionStore is the production implementation.
type AccountDeletionStore interface {
	// SoleOwnerTeamWithMembers returns the name of a Team organization the user owns alone while
	// other people are still members of it, or "" when there is none. Deleting the account would
	// leave that team without an owner, so the request is refused instead (C9b).
	SoleOwnerTeamWithMembers(ctx context.Context, userID uuid.UUID) (string, error)
	// RequestDeletion stamps deletion_requested_at and tokens_invalidated_at together: the flag
	// starts the 30-day grace period and the stamp kills every outstanding session.
	RequestDeletion(ctx context.Context, userID uuid.UUID, now time.Time) error
	// ClearDeletionRequest cancels a pending deletion (the user signed in during the grace period).
	// It reports whether a request was actually pending, so the caller can say so.
	ClearDeletionRequest(ctx context.Context, userID uuid.UUID) (bool, error)
	// UserExists reports whether an address belongs to an account. The internal mail endpoint uses
	// it so a compromised caller cannot turn the mailer into an open relay (C0).
	UserExists(email string) (bool, error)
}

// ErrNoSuchUser is returned when the address or id names no account.
var ErrNoSuchUser = errors.New("no such user")

// GormAccountDeletionStore is the production AccountDeletionStore.
type GormAccountDeletionStore struct{}

func (GormAccountDeletionStore) SoleOwnerTeamWithMembers(ctx context.Context, userID uuid.UUID) (string, error) {
	var names []string
	err := database.DB.WithContext(ctx).Raw(`
		SELECT o.name
		FROM organizations o
		JOIN organization_memberships m
		  ON m.organization_id = o.id AND m.user_id = ? AND m.role = ?
		WHERE o.kind = ?
		  AND o.deleted_at IS NULL
		  AND (SELECT COUNT(*) FROM organization_memberships x
		       JOIN application_users xu ON xu.id = x.user_id AND xu.is_service_account = false
		       WHERE x.organization_id = o.id) > 1
		  AND (SELECT COUNT(*) FROM organization_memberships y
		       WHERE y.organization_id = o.id AND y.role = ?) = 1
		LIMIT 1`,
		userID, models.OrgRoleOwner, models.OrgKindTeam, models.OrgRoleOwner).Scan(&names).Error
	if err != nil {
		return "", err
	}
	if len(names) == 0 {
		return "", nil
	}
	return names[0], nil
}

func (GormAccountDeletionStore) RequestDeletion(ctx context.Context, userID uuid.UUID, now time.Time) error {
	res := database.DB.WithContext(ctx).Model(&models.ApplicationUser{}).
		Where("id = ? AND is_service_account = ?", userID, false).
		Updates(map[string]any{
			"deletion_requested_at": now,
			"tokens_invalidated_at": now,
		})
	if res.Error != nil {
		return res.Error
	}
	if res.RowsAffected == 0 {
		return ErrNoSuchUser
	}
	return nil
}

func (GormAccountDeletionStore) ClearDeletionRequest(ctx context.Context, userID uuid.UUID) (bool, error) {
	res := database.DB.WithContext(ctx).Model(&models.ApplicationUser{}).
		Where("id = ? AND deletion_requested_at IS NOT NULL", userID).
		Update("deletion_requested_at", nil)
	return res.RowsAffected > 0, res.Error
}

func (GormAccountDeletionStore) UserExists(email string) (bool, error) {
	var n int64
	err := database.DB.Model(&models.ApplicationUser{}).
		Where("lower(email) = ? AND is_service_account = ?", strings.ToLower(strings.TrimSpace(email)), false).
		Count(&n).Error
	return n > 0, err
}

// DeletionDate renders the grace-period end the deletion_scheduled mail talks about, in UTC.
func DeletionDate(now time.Time, graceDays int) time.Time {
	if graceDays < 0 {
		graceDays = 0
	}
	return now.UTC().AddDate(0, 0, graceDays).Truncate(time.Second)
}

// IsMissingRow reports whether err is GORM's "no rows" sentinel, so a caller can answer 404
// without importing gorm itself.
func IsMissingRow(err error) bool {
	return errors.Is(err, gorm.ErrRecordNotFound)
}
