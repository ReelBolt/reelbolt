package models

import (
	"time"

	"github.com/google/uuid"
)

// UserAttribution maps the Inference-owned user_attribution table (initiative product-analytics):
// one first-touch row per user, written once at signup. The Go API maps it and never migrates it.
// Only closed, sanitised marketing labels: no IP, no user agent, no free text.
//
// Capture says how the row was learned (docs/design/new-user-funnel.md section 5): "cookie" for the
// consented rb_ft first-touch cookie, "url" for campaign parameters carried in the signup request
// or the OAuth state with nothing stored on the device. NULL on rows older than the column.
type UserAttribution struct {
	UserID       uuid.UUID  `gorm:"type:uuid;primaryKey;column:user_id"`
	SourceLabel  string     `gorm:"column:source_label;size:64"`
	UTMSource    *string    `gorm:"column:utm_source;size:64"`
	UTMMedium    *string    `gorm:"column:utm_medium;size:64"`
	UTMCampaign  *string    `gorm:"column:utm_campaign;size:64"`
	UTMContent   *string    `gorm:"column:utm_content;size:64"`
	UTMTerm      *string    `gorm:"column:utm_term;size:64"`
	ReferrerHost *string    `gorm:"column:referrer_host;size:100"`
	LandingPath  *string    `gorm:"column:landing_path;size:120"`
	VisitorID    *uuid.UUID `gorm:"type:uuid;column:visitor_id"`
	FirstTouchAt *time.Time `gorm:"column:first_touch_at"`
	CreatedAt    time.Time  `gorm:"column:created_at"`
	Capture      *string    `gorm:"column:capture;size:16"`
}

func (UserAttribution) TableName() string { return "user_attribution" }
