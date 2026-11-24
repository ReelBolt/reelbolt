package main

import (
	"context"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

// These tests exercise enrollment against a THROWAWAY Postgres, created and
// dropped by the caller. The Go CI leg has no database, so they run only when
// WORKER_TEST_DATABASE_URL points at one that may be destroyed:
//
//	docker run --rm -d --name reelbolt-d13-pg -e POSTGRES_PASSWORD=postgres -p 55434:5432 postgres:17-alpine
//	WORKER_TEST_DATABASE_URL='postgres://postgres:postgres@127.0.0.1:55434/postgres?sslmode=disable' //	  go test ./cmd/reelbolt-worker/ -run TestEnroll -v
//
// The DDL is transcribed from ReelBolt.Inference.Api/Migrations/*_AddRunnerDevices.cs:
// the columns, the types and the ck_runner_devices_org_or_pool check constraint
// are exactly as the migration creates them. The foreign keys to
// application_users/organizations are omitted because this database has no such
// tables.
const runnerDevicesDDL = `
CREATE TABLE IF NOT EXISTS runner_devices (
	id uuid PRIMARY KEY,
	organization_id uuid NULL,
	kind character varying(50) NOT NULL,
	name character varying(200) NOT NULL,
	public_key text NULL,
	public_key_fingerprint character varying(128) NULL,
	capabilities_json jsonb NULL,
	os text NULL,
	arch text NULL,
	runner_version text NULL,
	last_seen_at timestamp with time zone NULL,
	revoked_at timestamp with time zone NULL,
	revoked_by_user_id uuid NULL,
	created_by_user_id uuid NULL,
	paired_by_user_id uuid NULL,
	created_at timestamp with time zone NOT NULL DEFAULT now(),
	CONSTRAINT ck_runner_devices_org_or_pool CHECK (kind = 'CloudPool' OR organization_id IS NOT NULL)
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_runner_devices_public_key_fingerprint" ON runner_devices (public_key_fingerprint);
`

// enrollTestPool connects to the throwaway database and resets runner_devices.
// It skips the test when no database is configured, which is what makes these
// tests runnable in CI's CPU-only Go leg.
func enrollTestPool(t *testing.T) *pgxpool.Pool {
	t.Helper()
	url := os.Getenv("WORKER_TEST_DATABASE_URL")
	if url == "" {
		t.Skip("WORKER_TEST_DATABASE_URL is not set: skipping the enrollment integration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	pool, err := pgxpool.New(ctx, url)
	if err != nil {
		t.Fatalf("connect: %v", err)
	}
	t.Cleanup(pool.Close)
	if _, err := pool.Exec(ctx, runnerDevicesDDL); err != nil {
		t.Fatalf("create runner_devices: %v", err)
	}
	if _, err := pool.Exec(ctx, "DELETE FROM runner_devices"); err != nil {
		t.Fatalf("reset runner_devices: %v", err)
	}
	return pool
}

// TestEnrollIsIdempotent is the property cloud-init depends on: the same VM
// booting twice must land on ONE identity, not two rows the gateway resolves by
// row age.
func TestEnrollIsIdempotent(t *testing.T) {
	pool := enrollTestPool(t)
	ctx := context.Background()

	first, err := enrollPoolWorker(ctx, pool, "pool-1", "v1", false, false)
	if err != nil {
		t.Fatalf("first enroll: %v", err)
	}
	if !first.Created || first.ID == "" {
		t.Fatalf("first enroll = %+v, want a created row", first)
	}

	second, err := enrollPoolWorker(ctx, pool, "pool-1", "v2", false, false)
	if err != nil {
		t.Fatalf("second enroll: %v", err)
	}
	if second.Created {
		t.Fatalf("second enroll created a row: %+v", second)
	}
	if second.ID != first.ID {
		t.Fatalf("second enroll produced id %s, want %s", second.ID, first.ID)
	}

	var (
		count   int
		version string
		orgNull bool
	)
	if err := pool.QueryRow(ctx,
		"SELECT count(*), coalesce(max(runner_version), ''), bool_and(organization_id IS NULL) FROM runner_devices WHERE kind = 'CloudPool' AND name = 'pool-1'",
	).Scan(&count, &version, &orgNull); err != nil {
		t.Fatal(err)
	}
	if count != 1 {
		t.Fatalf("runner_devices holds %d rows named pool-1, want 1", count)
	}
	if version != "v2" {
		t.Fatalf("runner_version = %q, want the newest value", version)
	}
	if !orgNull {
		t.Fatal("a CloudPool row must have a null organization_id (ck_runner_devices_org_or_pool)")
	}
}

// TestEnrollLeavesARunnableWorkerUntouched: the command refreshes os/arch and
// version, and must NOT touch last_seen_at — that column is the gateway's
// evidence that a worker is alive, and enrollment writing it would make a
// machine that has never connected look like one that has.
func TestEnrollLeavesLastSeenAlone(t *testing.T) {
	pool := enrollTestPool(t)
	ctx := context.Background()
	if _, err := enrollPoolWorker(ctx, pool, "pool-2", "v1", false, false); err != nil {
		t.Fatal(err)
	}
	seen := time.Now().Add(-time.Hour).UTC().Truncate(time.Second)
	if _, err := pool.Exec(ctx, "UPDATE runner_devices SET last_seen_at = $1 WHERE name = 'pool-2'", seen); err != nil {
		t.Fatal(err)
	}
	if _, err := enrollPoolWorker(ctx, pool, "pool-2", "v2", false, false); err != nil {
		t.Fatal(err)
	}
	var got *time.Time
	if err := pool.QueryRow(ctx, "SELECT last_seen_at FROM runner_devices WHERE name = 'pool-2'").Scan(&got); err != nil {
		t.Fatal(err)
	}
	if got == nil || !got.UTC().Truncate(time.Second).Equal(seen) {
		t.Fatalf("last_seen_at = %v, want it left at %v", got, seen)
	}
}

// TestEnrollRefusesARevokedWorker is the security property, not a convenience:
// revocation must survive a reboot, or a leaked pool credential would be an
// identity-minting capability.
func TestEnrollRefusesARevokedWorker(t *testing.T) {
	pool := enrollTestPool(t)
	ctx := context.Background()
	out, err := enrollPoolWorker(ctx, pool, "pool-3", "v1", false, false)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, "UPDATE runner_devices SET revoked_at = now() WHERE id = $1::uuid", out.ID); err != nil {
		t.Fatal(err)
	}

	again, err := enrollPoolWorker(ctx, pool, "pool-3", "v2", false, false)
	if err != nil {
		t.Fatalf("enrolling a revoked worker returned an error rather than reporting it: %v", err)
	}
	if !again.Revoked || again.Reinstated {
		t.Fatalf("outcome = %+v, want revoked and not reinstated", again)
	}
	var revoked *time.Time
	if err := pool.QueryRow(ctx, "SELECT revoked_at FROM runner_devices WHERE id = $1::uuid", out.ID).Scan(&revoked); err != nil {
		t.Fatal(err)
	}
	if revoked == nil {
		t.Fatal("enrollment cleared revoked_at on its own")
	}

	// The explicit operator action does clear it.
	back, err := enrollPoolWorker(ctx, pool, "pool-3", "v2", true, false)
	if err != nil {
		t.Fatalf("reinstate: %v", err)
	}
	if !back.Reinstated || back.Created {
		t.Fatalf("reinstate outcome = %+v", back)
	}
	if err := pool.QueryRow(ctx, "SELECT revoked_at FROM runner_devices WHERE id = $1::uuid", out.ID).Scan(&revoked); err != nil {
		t.Fatal(err)
	}
	if revoked != nil {
		t.Fatalf("revoked_at = %v after -reinstate, want null", revoked)
	}
}

// TestEnrollRefusesAnAmbiguousName: the gateway's lookup is ORDER BY created_at
// LIMIT 1, so two rows with one name is a worker whose identity depends on row
// age. Enrollment refuses rather than adding a third.
func TestEnrollRefusesAnAmbiguousName(t *testing.T) {
	pool := enrollTestPool(t)
	ctx := context.Background()
	if _, err := enrollPoolWorker(ctx, pool, "pool-4", "v1", false, false); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx,
		"INSERT INTO runner_devices (id, kind, name, created_at) VALUES (gen_random_uuid(), 'CloudPool', 'pool-4', now())"); err != nil {
		t.Fatal(err)
	}
	_, err := enrollPoolWorker(ctx, pool, "pool-4", "v2", false, false)
	if err == nil {
		t.Fatal("enrollment accepted an ambiguous name")
	}
	if !strings.Contains(err.Error(), "CloudPool rows are named") {
		t.Fatalf("the refusal does not explain the ambiguity: %v", err)
	}
}

// TestEnrollDryRunWritesNothing is what an operator runs first.
func TestEnrollDryRunWritesNothing(t *testing.T) {
	pool := enrollTestPool(t)
	ctx := context.Background()
	out, err := enrollPoolWorker(ctx, pool, "pool-5", "v1", false, true)
	if err != nil {
		t.Fatal(err)
	}
	if !out.Created || out.ID == "" {
		t.Fatalf("dry run outcome = %+v, want a would-create with an id", out)
	}
	var count int
	if err := pool.QueryRow(ctx, "SELECT count(*) FROM runner_devices").Scan(&count); err != nil {
		t.Fatal(err)
	}
	if count != 0 {
		t.Fatalf("dry run wrote %d rows", count)
	}
}

// TestEnrollReportsAMissingSchema: enrollment against a database whose
// migrations have not run says so, instead of failing with a Postgres error that
// does not name the deployment order.
func TestEnsureRunnerSchemaReportsAMissingTable(t *testing.T) {
	pool := enrollTestPool(t)
	ctx := context.Background()
	if _, err := pool.Exec(ctx, "ALTER TABLE runner_devices RENAME TO runner_devices_tmp"); err != nil {
		t.Fatal(err)
	}
	defer func() {
		if _, err := pool.Exec(ctx, "ALTER TABLE runner_devices_tmp RENAME TO runner_devices"); err != nil {
			t.Fatalf("restore: %v", err)
		}
	}()
	err := ensureRunnerSchema(ctx, pool)
	if err == nil || !strings.Contains(err.Error(), "Inference API migrates") {
		t.Fatalf("ensureRunnerSchema = %v, want a refusal naming the migration order", err)
	}
}

func TestValidateWorkerName(t *testing.T) {
	if err := validateWorkerName(""); err == nil {
		t.Fatal("an empty name was accepted")
	}
	if err := validateWorkerName(strings.Repeat("x", 201)); err == nil {
		t.Fatal("a name longer than the column was accepted")
	}
	if err := validateWorkerName("pool-eu-west-1a-01"); err != nil {
		t.Fatalf("a legitimate name was refused: %v", err)
	}
}
