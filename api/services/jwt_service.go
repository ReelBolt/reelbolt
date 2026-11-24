package services

import (
	"fmt"
	"time"

	"github.com/golang-jwt/jwt/v5"
	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/models"
)

// The runner token is a deliberately narrow credential: a paired desktop machine
// proves possession of its Ed25519 key and gets one of these, which lets it pull the
// jobs routed to its own organization and PUT to the presigned URLs inside them —
// and nothing else (docs/design/desktop-runner-adr.md §6).
const (
	// RunnerTokenTTL is how long a minted runner token stays valid. It is short
	// because re-minting is cheap and the gateway re-checks revocation anyway.
	RunnerTokenTTL = 15 * time.Minute
	// RunnerTokenKind is the `kind` claim marking a token as a runner credential, so
	// a consumer never has to infer it from the subject's shape.
	RunnerTokenKind = "runner"
	// RunnerSubjectPrefix prefixes the device id in `sub`, keeping it structurally
	// distinct from a user id: middleware.Auth parses `sub` as a uuid and refuses
	// anything else, so a runner token cannot authenticate a user request.
	RunnerSubjectPrefix = "device:"
)

type TokenClaims struct {
	UserID string `json:"sub"`
	Email  string `json:"email"`
	// Org is the active organization id and OrgRole the caller's role in it at
	// issue time (consumers re-read the database; see middleware.Auth).
	Org     string `json:"org,omitempty"`
	OrgRole string `json:"orgRole,omitempty"`
	// PlatformAdmin is platform-wide authority. IsAdmin is its alias, emitted
	// for one release for consumers that have not moved over yet.
	PlatformAdmin      bool `json:"platformAdmin"`
	IsAdmin            bool `json:"isAdmin"`
	MustChangePassword bool `json:"mustChangePassword"`
	// EmailVerified is false until the user confirms their address; the UI is
	// restricted and (C5) workflow execution refused while it is false.
	EmailVerified bool `json:"emailVerified"`
	// TermsVersion is the version of the Terms this session accepted, and is
	// omitted when the user has accepted none (C10). Consumers must read an
	// absent claim as "never accepted", never as "accepted the current one".
	TermsVersion string `json:"termsVersion,omitempty"`
	// PrivacyVersion is the version of the Privacy Policy this session
	// accepted, omitted when none — read exactly like TermsVersion.
	PrivacyVersion string `json:"privacyVersion,omitempty"`
	// Kind is set only on a runner token (RunnerTokenKind); a user session leaves it
	// empty.
	Kind string `json:"kind,omitempty"`
	// Scope is the space-delimited OAuth scope set an MCP access token was granted
	// (docs/design/mcp-oauth.md §3). It is absent on an ordinary dashboard session,
	// which is why it is omitted when empty: a consumer must read an absent claim as
	// "not an OAuth token", never as "granted nothing".
	Scope string `json:"scope,omitempty"`
	jwt.RegisteredClaims
}

// RunnerSubject is the `sub` claim of a runner token for deviceID.
func RunnerSubject(deviceID uuid.UUID) string {
	return RunnerSubjectPrefix + deviceID.String()
}

// GenerateToken issues a 24h token for user scoped to the active organization
// described by m, without a terms claim. A nil m yields a token without org
// claims (legacy shape).
func GenerateToken(user *models.ApplicationUser, m *ActiveMembership) (string, error) {
	return GenerateTokenWithLegal(user, m, "", "")
}

// GenerateTokenWithTerms is GenerateToken plus the accepted terms version.
func GenerateTokenWithTerms(user *models.ApplicationUser, m *ActiveMembership, termsVersion string) (string, error) {
	return GenerateTokenWithLegal(user, m, termsVersion, "")
}

