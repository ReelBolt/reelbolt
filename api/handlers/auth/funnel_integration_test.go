package auth

import (
	"context"
	"fmt"
	"net/http/httptest"
	"net/url"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
	"gorm.io/gorm"
)

// The new-user funnel's account-creation writes with nothing replaced: the real account store, the
// real writers and a real PostgreSQL. It proves the one thing the in-memory tests cannot, that a
// signup leaves the three rows BI joins on (docs/design/new-user-funnel.md section 8) and that an
// existing address adds none. Skipped unless DATABASE_URL is set, like the services package's GORM
// integration tests; the Go API never migrates in production, so the test creates what it needs.

const funnelIntegrationSchema = "auth_funnel_it"

func funnelIntegrationDB(t *testing.T) *gorm.DB {
	t.Helper()
	dsn := os.Getenv("DATABASE_URL")
	if dsn == "" {
		t.Skip("DATABASE_URL not set; skipping the signup funnel integration test")
	}
	// A schema of its own. `go test ./...` runs packages side by side against the one database, and
	// the services package's integration tests hide and alter the very tables this test writes to
	// (that is how they prove a missing table cannot fail a signup).
	u, err := url.Parse(dsn)
	if err != nil || u.Scheme == "" {
		t.Skipf("DATABASE_URL is not a postgres:// URL, which this test needs to select its schema: %v", err)
	}
	q := u.Query()
	q.Set("search_path", funnelIntegrationSchema)
	u.RawQuery = q.Encode()

	old := database.DB
	db, err := database.Initialize(u.String()) // sets database.DB, which every other test here expects nil
	t.Cleanup(func() { database.DB = old })
	if err != nil {
		t.Skipf("Postgres not reachable (DATABASE_URL=%q): %v", dsn, err)
	}
	if err := db.Exec(`CREATE SCHEMA IF NOT EXISTS ` + funnelIntegrationSchema).Error; err != nil {
		t.Skipf("could not create the test schema: %v", err)
	}
	if err := db.AutoMigrate(
		&models.ApplicationUser{},
		&models.Organization{},
		&models.OrganizationMembership{},
		&models.UserToken{},
		&models.LegalAcceptance{},
		&models.LegalDocumentVersion{},
		&models.MarketingConsent{},
		&models.MarketingConsentEvent{},
		&models.ProductEvent{},
		&models.UserAttribution{},
		&models.UserOnboardingProfile{},
	); err != nil {
		t.Skipf("could not prepare the signup funnel test tables: %v", err)
	}
	// The Go model maps is_service_account read-only, so AutoMigrate creates it with no default
	// and a new row would hold NULL, which the store's `is_service_account = false` lookups skip.
	// The real, EF-owned column is NOT NULL DEFAULT false; give the test table the same default.
	for _, stmt := range []string{
		`UPDATE application_users SET is_service_account = false WHERE is_service_account IS NULL`,
		`ALTER TABLE application_users ALTER COLUMN is_service_account SET DEFAULT false`,
	} {
		if err := db.Exec(stmt).Error; err != nil {
			t.Skipf("could not prepare application_users: %v", err)
		}
	}
	return db
}

