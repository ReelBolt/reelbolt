package services

import (
	"errors"
	"os"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
)

// Admin email change with a pending address (issue #1207) against real PostgreSQL: the row locks,
// the partial unique index, the revocation UPDATE and the audit transaction are what the threat
// model (plan.md) leans on, and a fake driver cannot show any of them. Opt-in like the other
// PostgreSQL tests (`go test ./...` needs no database):
//
//	DATABASE_URL='postgres://postgres:pw@127.0.0.1:55471/t002?sslmode=disable' \
//	    go test ./services/ -run EmailChange -v
//
// Test names carry the threat-model row (TM1..TM12) they prove.

func emailChangeTestDB(t *testing.T) *gorm.DB {
	t.Helper()
	dsn := os.Getenv("DATABASE_URL")
	if dsn == "" {
		t.Skip("DATABASE_URL not set; skipping the email change PostgreSQL tests")
	}
	db, err := database.Initialize(dsn)
	if err != nil {
		t.Skipf("Postgres not reachable: %v", err)
	}
	if err := db.AutoMigrate(&models.ApplicationUser{}, &models.UserToken{},
		&models.MarketingConsent{}, &models.MarketingConsentEvent{}); err != nil {
		t.Fatalf("migrate: %v", err)
	}
	for _, ddl := range []string{
		// The model never writes is_service_account; the real schema defaults it to false.
		`ALTER TABLE application_users ALTER COLUMN is_service_account SET DEFAULT false`,
		`UPDATE application_users SET is_service_account = false WHERE is_service_account IS NULL`,
		`CREATE UNIQUE INDEX IF NOT EXISTS ux_email_change_test_email ON application_users (email)`,
		`CREATE UNIQUE INDEX IF NOT EXISTS "IX_user_tokens_one_open_email_change" ON user_tokens (user_id) WHERE purpose = 'EmailChange' AND used_at IS NULL`,
		`CREATE TABLE IF NOT EXISTS admin_audit_events (
			id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
			actor_user_id uuid NULL, target_user_id uuid NULL,
			action varchar(64) NOT NULL, changed_fields jsonb NULL,
			client_ip varchar(64) NULL, user_agent varchar(512) NULL,
			created_at timestamptz NOT NULL DEFAULT now())`,
	} {
		if err := db.Exec(ddl).Error; err != nil {
			t.Fatalf("ddl: %v", err)
		}
	}
	return db
}

func ecUser(t *testing.T, db *gorm.DB, verified bool) *models.ApplicationUser {
	t.Helper()
	now := time.Now().UTC()
	u := &models.ApplicationUser{
		ID: uuid.New(), Email: "u-" + uuid.NewString()[:8] + "@example.com", DisplayName: "U",
		CreatedAt: now, PasswordHash: "x", AuthProvider: "Password",
	}
	if verified {
		u.EmailVerifiedAt = &now
	}
	if err := db.Create(u).Error; err != nil {
		t.Fatalf("create user: %v", err)
	}
	return u
}

// ecAdmin is an active platform admin row (confirm requires the requester to still be one).
func ecAdmin() AdminActor {
	id := uuid.New()
	database.DB.Exec(`INSERT INTO application_users (id, email, display_name, password_hash, is_admin, is_service_account)
VALUES (?, ?, 'A', 'x', true, false)`, id, "admin-"+id.String()[:8]+"@example.com")
	return AdminActor{UserID: &id, ClientIP: "203.0.113.7", UserAgent: "test"}
}

func newAddr() string { return "new-" + uuid.NewString()[:8] + "@example.com" }

type auditRow struct {
	Action        string
	ActorUserID   *uuid.UUID
	ChangedFields string
}

func ecAudits(t *testing.T, db *gorm.DB, target uuid.UUID) []auditRow {
	t.Helper()
	var rows []auditRow
	if err := db.Raw(`SELECT action, actor_user_id, coalesce(changed_fields::text, '') AS changed_fields
FROM admin_audit_events WHERE target_user_id = ? ORDER BY created_at, action`, target).Scan(&rows).Error; err != nil {
		t.Fatalf("read audits: %v", err)
	}
	return rows
}

func reload(t *testing.T, db *gorm.DB, id uuid.UUID) models.ApplicationUser {
	t.Helper()
	var u models.ApplicationUser
	if err := db.First(&u, "id = ?", id).Error; err != nil {
		t.Fatalf("reload: %v", err)
	}
	return u
}

