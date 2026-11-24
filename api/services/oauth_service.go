package services

import (
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/golang-jwt/jwt/v5"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/models"
)

// The MCP OAuth authorization server's shared primitives (docs/design/mcp-oauth.md §3-§5).
//
// Everything here is pure: no database, no request. The handlers own the flow and the storage, and
// these helpers are what both the handlers and their tests agree on for scopes, hashing, PKCE and the
// access token, so the two cannot drift.

const (
	// OAuthScopeAssistantTools is the always-granted scope: the non-gated assistant tool set.
	OAuthScopeAssistantTools = "assistant.tools"
	// OAuthScopeAssistantAdmin is the opt-in scope for the 13 gated tools. It is granted only when
	// the user's mcp_admin_tools_enabled is true at consent time (docs/design/mcp-oauth.md §3).
	OAuthScopeAssistantAdmin = "assistant.admin"

	// OAuthScopeRunsWatch is the narrow scope that only lets a client read the status of runs. It is
	// granted on its own: it is never combined with the assistant scopes.
	OAuthScopeRunsWatch = "runs.watch"
	// OAuthAccessTokenTTL is the access-token lifetime (contract §4: expires_in 3600).
	OAuthAccessTokenTTL = 3600 * time.Second
	// OAuthRefreshTokenTTL is the refresh lifetime (contract §4: 30 days).
	OAuthRefreshTokenTTL = 30 * 24 * time.Hour
	// OAuthAuthorizationCodeTTL bounds how long an issued code may be exchanged. Short, because the
	// code is a bearer credential sitting in a URL: the browser has already been redirected by the
	// time it is used.
	OAuthAuthorizationCodeTTL = 10 * time.Minute

	// oauthSecretBytes is the entropy of every generated credential here (client ids, client
	// secrets, codes, refresh tokens). 32 bytes make guessing hopeless and base64url keeps the
	// result 43 characters, which also satisfies RFC 7636's minimum code_verifier length.
	oauthSecretBytes = 32
)

// SupportedOAuthScopes is the closed scope set this deployment understands, in the order the
// metadata document advertises it. Anything else a client asks for is dropped at consent.
func SupportedOAuthScopes() []string {
	return []string{OAuthScopeAssistantTools, OAuthScopeAssistantAdmin, OAuthScopeRunsWatch}
}

// GrantOAuthScopes computes the scopes actually granted for one consent.
//
// The rules are the contract's (§3): `assistant.tools` is always granted; `assistant.admin` is
// granted only when the user's opt-in is on AND the client asked for it; every other scope the client
// listed is dropped. The result is deduplicated and in SupportedOAuthScopes order, so the value that
// reaches the `scope` claim — and the value the consent screen shows — is exactly what was granted and
// can never echo back something that was not.
//
// `runs.watch` is the exception to "always grant assistant.tools": when the request names it and
// names neither assistant.tools nor assistant.admin, exactly ["runs.watch"] is granted. In every
// other request it is dropped, so it is never combined with another scope.
func GrantOAuthScopes(requested []string, adminEnabled bool) []string {
	wanted := make(map[string]bool, len(requested))
	for _, raw := range requested {
		for _, s := range strings.Fields(raw) {
			wanted[s] = true
		}
	}

	if wanted[OAuthScopeRunsWatch] && !wanted[OAuthScopeAssistantTools] && !wanted[OAuthScopeAssistantAdmin] {
		return []string{OAuthScopeRunsWatch}
	}
	granted := []string{OAuthScopeAssistantTools}
	if adminEnabled && wanted[OAuthScopeAssistantAdmin] {
		granted = append(granted, OAuthScopeAssistantAdmin)
	}
	return granted
}

// FormatOAuthScopes renders a granted scope set as the space-delimited string RFC 6749 §3.3 uses in
// both the token response and the `scope` claim.
func FormatOAuthScopes(scopes []string) string { return strings.Join(scopes, " ") }

// HashOAuthSecret is the one-way function every stored credential in this flow goes through: the
// authorization server keeps hex SHA-256 of the client secret, the authorization code and the refresh
// token, never the plaintext. SHA-256 (not bcrypt) is the right choice for all three because each
// value is 256 bits of `crypto/rand` output — there is no dictionary to slow an attacker down — and
// because codes and refresh tokens are looked up BY hash, which needs a deterministic function.
func HashOAuthSecret(raw string) string {
	sum := sha256.Sum256([]byte(raw))
	return hex.EncodeToString(sum[:])
}

