package handlers

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

func ucIn(org uuid.UUID, role string) middleware.UserContext {
	return middleware.UserContext{UserID: uuid.NewString(), OrganizationID: org, OrgRole: role}
}

func TestExecutionAccessFor(t *testing.T) {
	orgA, orgB := uuid.New(), uuid.New()
	for _, role := range []string{models.OrgRoleOwner, models.OrgRoleAdmin, models.OrgRoleMember} {
		if got := executionAccessFor(ucIn(orgA, role), orgA); got != accessGranted {
			t.Errorf("%s same org: %v, want granted", role, got)
		}
	}
	// A teammate's run is visible regardless of who initiated it (no user check).
	if executionAccessFor(ucIn(orgA, models.OrgRoleMember), orgA) != accessGranted {
		t.Error("teammate execution must be visible")
	}
	// Another org's execution is invisible (404), whatever the role.
	for _, role := range []string{models.OrgRoleOwner, models.OrgRoleAdmin, models.OrgRoleMember, models.OrgRoleBilling} {
		if got := executionAccessFor(ucIn(orgA, role), orgB); got != accessNotFound {
			t.Errorf("%s cross org: %v, want not found", role, got)
		}
	}
	// Billing in the same org is forbidden, not not-found.
	if got := executionAccessFor(ucIn(orgA, models.OrgRoleBilling), orgA); got != accessForbidden {
		t.Errorf("billing same org: %v", got)
	}
}

func TestExecutionAccess_NoSilentPlatformAdminBypass(t *testing.T) {
	orgA, orgB := uuid.New(), uuid.New()
	uc := ucIn(orgA, models.OrgRoleOwner)
	uc.IsAdmin, uc.PlatformAdmin = true, true
	if got := executionAccessFor(uc, orgB); got != accessNotFound {
		t.Fatalf("platform admin saw another org's execution: %v", got)
	}
}

func TestExecutionAccess_NoActiveOrgFailsClosed(t *testing.T) {
	// A legacy token (no org claim) has OrganizationID == uuid.Nil; an execution
	// row must never match it, even one with a zero org id.
	legacy := middleware.UserContext{UserID: uuid.NewString(), IsAdmin: true}
	if executionAccessFor(legacy, uuid.Nil) != accessNotFound || executionAccessFor(legacy, uuid.New()) != accessNotFound {
		t.Fatal("org-less caller must see nothing")
	}
	if canSeeExecutions(legacy) {
		t.Fatal("org-less caller must not pass the aggregate check")
	}
	if canSeeExecutions(ucIn(uuid.New(), models.OrgRoleBilling)) {
		t.Fatal("billing must not pass the aggregate check")
	}
	if !canSeeExecutions(ucIn(uuid.New(), models.OrgRoleMember)) {
		t.Fatal("member must pass the aggregate check")
	}
}

func TestVisibilityCache(t *testing.T) {
	orgA, orgB := uuid.New(), uuid.New()
	calls := map[string]int{}
	orgOf := map[string]uuid.UUID{"mine": orgA, "theirs": orgB}
	c := &visibilityCache{
		uc: ucIn(orgA, models.OrgRoleMember),
		lookup: func(id string) (uuid.UUID, bool, error) {
			calls[id]++
			if id == "boom" {
				return uuid.Nil, false, errors.New("db down")
			}
			o, ok := orgOf[id]
			return o, ok, nil
		},
		allowed: map[string]bool{},
	}
	for i := 0; i < 3; i++ {
		if !c.canSee("mine") || c.canSee("theirs") || c.canSee("unknown") {
			t.Fatal("wrong visibility")
		}
	}
	for id, n := range calls {
		if n != 1 {
			t.Errorf("%s looked up %d times, want 1 (cached per connection)", id, n)
		}
	}
	// Errors deny the event and are retried, not cached.
	if c.canSee("boom") || c.canSee("boom") || calls["boom"] != 2 { //nolint:staticcheck // duplicate call proves caching
		t.Fatalf("error handling: calls=%d", calls["boom"])
	}
}

