package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// ErrDeviceNotFound is returned when no runner_devices row matches an identity.
var ErrDeviceNotFound = errors.New("gateway: device not registered")

// Device is the part of a runner_devices row the gateway needs. The table is
// owned (migrated) by the Inference API and read and written here by raw SQL
// (schema ruling R1); the gateway never migrates it.
type Device struct {
	ID   string
	Kind string
	// OrganizationID is empty for a CloudPool worker, which serves any
	// organisation's pool jobs, and always set for a desktop runner.
	OrganizationID string
	Name           string
	// PublicKey is the base64 Ed25519 key registered at pairing. Empty for a pool
	// worker, which authenticates with the pool credential instead.
	PublicKey string
	// CapabilitiesJSON is the capability document the runner last advertised.
	CapabilitiesJSON string
	// RevokedAt is the zero time while the device is trusted.
	RevokedAt time.Time
}

// Revoked reports whether the device has been revoked.
func (d Device) Revoked() bool { return !d.RevokedAt.IsZero() }

// DeviceStore is the gateway's view of the device registry. It is an interface
// so the handshake and the revocation sweeper can be tested without a database.
type DeviceStore interface {
	// LookupDevice reads the row a desktop hello names, or ErrDeviceNotFound.
	LookupDevice(ctx context.Context, deviceID string) (Device, error)
	// LookupPoolWorker reads the CloudPool row a pool hello names, or
	// ErrDeviceNotFound. Pool workers are enrolled out of band: a worker with no
	// row is refused rather than handed a synthetic identity.
	LookupPoolWorker(ctx context.Context, workerName string) (Device, error)
	// Touch records that a runner was seen and refreshes the capability document
	// it advertised. It is bookkeeping: a failure is logged and never closes a
	// connection.
	Touch(ctx context.Context, deviceID string, seen time.Time, caps protocol.Capabilities) error
	// RevokedAmong returns the subset of the given runner ids whose row carries a
	// revoked_at. It backs the 60 s revocation re-check.
	RevokedAmong(ctx context.Context, deviceIDs []string) (map[string]struct{}, error)
	// Ping reports whether the registry is reachable.
	Ping(ctx context.Context) error
}

// PostgresDeviceStore reads and writes runner_devices through pgx.
//
// The uuid columns are read as text (id and organization_id are cast) so a row's
// identity travels as a plain string, and an id that is not a uuid is rejected
// in Go by validRunnerID before it reaches Postgres: an unauthenticated caller
// must not be able to turn a junk deviceId into a database error.
type PostgresDeviceStore struct {
	pool *pgxpool.Pool
}

// NewPostgresDeviceStore connects and verifies the connection. maxConns is
// DATABASE_MAX_CONNS (zero = unset; see pool.go for the precedence).
func NewPostgresDeviceStore(ctx context.Context, databaseURL string, maxConns int32) (*PostgresDeviceStore, error) {
	pool, err := newPgxPool(ctx, databaseURL, maxConns, 1, poolDevices)
	if err != nil {
		return nil, err
	}
	store := &PostgresDeviceStore{pool: pool}
	if err := store.Ping(ctx); err != nil {
		pool.Close()
		return nil, err
	}
	return store, nil
}

// Close releases the connection pool.
func (s *PostgresDeviceStore) Close() { s.pool.Close() }

const deviceColumns = `id::text, kind, coalesce(organization_id::text, ''), name,
	coalesce(public_key, ''), coalesce(capabilities_json::text, ''), revoked_at`

// Ping reports whether the database answers.
func (s *PostgresDeviceStore) Ping(ctx context.Context) error {
	if err := s.pool.Ping(ctx); err != nil {
		return fmt.Errorf("ping postgres: %w", err)
	}
	return nil
}