// NewOAuthSecret mints one opaque credential: 32 random bytes, base64url without padding.
func NewOAuthSecret() (string, error) {
	buf := make([]byte, oauthSecretBytes)
	if _, err := rand.Read(buf); err != nil {
		return "", fmt.Errorf("generate oauth secret: %w", err)
	}
	return base64.RawURLEncoding.EncodeToString(buf), nil
}

// VerifyOAuthSecret compares a presented secret against the stored hash in constant time, so the
// comparison is not itself an oracle over the stored value.
func VerifyOAuthSecret(storedHash, presented string) bool {
	if storedHash == "" || presented == "" {
		return false
	}
	return subtle.ConstantTimeCompare([]byte(storedHash), []byte(HashOAuthSecret(presented))) == 1
}

// ValidPKCEChallenge reports whether a client-supplied challenge has the shape RFC 7636 defines: a
// base64url string of 43 to 128 characters. Anything else is refused before any state is written, so
// a malformed challenge cannot produce a code that can never be redeemed.
func ValidPKCEChallenge(challenge string) bool {
	if len(challenge) < 43 || len(challenge) > 128 {
		return false
	}
	for _, c := range challenge {
		if !isBase64URLChar(c) {
			return false
		}
	}
	return true
}

// ValidPKCEVerifier is the same shape check for the verifier presented at the token endpoint.
func ValidPKCEVerifier(verifier string) bool { return ValidPKCEChallenge(verifier) }

// VerifyPKCES256 checks `BASE64URL(SHA256(verifier)) == challenge`, in constant time.
//
// S256 is the only method this server offers (contract §4), which is why there is no `plain` arm: a
// `plain` challenge would let anyone holding the authorization request redeem the code.
func VerifyPKCES256(challenge, verifier string) bool {
	if !ValidPKCEChallenge(challenge) || !ValidPKCEVerifier(verifier) {
		return false
	}
	sum := sha256.Sum256([]byte(verifier))
	computed := base64.RawURLEncoding.EncodeToString(sum[:])
	return subtle.ConstantTimeCompare([]byte(computed), []byte(challenge)) == 1
}

func isBase64URLChar(c rune) bool {
	return c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '_'
}

// GenerateOAuthAccessToken issues the access token for a granted consent.
//
// This is deliberately NOT a second permission model: it is the ordinary ReelBolt JWT — same issuer,
// same audience, same HS256 signing key, same `sub`/`org`/`orgRole`/`platformAdmin` claims — plus the
// `scope` claim (docs/design/mcp-oauth.md §2). The Inference API accepts it through the validator it
// already runs, and re-reads membership from the database exactly as it does for a dashboard session.
//
// now is passed in rather than read here so one token exchange uses one clock: the caller stamps the
// refresh row and computes expires_in from the same instant, and a test can drive the whole flow.
func GenerateOAuthAccessToken(user *models.ApplicationUser, m *ActiveMembership, scope, termsVersion, privacyVersion string, now time.Time) (string, time.Time, error) {
	if strings.TrimSpace(scope) == "" {
		return "", time.Time{}, errors.New("oauth access token requires a non-empty scope")
	}
	expires := now.Add(OAuthAccessTokenTTL)

	claims := TokenClaims{
		UserID:             user.ID.String(),
		Email:              user.Email,
		PlatformAdmin:      user.IsAdmin,
		IsAdmin:            user.IsAdmin,
		MustChangePassword: user.MustChangePassword,
		EmailVerified:      user.EmailVerifiedAt != nil,
		TermsVersion:       termsVersion,
		PrivacyVersion:     privacyVersion,
		Scope:              scope,
		RegisteredClaims: jwt.RegisteredClaims{
			Issuer:    config.Cfg.JWTIssuer,
			Audience:  jwt.ClaimStrings{config.Cfg.JWTAudience},
			Subject:   user.ID.String(),
			ExpiresAt: jwt.NewNumericDate(expires),
			IssuedAt:  jwt.NewNumericDate(now),
		},
	}
	if m != nil {
		claims.Org = m.OrgID.String()
		claims.OrgRole = m.Role
	}

	token := jwt.NewWithClaims(jwt.SigningMethodHS256, claims)
	signed, err := token.SignedString([]byte(config.Cfg.JWTSigningKey))
	if err != nil {
		return "", time.Time{}, fmt.Errorf("sign oauth access token: %w", err)
	}
	return signed, expires, nil
}
