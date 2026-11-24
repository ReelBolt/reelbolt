package handlers

import (
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"time"

	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
	"gorm.io/gorm"
)

// WorkflowStats holds aggregate execution counts by status.
type WorkflowStats struct {
	Queued    int64 `json:"queued"`
	Active    int64 `json:"active"`
	Completed int64 `json:"completed"`
	Failed    int64 `json:"failed"`
	Total     int64 `json:"total"`
}

// eventScopeKey names the one thing an event is scoped by. Event types are not interchangeable
// here: a workflow event is scoped by its execution, a media-derivative progress event (editor v2,
// P2) by its project, and the key is prefixed so two events of different types can never share a
// cached verdict.
func eventScopeKey(event services.WorkflowEvent) string {
	if event.ExecutionID != "" {
		return "execution:" + event.ExecutionID
	}
	return "project:" + event.ProjectID
}

// canUserSeeEvent decides one event's visibility through the connection's visibility cache. An
// event carrying neither an execution id nor a project id is not visible to anybody: there is
// nothing to check it against, and defaulting such an event to visible would turn a future
// producer's missing field into a broadcast. Decided without touching the database.
func canUserSeeEvent(visible *visibilityCache, event services.WorkflowEvent) bool {
	if event.ExecutionID != "" {
		return visible.canSee(event.ExecutionID)
	}
	if event.ProjectID != "" {
		return visible.canSeeProject(event.ProjectID)
	}
	return false
}

// handleWorkflowStats returns aggregate execution statistics queried directly
// from the workflow_executions table (owned by the WorkflowEngine service).
func handleWorkflowStats(w http.ResponseWriter, r *http.Request) {
	uc, ok := middleware.GetUserContext(r)
	if !ok {
		http.Error(w, `{"error":"unauthorized"}`, http.StatusUnauthorized)
		return
	}

	// Stats are scoped to the caller's active organization. A caller with no
	// active org (legacy token) or a role that may not see executions gets
	// zeros rather than a platform-wide or user-wide count.
	if !canSeeExecutions(uc) {
		writeStats(w, nil)
		return
	}
	query := database.DB.Model(&models.WorkflowExecution{}).
		Where("organization_id = ?", uc.OrganizationID)

	var rows []statusCountRow
	if err := query.
		Select("status, count(*) as count").
		Group("status").
		Scan(&rows).Error; err != nil {
		http.Error(w, `{"error":"failed to query workflow stats"}`, http.StatusInternalServerError)
		return
	}

	writeStats(w, rows)
}

type statusCountRow = struct {
	Status string
	Count  int64
}

// aggregateStats folds per-status counts into WorkflowStats.
func aggregateStats(rows []statusCountRow) WorkflowStats {
	stats := WorkflowStats{}
	for _, row := range rows {
		switch row.Status {
		case "Queued":
			stats.Queued = row.Count
		case "Running":
			stats.Active = row.Count
		case "Completed":
			stats.Completed = row.Count
		case "Failed":
			stats.Failed = row.Count
		}
		stats.Total += row.Count
	}
	return stats
}

func writeStats(w http.ResponseWriter, rows []statusCountRow) {
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(aggregateStats(rows))
}

// ssePingInterval is the keep-alive period of every event stream, and how often a stream
// re-checks that its caller would still be admitted. A variable so tests can shorten it.
var ssePingInterval = 25 * time.Second

// streamSession is what an event stream watches so it does not outlive its authorization
// (AUTHZ-GO-2, API-GO-04): expired fires at the token's exp, and stillValid re-runs the auth
// middleware's own check (session invalidation, membership, role) on the ping tick. The
// stream simply ends; the client reconnects and is answered by the middleware.
type streamSession struct {
	expired    <-chan time.Time
	stillValid func() bool
	stop       func()
}

func watchSession(r *http.Request) streamSession {
	s := streamSession{stillValid: func() bool { return true }, stop: func() {}}
	check, ok := middleware.GetSessionCheck(r)
	if !ok {
		return s
	}
	s.stillValid = check.StillValid
	if !check.Expires.IsZero() {
		timer := time.NewTimer(time.Until(check.Expires))
		s.expired = timer.C
		s.stop = func() { timer.Stop() }
	}
	return s
}

// handleWorkflowEvents streams real-time workflow execution events to the client
// using Server-Sent Events (SSE). The client must accept text/event-stream.
func handleWorkflowEvents(w http.ResponseWriter, r *http.Request) {
	flusher, ok := w.(http.Flusher)
	if !ok {
		http.Error(w, `{"error":"streaming not supported"}`, http.StatusInternalServerError)
		return
	}

	uc, ok := r.Context().Value(middleware.UserContextKey).(middleware.UserContext)
	if !ok {
		http.Error(w, `{"error":"unauthorized"}`, http.StatusUnauthorized)
		return
	}

	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("Connection", "keep-alive")
	w.Header().Set("X-Accel-Buffering", "no") // Disable nginx buffering

	if !canSeeExecutions(uc) {
		http.Error(w, `{"error":"not found"}`, http.StatusNotFound)
		return
	}

	ch, ok := services.Hub.Subscribe(services.HubScopeKey(uc.OrganizationID, uc.UserID))
	if !ok {
		http.Error(w, `{"error":"too many active workflow streams"}`, http.StatusTooManyRequests)
		return
	}
	defer services.Hub.Unsubscribe(ch)
	visible := newVisibilityCache(uc)

	// Send an initial "connected" event so the client knows the stream is live.
	fmt.Fprintf(w, "event: connected\ndata: {\"ts\":%d}\n\n", time.Now().UnixMilli())
	flusher.Flush()

	// Keep-alive ping every 25 seconds to prevent proxy timeouts.
	ticker := time.NewTicker(ssePingInterval)
	defer ticker.Stop()
	session := watchSession(r)
	defer session.stop()

	for {
		select {
		case <-r.Context().Done():
			return

		case <-session.expired:
			return

		case <-ticker.C:
			if !session.stillValid() {
				return
			}
			fmt.Fprintf(w, ": ping\n\n")
			flusher.Flush()

		case event, open := <-ch:
			if !open {
				return
			}

			if !canUserSeeEvent(visible, event) {
				continue
			}

			data, err := json.Marshal(event)
			if err != nil {
				continue
			}
			fmt.Fprintf(w, "event: %s\ndata: %s\n\n", event.Type, data)
			flusher.Flush()
		}
	}
}

