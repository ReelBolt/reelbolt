package services

import (
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

// The new-user funnel's account-creation writes (docs/design/new-user-funnel.md sections 5 and 6):
// user_attribution.capture, the URL-carried first touch, and the suggested persona. The pure rules
// run everywhere; the tests that need the real statements to execute skip unless DATABASE_URL is
// set, like the other GORM integration tests in this package.

func val(p *string) string {
	if p == nil {
		return "<nil>"
	}
	return *p
}

// ---- capture and the URL-carried touch ----

func TestParseFirstTouchCookie_IsACookieCapture(t *testing.T) {
	ft := ParseFirstTouchCookie(enc(`{"v":1,"s":"ads"}`))
	if ft == nil || ft.Capture != AttributionCaptureCookie {
		t.Fatalf("first touch %+v, want capture %q", ft, AttributionCaptureCookie)
	}
}

func TestURLAttribution_FirstTouch(t *testing.T) {
	now := time.Date(2026, 11, 11, 9, 30, 0, 0, time.FixedZone("CET", 3600))
	long := strings.Repeat("a", 64)
	for name, c := range map[string]struct {
		in      URLAttribution
		wantNil bool
		label   string
		source  string
		landing string
	}{
		"a tracked link":       {in: URLAttribution{Source: "hackernews", Medium: "community", Campaign: "launch-2026-11", Content: "hn-launch", LandingPath: "/for/software-teams"}, label: "hackernews", source: "hackernews", landing: "/for/software-teams"},
		"trimmed and lowered":  {in: URLAttribution{Source: "  Reddit ", LandingPath: "/For/Creators?utm_source=x#top"}, label: "reddit", source: "reddit", landing: "/for/creators"},
		"no source is direct":  {in: URLAttribution{Campaign: "spring"}, label: "direct"},
		"landing alone":        {in: URLAttribution{LandingPath: "/pricing"}, label: "direct", landing: "/pricing"},
		"own host source":      {in: URLAttribution{Source: "app.reelbolt.ai", Medium: "owned"}, label: "direct"},
		"64 characters":        {in: URLAttribution{Source: long}, label: long, source: long},
		"65 characters":        {in: URLAttribution{Source: long + "a"}, wantNil: true},
		"nothing":              {in: URLAttribution{}, wantNil: true},
		"all invalid":          {in: URLAttribution{Source: "a b", Medium: "-x", Campaign: "50%", Content: "a/b", Term: "a,b", LandingPath: "//evil.example/x"}, wantNil: true},
		"landing with no root": {in: URLAttribution{LandingPath: "pricing"}, wantNil: true},
	} {
		t.Run(name, func(t *testing.T) {
			ft := c.in.FirstTouch(now)
			if c.wantNil {
				if ft != nil {
					t.Fatalf("want no touch, got %+v", ft)
				}
				return
			}
			if ft == nil {
				t.Fatal("want a touch, got nil")
			}
			if ft.Capture != AttributionCaptureURL {
				t.Fatalf("capture = %q, want %q", ft.Capture, AttributionCaptureURL)
			}
			if got := ft.SourceLabel(); got != c.label {
				t.Fatalf("label = %q, want %q", got, c.label)
			}
			if (c.source == "") != (ft.Source == nil) || (ft.Source != nil && *ft.Source != c.source) {
				t.Fatalf("source = %s, want %q", val(ft.Source), c.source)
			}
			if (c.landing == "") != (ft.LandingPath == nil) || (ft.LandingPath != nil && *ft.LandingPath != c.landing) {
				t.Fatalf("landing = %s, want %q", val(ft.LandingPath), c.landing)
			}
			// The request is the touch: its instant, in UTC. No referrer and no visitor id exist here.
			if ft.At == nil || !ft.At.Equal(now) || ft.At.Location() != time.UTC {
				t.Fatalf("at = %v, want %v in UTC", ft.At, now)
			}
			if ft.ReferrerHost != nil || ft.VisitorID != nil {
				t.Fatalf("a URL touch has no referrer and no visitor id: %+v", ft)
			}
		})
	}
}

// The URL path and the cookie path must agree on what a valid value is: one set of sanitisers.
func TestURLAttribution_SanitisesLikeTheCookie(t *testing.T) {
	for _, v := range []string{"ads", " Ads ", "a b", "-x", "reelbolt.ai", "x.reelbolt.ai", "news.example.com", "utm+value", "50%", strings.Repeat("a", 64), strings.Repeat("a", 65), ""} {
		cookie := ParseFirstTouchCookie(enc(`{"v":1,"s":"` + v + `","m":"` + v + `","c":"keep"}`))
		fromURL := URLAttribution{Source: v, Medium: v, Campaign: "keep"}.FirstTouch(time.Now())
		if cookie == nil || fromURL == nil {
			t.Fatalf("%q: both touches keep the campaign, got cookie %v url %v", v, cookie, fromURL)
		}
		if val(cookie.Source) != val(fromURL.Source) || val(cookie.Medium) != val(fromURL.Medium) {
			t.Fatalf("%q: cookie kept source %s medium %s, the URL path kept source %s medium %s",
				v, val(cookie.Source), val(cookie.Medium), val(fromURL.Source), val(fromURL.Medium))
		}
	}
	for _, l := range []string{"/pricing", "/Pricing?x=1", "//evil.example", "pricing", "/a b", "/" + strings.Repeat("a", 119), "/" + strings.Repeat("a", 120)} {
		cookie := ParseFirstTouchCookie(enc(`{"v":1,"c":"keep","l":"` + l + `"}`))
		fromURL := URLAttribution{Campaign: "keep", LandingPath: l}.FirstTouch(time.Now())
		if val(cookie.LandingPath) != val(fromURL.LandingPath) {
			t.Fatalf("%q: cookie kept %s, the URL path kept %s", l, val(cookie.LandingPath), val(fromURL.LandingPath))
		}
	}
}

func TestURLAttribution_Sanitised(t *testing.T) {
	got := URLAttribution{Source: " HN ", Medium: "a b", Campaign: "launch", Content: "x/y", Term: "T1", LandingPath: "/For/X?y=1"}.Sanitised()
	want := URLAttribution{Source: "hn", Campaign: "launch", Term: "t1", LandingPath: "/for/x"}
	if got != want {
		t.Fatalf("got %+v, want %+v", got, want)
	}
	if !(URLAttribution{Source: "a b"}).Sanitised().IsZero() || (URLAttribution{Term: "x"}).IsZero() {
		t.Fatal("IsZero must be true exactly when no value is carried")
	}
}

// observedAttributionRow runs the recorder with the insert captured.
func observedAttributionRow(t *testing.T, ft *FirstTouch) *models.UserAttribution {
	t.Helper()
	var row *models.UserAttribution
	t.Cleanup(ObserveAttributionWrites(func(r *models.UserAttribution) { row = r }))
	RecordAttributionBestEffort(deadDB(t, false), uuid.New(), ft)
	return row
}

func TestRecordAttribution_WritesTheCapture(t *testing.T) {
	if row := observedAttributionRow(t, ParseFirstTouchCookie(enc(`{"v":1,"s":"ads"}`))); row == nil || val(row.Capture) != "cookie" {
		t.Fatalf("cookie touch: row %+v, want capture cookie", row)
	}
	now := time.Date(2026, 11, 11, 9, 0, 0, 0, time.UTC)
	row := observedAttributionRow(t, URLAttribution{Campaign: "spring", Content: "flyer-1"}.FirstTouch(now))
	if row == nil || val(row.Capture) != "url" || row.SourceLabel != "direct" || val(row.UTMContent) != "flyer-1" ||
		row.FirstTouchAt == nil || !row.FirstTouchAt.Equal(now) {
		t.Fatalf("url touch: row %+v", row)
	}
	// A touch that names no capture (nothing builds one today) leaves the column NULL, never "".
	if row := observedAttributionRow(t, &FirstTouch{}); row == nil || row.Capture != nil {
		t.Fatalf("no capture: row %+v, want a NULL capture", row)
	}
}

// ---- the suggested persona ----

func TestNormalizePersona_IsTheClosedSet(t *testing.T) {
	for _, p := range []string{"creator", "business", "educator", "nonprofit", "software", "agency", "other"} {
		if got, ok := NormalizePersona(p); !ok || got != p {
			t.Fatalf("%q must be a persona, got %q %v", p, got, ok)
		}
	}
	if len(personas) != 7 {
		t.Fatalf("the closed set has seven personas (design section 4), the map has %d", len(personas))
	}
	if got, ok := NormalizePersona("  Creator\t"); !ok || got != "creator" {
		t.Fatalf("trim and lower-case: got %q %v", got, ok)
	}
	for _, p := range []string{"", " ", "creators", "small-business", "developer", "for-creators", "creator,agency", "creator\x00", strings.Repeat("a", 33)} {
		if got, ok := NormalizePersona(p); ok || got != "" {
			t.Fatalf("%q is not a persona, got %q %v", p, got, ok)
		}
	}
}

func TestRecordSuggestedPersona_IsInsertOnConflictDoNothing(t *testing.T) {
	db := deadDB(t, true)
	userID := uuid.New()
	var sqlText string
	var row *models.UserOnboardingProfile
	old := onboardingProfileWriter
	t.Cleanup(func() { onboardingProfileWriter = old })
	onboardingProfileWriter = func(d *gorm.DB, r *models.UserOnboardingProfile) error {
		row = r
		sqlText = d.Session(&gorm.Session{DryRun: true}).Clauses(onConflictDoNothing).Create(r).Statement.SQL.String()
		return nil
	}
	RecordSuggestedPersonaBestEffort(db, userID, " Agency ")
	if !strings.Contains(sqlText, `INSERT INTO "user_onboarding_profiles"`) || !strings.Contains(sqlText, "ON CONFLICT") || !strings.Contains(sqlText, "DO NOTHING") {
		t.Fatalf("sql = %q", sqlText)
	}
	if row == nil || row.UserID != userID || val(row.SuggestedPersona) != "agency" {
		t.Fatalf("row = %+v", row)
	}
	// The suggestion and the two timestamps, nothing else: the answer columns are the Inference API's.
	if row.Persona != nil || row.Material != nil || row.AnsweredAt != nil || row.SkippedAt != nil || row.CreatedAt.IsZero() || !row.UpdatedAt.Equal(row.CreatedAt) {
		t.Fatalf("row = %+v", row)
	}
}

func TestRecordSuggestedPersona_NeverFailsTheFlowAndDropsWhatIsNotAPersona(t *testing.T) {
	calls := 0
	old := onboardingProfileWriter
	t.Cleanup(func() { onboardingProfileWriter = old })
	onboardingProfileWriter = func(*gorm.DB, *models.UserOnboardingProfile) error { calls++; return errors.New("boom") }

	RecordSuggestedPersonaBestEffort(nil, uuid.New(), "creator")             // no handle: silent no-op
	RecordSuggestedPersonaBestEffort(deadDB(t, false), uuid.New(), "wizard") // not a persona: nothing written
	RecordSuggestedPersonaBestEffort(deadDB(t, false), uuid.New(), "")       // nothing suggested
	if calls != 0 {
		t.Fatalf("the writer ran %d time(s) for a nil handle or a non-persona", calls)
	}
	RecordSuggestedPersonaBestEffort(deadDB(t, false), uuid.New(), "creator") // a failed insert must not panic
	if calls != 1 {
		t.Fatalf("the writer ran %d time(s), want 1", calls)
	}

	// And with the real writer against a connection that is refused: logged, swallowed.
	onboardingProfileWriter = old
	RecordSuggestedPersonaBestEffort(deadDB(t, false), uuid.New(), "creator")
}

// ---- the real statements, against PostgreSQL ----

// funnelTestDB opens the services test database and prepares the tables these tests touch. The Go
// API never migrates in production; like the other integration tests here it creates what it needs.
func funnelTestDB(t *testing.T) *gorm.DB {
	t.Helper()
	dsn := os.Getenv("DATABASE_URL")
	if dsn == "" {
		t.Skip("DATABASE_URL not set; skipping the new-user funnel GORM integration tests")
	}
	db, err := database.Initialize(dsn)
	if err != nil {
		t.Skipf("Postgres not reachable (DATABASE_URL=%q): %v", dsn, err)
	}
	if err := db.AutoMigrate(
		&models.ProductEvent{},
		&models.Organization{},
		&models.OrganizationMembership{},
		&models.ApplicationUser{},
		&models.UserAttribution{},
		&models.UserOnboardingProfile{},
	); err != nil {
		t.Skipf("could not prepare the funnel test tables: %v", err)
	}
	return db
}

func TestRecordAttribution_StoresCaptureAndFirstWriteWins(t *testing.T) {
	db := funnelTestDB(t)
	userID := uuid.New()
	now := time.Now().UTC().Truncate(time.Second)

	RecordAttributionBestEffort(db, userID, URLAttribution{Source: "hackernews", Content: "hn-launch", LandingPath: "/for/software-teams"}.FirstTouch(now))
	// A later touch for the same user (a cookie one, even) changes nothing: first write wins.
	RecordAttributionBestEffort(db, userID, ParseFirstTouchCookie(enc(`{"v":1,"s":"ads"}`)))

	var rows []models.UserAttribution
	if err := db.Where("user_id = ?", userID).Find(&rows).Error; err != nil {
		t.Fatal(err)
	}
	if len(rows) != 1 {
		t.Fatalf("rows = %d, want 1", len(rows))
	}
	r := rows[0]
	if val(r.Capture) != "url" || r.SourceLabel != "hackernews" || val(r.UTMContent) != "hn-launch" ||
		val(r.LandingPath) != "/for/software-teams" || r.FirstTouchAt == nil || !r.FirstTouchAt.Equal(now) {
		t.Fatalf("row = %+v", r)
	}

	cookieUser := uuid.New()
	RecordAttributionBestEffort(db, cookieUser, ParseFirstTouchCookie(enc(`{"v":1,"s":"ads"}`)))
	var c models.UserAttribution
	if err := db.Where("user_id = ?", cookieUser).First(&c).Error; err != nil || val(c.Capture) != "cookie" {
		t.Fatalf("cookie row %+v err %v, want capture cookie", c, err)
	}
}

// A referrer host wider than a label (65 to 100 characters) used to be written as the label, which
// PostgreSQL refuses for varchar(64): the insert failed and, the write being best effort, the whole
// first touch was dropped. The row must be there, labelled "direct", with the host beside it.
func TestRecordAttribution_StoresAReferrerHostWiderThanALabel(t *testing.T) {
	db := funnelTestDB(t)
	wide := strings.Repeat("a", 61) + ".example.com" // 73 characters
	userID := uuid.New()
	RecordAttributionBestEffort(db, userID, ParseFirstTouchCookie(enc(`{"v":1,"r":"`+wide+`","l":"/pricing"}`)))

	var rows []models.UserAttribution
	if err := db.Where("user_id = ?", userID).Find(&rows).Error; err != nil {
		t.Fatal(err)
	}
	if len(rows) != 1 {
		t.Fatalf("rows = %d, want 1: the first touch was lost", len(rows))
	}
	if r := rows[0]; r.SourceLabel != "direct" || val(r.ReferrerHost) != wide || val(r.LandingPath) != "/pricing" || val(r.Capture) != "cookie" {
		t.Fatalf("row = %+v", r)
	}
}

// The widest URL-carried touch the sanitisers let through (five 64-character campaign values and a
// 120-character landing path, which a provider sign-in now carries too) fits the table's columns:
// a value wider than its column would fail the insert and, the write being best effort, lose the row.
func TestRecordAttribution_StoresTheWidestURLTouch(t *testing.T) {
	db := funnelTestDB(t)
	long := strings.Repeat("a", 64)
	landing := "/" + strings.Repeat("p", 119)
	userID := uuid.New()
	now := time.Now().UTC().Truncate(time.Second)
	RecordAttributionBestEffort(db, userID, URLAttribution{
		Source: long, Medium: long, Campaign: long, Content: long, Term: long, LandingPath: landing,
	}.FirstTouch(now))

	var rows []models.UserAttribution
	if err := db.Where("user_id = ?", userID).Find(&rows).Error; err != nil {
		t.Fatal(err)
	}
	if len(rows) != 1 {
		t.Fatalf("rows = %d, want 1: the first touch was lost", len(rows))
	}
	if r := rows[0]; r.SourceLabel != long || val(r.UTMTerm) != long || val(r.LandingPath) != landing || val(r.Capture) != "url" {
		t.Fatalf("row = %+v", r)
	}
}

// The rollout-skew case: the Go API is serving before the Inference API's migration has added
// user_attribution.capture. The first touch must still be stored, with no capture, rather than lost.
func TestRecordAttribution_SurvivesAMissingCaptureColumn(t *testing.T) {
	db := funnelTestDB(t)
	if err := db.Exec(`ALTER TABLE user_attribution DROP COLUMN capture`).Error; err != nil {
		t.Fatalf("drop capture: %v", err)
	}
	t.Cleanup(func() {
		if err := db.Exec(`ALTER TABLE user_attribution ADD COLUMN IF NOT EXISTS capture varchar(16)`).Error; err != nil {
			t.Errorf("restore capture: %v", err)
		}
	})

	userID := uuid.New()
	RecordAttributionBestEffort(db, userID, ParseFirstTouchCookie(enc(`{"v":1,"s":"ads","c":"spring"}`)))

	var got struct {
		SourceLabel string
		UtmCampaign *string
	}
	if err := db.Raw(`SELECT source_label, utm_campaign FROM user_attribution WHERE user_id = ?`, userID).Scan(&got).Error; err != nil {
		t.Fatal(err)
	}
	if got.SourceLabel != "ads" || val(got.UtmCampaign) != "spring" {
		t.Fatalf("the first touch was lost with the column missing: %+v", got)
	}
}

func TestRecordSuggestedPersona_WritesOnceAndNeverOverAnExistingRow(t *testing.T) {
	db := funnelTestDB(t)

	// A new account: one row holding only the suggestion.
	fresh := uuid.New()
	RecordSuggestedPersonaBestEffort(db, fresh, "creator")
	var row models.UserOnboardingProfile
	if err := db.Where("user_id = ?", fresh).First(&row).Error; err != nil {
		t.Fatalf("no row for the new user: %v", err)
	}
	if val(row.SuggestedPersona) != "creator" || row.Persona != nil || row.Material != nil ||
		row.AnsweredAt != nil || row.SkippedAt != nil || row.CreatedAt.IsZero() || row.UpdatedAt.IsZero() {
		t.Fatalf("row = %+v", row)
	}

	// A row the Inference API already wrote (the person answered) is left exactly as it is.
	answered := uuid.New()
	persona, material := "agency", "footage"
	at := time.Now().UTC().Truncate(time.Second)
	if err := db.Create(&models.UserOnboardingProfile{
		UserID: answered, Persona: &persona, Material: &material, AnsweredAt: &at, CreatedAt: at, UpdatedAt: at,
	}).Error; err != nil {
		t.Fatal(err)
	}
	RecordSuggestedPersonaBestEffort(db, answered, "creator")
	var kept []models.UserOnboardingProfile
	if err := db.Where("user_id = ?", answered).Find(&kept).Error; err != nil {
		t.Fatal(err)
	}
	if len(kept) != 1 || val(kept[0].Persona) != "agency" || kept[0].SuggestedPersona != nil || !kept[0].UpdatedAt.Equal(at) {
		t.Fatalf("an existing profile was touched: %+v", kept)
	}

	// Not a persona: nothing at all.
	unknown := uuid.New()
	RecordSuggestedPersonaBestEffort(db, unknown, "wizard")
	var n int64
	if err := db.Model(&models.UserOnboardingProfile{}).Where("user_id = ?", unknown).Count(&n).Error; err != nil || n != 0 {
		t.Fatalf("rows for a non-persona = %d err %v", n, err)
	}
}

// The other rollout-skew case: user_onboarding_profiles does not exist yet. Nothing may fail.
func TestRecordSuggestedPersona_SurvivesAMissingTable(t *testing.T) {
	db := funnelTestDB(t)
	if err := db.Exec(`DROP TABLE IF EXISTS user_onboarding_profiles_hidden`).Error; err != nil {
		t.Fatal(err)
	}
	if err := db.Exec(`ALTER TABLE user_onboarding_profiles RENAME TO user_onboarding_profiles_hidden`).Error; err != nil {
		t.Fatalf("hide the table: %v", err)
	}
	t.Cleanup(func() {
		if err := db.Exec(`ALTER TABLE user_onboarding_profiles_hidden RENAME TO user_onboarding_profiles`).Error; err != nil {
			t.Errorf("restore the table: %v", err)
		}
	})
	RecordSuggestedPersonaBestEffort(db, uuid.New(), "creator") // logged and swallowed
}

// alpha.signup with the Personal organization: the row the handlers now write, read back.
func TestRecordProductEvent_SignupCarriesThePersonalOrganization(t *testing.T) {
	db := funnelTestDB(t)
	userID, orgID := uuid.New(), uuid.New()
	RecordProductEventBestEffort(db, userID, &orgID, ProductEventSignup)
	row := firstProductEvent(t, db, userID, ProductEventSignup)
	if row.OrganizationID == nil || *row.OrganizationID != orgID || row.Source != ProductEventSourceServer {
		t.Fatalf("row = %+v, want organization %s and source server", row, orgID)
	}
}

func TestNormalizePlanKey(t *testing.T) {
	for in, want := range map[string]string{" Pro ": "pro", "team-2": "team-2", "a_b": "a_b", "": "", "-x": "", "a b": "", strings.Repeat("a", 33): "", strings.Repeat("a", 32): strings.Repeat("a", 32)} {
		got, ok := NormalizePlanKey(in)
		if got != want || ok != (want != "") {
			t.Errorf("NormalizePlanKey(%q) = %q,%v want %q", in, got, ok, want)
		}
	}
}

func TestRecordSignupProfile_OneRowForPlanAndPersona(t *testing.T) {
	var rows []*models.UserOnboardingProfile
	t.Cleanup(ObserveOnboardingProfileWrites(func(r *models.UserOnboardingProfile) { rows = append(rows, r) }))
	db := deadDB(t, false)
	RecordSignupProfileBestEffort(db, uuid.New(), "wizard", "")
	RecordSignupProfileBestEffort(db, uuid.New(), "", "pro")
	RecordSignupProfileBestEffort(db, uuid.New(), "creator", "pro")
	if len(rows) != 2 || rows[0].SuggestedPersona != nil || *rows[0].PickedPlan != "pro" || *rows[1].SuggestedPersona != "creator" || *rows[1].PickedPlan != "pro" {
		t.Fatalf("rows = %+v", rows)
	}
}
