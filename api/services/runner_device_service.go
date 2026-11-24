package services

import (
	"context"
	"errors"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
)

// Sentinels the device-pairing API maps onto HTTP status codes.
var (
	// ErrPairingNotPending means the request was approved, denied or swept to
	// expired between the handler's read and its write.
	ErrPairingNotPending = errors.New("pairing request is no longer pending")
	// ErrKeyAlreadyPaired means the device's Ed25519 key is already registered to a
	// live device. runner_devices.public_key_fingerprint is unique, so one key can
	// pair exactly once until the old device is revoked.
	ErrKeyAlreadyPaired = errors.New("public key is already paired")
)

// RunnerStore is the persistence the device-pairing API needs. It is an interface
// so every branch (expiry, slow_down, refusal, cross-org 404) is testable without
// a database; GormRunnerStore is the production implementation.
type RunnerStore interface {
	// SweepExpiredPairingRequests settles every pending request whose window has
	// closed. It runs before a new code is issued, which is what keeps the short
	// user code unique among LIVE requests (the partial unique index only covers
	// status = 'Pending').
	SweepExpiredPairingRequests(ctx context.Context, now time.Time) error

	// CreatePairingRequest inserts req. A user-code collision returns a unique-index
	// violation the caller retries with a fresh code; see IsUniqueViolation.
	CreatePairingRequest(ctx context.Context, req *models.RunnerPairingRequest) error
	// FindPairingRequestByDeviceCodeHash returns (nil, nil) when no request has that
	// hash, so the caller can tell "unknown" from "database error".
	FindPairingRequestByDeviceCodeHash(ctx context.Context, hash string) (*models.RunnerPairingRequest, error)
	// FindPairingRequestByUserCode returns the live (pending) request for a code.
	FindPairingRequestByUserCode(ctx context.Context, userCode string) (*models.RunnerPairingRequest, error)

	TouchPairingPoll(ctx context.Context, id uuid.UUID, now time.Time) error
	// MarkPairingDenied and MarkPairingConsumed are conditional: they report false
	// when the request had already left Pending (approved) / was already delivered.
	MarkPairingDenied(ctx context.Context, id uuid.UUID, now time.Time) (bool, error)
	MarkPairingConsumed(ctx context.Context, id uuid.UUID, now time.Time) (bool, error)
	// ApprovePairing creates the device and settles the request in one transaction,
	// re-checking that the request is still pending inside it.
	ApprovePairing(ctx context.Context, req *models.RunnerPairingRequest, device *models.RunnerDevice, now time.Time) error

	// CountPairStartsFromIP backs the per-IP pairing rate limit. Like the other
	// counters in middleware, it takes no context: it is called from a limiter that
	// has none, and a failed count denies rather than failing the request.
	CountPairStartsFromIP(ip string, since time.Time) (int, error)

	ListActiveDevices(ctx context.Context, orgID uuid.UUID) ([]models.RunnerDevice, error)
	FindDeviceByID(ctx context.Context, id uuid.UUID) (*models.RunnerDevice, error)
	FindDeviceByFingerprint(ctx context.Context, fingerprint string) (*models.RunnerDevice, error)
	// RevokeDevice reports whether this call is the one that revoked the device.
	RevokeDevice(ctx context.Context, id, byUser uuid.UUID, now time.Time) (bool, error)
	// ListOrgOwnerEmails is the recipient list of the anti-phishing notification.
	ListOrgOwnerEmails(ctx context.Context, orgID uuid.UUID) ([]string, error)
}

// GormRunnerStore is the production RunnerStore over database.DB.
type GormRunnerStore struct{}

// IsUniqueViolation reports whether err is a PostgreSQL unique-index violation
// (SQLSTATE 23505). The pairing flow leans on two unique indexes — the partial one
// on a live user code and the one on a device key fingerprint — and must tell those
// apart from a genuine failure.
func IsUniqueViolation(err error) bool {
	var pgErr *pgconn.PgError
	return errors.As(err, &pgErr) && pgErr.Code == "23505"
}

func (GormRunnerStore) SweepExpiredPairingRequests(ctx context.Context, now time.Time) error {
	return database.DB.WithContext(ctx).Model(&models.RunnerPairingRequest{}).
		Where("status = ? AND expires_at <= ?", models.RunnerPairingPending, now).
		Update("status", models.RunnerPairingExpired).Error
}

func (GormRunnerStore) CreatePairingRequest(ctx context.Context, req *models.RunnerPairingRequest) error {
	return database.DB.WithContext(ctx).Create(req).Error
}

func (GormRunnerStore) FindPairingRequestByDeviceCodeHash(ctx context.Context, hash string) (*models.RunnerPairingRequest, error) {
	var req models.RunnerPairingRequest
	err := database.DB.WithContext(ctx).
		Where("device_code_hash = ?", hash).Take(&req).Error
	if errors.Is(err, gorm.ErrRecordNotFound) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &req, nil
}

