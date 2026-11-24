package main

import (
	"context"
	"errors"
	"os"
	"testing"
	"time"
)

const platformSettingsTestDDL = `
CREATE TABLE platform_settings (
	key character varying(100) PRIMARY KEY,
	paused boolean NOT NULL DEFAULT false,
	pool_slot_cap integer NULL,
	reason character varying(500) NULL,
	updated_at timestamp with time zone NOT NULL DEFAULT now(),
	updated_by character varying(320) NULL
)
`

// TestPostgresPoolStore exercises enrolment, sticky revocation and the waker
// heartbeat against a real Postgres. It skips without
// RUNNER_GATEWAY_TEST_DATABASE_URL, like the other integration tests, and
// creates and drops its tables in that throwaway database.
func TestPostgresPoolStore(t *testing.T) {
	url := os.Getenv("RUNNER_GATEWAY_TEST_DATABASE_URL")
	if url == "" {
		t.Skip("set RUNNER_GATEWAY_TEST_DATABASE_URL to run the pool-node integration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	devices, err := NewPostgresDeviceStore(ctx, url, 0)
	if err != nil {
		t.Fatalf("connect: %v", err)
	}
	defer devices.Close()
	for _, stmt := range []string{"DROP TABLE IF EXISTS runner_devices", "DROP TABLE IF EXISTS platform_settings", runnerDevicesDDL, platformSettingsTestDDL} {
		if _, err := devices.pool.Exec(ctx, stmt); err != nil {
			t.Fatalf("%s: %v", stmt, err)
		}
	}
	defer func() {
		_, _ = devices.pool.Exec(context.Background(), "DROP TABLE IF EXISTS runner_devices, platform_settings")
	}()
	store := NewPostgresPoolStore(devices.pool)

	id, err := store.Enroll(ctx, "rb-pool-it1")
	if err != nil || id == "" {
		t.Fatalf("enroll: %q %v", id, err)
	}
	again, err := store.Enroll(ctx, "rb-pool-it1")
	if err != nil || again != id {
		t.Fatalf("re-enrol must be idempotent: %q %v", again, err)
	}
	device, err := devices.LookupPoolWorker(ctx, "rb-pool-it1")
	if err != nil || device.Revoked() || device.OrganizationID != "" {
		t.Fatalf("lookup: %+v %v", device, err)
	}

	if err := store.Revoke(ctx, "rb-pool-it1"); err != nil {
		t.Fatalf("revoke: %v", err)
	}
	if err := store.Revoke(ctx, "rb-pool-it1"); err != nil {
		t.Fatalf("revoke twice: %v", err)
	}
	if err := store.Revoke(ctx, "rb-pool-missing"); err != nil {
		t.Fatalf("revoke of a missing row: %v", err)
	}
	if device, _ = devices.LookupPoolWorker(ctx, "rb-pool-it1"); !device.Revoked() {
		t.Fatal("row not revoked")
	}
	if _, err := store.Enroll(ctx, "rb-pool-it1"); !errors.Is(err, ErrNodeRevoked) {
		t.Fatalf("revocation must be sticky, got %v", err)
	}

	if _, found, err := store.GetWaker(ctx); err != nil || found {
		t.Fatalf("empty waker: found=%v err=%v", found, err)
	}
	if err := store.PutWaker(ctx, true, wakerDetail{Reason: "ok", Nodes: 1, MaxNodes: 2, Instance: "default"}); err != nil {
		t.Fatalf("put waker: %v", err)
	}
	if err := store.PutWaker(ctx, true, wakerDetail{Reason: "ok2", Capped: true, Instance: "default"}); err != nil {
		t.Fatalf("put waker (upsert): %v", err)
	}
	rec, found, err := store.GetWaker(ctx)
	if err != nil || !found || !rec.Healthy || rec.Detail.Reason != "ok2" || !rec.Detail.Capped {
		t.Fatalf("get waker: %+v found=%v err=%v", rec, found, err)
	}
	if v := poolViewFrom(rec); v.WakerHealthy || !v.Capped {
		t.Errorf("capped heartbeat must not be healthy: %+v", v)
	}
	// The switches code reads the same table and must ignore the row.
	snapshot, err := NewPostgresSwitchStoreFromPool(devices.pool).ReadPlatformSettings(ctx)
	if err != nil || snapshot.ComputePaused() {
		t.Fatalf("switches over a table with a waker row: %+v %v", snapshot, err)
	}
}