func issue(t *testing.T, u *models.ApplicationUser, purpose string, ttl time.Duration) string {
	t.Helper()
	tok, raw, err := NewToken(u.ID, u.Email, purpose, ttl, time.Now().UTC())
	if err != nil {
		t.Fatal(err)
	}
	if err := (GormAccountStore{}).IssueToken(tok); err != nil {
		t.Fatalf("issue: %v", err)
	}
	return raw
}

// TM1: the request changes nothing on the account; only the owner of the current verified
// mailbox (holder of the token bound to it) can apply it; every step is audited.
func TestEmailChange_TM1_RequestChangesNothingUntilCurrentMailboxConfirms(t *testing.T) {
	db := emailChangeTestDB(t)
	u := ecUser(t, db, true)
	admin := ecAdmin()
	to := strings.ToUpper(newAddr())
	res, err := RequestEmailChange(u.ID, to, admin, time.Now().UTC())
	if err != nil {
		t.Fatalf("request: %v", err)
	}
	if res.User.Email != u.Email || res.PendingEmail != strings.ToLower(to) {
		t.Fatalf("result: %+v", res)
	}
	if got := reload(t, db, u.ID); got.Email != u.Email || got.EmailVerifiedAt == nil {
		t.Fatalf("the request changed the account: %+v", got)
	}
	var tok models.UserToken
	if err := db.First(&tok, "token_hash = ?", HashToken(res.RawToken)).Error; err != nil {
		t.Fatal(err)
	}
	if tok.SentToEmail == nil || *tok.SentToEmail != u.Email || tok.PendingEmail == nil ||
		*tok.PendingEmail != res.PendingEmail || tok.RequestedByUserID == nil || *tok.RequestedByUserID != *admin.UserID {
		t.Fatalf("token row: %+v", tok)
	}
	if _, err := ConfirmEmailChange(HashToken(res.RawToken), AdminActor{ClientIP: "198.51.100.1"}, time.Now().UTC()); err != nil {
		t.Fatalf("confirm: %v", err)
	}
	got := reload(t, db, u.ID)
	if got.Email != res.PendingEmail || got.EmailVerifiedAt != nil || got.TokensInvalidatedAt == nil {
		t.Fatalf("after confirm: %+v", got)
	}
	a := ecAudits(t, db, u.ID)
	if len(a) != 2 || a[0].Action != AdminActionUserEmailChangeRequest || a[1].Action != AdminActionUserEmailChangeConfirm {
		t.Fatalf("audits: %+v", a)
	}
	if a[0].ActorUserID == nil || *a[0].ActorUserID != *admin.UserID || a[1].ActorUserID == nil || *a[1].ActorUserID != u.ID {
		t.Fatalf("audit actors: %+v", a)
	}
	if !strings.Contains(a[0].ChangedFields, u.Email) || !strings.Contains(a[0].ChangedFields, res.PendingEmail) ||
		strings.Contains(a[0].ChangedFields, res.RawToken) {
		t.Fatalf("request audit payload: %s", a[0].ChangedFields)
	}
}

// TM3: only the sha256 is stored, the TTL is 24h, and the token is single-use.
func TestEmailChange_TM3_HashOnly24hSingleUse(t *testing.T) {
	db := emailChangeTestDB(t)
	u := ecUser(t, db, true)
	now := time.Now().UTC()
	res, err := RequestEmailChange(u.ID, newAddr(), ecAdmin(), now)
	if err != nil {
		t.Fatal(err)
	}
	var n int64
	db.Model(&models.UserToken{}).Where("token_hash = ?", res.RawToken).Count(&n)
	if n != 0 {
		t.Fatal("the raw token is stored")
	}
	if d := res.ExpiresAt.Sub(now); d != EmailChangeTTL {
		t.Fatalf("ttl %v", d)
	}
	if _, err := ConfirmEmailChange(HashToken(res.RawToken), AdminActor{}, now); err != nil {
		t.Fatal(err)
	}
	if _, err := ConfirmEmailChange(HashToken(res.RawToken), AdminActor{}, now); !errors.Is(err, ErrInvalidToken) {
		t.Fatalf("second confirm: %v", err)
	}
}

