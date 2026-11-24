package services

import (
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
	"gorm.io/gorm/clause"
)

// tenancyWriter is the slice of GORM that provisioning needs, so the rules
// (which orgs and memberships a user gets) are unit-testable without a database.
type tenancyWriter interface {
	Create(value any) error
	SetLastActiveOrganization(userID, orgID uuid.UUID) error
	// DeleteMembership removes one membership row (no-op when absent).
	DeleteMembership(orgID, userID uuid.UUID) error
	// EnsureMembership inserts a membership, leaving an existing row untouched.
	EnsureMembership(m *models.OrganizationMembership) error
}

type gormTenancyWriter struct{ tx *gorm.DB }

func (g gormTenancyWriter) Create(value any) error { return g.tx.Create(value).Error }

func (g gormTenancyWriter) SetLastActiveOrganization(userID, orgID uuid.UUID) error {
	return g.tx.Model(&models.ApplicationUser{}).Where("id = ?", userID).
		Update("last_active_organization_id", orgID).Error
}

func (g gormTenancyWriter) DeleteMembership(orgID, userID uuid.UUID) error {
	return g.tx.Where("organization_id = ? AND user_id = ?", orgID, userID).
		Delete(&models.OrganizationMembership{}).Error
}

func (g gormTenancyWriter) EnsureMembership(m *models.OrganizationMembership) error {
	return g.tx.Clauses(clause.OnConflict{DoNothing: true}).Create(m).Error
}

// PersonalOrgSlug is the unique slug of a user's Personal workspace; it matches
// the A1 backfill so a re-run can never collide with a migrated row.
func PersonalOrgSlug(userID uuid.UUID) string {
	return "personal-" + strings.ReplaceAll(userID.String(), "-", "")
}

// ProvisionUserTenancy gives an already-inserted user their workspaces inside
// the caller's transaction: a Personal org with the user as its Owner, in
// SelfHosted mode a Default membership (Owner for platform admins, otherwise
// Member), and for platform admins an Owner membership of the Platform org.
// It also points last_active_organization_id at the Personal org.
func ProvisionUserTenancy(tx *gorm.DB, user *models.ApplicationUser) error {
	return provisionTenancy(gormTenancyWriter{tx}, user, config.CloudTenancy())
}

// ErrServiceAccountTenancy: a service account never gets a Personal org or memberships from the Go API.
var ErrServiceAccountTenancy = errors.New("service accounts are not provisioned by the API")

func provisionTenancy(w tenancyWriter, user *models.ApplicationUser, cloud bool) error {
	if models.IsServiceAccount(user) {
		return ErrServiceAccountTenancy
	}
	now := time.Now().UTC()
	creator := user.ID

	personal := models.Organization{
		ID:                uuid.New(),
		Name:              "Personal",
		Slug:              PersonalOrgSlug(user.ID),
		Kind:              models.OrgKindPersonal,
		CreatedByUserID:   &creator,
		ComputePreference: "Auto",
		CreatedAt:         now,
		UpdatedAt:         now,
	}
	if err := w.Create(&personal); err != nil {
		return fmt.Errorf("create personal organization: %w", err)
	}
	if err := w.Create(&models.OrganizationMembership{
		OrganizationID: personal.ID, UserID: user.ID, Role: models.OrgRoleOwner, CreatedAt: now,
	}); err != nil {
		return fmt.Errorf("create personal membership: %w", err)
	}

	if !cloud {
		role := models.OrgRoleMember
		if user.IsAdmin {
			role = models.OrgRoleOwner
		}
		if err := w.Create(&models.OrganizationMembership{
			OrganizationID: models.DefaultOrgID, UserID: user.ID, Role: role, CreatedAt: now,
		}); err != nil {
			return fmt.Errorf("create default membership: %w", err)
		}
	}

	if user.IsAdmin {
		if err := w.Create(&models.OrganizationMembership{
			OrganizationID: models.PlatformOrgID, UserID: user.ID, Role: models.OrgRoleOwner, CreatedAt: now,
		}); err != nil {
			return fmt.Errorf("create platform membership: %w", err)
		}
	}

	if err := w.SetLastActiveOrganization(user.ID, personal.ID); err != nil {
		return fmt.Errorf("set last active organization: %w", err)
	}
	user.LastActiveOrganizationID = &personal.ID
	return nil
}

// syncPlatformMembership keeps the Platform org's Owner row in step with
// application_users.is_admin, so a promotion or demotion is mirrored in the
// membership table that A3a-era tooling reads.
func syncPlatformMembership(w tenancyWriter, userID uuid.UUID, isAdmin bool) error {
	if isAdmin {
		return w.EnsureMembership(&models.OrganizationMembership{
			OrganizationID: models.PlatformOrgID, UserID: userID,
			Role: models.OrgRoleOwner, CreatedAt: time.Now().UTC(),
		})
	}
	return w.DeleteMembership(models.PlatformOrgID, userID)
}

// SyncPlatformMembership is syncPlatformMembership over a GORM transaction.
func SyncPlatformMembership(tx *gorm.DB, userID uuid.UUID, isAdmin bool) error {
	// A service account is never a platform admin and never holds a Platform membership.
	var n int64
	if err := tx.Model(&models.ApplicationUser{}).
		Where("id = ? AND is_service_account = ?", userID, true).Count(&n).Error; err != nil {
		return err
	}
	if n > 0 {
		return nil
	}
	return syncPlatformMembership(gormTenancyWriter{tx}, userID, isAdmin)
}
