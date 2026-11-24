package models

import (
	"time"

	"github.com/google/uuid"
)

// Legal document names (stored by name, matching the Inference API's LegalDocument enum).
const (
	LegalDocumentTerms   = "Terms"
	LegalDocumentPrivacy = "Privacy"
)

// LegalAcceptance records that a user accepted a version of a legal document.
type LegalAcceptance struct {
	ID         uuid.UUID `gorm:"type:uuid;primaryKey"`
	UserID     uuid.UUID `gorm:"column:user_id;type:uuid"`
	Document   string    `gorm:"column:document"`
	Version    string    `gorm:"column:version"`
	AcceptedAt time.Time `gorm:"column:accepted_at"`
	IP         *string   `gorm:"column:ip"`
}

func (LegalAcceptance) TableName() string {
	return "legal_acceptances"
}
