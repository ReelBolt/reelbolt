package services

import (
	"context"
	"errors"
	"net/textproto"
	"strings"
	"testing"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/vecchiotom/reelbolt/models"
)

// fakeNotifStore is a NotificationStore the tests drive: it returns the
// configured rows and records what the dispatcher asked of it.
type fakeNotifStore struct {
	prefs    map[uuid.UUID]NotificationPreferences
	users    map[uuid.UUID][2]string // [email, displayName]
	orgNames map[uuid.UUID]string
	members  map[uuid.UUID][]uuid.UUID
	owners   map[uuid.UUID]uuid.UUID
	execs    map[uuid.UUID]*models.WorkflowExecution
	projects map[uuid.UUID]string

	savedPrefs map[uuid.UUID]NotificationPreferences
}

func newFakeNotifStore() *fakeNotifStore {
	return &fakeNotifStore{
		prefs:      map[uuid.UUID]NotificationPreferences{},
		users:      map[uuid.UUID][2]string{},
		orgNames:   map[uuid.UUID]string{},
		members:    map[uuid.UUID][]uuid.UUID{},
		owners:     map[uuid.UUID]uuid.UUID{},
		execs:      map[uuid.UUID]*models.WorkflowExecution{},
		projects:   map[uuid.UUID]string{},
		savedPrefs: map[uuid.UUID]NotificationPreferences{},
	}
}

func (f *fakeNotifStore) GetPreferences(_ context.Context, userID uuid.UUID) (NotificationPreferences, error) {
	if p, ok := f.prefs[userID]; ok {
		return p, nil
	}
	return DefaultNotificationPreferences(), nil
}

func (f *fakeNotifStore) SavePreferences(_ context.Context, userID uuid.UUID, prefs NotificationPreferences) error {
	f.savedPrefs[userID] = prefs
	return nil
}

func (f *fakeNotifStore) RecordDelivery(_ context.Context, _ NotificationRecord) error { return nil }

func (f *fakeNotifStore) ListRecent(_ context.Context, _ uuid.UUID, _ int) ([]NotificationRecord, error) {
	return nil, nil
}

func (f *fakeNotifStore) LookupUser(_ context.Context, userID uuid.UUID) (string, string, bool, error) {
	u, ok := f.users[userID]
	return u[0], u[1], ok, nil
}

func (f *fakeNotifStore) OrgName(_ context.Context, orgID uuid.UUID) (string, error) {
	return f.orgNames[orgID], nil
}

func (f *fakeNotifStore) OrgMembers(_ context.Context, orgID uuid.UUID) ([]uuid.UUID, error) {
	return f.members[orgID], nil
}

func (f *fakeNotifStore) OrgOwner(_ context.Context, orgID uuid.UUID) (uuid.UUID, error) {
	return f.owners[orgID], nil
}

func (f *fakeNotifStore) WorkflowExecution(_ context.Context, id uuid.UUID) (*models.WorkflowExecution, error) {
	return f.execs[id], nil
}

func (f *fakeNotifStore) ProjectName(_ context.Context, id uuid.UUID) (string, error) {
	return f.projects[id], nil
}

// sentMail records one mail the dispatcher sent.
type sentMail struct {
	to   string
	tmpl TemplateName
	data MailData
}

// buildDispatcher wires the dispatcher over a fake store and a recording mailer.
func buildDispatcher(store *fakeNotifStore, sent *[]sentMail, failSend error) *NotificationDispatcherImpl {
	return &NotificationDispatcherImpl{
		store: store,
		send: func(to string, tmpl TemplateName, data MailData) error {
			*sent = append(*sent, sentMail{to: to, tmpl: tmpl, data: data})
			return failSend
		},
		base:   func() string { return "https://reelbolt.ai" },
		record: func(context.Context, NotificationRecord) {},
	}
}

func TestPreferencesDefaultAllOn(t *testing.T) {
	p := DefaultNotificationPreferences()
	for _, c := range []string{
		models.NotificationJobSucceeded, models.NotificationJobFailed,
		models.NotificationFileCreated, models.NotificationGraphicsEdited,
	} {
		if !p.Enabled(c) {
			t.Errorf("default should enable %s", c)
		}
	}
	if DefaultNotificationPreferences().Enabled("bogus") {
		t.Error("an unknown category must be off")
	}
}