// TM4: confirming revokes every unused token of the user, every purpose.
func TestEmailChange_TM4_ConfirmRevokesEveryOutstandingToken(t *testing.T) {
	db := emailChangeTestDB(t)
	u := ecUser(t, db, true)
	verifyRaw := issue(t, u, models.UserTokenVerifyEmail, VerifyEmailTTL)
	resetRaw := issue(t, u, models.UserTokenResetPassword, ResetPasswordTTL)
	res, err := RequestEmailChange(u.ID, newAddr(), ecAdmin(), time.Now().UTC())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := ConfirmEmailChange(HashToken(res.RawToken), AdminActor{}, time.Now().UTC()); err != nil {
		t.Fatal(err)
	}
	var open int64
	db.Model(&models.UserToken{}).Where("user_id = ? AND used_at IS NULL", u.ID).Count(&open)
	if open != 0 {
		t.Fatalf("%d tokens still open", open)
	}
	if _, err := (GormAccountStore{}).ConsumeVerifyEmail(HashToken(verifyRaw), time.Now().UTC()); !errors.Is(err, ErrInvalidToken) {
		t.Fatalf("old verify link: %v", err)
	}
	if _, err := (GormAccountStore{}).ConsumeResetPassword(HashToken(resetRaw), "h", time.Now().UTC()); !errors.Is(err, ErrInvalidToken) {
		t.Fatalf("old reset link: %v", err)
	}
}

// TM5 (T-009): a verify or reset token is bound to the address it was mailed to. Sent to A and
// redeemed after the account moved to B, it is refused and B stays unverified. A legacy token with
// no sent_to_email still works.
func TestEmailChange_TM5_TokensBoundToSentAddress(t *testing.T) {
	db := emailChangeTestDB(t)
	u := ecUser(t, db, false)
	verifyRaw := issue(t, u, models.UserTokenVerifyEmail, VerifyEmailTTL)
	resetRaw := issue(t, u, models.UserTokenResetPassword, ResetPasswordTTL)
	// The address moves under the tokens (any path, not only a confirm, which would revoke them).
	moved := newAddr()
	if err := db.Model(&models.ApplicationUser{}).Where("id = ?", u.ID).Update("email", moved).Error; err != nil {
		t.Fatal(err)
	}
	if _, err := (GormAccountStore{}).ConsumeVerifyEmail(HashToken(verifyRaw), time.Now().UTC()); !errors.Is(err, ErrInvalidToken) {
		t.Fatalf("verify sent to A redeemed for B: %v", err)
	}
	if got := reload(t, db, u.ID); got.EmailVerifiedAt != nil {
		t.Fatal("B was marked verified")
	}
	if _, err := (GormAccountStore{}).ConsumeResetPassword(HashToken(resetRaw), "h", time.Now().UTC()); !errors.Is(err, ErrInvalidToken) {
		t.Fatalf("reset sent to A redeemed for B: %v", err)
	}
	// Case-insensitive: a token sent to the same address in another case still works.
	upper := issue(t, &models.ApplicationUser{ID: u.ID, Email: strings.ToUpper(moved)}, models.UserTokenVerifyEmail, VerifyEmailTTL)
	if _, err := (GormAccountStore{}).ConsumeVerifyEmail(HashToken(upper), time.Now().UTC()); err != nil {
		t.Fatalf("same address, other case: %v", err)
	}
	// Legacy row: sent_to_email NULL stays valid until it expires.
	legacy, raw, _ := NewToken(u.ID, "", models.UserTokenResetPassword, ResetPasswordTTL, time.Now().UTC())
	legacy.SentToEmail = nil
	if err := (GormAccountStore{}).IssueToken(legacy); err != nil {
		t.Fatal(err)
	}
	if _, err := (GormAccountStore{}).ConsumeResetPassword(HashToken(raw), "h", time.Now().UTC()); err != nil {
		t.Fatalf("legacy token: %v", err)
	}
}

// TM6: before confirm the pending address is not the account's email, so the address-keyed
// public flows (forgot-password, resend, sign-in) cannot find the account by it.
func TestEmailChange_TM6_PendingAddressIsNotTheAccountEmail(t *testing.T) {
	db := emailChangeTestDB(t)
	u := ecUser(t, db, true)
	to := newAddr()
	if _, err := RequestEmailChange(u.ID, to, ecAdmin(), time.Now().UTC()); err != nil {
		t.Fatal(err)
	}
	if found, err := (GormAccountStore{}).FindUserByEmail(to); err != nil || found != nil {
		t.Fatalf("the pending address finds an account: %v %v", found, err)
	}
	p, err := GetPendingEmailChange(u.ID, time.Now().UTC())
	if err != nil || p == nil || p.NewEmail != to {
		t.Fatalf("pending: %+v %v", p, err)
	}
}

