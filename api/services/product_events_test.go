package services

import (
	"context"
	"errors"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
)

// The Go API's server-side funnel emitters (initiative product-events-funnel, T-003).
//
// product_events is Inference-owned: the Go API maps it and writes to it but never migrates it, so
// these tests create it (and the transition tables they exercise) themselves — exactly like
// connectMarketingTestDB does for the consent tables — and skip unless DATABASE_URL is set, so the
// default `go test ./...` needs no database. The two properties under test are the ones the plan
// locks: each named event is written at its transition with source='server', and a write that fails
// can never fail the transition it was measuring.

// productEventsTestDB opens the services test database and prepares the tables these tests touch.
func productEventsTestDB(t *testing.T) *gorm.DB {
	t.Helper()
	dsn := os.Getenv("DATABASE_URL")
	if dsn == "" {
		t.Skip("DATABASE_URL not set; skipping the product_events GORM integration tests")
	}
	db, err := database.Initialize(dsn)
	if err != nil {
		t.Skipf("Postgres not reachable (DATABASE_URL=%q): %v", dsn, err)
	}
	if err := db.AutoMigrate(
		&models.ProductEvent{},
		&models.Organization{},
		&models.OrganizationMembership{},
		&models.OrganizationInvite{},
		&models.ApplicationUser{},
		&models.UserToken{},
		&models.LegalAcceptance{},
		&models.MarketingConsent{},
		&models.MarketingConsentEvent{},
	); err != nil {
		t.Skipf("could not prepare the product_events test tables: %v", err)
	}
	return db
}

// hideProductEvents renames the table so a write to it genuinely fails — the rollout-skew case the
// plan calls out, where the Go API starts before the Inference API has migrated — and restores it
// when the test ends. It exercises the real writer, not a stub: the transition must survive the
// actual PostgreSQL error.
func hideProductEvents(t *testing.T, db *gorm.DB) {
	t.Helper()
	if err := db.Exec(`DROP TABLE IF EXISTS product_events_hidden`).Error; err != nil {
		t.Fatalf("drop a stale hidden table: %v", err)
	}
	if err := db.Exec(`ALTER TABLE product_events RENAME TO product_events_hidden`).Error; err != nil {
		t.Fatalf("hide product_events: %v", err)
	}
	t.Cleanup(func() {
		if err := db.Exec(`ALTER TABLE product_events_hidden RENAME TO product_events`).Error; err != nil {
			t.Errorf("restore product_events: %v", err)
		}
	})
}

// stubProductEventWriter swaps the writer behind the best-effort helper, so the swallow can be
// proven without a database.
func stubProductEventWriter(t *testing.T, w func(*gorm.DB, uuid.UUID, *uuid.UUID, string) error) {
	t.Helper()
	old := productEventWriter
	productEventWriter = w
	t.Cleanup(func() { productEventWriter = old })
}

// firstProductEvent reads the one row for a user and event name, failing when it is absent.
func firstProductEvent(t *testing.T, db *gorm.DB, userID uuid.UUID, event string) models.ProductEvent {
	t.Helper()
	var row models.ProductEvent
	if err := db.Where("user_id = ? AND event = ?", userID, event).First(&row).Error; err != nil {
		t.Fatalf("no %s row for user %s: %v", event, userID, err)
	}
	return row
}

// TestProductEventVocabulary pins the wire values the plan's table and the reader's query depend on.
func TestProductEventVocabulary(t *testing.T) {
	for _, tc := range []struct{ got, want string }{
		{ProductEventSignup, "alpha.signup"},
		{ProductEventEmailVerified, "alpha.email_verified"},
		{ProductEventWorkspaceCreated, "alpha.workspace_created"},
		{ProductEventInviteAccepted, "alpha.invite_accepted"},
		{ProductEventSourceServer, "server"},
	} {
		if tc.got != tc.want {
			t.Errorf("event vocabulary = %q, want %q", tc.got, tc.want)
		}
	}
}

