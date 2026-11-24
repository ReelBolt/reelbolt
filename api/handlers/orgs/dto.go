package orgs

import "time"

// OrgResponse is one organization as seen by a member. Role is the caller's.
type OrgResponse struct {
	ID        string    `json:"id"`
	Name      string    `json:"name"`
	Slug      string    `json:"slug"`
	Kind      string    `json:"kind"`
	Role      string    `json:"role"`
	CreatedAt time.Time `json:"createdAt"`
	// ComputePreference is where this workspace PREFERS its media work to run
	// (E8-UI): Auto | CloudOnly | PreferRunner | RunnerOnly, from
	// organizations.compute_preference. It is a request, not a guarantee - the
	// engine only consults it when the deployment runs Compute:Mode=Fabric, and a
	// plan that withholds runners downgrades it to CloudOnly.
	ComputePreference string `json:"computePreference"`
}

type CreateOrgRequest struct {
	Name string `json:"name"`
}

// UpdateOrgRequest is a partial update: a field left out is not touched. At
// least one field must be present.
type UpdateOrgRequest struct {
	Name              *string `json:"name"`
	ComputePreference *string `json:"computePreference"`
}

type MemberResponse struct {
	UserID      string    `json:"userId"`
	Email       string    `json:"email"`
	DisplayName string    `json:"displayName"`
	Role        string    `json:"role"`
	JoinedAt    time.Time `json:"joinedAt"`
}

type UpdateMemberRequest struct {
	Role string `json:"role"`
}

// InviteResponse never carries the token: it is only ever emailed.
type InviteResponse struct {
	ID        string    `json:"id"`
	Email     string    `json:"email"`
	Role      string    `json:"role"`
	CreatedAt time.Time `json:"createdAt"`
	ExpiresAt time.Time `json:"expiresAt"`
	// EmailSent is false when SMTP is unconfigured or the send failed; the
	// invite exists either way and can be revoked and re-sent.
	EmailSent *bool `json:"emailSent,omitempty"`
}

type CreateInviteRequest struct {
	Email string `json:"email"`
	Role  string `json:"role"`
	// Locale is optional (allowlisted): the invitee's language when they have no account yet.
	Locale string `json:"locale,omitempty"`
}

type AcceptInviteRequest struct {
	Token string `json:"token"`
}

type AcceptInviteResponse struct {
	OrganizationID string `json:"organizationId"`
	Name           string `json:"name"`
	Role           string `json:"role"`
}
