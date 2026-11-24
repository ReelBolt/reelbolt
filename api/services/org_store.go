package services

import (
	"context"
	"errors"
	"regexp"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
	"gorm.io/gorm/clause"
)

// ErrLastOwner means the operation would leave a Team org with no Owner.
var ErrLastOwner = errors.New("organization must keep at least one owner")

// OrgSummary is one organization a user belongs to, with their role in it.
type OrgSummary struct {
	Org  models.Organization
	Role string
}

// OrgMember is one membership row joined with the user's identity.
type OrgMember struct {
	UserID      uuid.UUID
	Email       string
	DisplayName string
	Role        string
	JoinedAt    time.Time
}

// OrgStore is the persistence the organizations API needs. It is an interface
// so the handlers (and every role branch in them) are testable without a
// database; GormOrgStore is the production implementation.
type OrgStore interface {
	ListUserOrgs(ctx context.Context, userID uuid.UUID) ([]OrgSummary, error)
	CreateTeamOrg(ctx context.Context, userID uuid.UUID, name string) (*models.Organization, error)
	UpdateOrgName(ctx context.Context, orgID uuid.UUID, name string) error
	// UpdateOrgComputePreference writes organizations.compute_preference (E8-UI).
	// The engine reads that column per decision through IComputeEntitlements and
	// never caches it, so a change takes effect on the next step.
	UpdateOrgComputePreference(ctx context.Context, orgID uuid.UUID, preference string) error
	// DeleteOrg soft-deletes the organization (sets deleted_at). Membership
	// lookups join on deleted_at IS NULL, so access ends immediately.
	DeleteOrg(ctx context.Context, orgID uuid.UUID) error

	ListMembers(ctx context.Context, orgID uuid.UUID) ([]OrgMember, error)
	// GetMemberRole returns ("", false, nil) when userID is not a member.
	GetMemberRole(ctx context.Context, orgID, userID uuid.UUID) (string, bool, error)
	CountOwners(ctx context.Context, orgID uuid.UUID) (int, error)
	// SetMemberRole and RemoveMember re-check the last-Owner invariant inside
	// their transaction and return ErrLastOwner when it would break.
	SetMemberRole(ctx context.Context, orgID, userID uuid.UUID, role string) error
	RemoveMember(ctx context.Context, orgID, userID uuid.UUID) error
	IsEmailMember(ctx context.Context, orgID uuid.UUID, email string) (bool, error)

	// CreateInvite revokes any still-pending invite for the same email first.
	CreateInvite(ctx context.Context, inv *models.OrganizationInvite) error
	ListPendingInvites(ctx context.Context, orgID uuid.UUID, now time.Time) ([]models.OrganizationInvite, error)
	RevokeInvite(ctx context.Context, orgID, inviteID uuid.UUID, now time.Time) (bool, error)
	// FindInviteByTokenHash returns (nil, nil) when no invite has that hash.
	FindInviteByTokenHash(ctx context.Context, hash string) (*models.OrganizationInvite, error)
	// AcceptInvite marks the invite accepted and inserts the membership in one
	// transaction (an existing membership is left untouched).
	AcceptInvite(ctx context.Context, inv *models.OrganizationInvite, userID uuid.UUID, now time.Time) error
	// GetOrganization returns (nil, nil) for an unknown or deleted org.
	GetOrganization(ctx context.Context, orgID uuid.UUID) (*models.Organization, error)
	GetUserIdentity(ctx context.Context, userID uuid.UUID) (email, displayName string, err error)
}

var slugCleaner = regexp.MustCompile(`[^a-z0-9]+`)

// TeamOrgSlug derives a unique slug for a Team org from its name: a lowercase
// ascii stem (at most 60 chars) plus a random 8-hex suffix so two orgs with the
// same name never collide on the unique slug index.
func TeamOrgSlug(name string) string {
	stem := strings.Trim(slugCleaner.ReplaceAllString(strings.ToLower(name), "-"), "-")
	if len(stem) > 60 {
		stem = strings.Trim(stem[:60], "-")
	}
	if stem == "" {
		stem = "team"
	}
	return stem + "-" + strings.ReplaceAll(uuid.NewString(), "-", "")[:8]
}

// GormOrgStore is the production OrgStore over database.DB.
type GormOrgStore struct{}

