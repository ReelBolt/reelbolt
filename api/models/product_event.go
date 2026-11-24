package models

import (
	"time"

	"github.com/google/uuid"
)

// ProductEvent maps the Inference-owned product_events table (initiative
// product-events-funnel). The Inference API owns the schema through its EF migrations; the Go API
// maps the table read/write and never migrates it, exactly like Organization above.
//
// Append-only and PII-free: a row is ids, a closed event name, a timestamp and a source, and
// nothing else. The two subject ids are both nullable because a payment belongs to an organization
// and may have no person, while a client event belongs to a person and may have no organization; a
// CHECK requires at least one, so a writer always supplies one.
type ProductEvent struct {
	ID             int64      `gorm:"type:bigint;primaryKey;column:id"`
	UserID         *uuid.UUID `gorm:"type:uuid;column:user_id"`
	OrganizationID *uuid.UUID `gorm:"type:uuid;column:organization_id"`
	Event          string     `gorm:"column:event;size:64"`
	OccurredAt     time.Time  `gorm:"column:occurred_at"`
	Source         string     `gorm:"column:source;size:16"`
	// Detail is a closed, sanitized token (<=64) qualifying the event; nil when none.
	Detail *string `gorm:"column:detail;size:64"`
}

func (ProductEvent) TableName() string { return "product_events" }
