package services

import (
	"errors"
	"testing"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/models"
)

type fakeWriter struct {
	orgs        []models.Organization
	memberships []models.OrganizationMembership
	lastActive  uuid.UUID
	deleted     []models.OrganizationMembership
	failOn      string
}

func (f *fakeWriter) Create(v any) error {
	switch x := v.(type) {
	case *models.Organization:
		if f.failOn == "org" {
			return errors.New("boom")
		}
		f.orgs = append(f.orgs, *x)
	case *models.OrganizationMembership:
		if f.failOn == "membership" {
			return errors.New("boom")
		}
		f.memberships = append(f.memberships, *x)
	}
	return nil
}

func (f *fakeWriter) SetLastActiveOrganization(_, orgID uuid.UUID) error {
	f.lastActive = orgID
	return nil
}

func (f *fakeWriter) DeleteMembership(orgID, userID uuid.UUID) error {
	f.deleted = append(f.deleted, models.OrganizationMembership{OrganizationID: orgID, UserID: userID})
	return nil
}

func (f *fakeWriter) EnsureMembership(m *models.OrganizationMembership) error {
	f.memberships = append(f.memberships, *m)
	return nil
}

func (f *fakeWriter) role(org uuid.UUID) string {
	for _, m := range f.memberships {
		if m.OrganizationID == org {
			return m.Role
		}
	}
	return ""
}

func TestProvision_SelfHostedMember(t *testing.T) {
	w := &fakeWriter{}
	u := &models.ApplicationUser{ID: uuid.New()}
	if err := provisionTenancy(w, u, false); err != nil {
		t.Fatal(err)
	}
	if len(w.orgs) != 1 || w.orgs[0].Kind != models.OrgKindPersonal || w.orgs[0].CreatedByUserID == nil || *w.orgs[0].CreatedByUserID != u.ID {
		t.Fatalf("orgs: %+v", w.orgs)
	}
	if w.orgs[0].Slug != PersonalOrgSlug(u.ID) {
		t.Fatalf("slug %q", w.orgs[0].Slug)
	}
	if w.role(w.orgs[0].ID) != models.OrgRoleOwner {
		t.Fatal("personal org owner membership missing")
	}
	if w.role(models.DefaultOrgID) != models.OrgRoleMember {
		t.Fatalf("default role %q", w.role(models.DefaultOrgID))
	}
	if w.role(models.PlatformOrgID) != "" {
		t.Fatal("non-admin must not join Platform")
	}
	if w.lastActive != w.orgs[0].ID || u.LastActiveOrganizationID == nil || *u.LastActiveOrganizationID != w.orgs[0].ID {
		t.Fatal("last active org must be the Personal org")
	}
}

func TestProvision_SelfHostedAdmin(t *testing.T) {
	w := &fakeWriter{}
	u := &models.ApplicationUser{ID: uuid.New(), IsAdmin: true}
	if err := provisionTenancy(w, u, false); err != nil {
		t.Fatal(err)
	}
	if w.role(models.DefaultOrgID) != models.OrgRoleOwner {
		t.Fatalf("admin default role %q", w.role(models.DefaultOrgID))
	}
	if w.role(models.PlatformOrgID) != models.OrgRoleOwner {
		t.Fatal("admin must own Platform")
	}
}

func TestProvision_CloudSkipsDefault(t *testing.T) {
	w := &fakeWriter{}
	u := &models.ApplicationUser{ID: uuid.New()}
	if err := provisionTenancy(w, u, true); err != nil {
		t.Fatal(err)
	}
	if w.role(models.DefaultOrgID) != "" {
		t.Fatal("Cloud mode must not add a Default membership")
	}
	if len(w.memberships) != 1 || w.role(w.orgs[0].ID) != models.OrgRoleOwner {
		t.Fatalf("memberships: %+v", w.memberships)
	}
}

func TestProvision_PropagatesErrors(t *testing.T) {
	for _, fail := range []string{"org", "membership"} {
		w := &fakeWriter{failOn: fail}
		if err := provisionTenancy(w, &models.ApplicationUser{ID: uuid.New()}, false); err == nil {
			t.Fatalf("%s failure was swallowed", fail)
		}
	}
}

func TestSyncPlatformMembership(t *testing.T) {
	id := uuid.New()
	w := &fakeWriter{}
	if err := syncPlatformMembership(w, id, true); err != nil {
		t.Fatal(err)
	}
	if w.role(models.PlatformOrgID) != models.OrgRoleOwner {
		t.Fatal("promotion must add Platform Owner")
	}
	if err := syncPlatformMembership(w, id, false); err != nil {
		t.Fatal(err)
	}
	if len(w.deleted) != 1 || w.deleted[0].OrganizationID != models.PlatformOrgID || w.deleted[0].UserID != id {
		t.Fatalf("demotion must remove Platform membership: %+v", w.deleted)
	}
}
