package services

import (
	"context"
	"strings"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
)

// localeLookup is implemented by notification stores that can also read a user's saved locale.
// It is optional so existing stores/fakes keep working; "" means "not known, use the default".
type localeLookup interface {
	UserLocaleByID(ctx context.Context, userID uuid.UUID) string
}

// UserLocaleByID returns the saved locale of a user ("" when unset, unknown or the column is absent).
func (GormNotificationStore) UserLocaleByID(ctx context.Context, userID uuid.UUID) string {
	var loc *string
	if database.DB == nil {
		return ""
	}
	if err := database.DB.WithContext(ctx).Model(&models.ApplicationUser{}).
		Where("id = ?", userID).Select("locale").Scan(&loc).Error; err != nil {
		return ""
	}
	return UserLocale(loc)
}

// UserLocaleByEmail returns the saved locale of the account with this address ("" when none).
func UserLocaleByEmail(email string) string {
	if database.DB == nil {
		return ""
	}
	var loc *string
	if err := database.DB.Model(&models.ApplicationUser{}).
		Where("lower(email) = ?", strings.ToLower(strings.TrimSpace(email))).
		Select("locale").Scan(&loc).Error; err != nil {
		return ""
	}
	return UserLocale(loc)
}
