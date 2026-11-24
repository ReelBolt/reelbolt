package orgs

import (
	"bytes"
	"context"
	"encoding/json"
	"log"
	"net/http"
	"net/http/httptest"
	"net/textproto"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

// ---- fakes ----

type fakeUser struct{ email, name string }

// fakeStore is an in-memory OrgStore + MembershipLookup that also mimics the
// store-level last-Owner guard.
type fakeStore struct {
	orgs    map[uuid.UUID]*models.Organization
	members map[uuid.UUID]map[uuid.UUID]string // org -> user -> role
	users   map[uuid.UUID]fakeUser
	invites []models.OrganizationInvite
	sent    []services.MailData
	sentTo  []string
	evicted [][2]uuid.UUID
	now     time.Time
	// sendErr, when set, is what SendTemplate fails with.
	sendErr error
}

func newFake() *fakeStore {
	return &fakeStore{
		orgs:    map[uuid.UUID]*models.Organization{},
		members: map[uuid.UUID]map[uuid.UUID]string{},
		users:   map[uuid.UUID]fakeUser{},
		now:     time.Date(2026, 10, 5, 12, 0, 0, 0, time.UTC),
	}
}

func (f *fakeStore) user(email string) uuid.UUID {
	id := uuid.New()
	f.users[id] = fakeUser{email: email, name: strings.Split(email, "@")[0]}
	return id
}

func (f *fakeStore) org(kind, name string) uuid.UUID {
	id := uuid.New()
	f.orgs[id] = &models.Organization{ID: id, Name: name, Slug: name, Kind: kind}
	f.members[id] = map[uuid.UUID]string{}
	return id
}

func (f *fakeStore) add(org, user uuid.UUID, role string) { f.members[org][user] = role }

func (f *fakeStore) GetMembership(_ context.Context, userID, orgID uuid.UUID) (*services.ActiveMembership, error) {
	o, ok := f.orgs[orgID]
	if !ok || o.DeletedAt != nil {
		return nil, nil
	}
	role, ok := f.members[orgID][userID]
	if !ok {
		return nil, nil
	}
	return &services.ActiveMembership{OrgID: orgID, OrgName: o.Name, OrgKind: o.Kind, Role: role}, nil
}

func (f *fakeStore) ListUserOrgs(_ context.Context, u uuid.UUID) ([]services.OrgSummary, error) {
	var out []services.OrgSummary
	for id, m := range f.members {
		if role, ok := m[u]; ok && f.orgs[id].Kind != models.OrgKindPlatform && f.orgs[id].DeletedAt == nil {
			out = append(out, services.OrgSummary{Org: *f.orgs[id], Role: role})
		}
	}
	return out, nil
}

func (f *fakeStore) CreateTeamOrg(_ context.Context, u uuid.UUID, name string) (*models.Organization, error) {
	id := f.org(models.OrgKindTeam, name)
	f.add(id, u, models.OrgRoleOwner)
	return f.orgs[id], nil
}
func (f *fakeStore) UpdateOrgName(_ context.Context, id uuid.UUID, name string) error {
	f.orgs[id].Name = name
	return nil
}
func (f *fakeStore) UpdateOrgComputePreference(_ context.Context, id uuid.UUID, preference string) error {
	f.orgs[id].ComputePreference = preference
	return nil
}
func (f *fakeStore) DeleteOrg(_ context.Context, id uuid.UUID) error {
	n := f.now
	f.orgs[id].DeletedAt = &n
	return nil
}
func (f *fakeStore) ListMembers(_ context.Context, id uuid.UUID) ([]services.OrgMember, error) {
	var out []services.OrgMember
	for u, r := range f.members[id] {
		out = append(out, services.OrgMember{UserID: u, Email: f.users[u].email, DisplayName: f.users[u].name, Role: r})
	}
	return out, nil
}
func (f *fakeStore) GetMemberRole(_ context.Context, org, u uuid.UUID) (string, bool, error) {
	r, ok := f.members[org][u]
	return r, ok, nil
}
func (f *fakeStore) CountOwners(_ context.Context, org uuid.UUID) (int, error) {
	n := 0
	for _, r := range f.members[org] {
		if r == models.OrgRoleOwner {
			n++
		}
	}
	return n, nil
}
func (f *fakeStore) SetMemberRole(ctx context.Context, org, u uuid.UUID, role string) error {
	if f.members[org][u] == models.OrgRoleOwner && role != models.OrgRoleOwner {
		if n, _ := f.CountOwners(ctx, org); n <= 1 {
			return services.ErrLastOwner
		}
	}
	f.members[org][u] = role
	return nil
}
func (f *fakeStore) RemoveMember(ctx context.Context, org, u uuid.UUID) error {
	if f.members[org][u] == models.OrgRoleOwner {
		if n, _ := f.CountOwners(ctx, org); n <= 1 {
			return services.ErrLastOwner
		}
	}
	delete(f.members[org], u)
	return nil
}
func (f *fakeStore) IsEmailMember(_ context.Context, org uuid.UUID, email string) (bool, error) {
	for u := range f.members[org] {
		if strings.EqualFold(f.users[u].email, email) {
			return true, nil
		}
	}
	return false, nil
}
func (f *fakeStore) CreateInvite(_ context.Context, inv *models.OrganizationInvite) error {
	for i := range f.invites {
		if f.invites[i].OrganizationID == inv.OrganizationID && f.invites[i].Email == inv.Email && f.invites[i].RevokedAt == nil && f.invites[i].AcceptedAt == nil {
			t := inv.CreatedAt
			f.invites[i].RevokedAt = &t
		}
	}
	f.invites = append(f.invites, *inv)
	return nil
}
func (f *fakeStore) ListPendingInvites(_ context.Context, org uuid.UUID, now time.Time) ([]models.OrganizationInvite, error) {
	var out []models.OrganizationInvite
	for _, i := range f.invites {
		if i.OrganizationID == org && i.RevokedAt == nil && i.AcceptedAt == nil && i.ExpiresAt.After(now) {
			out = append(out, i)
		}
	}
	return out, nil
}
func (f *fakeStore) RevokeInvite(_ context.Context, org, id uuid.UUID, now time.Time) (bool, error) {
	for i := range f.invites {
		if f.invites[i].ID == id && f.invites[i].OrganizationID == org && f.invites[i].RevokedAt == nil && f.invites[i].AcceptedAt == nil {
			f.invites[i].RevokedAt = &now
			return true, nil
		}
	}
	return false, nil
}
func (f *fakeStore) FindInviteByTokenHash(_ context.Context, h string) (*models.OrganizationInvite, error) {
	for i := range f.invites {
		if f.invites[i].TokenHash == h {
			c := f.invites[i]
			return &c, nil
		}
	}
	return nil, nil
}
func (f *fakeStore) AcceptInvite(_ context.Context, inv *models.OrganizationInvite, u uuid.UUID, now time.Time) error {
	for i := range f.invites {
		if f.invites[i].ID == inv.ID {
			f.invites[i].AcceptedAt = &now
		}
	}
	if _, ok := f.members[inv.OrganizationID][u]; !ok {
		f.members[inv.OrganizationID][u] = inv.Role
	}
	return nil
}
func (f *fakeStore) GetOrganization(_ context.Context, id uuid.UUID) (*models.Organization, error) {
	o, ok := f.orgs[id]
	if !ok || o.DeletedAt != nil {
		return nil, nil
	}
	c := *o
	return &c, nil
}
func (f *fakeStore) GetUserIdentity(_ context.Context, id uuid.UUID) (string, string, error) {
	u := f.users[id]
	return u.email, u.name, nil
}

func (f *fakeStore) SendTemplate(to string, _ services.TemplateName, d services.MailData) error {
	f.sentTo = append(f.sentTo, to)
	f.sent = append(f.sent, d)
	return f.sendErr
}

// do runs one request as `as` through the real routes.
func (f *fakeStore) do(as uuid.UUID, method, path, body string) *httptest.ResponseRecorder {
	h := &Handler{
		Store: f, Lookup: f,
		Mailer:     func() services.Mailer { return f },
		Invalidate: func(u, o uuid.UUID) { f.evicted = append(f.evicted, [2]uuid.UUID{u, o}) },
		Now:        func() time.Time { return f.now },
		BaseURL:    func() string { return "https://app.example" },
	}
	r := mux.NewRouter()
	r.Use(func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
			uc := middleware.UserContext{UserID: as.String(), Email: f.users[as].email}
			next.ServeHTTP(w, req.WithContext(context.WithValue(req.Context(), middleware.UserContextKey, uc)))
		})
	})
	h.RegisterRoutes(r)
	req := httptest.NewRequestWithContext(context.Background(), method, path, strings.NewReader(body))
	rec := httptest.NewRecorder()
	r.ServeHTTP(rec, req)
	return rec
}

