package services

import (
	"context"
	"database/sql"
	"database/sql/driver"
	"errors"
	"fmt"
	"io"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"gorm.io/driver/postgres"
	"gorm.io/gorm"

	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/testutil/fakesql"
)

// svcCount answers the "is this user a service account" count query with 1 (it is one) and
// every other query with no rows.
func svcCount(svc bool) fakesql.Handler {
	return func(q string, _ []driver.Value) ([]string, [][]driver.Value) {
		if strings.Contains(q, "count(*)") && strings.Contains(q, "application_users") {
			n := int64(0)
			if svc {
				n = 1
			}
			return []string{"count"}, [][]driver.Value{{n}}
		}
		return nil, nil
	}
}

func TestServiceAccount_ListMembersExcludesThem(t *testing.T) {
	db := fakesql.Install(t, nil)
	if _, err := (GormOrgStore{}).ListMembers(context.Background(), uuid.New()); err != nil {
		t.Fatal(err)
	}
	if !db.Contains("organization_memberships", "u.is_service_account =") {
		t.Fatalf("member list does not filter service accounts: %+v", db.Statements())
	}
}

func TestServiceAccount_GetMemberRoleAndIsEmailMemberHideThem(t *testing.T) {
	db := fakesql.Install(t, nil)
	_, found, err := (GormOrgStore{}).GetMemberRole(context.Background(), uuid.New(), uuid.New())
	if err != nil || found {
		t.Fatalf("found=%v err=%v", found, err)
	}
	if !db.Contains("is_service_account") {
		t.Fatal("GetMemberRole does not exclude service accounts")
	}
	if _, err := (GormOrgStore{}).IsEmailMember(context.Background(), uuid.New(), "apikey+x@service.invalid"); err != nil {
		t.Fatal(err)
	}
	if !db.Contains("lower(u.email)", "is_service_account") {
		t.Fatal("IsEmailMember does not exclude service accounts")
	}
}

func TestServiceAccount_RoleChangeAndRemoveAre404(t *testing.T) {
	fakesql.Install(t, svcCount(true))
	ctx := context.Background()
	org, user := uuid.New(), uuid.New()
	if err := (GormOrgStore{}).SetMemberRole(ctx, org, user, models.OrgRoleOwner); !errors.Is(err, gorm.ErrRecordNotFound) {
		t.Fatalf("SetMemberRole err = %v, want not found", err)
	}
	if err := (GormOrgStore{}).RemoveMember(ctx, org, user); !errors.Is(err, gorm.ErrRecordNotFound) {
		t.Fatalf("RemoveMember err = %v, want not found", err)
	}
}

func TestServiceAccount_InviteAcceptRefused(t *testing.T) {
	fakesql.Install(t, svcCount(true))
	inv := &models.OrganizationInvite{ID: uuid.New(), OrganizationID: uuid.New()}
	err := (GormOrgStore{}).AcceptInvite(context.Background(), inv, uuid.New(), time.Now().UTC())
	if !errors.Is(err, gorm.ErrRecordNotFound) {
		t.Fatalf("err = %v, want not found", err)
	}
}

func TestServiceAccount_AdminListAndLookupsExcludeThem(t *testing.T) {
	db := fakesql.Install(t, nil)
	if _, err := ListUsers(); err != nil {
		t.Fatal(err)
	}
	if !db.Contains("from \"application_users\"", "is_service_account") {
		t.Fatalf("ListUsers does not filter: %+v", db.Statements())
	}
	for name, call := range map[string]func(){
		"GetUserByID":    func() { _, _ = GetUserByID(uuid.New()) },
		"GetUserByEmail": func() { _, _ = GetUserByEmail("a@b.c") },
		"FindUserByID":   func() { _, _ = (GormAccountStore{}).FindUserByID(uuid.New()) },
		"FindUserByEmail": func() {
			_, _ = (GormAccountStore{}).FindUserByEmail("a@b.c")
		},
	} {
		before := len(db.Statements())
		call()
		got := db.Statements()[before:]
		if len(got) == 0 || !strings.Contains(got[0].Query, "is_service_account") {
			t.Errorf("%s does not filter service accounts: %+v", name, got)
		}
	}
}

func TestServiceAccount_AdminUpdateAndDeleteAre404(t *testing.T) {
	fakesql.Install(t, svcCount(true))
	if _, err := UpdateUser(uuid.New(), map[string]interface{}{"is_admin": true}, AdminActor{}); !errors.Is(err, gorm.ErrRecordNotFound) {
		t.Fatalf("UpdateUser err = %v", err)
	}
	if err := DeleteUser(uuid.New(), AdminActor{}); !errors.Is(err, gorm.ErrRecordNotFound) {
		t.Fatalf("DeleteUser err = %v", err)
	}
}

