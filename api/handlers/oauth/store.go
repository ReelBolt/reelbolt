package oauth

import (
	"context"
	"errors"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
)

// OAuthStore is the authorization server's persistence boundary
// (docs/design/mcp-oauth.md §5). It exists as an interface so the whole flow —
// registration, authorize, token, revoke — can be exercised in tests without a
// PostgreSQL instance, which is the same shape the rest of this service uses for
// its collaborators.
//
// The Go API never migrates: these tables are created by the Inference API's EF
// migration (`AddMcpOAuth`), and the column names here are an interface with
// `ReelBolt.Shared/Data/Models/McpOAuth.cs`.
type OAuthStore interface {
	// CreateClient persists a newly registered client.
	CreateClient(ctx context.Context, client *models.OAuthClient) error
	// FindClientByClientID returns (nil, nil) for an unknown client id — never an
	// error — so a caller cannot accidentally turn "not found" into a 500 that an
	// attacker can tell apart from a wrong secret.
	FindClientByClientID(ctx context.Context, clientID string) (*models.OAuthClient, error)
	// TouchClientUsed stamps last_used_at. Best-effort: a failure here must never
	// fail the token request.
	TouchClientUsed(ctx context.Context, clientID string, at time.Time) error

	// CreateCode persists an issued authorization code (only its hash).
	CreateCode(ctx context.Context, code *models.OAuthAuthorizationCode) error
	// FindCodeByHash returns (nil, nil) for an unknown code hash.
	FindCodeByHash(ctx context.Context, codeHash string) (*models.OAuthAuthorizationCode, error)
	// ConsumeCode marks the row consumed and reports whether THIS call did it. It
	// is a conditional UPDATE, so two concurrent redemptions of the same code
	// cannot both succeed.
	ConsumeCode(ctx context.Context, id uuid.UUID, at time.Time) (bool, error)

	// CreateRefreshToken persists a newly issued refresh token (only its hash).
	CreateRefreshToken(ctx context.Context, token *models.OAuthRefreshToken) error
	// FindRefreshTokenByHash returns (nil, nil) for an unknown token hash.
	FindRefreshTokenByHash(ctx context.Context, tokenHash string) (*models.OAuthRefreshToken, error)
	// RotateRefreshToken revokes the token a rotation replaced. It is conditional
	// on the row not being revoked already, so a double rotation reports false
	// rather than silently overwriting the first one.
	RotateRefreshToken(ctx context.Context, id uuid.UUID, at time.Time) (bool, error)
	// RevokeGrant revokes every live refresh token for one (client, user) grant.
	// This is what a reused rotated token triggers: the whole chain dies, not just
	// the token that was presented.
	RevokeGrant(ctx context.Context, clientID string, userID uuid.UUID, at time.Time) error
}

// GormOAuthStore is the production OAuthStore over database.DB.
type GormOAuthStore struct{}

func (GormOAuthStore) CreateClient(ctx context.Context, client *models.OAuthClient) error {
	return database.DB.WithContext(ctx).Create(client).Error
}

func (GormOAuthStore) FindClientByClientID(ctx context.Context, clientID string) (*models.OAuthClient, error) {
	var row models.OAuthClient
	err := database.DB.WithContext(ctx).Where("client_id = ?", clientID).First(&row).Error
	if errors.Is(err, gorm.ErrRecordNotFound) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &row, nil
}

func (GormOAuthStore) TouchClientUsed(ctx context.Context, clientID string, at time.Time) error {
	return database.DB.WithContext(ctx).
		Model(&models.OAuthClient{}).
		Where("client_id = ?", clientID).
		Update("last_used_at", at).Error
}

func (GormOAuthStore) CreateCode(ctx context.Context, code *models.OAuthAuthorizationCode) error {
	return database.DB.WithContext(ctx).Create(code).Error
}

func (GormOAuthStore) FindCodeByHash(ctx context.Context, codeHash string) (*models.OAuthAuthorizationCode, error) {
	var row models.OAuthAuthorizationCode
	err := database.DB.WithContext(ctx).Where("code_hash = ?", codeHash).First(&row).Error
	if errors.Is(err, gorm.ErrRecordNotFound) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &row, nil
}

func (GormOAuthStore) ConsumeCode(ctx context.Context, id uuid.UUID, at time.Time) (bool, error) {
	res := database.DB.WithContext(ctx).
		Model(&models.OAuthAuthorizationCode{}).
		Where("id = ? AND consumed_at IS NULL", id).
		Update("consumed_at", at)
	if res.Error != nil {
		return false, res.Error
	}
	return res.RowsAffected == 1, nil
}

func (GormOAuthStore) CreateRefreshToken(ctx context.Context, token *models.OAuthRefreshToken) error {
	return database.DB.WithContext(ctx).Create(token).Error
}

func (GormOAuthStore) FindRefreshTokenByHash(ctx context.Context, tokenHash string) (*models.OAuthRefreshToken, error) {
	var row models.OAuthRefreshToken
	err := database.DB.WithContext(ctx).Where("token_hash = ?", tokenHash).First(&row).Error
	if errors.Is(err, gorm.ErrRecordNotFound) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &row, nil
}

func (GormOAuthStore) RotateRefreshToken(ctx context.Context, id uuid.UUID, at time.Time) (bool, error) {
	res := database.DB.WithContext(ctx).
		Model(&models.OAuthRefreshToken{}).
		Where("id = ? AND revoked_at IS NULL", id).
		Update("revoked_at", at)
	if res.Error != nil {
		return false, res.Error
	}
	return res.RowsAffected == 1, nil
}

func (GormOAuthStore) RevokeGrant(ctx context.Context, clientID string, userID uuid.UUID, at time.Time) error {
	return database.DB.WithContext(ctx).
		Model(&models.OAuthRefreshToken{}).
		Where("client_id = ? AND user_id = ? AND revoked_at IS NULL", clientID, userID).
		Update("revoked_at", at).Error
}