// TM7: two confirms race (one wins); confirm after cancel fails; an email changed in between voids
// the token; a second request supersedes the first; the partial index allows one open row.
func TestEmailChange_TM7_Races(t *testing.T) {
	db := emailChangeTestDB(t)

	t.Run("concurrent confirms", func(t *testing.T) {
		u := ecUser(t, db, true)
		res, err := RequestEmailChange(u.ID, newAddr(), ecAdmin(), time.Now().UTC())
		if err != nil {
			t.Fatal(err)
		}
		var wg sync.WaitGroup
		var mu sync.Mutex
		ok := 0
		for i := 0; i < 4; i++ {
			wg.Add(1)
			go func() {
				defer wg.Done()
				if _, err := ConfirmEmailChange(HashToken(res.RawToken), AdminActor{}, time.Now().UTC()); err == nil {
					mu.Lock()
					ok++
					mu.Unlock()
				} else if !errors.Is(err, ErrInvalidToken) {
					t.Errorf("confirm: %v", err)
				}
			}()
		}
		wg.Wait()
		if ok != 1 {
			t.Fatalf("%d confirms succeeded", ok)
		}
		if n := len(ecAudits(t, db, u.ID)); n != 2 {
			t.Fatalf("%d audit rows", n)
		}
	})

	t.Run("confirm after cancel", func(t *testing.T) {
		u := ecUser(t, db, true)
		res, _ := RequestEmailChange(u.ID, newAddr(), ecAdmin(), time.Now().UTC())
		if err := CancelEmailChangeByToken(HashToken(res.RawToken), AdminActor{}, time.Now().UTC()); err != nil {
			t.Fatal(err)
		}
		if _, err := ConfirmEmailChange(HashToken(res.RawToken), AdminActor{}, time.Now().UTC()); !errors.Is(err, ErrInvalidToken) {
			t.Fatalf("confirm after cancel: %v", err)
		}
		if got := reload(t, db, u.ID); got.Email != u.Email {
			t.Fatal("email changed")
		}
	})

	t.Run("email changed in between", func(t *testing.T) {
		u := ecUser(t, db, true)
		res, _ := RequestEmailChange(u.ID, newAddr(), ecAdmin(), time.Now().UTC())
		other := newAddr()
		db.Model(&models.ApplicationUser{}).Where("id = ?", u.ID).Update("email", other)
		if _, err := PreviewEmailChange(HashToken(res.RawToken), time.Now().UTC()); !errors.Is(err, ErrInvalidToken) {
			t.Fatalf("preview: %v", err)
		}
		if _, err := ConfirmEmailChange(HashToken(res.RawToken), AdminActor{}, time.Now().UTC()); !errors.Is(err, ErrInvalidToken) {
			t.Fatalf("confirm: %v", err)
		}
		if err := CancelEmailChangeByToken(HashToken(res.RawToken), AdminActor{}, time.Now().UTC()); !errors.Is(err, ErrInvalidToken) {
			t.Fatalf("cancel: %v", err)
		}
	})

	t.Run("second request supersedes the first", func(t *testing.T) {
		u := ecUser(t, db, true)
		first, _ := RequestEmailChange(u.ID, newAddr(), ecAdmin(), time.Now().UTC())
		second, err := RequestEmailChange(u.ID, newAddr(), ecAdmin(), time.Now().UTC())
		if err != nil {
			t.Fatal(err)
		}
		if _, err := ConfirmEmailChange(HashToken(first.RawToken), AdminActor{}, time.Now().UTC()); !errors.Is(err, ErrInvalidToken) {
			t.Fatalf("superseded token: %v", err)
		}
		var open int64
		db.Model(&models.UserToken{}).Where("user_id = ? AND purpose = ? AND used_at IS NULL", u.ID, models.UserTokenEmailChange).Count(&open)
		if open != 1 {
			t.Fatalf("%d open requests", open)
		}
		if _, err := ConfirmEmailChange(HashToken(second.RawToken), AdminActor{}, time.Now().UTC()); err != nil {
			t.Fatal(err)
		}
	})

	t.Run("confirm concurrent with verify and reset consume", func(t *testing.T) {
		for i := 0; i < 20; i++ {
			u := ecUser(t, db, true)
			verifyRaw := issue(t, u, models.UserTokenVerifyEmail, VerifyEmailTTL)
			resetRaw := issue(t, u, models.UserTokenResetPassword, ResetPasswordTTL)
			res, err := RequestEmailChange(u.ID, newAddr(), ecAdmin(), time.Now().UTC())
			if err != nil {
				t.Fatal(err)
			}
			var wg sync.WaitGroup
			errs := make([]error, 3)
			wg.Add(3)
			go func() {
				defer wg.Done()
				_, errs[0] = ConfirmEmailChange(HashToken(res.RawToken), AdminActor{}, time.Now().UTC())
			}()
			go func() {
				defer wg.Done()
				_, errs[1] = (GormAccountStore{}).ConsumeVerifyEmail(HashToken(verifyRaw), time.Now().UTC())
			}()
			go func() {
				defer wg.Done()
				_, errs[2] = (GormAccountStore{}).ConsumeResetPassword(HashToken(resetRaw), "h", time.Now().UTC())
			}()
			wg.Wait()
			for j, err := range errs {
				if err != nil && !errors.Is(err, ErrInvalidToken) {
					t.Fatalf("run %d op %d: %v (deadlock or other DB error)", i, j, err)
				}
			}
			if errs[0] != nil {
				t.Fatalf("run %d: confirm failed: %v", i, errs[0])
			}
			// Whichever won, the account ends on the new address, unverified.
			if got := reload(t, db, u.ID); got.Email != res.PendingEmail || got.EmailVerifiedAt != nil {
				t.Fatalf("run %d: %+v", i, got)
			}
		}
	})

	t.Run("concurrent requests leave one open", func(t *testing.T) {
		u := ecUser(t, db, true)
		var wg sync.WaitGroup
		for i := 0; i < 3; i++ {
			wg.Add(1)
			go func() { defer wg.Done(); _, _ = RequestEmailChange(u.ID, newAddr(), ecAdmin(), time.Now().UTC()) }()
		}
		wg.Wait()
		var open int64
		db.Model(&models.UserToken{}).Where("user_id = ? AND purpose = ? AND used_at IS NULL", u.ID, models.UserTokenEmailChange).Count(&open)
		if open != 1 {
			t.Fatalf("%d open requests", open)
		}
	})
}

