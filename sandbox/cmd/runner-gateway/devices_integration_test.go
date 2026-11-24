package main

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"errors"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// runnerDevicesDDL is the part of the D3 migration
// (ReelBolt.Inference.Api/Migrations/*_AddRunnerDevices.cs) the gateway touches,
// including the check constraint that makes organization_id nullable only for a
// CloudPool row. It is written out here rather than run as a migration so this
// test needs nothing but an empty database.
const runnerDevicesDDL = `
CREATE TABLE runner_devices (
	id uuid PRIMARY KEY,
	organization_id uuid NULL,
	kind character varying(50) NOT NULL,
	name character varying(200) NOT NULL,
	public_key text NULL,
	capabilities_json jsonb NULL,
	os text NULL,
	arch text NULL,
	runner_version text NULL,
	last_seen_at timestamp with time zone NULL,
	revoked_at timestamp with time zone NULL,
	created_at timestamp with time zone NOT NULL DEFAULT now(),
	CONSTRAINT ck_runner_devices_org_or_pool CHECK (kind = 'CloudPool' OR organization_id IS NOT NULL)
)
`

// TestPostgresDeviceStore exercises the gateway's raw SQL against a real
// Postgres. The Go CI leg has no database, so it runs only when
// RUNNER_GATEWAY_TEST_DATABASE_URL points at a throwaway one:
//
//	docker run --rm -d --name reelbolt-gw-test -e POSTGRES_PASSWORD=postgres -p 55432:5432 postgres:17-alpine
//	RUNNER_GATEWAY_TEST_DATABASE_URL='postgres://postgres:postgres@127.0.0.1:55432/postgres?sslmode=disable' //	  go test ./cmd/runner-gateway/ -run TestPostgresDeviceStore -v
//
// The table is created and dropped by this test, in that database only.
func TestPostgresDeviceStore(t *testing.T) {
	url := os.Getenv("RUNNER_GATEWAY_TEST_DATABASE_URL")
	if url == "" {
		t.Skip("set RUNNER_GATEWAY_TEST_DATABASE_URL to run the device-registry integration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	store, err := NewPostgresDeviceStore(ctx, url, 0)
	if err != nil {
		t.Fatalf("connect: %v", err)
	}
	// Registered before the drop below so the drop runs first: t.Cleanup is
	// last-in-first-out, and the pool must still be open when it runs.
	t.Cleanup(store.Close)
	if err := store.Ping(ctx); err != nil {
		t.Fatalf("ping: %v", err)
	}

	if _, err := store.pool.Exec(ctx, "DROP TABLE IF EXISTS runner_devices"); err != nil {
		t.Fatalf("drop: %v", err)
	}
	if _, err := store.pool.Exec(ctx, runnerDevicesDDL); err != nil {
		t.Fatalf("create: %v", err)
	}
	t.Cleanup(func() {
		if _, err := store.pool.Exec(context.Background(), "DROP TABLE IF EXISTS runner_devices"); err != nil {
			t.Logf("drop runner_devices: %v", err)
		}
	})

	const (
		desktopID = "c3a1f0de-2b4c-4d8e-9a10-6e5f7b8c9d01"
		revokedID = "c3a1f0de-2b4c-4d8e-9a10-6e5f7b8c9d02"
		poolID    = "c3a1f0de-2b4c-4d8e-9a10-6e5f7b8c9d03"
		orgID     = "5e2b1c90-7a3d-4f68-8b21-0c9d4e6f1a22"
	)
	publicKey, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("generate key: %v", err)
	}
	pub := base64.StdEncoding.EncodeToString(publicKey)
	revokedAt := time.Date(2026, 10, 5, 9, 0, 0, 0, time.UTC)

	insert := `INSERT INTO runner_devices (id, organization_id, kind, name, public_key, revoked_at)
	           VALUES ($1::uuid, $2, $3, $4, $5, $6)`
	if _, err := store.pool.Exec(ctx, insert, desktopID, orgID, protocol.KindDesktop, "study-mac-mini", pub, nil); err != nil {
		t.Fatalf("insert desktop: %v", err)
	}
	if _, err := store.pool.Exec(ctx, insert, revokedID, orgID, protocol.KindDesktop, "old-laptop", pub, revokedAt); err != nil {
		t.Fatalf("insert revoked: %v", err)
	}
	// organization_id is null and allowed: the check constraint only exempts CloudPool.
	if _, err := store.pool.Exec(ctx, insert, poolID, nil, protocol.KindCloudPool, "pool-worker-fixture-01", nil, nil); err != nil {
		t.Fatalf("insert pool worker: %v", err)
	}

	t.Run("LookupDevice", func(t *testing.T) {
		device, err := store.LookupDevice(ctx, desktopID)
		if err != nil {
			t.Fatalf("LookupDevice: %v", err)
		}
		if device.ID != desktopID || device.Kind != protocol.KindDesktop {
			t.Errorf("device = %+v", device)
		}
		if device.OrganizationID != orgID {
			t.Errorf("organizationID = %q, want %q", device.OrganizationID, orgID)
		}
		if device.PublicKey != pub {
			t.Errorf("publicKey = %q, want %q", device.PublicKey, pub)
		}
		if device.Revoked() {
			t.Error("a live device reports itself revoked")
		}
		if device.Name != "study-mac-mini" {
			t.Errorf("name = %q", device.Name)
		}
	})

	t.Run("LookupDevice missing and malformed", func(t *testing.T) {
		if _, err := store.LookupDevice(ctx, "11111111-2222-3333-4444-555555555555"); !errors.Is(err, ErrDeviceNotFound) {
			t.Errorf("unknown id error = %v, want ErrDeviceNotFound", err)
		}
		// A junk id is an unknown device, never a database error an
		// unauthenticated caller could provoke.
		if _, err := store.LookupDevice(ctx, "not-a-uuid"); !errors.Is(err, ErrDeviceNotFound) {
			t.Errorf("malformed id error = %v, want ErrDeviceNotFound", err)
		}
	})

	t.Run("LookupDevice reports revocation", func(t *testing.T) {
		device, err := store.LookupDevice(ctx, revokedID)
		if err != nil {
			t.Fatalf("LookupDevice: %v", err)
		}
		if !device.Revoked() || !device.RevokedAt.Equal(revokedAt) {
			t.Errorf("revokedAt = %v (revoked=%v), want %v", device.RevokedAt, device.Revoked(), revokedAt)
		}
	})

	t.Run("LookupPoolWorker", func(t *testing.T) {
		worker, err := store.LookupPoolWorker(ctx, "pool-worker-fixture-01")
		if err != nil {
			t.Fatalf("LookupPoolWorker: %v", err)
		}
		if worker.Kind != protocol.KindCloudPool || worker.OrganizationID != "" {
			t.Errorf("worker = %+v", worker)
		}
		if _, err := store.LookupPoolWorker(ctx, "nobody"); !errors.Is(err, ErrDeviceNotFound) {
			t.Errorf("unknown worker error = %v", err)
		}
		// A desktop device's name must not be a way to authenticate as it.
		if _, err := store.LookupPoolWorker(ctx, "study-mac-mini"); !errors.Is(err, ErrDeviceNotFound) {
			t.Errorf("a desktop name authenticated as a pool worker: %v", err)
		}
	})

	t.Run("Touch writes last_seen and capabilities", func(t *testing.T) {
		digest := "sha256:" + strings.Repeat("a", 64)
		caps := protocol.Capabilities{
			ProtocolVersion: protocol.ProtocolVersion, AgentVersion: "0.1.0",
			OS: "linux", Arch: "amd64", Slots: 2, Container: protocol.ContainerDocker,
			RuntimeImageDigest: &digest, FfmpegVersion: "7.1", FontsetVersion: "1",
			JobTypes: protocol.JobTypes(), MaxOutputBytes: 1 << 32,
		}
		seen := time.Date(2026, 10, 8, 7, 30, 0, 0, time.UTC)
		if err := store.Touch(ctx, desktopID, seen, caps); err != nil {
			t.Fatalf("Touch: %v", err)
		}
		var (
			lastSeen time.Time
			stored   string
			os, arch string
			version  string
		)
		err := store.pool.QueryRow(ctx,
			`SELECT last_seen_at, capabilities_json::text, os, arch, runner_version
			   FROM runner_devices WHERE id = $1::uuid`, desktopID).
			Scan(&lastSeen, &stored, &os, &arch, &version)
		if err != nil {
			t.Fatalf("read back: %v", err)
		}
		if !lastSeen.Equal(seen) {
			t.Errorf("last_seen_at = %v, want %v", lastSeen, seen)
		}
		var roundTripped protocol.Capabilities
		if err := json.Unmarshal([]byte(stored), &roundTripped); err != nil {
			t.Fatalf("capabilities_json is not valid JSON: %v (%s)", err, stored)
		}
		if roundTripped.Slots != 2 || !roundTripped.Supports(protocol.JobVideoCompile) {
			t.Errorf("stored capabilities = %+v", roundTripped)
		}
		if os != "linux" || arch != "amd64" || version != "0.1.0" {
			t.Errorf("os/arch/runner_version = %q/%q/%q", os, arch, version)
		}
	})

	t.Run("RevokedAmong", func(t *testing.T) {
		revoked, err := store.RevokedAmong(ctx, []string{desktopID, revokedID, "11111111-2222-3333-4444-555555555555", "not-a-uuid"})
		if err != nil {
			t.Fatalf("RevokedAmong: %v", err)
		}
		if len(revoked) != 1 {
			t.Fatalf("revoked = %v, want exactly the revoked device", revoked)
		}
		if _, ok := revoked[revokedID]; !ok {
			t.Errorf("revoked = %v, want %s", revoked, revokedID)
		}
		if empty, err := store.RevokedAmong(ctx, nil); err != nil || len(empty) != 0 {
			t.Errorf("RevokedAmong(nil) = %v, %v", empty, err)
		}
	})
}
