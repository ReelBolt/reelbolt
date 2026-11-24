package main

import (
	"crypto/subtle"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/mux"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// This file is the engine-facing API of the gateway ("Scope / files" of D5b):
//
//	POST   /internal/v1/jobs            (idempotent)
//	GET    /internal/v1/jobs/{id}       (long-poll: wait=30s)
//	DELETE /internal/v1/jobs/{id}       (cancel)
//	GET    /internal/v1/runners         (is a runner online?)
//
// It is NOT public: it creates and cancels work and reports which organisations
// have runners connected, so every route requires the shared internal token and
// the whole surface is refused when none is configured (fail closed — an
// unauthenticated job-creation endpoint would let anyone enqueue compute on
// someone else's runners).

// internalTokenHeader is the header the token rides in. It is the standard
// bearer form so an operator can point a generic client at it.
const internalTokenHeader = "Authorization"

// maxWait is the longest long-poll a caller may ask for. The specification's
// example is 30 s; a longer wait buys nothing (the caller can re-poll) and holds
// a connection.
const maxWait = 60 * time.Second

// pollInterval is how often a long-poll re-reads the job. The queue's NOTIFY
// wakes claimers, not waiters: a waiter only ever asks about ONE job, whose
// transitions are written by this gateway or its peers, so polling is the honest
// implementation (an in-process wait would miss a transition made by another
// replica) and it is bounded by the wait window.
const pollInterval = 200 * time.Millisecond

// registerInternalRoutes wires the engine-facing surface onto the router.
// plain is true on the plain listener, where RUNNER_GATEWAY_INTERNAL_PLAIN_LOCAL_ONLY
// may restrict the surface to local peers; the TLS listener passes false.
func (g *gateway) registerInternalRoutes(router *mux.Router, plain bool) {
	internal := router.PathPrefix("/internal/v1").Subrouter()
	if plain && g.cfg.InternalPlainLocalOnly {
		internal.Use(g.requireLocalPeer)
	}
	internal.Use(g.requireInternalToken)
	internal.HandleFunc("/jobs", g.handleCreateJob).Methods(http.MethodPost)
	internal.HandleFunc("/jobs/{id}", g.handleGetJob).Methods(http.MethodGet)
	internal.HandleFunc("/jobs/{id}", g.handleCancelJob).Methods(http.MethodDelete)
	internal.HandleFunc("/runners", g.handleListRunners).Methods(http.MethodGet)
	// D5c: one op on a session, and the scaling signal. Both sit behind the same
	// token as the job surface, because both either run code on a customer's
	// machine (an op) or describe the fleet.
	internal.HandleFunc("/sessions/{id}/ops", g.handleSessionOp).Methods(http.MethodPost)
	internal.HandleFunc("/scaling", g.handleScaling).Methods(http.MethodGet)
	// The local-LLM relay (llm_relay.go): the user's own model server, reached
	// through their runner. Same token as the rest of the internal surface.
	g.registerLLMRoutes(internal)
	// On-demand pool: node enrolment, revocation and the waker heartbeat.
	g.registerPoolRoutes(internal)
}

// requireInternalToken authenticates the caller. A gateway with no configured
// token refuses every internal request rather than serving them openly.
func (g *gateway) requireInternalToken(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if g.cfg.InternalToken == "" {
			w.Header().Set(llmRelayErrorHeader, "internal_api_disabled")
			writeJSON(w, http.StatusServiceUnavailable, map[string]any{
				"error":   "internal_api_disabled",
				"message": "RUNNER_INTERNAL_TOKEN is not configured, so the internal API is closed",
			})
			return
		}
		presented := strings.TrimSpace(strings.TrimPrefix(r.Header.Get(internalTokenHeader), "Bearer "))
		if subtle.ConstantTimeCompare([]byte(presented), []byte(g.cfg.InternalToken)) != 1 {
			w.Header().Set(llmRelayErrorHeader, "unauthorized")
			writeJSON(w, http.StatusUnauthorized, map[string]any{"error": "unauthorized"})
			return
		}
		next.ServeHTTP(w, r)
	})
}

// createJobRequest is POST /internal/v1/jobs.
type createJobRequest struct {
	IdempotencyKey string          `json:"idempotencyKey,omitempty"`
	OrgID          string          `json:"orgId"`
	ExecutionID    string          `json:"executionId"`
	StepID         string          `json:"stepId,omitempty"`
	JobType        string          `json:"jobType"`
	Target         string          `json:"target"`
	Priority       int             `json:"priority,omitempty"`
	MaxAttempts    int             `json:"maxAttempts,omitempty"`
	Iteration      int             `json:"iteration,omitempty"`
	Purpose        string          `json:"purpose,omitempty"`
	RequiredCaps   *RequiredCaps   `json:"requiredCaps,omitempty"`
	Spec           json.RawMessage `json:"spec,omitempty"`
}