// TM8: the new address taken before confirm: refused with ErrEmailInUse, nothing changes, the
// request is consumed. Case-insensitive.
func TestEmailChange_TM8_SquattedAddressRefusedAtConfirm(t *testing.T) {
	db := emailChangeTestDB(t)
	u := ecUser(t, db, true)
	to := newAddr()
	res, err := RequestEmailChange(u.ID, to, ecAdmin(), time.Now().UTC())
	if err != nil {
		t.Fatal(err)
	}
	squat := &models.ApplicationUser{ID: uuid.New(), Email: strings.ToUpper(to), DisplayName: "S", PasswordHash: "x", AuthProvider: "Password", CreatedAt: time.Now()}
	if err := db.Create(squat).Error; err != nil {
		t.Fatal(err)
	}
	if _, err := ConfirmEmailChange(HashToken(res.RawToken), AdminActor{}, time.Now().UTC()); !errors.Is(err, ErrEmailInUse) {
		t.Fatalf("confirm: %v", err)
	}
	if got := reload(t, db, u.ID); got.Email != u.Email || got.EmailVerifiedAt == nil {
		t.Fatalf("account changed: %+v", got)
	}
	if _, err := ConfirmEmailChange(HashToken(res.RawToken), AdminActor{}, time.Now().UTC()); !errors.Is(err, ErrInvalidToken) {
		t.Fatalf("request not consumed: %v", err)
	}
	// And at request time.
	if _, err := RequestEmailChange(u.ID, strings.ToUpper(to), ecAdmin(), time.Now().UTC()); !errors.Is(err, ErrEmailInUse) {
		t.Fatalf("request for a taken address: %v", err)
	}
}

