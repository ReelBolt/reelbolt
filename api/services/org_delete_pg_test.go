package services

import (
	"context"
	"database/sql"
	"os"
	"strings"
	"testing"

	"github.com/google/uuid"
	"gorm.io/driver/postgres"
	"gorm.io/gorm"
	"gorm.io/gorm/logger"

	"github.com/vecchiotom/reelbolt/database"
)

// DeleteOrg must only soft-delete the organization. The old code hard-deleted its API-key
// service accounts, which cascaded through projects.owner_id into the RESTRICT
// fk_workflow_executions_projects_project_id: an org with one key-created project that had
// a run answered 500, and every API-created project of the org was destroyed.
//
// The fake-DB tests can show the SQL, not the constraints. This one runs the real foreign
// keys: a service-account-owned project with an execution must not block or be lost.
//
// It is opt-in, like the other PostgreSQL tests: `go test ./...` must not need a database.
//
//	REELBOLT_TEST_PG='Host=127.0.0.1;Port=55439;Username=review;Password=review;Database=review' \
//	    go test ./services/ -run DeleteOrg -v
const orgDeleteDDL = `
CREATE TABLE IF NOT EXISTS application_users (
    id uuid PRIMARY KEY,
    email character varying(256) NOT NULL,
    display_name character varying(200) NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT now(),
    is_service_account boolean NOT NULL DEFAULT false
);

CREATE TABLE IF NOT EXISTS organizations (
    id uuid PRIMARY KEY,
    name character varying(200) NOT NULL,
    slug character varying(100) NOT NULL,
    kind character varying(50) NOT NULL,
    created_by_user_id uuid NULL REFERENCES application_users (id) ON DELETE SET NULL,
    compute_preference character varying(50) NOT NULL DEFAULT 'Auto',
    created_at timestamp with time zone NOT NULL DEFAULT now(),
    updated_at timestamp with time zone NOT NULL DEFAULT now(),
    deleted_at timestamp with time zone NULL
);

CREATE TABLE IF NOT EXISTS organization_memberships (
    organization_id uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    user_id uuid NOT NULL REFERENCES application_users (id) ON DELETE CASCADE,
    role character varying(50) NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT now(),
    PRIMARY KEY (organization_id, user_id)
);

-- owner_id is ON DELETE CASCADE and organization_id is ON DELETE RESTRICT, exactly as the
-- Inference API migrations define them.
CREATE TABLE IF NOT EXISTS projects (
    id uuid PRIMARY KEY,
    name character varying(200) NOT NULL,
    owner_id uuid NOT NULL REFERENCES application_users (id) ON DELETE CASCADE,
    organization_id uuid NULL REFERENCES organizations (id) ON DELETE RESTRICT,
    created_at timestamp with time zone NOT NULL DEFAULT now(),
    updated_at timestamp with time zone NOT NULL DEFAULT now()
);

-- The RESTRICT that made the old hard delete fail for any project that had a run.
CREATE TABLE IF NOT EXISTS workflow_executions (
    id uuid PRIMARY KEY,
    project_id uuid NOT NULL REFERENCES projects (id) ON DELETE RESTRICT,
    organization_id uuid NOT NULL,
    status character varying(50) NOT NULL DEFAULT 'Running',
    created_at timestamp with time zone NOT NULL DEFAULT now()
);
`

// pgDSN accepts a postgres:// URL or the Npgsql-style key=value form the review database is
// described with (Host=...;Port=...;Username=...;Password=...;Database=...).
func pgDSN(raw string) string {
	if strings.HasPrefix(raw, "postgres://") || strings.HasPrefix(raw, "postgresql://") {
		return raw
	}
	names := map[string]string{"host": "host", "port": "port", "username": "user", "password": "password", "database": "dbname"}
	var parts []string
	for _, field := range strings.Split(raw, ";") {
		key, value, ok := strings.Cut(strings.TrimSpace(field), "=")
		if !ok {
			continue
		}
		if mapped, known := names[strings.ToLower(strings.TrimSpace(key))]; known {
			parts = append(parts, mapped+"="+value)
		}
	}
	return strings.Join(append(parts, "sslmode=disable"), " ")
}