// GenerateTokenWithLegal is GenerateToken plus the accepted Terms and Privacy
// versions ("" for a document never accepted, which omits the claim).
func GenerateTokenWithLegal(user *models.ApplicationUser, m *ActiveMembership, termsVersion, privacyVersion string) (string, error) {
	claims := TokenClaims{
		TermsVersion:       termsVersion,
		PrivacyVersion:     privacyVersion,
		UserID:             user.ID.String(),
		Email:              user.Email,
		PlatformAdmin:      user.IsAdmin,
		IsAdmin:            user.IsAdmin,
		MustChangePassword: user.MustChangePassword,
		EmailVerified:      user.EmailVerifiedAt != nil,
		RegisteredClaims: jwt.RegisteredClaims{
			Issuer:    config.Cfg.JWTIssuer,
			Audience:  jwt.ClaimStrings{config.Cfg.JWTAudience},
			Subject:   user.ID.String(),
			ExpiresAt: jwt.NewNumericDate(time.Now().Add(24 * time.Hour)),
			IssuedAt:  jwt.NewNumericDate(time.Now()),
		},
	}
	if m != nil {
		claims.Org = m.OrgID.String()
		claims.OrgRole = m.Role
	}

	token := jwt.NewWithClaims(jwt.SigningMethodHS256, claims)
	return token.SignedString([]byte(config.Cfg.JWTSigningKey))
}

// GenerateRunnerToken issues a short-lived token for one paired device, scoped to
// exactly one organization: sub=device:<id>, org=<id>, kind=runner (WP E4b).
//
// It carries no user identity, no orgRole and no platformAdmin, and it is never
// minted for a revoked device — the caller checks that first. Because
// middleware.Auth parses `sub` as a user uuid, this token is structurally unusable
// against every user-facing endpoint in this service.
func GenerateRunnerToken(deviceID, orgID uuid.UUID) (string, time.Time, error) {
	now := time.Now()
	expires := now.Add(RunnerTokenTTL)
	claims := TokenClaims{
		UserID: RunnerSubject(deviceID),
		Org:    orgID.String(),
		Kind:   RunnerTokenKind,
		RegisteredClaims: jwt.RegisteredClaims{
			Issuer:    config.Cfg.JWTIssuer,
			Audience:  jwt.ClaimStrings{config.Cfg.JWTAudience},
			Subject:   RunnerSubject(deviceID),
			ExpiresAt: jwt.NewNumericDate(expires),
			IssuedAt:  jwt.NewNumericDate(now),
		},
	}

	token := jwt.NewWithClaims(jwt.SigningMethodHS256, claims)
	signed, err := token.SignedString([]byte(config.Cfg.JWTSigningKey))
	if err != nil {
		return "", time.Time{}, fmt.Errorf("sign runner token: %w", err)
	}
	return signed, expires, nil
}

func ValidateToken(tokenString string) (*TokenClaims, error) {
	// Every issuer in this repo (sessions, MCP OAuth access tokens, runner tokens)
	// signs HS256 and sets exp and iat; anything else is refused.
	token, err := jwt.ParseWithClaims(tokenString, &TokenClaims{}, func(t *jwt.Token) (interface{}, error) {
		if t.Method != jwt.SigningMethodHS256 {
			return nil, fmt.Errorf("unexpected signing method: %v", t.Header["alg"])
		}
		return []byte(config.Cfg.JWTSigningKey), nil
	},
		jwt.WithValidMethods([]string{jwt.SigningMethodHS256.Alg()}),
		jwt.WithExpirationRequired(),
		jwt.WithIssuedAt(),
		jwt.WithIssuer(config.Cfg.JWTIssuer),
		jwt.WithAudience(config.Cfg.JWTAudience),
	)
	if err != nil {
		return nil, err
	}

	claims, ok := token.Claims.(*TokenClaims)
	if !ok || !token.Valid {
		return nil, fmt.Errorf("invalid token claims")
	}
	// WithIssuedAt only rejects a future iat; an absent one is refused here.
	if claims.IssuedAt == nil {
		return nil, fmt.Errorf("token has no iat")
	}

	return claims, nil
}