// team builds a Team org with one user per role and returns their ids.
type team struct {
	f                                 *fakeStore
	org                               uuid.UUID
	owner, admin, member, billing, o2 uuid.UUID
}

func newTeam() team {
	f := newFake()
	t := team{f: f, org: f.org(models.OrgKindTeam, "Acme")}
	t.owner, t.admin, t.member, t.billing, t.o2 = f.user("owner@x.io"), f.user("admin@x.io"), f.user("member@x.io"), f.user("billing@x.io"), f.user("owner2@x.io")
	f.add(t.org, t.owner, models.OrgRoleOwner)
	f.add(t.org, t.admin, models.OrgRoleAdmin)
	f.add(t.org, t.member, models.OrgRoleMember)
	f.add(t.org, t.billing, models.OrgRoleBilling)
	return t
}

func (t team) p(suffix string) string { return "/api/v1/orgs/" + t.org.String() + suffix }

func want(t *testing.T, rec *httptest.ResponseRecorder, code int) {
	t.Helper()
	if rec.Code != code {
		t.Fatalf("status = %d, want %d (body %s)", rec.Code, code, rec.Body.String())
	}
}

// ---- tests ----

func TestListAndCreateOrgs(t *testing.T) {
	tm := newTeam()
	personal := tm.f.org(models.OrgKindPersonal, "Personal")
	tm.f.add(personal, tm.member, models.OrgRoleOwner)
	platform := tm.f.org(models.OrgKindPlatform, "Platform")
	tm.f.add(platform, tm.member, models.OrgRoleOwner)

	rec := tm.f.do(tm.member, "GET", "/api/v1/orgs", "")
	want(t, rec, 200)
	var list []OrgResponse
	_ = json.Unmarshal(rec.Body.Bytes(), &list)
	if len(list) != 2 {
		t.Fatalf("want personal + team (platform hidden), got %+v", list)
	}

	rec = tm.f.do(tm.member, "POST", "/api/v1/orgs", `{"name":"  New Co "}`)
	want(t, rec, 201)
	var created OrgResponse
	_ = json.Unmarshal(rec.Body.Bytes(), &created)
	if created.Role != "Owner" || created.Kind != "Team" || created.Name != "New Co" {
		t.Fatalf("created: %+v", created)
	}
	want(t, tm.f.do(tm.member, "POST", "/api/v1/orgs", `{"name":"   "}`), 400)
}