// handleExecutionEvents streams real-time workflow events for a SINGLE execution ID.
// Security: the execution must belong to the caller's active organization (404 otherwise).
func handleExecutionEvents(w http.ResponseWriter, r *http.Request) {
	flusher, ok := w.(http.Flusher)
	if !ok {
		http.Error(w, `{"error":"streaming not supported"}`, http.StatusInternalServerError)
		return
	}

	// Extract route parameters
	vars := mux.Vars(r)
	projectID := vars["projectId"]
	workflowID := vars["workflowId"]
	executionID := vars["executionId"]

	// Get user context from auth middleware
	uc, ok := r.Context().Value(middleware.UserContextKey).(middleware.UserContext)
	if !ok {
		http.Error(w, `{"error":"unauthorized"}`, http.StatusUnauthorized)
		return
	}

	// Validate execution belongs to the requested project + workflow path.
	var execution models.WorkflowExecution
	if err := database.DB.Where("id = ? AND project_id = ? AND workflow_definition_id = ?", executionID, projectID, workflowID).First(&execution).Error; err != nil {
		if errors.Is(err, gorm.ErrRecordNotFound) {
			http.Error(w, `{"error":"execution not found"}`, http.StatusNotFound)
			return
		}
		http.Error(w, `{"error":"failed to validate execution"}`, http.StatusInternalServerError)
		return
	}

	if !denyExecution(w, uc, &execution) {
		return
	}

	streamExecutionEvents(w, r, flusher, uc, executionID)
}

// handleExecutionEventsByID streams workflow events for a single execution ID
// under a route rooted at /api/v1/workflows/* (which is already proxied to go-api by nginx).
func handleExecutionEventsByID(w http.ResponseWriter, r *http.Request) {
	flusher, ok := w.(http.Flusher)
	if !ok {
		http.Error(w, `{"error":"streaming not supported"}`, http.StatusInternalServerError)
		return
	}

	vars := mux.Vars(r)
	executionID := vars["executionId"]

	uc, ok := r.Context().Value(middleware.UserContextKey).(middleware.UserContext)
	if !ok {
		http.Error(w, `{"error":"unauthorized"}`, http.StatusUnauthorized)
		return
	}

	var execution models.WorkflowExecution
	if err := database.DB.Where("id = ?", executionID).First(&execution).Error; err != nil {
		if errors.Is(err, gorm.ErrRecordNotFound) {
			http.Error(w, `{"error":"execution not found"}`, http.StatusNotFound)
			return
		}
		http.Error(w, `{"error":"failed to validate execution"}`, http.StatusInternalServerError)
		return
	}

	if !denyExecution(w, uc, &execution) {
		return
	}

	streamExecutionEvents(w, r, flusher, uc, executionID)
}

func streamExecutionEvents(w http.ResponseWriter, r *http.Request, flusher http.Flusher, uc middleware.UserContext, executionID string) {
	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("Connection", "keep-alive")
	w.Header().Set("X-Accel-Buffering", "no")

	ch, ok := services.Hub.Subscribe(services.ExecutionHubScopeKey(uc.OrganizationID, uc.UserID))
	if !ok {
		http.Error(w, `{"error":"too many active workflow streams"}`, http.StatusTooManyRequests)
		return
	}
	defer services.Hub.Unsubscribe(ch)

	fmt.Fprintf(w, "event: connected\ndata: {\"ts\":%d}\n\n", time.Now().UnixMilli())
	flusher.Flush()

	ticker := time.NewTicker(ssePingInterval)
	defer ticker.Stop()
	session := watchSession(r)
	defer session.stop()

	for {
		select {
		case <-r.Context().Done():
			return
		case <-session.expired:
			return
		case <-ticker.C:
			if !session.stillValid() {
				return
			}
			fmt.Fprintf(w, ": ping\n\n")
			flusher.Flush()
		case event, open := <-ch:
			if !open {
				return
			}
			if event.ExecutionID != executionID {
				continue
			}
			data, err := json.Marshal(event)
			if err != nil {
				continue
			}
			fmt.Fprintf(w, "event: %s\ndata: %s\n\n", event.Type, data)
			flusher.Flush()
		}
	}
}

// denyExecution reports whether the caller may see the execution; when not, it
// has already written the error. Another organization's execution is 404
// (existence never leaks); a same-org role that may not see executions
// (Billing) is 403.
func denyExecution(w http.ResponseWriter, uc middleware.UserContext, execution *models.WorkflowExecution) bool {
	switch executionAccessFor(uc, execution.OrganizationID) {
	case accessGranted:
		return true
	case accessForbidden:
		http.Error(w, `{"error":"forbidden"}`, http.StatusForbidden)
	default:
		http.Error(w, `{"error":"execution not found"}`, http.StatusNotFound)
	}
	return false
}
