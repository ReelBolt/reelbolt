package services

import (
	"testing"

	"github.com/golang-jwt/jwt/v5"
	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/models"
)

func setJWTConfig(t *testing.T) {
	t.Helper()
	old := config.Cfg
	config.Cfg.JWTSigningKey = "0123456789abcdef0123456789abcdef"
	config.Cfg.JWTIssuer = "reelbolt-api"
	config.Cfg.JWTAudience = "reelbolt-inference"
	t.Cleanup(func() { config.Cfg = old })
}

func TestGenerateToken_OrgClaimsRoundTrip(t *testing.T) {
	setJWTConfig(t)
	user := &models.ApplicationUser{ID: uuid.New(), Email: "a@b.c", IsAdmin: true}
	m := &ActiveMembership{OrgID: uuid.New(), Role: models.OrgRoleAdmin}

	tok, err := GenerateToken(user, m)
	if err != nil {
		t.Fatal(err)
	}
	c, err := ValidateToken(tok)
	if err != nil {
		t.Fatal(err)
	}
	if c.Org != m.OrgID.String() || c.OrgRole != "Admin" {
		t.Fatalf("org claims lost: %+v", c)
	}
	if !c.PlatformAdmin || !c.IsAdmin {
		t.Fatalf("platformAdmin and its isAdmin alias must both be true: %+v", c)
	}
}

func TestGenerateToken_IsAdminAliasTracksPlatformAdmin(t *testing.T) {
	setJWTConfig(t)
	for _, admin := range []bool{true, false} {
		user := &models.ApplicationUser{ID: uuid.New(), Email: "a@b.c", IsAdmin: admin}
		tok, _ := GenerateToken(user, &ActiveMembership{OrgID: uuid.New(), Role: models.OrgRoleOwner})
		parsed, _, err := jwt.NewParser().ParseUnverified(tok, jwt.MapClaims{})
		if err != nil {
			t.Fatal(err)
		}
		mc := parsed.Claims.(jwt.MapClaims)
		if mc["isAdmin"] != admin || mc["platformAdmin"] != admin {
			t.Fatalf("admin=%v: isAdmin=%v platformAdmin=%v", admin, mc["isAdmin"], mc["platformAdmin"])
		}
		if mc["org"] == nil || mc["orgRole"] != "Owner" {
			t.Fatalf("wire names wrong: %v", mc)
		}
	}
}

func TestGenerateToken_NilMembershipOmitsOrgClaims(t *testing.T) {
	setJWTConfig(t)
	user := &models.ApplicationUser{ID: uuid.New(), Email: "a@b.c"}
	tok, _ := GenerateToken(user, nil)
	c, err := ValidateToken(tok)
	if err != nil {
		t.Fatal(err)
	}
	if c.Org != "" || c.OrgRole != "" {
		t.Fatalf("unexpected org claims: %+v", c)
	}
}