// jobView is the wire shape of a job for the engine. It carries no token, no
// presigned URL and no credential: the engine's own copy of the spec is the
// authority for those.
type jobView struct {
	JobID       string          `json:"jobId"`
	SessionID   string          `json:"sessionId,omitempty"`
	Status      string          `json:"status"`
	JobType     string          `json:"jobType"`
	Target      string          `json:"target"`
	Attempt     int             `json:"attempt"`
	MaxAttempts int             `json:"maxAttempts"`
	LeaseEpoch  int64           `json:"leaseEpoch"`
	Created     bool            `json:"created,omitempty"`
	Result      json.RawMessage `json:"result,omitempty"`
	ErrorClass  string          `json:"errorClass,omitempty"`
	// SessionLost is set on an Expired job: the engine is told session_lost and
	// decides whether to open a new session or fail the step.
	SessionLost bool `json:"sessionLost,omitempty"`
	// TimedOut is set on a long-poll that returned without a terminal state.
	TimedOut bool `json:"timedOut,omitempty"`
}

func jobToView(job Job, created bool) jobView {
	view := jobView{
		JobID: job.ID, SessionID: job.SessionID, Status: job.Status, JobType: job.JobType,
		Target: job.Target, Attempt: job.Attempt, MaxAttempts: job.MaxAttempts,
		LeaseEpoch: job.LeaseEpoch, Created: created, ErrorClass: job.ErrorClass,
		SessionLost: job.Status == jobExpired,
	}
	if strings.TrimSpace(job.ResultJSON) != "" && job.ResultJSON != "null" {
		view.Result = json.RawMessage(job.ResultJSON)
	}
	return view
}

// handleCreateJob creates (or returns) a job.
func (g *gateway) handleCreateJob(w http.ResponseWriter, r *http.Request) {
	var req createJobRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 1<<20)).Decode(&req); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "bad_request", "message": err.Error()})
		return
	}
	if req.Target != targetCloudPool && req.Target != targetOrgRunner {
		writeJSON(w, http.StatusBadRequest, map[string]any{
			"error":   "bad_request",
			"message": fmt.Sprintf("target must be %s or %s", targetCloudPool, targetOrgRunner),
		})
		return
	}
	for name, value := range map[string]string{"orgId": req.OrgID, "executionId": req.ExecutionID} {
		if _, err := uuid.Parse(value); err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]any{
				"error": "bad_request", "message": name + " must be a uuid",
			})
			return
		}
	}
	if req.StepID != "" {
		if _, err := uuid.Parse(req.StepID); err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]any{
				"error": "bad_request", "message": "stepId must be a uuid",
			})
			return
		}
	}
	if strings.TrimSpace(req.JobType) == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "bad_request", "message": "jobType is required"})
		return
	}
	key := strings.TrimSpace(req.IdempotencyKey)
	if key == "" {
		// The engine may supply the key; when it does not, the gateway computes
		// the one the specification defines, so a retry with the same components
		// still deduplicates.
		key = IdempotencyKey(req.ExecutionID, req.StepID, req.Iteration, req.Purpose)
	}

	params := CreateJobParams{
		IdempotencyKey: key, OrganizationID: req.OrgID, ExecutionID: req.ExecutionID,
		StepID: req.StepID, JobType: req.JobType, Target: req.Target,
		Priority: req.Priority, MaxAttempts: req.MaxAttempts, Spec: req.Spec,
	}
	if req.RequiredCaps != nil {
		params.RequiredCaps = *req.RequiredCaps
	}
	job, created, err := g.jobs.CreateJob(r.Context(), params)
	if err != nil {
		log.Printf("internal api: creating job failed: %v", err)
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "internal"})
		return
	}
	if created {
		g.metrics.jobsCreated.Inc()
	}
	g.pokeAssigners()
	view := jobToView(job, created)
	status := http.StatusOK
	if created {
		status = http.StatusCreated
	}
	writeJSON(w, status, view)
}

// handleGetJob reads one job, optionally waiting for it to reach a terminal
// state.
func (g *gateway) handleGetJob(w http.ResponseWriter, r *http.Request) {
	jobID := mux.Vars(r)["id"]
	if _, err := uuid.Parse(jobID); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "bad_request", "message": "id must be a uuid"})
		return
	}
	wait, err := parseWait(r.URL.Query().Get("wait"))
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "bad_request", "message": err.Error()})
		return
	}
	deadline := time.Now().Add(wait)
	for {
		job, err := g.jobs.GetJob(r.Context(), jobID)
		if err != nil {
			if errors.Is(err, ErrJobNotFound) {
				writeJSON(w, http.StatusNotFound, map[string]any{"error": "job_not_found"})
				return
			}
			log.Printf("internal api: reading job %s failed: %v", shortID(jobID), err)
			writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "internal"})
			return
		}
		if job.Terminal() {
			writeJSON(w, http.StatusOK, jobToView(job, false))
			return
		}
		if wait <= 0 || !time.Now().Before(deadline) {
			view := jobToView(job, false)
			view.TimedOut = wait > 0
			writeJSON(w, http.StatusOK, view)
			return
		}
		select {
		case <-r.Context().Done():
			return
		case <-time.After(pollInterval):
		}
	}
}

