package services

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"time"
)

// Emailed single-use token rules (docs/design/saas-accounts.md "Token rules").
const (
	VerifyEmailTTL   = 24 * time.Hour
	ResetPasswordTTL = time.Hour

	// MinPasswordLength applies on every set path; MaxPasswordBytes is bcrypt's input limit.
	MinPasswordLength = 10
	MaxPasswordBytes  = 72
)

// NewRawToken returns 32 random bytes as hex (the value that goes in the email)
// and its SHA-256 hex digest (the only thing stored).
func NewRawToken() (raw, hash string, err error) {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return "", "", fmt.Errorf("generate token: %w", err)
	}
	raw = hex.EncodeToString(b)
	return raw, HashToken(raw), nil
}

// HashToken is the hex SHA-256 of a raw token, used for storage and lookup.
func HashToken(raw string) string {
	sum := sha256.Sum256([]byte(raw))
	return hex.EncodeToString(sum[:])
}

// ValidateNewPassword enforces the shared password policy.
func ValidateNewPassword(p string) error {
	if len(p) < MinPasswordLength {
		return fmt.Errorf("password must be at least %d characters", MinPasswordLength)
	}
	if len(p) > MaxPasswordBytes {
		return fmt.Errorf("password must be at most %d bytes", MaxPasswordBytes)
	}
	return nil
}
