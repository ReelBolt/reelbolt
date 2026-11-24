package main

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/jackc/pgx/v5"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// assign.display: the human names a runner's own UI shows next to a job.
//
// The queue knows a job by ids only, and a desktop app that lists "job 7d1c…"
// is a desktop app nobody can read. So when the gateway offers a job it makes
// ONE bounded read for the names of the workspace, project and workflow the
// job's execution belongs to, and sends them along.
//
// Three rules shape it:
//
//   - It never blocks or fails an assignment. The lease is already granted when
//     the read runs; a read that errors, finds nothing or does not answer within
//     displayLookupTimeout just leaves assign.display out, and the runner says
//     "no name" instead of waiting.
//   - It never crosses tenants. Every table in the join is pinned to the job's
//     own organization, so a job whose execution id somehow named another
//     organization's run gets no names at all rather than someone else's.
//   - It is display-only. Nothing on the runner may decide anything by it (see
//     protocol.AssignDisplay), so a stale or missing name is a cosmetic bug,
//     never a security one.

// displayLookupTimeout bounds the one read. A gateway whose database is slow
// enough to miss it is having a worse day than a missing project name.
const displayLookupTimeout = 2 * time.Second

// JobDisplayStore reads assign.display for one job. It is an interface so the
// offer path can be tested with a fake that is slow, broken or empty.
type JobDisplayStore interface {
	// JobDisplay returns the names for the execution, read only within the
	// organization. ErrJobNotFound means nothing matched there.
	JobDisplay(ctx context.Context, executionID, organizationID string) (protocol.AssignDisplay, error)
}

// attachDisplay gives the gateway its display lookup. Nil (every test that does
// not ask for one) means assignments carry no display, exactly as before.
func (g *gateway) attachDisplay(display JobDisplayStore) {
	g.display = display
}

// jobDisplay returns the display for a job, or nil when there is none to send.
// It never returns an error: every failure is "no names", counted and dropped.
func (g *gateway) jobDisplay(ctx context.Context, job Job) *protocol.AssignDisplay {
	if g.display == nil || job.ExecutionID == "" || job.OrganizationID == "" {
		return nil
	}
	lookupCtx, cancel := context.WithTimeout(ctx, displayLookupTimeout)
	defer cancel()
	display, err := g.display.JobDisplay(lookupCtx, job.ExecutionID, job.OrganizationID)
	switch {
	case errors.Is(err, ErrJobNotFound):
		g.metrics.displayLookups.WithLabelValues("missing").Inc()
		return nil
	case err != nil:
		g.metrics.displayLookups.WithLabelValues("error").Inc()
		return nil
	}
	g.metrics.displayLookups.WithLabelValues("ok").Inc()
	if display.Empty() {
		return nil
	}
	return &display
}

// jobDisplaySQL is the one read. Every join is pinned to $2, the job's own
// organization, so the names can only ever be that organization's. Names are
// coalesced so a missing row in the middle of the chain (a workflow deleted
// while its run was still going) yields no row at all rather than a half-filled
// display, which the caller reports as "missing".
const jobDisplaySQL = `
SELECT coalesce(o.name, ''), coalesce(p.name, ''), coalesce(w.name, ''),
       p.id::text, w.id::text, e.id::text
  FROM workflow_executions e
  JOIN workflow_definitions w ON w.id = e.workflow_definition_id
  JOIN projects p             ON p.id = w.project_id AND p.organization_id = $2::uuid
  JOIN organizations o        ON o.id = p.organization_id
 WHERE e.id = $1::uuid
   AND e.organization_id = $2::uuid`

// JobDisplay implements JobDisplayStore over the inference tables the gateway
// otherwise never reads. It only reads, and only these columns.
func (s *PostgresJobStore) JobDisplay(ctx context.Context, executionID, organizationID string) (protocol.AssignDisplay, error) {
	var d protocol.AssignDisplay
	err := s.pool.QueryRow(ctx, jobDisplaySQL, executionID, organizationID).Scan(
		&d.WorkspaceName, &d.ProjectName, &d.WorkflowName, &d.ProjectID, &d.WorkflowID, &d.ExecutionID)
	if errors.Is(err, pgx.ErrNoRows) {
		return protocol.AssignDisplay{}, ErrJobNotFound
	}
	if err != nil {
		return protocol.AssignDisplay{}, fmt.Errorf("read job display: %w", err)
	}
	return d, nil
}
