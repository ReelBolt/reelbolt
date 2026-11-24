package services

import (
	"context"
	"errors"
	"fmt"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
)

// ActiveMembership describes a user's membership in the organization they are
// currently working in.
type ActiveMembership struct {
	OrgID   uuid.UUID
	OrgName string
	OrgKind string
	Role    string
	// PlatformAdmin is the user's current platform authority, read in the same
	// query so a demotion takes effect with the membership cache TTL.
	PlatformAdmin bool
}

// MembershipLookup resolves a user's membership in one organization. It
// returns (nil, nil) when the user is not a member of a live organization, so
// callers can tell "revoked" from "database error". It is an interface so the
// auth middleware and handlers are unit-testable without a database.
type MembershipLookup interface {
	GetMembership(ctx context.Context, userID, orgID uuid.UUID) (*ActiveMembership, error)
}

// GormMembershipLookup is the production MembershipLookup over database.DB.
type GormMembershipLookup struct{}

type membershipRow struct {
	OrgID         uuid.UUID `gorm:"column:org_id"`
	OrgName       string    `gorm:"column:org_name"`
	OrgKind       string    `gorm:"column:org_kind"`
	Role          string    `gorm:"column:role"`
	PlatformAdmin bool      `gorm:"column:platform_admin"`
}

func (GormMembershipLookup) GetMembership(ctx context.Context, userID, orgID uuid.UUID) (*ActiveMembership, error) {
	var rows []membershipRow
	err := database.DB.WithContext(ctx).
		Table("organization_memberships AS m").
		Select("o.id AS org_id, o.name AS org_name, o.kind AS org_kind, m.role AS role, u.is_admin AS platform_admin").
		Joins("JOIN organizations o ON o.id = m.organization_id AND o.deleted_at IS NULL").
		Joins("JOIN application_users u ON u.id = m.user_id").
		Where("m.user_id = ? AND m.organization_id = ? AND u.is_service_account = false", userID, orgID).
		Limit(1).
		Scan(&rows).Error
	if err != nil {
		return nil, err
	}
	if len(rows) == 0 {
		return nil, nil
	}
	r := rows[0]
	return &ActiveMembership{OrgID: r.OrgID, OrgName: r.OrgName, OrgKind: r.OrgKind, Role: r.Role, PlatformAdmin: r.PlatformAdmin}, nil
}

// ErrNoWorkspace means the user belongs to no organization they can work in.
var ErrNoWorkspace = errors.New("user has no workspace")

// ResolveActiveMembership picks the organization a login should land in: the
// user's last active org while they are still a member, otherwise their
// Personal workspace, otherwise any other workspace. The Platform org is never
// a workspace and is never returned.
func ResolveActiveMembership(ctx context.Context, user *models.ApplicationUser) (*ActiveMembership, error) {
	// A service account (one per API key) is a non-person: it never gets a session.
	if models.IsServiceAccount(user) {
		return nil, ErrNoWorkspace
	}
	if user.LastActiveOrganizationID != nil {
		m, err := (GormMembershipLookup{}).GetMembership(ctx, user.ID, *user.LastActiveOrganizationID)
		if err != nil {
			return nil, err
		}
		if m != nil && m.OrgKind != models.OrgKindPlatform {
			return m, nil
		}
	}

	var rows []membershipRow
	err := database.DB.WithContext(ctx).
		Table("organization_memberships AS m").
		Select("o.id AS org_id, o.name AS org_name, o.kind AS org_kind, m.role AS role, u.is_admin AS platform_admin").
		Joins("JOIN organizations o ON o.id = m.organization_id AND o.deleted_at IS NULL").
		Joins("JOIN application_users u ON u.id = m.user_id").
		Where("m.user_id = ? AND o.kind <> ?", user.ID, models.OrgKindPlatform).
		// Personal first, then oldest membership, so the choice is stable.
		Order(gorm.Expr("CASE o.kind WHEN ? THEN 0 ELSE 1 END, m.created_at, o.id", models.OrgKindPersonal)).
		Limit(1).
		Scan(&rows).Error
	if err != nil {
		return nil, err
	}
	if len(rows) == 0 {
		return nil, ErrNoWorkspace
	}
	r := rows[0]
	return &ActiveMembership{OrgID: r.OrgID, OrgName: r.OrgName, OrgKind: r.OrgKind, Role: r.Role, PlatformAdmin: r.PlatformAdmin}, nil
}

// SetLastActiveOrganization records the org a user switched to.
func SetLastActiveOrganization(ctx context.Context, userID, orgID uuid.UUID) error {
	if err := database.DB.WithContext(ctx).Model(&models.ApplicationUser{}).
		Where("id = ?", userID).
		Update("last_active_organization_id", orgID).Error; err != nil {
		return fmt.Errorf("set last active organization: %w", err)
	}
	return nil
}
