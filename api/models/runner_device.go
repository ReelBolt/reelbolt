package models

import (
	"time"

	"github.com/google/uuid"
)

// The runner tables are owned (migrated) by the Inference API; this service maps
// them through GORM and never migrates. The stored values below repeat
// ReelBolt.Shared.Data.Models enums verbatim: they are append-only and must never
// be renamed. RunnerEnumDriftGuardTests fails when a constant here stops matching
// the C# enum, which is the only thing that keeps the two in step.
const (
	RunnerKindDesktop   = "Desktop"
	RunnerKindCloudPool = "CloudPool"

	RunnerPairingPending  = "Pending"
	RunnerPairingApproved = "Approved"
	RunnerPairingDenied   = "Denied"
	RunnerPairingExpired  = "Expired"
)

// RunnerDevice maps the runner_devices table (migration AddRunnerDevices, WP D3).
// The Go runner gateway also writes it by raw SQL, so the column set is shared.
type RunnerDevice struct {
	ID             uuid.UUID  `gorm:"type:uuid;primaryKey;column:id"`
	OrganizationID *uuid.UUID `gorm:"type:uuid;column:organization_id"`
	Kind           string     `gorm:"column:kind"`
	Name           string     `gorm:"column:name"`

	// PublicKey is base64 Ed25519, registered at pairing; nil for pool workers.
	PublicKey *string `gorm:"column:public_key"`
	// PublicKeyFingerprint is the hex SHA-256 of the key, unique across the table.
	PublicKeyFingerprint *string `gorm:"column:public_key_fingerprint"`

	// CapabilitiesJSON is what the runner last advertised in hello (jsonb).
	CapabilitiesJSON *string `gorm:"column:capabilities_json"`

	Os            *string `gorm:"column:os"`
	Arch          *string `gorm:"column:arch"`
	RunnerVersion *string `gorm:"column:runner_version"`

	LastSeenAt      *time.Time `gorm:"column:last_seen_at"`
	RevokedAt       *time.Time `gorm:"column:revoked_at"`
	RevokedByUserID *uuid.UUID `gorm:"type:uuid;column:revoked_by_user_id"`

	// CreatedByUserID is who approved the pairing; PairedByUserID who completed it
	// at the machine. Both are the approver for a desktop device.
	CreatedByUserID *uuid.UUID `gorm:"type:uuid;column:created_by_user_id"`
	PairedByUserID  *uuid.UUID `gorm:"type:uuid;column:paired_by_user_id"`

	CreatedAt time.Time `gorm:"column:created_at"`
}

// TableName pins the GORM table for RunnerDevice.
func (RunnerDevice) TableName() string { return "runner_devices" }

// RunnerPairingRequest maps the runner_pairing_requests table (WP E4a). It is the
// unapproved half of pairing: the device code is a bearer secret for this row
// alone, and approving the request is what creates the RunnerDevice.
type RunnerPairingRequest struct {
	ID             uuid.UUID `gorm:"type:uuid;primaryKey;column:id"`
	DeviceCodeHash string    `gorm:"column:device_code_hash"`
	UserCode       string    `gorm:"column:user_code"`

	PublicKey            string `gorm:"column:public_key"`
	PublicKeyFingerprint string `gorm:"column:public_key_fingerprint"`

	Name          string  `gorm:"column:name"`
	Os            *string `gorm:"column:os"`
	Arch          *string `gorm:"column:arch"`
	RunnerVersion *string `gorm:"column:runner_version"`

	RequestIP *string `gorm:"column:request_ip"`

	CreatedAt    time.Time  `gorm:"column:created_at"`
	ExpiresAt    time.Time  `gorm:"column:expires_at"`
	LastPolledAt *time.Time `gorm:"column:last_polled_at"`

	Status string `gorm:"column:status"`

	ApprovedAt             *time.Time `gorm:"column:approved_at"`
	DeniedAt               *time.Time `gorm:"column:denied_at"`
	ApprovedOrganizationID *uuid.UUID `gorm:"type:uuid;column:approved_organization_id"`
	ApprovedByUserID       *uuid.UUID `gorm:"type:uuid;column:approved_by_user_id"`
	ApprovedDeviceID       *uuid.UUID `gorm:"type:uuid;column:approved_device_id"`

	// ConsumedAt marks the single delivery of the approved result to the device.
	ConsumedAt *time.Time `gorm:"column:consumed_at"`
}

// TableName pins the GORM table for RunnerPairingRequest.
func (RunnerPairingRequest) TableName() string { return "runner_pairing_requests" }