// TM9: 3 requests per target per 24h, 20 per admin per hour, from the database.
func TestEmailChange_TM9_DurableRateLimits(t *testing.T) {
	db := emailChangeTestDB(t)
	u := ecUser(t, db, true)
	for i := 0; i < EmailChangeMaxPerTarget; i++ {
		if _, err := RequestEmailChange(u.ID, newAddr(), ecAdmin(), time.Now().UTC()); err != nil {
			t.Fatalf("request %d: %v", i, err)
		}
	}
	if _, err := RequestEmailChange(u.ID, newAddr(), ecAdmin(), time.Now().UTC()); !errors.Is(err, ErrEmailChangeRateLimited) {
		t.Fatalf("4th request for one target: %v", err)
	}
	admin := ecAdmin()
	for i := 0; i < EmailChangeMaxPerAdmin; i++ {
		v := ecUser(t, db, true)
		if _, err := RequestEmailChange(v.ID, newAddr(), admin, time.Now().UTC()); err != nil {
			t.Fatalf("admin request %d: %v", i, err)
		}
	}
	v := ecUser(t, db, true)
	if _, err := RequestEmailChange(v.ID, newAddr(), admin, time.Now().UTC()); !errors.Is(err, ErrEmailChangeRateLimited) {
		t.Fatalf("21st request by one admin: %v", err)
	}
}

// TM10: one answer, ErrInvalidToken, for every token failure on preview, confirm and cancel.
func TestEmailChange_TM10_UniformInvalidToken(t *testing.T) {
	db := emailChangeTestDB(t)
	u := ecUser(t, db, true)
	verifyRaw := issue(t, u, models.UserTokenVerifyEmail, VerifyEmailTTL) // wrong purpose
	expired, _ := RequestEmailChange(u.ID, newAddr(), ecAdmin(), time.Now().UTC())
	db.Model(&models.UserToken{}).Where("token_hash = ?", HashToken(expired.RawToken)).Update("expires_at", time.Now().Add(-time.Minute))
	used, _ := RequestEmailChange(u.ID, newAddr(), ecAdmin(), time.Now().UTC()) // supersedes "expired"
	_ = CancelEmailChangeByToken(HashToken(used.RawToken), AdminActor{}, time.Now().UTC())
	for name, hash := range map[string]string{
		"unknown": HashToken("nope"), "wrong purpose": HashToken(verifyRaw),
		"expired": HashToken(expired.RawToken), "used": HashToken(used.RawToken),
	} {
		if _, err := PreviewEmailChange(hash, time.Now().UTC()); !errors.Is(err, ErrInvalidToken) {
			t.Errorf("%s preview: %v", name, err)
		}
		if _, err := ConfirmEmailChange(hash, AdminActor{}, time.Now().UTC()); !errors.Is(err, ErrInvalidToken) {
			t.Errorf("%s confirm: %v", name, err)
		}
	}
	for _, hash := range []string{HashToken("nope"), HashToken(verifyRaw)} {
		if err := CancelEmailChangeByToken(hash, AdminActor{}, time.Now().UTC()); !errors.Is(err, ErrInvalidToken) {
			t.Errorf("cancel: %v", err)
		}
	}
}

// TM11: service accounts and unverified current addresses are refused at the request step; an
// unchanged address too.
func TestEmailChange_TM11_RefusedTargets(t *testing.T) {
	db := emailChangeTestDB(t)
	unverified := ecUser(t, db, false)
	if _, err := RequestEmailChange(unverified.ID, newAddr(), ecAdmin(), time.Now().UTC()); !errors.Is(err, ErrEmailChangeCurrentUnverified) {
		t.Fatalf("unverified: %v", err)
	}
	svc := ecUser(t, db, true)
	db.Exec(`UPDATE application_users SET is_service_account = true WHERE id = ?`, svc.ID)
	if _, err := RequestEmailChange(svc.ID, newAddr(), ecAdmin(), time.Now().UTC()); !errors.Is(err, gorm.ErrRecordNotFound) {
		t.Fatalf("service account: %v", err)
	}
	if err := CancelEmailChange(svc.ID, EmailChangeViaAdmin, ecAdmin(), time.Now().UTC()); !errors.Is(err, gorm.ErrRecordNotFound) {
		t.Fatalf("cancel on a service account: %v", err)
	}
	if _, err := RequestEmailChange(uuid.New(), newAddr(), ecAdmin(), time.Now().UTC()); !errors.Is(err, gorm.ErrRecordNotFound) {
		t.Fatalf("missing user: %v", err)
	}
	u := ecUser(t, db, true)
	if _, err := RequestEmailChange(u.ID, strings.ToUpper(u.Email), ecAdmin(), time.Now().UTC()); !errors.Is(err, ErrEmailUnchanged) {
		t.Fatalf("unchanged: %v", err)
	}
	var n int64
	db.Model(&models.UserToken{}).Where("user_id IN ?", []uuid.UUID{unverified.ID, svc.ID, u.ID}).Count(&n)
	if n != 0 {
		t.Fatalf("%d tokens written by refused requests", n)
	}
}

