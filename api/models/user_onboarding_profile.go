package models

import (
	"time"

	"github.com/google/uuid"
)

// UserOnboardingProfile maps the Inference-owned user_onboarding_profiles table (new-user funnel,
// docs/design/new-user-funnel.md section 5): one row per user holding who they said they are and
// what they start from. The Inference API owns the schema and the answers (persona, material,
// answered_at, skipped_at); the Go API maps the table and writes two things to it at account
// creation, the persona a landing page suggested and the plan the visitor picked. It never migrates it.
//
// Persona values are the closed set in services.NormalizePersona: no free text reaches this table.
type UserOnboardingProfile struct {
	UserID           uuid.UUID  `gorm:"type:uuid;primaryKey;column:user_id"`
	Persona          *string    `gorm:"column:persona;size:32"`
	Material         *string    `gorm:"column:material;size:32"`
	SuggestedPersona *string    `gorm:"column:suggested_persona;size:32"`
	PickedPlan       *string    `gorm:"column:picked_plan;size:32"`
	AnsweredAt       *time.Time `gorm:"column:answered_at"`
	SkippedAt        *time.Time `gorm:"column:skipped_at"`
	CreatedAt        time.Time  `gorm:"column:created_at"`
	UpdatedAt        time.Time  `gorm:"column:updated_at"`
}

func (UserOnboardingProfile) TableName() string { return "user_onboarding_profiles" }