func (GormOrgStore) ListUserOrgs(ctx context.Context, userID uuid.UUID) ([]OrgSummary, error) {
	type row struct {
		models.Organization
		MemberRole string `gorm:"column:member_role"`
	}
	var rows []row
	err := database.DB.WithContext(ctx).
		Table("organization_memberships AS m").
		Select("o.*, m.role AS member_role").
		Joins("JOIN organizations o ON o.id = m.organization_id AND o.deleted_at IS NULL").
		Where("m.user_id = ? AND o.kind <> ?", userID, models.OrgKindPlatform).
		Order("CASE o.kind WHEN 'Personal' THEN 0 ELSE 1 END, o.name, o.id").
		Scan(&rows).Error
	if err != nil {
		return nil, err
	}
	out := make([]OrgSummary, len(rows))
	for i, r := range rows {
		out[i] = OrgSummary{Org: r.Organization, Role: r.MemberRole}
	}
	return out, nil
}

func (GormOrgStore) CreateTeamOrg(ctx context.Context, userID uuid.UUID, name string) (*models.Organization, error) {
	now := time.Now().UTC()
	org := models.Organization{
		ID: uuid.New(), Name: name, Slug: TeamOrgSlug(name),
		Kind: models.OrgKindTeam, CreatedByUserID: &userID,
		ComputePreference: "Auto", CreatedAt: now, UpdatedAt: now,
	}
	err := database.DB.WithContext(ctx).Transaction(func(tx *gorm.DB) error {
		if err := tx.Create(&org).Error; err != nil {
			return err
		}
		return tx.Create(&models.OrganizationMembership{
			OrganizationID: org.ID, UserID: userID, Role: models.OrgRoleOwner, CreatedAt: now,
		}).Error
	})
	if err != nil {
		return nil, err
	}
	// After the transaction: the team workspace exists, and a lost funnel row must not undo it.
	RecordProductEventBestEffort(database.DB, userID, &org.ID, ProductEventWorkspaceCreated)
	return &org, nil
}

func (GormOrgStore) UpdateOrgName(ctx context.Context, orgID uuid.UUID, name string) error {
	return database.DB.WithContext(ctx).Model(&models.Organization{}).
		Where("id = ? AND deleted_at IS NULL", orgID).
		Updates(map[string]any{"name": name, "updated_at": time.Now().UTC()}).Error
}

func (GormOrgStore) UpdateOrgComputePreference(ctx context.Context, orgID uuid.UUID, preference string) error {
	return database.DB.WithContext(ctx).Model(&models.Organization{}).
		Where("id = ? AND deleted_at IS NULL", orgID).
		Updates(map[string]any{"compute_preference": preference, "updated_at": time.Now().UTC()}).Error
}

func (GormOrgStore) DeleteOrg(ctx context.Context, orgID uuid.UUID) error {
	now := time.Now().UTC()
	// Soft-delete only. The org's API-key service accounts are hidden users whose only
	// membership is this org, but they are NOT deleted here: the Inference API's
	// AccountPurgeService deletes them (keys and memberships first) when the workspace is
	// purged. Hard-deleting them from this soft delete cascaded through projects.owner_id
	// into fk_workflow_executions_projects_project_id (RESTRICT), so any key-created project
	// with a run made the whole delete answer 500, and API-created projects vanished.
	return database.DB.WithContext(ctx).Model(&models.Organization{}).
		Where("id = ? AND deleted_at IS NULL", orgID).
		Updates(map[string]any{"deleted_at": now, "updated_at": now}).Error
}

func (GormOrgStore) ListMembers(ctx context.Context, orgID uuid.UUID) ([]OrgMember, error) {
	type row struct {
		UserID      uuid.UUID `gorm:"column:user_id"`
		Email       string    `gorm:"column:email"`
		DisplayName string    `gorm:"column:display_name"`
		Role        string    `gorm:"column:role"`
		JoinedAt    time.Time `gorm:"column:joined_at"`
	}
	var rows []row
	err := database.DB.WithContext(ctx).
		Table("organization_memberships AS m").
		Select("m.user_id, u.email, u.display_name, m.role, m.created_at AS joined_at").
		Joins("JOIN application_users u ON u.id = m.user_id").
		Where("m.organization_id = ? AND u.is_service_account = ?", orgID, false).
		Order("m.created_at, u.email").
		Scan(&rows).Error
	if err != nil {
		return nil, err
	}
	out := make([]OrgMember, len(rows))
	for i, r := range rows {
		out[i] = OrgMember(r)
	}
	return out, nil
}