func TestCrossOrgIsNotFound(t *testing.T) {
	tm := newTeam()
	otherOrg := tm.f.org(models.OrgKindTeam, "Other")
	outsider := tm.f.user("out@x.io")
	tm.f.add(otherOrg, outsider, models.OrgRoleOwner)

	// Member of org A cannot list org B's members, read it, or touch invites: 404, never 403.
	for _, c := range []struct{ m, path, body string }{
		{"GET", "/members", ""},
		{"GET", "", ""},
		{"PATCH", "", `{"name":"x"}`},
		{"DELETE", "", ""},
		{"GET", "/invites", ""},
		{"POST", "/invites", `{"email":"a@b.co","role":"Member"}`},
		{"PATCH", "/members/" + tm.member.String(), `{"role":"Admin"}`},
		{"DELETE", "/members/" + tm.member.String(), ""},
	} {
		rec := tm.f.do(outsider, c.m, tm.p(c.path), c.body)
		if rec.Code != 404 {
			t.Errorf("%s %s: %d, want 404", c.m, c.path, rec.Code)
		}
	}
	want(t, tm.f.do(outsider, "GET", "/api/v1/orgs/not-a-uuid/members", ""), 404)
	want(t, tm.f.do(outsider, "GET", "/api/v1/orgs/"+uuid.NewString(), ""), 404)
}

func TestPlatformOrgIsNotFoundEvenForItsOwner(t *testing.T) {
	tm := newTeam()
	platform := tm.f.org(models.OrgKindPlatform, "Platform")
	tm.f.add(platform, tm.owner, models.OrgRoleOwner)
	want(t, tm.f.do(tm.owner, "GET", "/api/v1/orgs/"+platform.String()+"/members", ""), 404)
}