func TestServiceAccount_NeverPlatformAdminOrProvisioned(t *testing.T) {
	db := fakesql.Install(t, svcCount(true))
	if err := SyncPlatformMembership(database.DB, uuid.New(), true); err != nil {
		t.Fatal(err)
	}
	for _, s := range db.Statements() {
		if s.Exec {
			t.Fatalf("a membership was written for a service account: %s", s.Query)
		}
	}
	err := ProvisionUserTenancy(database.DB, &models.ApplicationUser{ID: uuid.New(), IsServiceAccount: true})
	if !errors.Is(err, ErrServiceAccountTenancy) {
		t.Fatalf("provision err = %v", err)
	}
	_, err = ResolveActiveMembership(context.Background(), &models.ApplicationUser{ID: uuid.New(), IsServiceAccount: true})
	if err == nil {
		t.Fatal("ResolveActiveMembership must refuse a service account")
	}
}

func TestServiceAccount_SoleOwnerTeamIgnoresThem(t *testing.T) {
	db := fakesql.Install(t, nil)
	if _, err := (GormAccountDeletionStore{}).SoleOwnerTeamWithMembers(context.Background(), uuid.New()); err != nil {
		t.Fatal(err)
	}
	if !db.Contains("is_service_account = false") {
		t.Fatal("SoleOwnerTeamWithMembers counts service accounts as members")
	}
	if _, err := (GormAccountDeletionStore{}).UserExists("apikey+x@service.invalid"); err != nil {
		t.Fatal(err)
	}
	if !db.Contains("lower(email)", "is_service_account") {
		t.Fatal("UserExists is true for service accounts")
	}
}

func TestServiceAccount_OrgMembersExcludesThem(t *testing.T) {
	db := fakesql.Install(t, nil)
	if _, err := (GormNotificationStore{}).OrgMembers(context.Background(), uuid.New()); err != nil {
		t.Fatal(err)
	}
	if !db.Contains("join application_users", "is_service_account") {
		t.Fatal("OrgMembers mails service accounts")
	}
}

func TestServiceAccount_WorkflowEventSendsNoMail(t *testing.T) {
	store := newFakeNotifStore()
	org, project, svc, owner := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	execID := uuid.New()
	store.execs[execID] = &models.WorkflowExecution{
		ID: execID, ProjectID: project, OrganizationID: org,
		WorkflowDefinitionID: uuid.New(), InitiatedByUserID: &svc,
	}
	// The real LookupUser reports a service account as absent; the owner must not get the mail either.
	store.owners[org] = owner
	store.users[owner] = [2]string{"owner@example.com", "Owner"}
	var sent []sentMail
	d := buildDispatcher(store, &sent, nil)
	d.HandleWorkflowEvent(context.Background(), "execution.completed", execID)
	if len(sent) != 0 {
		t.Fatalf("a run initiated by a service account sent mail: %+v", sent)
	}
}

func TestServiceAccount_LookupUserDropsThem(t *testing.T) {
	fakesql.Install(t, func(q string, _ []driver.Value) ([]string, [][]driver.Value) {
		if strings.Contains(q, "application_users") {
			return []string{"id", "email", "display_name", "is_service_account"},
				[][]driver.Value{{uuid.NewString(), "apikey+k@service.invalid", "key", true}}
		}
		return nil, nil
	})
	_, _, ok, err := (GormNotificationStore{}).LookupUser(context.Background(), uuid.New())
	if err != nil || ok {
		t.Fatalf("ok=%v err=%v", ok, err)
	}
}

func TestServiceAccount_ReservedDomainIsNotARecipient(t *testing.T) {
	if _, err := ValidateRecipient("apikey+abc@service.invalid"); err == nil {
		t.Fatal("service.invalid must not validate as a recipient or signup address")
	}
	if _, err := ValidateRecipient("someone@example.com"); err != nil {
		t.Fatal(err)
	}
}

func TestServiceAccount_NoMarketingConsentOrProductEvent(t *testing.T) {
	db := fakesql.Install(t, svcCount(true))
	user := &models.ApplicationUser{ID: uuid.New(), Email: "apikey+k@service.invalid", IsServiceAccount: true}
	if err := writeSignupMarketingConsentTx(database.DB, user, &MarketingOptIn{}); err != nil {
		t.Fatal(err)
	}
	if err := RecordProductEvent(database.DB, user.ID, nil, ProductEventInviteAccepted); err != nil {
		t.Fatal(err)
	}
	for _, s := range db.Statements() {
		if s.Exec {
			t.Fatalf("a consent or event row was written: %s", s.Query)
		}
	}
}

func TestServiceAccount_DeleteOrgOnlySoftDeletes(t *testing.T) {
	db := fakesql.Install(t, nil)
	if err := (GormOrgStore{}).DeleteOrg(context.Background(), uuid.New()); err != nil {
		t.Fatal(err)
	}
	if !db.Contains("update \"organizations\"", "deleted_at") {
		t.Fatalf("org was not soft-deleted: %+v", db.Statements())
	}
	for _, s := range db.Statements() {
		if s.Exec && (strings.Contains(s.Query, "\"application_users\"") || strings.Contains(s.Query, "\"organization_memberships\"") || strings.Contains(s.Query, "\"projects\"")) {
			t.Fatalf("org delete hard-deleted rows: %s", s.Query)
		}
	}
}