func (GormRunnerStore) FindPairingRequestByUserCode(ctx context.Context, userCode string) (*models.RunnerPairingRequest, error) {
	var req models.RunnerPairingRequest
	err := database.DB.WithContext(ctx).
		Where("user_code = ? AND status = ?", userCode, models.RunnerPairingPending).
		Take(&req).Error
	if errors.Is(err, gorm.ErrRecordNotFound) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &req, nil
}

func (GormRunnerStore) TouchPairingPoll(ctx context.Context, id uuid.UUID, now time.Time) error {
	return database.DB.WithContext(ctx).Model(&models.RunnerPairingRequest{}).
		Where("id = ?", id).Update("last_polled_at", now).Error
}

func (GormRunnerStore) MarkPairingDenied(ctx context.Context, id uuid.UUID, now time.Time) (bool, error) {
	res := database.DB.WithContext(ctx).Model(&models.RunnerPairingRequest{}).
		Where("id = ? AND status = ?", id, models.RunnerPairingPending).
		Updates(map[string]any{"status": models.RunnerPairingDenied, "denied_at": now})
	if res.Error != nil {
		return false, res.Error
	}
	return res.RowsAffected > 0, nil
}

func (GormRunnerStore) MarkPairingConsumed(ctx context.Context, id uuid.UUID, now time.Time) (bool, error) {
	res := database.DB.WithContext(ctx).Model(&models.RunnerPairingRequest{}).
		Where("id = ? AND status = ? AND consumed_at IS NULL", id, models.RunnerPairingApproved).
		Update("consumed_at", now)
	if res.Error != nil {
		return false, res.Error
	}
	return res.RowsAffected > 0, nil
}

func (GormRunnerStore) ApprovePairing(
	ctx context.Context, req *models.RunnerPairingRequest, device *models.RunnerDevice, now time.Time) error {
	err := database.DB.WithContext(ctx).Transaction(func(tx *gorm.DB) error {
		if err := tx.Create(device).Error; err != nil {
			return err
		}
		res := tx.Model(&models.RunnerPairingRequest{}).
			Where("id = ? AND status = ?", req.ID, models.RunnerPairingPending).
			Updates(map[string]any{
				"status":                   models.RunnerPairingApproved,
				"approved_at":              now,
				"approved_by_user_id":      device.PairedByUserID,
				"approved_organization_id": device.OrganizationID,
				"approved_device_id":       device.ID,
			})
		if res.Error != nil {
			return res.Error
		}
		if res.RowsAffected == 0 {
			// Someone denied, consumed or swept it first: roll the device insert back.
			return ErrPairingNotPending
		}
		return nil
	})
	if err != nil {
		if IsUniqueViolation(err) {
			return ErrKeyAlreadyPaired
		}
		return err
	}
	return nil
}

func (GormRunnerStore) CountPairStartsFromIP(ip string, since time.Time) (int, error) {
	var n int64
	err := database.DB.Model(&models.RunnerPairingRequest{}).
		Where("request_ip = ? AND created_at >= ?", ip, since).Count(&n).Error
	return int(n), err
}

func (GormRunnerStore) ListActiveDevices(ctx context.Context, orgID uuid.UUID) ([]models.RunnerDevice, error) {
	var devices []models.RunnerDevice
	// Desktop is implied by the organization filter (a pool worker has no org), and
	// stated anyway so the intent survives a schema change.
	err := database.DB.WithContext(ctx).
		Where("organization_id = ? AND kind = ? AND revoked_at IS NULL", orgID, models.RunnerKindDesktop).
		Order("created_at DESC").
		Find(&devices).Error
	return devices, err
}

func (GormRunnerStore) FindDeviceByID(ctx context.Context, id uuid.UUID) (*models.RunnerDevice, error) {
	var device models.RunnerDevice
	err := database.DB.WithContext(ctx).Where("id = ?", id).Take(&device).Error
	if errors.Is(err, gorm.ErrRecordNotFound) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &device, nil
}

func (GormRunnerStore) FindDeviceByFingerprint(ctx context.Context, fingerprint string) (*models.RunnerDevice, error) {
	var device models.RunnerDevice
	err := database.DB.WithContext(ctx).
		Where("public_key_fingerprint = ? AND revoked_at IS NULL", fingerprint).
		Take(&device).Error
	if errors.Is(err, gorm.ErrRecordNotFound) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &device, nil
}

func (GormRunnerStore) RevokeDevice(ctx context.Context, id, byUser uuid.UUID, now time.Time) (bool, error) {
	res := database.DB.WithContext(ctx).Model(&models.RunnerDevice{}).
		Where("id = ? AND revoked_at IS NULL", id).
		Updates(map[string]any{"revoked_at": now, "revoked_by_user_id": byUser})
	if res.Error != nil {
		return false, res.Error
	}
	return res.RowsAffected > 0, nil
}

func (GormRunnerStore) ListOrgOwnerEmails(ctx context.Context, orgID uuid.UUID) ([]string, error) {
	var emails []string
	err := database.DB.WithContext(ctx).
		Table("organization_memberships AS m").
		Joins("JOIN application_users u ON u.id = m.user_id").
		Where("m.organization_id = ? AND m.role = ? AND u.is_service_account = false", orgID, models.OrgRoleOwner).
		Pluck("u.email", &emails).Error
	return emails, err
}
