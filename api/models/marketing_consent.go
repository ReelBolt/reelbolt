package models

import (
	"time"

	"github.com/google/uuid"
)

// Marketing consent (saas-promotions). The tables are owned by the Inference
// API's EF migrations; the Go API writes them only at signup (the opt-in
// checkbox) and on email verification (confirming that opt-in), the same way it
// already writes legal_acceptances. Enum columns hold the C# member names.

// MarketingConsentStatus values (C# MarketingConsentStatus).
const (
	MarketingConsentStatusPending   = "Pending"
	MarketingConsentStatusOptedIn   = "OptedIn"
	MarketingConsentStatusWithdrawn = "Withdrawn"
)

// MarketingConsentSource values (C# MarketingConsentSource). Go writes only Signup.
const (
	MarketingConsentSourceSignup      = "Signup"
	MarketingConsentSourceSettings    = "Settings"
	MarketingConsentSourceCheckout    = "Checkout"
	MarketingConsentSourceAcceptTerms = "AcceptTerms"
	MarketingConsentSourceSiteContact = "SiteContact"
	MarketingConsentSourceSiteFooter  = "SiteFooter"
	MarketingConsentSourceUnsubscribe = "Unsubscribe"
)

// MarketingConsentAction values (C# MarketingConsentAction).
const (
	MarketingConsentActionRequested = "Requested"
	MarketingConsentActionGranted   = "Granted"
	MarketingConsentActionConfirmed = "Confirmed"
	MarketingConsentActionWithdrawn = "Withdrawn"
)

// MarketingConsent is one row per normalized email address.
type MarketingConsent struct {
	ID                     uuid.UUID  `gorm:"type:uuid;primaryKey"`
	Email                  string     `gorm:"column:email;size:320;uniqueIndex"`
	UserID                 *uuid.UUID `gorm:"column:user_id;type:uuid"`
	Status                 string     `gorm:"column:status"`
	WordingVersion         string     `gorm:"column:wording_version;size:64"`
	Source                 string     `gorm:"column:source"`
	RequestedAt            *time.Time `gorm:"column:requested_at"`
	ConsentedAt            *time.Time `gorm:"column:consented_at"`
	ConfirmedAt            *time.Time `gorm:"column:confirmed_at"`
	WithdrawnAt            *time.Time `gorm:"column:withdrawn_at"`
	IP                     *string    `gorm:"column:ip;size:64"`
	UserAgent              *string    `gorm:"column:user_agent;size:256"`
	LastConfirmationSentAt *time.Time `gorm:"column:last_confirmation_sent_at"`
	// ResubscribeRequestedAt records a request to subscribe again made while the row is Withdrawn
	// (column added by the Inference migration AddPromotionReviewFixes). The row stays Withdrawn until
	// the address is proven (S-18).
	ResubscribeRequestedAt *time.Time `gorm:"column:resubscribe_requested_at"`
	UpdatedAt              time.Time  `gorm:"column:updated_at;autoUpdateTime:false"`
}

func (MarketingConsent) TableName() string {
	return "marketing_consents"
}

// MarketingConsentEvent is the append-only history of a consent row.
type MarketingConsentEvent struct {
	ID             uuid.UUID  `gorm:"type:uuid;primaryKey"`
	ConsentID      uuid.UUID  `gorm:"column:consent_id;type:uuid"`
	Email          string     `gorm:"column:email"`
	UserID         *uuid.UUID `gorm:"column:user_id;type:uuid"`
	Action         string     `gorm:"column:action"`
	Source         string     `gorm:"column:source"`
	WordingVersion string     `gorm:"column:wording_version"`
	IP             *string    `gorm:"column:ip"`
	UserAgent      *string    `gorm:"column:user_agent"`
	OccurredAt     time.Time  `gorm:"column:occurred_at"`
}

func (MarketingConsentEvent) TableName() string {
	return "marketing_consent_events"
}
