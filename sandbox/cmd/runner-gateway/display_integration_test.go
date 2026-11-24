package main

import (
	"context"
	"errors"
	"testing"

	"github.com/jackc/pgx/v5/pgxpool"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// assign.display against a real database: the one read display.go makes, the
// tables it joins and the organization pin on every one of them. Runs when
// RUNNER_GATEWAY_TEST_DATABASE_URL is set, like the rest of the queue suite.

// displayDDL transcribes the four inference-owned tables the read touches, with
// only the columns it reads. workflow_executions already exists in the queue
// fixture (id, status); the two columns the join needs are added to it.
const displayDDL = `
ALTER TABLE workflow_executions ADD COLUMN IF NOT EXISTS workflow_definition_id uuid;
ALTER TABLE workflow_executions ADD COLUMN IF NOT EXISTS organization_id uuid;
CREATE TABLE IF NOT EXISTS organizations (id uuid PRIMARY KEY, name text NOT NULL);
CREATE TABLE IF NOT EXISTS projects (id uuid PRIMARY KEY, name text NOT NULL, organization_id uuid NOT NULL);
CREATE TABLE IF NOT EXISTS workflow_definitions (id uuid PRIMARY KEY, name text NOT NULL, project_id uuid NOT NULL);
TRUNCATE organizations, projects, workflow_definitions;
`

const (
	displayProjectA  = "6b1f2a10-0000-4000-8000-0000000000a1"
	displayWorkflowA = "6b1f2a10-0000-4000-8000-0000000000b1"
	displayProjectB  = "6b1f2a10-0000-4000-8000-0000000000a2"
	displayWorkflowB = "6b1f2a10-0000-4000-8000-0000000000b2"
)

func displayFixture(t *testing.T, pool *pgxpool.Pool) {
	t.Helper()
	ctx := context.Background()
	if _, err := pool.Exec(ctx, displayDDL); err != nil {
		t.Fatalf("display schema: %v", err)
	}
	for _, stmt := range []struct {
		sql  string
		args []any
	}{
		{`INSERT INTO organizations (id, name) VALUES ($1::uuid, 'Acme'), ($2::uuid, 'Other Co')`, []any{testOrgA, testOrgB}},
		{`INSERT INTO projects (id, name, organization_id) VALUES ($1::uuid, 'Launch video', $2::uuid), ($3::uuid, 'Secret', $4::uuid)`,
			[]any{displayProjectA, testOrgA, displayProjectB, testOrgB}},
		{`INSERT INTO workflow_definitions (id, name, project_id) VALUES ($1::uuid, 'Promo cut', $2::uuid), ($3::uuid, 'Hidden', $4::uuid)`,
			[]any{displayWorkflowA, displayProjectA, displayWorkflowB, displayProjectB}},
		{`INSERT INTO workflow_executions (id, status, workflow_definition_id, organization_id)
		  VALUES ($1::uuid, 'Running', $2::uuid, $3::uuid), ($4::uuid, 'Running', $5::uuid, $6::uuid)`,
			[]any{testExecA, displayWorkflowA, testOrgA, testExecB, displayWorkflowB, testOrgB}},
	} {
		if _, err := pool.Exec(ctx, stmt.sql, stmt.args...); err != nil {
			t.Fatalf("display fixture: %v", err)
		}
	}
}

func TestPostgresJobDisplayReadsTheNamesOfOneOrganization(t *testing.T) {
	store := queueTestStore(t)
	displayFixture(t, store.pool)
	ctx := context.Background()

	got, err := store.JobDisplay(ctx, testExecA, testOrgA)
	if err != nil {
		t.Fatalf("JobDisplay: %v", err)
	}
	want := protocol.AssignDisplay{
		WorkspaceName: "Acme", ProjectName: "Launch video", WorkflowName: "Promo cut",
		ProjectID: displayProjectA, WorkflowID: displayWorkflowA, ExecutionID: testExecA,
	}
	if got != want {
		t.Fatalf("display = %+v, want %+v", got, want)
	}

	// Another organization's execution, asked for under this organization's
	// job, yields nothing - not the other organization's names.
	if _, err := store.JobDisplay(ctx, testExecB, testOrgA); !errors.Is(err, ErrJobNotFound) {
		t.Fatalf("a cross-organization lookup answered %v, want ErrJobNotFound", err)
	}
	// An unknown execution is the same answer.
	if _, err := store.JobDisplay(ctx, "00000000-0000-4000-8000-000000000000", testOrgA); !errors.Is(err, ErrJobNotFound) {
		t.Fatalf("an unknown execution answered %v, want ErrJobNotFound", err)
	}
}

// The whole path: a job offered to a connected runner carries the names.
func TestAnOfferCarriesTheDisplayNames(t *testing.T) {
	qg := newQueueGateway(t, nil)
	displayFixture(t, qg.pool)
	r := qg.newQueueRunner(t, testOrgA)
	view := qg.createJob(t, map[string]any{
		"orgId": testOrgA, "executionId": testExecA, "stepId": testStepID,
		"jobType": protocol.JobVideoCompile, "target": targetOrgRunner,
		"purpose": "video.compile",
		"spec":    map[string]any{"executionId": testExecA, "stepId": testStepID, "mode": "Reencode"},
	})
	ws, _ := qg.handshake(t, r)
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
	env := readEnvelope(t, ws)
	if env.Type != protocol.TypeAssign || env.JobID != view.JobID {
		t.Fatalf("got %s for %s, want the assign of %s", env.Type, env.JobID, view.JobID)
	}
	assign := readPayload[protocol.AssignPayload](t, env)
	if assign.Display == nil || assign.Display.ProjectName != "Launch video" || assign.Display.WorkspaceName != "Acme" ||
		assign.Display.WorkflowName != "Promo cut" {
		t.Fatalf("assign.display = %+v", assign.Display)
	}
}