// TestRecordProductEvent_WritesOneServerRowForThePerson is the writer's contract: one append-only
// row carrying the person, the event, a timestamp and source='server' — and nothing else.
func TestRecordProductEvent_WritesOneServerRowForThePerson(t *testing.T) {
	db := productEventsTestDB(t)
	userID := uuid.New()

	if err := RecordProductEvent(db, userID, nil, ProductEventSignup); err != nil {
		t.Fatalf("RecordProductEvent: %v", err)
	}

	row := firstProductEvent(t, db, userID, ProductEventSignup)
	if row.Source != ProductEventSourceServer {
		t.Errorf("source = %q, want %q", row.Source, ProductEventSourceServer)
	}
	if row.UserID == nil || *row.UserID != userID {
		t.Errorf("user_id = %v, want %s", row.UserID, userID)
	}
	if row.OrganizationID != nil {
		t.Errorf("organization_id = %v, want NULL for a person-only event", row.OrganizationID)
	}
	if row.OccurredAt.IsZero() {
		t.Error("occurred_at must be stamped")
	}
	if row.ID == 0 {
		t.Error("the identity column must be populated on insert")
	}

	var n int64
	if err := db.Model(&models.ProductEvent{}).Where("user_id = ?", userID).Count(&n).Error; err != nil {
		t.Fatalf("count rows: %v", err)
	}
	if n != 1 {
		t.Fatalf("rows for the user = %d, want exactly 1", n)
	}
}

// TestRecordProductEvent_NilHandleReturnsAnError keeps a missing handle an error rather than a
// panic: the helper must never be able to take the process down.
func TestRecordProductEvent_NilHandleReturnsAnError(t *testing.T) {
	if err := RecordProductEvent(nil, uuid.New(), nil, ProductEventSignup); err == nil {
		t.Fatal("a nil database handle must be reported as an error, not panic")
	}
}

// TestRecordProductEvent_ReturnsAnErrorWhenTheWriteFails proves the real writer surfaces the
// PostgreSQL error (it does not swallow it): the swallow is the helper's job, not the writer's.
func TestRecordProductEvent_ReturnsAnErrorWhenTheWriteFails(t *testing.T) {
	db := productEventsTestDB(t)
	hideProductEvents(t, db)

	if err := RecordProductEvent(db, uuid.New(), nil, ProductEventSignup); err == nil {
		t.Fatal("a failed insert must be returned as an error")
	}
}

// TestRecordProductEventBestEffort_LogsAndSwallowsAFailedWrite is Review Focus 3's unit: the write
// fails, the helper logs at WARN, and the caller's flow is untouched.
func TestRecordProductEventBestEffort_LogsAndSwallowsAFailedWrite(t *testing.T) {
	stubProductEventWriter(t, func(*gorm.DB, uuid.UUID, *uuid.UUID, string) error {
		return errors.New(`pq: relation "product_events" does not exist`)
	})

	// The transition's shape: it does its own work, then records best-effort, and its outcome does
	// not depend on what the recording did.
	var transitionErr error
	logs := captureLogs(func() {
		transitionErr = func() error {
			RecordProductEventBestEffort(&gorm.DB{}, uuid.New(), nil, ProductEventSignup)
			return nil
		}()
	})

	if transitionErr != nil {
		t.Fatalf("a failed product_events write must not fail the transition: %v", transitionErr)
	}
	if !strings.Contains(logs, "WARN") || !strings.Contains(logs, ProductEventSignup) {
		t.Fatalf("the failure must be logged at WARN: %q", logs)
	}
}

// TestRecordProductEventBestEffort_NilHandleIsSilent keeps the emitter a no-op when telemetry is
// not wired (no database handle), rather than logging a failure that never happened.
func TestRecordProductEventBestEffort_NilHandleIsSilent(t *testing.T) {
	logs := captureLogs(func() {
		RecordProductEventBestEffort(nil, uuid.New(), nil, ProductEventSignup)
	})
	if strings.Contains(logs, "WARN") {
		t.Fatalf("a nil handle is not a write failure: %q", logs)
	}
}

