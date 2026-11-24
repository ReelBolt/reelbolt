package services

import (
	"testing"
	"time"

	"github.com/golang-jwt/jwt/v5"
	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/models"
)

func withTestConfig(t *testing.T) {
	t.Helper()
	saved := config.Cfg
	config.Cfg.JWTSigningKey = "0123456789abcdef0123456789abcdef"
	config.Cfg.JWTIssuer = "reelbolt-api"
	config.Cfg.JWTAudience = "reelbolt-inference"
	t.Cleanup(func() { config.Cfg = saved })
}

func TestGenerateRunnerToken_IsScopedToItsDeviceAndOrganization(t *testing.T) {
	withTestConfig(t)

	deviceID, orgID := uuid.New(), uuid.New()
	token, expires, err := GenerateRunnerToken(deviceID, orgID)
	if err != nil {
		t.Fatalf("generate: %v", err)
	}

	if d := time.Until(expires); d <= 0 || d > RunnerTokenTTL+time.Minute {
		t.Fatalf("expiry in %v, want about %v", d, RunnerTokenTTL)
	}

	claims, err := ValidateToken(token)
	if err != nil {
		t.Fatalf("validate: %v", err)
	}
	if claims.Kind != RunnerTokenKind {
		t.Fatalf("kind = %q, want %q", claims.Kind, RunnerTokenKind)
	}
	if claims.Org != orgID.String() {
		t.Fatalf("org = %q, want %q", claims.Org, orgID)
	}
	if claims.UserID != "device:"+deviceID.String() {
		t.Fatalf("sub = %q", claims.UserID)
	}
	if claims.Email != "" || claims.OrgRole != "" || claims.PlatformAdmin || claims.IsAdmin {
		t.Fatalf("a runner token must carry no user identity or authority: %+v", claims)
	}
	if claims.Audience[0] != config.Cfg.JWTAudience || claims.Issuer != config.Cfg.JWTIssuer {
		t.Fatalf("issuer/audience = %q/%v", claims.Issuer, claims.Audience)
	}
}

// middleware.Auth identifies a caller by parsing the sub claim as a user uuid, so
// the device-prefixed subject is what keeps a runner token out of every user-facing
// endpoint. This asserts the property that check depends on.
func TestRunnerTokenSubjectIsNotAUserID(t *testing.T) {
	subject := RunnerSubject(uuid.New())
	if _, err := uuid.Parse(subject); err == nil {
		t.Fatalf("subject %q parsed as a user id", subject)
	}
}

func TestGenerateRunnerToken_UsesHS256(t *testing.T) {
	withTestConfig(t)
	token, _, err := GenerateRunnerToken(uuid.New(), uuid.New())
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	parsed, err := jwt.Parse(token, func(*jwt.Token) (interface{}, error) {
		return []byte(config.Cfg.JWTSigningKey), nil
	})
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if _, ok := parsed.Method.(*jwt.SigningMethodHMAC); !ok {
		t.Fatalf("unexpected alg %v", parsed.Method.Alg())
	}
}

// A user session must not be mistakable for a runner credential.
func TestUserTokenHasNoRunnerKind(t *testing.T) {
	withTestConfig(t)
	user := &models.ApplicationUser{Email: "a@b.test"}
	user.ID = uuid.New()

	token, err := GenerateToken(user, &ActiveMembership{OrgID: uuid.New(), Role: models.OrgRoleOwner})
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	claims, err := ValidateToken(token)
	if err != nil {
		t.Fatalf("validate: %v", err)
	}
	if claims.Kind != "" {
		t.Fatalf("user token kind = %q, want empty", claims.Kind)
	}
	if _, err := uuid.Parse(claims.UserID); err != nil {
		t.Fatalf("user sub %q is not a uuid", claims.UserID)
	}
}