func TestDenyExecutionStatusCodes(t *testing.T) {
	orgA, orgB := uuid.New(), uuid.New()
	cases := []struct {
		name string
		uc   middleware.UserContext
		exec uuid.UUID
		code int
		ok   bool
	}{
		{"same org", ucIn(orgA, models.OrgRoleMember), orgA, 200, true},
		{"other org", ucIn(orgA, models.OrgRoleOwner), orgB, http.StatusNotFound, false},
		{"billing same org", ucIn(orgA, models.OrgRoleBilling), orgA, http.StatusForbidden, false},
	}
	for _, c := range cases {
		rec := httptest.NewRecorder()
		ok := denyExecution(rec, c.uc, &models.WorkflowExecution{OrganizationID: c.exec})
		if ok != c.ok || (!ok && rec.Code != c.code) {
			t.Errorf("%s: ok=%v code=%d", c.name, ok, rec.Code)
		}
	}
}

func TestAggregateStats(t *testing.T) {
	got := aggregateStats([]statusCountRow{{"Queued", 2}, {"Running", 1}, {"Completed", 5}, {"Failed", 3}, {"Cancelled", 4}})
	want := WorkflowStats{Queued: 2, Active: 1, Completed: 5, Failed: 3, Total: 15}
	if got != want {
		t.Fatalf("got %+v want %+v", got, want)
	}
	if aggregateStats(nil) != (WorkflowStats{}) {
		t.Fatal("nil rows must be zero stats")
	}
}

func TestStatsHandlerFailsClosedWithoutOrg(t *testing.T) {
	// No database is configured in this test: the handler must answer zeros
	// before ever touching it when the caller has no visible org.
	for _, uc := range []middleware.UserContext{
		{UserID: uuid.NewString(), IsAdmin: true}, // legacy admin token
		ucIn(uuid.New(), models.OrgRoleBilling),
	} {
		req := httptest.NewRequestWithContext(context.Background(), "GET", "/api/v1/workflows/stats", nil)
		rec := httptest.NewRecorder()
		handleWorkflowStats(rec, req.WithContext(withUC(req, uc)))
		if rec.Code != 200 || !strings.Contains(rec.Body.String(), `"total":0`) {
			t.Fatalf("got %d %s", rec.Code, rec.Body.String())
		}
	}
}

func withUC(r *http.Request, uc middleware.UserContext) context.Context {
	return context.WithValue(r.Context(), middleware.UserContextKey, uc)
}

func TestHubScopeKeys(t *testing.T) {
	a, b := uuid.New(), uuid.New()
	if services.HubScopeKey(a, "u1") != "org:"+a.String()+":u1" {
		t.Fatal("scope key format")
	}
	if services.HubScopeKey(a, "u1") == services.HubScopeKey(b, "u1") {
		t.Fatal("same user in two orgs must not share a connection scope")
	}
	if services.ExecutionHubScopeKey(a, "u1") == services.HubScopeKey(a, "u1") {
		t.Fatal("execution streams must have their own scope")
	}
	// The per-user cap applies per (org, user): a third and fourth connection in
	// the same org are refused, while the same user in another org is not.
	var chans []chan services.WorkflowEvent
	for i := 0; i < 3; i++ {
		ch, ok := services.Hub.Subscribe(services.HubScopeKey(a, "capuser"))
		if !ok {
			t.Fatalf("connection %d refused", i)
		}
		chans = append(chans, ch)
	}
	if _, ok := services.Hub.Subscribe(services.HubScopeKey(a, "capuser")); ok {
		t.Fatal("fourth connection in the same org must be refused")
	}
	other, ok := services.Hub.Subscribe(services.HubScopeKey(b, "capuser"))
	if !ok {
		t.Fatal("same user in another org must have its own slots")
	}
	services.Hub.Unsubscribe(other)
	for _, ch := range chans {
		services.Hub.Unsubscribe(ch)
	}
}