// TM12: the audit insert is in the same transaction; when it fails nothing is written.
func TestEmailChange_TM12_AuditFailureWritesNothing(t *testing.T) {
	db := emailChangeTestDB(t)
	u := ecUser(t, db, true)
	res, err := RequestEmailChange(u.ID, newAddr(), ecAdmin(), time.Now().UTC())
	if err != nil {
		t.Fatal(err)
	}
	if err := db.Exec(`ALTER TABLE admin_audit_events RENAME TO admin_audit_events_hidden`).Error; err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { db.Exec(`ALTER TABLE admin_audit_events_hidden RENAME TO admin_audit_events`) })

	if _, err := ConfirmEmailChange(HashToken(res.RawToken), AdminActor{}, time.Now().UTC()); err == nil {
		t.Fatal("confirm succeeded without its audit row")
	}
	if got := reload(t, db, u.ID); got.Email != u.Email || got.EmailVerifiedAt == nil {
		t.Fatalf("confirm wrote without audit: %+v", got)
	}
	if err := CancelEmailChangeByToken(HashToken(res.RawToken), AdminActor{}, time.Now().UTC()); err == nil {
		t.Fatal("cancel succeeded without its audit row")
	}
	v := ecUser(t, db, true)
	if _, err := RequestEmailChange(v.ID, newAddr(), ecAdmin(), time.Now().UTC()); err == nil {
		t.Fatal("request succeeded without its audit row")
	}
	var n int64
	db.Model(&models.UserToken{}).Where("user_id = ?", v.ID).Count(&n)
	if n != 0 {
		t.Fatal("a token was written without its audit row")
	}
	if p, _ := GetPendingEmailChange(u.ID, time.Now().UTC()); p == nil {
		t.Fatal("the open request was consumed without an audit row")
	}
}

// Cancel by link, by the user and by an admin: idempotent, audited once with {via}.
func TestEmailChange_CancelVariantsIdempotentAndAudited(t *testing.T) {
	db := emailChangeTestDB(t)
	for _, via := range []string{EmailChangeViaLink, EmailChangeViaUser, EmailChangeViaAdmin} {
		u := ecUser(t, db, true)
		res, err := RequestEmailChange(u.ID, newAddr(), ecAdmin(), time.Now().UTC())
		if err != nil {
			t.Fatal(err)
		}
		for i := 0; i < 2; i++ {
			if via == EmailChangeViaLink {
				err = CancelEmailChangeByToken(HashToken(res.RawToken), AdminActor{}, time.Now().UTC())
			} else {
				err = CancelEmailChange(u.ID, via, AdminActor{UserID: &u.ID}, time.Now().UTC())
			}
			if err != nil {
				t.Fatalf("%s cancel %d: %v", via, i, err)
			}
		}
		if p, _ := GetPendingEmailChange(u.ID, time.Now().UTC()); p != nil {
			t.Fatalf("%s: still pending", via)
		}
		a := ecAudits(t, db, u.ID)
		if len(a) != 2 || a[1].Action != AdminActionUserEmailChangeCancel || !strings.Contains(a[1].ChangedFields, `"via": "`+via+`"`) {
			t.Fatalf("%s audits: %+v", via, a)
		}
	}
}

// Visibility: expired and used requests are not pending.
func TestEmailChange_PendingOnlyOpenAndUnexpired(t *testing.T) {
	db := emailChangeTestDB(t)
	u := ecUser(t, db, true)
	res, _ := RequestEmailChange(u.ID, newAddr(), ecAdmin(), time.Now().UTC())
	m, err := PendingEmailChanges([]uuid.UUID{u.ID, uuid.New()}, time.Now().UTC())
	if err != nil || len(m) != 1 {
		t.Fatalf("pending: %v %v", m, err)
	}
	if p, _ := GetPendingEmailChange(u.ID, res.ExpiresAt.Add(time.Second)); p != nil {
		t.Fatal("expired request shown as pending")
	}
}