func TestGetOrgAnyMemberIncludingBilling(t *testing.T) {
	tm := newTeam()
	for _, u := range []uuid.UUID{tm.owner, tm.admin, tm.member, tm.billing} {
		want(t, tm.f.do(u, "GET", tm.p(""), ""), 200)
	}
}

func TestRoleMatrix(t *testing.T) {
	tm := newTeam()
	cases := []struct {
		name         string
		method, path string
		body         string
		as           uuid.UUID
		code         int
	}{
		{"owner lists members", "GET", "/members", "", tm.owner, 200},
		{"admin lists members", "GET", "/members", "", tm.admin, 200},
		{"member lists members", "GET", "/members", "", tm.member, 200},
		{"billing cannot list members", "GET", "/members", "", tm.billing, 403},

		{"admin renames", "PATCH", "", `{"name":"Acme 2"}`, tm.admin, 200},
		{"member cannot rename", "PATCH", "", `{"name":"x"}`, tm.member, 403},
		{"billing cannot rename", "PATCH", "", `{"name":"x"}`, tm.billing, 403},

		{"admin cannot delete org", "DELETE", "", "", tm.admin, 403},
		{"member cannot delete org", "DELETE", "", "", tm.member, 403},

		{"owner lists invites", "GET", "/invites", "", tm.owner, 200},
		{"admin lists invites", "GET", "/invites", "", tm.admin, 200},
		{"member cannot list invites", "GET", "/invites", "", tm.member, 403},
		{"billing cannot list invites", "GET", "/invites", "", tm.billing, 403},
		{"member cannot invite", "POST", "/invites", `{"email":"n@x.io","role":"Member"}`, tm.member, 403},
		{"billing cannot invite", "POST", "/invites", `{"email":"n@x.io","role":"Member"}`, tm.billing, 403},
		{"admin invites member", "POST", "/invites", `{"email":"n@x.io","role":"Member"}`, tm.admin, 201},
		{"admin cannot invite owner", "POST", "/invites", `{"email":"n2@x.io","role":"Owner"}`, tm.admin, 403},
		{"owner invites owner", "POST", "/invites", `{"email":"n3@x.io","role":"Owner"}`, tm.owner, 201},
		{"bad role", "POST", "/invites", `{"email":"n4@x.io","role":"Root"}`, tm.owner, 400},
		{"bad email", "POST", "/invites", `{"email":"nope","role":"Member"}`, tm.owner, 400},
		{"already a member", "POST", "/invites", `{"email":"MEMBER@x.io","role":"Member"}`, tm.owner, 409},

		{"member cannot change role", "PATCH", "/members/" + tm.billing.String(), `{"role":"Member"}`, tm.member, 403},
		{"admin sets member to billing", "PATCH", "/members/" + tm.member.String(), `{"role":"Billing"}`, tm.admin, 204},
		{"admin cannot grant owner", "PATCH", "/members/" + tm.billing.String(), `{"role":"Owner"}`, tm.admin, 403},
		{"admin cannot touch owner", "PATCH", "/members/" + tm.owner.String(), `{"role":"Member"}`, tm.admin, 403},
		{"owner grants owner", "PATCH", "/members/" + tm.billing.String(), `{"role":"Owner"}`, tm.owner, 204},
		{"bad member role", "PATCH", "/members/" + tm.admin.String(), `{"role":"Root"}`, tm.owner, 400},
		{"unknown member", "PATCH", "/members/" + uuid.NewString(), `{"role":"Member"}`, tm.owner, 404},

		{"admin cannot remove owner", "DELETE", "/members/" + tm.owner.String(), "", tm.admin, 403},
		{"billing cannot remove other", "DELETE", "/members/" + tm.admin.String(), "", tm.billing, 403},
		{"member cannot remove other", "DELETE", "/members/" + tm.admin.String(), "", tm.member, 403},
	}
	for _, c := range cases {
		// Each case starts from a fresh team so cases do not interact.
		fresh := newTeam()
		remap := map[uuid.UUID]uuid.UUID{tm.owner: fresh.owner, tm.admin: fresh.admin, tm.member: fresh.member, tm.billing: fresh.billing}
		path, body := c.path, c.body
		for old, nw := range remap {
			path = strings.ReplaceAll(path, old.String(), nw.String())
		}
		rec := fresh.f.do(remap[c.as], c.method, fresh.p(path), body)
		if rec.Code != c.code {
			t.Errorf("%s: %d, want %d (%s)", c.name, rec.Code, c.code, rec.Body.String())
		}
	}
}

