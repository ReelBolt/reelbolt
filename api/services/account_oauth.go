package services

import (
	"fmt"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
)

// OAuth account persistence (C10). The handler owns the protocol (PKCE, state,
// token exchange, identity parsing); this is only what has to be atomic: a new
// account with its Personal workspace, and the one column an existing account
// gains from a provider that has already proven the address.

// RegisterOAuthUser creates an account the provider verified and provisions its
// workspaces in the same transaction, exactly like RegisterUser does for
// signup. There is no UserToken row and no legal acceptance: a first OAuth
// login has no form to accept on, so consent is captured by the /accept-terms
// page afterwards (the same endpoint a re-acceptance uses).
func (GormAccountStore) RegisterOAuthUser(user *models.ApplicationUser) error {
	err := database.DB.Transaction(func(tx *gorm.DB) error {
		if err := tx.Create(user).Error; err != nil {
			return fmt.Errorf("create user: %w", err)
		}
		return ProvisionUserTenancy(tx, user)
	})
	if err != nil {
		return err
	}
	// After the transaction: the first OAuth sign-in created a Personal workspace, and a lost
	// funnel row must not fail the sign-in.
	recordPersonalWorkspaceCreatedBestEffort(user)
	return nil
}

// MarkEmailVerified stamps email_verified_at when it is still NULL. Linking a
// password account to an OAuth identity is only allowed for an address the
// provider reports as verified, so this is the one write that link needs.
func (GormAccountStore) MarkEmailVerified(userID uuid.UUID, at time.Time) error {
	return database.DB.Model(&models.ApplicationUser{}).
		Where("id = ? AND email_verified_at IS NULL", userID).
		Update("email_verified_at", at).Error
}
