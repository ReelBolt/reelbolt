package services

import (
	"context"
	"strings"
	"testing"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/models"
)

func TestReviewActivityRouting(t *testing.T) {
	org, actor, owner, outsider := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	project := uuid.New()
	str := func(u uuid.UUID) *string { s := u.String(); return &s }
	empty := ""

	cases := []struct {
		name      string
		recipient *string
		actor     string
		want      int
	}{
		{"recipient only", str(owner), "", 1},
		{"actor excluded", str(actor), actor.String(), 0},
		{"non-member skipped", str(outsider), "", 0},
		{"missing recipient skipped", nil, "", 0},
		{"empty recipient skipped", &empty, "", 0},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			store := newFakeNotifStore()
			store.members[org] = []uuid.UUID{actor, owner}
			for _, u := range []uuid.UUID{actor, owner, outsider} {
				store.users[u] = [2]string{u.String() + "@example.com", "U"}
			}
			store.projects[project] = "Q3"
			var sent []sentMail
			d := buildDispatcher(store, &sent, nil)
			d.HandleActivityEvent(context.Background(), UserActivityEvent{
				OrganizationID: org.String(), ActorUserID: c.actor, ProjectID: project.String(),
				Type: models.NotificationReviewActivity, Subject: "Cut v2", Details: "Trim the intro",
				RecipientUserID: c.recipient,
			})
			if len(sent) != c.want {
				t.Fatalf("want %d mails, got %d: %+v", c.want, len(sent), sent)
			}
			if c.want == 1 {
				if sent[0].to != owner.String()+"@example.com" || sent[0].tmpl != TemplateReviewActivity {
					t.Errorf("unexpected mail: %+v", sent[0])
				}
			}
		})
	}
}

func TestReviewActivityPreference(t *testing.T) {
	cases := []struct {
		name  string
		prefs NotificationPreferences
		cat   string
		want  bool
	}{
		{"default on", DefaultNotificationPreferences(), models.NotificationReviewActivity, true},
		{"off suppresses", NotificationPreferences{JobSucceeded: true, JobFailed: true, FileCreated: true, GraphicsEdited: true}, models.NotificationReviewActivity, false},
		{"others unaffected when off", NotificationPreferences{JobSucceeded: true, JobFailed: true, FileCreated: true, GraphicsEdited: true}, models.NotificationFileCreated, true},
		{"review on, file off", NotificationPreferences{ReviewActivity: true}, models.NotificationFileCreated, false},
		{"review on, file off: review", NotificationPreferences{ReviewActivity: true}, models.NotificationReviewActivity, true},
	}
	for _, c := range cases {
		if got := c.prefs.Enabled(c.cat); got != c.want {
			t.Errorf("%s: Enabled(%s) = %v, want %v", c.name, c.cat, got, c.want)
		}
	}
}

func TestReviewActivityOffSendsNothing(t *testing.T) {
	org, owner, project := uuid.New(), uuid.New(), uuid.New()
	for _, on := range []bool{true, false} {
		store := newFakeNotifStore()
		store.members[org] = []uuid.UUID{owner}
		store.users[owner] = [2]string{"o@example.com", "O"}
		store.projects[project] = "Q3"
		p := DefaultNotificationPreferences()
		p.ReviewActivity = on
		store.prefs[owner] = p
		var sent []sentMail
		d := buildDispatcher(store, &sent, nil)
		rid := owner.String()
		d.HandleActivityEvent(context.Background(), UserActivityEvent{
			OrganizationID: org.String(), ProjectID: project.String(),
			Type: models.NotificationReviewActivity, Subject: "Cut", Details: "x", RecipientUserID: &rid,
		})
		want := 0
		if on {
			want = 1
		}
		if len(sent) != want {
			t.Errorf("reviewActivity=%v: want %d mails, got %d", on, want, len(sent))
		}
	}
}

func TestReviewActivityTemplateEscapesAndSubject(t *testing.T) {
	data := MailData{
		Name: "<b>Ann</b>", OrgName: "Acme", ProjectName: "Q3 <script>x</script>",
		FileName: "Cut <script>alert(1)</script>", ErrorDetail: "<script>bad()</script>",
		Link: "https://reelbolt.ai/app/projects/p", FooterNote: "alpha",
	}
	mail, err := NewSMTPMailer(MailerConfig{PublicBaseURL: "https://reelbolt.ai"}).Render("a@example.com", TemplateReviewActivity, data)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(mail.HTML, "<script>") {
		t.Errorf("html not escaped: %s", mail.HTML)
	}
	if !strings.Contains(mail.HTML, "&lt;script&gt;") {
		t.Error("expected escaped script text in html")
	}
	if want := "New review activity on Cut <script>alert(1)</script>"; mail.Subject != want {
		t.Errorf("subject = %q, want %q", mail.Subject, want)
	}
}
