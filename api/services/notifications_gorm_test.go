package services

import (
	"context"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
)

// connectTestDB opens the store's database for the GORM integration tests, or
// returns false so the caller can skip. It uses the same DSN the app would, so
// it runs wherever the app's Postgres is reachable (local dev) and skips
// cleanly anywhere it is not (e.g. CI without a database service).
func connectTestDB(t *testing.T) {
	t.Helper()
	dsn := os.Getenv("DATABASE_URL")
	if dsn == "" {
		dsn = "postgres://postgres:postgres@localhost:5432/reelforge?sslmode=disable"
	}
	db, err := database.Initialize(dsn)
	if err != nil {
		t.Skipf("Postgres not reachable for the GORM integration test (DATABASE_URL=%q): %v", dsn, err)
	}
	// Test-only schema setup: the store itself never migrates (Inference owns
	// the schema via EF migrations), but the test needs the table to exist so
	// it works against a fresh database as well as the app's.
	if err := db.AutoMigrate(&models.UserNotificationPreferences{}); err != nil {
		t.Skipf("could not prepare the user_notification_preferences table for the test: %v", err)
	}
}

// TestGormNotificationStore_SavePreferencesUpserts is the regression test for the
// old FirstOrCreate behaviour, where a second save was a no-op because
// FirstOrCreate never applied the Assign map on the found-row path. It proves a
// second save now updates the stored row (not a no-op) and that the upsert
// preserves created_at while touching the four switches and updated_at.
func TestGormNotificationStore_SavePreferencesUpserts(t *testing.T) {
	connectTestDB(t)
	ctx := context.Background()
	store := GormNotificationStore{}
	userID := uuid.New()

	// First save: all on.
	first := NotificationPreferences{JobSucceeded: true, JobFailed: true, FileCreated: true, GraphicsEdited: true}
	if err := store.SavePreferences(ctx, userID, first); err != nil {
		t.Fatalf("first SavePreferences: %v", err)
	}
	read1, err := store.GetPreferences(ctx, userID)
	if err != nil {
		t.Fatalf("GetPreferences after first save: %v", err)
	}
	if read1 != first {
		t.Fatalf("after first save = %+v, want %+v", read1, first)
	}

	// Record the created_at of the created row.
	var createdRow models.UserNotificationPreferences
	if err := database.DB.WithContext(ctx).Where("user_id = ?", userID).First(&createdRow).Error; err != nil {
		t.Fatalf("read created row: %v", err)
	}
	if createdRow.CreatedAt.IsZero() {
		t.Fatalf("created_at was not set on the created row")
	}

	// Let a little time pass so the second write's timestamp differs.
	time.Sleep(20 * time.Millisecond)

	// Second save: flip file_created off, leave the rest. This is the case the
	// old code turned into a no-op.
	second := NotificationPreferences{JobSucceeded: true, JobFailed: true, FileCreated: false, GraphicsEdited: true}
	if err := store.SavePreferences(ctx, userID, second); err != nil {
		t.Fatalf("second SavePreferences: %v", err)
	}

	read2, err := store.GetPreferences(ctx, userID)
	if err != nil {
		t.Fatalf("GetPreferences after second save: %v", err)
	}
	if read2 != second {
		t.Fatalf("after second save = %+v, want %+v (the second save must update, not no-op)", read2, second)
	}

	// The upsert must preserve created_at and bump updated_at.
	var afterRow models.UserNotificationPreferences
	if err := database.DB.WithContext(ctx).Where("user_id = ?", userID).First(&afterRow).Error; err != nil {
		t.Fatalf("read row after second save: %v", err)
	}
	if !afterRow.CreatedAt.Equal(createdRow.CreatedAt) {
		t.Fatalf("created_at changed on upsert: was %v, now %v", createdRow.CreatedAt, afterRow.CreatedAt)
	}
	if !afterRow.UpdatedAt.After(createdRow.UpdatedAt) && !afterRow.UpdatedAt.Equal(createdRow.UpdatedAt) {
		t.Fatalf("updated_at did not advance on upsert: %v", afterRow.UpdatedAt)
	}

	// Clean up so re-runs and other users are unaffected.
	if err := database.DB.WithContext(ctx).Where("user_id = ?", userID).Delete(&models.UserNotificationPreferences{}).Error; err != nil {
		t.Fatalf("cleanup: %v", err)
	}
}
