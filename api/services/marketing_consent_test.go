package services

import (
	"encoding/json"
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

// The wording registry the Inference API serves is canonical; the Go constant
// signup validates against has to name its current entry.
func TestMarketingWordingVersionMatchesRegistry(t *testing.T) {
	raw, err := os.ReadFile("../../inference/src/ReelBolt.Inference.Api/Marketing/marketing-consent.wording.json")
	if err != nil {
		t.Fatalf("read the wording registry: %v", err)
	}
	var reg struct {
		Current  string            `json:"current"`
		Versions map[string]string `json:"versions"`
	}
	if err := json.Unmarshal(raw, &reg); err != nil {
		t.Fatalf("parse the wording registry: %v", err)
	}
	if reg.Current != MarketingWordingVersion {
		t.Fatalf("registry current = %q, Go MarketingWordingVersion = %q", reg.Current, MarketingWordingVersion)
	}
	if strings.TrimSpace(reg.Versions[reg.Current]) == "" {
		t.Fatalf("the registry has no text for its current version %q", reg.Current)
	}
}

func TestMarketingConfirmTemplate_PlainWithoutUnsubscribe(t *testing.T) {
	m := NewSMTPMailer(MailerConfig{PublicBaseURL: "https://reelbolt.ai"})
	link := "https://reelbolt.ai/app/marketing/confirm?token=c1.k1.abc"
	r, err := m.Render("user@example.com", TemplateMarketingConfirm, MailData{Link: link})
	if err != nil {
		t.Fatalf("render: %v", err)
	}
	if string(TemplateMarketingConfirm) != "marketing-confirm" {
		t.Fatalf("template name = %q", TemplateMarketingConfirm)
	}
	if r.Subject != "Confirm your ReelBolt email subscription" {
		t.Fatalf("subject = %q", r.Subject)
	}
	if !strings.Contains(r.Text, link) || !strings.Contains(r.HTML, "token=c1.k1.abc") {
		t.Fatal("the confirm link is missing")
	}
	for _, body := range []string{r.Text, r.HTML} {
		if strings.Contains(strings.ToLower(body), "unsubscribe") {
			t.Fatalf("the confirmation mail must carry no unsubscribe footer:\n%s", body)
		}
		if !strings.Contains(body, "ignore") {
			t.Fatalf("the mail must tell a recipient who did not ask to ignore it:\n%s", body)
		}
	}
	if _, err := m.Render("user@example.com", TemplateMarketingConfirm, MailData{}); err == nil {
		t.Fatal("a confirmation mail without a link must be refused")
	}
}

func newUser(verified bool) *models.ApplicationUser {
	u := &models.ApplicationUser{ID: uuid.New(), Email: "ada@example.com"}
	if verified {
		at := time.Date(2026, 10, 12, 9, 0, 0, 0, time.UTC)
		u.EmailVerifiedAt = &at
	}
	return u
}

func TestSignupMarketingConsent_NewUnverifiedRowIsOptedInUnconfirmed(t *testing.T) {
	at := time.Date(2026, 10, 12, 10, 0, 0, 0, time.UTC)
	ip := "203.0.113.9"
	u := newUser(false)
	c, events := SignupMarketingConsent(nil, u, MarketingOptIn{WordingVersion: MarketingWordingVersion, IP: &ip, UserAgent: "UA", At: at})

	if c.ID == uuid.Nil || c.Email != "ada@example.com" || c.UserID == nil || *c.UserID != u.ID {
		t.Fatalf("identity: %+v", c)
	}
	if c.Status != models.MarketingConsentStatusOptedIn || c.Source != models.MarketingConsentSourceSignup || c.WordingVersion != MarketingWordingVersion {
		t.Fatalf("state: %+v", c)
	}
	if c.ConsentedAt == nil || !c.ConsentedAt.Equal(at) || c.ConfirmedAt != nil || c.WithdrawnAt != nil {
		t.Fatalf("timestamps: %+v", c)
	}
	if c.IP == nil || *c.IP != ip || c.UserAgent == nil || *c.UserAgent != "UA" || !c.UpdatedAt.Equal(at) {
		t.Fatalf("evidence: %+v", c)
	}
	if len(events) != 1 || events[0].Action != models.MarketingConsentActionGranted || events[0].ConsentID != c.ID ||
		events[0].Source != models.MarketingConsentSourceSignup || events[0].WordingVersion != MarketingWordingVersion ||
		events[0].Email != c.Email || events[0].UserID == nil || *events[0].UserID != u.ID || !events[0].OccurredAt.Equal(at) {
		t.Fatalf("events: %+v", events)
	}
}

func TestSignupMarketingConsent_VerifiedUserIsConfirmedAtOnce(t *testing.T) {
	at := time.Date(2026, 10, 12, 10, 0, 0, 0, time.UTC)
	c, events := SignupMarketingConsent(nil, newUser(true), MarketingOptIn{WordingVersion: MarketingWordingVersion, At: at})
	if c.ConfirmedAt == nil || !c.ConfirmedAt.Equal(at) {
		t.Fatalf("an invited (already verified) signup is confirmed at once: %+v", c)
	}
	if len(events) != 2 || events[0].Action != models.MarketingConsentActionGranted || events[1].Action != models.MarketingConsentActionConfirmed {
		t.Fatalf("events: %+v", events)
	}
}

// A verified signup (an invite proved the address) may rewrite the row, Withdrawn included.
func TestSignupMarketingConsent_VerifiedSignupUpsertsAnExistingRowByEmail(t *testing.T) {
	at := time.Date(2026, 10, 12, 10, 0, 0, 0, time.UTC)
	withdrawn := at.Add(-time.Hour)
	existing := &models.MarketingConsent{
		ID: uuid.New(), Email: "ada@example.com", Status: models.MarketingConsentStatusWithdrawn,
		Source: models.MarketingConsentSourceSiteFooter, WordingVersion: "old", WithdrawnAt: &withdrawn, ConfirmedAt: &withdrawn,
		ResubscribeRequestedAt: &withdrawn,
	}
	u := newUser(true)
	c, events := SignupMarketingConsent(existing, u, MarketingOptIn{WordingVersion: MarketingWordingVersion, At: at})
	if c.ID != existing.ID || c.Status != models.MarketingConsentStatusOptedIn || c.WithdrawnAt != nil || c.ConfirmedAt == nil ||
		c.UserID == nil || *c.UserID != u.ID || c.Source != models.MarketingConsentSourceSignup || c.WordingVersion != MarketingWordingVersion ||
		c.ResubscribeRequestedAt != nil {
		t.Fatalf("upsert: %+v", c)
	}
	if len(events) != 2 || events[0].ConsentID != existing.ID {
		t.Fatalf("events: %+v", events)
	}
}

// S-18: an unverified signup never rewrites a Withdrawn row. It stays Withdrawn with its withdrawal
// evidence and owner; the request is recorded (resubscribe_requested_at and a Requested event naming
// the signup's user), and only verifying the address opts it back in.
func TestSignupMarketingConsent_UnverifiedSignupLeavesAWithdrawnRowWithdrawn(t *testing.T) {
	at := time.Date(2026, 10, 12, 10, 0, 0, 0, time.UTC)
	withdrawn := at.Add(-time.Hour)
	ip := "203.0.113.9"
	existing := &models.MarketingConsent{
		ID: uuid.New(), Email: "ada@example.com", Status: models.MarketingConsentStatusWithdrawn,
		Source: models.MarketingConsentSourceSiteFooter, WordingVersion: "old", WithdrawnAt: &withdrawn, ConfirmedAt: &withdrawn,
		UpdatedAt: withdrawn,
	}
	u := newUser(false)
	c, events := SignupMarketingConsent(existing, u, MarketingOptIn{WordingVersion: MarketingWordingVersion, IP: &ip, UserAgent: "UA", At: at})
	if c.Status != models.MarketingConsentStatusWithdrawn || c.WithdrawnAt == nil || !c.WithdrawnAt.Equal(withdrawn) ||
		c.UserID != nil || c.Source != models.MarketingConsentSourceSiteFooter || c.WordingVersion != "old" || c.ConsentedAt != nil {
		t.Fatalf("a Withdrawn row was rewritten by an unverified signup: %+v", c)
	}
	if c.ResubscribeRequestedAt == nil || !c.ResubscribeRequestedAt.Equal(at) || !c.UpdatedAt.Equal(at) {
		t.Fatalf("the re-subscribe request is not recorded: %+v", c)
	}
	if len(events) != 1 {
		t.Fatalf("events: %+v", events)
	}
	ev := events[0]
	if ev.Action != models.MarketingConsentActionRequested || ev.ConsentID != existing.ID || ev.Source != models.MarketingConsentSourceSignup ||
		ev.WordingVersion != MarketingWordingVersion || ev.UserID == nil || *ev.UserID != u.ID || ev.IP == nil || *ev.IP != ip || !ev.OccurredAt.Equal(at) {
		t.Fatalf("request event: %+v", ev)
	}
}

// S-18: an unverified signup does not claim (bind user_id on) or rewrite a row that has no owner.
func TestSignupMarketingConsent_UnverifiedSignupDoesNotClaimAnUnownedRow(t *testing.T) {
	at := time.Date(2026, 10, 12, 10, 0, 0, 0, time.UTC)
	requested := at.Add(-time.Hour)
	existing := &models.MarketingConsent{
		ID: uuid.New(), Email: "ada@example.com", Status: models.MarketingConsentStatusPending,
		Source: models.MarketingConsentSourceSiteFooter, WordingVersion: "old", RequestedAt: &requested, UpdatedAt: requested,
	}
	before := *existing
	c, events := SignupMarketingConsent(existing, newUser(false), MarketingOptIn{WordingVersion: MarketingWordingVersion, At: at})
	if c != before {
		t.Fatalf("an unowned row was claimed or rewritten: %+v", c)
	}
	if len(events) != 1 || events[0].Action != models.MarketingConsentActionRequested {
		t.Fatalf("events: %+v", events)
	}
}

// S-18: verifying the address is what turns a signup's recorded request into an opt-in: the row
// becomes OptedIn and confirmed under the request's wording, and an unowned row is bound now.
func TestConfirmSignupOptInOnVerify_RevivesARequestedRow(t *testing.T) {
	at := time.Date(2026, 10, 12, 12, 0, 0, 0, time.UTC)
	withdrawn := at.Add(-3 * time.Hour)
	reqAt := at.Add(-time.Hour)
	uid := uuid.New()
	newRow := func() *models.MarketingConsent {
		return &models.MarketingConsent{ID: uuid.New(), Email: "ada@example.com", Status: models.MarketingConsentStatusWithdrawn,
			Source: models.MarketingConsentSourceSiteFooter, WordingVersion: "old", WithdrawnAt: &withdrawn, ResubscribeRequestedAt: &reqAt}
	}
	request := func(row *models.MarketingConsent, user uuid.UUID, when time.Time) *models.MarketingConsentEvent {
		return &models.MarketingConsentEvent{ID: uuid.New(), ConsentID: row.ID, UserID: &user, Action: models.MarketingConsentActionRequested,
			Source: models.MarketingConsentSourceSignup, WordingVersion: MarketingWordingVersion, OccurredAt: when}
	}

	row := newRow()
	events := ConfirmSignupOptInOnVerify(row, request(row, uid, reqAt), uid, at)
	if row.Status != models.MarketingConsentStatusOptedIn || row.ConfirmedAt == nil || !row.ConfirmedAt.Equal(at) || row.WithdrawnAt != nil ||
		row.ResubscribeRequestedAt != nil || row.UserID == nil || *row.UserID != uid || row.Source != models.MarketingConsentSourceSignup ||
		row.WordingVersion != MarketingWordingVersion {
		t.Fatalf("revived row: %+v", row)
	}
	if len(events) != 1 || events[0].Action != models.MarketingConsentActionConfirmed {
		t.Fatalf("events: %+v", events)
	}

	for name, tc := range map[string]struct {
		mutate func(*models.MarketingConsent)
		user   uuid.UUID
		when   time.Time
	}{
		"another user's request":        {func(*models.MarketingConsent) {}, uuid.New(), reqAt},
		"request before the withdrawal": {func(*models.MarketingConsent) {}, uid, withdrawn.Add(-time.Hour)},
		"request cleared (said no)":     {func(r *models.MarketingConsent) { r.ResubscribeRequestedAt = nil }, uid, reqAt},
	} {
		row := newRow()
		tc.mutate(row)
		before := *row
		if ev := ConfirmSignupOptInOnVerify(row, request(row, tc.user, tc.when), uid, at); ev != nil || *row != before {
			t.Fatalf("%s: revived %+v", name, row)
		}
	}
	if ev := ConfirmSignupOptInOnVerify(newRow(), nil, uid, at); ev != nil {
		t.Fatal("no request: revived")
	}
}

// S-32: an opt-in is pending for the verify mail exactly when verifying would confirm it.
func TestSignupOptInPending(t *testing.T) {
	uid := uuid.New()
	at := time.Date(2026, 10, 12, 12, 0, 0, 0, time.UTC)
	own := &models.MarketingConsent{ID: uuid.New(), UserID: &uid, Status: models.MarketingConsentStatusOptedIn}
	if !SignupOptInPending(own, nil, uid) {
		t.Fatal("the user's own unconfirmed opt-in is pending")
	}
	confirmed := *own
	confirmed.ConfirmedAt = &at
	if SignupOptInPending(&confirmed, nil, uid) {
		t.Fatal("a confirmed opt-in is not pending")
	}
	pending := &models.MarketingConsent{ID: uuid.New(), Status: models.MarketingConsentStatusPending}
	req := &models.MarketingConsentEvent{ConsentID: pending.ID, UserID: &uid, Action: models.MarketingConsentActionRequested,
		Source: models.MarketingConsentSourceSignup, OccurredAt: at}
	if !SignupOptInPending(pending, req, uid) || SignupOptInPending(pending, nil, uid) || SignupOptInPending(nil, nil, uid) {
		t.Fatal("a recorded signup request on an unowned row is pending, and only then")
	}
}

func TestConfirmMarketingConsentOnVerify(t *testing.T) {
	at := time.Date(2026, 10, 12, 11, 0, 0, 0, time.UTC)
	uid := uuid.New()
	pending := &models.MarketingConsent{ID: uuid.New(), Email: "ada@example.com", UserID: &uid, Status: models.MarketingConsentStatusOptedIn, Source: models.MarketingConsentSourceSignup, WordingVersion: MarketingWordingVersion}
	ev := ConfirmMarketingConsentOnVerify(pending, at)
	if ev == nil || pending.ConfirmedAt == nil || !pending.ConfirmedAt.Equal(at) || !pending.UpdatedAt.Equal(at) {
		t.Fatalf("confirm: %+v %+v", pending, ev)
	}
	if ev.Action != models.MarketingConsentActionConfirmed || ev.ConsentID != pending.ID || ev.WordingVersion != MarketingWordingVersion || !ev.OccurredAt.Equal(at) {
		t.Fatalf("event: %+v", ev)
	}
	if again := ConfirmMarketingConsentOnVerify(pending, at.Add(time.Hour)); again != nil || !pending.ConfirmedAt.Equal(at) {
		t.Fatal("an already-confirmed row is left alone")
	}
	withdrawn := &models.MarketingConsent{ID: uuid.New(), Status: models.MarketingConsentStatusWithdrawn}
	if ev := ConfirmMarketingConsentOnVerify(withdrawn, at); ev != nil || withdrawn.ConfirmedAt != nil {
		t.Fatal("only an OptedIn row is confirmed")
	}
}

// connectMarketingTestDB opens a Postgres for the GORM consent tests. Unlike
// connectTestDB it only runs against an explicitly configured DATABASE_URL, so
// it never writes into whatever database happens to listen on localhost.
func connectMarketingTestDB(t *testing.T) {
	t.Helper()
	dsn := os.Getenv("DATABASE_URL")
	if dsn == "" {
		t.Skip("DATABASE_URL not set; skipping the GORM marketing consent tests")
	}
	db, err := database.Initialize(dsn)
	if err != nil {
		t.Skipf("Postgres not reachable (DATABASE_URL=%q): %v", dsn, err)
	}
	// Test-only schema: the Inference API owns these tables through EF.
	if err := db.AutoMigrate(&models.MarketingConsent{}, &models.MarketingConsentEvent{}, &models.ApplicationUser{}, &models.UserToken{}); err != nil {
		t.Skipf("could not prepare the marketing consent tables: %v", err)
	}
}

func TestGorm_SignupMarketingConsent_RollsBackWithTheTransaction(t *testing.T) {
	connectMarketingTestDB(t)
	u := newUser(false)
	u.Email = "rollback-" + uuid.NewString() + "@example.com"
	boom := errors.New("later step failed")
	err := database.DB.Transaction(func(tx *gorm.DB) error {
		if err := writeSignupMarketingConsentTx(tx, u, &MarketingOptIn{WordingVersion: MarketingWordingVersion, At: time.Now().UTC()}); err != nil {
			return err
		}
		var n int64
		tx.Model(&models.MarketingConsentEvent{}).Where("email = ?", u.Email).Count(&n)
		if n != 1 {
			t.Errorf("inside the transaction the Granted event exists, got %d", n)
		}
		return boom
	})
	if !errors.Is(err, boom) {
		t.Fatalf("tx err = %v", err)
	}
	var consents, events int64
	database.DB.Model(&models.MarketingConsent{}).Where("email = ?", u.Email).Count(&consents)
	database.DB.Model(&models.MarketingConsentEvent{}).Where("email = ?", u.Email).Count(&events)
	if consents != 0 || events != 0 {
		t.Fatalf("a rolled-back signup must leave no consent (%d) or event (%d)", consents, events)
	}
}

func TestGorm_VerifyEmail_ConfirmsPendingOptIn(t *testing.T) {
	connectMarketingTestDB(t)
	now := time.Now().UTC().Truncate(time.Microsecond)
	u := newUser(false)
	u.Email = "verify-" + uuid.NewString() + "@example.com"
	u.CreatedAt = now
	tok, raw, err := NewToken(u.ID, u.Email, models.UserTokenVerifyEmail, VerifyEmailTTL, now)
	if err != nil {
		t.Fatal(err)
	}
	if err := database.DB.Transaction(func(tx *gorm.DB) error {
		if err := tx.Create(u).Error; err != nil {
			return err
		}
		if err := tx.Create(tok).Error; err != nil {
			return err
		}
		return writeSignupMarketingConsentTx(tx, u, &MarketingOptIn{WordingVersion: MarketingWordingVersion, At: now})
	}); err != nil {
		t.Fatalf("seed: %v", err)
	}

	if _, err := (GormAccountStore{}).ConsumeVerifyEmail(HashToken(raw), now.Add(time.Minute)); err != nil {
		t.Fatalf("verify: %v", err)
	}
	var c models.MarketingConsent
	if err := database.DB.First(&c, "email = ?", u.Email).Error; err != nil {
		t.Fatal(err)
	}
	if c.ConfirmedAt == nil || c.Status != models.MarketingConsentStatusOptedIn {
		t.Fatalf("consent after verify: %+v", c)
	}
	var actions []string
	database.DB.Model(&models.MarketingConsentEvent{}).Where("consent_id = ?", c.ID).Order("occurred_at, action").Pluck("action", &actions)
	if strings.Join(actions, ",") != "Granted,Confirmed" {
		t.Fatalf("events: %v", actions)
	}
}

// S-18, against Postgres: an unverified signup leaves an unowned Pending row unclaimed; verifying the
// address then opts it in and binds it.
func TestGorm_SignupMarketingConsent_ClaimsAnUnownedRowOnlyOnVerify(t *testing.T) {
	connectMarketingTestDB(t)
	now := time.Now().UTC().Truncate(time.Microsecond)
	email := "upsert-" + uuid.NewString() + "@example.com"
	pending := models.MarketingConsent{ID: uuid.New(), Email: email, Status: models.MarketingConsentStatusPending,
		Source: models.MarketingConsentSourceSiteFooter, WordingVersion: MarketingWordingVersion, RequestedAt: &now, UpdatedAt: now}
	if err := database.DB.Create(&pending).Error; err != nil {
		t.Fatal(err)
	}
	u := newUser(false)
	u.Email = email
	u.CreatedAt = now
	tok, raw, err := NewToken(u.ID, u.Email, models.UserTokenVerifyEmail, VerifyEmailTTL, now)
	if err != nil {
		t.Fatal(err)
	}
	if err := database.DB.Transaction(func(tx *gorm.DB) error {
		if err := tx.Create(u).Error; err != nil {
			return err
		}
		if err := tx.Create(tok).Error; err != nil {
			return err
		}
		return writeSignupMarketingConsentTx(tx, u, &MarketingOptIn{WordingVersion: MarketingWordingVersion, At: now})
	}); err != nil {
		t.Fatal(err)
	}
	var rows []models.MarketingConsent
	database.DB.Where("email = ?", email).Find(&rows)
	if len(rows) != 1 || rows[0].ID != pending.ID || rows[0].Status != models.MarketingConsentStatusPending || rows[0].UserID != nil ||
		rows[0].Source != models.MarketingConsentSourceSiteFooter {
		t.Fatalf("an unverified signup claimed or rewrote the row: %+v", rows)
	}
	if ok, err := (GormAccountStore{}).MarketingOptInPending(u.ID, email); err != nil || !ok {
		t.Fatalf("the signup's request is pending: %v %v", ok, err)
	}

	if _, err := (GormAccountStore{}).ConsumeVerifyEmail(HashToken(raw), now.Add(time.Minute)); err != nil {
		t.Fatalf("verify: %v", err)
	}
	var got models.MarketingConsent
	if err := database.DB.First(&got, "email = ?", email).Error; err != nil {
		t.Fatal(err)
	}
	if got.Status != models.MarketingConsentStatusOptedIn || got.ConfirmedAt == nil || got.UserID == nil || *got.UserID != u.ID ||
		got.Source != models.MarketingConsentSourceSignup {
		t.Fatalf("after verify: %+v", got)
	}
	if ok, _ := (GormAccountStore{}).MarketingOptInPending(u.ID, email); ok {
		t.Fatal("a confirmed opt-in is still pending")
	}
}

// S-18, against Postgres: a Withdrawn row stays Withdrawn through an unverified signup (the request is
// recorded in resubscribe_requested_at) and is opted back in by verifying the address.
func TestGorm_SignupMarketingConsent_WithdrawnStaysWithdrawnUntilVerify(t *testing.T) {
	connectMarketingTestDB(t)
	now := time.Now().UTC().Truncate(time.Microsecond)
	withdrawn := now.Add(-time.Hour)
	email := "withdrawn-" + uuid.NewString() + "@example.com"
	row := models.MarketingConsent{ID: uuid.New(), Email: email, Status: models.MarketingConsentStatusWithdrawn,
		Source: models.MarketingConsentSourceSiteFooter, WordingVersion: "old", WithdrawnAt: &withdrawn, UpdatedAt: withdrawn}
	if err := database.DB.Create(&row).Error; err != nil {
		t.Fatal(err)
	}
	u := newUser(false)
	u.Email = email
	u.CreatedAt = now
	tok, raw, err := NewToken(u.ID, u.Email, models.UserTokenVerifyEmail, VerifyEmailTTL, now)
	if err != nil {
		t.Fatal(err)
	}
	if err := database.DB.Transaction(func(tx *gorm.DB) error {
		if err := tx.Create(u).Error; err != nil {
			return err
		}
		if err := tx.Create(tok).Error; err != nil {
			return err
		}
		return writeSignupMarketingConsentTx(tx, u, &MarketingOptIn{WordingVersion: MarketingWordingVersion, At: now})
	}); err != nil {
		t.Fatal(err)
	}
	var got models.MarketingConsent
	if err := database.DB.First(&got, "email = ?", email).Error; err != nil {
		t.Fatal(err)
	}
	if got.Status != models.MarketingConsentStatusWithdrawn || got.WithdrawnAt == nil || got.UserID != nil ||
		got.ResubscribeRequestedAt == nil || !got.ResubscribeRequestedAt.Equal(now) {
		t.Fatalf("after an unverified signup: %+v", got)
	}

	if _, err := (GormAccountStore{}).ConsumeVerifyEmail(HashToken(raw), now.Add(time.Minute)); err != nil {
		t.Fatalf("verify: %v", err)
	}
	got = models.MarketingConsent{} // GORM leaves a pointer field set when it scans NULL into it
	if err := database.DB.First(&got, "email = ?", email).Error; err != nil {
		t.Fatal(err)
	}
	if got.Status != models.MarketingConsentStatusOptedIn || got.ConfirmedAt == nil || got.WithdrawnAt != nil ||
		got.ResubscribeRequestedAt != nil || got.UserID == nil || *got.UserID != u.ID || got.WordingVersion != MarketingWordingVersion {
		t.Fatalf("after verify: %+v", got)
	}
	var actions []string
	database.DB.Model(&models.MarketingConsentEvent{}).Where("consent_id = ?", row.ID).Order("occurred_at, action").Pluck("action", &actions)
	if strings.Join(actions, ",") != "Requested,Confirmed" {
		t.Fatalf("events: %v", actions)
	}
}

// S-18: an unverified signup never rebinds a row that already belongs to another user.
func TestSignupMarketingConsent_UnverifiedSignupNeverRebindsTheOwner(t *testing.T) {
	at := time.Date(2026, 10, 12, 10, 0, 0, 0, time.UTC)
	owner := uuid.New()
	withdrawn := at.Add(-time.Hour)
	existing := &models.MarketingConsent{
		ID: uuid.New(), Email: "ada@example.com", UserID: &owner, Status: models.MarketingConsentStatusWithdrawn,
		Source: models.MarketingConsentSourceSignup, WordingVersion: "old", WithdrawnAt: &withdrawn,
	}
	c, _ := SignupMarketingConsent(existing, newUser(false), MarketingOptIn{WordingVersion: MarketingWordingVersion, At: at})
	if c.UserID == nil || *c.UserID != owner {
		t.Fatalf("user_id rebound by an unverified signup: %v", c.UserID)
	}
}

// S-18, against Postgres: the confirmed row and its history are exactly as before the signup.
func TestGorm_SignupMarketingConsent_LeavesAConfirmedRowUntouched(t *testing.T) {
	connectMarketingTestDB(t)
	now := time.Now().UTC().Truncate(time.Microsecond)
	confirmed := now.Add(-48 * time.Hour)
	email := "confirmed-" + uuid.NewString() + "@example.com"
	owner := uuid.New()
	row := models.MarketingConsent{ID: uuid.New(), Email: email, UserID: &owner, Status: models.MarketingConsentStatusOptedIn,
		Source: models.MarketingConsentSourceSiteFooter, WordingVersion: "old", ConsentedAt: &confirmed, ConfirmedAt: &confirmed, UpdatedAt: confirmed}
	if err := database.DB.Create(&row).Error; err != nil {
		t.Fatal(err)
	}
	u := newUser(false)
	u.Email = email
	if err := database.DB.Transaction(func(tx *gorm.DB) error {
		return writeSignupMarketingConsentTx(tx, u, &MarketingOptIn{WordingVersion: MarketingWordingVersion, At: now})
	}); err != nil {
		t.Fatal(err)
	}
	var got models.MarketingConsent
	if err := database.DB.First(&got, "email = ?", email).Error; err != nil {
		t.Fatal(err)
	}
	if got.UserID == nil || *got.UserID != owner || got.Source != models.MarketingConsentSourceSiteFooter || got.WordingVersion != "old" ||
		got.ConfirmedAt == nil || !got.ConfirmedAt.Equal(confirmed) || !got.UpdatedAt.Equal(confirmed) {
		t.Fatalf("row rewritten: %+v", got)
	}
	var n int64
	database.DB.Model(&models.MarketingConsentEvent{}).Where("consent_id = ?", row.ID).Count(&n)
	if n != 0 {
		t.Fatalf("events appended: %d", n)
	}
}

// S-19: verify-email confirms the consent row only when it is the verifying user's AND for the
// address being verified. A user who opted in at one address and then changed it must not confirm the
// old address's row by verifying the new one.
func TestGorm_VerifyEmail_DoesNotConfirmTheRowOfAnotherAddress(t *testing.T) {
	connectMarketingTestDB(t)
	now := time.Now().UTC().Truncate(time.Microsecond)
	u := newUser(false)
	old := "old-" + uuid.NewString() + "@example.com"
	u.Email = old
	u.CreatedAt = now
	if err := database.DB.Transaction(func(tx *gorm.DB) error {
		if err := tx.Create(u).Error; err != nil {
			return err
		}
		return writeSignupMarketingConsentTx(tx, u, &MarketingOptIn{WordingVersion: MarketingWordingVersion, At: now})
	}); err != nil {
		t.Fatalf("seed: %v", err)
	}
	// The account now has another address, and a verification token for it.
	if err := database.DB.Model(&models.ApplicationUser{}).Where("id = ?", u.ID).Update("email", "New-"+old).Error; err != nil {
		t.Fatal(err)
	}
	// The token is bound to the address it was sent to (T-009): the new one, not u.Email (still old).
	tok, raw, err := NewToken(u.ID, "New-"+old, models.UserTokenVerifyEmail, VerifyEmailTTL, now)
	if err != nil {
		t.Fatal(err)
	}
	if err := database.DB.Create(tok).Error; err != nil {
		t.Fatal(err)
	}
	if _, err := (GormAccountStore{}).ConsumeVerifyEmail(HashToken(raw), now.Add(time.Minute)); err != nil {
		t.Fatalf("verify: %v", err)
	}
	var c models.MarketingConsent
	if err := database.DB.First(&c, "email = ?", old).Error; err != nil {
		t.Fatal(err)
	}
	if c.ConfirmedAt != nil {
		t.Fatalf("verifying another address confirmed %s: %+v", old, c)
	}
}
