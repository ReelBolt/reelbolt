package services

import (
	"errors"
	"log"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
)

// The Go API's server-side funnel emitters (initiative product-events-funnel, T-003).
//
// product_events is Inference-owned: the Go API maps the table and writes to it but never migrates
// it (models/product_event.go). Every write is best-effort and happens *after* the transition's own
// transaction has committed, on the connection pool rather than inside the transaction. That
// ordering is the whole point: a failed statement aborts a PostgreSQL transaction, so an insert
// placed inside signup's transaction would roll back the very signup it was measuring. A lost row
// is a lost metric; a broken signup is a broken product.
//
// The row is ids, an event name, a timestamp and a source. Never an email, a display name, an IP, a
// user agent, free text or any request payload.

// The event names the Go API emits. varchar(64) means a new name costs no migration.
const (
	ProductEventSignup           = "alpha.signup"
	ProductEventEmailVerified    = "alpha.email_verified"
	ProductEventWorkspaceCreated = "alpha.workspace_created"
	ProductEventInviteAccepted   = "alpha.invite_accepted"
	// ProductEventLogin is a successful sign-in; detail is the method token (password|google|github).
	ProductEventLogin = "auth.login"
)

// ProductEventSourceServer is the product_events.source this service writes: a backend
// observation. It exists for every user the transition happened to, and consent cannot erase it.
// The database states the same vocabulary in ck_product_events_source.
const ProductEventSourceServer = "server"

// errNoProductEventDatabase keeps a missing handle an error rather than a panic.
var errNoProductEventDatabase = errors.New("product_events: no database handle")

// productEventWriter is RecordProductEvent behind a package var so a test can substitute a failing
// writer and show a transition is unaffected, without needing a database.
var productEventWriter = RecordProductEvent

// RecordProductEvent appends one server-observed row and returns the error. It takes the *gorm.DB
// explicitly so a caller can pass the pool (database.DB) after its transaction has committed, and
// never the transaction itself.
func RecordProductEvent(db *gorm.DB, userID uuid.UUID, orgID *uuid.UUID, event string) error {
	if db == nil {
		return errNoProductEventDatabase
	}
	// Never record an event for an API-key service account: it is not a person in any funnel.
	// Fail closed: if the lookup errors we do not know whether this is one, so record nothing.
	var svc int64
	err := db.Model(&models.ApplicationUser{}).
		Where("id = ? AND is_service_account = ?", userID, true).Count(&svc).Error
	if err != nil {
		return err
	}
	if svc > 0 {
		return nil
	}
	row := models.ProductEvent{
		UserID:         &userID,
		OrganizationID: orgID,
		Event:          event,
		OccurredAt:     time.Now().UTC(),
		Source:         ProductEventSourceServer,
	}
	return db.Create(&row).Error
}

// RecordProductEventDetailBestEffort is RecordProductEventBestEffort for an event qualified by a
// closed token (never free text, never personal data); detail is bounded to 64 characters.
func RecordProductEventDetailBestEffort(db *gorm.DB, userID uuid.UUID, orgID *uuid.UUID, event, detail string) {
	if db == nil {
		return
	}
	if len(detail) > 64 {
		detail = detail[:64]
	}
	row := models.ProductEvent{
		UserID:         &userID,
		OrganizationID: orgID,
		Event:          event,
		Detail:         &detail,
		OccurredAt:     time.Now().UTC(),
		Source:         ProductEventSourceServer,
	}
	if err := db.Create(&row).Error; err != nil {
		log.Printf("WARN product_events: %s for user %s not recorded: %v", event, userID, err)
	}
}

// RecordProductEventBestEffort appends one server-observed row and never returns an error: a lost
// metric must never fail the transition it was measuring. A failed write is logged at WARN; a nil
// handle means telemetry is not wired, which is a silent no-op rather than a failure.
func RecordProductEventBestEffort(db *gorm.DB, userID uuid.UUID, orgID *uuid.UUID, event string) {
	if db == nil {
		return
	}
	if err := productEventWriter(db, userID, orgID, event); err != nil {
		log.Printf("WARN product_events: %s for user %s not recorded: %v", event, userID, err)
	}
}

// recordPersonalWorkspaceCreatedBestEffort emits alpha.workspace_created for the Personal workspace
// a just-committed transition created. The org id is the one provisionTenancy stamped on the user
// (LastActiveOrganizationID), read after the transaction rather than inside it.
func recordPersonalWorkspaceCreatedBestEffort(user *models.ApplicationUser) {
	if user == nil || user.LastActiveOrganizationID == nil {
		return
	}
	RecordProductEventBestEffort(database.DB, user.ID, user.LastActiveOrganizationID, ProductEventWorkspaceCreated)
}