func (GormOrgStore) GetMemberRole(ctx context.Context, orgID, userID uuid.UUID) (string, bool, error) {
	var m models.OrganizationMembership
	// A service account is never a member as far as the member routes are concerned.
	err := database.DB.WithContext(ctx).
		Where("organization_id = ? AND user_id = ?", orgID, userID).
		Where("NOT EXISTS (SELECT 1 FROM application_users u WHERE u.id = organization_memberships.user_id AND u.is_service_account = ?)", true).
		First(&m).Error
	if errors.Is(err, gorm.ErrRecordNotFound) {
		return "", false, nil
	}
	if err != nil {
		return "", false, err
	}
	return m.Role, true, nil
}

func (GormOrgStore) CountOwners(ctx context.Context, orgID uuid.UUID) (int, error) {
	var n int64
	// A service account is never an owner: it must not keep a Team workspace alive.
	err := database.DB.WithContext(ctx).Model(&models.OrganizationMembership{}).
		Joins("JOIN application_users u ON u.id = organization_memberships.user_id").
		Where("organization_memberships.organization_id = ? AND organization_memberships.role = ? AND u.is_service_account = ?", orgID, models.OrgRoleOwner, false).
		Count(&n).Error
	return int(n), err
}

// lockedOwnerCount counts Owners while holding row locks on them, so two
// concurrent demotions/removals serialize and the loser sees the true count.
// Service accounts are excluded: a hidden API-key user cannot hold an Owner role.
func lockedOwnerCount(tx *gorm.DB, orgID uuid.UUID) (int, error) {
	var owners []models.OrganizationMembership
	err := tx.Clauses(clause.Locking{Strength: "UPDATE", Table: clause.Table{Name: "organization_memberships"}}).
		Joins("JOIN application_users u ON u.id = organization_memberships.user_id").
		Where("organization_memberships.organization_id = ? AND organization_memberships.role = ? AND u.is_service_account = ?", orgID, models.OrgRoleOwner, false).
		Find(&owners).Error
	return len(owners), err
}

// isServiceAccountUser reports whether the user row is an API-key service account.
func isServiceAccountUser(tx *gorm.DB, userID uuid.UUID) (bool, error) {
	var n int64
	err := tx.Model(&models.ApplicationUser{}).
		Where("id = ? AND is_service_account = ?", userID, true).Count(&n).Error
	return n > 0, err
}

func (GormOrgStore) SetMemberRole(ctx context.Context, orgID, userID uuid.UUID, role string) error {
	return database.DB.WithContext(ctx).Transaction(func(tx *gorm.DB) error {
		// A service-account target is invisible here: 404, keys are revoked in the Inference API.
		if svc, err := isServiceAccountUser(tx, userID); err != nil {
			return err
		} else if svc {
			return gorm.ErrRecordNotFound
		}
		n, err := lockedOwnerCount(tx, orgID)
		if err != nil {
			return err
		}
		var cur models.OrganizationMembership
		if err := tx.Where("organization_id = ? AND user_id = ?", orgID, userID).First(&cur).Error; err != nil {
			return err
		}
		if cur.Role == models.OrgRoleOwner && role != models.OrgRoleOwner && n <= 1 {
			return ErrLastOwner
		}
		return tx.Model(&models.OrganizationMembership{}).
			Where("organization_id = ? AND user_id = ?", orgID, userID).
			Update("role", role).Error
	})
}

func (GormOrgStore) RemoveMember(ctx context.Context, orgID, userID uuid.UUID) error {
	return database.DB.WithContext(ctx).Transaction(func(tx *gorm.DB) error {
		if svc, err := isServiceAccountUser(tx, userID); err != nil {
			return err
		} else if svc {
			return gorm.ErrRecordNotFound
		}
		n, err := lockedOwnerCount(tx, orgID)
		if err != nil {
			return err
		}
		var cur models.OrganizationMembership
		if err := tx.Where("organization_id = ? AND user_id = ?", orgID, userID).First(&cur).Error; err != nil {
			return err
		}
		if cur.Role == models.OrgRoleOwner && n <= 1 {
			return ErrLastOwner
		}
		return tx.Where("organization_id = ? AND user_id = ?", orgID, userID).
			Delete(&models.OrganizationMembership{}).Error
	})
}

func (GormOrgStore) IsEmailMember(ctx context.Context, orgID uuid.UUID, email string) (bool, error) {
	var n int64
	err := database.DB.WithContext(ctx).
		Table("organization_memberships AS m").
		Joins("JOIN application_users u ON u.id = m.user_id").
		Where("m.organization_id = ? AND lower(u.email) = lower(?) AND u.is_service_account = false", orgID, email).
		Count(&n).Error
	return n > 0, err
}

