package auth

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

type fakeLookup struct{ m *services.ActiveMembership }

func (f fakeLookup) GetMembership(context.Context, uuid.UUID, uuid.UUID) (*services.ActiveMembership, error) {
	return f.m, nil
}

func runSwitch(t *testing.T, m *services.ActiveMembership, body string, lastActive *uuid.UUID) *httptest.ResponseRecorder {
	t.Helper()
	old, oldDeps := config.Cfg, deps
	config.Cfg.JWTSigningKey = "0123456789abcdef0123456789abcdef"
	config.Cfg.JWTIssuer = "reelbolt-api"
	config.Cfg.JWTAudience = "reelbolt-inference"
	t.Cleanup(func() { config.Cfg, deps = old, oldDeps })

	user := &models.ApplicationUser{ID: uuid.New(), Email: "a@b.c"}
	deps = switchDeps{
		lookup:  fakeLookup{m},
		getUser: func(uuid.UUID) (*models.ApplicationUser, error) { return user, nil },
		setLastActive: func(_ context.Context, _, org uuid.UUID) error {
			*lastActive = org
			return nil
		},
	}
	req := httptest.NewRequestWithContext(context.Background(), "POST", "/api/v1/auth/switch-org", strings.NewReader(body))
	req = req.WithContext(context.WithValue(req.Context(), middleware.UserContextKey,
		middleware.UserContext{UserID: user.ID.String(), Email: user.Email}))
	rec := httptest.NewRecorder()
	handleSwitchOrg(rec, req)
	return rec
}

func TestSwitchOrg_ReissuesTokenAndCookies(t *testing.T) {
	org := uuid.New()
	var last uuid.UUID
	m := &services.ActiveMembership{OrgID: org, OrgName: "Acme", OrgKind: models.OrgKindTeam, Role: models.OrgRoleAdmin}
	rec := runSwitch(t, m, `{"organizationId":"`+org.String()+`"}`, &last)

	if rec.Code != 200 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
	}
	if last != org {
		t.Fatal("last active org not recorded")
	}
	var resp TokenResponse
	_ = json.Unmarshal(rec.Body.Bytes(), &resp)
	claims, err := services.ValidateToken(resp.AccessToken)
	if err != nil || claims.Org != org.String() || claims.OrgRole != "Admin" {
		t.Fatalf("token: %+v err=%v", claims, err)
	}

	cookies := map[string]*http.Cookie{}
	for _, c := range rec.Result().Cookies() {
		cookies[c.Name] = c
	}
	tok, usr := cookies["reelbolt_token"], cookies["reelbolt_user"]
	if tok == nil || !tok.HttpOnly || tok.Value != resp.AccessToken {
		t.Fatalf("token cookie: %+v", tok)
	}
	if usr == nil || usr.HttpOnly {
		t.Fatalf("user cookie: %+v", usr)
	}
	raw, _ := url.QueryUnescape(usr.Value)
	var ui map[string]any
	if err := json.Unmarshal([]byte(raw), &ui); err != nil {
		t.Fatal(err)
	}
	if ui["org"] != org.String() || ui["orgName"] != "Acme" || ui["orgKind"] != "Team" || ui["orgRole"] != "Admin" {
		t.Fatalf("user cookie: %v", ui)
	}
}

func TestSwitchOrg_NonMemberIs404(t *testing.T) {
	var last uuid.UUID
	rec := runSwitch(t, nil, `{"organizationId":"`+uuid.NewString()+`"}`, &last)
	if rec.Code != http.StatusNotFound || last != uuid.Nil {
		t.Fatalf("got %d last=%v", rec.Code, last)
	}
}

func TestSwitchOrg_PlatformOrgIs404(t *testing.T) {
	var last uuid.UUID
	m := &services.ActiveMembership{OrgID: models.PlatformOrgID, OrgKind: models.OrgKindPlatform, Role: models.OrgRoleOwner}
	rec := runSwitch(t, m, `{"organizationId":"`+models.PlatformOrgID.String()+`"}`, &last)
	if rec.Code != http.StatusNotFound {
		t.Fatalf("got %d", rec.Code)
	}
}

func TestSwitchOrg_BadBody(t *testing.T) {
	var last uuid.UUID
	if rec := runSwitch(t, nil, `{"organizationId":"nope"}`, &last); rec.Code != http.StatusBadRequest {
		t.Fatalf("got %d", rec.Code)
	}
}
