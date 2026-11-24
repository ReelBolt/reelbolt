package models

import (
	"time"

	"github.com/google/uuid"
)

// Organization kinds and roles are stored as strings by the Inference API
// schema (docs/design/tenancy.md). The Go API maps the tables read/write but
// never migrates them. Values are append-only; never rename.
const (
	OrgKindPersonal = "Personal"
	OrgKindTeam     = "Team"
	OrgKindPlatform = "Platform"

	OrgRoleOwner   = "Owner"
	OrgRoleAdmin   = "Admin"
	OrgRoleMember  = "Member"
	OrgRoleBilling = "Billing"
)

// Compute preferences (ReelBolt.Shared.Tenancy.ComputePreference). Stored as a
// string on organizations.compute_preference, default "Auto". Append-only: the
// engine reads the column through IComputeEntitlements and switches over it
// exhaustively, so a value this build does not know behaves as "no runner"
// rather than failing the step.
const (
	ComputePreferenceAuto         = "Auto"
	ComputePreferenceCloudOnly    = "CloudOnly"
	ComputePreferencePreferRunner = "PreferRunner"
	ComputePreferenceRunnerOnly   = "RunnerOnly"
)

// ValidComputePreference reports whether s is one of the four values this build
// knows. The handler refuses anything else rather than writing a value the
// engine would silently read as something it is not.
func ValidComputePreference(s string) bool {
	switch s {
	case ComputePreferenceAuto, ComputePreferenceCloudOnly, ComputePreferencePreferRunner, ComputePreferenceRunnerOnly:
		return true
	default:
		return false
	}
}

// Fixed ids of the two organizations present in every deployment
// (ReelBolt.Shared.Tenancy.WellKnownOrganizations). Never change a value.
var (
	PlatformOrgID = uuid.MustParse("5c1f0000-0000-4000-8000-000000000001")
	DefaultOrgID  = uuid.MustParse("5c1f0000-0000-4000-8000-000000000002")
)

// Organization maps the organizations table.
type Organization struct {
	ID                uuid.UUID  `gorm:"type:uuid;primaryKey;column:id"`
	Name              string     `gorm:"column:name"`
	Slug              string     `gorm:"column:slug"`
	Kind              string     `gorm:"column:kind"`
	CreatedByUserID   *uuid.UUID `gorm:"type:uuid;column:created_by_user_id"`
	ComputePreference string     `gorm:"column:compute_preference;default:Auto"`
	CreatedAt         time.Time  `gorm:"column:created_at"`
	UpdatedAt         time.Time  `gorm:"column:updated_at"`
	DeletedAt         *time.Time `gorm:"column:deleted_at"`
}

func (Organization) TableName() string { return "organizations" }

// OrganizationMembership maps organization_memberships (PK: organization, user).
type OrganizationMembership struct {
	OrganizationID uuid.UUID `gorm:"type:uuid;primaryKey;column:organization_id"`
	UserID         uuid.UUID `gorm:"type:uuid;primaryKey;column:user_id"`
	Role           string    `gorm:"column:role"`
	CreatedAt      time.Time `gorm:"column:created_at"`
}

func (OrganizationMembership) TableName() string { return "organization_memberships" }

// OrganizationInvite maps organization_invites. A4/C8 own the behaviour; the
// model lives here so every Go table mapping is in one place.
type OrganizationInvite struct {
	ID              uuid.UUID  `gorm:"type:uuid;primaryKey;column:id"`
	OrganizationID  uuid.UUID  `gorm:"type:uuid;column:organization_id"`
	Email           string     `gorm:"column:email"`
	Role            string     `gorm:"column:role"`
	TokenHash       string     `gorm:"column:token_hash"`
	InvitedByUserID *uuid.UUID `gorm:"type:uuid;column:invited_by_user_id"`
	CreatedAt       time.Time  `gorm:"column:created_at"`
	ExpiresAt       time.Time  `gorm:"column:expires_at"`
	AcceptedAt      *time.Time `gorm:"column:accepted_at"`
	RevokedAt       *time.Time `gorm:"column:revoked_at"`
}

func (OrganizationInvite) TableName() string { return "organization_invites" }