// E8-UI: the workspace's compute preference is a workspace policy, so it follows
// the same rule as a rename (Owner/Admin), and a value this build does not know
// is refused rather than stored for the engine to misread.
func TestComputePreferenceIsAnAdminSetting(t *testing.T) {
	tm := newTeam()

	// A row written before the column was ever set reads as Auto, not as blank.
	rec := tm.f.do(tm.member, "GET", tm.p(""), "")
	want(t, rec, 200)
	var org OrgResponse
	_ = json.Unmarshal(rec.Body.Bytes(), &org)
	if org.ComputePreference != models.ComputePreferenceAuto {
		t.Fatalf("unset preference reads as %q, want Auto", org.ComputePreference)
	}

	rec = tm.f.do(tm.owner, "PATCH", tm.p(""), `{"computePreference":"PreferRunner"}`)
	want(t, rec, 200)
	_ = json.Unmarshal(rec.Body.Bytes(), &org)
	if org.ComputePreference != models.ComputePreferencePreferRunner {
		t.Fatalf("after PATCH: %q, want PreferRunner", org.ComputePreference)
	}
	if tm.f.orgs[tm.org].Name != "Acme" {
		t.Fatalf("a compute-only PATCH renamed the org to %q", tm.f.orgs[tm.org].Name)
	}

	// An Admin may set it too (the same role that may rename); a Member may not.
	want(t, tm.f.do(tm.admin, "PATCH", tm.p(""), `{"computePreference":"RunnerOnly"}`), 200)
	want(t, tm.f.do(tm.member, "PATCH", tm.p(""), `{"computePreference":"CloudOnly"}`), 403)
	want(t, tm.f.do(tm.billing, "PATCH", tm.p(""), `{"computePreference":"CloudOnly"}`), 403)

	// Unknown values are refused, and a refused call changes nothing.
	for _, bad := range []string{"RunnerFirst", "", "auto", "Cloud"} {
		rec = tm.f.do(tm.owner, "PATCH", tm.p(""), `{"computePreference":"`+bad+`"}`)
		want(t, rec, 400)
	}
	if got := tm.f.orgs[tm.org].ComputePreference; got != models.ComputePreferenceRunnerOnly {
		t.Fatalf("a refused PATCH wrote %q", got)
	}

	// An empty body is still "nothing to update".
	want(t, tm.f.do(tm.owner, "PATCH", tm.p(""), `{}`), 400)

	// A name and a preference in one call both land.
	rec = tm.f.do(tm.owner, "PATCH", tm.p(""), `{"name":"Acme Two","computePreference":"CloudOnly"}`)
	want(t, rec, 200)
	if tm.f.orgs[tm.org].Name != "Acme Two" || tm.f.orgs[tm.org].ComputePreference != models.ComputePreferenceCloudOnly {
		t.Fatalf("combined PATCH: %+v", tm.f.orgs[tm.org])
	}
}

func TestDeleteOrgByOwnerEvictsAllMembers(t *testing.T) {
	tm := newTeam()
	want(t, tm.f.do(tm.owner, "DELETE", tm.p(""), ""), 204)
	if len(tm.f.evicted) != 4 {
		t.Fatalf("evicted %d memberships, want 4", len(tm.f.evicted))
	}
	want(t, tm.f.do(tm.owner, "GET", tm.p(""), ""), 404)
}

func TestPersonalOrgRules(t *testing.T) {
	f := newFake()
	u := f.user("solo@x.io")
	p := f.org(models.OrgKindPersonal, "Personal")
	f.add(p, u, models.OrgRoleOwner)
	base := "/api/v1/orgs/" + p.String()

	want(t, f.do(u, "DELETE", base, ""), 400)
	want(t, f.do(u, "POST", base+"/invites", `{"email":"a@b.co","role":"Member"}`), 400)
	want(t, f.do(u, "DELETE", base+"/members/"+u.String(), ""), 400) // cannot leave
	want(t, f.do(u, "PATCH", base+"/members/"+u.String(), `{"role":"Member"}`), 400)
	want(t, f.do(u, "GET", base, ""), 200)
	want(t, f.do(u, "GET", base+"/members", ""), 200)
}