func (GormOrgStore) CreateInvite(ctx context.Context, inv *models.OrganizationInvite) error {
	return database.DB.WithContext(ctx).Transaction(func(tx *gorm.DB) error {
		if err := tx.Model(&models.OrganizationInvite{}).
			Where("organization_id = ? AND lower(email) = lower(?) AND accepted_at IS NULL AND revoked_at IS NULL",
				inv.OrganizationID, inv.Email).
			Update("revoked_at", inv.CreatedAt).Error; err != nil {
			return err
		}
		return tx.Create(inv).Error
	})
}

func (GormOrgStore) ListPendingInvites(ctx context.Context, orgID uuid.UUID, now time.Time) ([]models.OrganizationInvite, error) {
	var out []models.OrganizationInvite
	err := database.DB.WithContext(ctx).
		Where("organization_id = ? AND accepted_at IS NULL AND revoked_at IS NULL AND expires_at > ?", orgID, now).
		Order("created_at DESC").Find(&out).Error
	return out, err
}

func (GormOrgStore) RevokeInvite(ctx context.Context, orgID, inviteID uuid.UUID, now time.Time) (bool, error) {
	res := database.DB.WithContext(ctx).Model(&models.OrganizationInvite{}).
		Where("id = ? AND organization_id = ? AND accepted_at IS NULL AND revoked_at IS NULL", inviteID, orgID).
		Update("revoked_at", now)
	return res.RowsAffected > 0, res.Error
}

func (GormOrgStore) FindInviteByTokenHash(ctx context.Context, hash string) (*models.OrganizationInvite, error) {
	var inv models.OrganizationInvite
	err := database.DB.WithContext(ctx).Where("token_hash = ?", hash).First(&inv).Error
	if errors.Is(err, gorm.ErrRecordNotFound) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &inv, nil
}

// acceptInviteTx marks the invite accepted and inserts the membership inside
// tx. The conditional update makes accept single-use even under a race; an
// existing membership is left untouched. Shared by GormOrgStore.AcceptInvite
// (existing user) and GormAccountStore.RegisterInvitedUser (new user).
func acceptInviteTx(tx *gorm.DB, inv *models.OrganizationInvite, userID uuid.UUID, now time.Time) error {
	if svc, err := isServiceAccountUser(tx, userID); err != nil {
		return err
	} else if svc {
		return gorm.ErrRecordNotFound
	}
	res := tx.Model(&models.OrganizationInvite{}).
		Where("id = ? AND accepted_at IS NULL AND revoked_at IS NULL", inv.ID).
		Update("accepted_at", now)
	if res.Error != nil {
		return res.Error
	}
	if res.RowsAffected == 0 {
		return gorm.ErrRecordNotFound
	}
	return tx.Clauses(clause.OnConflict{DoNothing: true}).Create(&models.OrganizationMembership{
		OrganizationID: inv.OrganizationID, UserID: userID, Role: inv.Role, CreatedAt: now,
	}).Error
}

func (GormOrgStore) AcceptInvite(ctx context.Context, inv *models.OrganizationInvite, userID uuid.UUID, now time.Time) error {
	err := database.DB.WithContext(ctx).Transaction(func(tx *gorm.DB) error {
		return acceptInviteTx(tx, inv, userID, now)
	})
	if err != nil {
		return err
	}
	// After the transaction: the membership exists, and a lost funnel row must not revoke it.
	RecordProductEventBestEffort(database.DB, userID, &inv.OrganizationID, ProductEventInviteAccepted)
	return nil
}

func (GormOrgStore) GetOrganization(ctx context.Context, orgID uuid.UUID) (*models.Organization, error) {
	var o models.Organization
	err := database.DB.WithContext(ctx).Where("id = ? AND deleted_at IS NULL", orgID).First(&o).Error
	if errors.Is(err, gorm.ErrRecordNotFound) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &o, nil
}

func (GormOrgStore) GetUserIdentity(ctx context.Context, userID uuid.UUID) (string, string, error) {
	var u models.ApplicationUser
	// A service account has no identity a member route may show: it reads as absent.
	if err := database.DB.WithContext(ctx).Select("email", "display_name").
		Where("id = ? AND is_service_account = ?", userID, false).First(&u).Error; err != nil {
		return "", "", err
	}
	return u.Email, u.DisplayName, nil
}