// Incident cleanup: a request whose admin was demoted or deleted is refused (uniform invalid) and
// retired; nothing else changes.
func TestEmailChange_RevokedAdminRequestDies(t *testing.T) {
	db := emailChangeTestDB(t)
	for _, revoke := range []string{
		`UPDATE application_users SET is_admin = false WHERE id = ?`,
		`DELETE FROM application_users WHERE id = ?`,
	} {
		admin := ecUser(t, db, true)
		db.Exec(`UPDATE application_users SET is_admin = true WHERE id = ?`, admin.ID)
		u := ecUser(t, db, true)
		res, err := RequestEmailChange(u.ID, newAddr(), AdminActor{UserID: &admin.ID}, time.Now().UTC())
		if err != nil {
			t.Fatal(err)
		}
		db.Exec(revoke, admin.ID)
		if _, err := ConfirmEmailChange(HashToken(res.RawToken), AdminActor{}, time.Now().UTC()); !errors.Is(err, ErrInvalidToken) {
			t.Fatalf("%s: confirm %v", revoke, err)
		}
		if got := reload(t, db, u.ID); got.Email != u.Email || got.EmailVerifiedAt == nil {
			t.Fatalf("%s: account changed", revoke)
		}
		if p, _ := GetPendingEmailChange(u.ID, time.Now().UTC()); p != nil {
			t.Fatalf("%s: request not retired", revoke)
		}
		if n := len(ecAudits(t, db, u.ID)); n != 1 {
			t.Fatalf("%s: %d audit rows", revoke, n)
		}
	}
	// Control: an active admin's request still confirms.
	admin := ecUser(t, db, true)
	db.Exec(`UPDATE application_users SET is_admin = true WHERE id = ?`, admin.ID)
	u := ecUser(t, db, true)
	res, _ := RequestEmailChange(u.ID, newAddr(), AdminActor{UserID: &admin.ID}, time.Now().UTC())
	if _, err := ConfirmEmailChange(HashToken(res.RawToken), AdminActor{}, time.Now().UTC()); err != nil {
		t.Fatalf("active admin: %v", err)
	}
}

// Defence in depth: a current address that became unverified cannot confirm.
func TestEmailChange_ConfirmRequiresVerifiedCurrentAddress(t *testing.T) {
	db := emailChangeTestDB(t)
	u := ecUser(t, db, true)
	res, err := RequestEmailChange(u.ID, newAddr(), AdminActor{}, time.Now().UTC())
	if err != nil {
		t.Fatal(err)
	}
	db.Exec(`UPDATE application_users SET email_verified_at = NULL WHERE id = ?`, u.ID)
	if _, err := ConfirmEmailChange(HashToken(res.RawToken), AdminActor{}, time.Now().UTC()); !errors.Is(err, ErrInvalidToken) {
		t.Fatalf("confirm: %v", err)
	}
	if got := reload(t, db, u.ID); got.Email != u.Email {
		t.Fatal("account changed")
	}
}

// Preview and the pending lookups apply confirm's validity predicate: a demoted requester or an
// unverified current address makes the request invalid (preview) and not pending (admin/settings).
func TestEmailChange_PreviewAndPendingShareConfirmValidity(t *testing.T) {
	db := emailChangeTestDB(t)
	for name, spoil := range map[string]func(admin, user uuid.UUID){
		"demoted requester":  func(a, _ uuid.UUID) { db.Exec(`UPDATE application_users SET is_admin = false WHERE id = ?`, a) },
		"deleted requester":  func(a, _ uuid.UUID) { db.Exec(`DELETE FROM application_users WHERE id = ?`, a) },
		"unverified current": func(_, u uuid.UUID) { db.Exec(`UPDATE application_users SET email_verified_at = NULL WHERE id = ?`, u) },
	} {
		admin := ecAdmin()
		u := ecUser(t, db, true)
		res, err := RequestEmailChange(u.ID, newAddr(), admin, time.Now().UTC())
		if err != nil {
			t.Fatal(err)
		}
		if _, err := PreviewEmailChange(HashToken(res.RawToken), time.Now().UTC()); err != nil {
			t.Fatalf("%s: preview before: %v", name, err)
		}
		spoil(*admin.UserID, u.ID)
		if _, err := PreviewEmailChange(HashToken(res.RawToken), time.Now().UTC()); !errors.Is(err, ErrInvalidToken) {
			t.Fatalf("%s: preview: %v", name, err)
		}
		if p, err := GetPendingEmailChange(u.ID, time.Now().UTC()); err != nil || p != nil {
			t.Fatalf("%s: pending %+v %v", name, p, err)
		}
		var open int64
		db.Model(&models.UserToken{}).Where("user_id = ? AND used_at IS NULL", u.ID).Count(&open)
		if open != 1 {
			t.Fatalf("%s: preview or lookup wrote (%d open)", name, open)
		}
	}
}
