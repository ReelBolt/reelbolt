package services

import (
	"context"
	"strings"
	"testing"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/models"
)

// The 7-day avatar retention warning (docs/design/avatars.md): one recipient, the
// member who attested the consent, only while a member and only while the
// avatar_retention switch is on (default on).
func TestAvatarRetentionRouting(t *testing.T) {
	org, attester, outsider, project := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	str := func(u uuid.UUID) *string { s := u.String(); return &s }
	cases := []struct {
		name      string
		recipient *string
		prefsOff  bool
		want      int
	}{
		{"attester mailed", str(attester), false, 1},
		{"non-member skipped", str(outsider), false, 0},
		{"missing recipient skipped", nil, false, 0},
		{"switch off sends nothing", str(attester), true, 0},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			store := newFakeNotifStore()
			store.members[org] = []uuid.UUID{attester}
			for _, u := range []uuid.UUID{attester, outsider} {
				store.users[u] = [2]string{u.String() + "@example.com", "U"}
			}
			if c.prefsOff {
				p := DefaultNotificationPreferences()
				p.AvatarRetention = false
				store.prefs[attester] = p
			}
			store.projects[project] = "Q3"
			var sent []sentMail
			d := buildDispatcher(store, &sent, nil)
			d.HandleActivityEvent(context.Background(), UserActivityEvent{
				OrganizationID: org.String(), ProjectID: project.String(),
				Type: models.NotificationAvatarRetention, Subject: "Anna", Details: "Deleted on 2026-11-12",
				RecipientUserID: c.recipient,
			})
			if len(sent) != c.want {
				t.Fatalf("want %d mails, got %d: %+v", c.want, len(sent), sent)
			}
			if c.want == 1 && (sent[0].to != attester.String()+"@example.com" || sent[0].tmpl != TemplateAvatarRetention) {
				t.Errorf("unexpected mail: %+v", sent[0])
			}
		})
	}
	if !DefaultNotificationPreferences().Enabled(models.NotificationAvatarRetention) {
		t.Error("avatar_retention must default on")
	}
}

// A workspace avatar library item's warning (docs/design/avatar-objects.md) is the
// same category with the nil project id: it is mailed without a project name and
// links to the workspace's avatar library page.
func TestAvatarLibraryRetentionWarningNamesNoProject(t *testing.T) {
	org, admin := uuid.New(), uuid.New()
	store := newFakeNotifStore()
	store.members[org] = []uuid.UUID{admin}
	store.users[admin] = [2]string{admin.String() + "@example.com", "U"}
	var sent []sentMail
	d := buildDispatcher(store, &sent, nil)
	recipient := admin.String()
	d.HandleActivityEvent(context.Background(), UserActivityEvent{
		OrganizationID: org.String(), ProjectID: uuid.Nil.String(),
		Type: models.NotificationAvatarRetention, Subject: "Anna", Details: "Deleted on 2026-11-12 (UTC).",
		RecipientUserID: &recipient,
	})
	if len(sent) != 1 {
		t.Fatalf("want 1 mail, got %d: %+v", len(sent), sent)
	}
	if sent[0].tmpl != TemplateAvatarRetention || sent[0].data.ProjectName != "" {
		t.Errorf("unexpected mail: %+v", sent[0])
	}
	if !strings.HasSuffix(sent[0].data.Link, "/app/settings/avatar-library") {
		t.Errorf("a library warning links to the workspace library page, got %q", sent[0].data.Link)
	}
}

// social_publishing: one recipient, member only, switch honoured, no project id needed.
func TestSocialPublishingRouting(t *testing.T) {
	org, maker, outsider := uuid.New(), uuid.New(), uuid.New()
	str := func(u uuid.UUID) *string { s := u.String(); return &s }
	cases := []struct {
		name      string
		recipient *string
		prefsOff  bool
		want      int
	}{
		{"maker mailed", str(maker), false, 1},
		{"non-member skipped", str(outsider), false, 0},
		{"missing recipient skipped", nil, false, 0},
		{"switch off sends nothing", str(maker), true, 0},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			store := newFakeNotifStore()
			store.members[org] = []uuid.UUID{maker}
			for _, u := range []uuid.UUID{maker, outsider} {
				store.users[u] = [2]string{u.String() + "@example.com", "U"}
			}
			if c.prefsOff {
				p := DefaultNotificationPreferences()
				p.SocialPublishing = false
				store.prefs[maker] = p
			}
			var sent []sentMail
			d := buildDispatcher(store, &sent, nil)
			d.HandleActivityEvent(context.Background(), UserActivityEvent{
				OrganizationID: org.String(), Type: models.NotificationSocialPublishing,
				Subject: "Account needs reconnecting: YouTube", Details: "Reconnect it to keep posting.",
				RecipientUserID: c.recipient,
			})
			if len(sent) != c.want {
				t.Fatalf("want %d mails, got %d: %+v", c.want, len(sent), sent)
			}
		})
	}
}