// LookupDevice reads one desktop runner by the id in hello.auth.deviceId, which
// is the runner_devices row id (welcome.runnerId echoes it back).
func (s *PostgresDeviceStore) LookupDevice(ctx context.Context, deviceID string) (Device, error) {
	if !validRunnerID(deviceID) {
		return Device{}, ErrDeviceNotFound
	}
	return s.lookup(ctx, "SELECT "+deviceColumns+" FROM runner_devices WHERE id = $1::uuid", deviceID)
}

// LookupPoolWorker reads one CloudPool worker by the name in
// hello.auth.workerName. Only CloudPool rows are eligible: a desktop device's
// name must never become a way to authenticate as that device.
func (s *PostgresDeviceStore) LookupPoolWorker(ctx context.Context, workerName string) (Device, error) {
	if strings.TrimSpace(workerName) == "" {
		return Device{}, ErrDeviceNotFound
	}
	return s.lookup(ctx,
		"SELECT "+deviceColumns+" FROM runner_devices WHERE kind = $1 AND name = $2 ORDER BY created_at LIMIT 1",
		protocol.KindCloudPool, workerName)
}

func (s *PostgresDeviceStore) lookup(ctx context.Context, query string, args ...any) (Device, error) {
	var (
		d       Device
		revoked *time.Time
	)
	err := s.pool.QueryRow(ctx, query, args...).Scan(
		&d.ID, &d.Kind, &d.OrganizationID, &d.Name, &d.PublicKey, &d.CapabilitiesJSON, &revoked)
	if errors.Is(err, pgx.ErrNoRows) {
		return Device{}, ErrDeviceNotFound
	}
	if err != nil {
		return Device{}, fmt.Errorf("read runner_devices: %w", err)
	}
	if revoked != nil {
		d.RevokedAt = *revoked
	}
	return d, nil
}

// Touch records last_seen_at and the capability document from hello. os, arch
// and runner_version are refreshed from the same document: they are the same
// facts the pairing flow records, and the gateway's copy is the newer one.
func (s *PostgresDeviceStore) Touch(ctx context.Context, deviceID string, seen time.Time, caps protocol.Capabilities) error {
	if !validRunnerID(deviceID) {
		return ErrDeviceNotFound
	}
	document, err := json.Marshal(caps)
	if err != nil {
		return fmt.Errorf("marshal capabilities: %w", err)
	}
	_, err = s.pool.Exec(ctx,
		`UPDATE runner_devices
		    SET last_seen_at = $2, capabilities_json = $3::jsonb, os = $4, arch = $5, runner_version = $6
		  WHERE id = $1::uuid`,
		deviceID, seen, string(document), caps.OS, caps.Arch, caps.AgentVersion)
	if err != nil {
		return fmt.Errorf("update runner_devices: %w", err)
	}
	return nil
}

// RevokedAmong returns the ids whose row is revoked.
func (s *PostgresDeviceStore) RevokedAmong(ctx context.Context, deviceIDs []string) (map[string]struct{}, error) {
	out := map[string]struct{}{}
	var valid []string
	for _, id := range deviceIDs {
		if validRunnerID(id) {
			valid = append(valid, id)
		}
	}
	if len(valid) == 0 {
		return out, nil
	}
	rows, err := s.pool.Query(ctx,
		`SELECT id::text FROM runner_devices WHERE id = ANY($1::uuid[]) AND revoked_at IS NOT NULL`, valid)
	if err != nil {
		return nil, fmt.Errorf("read revoked runner_devices: %w", err)
	}
	defer rows.Close()
	for rows.Next() {
		var id string
		if err := rows.Scan(&id); err != nil {
			return nil, fmt.Errorf("scan revoked runner_devices: %w", err)
		}
		out[id] = struct{}{}
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("read revoked runner_devices: %w", err)
	}
	return out, nil
}

// validRunnerID reports whether an id could name a runner_devices row. A
// non-uuid is treated as "no such device", never as a database error.
func validRunnerID(id string) bool {
	_, err := uuid.Parse(id)
	return err == nil
}