func TestJobCompletedMailsInitiator(t *testing.T) {
	store := newFakeNotifStore()
	org, project, initiator := uuid.New(), uuid.New(), uuid.New()
	execID := uuid.New()
	store.execs[execID] = &models.WorkflowExecution{
		ID: execID, ProjectID: project, OrganizationID: org,
		WorkflowDefinitionID: uuid.New(),
		InitiatedByUserID:    &initiator,
	}
	store.users[initiator] = [2]string{"init@example.com", "Init"}
	store.orgNames[org] = "Acme"
	store.projects[project] = "Q3"

	var sent []sentMail
	d := buildDispatcher(store, &sent, nil)
	d.HandleWorkflowEvent(context.Background(), "execution.completed", execID)

	if len(sent) != 1 {
		t.Fatalf("want 1 mail, got %d", len(sent))
	}
	if sent[0].to != "init@example.com" || sent[0].tmpl != TemplateJobSucceeded {
		t.Errorf("unexpected mail: %+v", sent[0])
	}
	if sent[0].data.ProjectName != "Q3" || sent[0].data.OrgName != "Acme" {
		t.Errorf("data not filled: %+v", sent[0].data)
	}
	if sent[0].data.Link != "https://reelbolt.ai/app/projects/"+project.String()+"/workflows/"+store.execs[execID].WorkflowDefinitionID.String()+"/executions/"+execID.String() {
		t.Errorf("bad link %q", sent[0].data.Link)
	}
}

func TestJobFailedFallsBackToOrgOwner(t *testing.T) {
	store := newFakeNotifStore()
	org, owner := uuid.New(), uuid.New()
	store.owners[org] = owner
	execID := uuid.New()
	store.execs[execID] = &models.WorkflowExecution{
		ID: execID, ProjectID: uuid.New(), OrganizationID: org,
		WorkflowDefinitionID: uuid.New(),
		// InitiatedByUserID nil -> owner fallback.
	}
	errMsg := "boom"
	store.execs[execID].ErrorMessage = &errMsg
	store.users[owner] = [2]string{"owner@example.com", "Olive"}
	store.orgNames[org] = "Acme"

	var sent []sentMail
	d := buildDispatcher(store, &sent, nil)
	d.HandleWorkflowEvent(context.Background(), "execution.failed", execID)

	if len(sent) != 1 || sent[0].to != "owner@example.com" {
		t.Fatalf("want one mail to the owner, got %+v", sent)
	}
	if sent[0].data.ErrorDetail != "boom" {
		t.Errorf("error detail missing: %q", sent[0].data.ErrorDetail)
	}
}

func TestJobSkippedWhenSwitchOff(t *testing.T) {
	store := newFakeNotifStore()
	org, initiator := uuid.New(), uuid.New()
	execID := uuid.New()
	store.execs[execID] = &models.WorkflowExecution{
		ID: execID, ProjectID: uuid.New(), OrganizationID: org,
		WorkflowDefinitionID: uuid.New(), InitiatedByUserID: &initiator,
	}
	store.users[initiator] = [2]string{"init@example.com", "Init"}
	store.prefs[initiator] = NotificationPreferences{JobSucceeded: false, JobFailed: true}

	var sent []sentMail
	d := buildDispatcher(store, &sent, nil)
	d.HandleWorkflowEvent(context.Background(), "execution.completed", execID)
	if len(sent) != 0 {
		t.Fatalf("switch off should not mail, got %d", len(sent))
	}
}

func TestActivityMailsMembersExcludingActor(t *testing.T) {
	store := newFakeNotifStore()
	org, actor, other := uuid.New(), uuid.New(), uuid.New()
	store.members[org] = []uuid.UUID{actor, other}
	store.users[actor] = [2]string{"actor@example.com", "Act"}
	store.users[other] = [2]string{"other@example.com", "Oth"}
	store.orgNames[org] = "Acme"
	project := uuid.New()
	store.projects[project] = "Q3"

	var sent []sentMail
	d := buildDispatcher(store, &sent, nil)
	d.HandleActivityEvent(context.Background(), UserActivityEvent{
		OrganizationID: org.String(), ActorUserID: actor.String(),
		ProjectID: project.String(), Type: models.NotificationFileCreated,
		Subject: "clip.mp4",
	})

	if len(sent) != 1 {
		t.Fatalf("want mail to the non-actor only, got %d: %+v", len(sent), sent)
	}
	if sent[0].to != "other@example.com" {
		t.Errorf("want the other member, got %s", sent[0].to)
	}
	if sent[0].data.ActorName != "Act" || sent[0].data.FileName != "clip.mp4" {
		t.Errorf("actor/file not filled: %+v", sent[0].data)
	}
}