func TestLastOwnerGuard(t *testing.T) {
	tm := newTeam()
	// Sole owner cannot demote or remove themselves.
	want(t, tm.f.do(tm.owner, "PATCH", tm.p("/members/"+tm.owner.String()), `{"role":"Admin"}`), 409)
	want(t, tm.f.do(tm.owner, "DELETE", tm.p("/members/"+tm.owner.String()), ""), 409)

	// With a second owner either may leave / be demoted.
	tm.f.add(tm.org, tm.o2, models.OrgRoleOwner)
	want(t, tm.f.do(tm.owner, "PATCH", tm.p("/members/"+tm.o2.String()), `{"role":"Admin"}`), 204)
	// owner2 is now Admin: the original owner is the last again.
	want(t, tm.f.do(tm.admin, "DELETE", tm.p("/members/"+tm.owner.String()), ""), 403)
	want(t, tm.f.do(tm.owner, "DELETE", tm.p("/members/"+tm.owner.String()), ""), 409)
}

func TestStoreLevelGuardMapsTo409(t *testing.T) {
	// The handler pre-check passes (two owners), but the store loses a race and
	// reports ErrLastOwner: still a clean 409.
	tm := newTeam()
	tm.f.add(tm.org, tm.o2, models.OrgRoleOwner)
	racy := &racyStore{fakeStore: tm.f}
	h := &Handler{Store: racy, Lookup: tm.f, Mailer: func() services.Mailer { return tm.f },
		Invalidate: func(uuid.UUID, uuid.UUID) {}, Now: time.Now, BaseURL: func() string { return "" }}
	r := mux.NewRouter()
	r.Use(func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
			uc := middleware.UserContext{UserID: tm.owner.String()}
			next.ServeHTTP(w, req.WithContext(context.WithValue(req.Context(), middleware.UserContextKey, uc)))
		})
	})
	h.RegisterRoutes(r)
	rec := httptest.NewRecorder()
	r.ServeHTTP(rec, httptest.NewRequestWithContext(context.Background(), "DELETE", tm.p("/members/"+tm.o2.String()), nil))
	want(t, rec, 409)
}

type racyStore struct{ *fakeStore }

func (racyStore) RemoveMember(context.Context, uuid.UUID, uuid.UUID) error {
	return services.ErrLastOwner
}

func TestLeaveAndRemoveEvictCache(t *testing.T) {
	tm := newTeam()
	// Any role may leave, including Billing and Member.
	want(t, tm.f.do(tm.billing, "DELETE", tm.p("/members/"+tm.billing.String()), ""), 204)
	want(t, tm.f.do(tm.member, "DELETE", tm.p("/members/"+tm.member.String()), ""), 204)
	// Admin removes... nobody left but owner/admin; owner removes admin.
	want(t, tm.f.do(tm.owner, "DELETE", tm.p("/members/"+tm.admin.String()), ""), 204)
	if len(tm.f.evicted) != 3 {
		t.Fatalf("evictions = %d, want 3", len(tm.f.evicted))
	}
	for _, e := range tm.f.evicted {
		if e[1] != tm.org {
			t.Fatal("evicted wrong org")
		}
		if _, still := tm.f.members[tm.org][e[0]]; still {
			t.Fatal("evicted but still a member")
		}
	}
	// Removed user now gets 404 (no longer a member).
	want(t, tm.f.do(tm.member, "GET", tm.p("/members"), ""), 404)
}

func TestRoleChangeEvicts(t *testing.T) {
	tm := newTeam()
	want(t, tm.f.do(tm.owner, "PATCH", tm.p("/members/"+tm.member.String()), `{"role":"Admin"}`), 204)
	if len(tm.f.evicted) != 1 || tm.f.evicted[0] != [2]uuid.UUID{tm.member, tm.org} {
		t.Fatalf("evicted: %v", tm.f.evicted)
	}
	// Failed requests must not evict.
	tm.f.evicted = nil
	want(t, tm.f.do(tm.billing, "PATCH", tm.p("/members/"+tm.admin.String()), `{"role":"Member"}`), 403)
	if len(tm.f.evicted) != 0 {
		t.Fatal("evicted on a rejected request")
	}
}

func inviteToken(t *testing.T, tm team) string {
	t.Helper()
	if len(tm.f.sent) == 0 {
		t.Fatal("no invite mail")
	}
	link := tm.f.sent[len(tm.f.sent)-1].Link
	const prefix = "https://app.example/app/invite/"
	if !strings.HasPrefix(link, prefix) {
		t.Fatalf("link %q", link)
	}
	return strings.TrimPrefix(link, prefix)
}