// TestCreateTeamOrg_StillSucceedsWhenTheProductEventsWriteFails is the acceptance test: a real
// transition, a real failing insert, and the transition still commits and returns its org.
func TestCreateTeamOrg_StillSucceedsWhenTheProductEventsWriteFails(t *testing.T) {
	db := productEventsTestDB(t)
	hideProductEvents(t, db)

	var org *models.Organization
	var err error
	logs := captureLogs(func() {
		org, err = GormOrgStore{}.CreateTeamOrg(context.Background(), uuid.New(), "Funnel Team")
	})

	if err != nil {
		t.Fatalf("a failed product_events write must not fail the transition: %v", err)
	}
	if org == nil {
		t.Fatal("the team org must still have been created")
	}
	var n int64
	if err := db.Model(&models.Organization{}).Where("id = ?", org.ID).Count(&n).Error; err != nil {
		t.Fatalf("count the org: %v", err)
	}
	if n != 1 {
		t.Fatalf("the team org was not committed: %d rows", n)
	}
	if !strings.Contains(logs, "WARN") {
		t.Fatalf("the swallowed failure must be visible in the logs: %q", logs)
	}
}

// TestRegisterUser_StillSucceedsWhenTheProductEventsWriteFails is the acceptance test in the
// signup's own terms: the account transition commits, the funnel write fails, and signup still
// succeeds — the failure is only a WARN in the log.
func TestRegisterUser_StillSucceedsWhenTheProductEventsWriteFails(t *testing.T) {
	db := productEventsTestDB(t)
	hideProductEvents(t, db)
	now := time.Now().UTC()

	user := &models.ApplicationUser{
		ID: uuid.New(), Email: "signup-" + uuid.NewString() + "@example.com", DisplayName: "Sig",
		CreatedAt: now, PasswordHash: "not-a-real-hash", AuthProvider: "Password",
	}
	tok := &models.UserToken{
		ID: uuid.New(), UserID: user.ID, Purpose: models.UserTokenVerifyEmail,
		TokenHash: HashToken("signup-" + uuid.NewString()), CreatedAt: now, ExpiresAt: now.Add(time.Hour),
	}

	var err error
	logs := captureLogs(func() {
		err = GormAccountStore{}.RegisterUser(user, nil, tok, nil)
	})

	if err != nil {
		t.Fatalf("signup must still succeed when the product_events write fails: %v", err)
	}
	var n int64
	if err := db.Model(&models.ApplicationUser{}).Where("id = ?", user.ID).Count(&n).Error; err != nil {
		t.Fatalf("count the account: %v", err)
	}
	if n != 1 {
		t.Fatalf("the account was not committed: %d rows", n)
	}
	if user.LastActiveOrganizationID == nil {
		t.Fatal("the Personal workspace must still have been provisioned")
	}
	if !strings.Contains(logs, "WARN") {
		t.Fatalf("the swallowed failure must be visible in the logs: %q", logs)
	}
}

// TestCreateTeamOrg_EmitsWorkspaceCreated covers the alpha.workspace_created emitter on the team
// route, with the org the transition produced.
func TestCreateTeamOrg_EmitsWorkspaceCreated(t *testing.T) {
	db := productEventsTestDB(t)
	userID := uuid.New()

	org, err := GormOrgStore{}.CreateTeamOrg(context.Background(), userID, "Funnel Team")
	if err != nil {
		t.Fatalf("CreateTeamOrg: %v", err)
	}

	row := firstProductEvent(t, db, userID, ProductEventWorkspaceCreated)
	if row.Source != ProductEventSourceServer {
		t.Errorf("source = %q, want %q", row.Source, ProductEventSourceServer)
	}
	if row.OrganizationID == nil || *row.OrganizationID != org.ID {
		t.Errorf("organization_id = %v, want the org the transition created (%s)", row.OrganizationID, org.ID)
	}
}

