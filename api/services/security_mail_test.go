package services

import (
	"context"
	"strings"
	"testing"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/models"
)

func TestSecurityMailRouting(t *testing.T) {
	org, owner, outsider := uuid.New(), uuid.New(), uuid.New()
	str := func(u uuid.UUID) *string { s := u.String(); return &s }
	bad := "not-a-uuid"
	cases := []struct {
		name      string
		typ       string
		recipient *string
		want      int
		tmpl      TemplateName
		link      string
	}{
		{"key to recipient", models.NotificationAPIKeyExpiring, str(owner), 1, TemplateApiKeyExpiring, "/app/org/api-keys"},
		{"webhook to recipient", models.NotificationWebhookDisabled, str(owner), 1, TemplateWebhookDisabled, "/app/org/api-keys?tab=webhooks"},
		{"non-member skipped", models.NotificationAPIKeyExpiring, str(outsider), 0, "", ""},
		{"unparseable skipped", models.NotificationWebhookDisabled, &bad, 0, "", ""},
		{"missing skipped", models.NotificationAPIKeyExpiring, nil, 0, "", ""},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			store := newFakeNotifStore()
			store.members[org] = []uuid.UUID{owner}
			for _, u := range []uuid.UUID{owner, outsider} {
				store.users[u] = [2]string{u.String() + "@example.com", "U"}
			}
			// every preference off: the mail must still go out
			store.prefs[owner] = NotificationPreferences{}
			var sent []sentMail
			d := buildDispatcher(store, &sent, nil)
			d.HandleActivityEvent(context.Background(), UserActivityEvent{
				OrganizationID: org.String(), ProjectID: uuid.Nil.String(), Type: c.typ,
				Subject: "ci-key", Details: "2026-12-01T10:00:00Z", RecipientUserID: c.recipient,
			})
			if len(sent) != c.want {
				t.Fatalf("want %d mails, got %d", c.want, len(sent))
			}
			if c.want == 1 {
				if sent[0].to != owner.String()+"@example.com" || sent[0].tmpl != c.tmpl {
					t.Errorf("unexpected mail: %+v", sent[0])
				}
				if sent[0].data.Link != "https://reelbolt.ai"+c.link {
					t.Errorf("link = %q", sent[0].data.Link)
				}
			}
		})
	}
}

func TestSecurityMailTemplatesRenderInBothLocales(t *testing.T) {
	m := NewSMTPMailer(MailerConfig{PublicBaseURL: "https://reelbolt.ai"})
	for _, loc := range []string{"en", "it"} {
		for _, tm := range []TemplateName{TemplateApiKeyExpiring, TemplateWebhookDisabled} {
			mail, err := m.Render("a@example.com", tm, MailData{
				Name: "Ann", OrgName: "Acme", FileName: "<script>x</script>", ExpiryDate: "2026-12-01 10:00 UTC",
				ErrorDetail: "No successful delivery for 72 hours", Link: "https://reelbolt.ai/app/org/api-keys",
				FooterNote: "alpha", Locale: loc,
			})
			if err != nil {
				t.Fatalf("%s/%s: %v", loc, tm, err)
			}
			if strings.Contains(mail.HTML, "<script>") || !strings.Contains(mail.HTML, "Acme") || mail.Subject == "" {
				t.Errorf("%s/%s bad render", loc, tm)
			}
		}
	}
}