func TestSignup_Integration_WritesTheFunnelRowsOnceForANewAccountOnly(t *testing.T) {
	db := funnelIntegrationDB(t)
	h := setup(t, true)
	acct.store = services.GormAccountStore{}
	acct.now = time.Now
	urlCapture(true)

	// The versions this deployment serves, which the submission has to name (F-7).
	for _, doc := range []string{models.LegalDocumentTerms, models.LegalDocumentPrivacy} {
		row := models.LegalDocumentVersion{Document: doc, Version: "2026-10", EffectiveAt: time.Now().UTC(), UpdatedAt: time.Now().UTC()}
		if err := db.Save(&row).Error; err != nil {
			t.Fatalf("seed legal_documents: %v", err)
		}
	}

	email := "funnel-" + uuid.NewString() + "@example.com"
	body := `{"email":"` + email + `","password":"correct horse","displayName":"Ada","acceptTerms":true,` +
		`"termsVersion":"2026-10","privacyVersion":"2026-10","persona":"creator"` + linkAttribution + `}`

	// The per-IP signup cap is counted from application_users, which here outlives the test run, so
	// every run signs up from an address of its own.
	id := uuid.New()
	ip := fmt.Sprintf("10.%d.%d.%d", id[0], id[1], id[2])
	signup := func(body string) *httptest.ResponseRecorder {
		req := httptest.NewRequestWithContext(context.Background(), "POST", "/x", strings.NewReader(body))
		req.Header.Set("X-Real-IP", ip)
		rec := httptest.NewRecorder()
		handleSignup(rec, req)
		return rec
	}

	first := signup(body)
	if first.Code != 202 {
		t.Fatalf("status = %d %s, want 202", first.Code, first.Body)
	}
	var user models.ApplicationUser
	if err := db.Where("email = ?", email).First(&user).Error; err != nil {
		t.Fatalf("the account was not stored: %v", err)
	}
	var personal models.Organization
	if err := db.Where("created_by_user_id = ? AND kind = ?", user.ID, models.OrgKindPersonal).First(&personal).Error; err != nil {
		t.Fatalf("no Personal organization: %v", err)
	}

	check := func(when string) {
		t.Helper()
		// alpha.signup: one row, naming the Personal organization.
		var signups []models.ProductEvent
		if err := db.Where("user_id = ? AND event = ?", user.ID, services.ProductEventSignup).Find(&signups).Error; err != nil {
			t.Fatal(err)
		}
		if len(signups) != 1 || signups[0].OrganizationID == nil || *signups[0].OrganizationID != personal.ID {
			t.Fatalf("%s: alpha.signup rows = %+v, want one for the Personal organization %s", when, signups, personal.ID)
		}
		// user_attribution: one row, from the request, capture url.
		var attrs []models.UserAttribution
		if err := db.Where("user_id = ?", user.ID).Find(&attrs).Error; err != nil {
			t.Fatal(err)
		}
		if len(attrs) != 1 || str(attrs[0].Capture) != "url" || attrs[0].SourceLabel != "hackernews" ||
			str(attrs[0].UTMContent) != "hn-launch" || str(attrs[0].LandingPath) != "/for/software-teams" || attrs[0].FirstTouchAt == nil {
			t.Fatalf("%s: user_attribution rows = %+v", when, attrs)
		}
		// user_onboarding_profiles: one row, the suggestion only.
		var profiles []models.UserOnboardingProfile
		if err := db.Where("user_id = ?", user.ID).Find(&profiles).Error; err != nil {
			t.Fatal(err)
		}
		if len(profiles) != 1 || str(profiles[0].SuggestedPersona) != "creator" || profiles[0].Persona != nil || profiles[0].AnsweredAt != nil {
			t.Fatalf("%s: user_onboarding_profiles rows = %+v", when, profiles)
		}
	}
	check("after the signup")

	// The same address again with other marketing data: the uniform 202, and not one row more or changed.
	second := signup(strings.Replace(strings.Replace(body, `"persona":"creator"`, `"persona":"agency"`, 1), "hn-launch", "other-link", 1))
	if second.Code != first.Code || second.Body.String() != first.Body.String() {
		t.Fatalf("existing address answered %d %q, the new one %d %q", second.Code, second.Body, first.Code, first.Body)
	}
	check("after the repeat")
	var users int64
	if err := db.Model(&models.ApplicationUser{}).Where("email = ?", email).Count(&users).Error; err != nil || users != 1 {
		t.Fatalf("accounts for the address = %d err %v", users, err)
	}
	if len(h.mails) != 2 {
		t.Fatalf("mails = %d, want the verification mail twice (the repeat mails the owner)", len(h.mails))
	}
}
