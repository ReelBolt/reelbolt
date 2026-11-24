package models

import (
	"time"

	"github.com/google/uuid"
)

type ApplicationUser struct {
	ID                 uuid.UUID `gorm:"type:uuid;primaryKey"`
	Email              string    `gorm:"column:email;uniqueIndex"`
	DisplayName        string    `gorm:"column:display_name"`
	CreatedAt          time.Time `gorm:"column:created_at"`
	PasswordHash       string    `gorm:"column:password_hash"`
	IsAdmin            bool      `gorm:"column:is_admin"`
	MustChangePassword bool      `gorm:"column:must_change_password"`

	// LastActiveOrganizationID: login picks it again while the user remains a member (A3a).
	LastActiveOrganizationID *uuid.UUID `gorm:"type:uuid;column:last_active_organization_id"`

	// Account lifecycle columns (docs/design/saas-accounts.md). The schema is owned by the
	// Inference API's EF migrations; pointers map the nullable columns. PasswordHash above is
	// never empty: an OAuth-only account carries a real, unusable bcrypt hash of a random
	// value (services.GeneratePlaceholderPasswordHash), so the login path does the same
	// work for it as for a password account and its timing does not disclose that it is
	// OAuth-only (C11 F-3).
	EmailVerifiedAt     *time.Time `gorm:"column:email_verified_at"`
	TokensInvalidatedAt *time.Time `gorm:"column:tokens_invalidated_at"`
	DeletionRequestedAt *time.Time `gorm:"column:deletion_requested_at"`
	SignupIP            *string    `gorm:"column:signup_ip"`
	AuthProvider        string     `gorm:"column:auth_provider;default:Password"`

	// McpAdminToolsEnabled is the per-user opt-in for the gated assistant tools over MCP
	// (docs/design/mcp-oauth.md §3/§6). Off by default and set from the web portal. It is read
	// here at consent time to decide whether `assistant.admin` may be granted at all, and it is
	// re-read per call by the Inference API's bridge, which is where it is enforced.
	McpAdminToolsEnabled bool `gorm:"column:mcp_admin_tools_enabled"`
	// McpMediaEgressEnabled is the per-user opt-in that lets connected tools (MCP) allow sending the
	// user's media to AI services for a run they start. Off by default; the Inference API reads it
	// live on every such call. EF-owned column, never migrated here.
	McpMediaEgressEnabled bool `gorm:"column:mcp_media_egress_enabled"`

	// Locale is the user's saved UI/e-mail language (application_users.locale, nullable; nil =
	// default). Validated against services/locales.json before it is stored.
	Locale *string `gorm:"column:locale" json:"locale"`

	// IsServiceAccount marks the hidden per-API-key user. It is created
	// only by the Inference API and is read-only here: such a user is a non-person and can never
	// sign in, receive mail, be listed as a member or hold an admin role.
	IsServiceAccount bool `gorm:"column:is_service_account;->"`
}

// ServiceAccountEmailDomain is the reserved domain of service-account e-mails
// (apikey+<keyid>@service.invalid). Signup refuses it.
const ServiceAccountEmailDomain = "@service.invalid"

// IsServiceAccount reports whether u is an API-key service account.
func IsServiceAccount(u *ApplicationUser) bool {
	return u != nil && u.IsServiceAccount
}

func (ApplicationUser) TableName() string {
	return "application_users"
}
