package services

import (
	"log"
	"regexp"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
)

// The persona a landing page suggested (new-user funnel, docs/design/new-user-funnel.md).
//
// A landing page for one audience sends its visitors to signup with `for=<persona>`; the value
// rides the signup request (or the OAuth state) and is stored at account creation as
// user_onboarding_profiles.suggested_persona, so the welcome page can preselect it. It is a
// suggestion only: the person's own answer (persona, material) is written by the Inference API,
// which owns the table and its schema. The Go API maps the table and never migrates it.

// personas is the closed set shared by the site, the app, the API and BI (design section 4).
var personas = map[string]struct{}{
	"creator":   {},
	"business":  {},
	"educator":  {},
	"nonprofit": {},
	"software":  {},
	"agency":    {},
	"other":     {},
}

// NormalizePersona returns the persona token for s (trimmed, lower-cased) and whether it is one of
// the closed set. Anything else is not a persona: the caller drops it and carries on, because a
// signup must never fail on marketing data.
func NormalizePersona(s string) (string, bool) {
	s = strings.ToLower(strings.TrimSpace(s))
	if _, ok := personas[s]; !ok {
		return "", false
	}
	return s, true
}

// onboardingProfileWriter is the insert behind a package var so a test can observe or break it.
// ON CONFLICT DO NOTHING: a row the Inference API already wrote for this user (an answer given
// before this write landed) is never touched.
var onboardingProfileWriter = func(db *gorm.DB, row *models.UserOnboardingProfile) error {
	return db.Clauses(onConflictDoNothing).Create(row).Error
}

var planKeyRe = regexp.MustCompile(`^[a-z0-9][a-z0-9_-]{0,31}$`)

// NormalizePlanKey returns the plan key for s (trimmed, lower-cased) and whether it is well formed.
// Anything else is dropped by the caller: a signup must never fail on marketing data.
func NormalizePlanKey(s string) (string, bool) {
	s = strings.ToLower(strings.TrimSpace(s))
	if !planKeyRe.MatchString(s) {
		return "", false
	}
	return s, true
}

// RecordSuggestedPersonaBestEffort stores only the landing page's persona; see
// RecordSignupProfileBestEffort.
func RecordSuggestedPersonaBestEffort(db *gorm.DB, userID uuid.UUID, persona string) {
	RecordSignupProfileBestEffort(db, userID, persona, "")
}

// RecordSignupProfileBestEffort stores the landing page's persona and the picked plan for a
// just-created user in one insert and never fails the flow: a nil handle or neither value valid is
// a no-op, and a failed insert (the table not migrated yet, a dead connection) is logged without
// the values. It runs after the account's own transaction has committed, on the pool, like every
// other funnel write: placed inside that transaction, a failed insert would roll the signup back.
func RecordSignupProfileBestEffort(db *gorm.DB, userID uuid.UUID, persona, plan string) {
	row := &models.UserOnboardingProfile{UserID: userID}
	if token, ok := NormalizePersona(persona); ok {
		row.SuggestedPersona = &token
	}
	if key, ok := NormalizePlanKey(plan); ok {
		row.PickedPlan = &key
	}
	if db == nil || (row.SuggestedPersona == nil && row.PickedPlan == nil) {
		return
	}
	now := time.Now().UTC()
	row.CreatedAt, row.UpdatedAt = now, now
	if err := onboardingProfileWriter(db, row); err != nil {
		log.Printf("WARN user_onboarding_profiles: signup profile not recorded for user %s: %v", userID, err)
	}
}

// ObserveOnboardingProfileWrites replaces the insert with fn until the returned restore func runs.
// Test seam for handler tests that must prove which signup paths store a suggested persona.
func ObserveOnboardingProfileWrites(fn func(row *models.UserOnboardingProfile)) (restore func()) {
	old := onboardingProfileWriter
	onboardingProfileWriter = func(_ *gorm.DB, row *models.UserOnboardingProfile) error { fn(row); return nil }
	return func() { onboardingProfileWriter = old }
}