func TestServiceAccount_OwnerCountsAndOwnerLookupExcludeThem(t *testing.T) {
	db := fakesql.Install(t, nil)
	ctx := context.Background()
	org := uuid.New()
	if _, err := (GormOrgStore{}).CountOwners(ctx, org); err != nil {
		t.Fatal(err)
	}
	if _, err := lockedOwnerCount(database.DB, org); err != nil {
		t.Fatal(err)
	}
	_, _ = (GormNotificationStore{}).OrgOwner(ctx, org)
	for _, s := range db.Statements() {
		q := strings.ToLower(s.Query)
		if strings.Contains(q, "role = ") && !strings.Contains(q, "is_service_account") {
			t.Fatalf("an owner query counts a service account: %s", s.Query)
		}
	}
}

func TestServiceAccount_GetUserIdentityHidesThem(t *testing.T) {
	db := fakesql.Install(t, nil)
	_, _, _ = (GormOrgStore{}).GetUserIdentity(context.Background(), uuid.New())
	if !db.Contains("select", "is_service_account") {
		t.Fatalf("GetUserIdentity does not exclude service accounts: %+v", db.Statements())
	}
}

func TestServiceAccount_ResetPasswordWritesNothing(t *testing.T) {
	db := fakesql.Install(t, svcCount(true))
	if _, err := ResetPassword(uuid.New(), "a@example.com", AdminActor{}); err != nil && !errors.Is(err, ErrResetPreconditionChanged) {
		t.Fatal(err)
	}
	if !db.Contains("update \"application_users\"", "is_service_account") {
		t.Fatalf("ResetPassword can write a service account: %+v", db.Statements())
	}
}

// failingCountDriver answers every query with an error and records whether a write was attempted.
type failingCountDriver struct{ execs *int }

func (d failingCountDriver) Open(string) (driver.Conn, error) { return &failingCountConn{d.execs}, nil }

type failingCountConn struct{ execs *int }

func (c *failingCountConn) Prepare(string) (driver.Stmt, error) {
	return nil, errors.New("database is down")
}
func (c *failingCountConn) Close() error              { return nil }
func (c *failingCountConn) Begin() (driver.Tx, error) { return nil, errors.New("database is down") }
func (c *failingCountConn) QueryContext(context.Context, string, []driver.NamedValue) (driver.Rows, error) {
	return nil, errors.New("database is down")
}
func (c *failingCountConn) ExecContext(context.Context, string, []driver.NamedValue) (driver.Result, error) {
	*c.execs++
	return driver.RowsAffected(0), nil
}

func TestServiceAccount_ProductEventFailsClosedOnDBError(t *testing.T) {
	execs := 0
	name := fmt.Sprintf("reelbolt-svc-failing-%d", time.Now().UnixNano())
	sql.Register(name, failingCountDriver{execs: &execs})
	sqlDB, err := sql.Open(name, "")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = sqlDB.Close() })
	cfg := database.GormConfig(io.Discard)
	cfg.DisableAutomaticPing = true
	db, err := gorm.Open(postgres.New(postgres.Config{Conn: sqlDB}), cfg)
	if err != nil {
		t.Fatal(err)
	}
	if err := RecordProductEvent(db, uuid.New(), nil, ProductEventSignup); err == nil {
		t.Fatal("a failed service-account lookup must surface as an error")
	}
	if execs != 0 {
		t.Fatalf("a product event was written despite the lookup error: %d execs", execs)
	}
}

// Q-L6: a reset or verify token whose user is a service account is never redeemable, and
// the token is not marked used.
func TestServiceAccount_ConsumeRefusesTheirTokens(t *testing.T) {
	now := time.Now()
	for _, purpose := range []string{models.UserTokenResetPassword, models.UserTokenVerifyEmail} {
		db := fakesql.Install(t, func(q string, a []driver.Value) ([]string, [][]driver.Value) {
			if strings.Contains(q, "user_tokens") && strings.Contains(q, "select") {
				return []string{"id", "user_id", "purpose", "token_hash", "created_at", "expires_at", "used_at"},
					[][]driver.Value{{uuid.New().String(), uuid.New().String(), purpose, "h", now, now.Add(time.Hour), nil}}
			}
			return svcCount(true)(q, a)
		})
		_, err := consume(database.DB, purpose, "h", now)
		if !errors.Is(err, ErrInvalidToken) {
			t.Fatalf("%s: consume = %v, want ErrInvalidToken", purpose, err)
		}
		if db.Contains("update \"user_tokens\"") {
			t.Fatalf("%s: token was marked used: %+v", purpose, db.Statements())
		}
	}
}
