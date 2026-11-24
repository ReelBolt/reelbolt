package internalmail

// Integration tests for the C2 upsert against a real PostgreSQL. Skipped unless
// MAIL_RATE_TEST_DATABASE_URL is set, e.g.
//
//	docker run --rm -d --name mailrate-pg --tmpfs /var/lib/postgresql/data -e POSTGRES_PASSWORD=postgres -p 127.0.0.1:55433:5432 postgres:16-alpine
//	MAIL_RATE_TEST_DATABASE_URL='postgres://postgres:postgres@127.0.0.1:55433/postgres?sslmode=disable' \
//	  go test ./handlers/internalmail/ -run Integration
//
// The DDL below mirrors the Inference API's AddMailRateCounters migration (plan contract C2); the Go
// API itself never migrates the table.

import (
	"context"
	"fmt"
	"net/url"
	"os"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"gorm.io/driver/postgres"
	"gorm.io/gorm"
	"gorm.io/gorm/logger"
)

const mailRateCountersDDL = `
CREATE TABLE IF NOT EXISTS mail_rate_counters (
    bucket text NOT NULL,
    window_start timestamp with time zone NOT NULL,
    count integer NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    PRIMARY KEY (bucket, window_start)
);`

func mailRateDSN(t *testing.T) string {
	t.Helper()
	dsn := os.Getenv("MAIL_RATE_TEST_DATABASE_URL")
	if dsn == "" {
		t.Skip("MAIL_RATE_TEST_DATABASE_URL is not set; skipping the PostgreSQL integration test")
	}
	return dsn
}

func openDB(t *testing.T, dsn string) *gorm.DB {
	t.Helper()
	db, err := gorm.Open(postgres.Open(dsn), &gorm.Config{Logger: logger.Default.LogMode(logger.Silent)})
	if err != nil {
		t.Fatalf("connect: %v", err)
	}
	t.Cleanup(func() {
		if sqlDB, err := db.DB(); err == nil {
			_ = sqlDB.Close()
		}
	})
	return db
}

// uniqueHour returns a clock in an hour no other run uses, since the bucket names are fixed and the
// database may be shared.
func uniqueHour() time.Time {
	return time.Unix(0, 0).UTC().Add(time.Duration(time.Now().UnixNano()%(1<<20)) * 24 * time.Hour).Add(10 * time.Hour)
}

func TestIntegration_SQLCounterStoreSharesCountsAcrossLimiters(t *testing.T) {
	db := openDB(t, mailRateDSN(t))
	if err := db.Exec(mailRateCountersDDL).Error; err != nil {
		t.Fatalf("apply schema: %v", err)
	}
	store := sqlCounterStore{db: func() *gorm.DB { return db }}
	now := uniqueHour()
	clock := func() time.Time { return now }
	limits := defaultLimits()
	limits.domainPerHour = 1000

	for i := 0; i < 3; i++ {
		if !newConfirmLimiter(clock, store, limits).allow(context.Background(), "v.ictim+"+fmt.Sprint(i)+"@googlemail.com") {
			t.Fatalf("send %d refused", i+1)
		}
	}
	if newConfirmLimiter(clock, store, limits).allow(context.Background(), "victim@gmail.com") {
		t.Fatal("a fourth mail to the same inbox was admitted across limiter instances")
	}

	var count int
	if err := db.Raw(`SELECT count FROM mail_rate_counters WHERE bucket = ? AND window_start = ?`,
		"relay:rcpt:"+sha256Hex("victim@gmail.com"), windowStart(now, recipientWindow)).Scan(&count).Error; err != nil {
		t.Fatalf("read back: %v", err)
	}
	if count != 3 {
		t.Fatalf("stored count %d, want 3 (three admitted; the refused attempt increments nothing)", count)
	}
}

// C5: concurrent decisions on one bucket, each through its own limiter (separate replicas), admit
// exactly the cap: the advisory locks serialise read-decide-increment, so none overshoots.
func TestIntegration_ConcurrentDecisionsOnOneBucketAdmitExactlyTheCap(t *testing.T) {
	db := openDB(t, mailRateDSN(t))
	if err := db.Exec(mailRateCountersDDL).Error; err != nil {
		t.Fatalf("apply schema: %v", err)
	}
	store := sqlCounterStore{db: func() *gorm.DB { return db }}
	now := uniqueHour()
	clock := func() time.Time { return now }
	mailbox := fmt.Sprintf("victim%d@gmail.com", now.Unix())

	const workers = 24
	run := func(addr func(i int) string, limits relayLimits) int {
		var wg sync.WaitGroup
		var admitted atomic.Int32
		start := make(chan struct{})
		for i := 0; i < workers; i++ {
			wg.Add(1)
			go func(i int) {
				defer wg.Done()
				<-start
				if newConfirmLimiter(clock, store, limits).allow(context.Background(), addr(i)) {
					admitted.Add(1)
				}
			}(i)
		}
		close(start)
		wg.Wait()
		return int(admitted.Load())
	}

	// One mailbox, many replicas at once: exactly 3.
	limits := defaultLimits()
	limits.globalPerHour = 1000
	if got := run(func(int) string { return mailbox }, limits); got != 3 {
		t.Fatalf("admitted %d concurrent mails to one mailbox, want 3", got)
	}
	var count int
	if err := db.Raw(`SELECT count FROM mail_rate_counters WHERE bucket = ? AND window_start = ?`,
		"relay:rcpt:"+sha256Hex(CanonicalMailbox(mailbox)), windowStart(now, recipientWindow)).Scan(&count).Error; err != nil {
		t.Fatalf("read back: %v", err)
	}
	if count != 3 {
		t.Fatalf("stored rcpt count %d, want 3", count)
	}

	// The shared global bucket under distinct mailboxes (overlapping lock sets, no deadlock): exactly
	// the cap, in a fresh hour.
	now = now.Add(3 * time.Hour)
	limits.globalPerHour = 5
	if got := run(func(i int) string { return fmt.Sprintf("u%d-%d@d%d.example", now.Unix(), i, i) }, limits); got != 5 {
		t.Fatalf("admitted %d concurrent mails under a global cap of 5", got)
	}
}

// Fail closed: with the table missing (a fresh schema on the search path), the send is refused.
func TestIntegration_MissingTableRefuses(t *testing.T) {
	dsn := mailRateDSN(t)
	admin := openDB(t, dsn)
	schema := fmt.Sprintf("mailrate_empty_%d", time.Now().UnixNano())
	if err := admin.Exec("CREATE SCHEMA " + schema).Error; err != nil {
		t.Fatalf("create schema: %v", err)
	}
	t.Cleanup(func() { admin.Exec("DROP SCHEMA " + schema + " CASCADE") })

	u, err := url.Parse(dsn)
	if err != nil {
		t.Fatalf("parse dsn: %v", err)
	}
	q := u.Query()
	q.Set("search_path", schema)
	u.RawQuery = q.Encode()
	db := openDB(t, u.String())

	l := newConfirmLimiter(time.Now, sqlCounterStore{db: func() *gorm.DB { return db }}, defaultLimits())
	if l.allow(context.Background(), "subscriber@example.com") {
		t.Fatal("a missing mail_rate_counters table admitted the send")
	}
}