func TestInviteCreate_StoresOnlyHashAndNeverReturnsToken(t *testing.T) {
	tm := newTeam()
	rec := tm.f.do(tm.owner, "POST", tm.p("/invites"), `{"email":" New@X.io ","role":"Member"}`)
	want(t, rec, 201)
	token := inviteToken(t, tm)
	if strings.Contains(rec.Body.String(), token) {
		t.Fatal("response leaks the invite token")
	}
	if len(token) != 43 { // 32 bytes, base64url without padding
		t.Fatalf("token length %d", len(token))
	}
	inv := tm.f.invites[0]
	if inv.TokenHash != HashInviteToken(token) || inv.TokenHash == token || len(inv.TokenHash) != 64 {
		t.Fatalf("stored hash wrong: %q", inv.TokenHash)
	}
	if inv.Email != "new@x.io" || inv.ExpiresAt.Sub(inv.CreatedAt) != 7*24*time.Hour {
		t.Fatalf("invite: %+v", inv)
	}
	if tm.f.sentTo[0] != "new@x.io" || tm.f.sent[0].OrgName != "Acme" || tm.f.sent[0].Role != "Member" || tm.f.sent[0].InviterName != "owner" {
		t.Fatalf("mail: %+v to %v", tm.f.sent[0], tm.f.sentTo)
	}
}

func TestInviteCreate_RefusesServiceAccountDomain(t *testing.T) {
	tm := newTeam()
	want(t, tm.f.do(tm.owner, "POST", tm.p("/invites"), `{"email":"apikey+abc@service.invalid","role":"Member"}`), 400)
	if len(tm.f.invites) != 0 || len(tm.f.sent) != 0 {
		t.Fatalf("invited a service-account address: invites=%+v sent=%+v", tm.f.invites, tm.f.sent)
	}
}

func TestInviteReinviteRevokesOldAndRevokeEndpoint(t *testing.T) {
	tm := newTeam()
	want(t, tm.f.do(tm.owner, "POST", tm.p("/invites"), `{"email":"n@x.io","role":"Member"}`), 201)
	want(t, tm.f.do(tm.owner, "POST", tm.p("/invites"), `{"email":"n@x.io","role":"Admin"}`), 201)

	rec := tm.f.do(tm.admin, "GET", tm.p("/invites"), "")
	want(t, rec, 200)
	var list []InviteResponse
	_ = json.Unmarshal(rec.Body.Bytes(), &list)
	if len(list) != 1 || list[0].Role != "Admin" {
		t.Fatalf("pending: %+v", list)
	}
	want(t, tm.f.do(tm.member, "DELETE", tm.p("/invites/"+list[0].ID), ""), 403)
	want(t, tm.f.do(tm.admin, "DELETE", tm.p("/invites/"+list[0].ID), ""), 204)
	want(t, tm.f.do(tm.admin, "DELETE", tm.p("/invites/"+list[0].ID), ""), 404) // already revoked
	want(t, tm.f.do(tm.admin, "DELETE", tm.p("/invites/nope"), ""), 404)
}

func TestAcceptInvite(t *testing.T) {
	tm := newTeam()
	invitee := tm.f.user("new@x.io")
	stranger := tm.f.user("stranger@x.io")
	want(t, tm.f.do(tm.owner, "POST", tm.p("/invites"), `{"email":"new@x.io","role":"Billing"}`), 201)
	token := inviteToken(t, tm)
	body := `{"token":"` + token + `"}`

	// Wrong email: 403 and the invite is NOT consumed.
	want(t, tm.f.do(stranger, "POST", "/api/v1/orgs/invites/accept", body), 403)
	if _, ok := tm.f.members[tm.org][stranger]; ok {
		t.Fatal("stranger joined")
	}

	rec := tm.f.do(invitee, "POST", "/api/v1/orgs/invites/accept", body)
	want(t, rec, 200)
	var resp AcceptInviteResponse
	_ = json.Unmarshal(rec.Body.Bytes(), &resp)
	if resp.OrganizationID != tm.org.String() || resp.Role != "Billing" || tm.f.members[tm.org][invitee] != "Billing" {
		t.Fatalf("accept: %+v", resp)
	}
	if len(tm.f.evicted) == 0 || tm.f.evicted[len(tm.f.evicted)-1] != [2]uuid.UUID{invitee, tm.org} {
		t.Fatal("membership cache not evicted for the new member")
	}
	// Single use.
	want(t, tm.f.do(invitee, "POST", "/api/v1/orgs/invites/accept", body), 404)
}

