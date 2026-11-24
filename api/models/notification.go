package models

import (
	"time"

	"github.com/google/uuid"
)

// NotificationCategory names one switchable class of email notification. Values
// are append-only: the Inference API publishes activity messages carrying these
// strings, and stored rows address them by string too.
const (
	NotificationJobSucceeded   = "job_succeeded"
	NotificationJobFailed      = "job_failed"
	NotificationFileCreated    = "file_created"
	NotificationGraphicsEdited = "graphics_edited"
	// NotificationReviewActivity is a guest note or decision on a shared review
	// link. Switchable (column review_activity, default true).
	NotificationReviewActivity = "review_activity"
	// NotificationAPIKeyExpiring and NotificationWebhookDisabled are security
	// mails: always sent to their one recipient, no preference column.
	NotificationAPIKeyExpiring  = "api_key_expiring"
	NotificationWebhookDisabled = "webhook_disabled"
	// NotificationAvatarRetention warns an avatar's attesting member seven days
	// before the avatar is deleted by its retention date, so they can re-attest
	// to keep it. Switchable (column avatar_retention, default true).
	NotificationAvatarRetention = "avatar_retention"
	// NotificationSocialPublishing is a social post that was published, failed, or
	// an account that needs reconnecting. Switchable (column social_publishing,
	// default true); mailed to the one member who made the post or connected the account.
	NotificationSocialPublishing = "social_publishing"
)

// UserNotificationPreferences is a read/write GORM view of the
// user_notification_preferences table, whose schema the Inference API owns (EF
// Core migration AddUserNotifications). The Go API never migrates; it creates
// a row on first write and treats an absent row as "all on".
type UserNotificationPreferences struct {
	UserID           uuid.UUID `gorm:"type:uuid;primaryKey"`
	JobSucceeded     bool      `gorm:"column:job_succeeded"`
	JobFailed        bool      `gorm:"column:job_failed"`
	FileCreated      bool      `gorm:"column:file_created"`
	GraphicsEdited   bool      `gorm:"column:graphics_edited"`
	ReviewActivity   bool      `gorm:"column:review_activity"`
	AvatarRetention  bool      `gorm:"column:avatar_retention"`
	SocialPublishing bool      `gorm:"column:social_publishing"`
	CreatedAt        time.Time `gorm:"column:created_at"`
	UpdatedAt        time.Time `gorm:"column:updated_at"`
}

// TableName pins the table name.
func (UserNotificationPreferences) TableName() string { return "user_notification_preferences" }

// UserNotification is one delivered (or attempted) notification email, the
// per-user delivery log. The Go API writes it; the Inference API never touches
// it, which is why it carries no tenant query filter (docs/multi-tenancy.md:
// it is reached only through the authenticated caller's own id).
type UserNotification struct {
	ID             uuid.UUID `gorm:"type:uuid;primaryKey"`
	UserID         uuid.UUID `gorm:"type:uuid;index"`
	OrganizationID uuid.UUID `gorm:"type:uuid"`
	Category       string    `gorm:"column:category"`
	Title          string    `gorm:"column:title"`
	Delivered      bool      `gorm:"column:delivered"`
	Error          *string   `gorm:"column:error"`
	CreatedAt      time.Time `gorm:"column:created_at"`
}

// TableName pins the table name.
func (UserNotification) TableName() string { return "user_notifications" }
