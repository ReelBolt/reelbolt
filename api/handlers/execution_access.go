package handlers

import (
	"errors"
	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
)

// executionAccess is the outcome of checking a caller against an execution
// (docs/design/tenancy.md, error rule D5).
type executionAccess int

const (
	// accessGranted: the execution belongs to the caller's active organization.
	accessGranted executionAccess = iota
	// accessNotFound: another organization's execution, an unknown one, or a
	// caller with no active org. Existence never leaks.
	accessNotFound
	// accessForbidden: the caller's own organization, but their role may not
	// see executions (Billing sees billing pages only).
	accessForbidden
)

// executionAccessFor decides whether uc may see an execution owned by
// executionOrgID. Visibility is organization-scoped: any member of the active
// org with a project-capable role sees every execution in it, and nobody else
// does, platform admins included (platform-wide views live under
// /api/v1/admin/*). Membership itself was already revalidated by
// middleware.Auth, so uc.OrganizationID is trustworthy here.
func executionAccessFor(uc middleware.UserContext, executionOrgID uuid.UUID) executionAccess {
	if uc.OrganizationID == uuid.Nil || executionOrgID != uc.OrganizationID {
		return accessNotFound
	}
	if uc.OrgRole == models.OrgRoleBilling {
		return accessForbidden
	}
	return accessGranted
}

// canSeeExecutions reports whether uc may see executions of its active org at
// all, used for the aggregate endpoints that have no single execution to check.
func canSeeExecutions(uc middleware.UserContext) bool {
	return executionAccessFor(uc, uc.OrganizationID) == accessGranted
}

// executionOrgLookup returns an execution's organization id; found is false
// when the execution does not exist. A package variable so the per-connection
// cache is testable without a database.
var executionOrgLookup = func(executionID string) (orgID uuid.UUID, found bool, err error) {
	var execution models.WorkflowExecution
	err = database.DB.Select("id", "organization_id").Where("id = ?", executionID).First(&execution).Error
	if errors.Is(err, gorm.ErrRecordNotFound) {
		return uuid.Nil, false, nil
	}
	if err != nil {
		return uuid.Nil, false, err
	}
	return execution.OrganizationID, true, nil
}

// visibilityCache memoizes, for ONE SSE connection, whether the caller may see
// each execution id. An execution's organization never changes, and an org
// switch issues a new token (the web client reconnects), so the answer is valid
// for the life of the connection. Lookup errors are not cached and deny the
// event, so a transient database error never leaks it.
type visibilityCache struct {
	uc      middleware.UserContext
	lookup  func(executionID string) (uuid.UUID, bool, error)
	allowed map[string]bool
}

func newVisibilityCache(uc middleware.UserContext) *visibilityCache {
	return &visibilityCache{uc: uc, lookup: executionOrgLookup, allowed: make(map[string]bool)}
}

func (c *visibilityCache) canSee(executionID string) bool {
	if v, ok := c.allowed[executionID]; ok {
		return v
	}
	orgID, found, err := c.lookup(executionID)
	if err != nil {
		return false
	}
	v := found && executionAccessFor(c.uc, orgID) == accessGranted
	c.allowed[executionID] = v
	return v
}

// projectOrgLookup returns a project's organization id; found is false when the project does not
// exist or has not been assigned to an organization yet. A package variable for the same reason
// executionOrgLookup is.
var projectOrgLookup = func(projectID string) (orgID uuid.UUID, found bool, err error) {
	var project models.Project
	err = database.DB.Select("id", "organization_id").Where("id = ?", projectID).First(&project).Error
	if errors.Is(err, gorm.ErrRecordNotFound) {
		return uuid.Nil, false, nil
	}
	if err != nil {
		return uuid.Nil, false, err
	}
	if project.OrganizationID == nil {
		return uuid.Nil, false, nil
	}
	return *project.OrganizationID, true, nil
}

// canSeeProject is canSee for the events that have no execution to be scoped by — the editor's
// media-derivative progress events, since a derivative is not a workflow step. Same organization
// rule, same per-connection memo (keyed with a prefix so a project id and an execution id can never
// share an entry), same deny-on-error.
func (c *visibilityCache) canSeeProject(projectID string) bool {
	key := "project:" + projectID
	if v, ok := c.allowed[key]; ok {
		return v
	}
	orgID, found, err := projectOrgLookup(projectID)
	if err != nil {
		return false
	}
	v := found && executionAccessFor(c.uc, orgID) == accessGranted
	c.allowed[key] = v
	return v
}