func pgOrgDeleteDB(t *testing.T) *gorm.DB {
	t.Helper()
	raw := os.Getenv("REELBOLT_TEST_PG")
	if raw == "" {
		t.Skip("REELBOLT_TEST_PG is not set; skipping the PostgreSQL DeleteOrg test")
	}
	db, err := gorm.Open(postgres.Open(pgDSN(raw)), &gorm.Config{Logger: logger.Default.LogMode(logger.Silent)})
	if err != nil {
		t.Fatalf("connect to the review database: %v", err)
	}
	if err := db.Exec(orgDeleteDDL).Error; err != nil {
		t.Fatalf("apply the FK schema: %v", err)
	}
	return db
}

func TestDeleteOrgSoftDeletesAWorkspaceWithKeyOwnedProjects(t *testing.T) {
	db := pgOrgDeleteDB(t)

	org, svc, human, project, execution := uuid.New(), uuid.New(), uuid.New(), uuid.New(), uuid.New()
	mustExec := func(q string, args ...any) {
		t.Helper()
		if err := db.Exec(q, args...).Error; err != nil {
			t.Fatalf("insert: %v", err)
		}
	}
	mustExec(`INSERT INTO application_users (id, email, display_name, is_service_account) VALUES (?, ?, ?, false)`,
		human, "owner-"+human.String()+"@example.com", "Owner")
	mustExec(`INSERT INTO application_users (id, email, display_name, is_service_account) VALUES (?, ?, ?, true)`,
		svc, "apikey+"+svc.String()+"@service.invalid", "key")
	mustExec(`INSERT INTO organizations (id, name, slug, kind) VALUES (?, ?, ?, 'Team')`,
		org, "Org "+org.String(), "org-"+org.String())
	mustExec(`INSERT INTO organization_memberships (organization_id, user_id, role) VALUES (?, ?, 'Owner')`, org, human)
	mustExec(`INSERT INTO organization_memberships (organization_id, user_id, role) VALUES (?, ?, 'Member')`, org, svc)
	mustExec(`INSERT INTO projects (id, name, owner_id, organization_id) VALUES (?, ?, ?, ?)`,
		project, "Key project", svc, org)
	mustExec(`INSERT INTO workflow_executions (id, project_id, organization_id) VALUES (?, ?, ?)`,
		execution, project, org)

	old := database.DB
	database.DB = db
	t.Cleanup(func() { database.DB = old })

	// Before the fix this returned the RESTRICT violation from deleting the service user.
	if err := (GormOrgStore{}).DeleteOrg(context.Background(), org); err != nil {
		t.Fatalf("DeleteOrg: %v", err)
	}

	var deletedAt sql.NullTime
	if err := db.Raw(`SELECT deleted_at FROM organizations WHERE id = ?`, org).Row().Scan(&deletedAt); err != nil {
		t.Fatalf("read the org: %v", err)
	}
	if !deletedAt.Valid || deletedAt.Time.IsZero() {
		t.Fatal("the organization was not soft-deleted")
	}

	count := func(table, where string, args ...any) int64 {
		t.Helper()
		var n int64
		if err := db.Table(table).Where(where, args...).Count(&n).Error; err != nil {
			t.Fatalf("count %s: %v", table, err)
		}
		return n
	}
	if n := count("application_users", "id = ?", svc); n != 1 {
		t.Fatalf("the service account was deleted: %d rows", n)
	}
	if n := count("organization_memberships", "organization_id = ? AND user_id = ?", org, svc); n != 1 {
		t.Fatalf("the service-account membership was deleted: %d rows", n)
	}
	if n := count("projects", "id = ?", project); n != 1 {
		t.Fatalf("the key-created project was deleted: %d rows", n)
	}
	if n := count("workflow_executions", "id = ?", execution); n != 1 {
		t.Fatalf("the run was deleted: %d rows", n)
	}
}