// handleCancelJob cancels a job. A queued job is Cancelled immediately; a leased
// one is asked to stop and becomes Cancelled when the runner acknowledges it or
// when the grace window closes, whichever comes first.
func (g *gateway) handleCancelJob(w http.ResponseWriter, r *http.Request) {
	jobID := mux.Vars(r)["id"]
	if _, err := uuid.Parse(jobID); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "bad_request", "message": "id must be a uuid"})
		return
	}
	job, err := g.jobs.GetJob(r.Context(), jobID)
	if err != nil {
		if errors.Is(err, ErrJobNotFound) {
			writeJSON(w, http.StatusNotFound, map[string]any{"error": "job_not_found"})
			return
		}
		log.Printf("internal api: reading job %s failed: %v", shortID(jobID), err)
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "internal"})
		return
	}
	g.cancelJob(r.Context(), job, protocol.CancelUser)
	current, err := g.jobs.GetJob(r.Context(), jobID)
	if err != nil {
		current = job
	}
	writeJSON(w, http.StatusAccepted, jobToView(current, false))
}

// runnersResponse is GET /internal/v1/runners.
type runnersResponse struct {
	Online  int          `json:"online"`
	Runners []runnerView `json:"runners"`
	// Pool is the on-demand pool waker's state, computed from the stored heartbeat.
	Pool poolView `json:"pool"`
}

// runnerView is one live connection, as much as the engine may know.
type runnerView struct {
	RunnerID     string    `json:"runnerId"`
	Kind         string    `json:"kind"`
	OrgID        string    `json:"orgId,omitempty"`
	Name         string    `json:"name"`
	FreeSlots    int       `json:"freeSlots"`
	Running      int       `json:"running"`
	Draining     bool      `json:"draining"`
	ConnectedAt  time.Time `json:"connectedAt"`
	JobTypes     []string  `json:"jobTypes"`
	Container    string    `json:"container"`
	AgentVersion string    `json:"agentVersion"`
}

// handleListRunners answers "is a runner online?" for the engine's selector.
//
// It is per instance, which is the honest answer for a single-replica
// deployment and a conservative one for a multi-replica deployment: a "no"
// never means "nobody anywhere", it means "not on this replica", which is why
// the engine treats it as a hint and never as a routing decision it can rely
// on. The registry is deliberately not shared between replicas (D5c routes
// sessions by runner_sessions.gateway_instance instead).
func (g *gateway) handleListRunners(w http.ResponseWriter, r *http.Request) {
	orgID := r.URL.Query().Get("org")
	caps := splitList(r.URL.Query().Get("caps"))

	out := runnersResponse{Runners: []runnerView{}}
	for _, c := range g.registry.snapshot() {
		snap := c.snapshot()
		if orgID != "" && snap.OrgID != orgID {
			continue
		}
		if len(caps) > 0 && !supportsAll(snap.Caps, caps) {
			continue
		}
		out.Runners = append(out.Runners, runnerView{
			RunnerID: snap.RunnerID, Kind: snap.Kind, OrgID: snap.OrgID, Name: snap.Name,
			FreeSlots: snap.FreeSlots, Running: len(snap.Running), Draining: snap.Draining,
			ConnectedAt: snap.Connected, JobTypes: snap.Caps.JobTypes,
			Container: snap.Caps.Container, AgentVersion: snap.Caps.AgentVersion,
		})
	}
	out.Online = len(out.Runners)
	out.Pool = g.poolStatus(r.Context())
	writeJSON(w, http.StatusOK, out)
}

// supportsAll reports whether a capability document advertises every job type
// asked for.
func supportsAll(caps protocol.Capabilities, want []string) bool {
	for _, jobType := range want {
		if !caps.Supports(jobType) {
			return false
		}
	}
	return true
}

// parseWait reads the long-poll parameter. It is a Go duration ("30s") so the
// caller controls the unit; a bare integer is read as seconds.
func parseWait(raw string) (time.Duration, error) {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return 0, nil
	}
	if seconds, err := strconv.Atoi(raw); err == nil {
		if seconds < 0 {
			return 0, errors.New("wait must not be negative")
		}
		if time.Duration(seconds)*time.Second > maxWait {
			return maxWait, nil
		}
		return time.Duration(seconds) * time.Second, nil
	}
	d, err := time.ParseDuration(raw)
	if err != nil {
		return 0, fmt.Errorf("wait %q is not a duration", raw)
	}
	if d < 0 {
		return 0, errors.New("wait must not be negative")
	}
	if d > maxWait {
		return maxWait, nil
	}
	return d, nil
}

func splitList(raw string) []string {
	var out []string
	for _, part := range strings.Split(raw, ",") {
		if v := strings.TrimSpace(part); v != "" {
			out = append(out, v)
		}
	}
	return out
}