func TestActivitySystemActorReachesEveryone(t *testing.T) {
	store := newFakeNotifStore()
	org, a, b := uuid.New(), uuid.New(), uuid.New()
	store.members[org] = []uuid.UUID{a, b}
	store.users[a] = [2]string{"a@example.com", "A"}
	store.users[b] = [2]string{"b@example.com", "B"}
	project := uuid.New()
	store.projects[project] = "Q3"

	var sent []sentMail
	d := buildDispatcher(store, &sent, nil)
	d.HandleActivityEvent(context.Background(), UserActivityEvent{
		OrganizationID: org.String(),
		// ActorUserID empty: a system-triggered edit.
		ProjectID: project.String(), Type: models.NotificationGraphicsEdited,
	})

	if len(sent) != 2 {
		t.Fatalf("a system actor should mail every member, got %d", len(sent))
	}
}

func TestActivitySendFailureIsRecordedNotPanic(t *testing.T) {
	store := newFakeNotifStore()
	org, a, b := uuid.New(), uuid.New(), uuid.New()
	store.members[org] = []uuid.UUID{a, b}
	store.users[a] = [2]string{"a@example.com", "A"}
	store.users[b] = [2]string{"b@example.com", "B"}
	project := uuid.New()
	store.projects[project] = "Q3"

	var sent []sentMail
	d := buildDispatcher(store, &sent, errors.New("smtp down"))
	// Must not panic, and the attempt still counts as "sent" from the loop's view.
	d.HandleActivityEvent(context.Background(), UserActivityEvent{
		OrganizationID: org.String(), ActorUserID: a.String(),
		ProjectID: project.String(), Type: models.NotificationFileCreated,
	})
	if len(sent) != 1 {
		t.Fatalf("expected one attempt despite the failure, got %d", len(sent))
	}
}

// TestErrorsIsMissingTable pins the recognition of Postgres's undefined_table
// error (42P01) — the state a notification read hits when the Go API is running
// before the Inference API's EF migration has created the tables. Wrapped
// errors must still be recognized, and every other error (including a different
// Postgres code) must NOT be mistaken for a missing table.
func TestErrorsIsMissingTable(t *testing.T) {
	missing := &pgconn.PgError{Code: "42P01", Message: `relation "user_notification_preferences" does not exist`}
	if !errorsIsMissingTable(missing) {
		t.Fatalf("a bare 42P01 must be recognized as a missing table")
	}
	if !errorsIsMissingTable(errors.Join(errors.New("db: query failed"), missing)) {
		t.Fatalf("a 42P01 wrapped in other errors must still be recognized")
	}
	if errorsIsMissingTable(&pgconn.PgError{Code: "42703", Message: "column does not exist"}) {
		t.Fatalf("a different Postgres error code must not be treated as a missing table")
	}
	if errorsIsMissingTable(errors.New("connection refused")) {
		t.Fatalf("a non-Postgres error must not be treated as a missing table")
	}
}

// F-4: a failed notification mail is logged by reference with the SMTP error sanitised.
func TestNotificationSendFailureLogsNoAddress(t *testing.T) {
	store := newFakeNotifStore()
	org, a, b := uuid.New(), uuid.New(), uuid.New()
	store.members[org] = []uuid.UUID{a, b}
	store.users[a] = [2]string{"a@example.com", "A"}
	store.users[b] = [2]string{"victim@example.com", "B"}
	project := uuid.New()
	store.projects[project] = "Q3"
	buf := captureStdLog(t)

	var sent []sentMail
	d := buildDispatcher(store, &sent, &textproto.Error{Code: 550, Msg: "5.1.1 <victim@example.com>: user unknown"})
	d.HandleActivityEvent(context.Background(), UserActivityEvent{
		OrganizationID: org.String(), ActorUserID: a.String(),
		ProjectID: project.String(), Type: models.NotificationFileCreated,
	})
	if out := buf.String(); strings.Contains(out, "@") || !strings.Contains(out, "rcpt=") || !strings.Contains(out, "550") {
		t.Fatalf("log: %s", out)
	}
}