func TestAcceptInviteFailures(t *testing.T) {
	tm := newTeam()
	invitee := tm.f.user("new@x.io")
	post := func(tok string) *httptest.ResponseRecorder {
		return tm.f.do(invitee, "POST", "/api/v1/orgs/invites/accept", `{"token":"`+tok+`"}`)
	}
	want(t, post(""), 400)
	want(t, post("garbage"), 404)

	// Expired.
	want(t, tm.f.do(tm.owner, "POST", tm.p("/invites"), `{"email":"new@x.io","role":"Member"}`), 201)
	token := inviteToken(t, tm)
	tm.f.now = tm.f.now.Add(7*24*time.Hour + time.Second)
	want(t, post(token), 410)

	// Revoked.
	tm.f.now = tm.f.now.Add(-8 * 24 * time.Hour)
	want(t, tm.f.do(tm.owner, "POST", tm.p("/invites"), `{"email":"new@x.io","role":"Member"}`), 201)
	tok2 := inviteToken(t, tm)
	var id string
	for _, i := range tm.f.invites {
		if i.RevokedAt == nil {
			id = i.ID.String()
		}
	}
	want(t, tm.f.do(tm.owner, "DELETE", tm.p("/invites/"+id), ""), 204)
	want(t, post(tok2), 404)

	// Org deleted after the invite was sent.
	want(t, tm.f.do(tm.owner, "POST", tm.p("/invites"), `{"email":"new@x.io","role":"Member"}`), 201)
	tok3 := inviteToken(t, tm)
	want(t, tm.f.do(tm.owner, "DELETE", tm.p(""), ""), 204)
	want(t, post(tok3), 404)
}

func TestAcceptInviteAlreadyMemberKeepsRole(t *testing.T) {
	tm := newTeam()
	// Invite created for an email that later becomes a member some other way.
	other := tm.f.user("late@x.io")
	want(t, tm.f.do(tm.owner, "POST", tm.p("/invites"), `{"email":"late@x.io","role":"Billing"}`), 201)
	token := inviteToken(t, tm)
	tm.f.add(tm.org, other, models.OrgRoleAdmin)
	rec := tm.f.do(other, "POST", "/api/v1/orgs/invites/accept", `{"token":"`+token+`"}`)
	want(t, rec, 200)
	if tm.f.members[tm.org][other] != "Admin" || !strings.Contains(rec.Body.String(), `"role":"Admin"`) {
		t.Fatalf("role changed: %s", rec.Body.String())
	}
}

func TestRolePredicates(t *testing.T) {
	for role, want := range map[string][3]bool{ // manage, view, assignOwner-over-member
		"Owner": {true, true, true}, "Admin": {true, true, false},
		"Member": {false, true, false}, "Billing": {false, false, false},
	} {
		if CanManageMembers(role) != want[0] || CanViewMembers(role) != want[1] || CanAssign(role, "Member", "Owner") != want[2] {
			t.Errorf("%s predicates wrong", role)
		}
	}
	if CanAssign("Admin", "Owner", "Member") || !CanAssign("Admin", "Member", "Billing") {
		t.Error("admin vs owner target")
	}
	if ValidRole("Root") || !ValidRole("Billing") {
		t.Error("ValidRole")
	}
}

// F-4: a failed invitation mail is logged by reference with the SMTP error sanitised.
func TestInviteMailFailureLogsNoAddress(t *testing.T) {
	tm := newTeam()
	tm.f.sendErr = &textproto.Error{Code: 550, Msg: "5.1.1 <victim@x.io>: user unknown"}
	var buf bytes.Buffer
	old := log.Writer()
	log.SetOutput(&buf)
	defer log.SetOutput(old)

	want(t, tm.f.do(tm.owner, "POST", tm.p("/invites"), `{"email":"victim@x.io","role":"Member"}`), 201)
	if out := buf.String(); strings.Contains(out, "@") || !strings.Contains(out, "rcpt=") || !strings.Contains(out, "550") {
		t.Fatalf("log: %s", out)
	}
}
