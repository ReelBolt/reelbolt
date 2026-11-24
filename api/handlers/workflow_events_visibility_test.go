package handlers

import (
	"testing"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/services"
)

// The stream carries two families of event: workflow events scoped by an execution, and
// media-derivative progress events scoped by a project. The scope key must separate them, or one
// event type's cached verdict would decide another's visibility.
func TestEventScopeKey_SeparatesExecutionsFromProjects(t *testing.T) {
	same := "11111111-1111-1111-1111-111111111111"

	executionKey := eventScopeKey(services.WorkflowEvent{ExecutionID: same})
	projectKey := eventScopeKey(services.WorkflowEvent{ProjectID: same})

	if executionKey == projectKey {
		t.Fatalf("an execution and a project with the same id share a cache key: %q", executionKey)
	}
	if executionKey != "execution:"+same {
		t.Fatalf("unexpected execution scope key %q", executionKey)
	}
	if projectKey != "project:"+same {
		t.Fatalf("unexpected project scope key %q", projectKey)
	}
}

// The execution id wins when both are present, which is what every pre-existing workflow event
// relies on and must keep.
func TestEventScopeKey_PrefersTheExecutionWhenBothArePresent(t *testing.T) {
	got := eventScopeKey(services.WorkflowEvent{
		ExecutionID: "22222222-2222-2222-2222-222222222222",
		ProjectID:   "33333333-3333-3333-3333-333333333333",
	})
	if got != "execution:22222222-2222-2222-2222-222222222222" {
		t.Fatalf("unexpected scope key %q", got)
	}
}

// Defaulting an unscoped event to visible would turn a future producer's missing field into a
// broadcast to every subscriber. It must be refused, and refused without reaching the database.
func TestCanUserSeeEvent_RefusesAnEventWithNeitherId(t *testing.T) {
	uc := middleware.UserContext{UserID: "44444444-4444-4444-4444-444444444444", OrganizationID: uuid.New()}
	visible := newVisibilityCache(uc)
	visible.lookup = func(string) (uuid.UUID, bool, error) {
		t.Fatalf("an event with neither id reached the execution lookup")
		return uuid.Nil, false, nil
	}

	if canUserSeeEvent(visible, services.WorkflowEvent{}) {
		t.Fatalf("an event with neither an execution id nor a project id was treated as visible")
	}
}

// A project-scoped event is visible exactly to the project's own organization, and the verdict is
// memoised per connection under its own key.
func TestCanSeeProject_IsOrganizationScopedAndMemoised(t *testing.T) {
	orgID := uuid.New()
	projectID := "55555555-5555-5555-5555-555555555555"
	lookups := 0
	original := projectOrgLookup
	projectOrgLookup = func(string) (uuid.UUID, bool, error) {
		lookups++
		return orgID, true, nil
	}
	t.Cleanup(func() { projectOrgLookup = original })

	member := newVisibilityCache(middleware.UserContext{UserID: "u", OrganizationID: orgID})
	if !member.canSeeProject(projectID) || !member.canSeeProject(projectID) { //nolint:staticcheck // duplicate call proves caching
		t.Fatalf("a member of the project's organization was denied its derivative events")
	}
	if lookups != 1 {
		t.Fatalf("expected one lookup per connection, got %d", lookups)
	}

	stranger := newVisibilityCache(middleware.UserContext{UserID: "v", OrganizationID: uuid.New()})
	if stranger.canSeeProject(projectID) {
		t.Fatalf("another organization saw a project's derivative events")
	}
}