// TestAcceptInvite_EmitsInviteAccepted covers the alpha.invite_accepted emitter: both accept routes
// funnel through acceptInviteTx, and the row carries the invite's organization.
func TestAcceptInvite_EmitsInviteAccepted(t *testing.T) {
	db := productEventsTestDB(t)
	now := time.Now().UTC()
	userID := uuid.New()

	inv := &models.OrganizationInvite{
		ID: uuid.New(), OrganizationID: uuid.New(), Email: "invitee@example.com",
		Role: models.OrgRoleMember, TokenHash: "hash-" + uuid.NewString(),
		CreatedAt: now, ExpiresAt: now.Add(time.Hour),
	}
	if err := db.Create(inv).Error; err != nil {
		t.Fatalf("seed invite: %v", err)
	}

	if err := (GormOrgStore{}).AcceptInvite(context.Background(), inv, userID, now); err != nil {
		t.Fatalf("AcceptInvite: %v", err)
	}

	row := firstProductEvent(t, db, userID, ProductEventInviteAccepted)
	if row.Source != ProductEventSourceServer {
		t.Errorf("source = %q, want %q", row.Source, ProductEventSourceServer)
	}
	if row.OrganizationID == nil || *row.OrganizationID != inv.OrganizationID {
		t.Errorf("organization_id = %v, want %s", row.OrganizationID, inv.OrganizationID)
	}
}

// TestConsumeVerifyEmail_EmitsEmailVerifiedOnce covers the alpha.email_verified emitter: written
// after the verification transaction commits, and only when the address actually became verified.
func TestConsumeVerifyEmail_EmitsEmailVerifiedOnce(t *testing.T) {
	db := productEventsTestDB(t)
	now := time.Now().UTC().Truncate(time.Microsecond)

	user := &models.ApplicationUser{
		ID: uuid.New(), Email: "verify-" + uuid.NewString() + "@example.com", DisplayName: "Vera",
		CreatedAt: now, PasswordHash: "not-a-real-hash", AuthProvider: "Password",
	}
	if err := db.Create(user).Error; err != nil {
		t.Fatalf("seed user: %v", err)
	}
	issue := func() string {
		raw := "verify-" + uuid.NewString()
		tok := &models.UserToken{
			ID: uuid.New(), UserID: user.ID, Purpose: models.UserTokenVerifyEmail,
			TokenHash: HashToken(raw), CreatedAt: now, ExpiresAt: now.Add(time.Hour),
		}
		if err := db.Create(tok).Error; err != nil {
			t.Fatalf("seed token: %v", err)
		}
		return raw
	}

	verified, err := GormAccountStore{}.ConsumeVerifyEmail(HashToken(issue()), now)
	if err != nil {
		t.Fatalf("ConsumeVerifyEmail: %v", err)
	}
	if verified.EmailVerifiedAt == nil {
		t.Fatal("the transition did not verify the address")
	}
	row := firstProductEvent(t, db, user.ID, ProductEventEmailVerified)
	if row.Source != ProductEventSourceServer {
		t.Errorf("source = %q, want %q", row.Source, ProductEventSourceServer)
	}

	// A second verification of an already-verified address is not a new funnel step.
	if _, err := (GormAccountStore{}).ConsumeVerifyEmail(HashToken(issue()), now.Add(time.Minute)); err != nil {
		t.Fatalf("second ConsumeVerifyEmail: %v", err)
	}
	var n int64
	if err := db.Model(&models.ProductEvent{}).
		Where("user_id = ? AND event = ?", user.ID, ProductEventEmailVerified).Count(&n).Error; err != nil {
		t.Fatalf("count email_verified rows: %v", err)
	}
	if n != 1 {
		t.Fatalf("alpha.email_verified rows = %d, want exactly 1", n)
	}
}

// TestCreateUser_EmitsWorkspaceCreatedForThePersonalOrg covers provisionTenancy's Personal-org
// creation: the row carries the org the transition stamped on the user.
func TestCreateUser_EmitsWorkspaceCreatedForThePersonalOrg(t *testing.T) {
	db := productEventsTestDB(t)

	user, _, err := CreateUser("funnel-"+uuid.NewString()+"@example.com", "Funnel", false, AdminActor{})
	if err != nil {
		t.Fatalf("CreateUser: %v", err)
	}
	if user.LastActiveOrganizationID == nil {
		t.Fatal("the Personal org was not provisioned")
	}

	row := firstProductEvent(t, db, user.ID, ProductEventWorkspaceCreated)
	if row.Source != ProductEventSourceServer {
		t.Errorf("source = %q, want %q", row.Source, ProductEventSourceServer)
	}
	if row.OrganizationID == nil || *row.OrganizationID != *user.LastActiveOrganizationID {
		t.Errorf("organization_id = %v, want the Personal org %s", row.OrganizationID, *user.LastActiveOrganizationID)
	}
}
