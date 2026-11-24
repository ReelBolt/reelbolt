package admin

import (
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/models"
)

type CreateUserRequest struct {
	Email       string `json:"email"`
	DisplayName string `json:"displayName"`
	IsAdmin     bool   `json:"isAdmin"`
	// IsPlatformAdmin is the preferred name; either one grants platform authority.
	IsPlatformAdmin bool `json:"isPlatformAdmin"`
}

type CreateUserResponse struct {
	User              UserResponse `json:"user"`
	TemporaryPassword string       `json:"temporaryPassword"`
}

type UpdateUserRequest struct {
	Email       *string `json:"email,omitempty"`
	DisplayName *string `json:"displayName,omitempty"`
	IsAdmin     *bool   `json:"isAdmin,omitempty"`
	// IsPlatformAdmin takes precedence over its alias IsAdmin when both are sent.
	IsPlatformAdmin *bool `json:"isPlatformAdmin,omitempty"`
	ResetPassword   bool  `json:"resetPassword,omitempty"`
}

type UserResponse struct {
	ID                 uuid.UUID `json:"id"`
	Email              string    `json:"email"`
	DisplayName        string    `json:"displayName"`
	IsAdmin            bool      `json:"isAdmin"` // alias of isPlatformAdmin, one release
	IsPlatformAdmin    bool      `json:"isPlatformAdmin"`
	MustChangePassword bool      `json:"mustChangePassword"`
	CreatedAt          time.Time `json:"createdAt"`
	// EmailVerified: the current address is verified (an email change needs it).
	EmailVerified bool `json:"emailVerified"`
	// PendingEmailChange is the open, unexpired email change request, or null (issue #1207).
	PendingEmailChange *PendingEmailChangeResponse `json:"pendingEmailChange"`
}

// PendingEmailChangeResponse is an open email change request as the admin sees it.
type PendingEmailChangeResponse struct {
	NewEmail    string    `json:"newEmail"`
	RequestedAt time.Time `json:"requestedAt"`
	ExpiresAt   time.Time `json:"expiresAt"`
}

// RequestEmailChangeRequest is the body of POST /admin/users/{id}/email-change.
type RequestEmailChangeRequest struct {
	NewEmail string `json:"newEmail"`
}

// RequestEmailChangeResponse is its 202 body.
type RequestEmailChangeResponse struct {
	PendingEmail string    `json:"pendingEmail"`
	ExpiresAt    time.Time `json:"expiresAt"`
}

func toUserResponse(u *models.ApplicationUser) UserResponse {
	return UserResponse{
		ID:                 u.ID,
		Email:              u.Email,
		DisplayName:        u.DisplayName,
		IsAdmin:            u.IsAdmin,
		IsPlatformAdmin:    u.IsAdmin,
		MustChangePassword: u.MustChangePassword,
		CreatedAt:          u.CreatedAt,
		EmailVerified:      u.EmailVerifiedAt != nil,
	}
}
