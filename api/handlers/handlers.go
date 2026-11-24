package handlers

import (
	"net/http"

	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/handlers/admin"
	"github.com/vecchiotom/reelbolt/handlers/auth"
	"github.com/vecchiotom/reelbolt/handlers/health"
	"github.com/vecchiotom/reelbolt/handlers/internalmail"
	"github.com/vecchiotom/reelbolt/handlers/oauth"
	"github.com/vecchiotom/reelbolt/handlers/orgs"
	"github.com/vecchiotom/reelbolt/handlers/runners"
	"github.com/vecchiotom/reelbolt/middleware"
)

var Router *mux.Router

// authMiddleware guards the authenticated and admin subrouters; a variable so a router-level
// test can stand in for the database-backed session check.
var authMiddleware = middleware.Auth

func NewHandler() *mux.Router {
	router := mux.NewRouter()
	Router = router
	return router
}

func RegisterHandlers() {
	if Router == nil {
		NewHandler()
	}

	// One handler, two surfaces: pairing is unauthenticated (the device has no
	// session yet) while approving, listing and revoking require one. They share this
	// instance so they share its signature replay cache.
	runnerHandler := runners.New()

	// Public routes (no auth required)
	health.RegisterHealthRoutes(Router)
	auth.RegisterAuthRoutes(Router)
	runnerHandler.RegisterPublicRoutes(Router)

	// OAuth 2.1 authorization server for MCP clients (docs/design/mcp-oauth.md §4). The metadata,
	// registration, token and revocation endpoints are public; /oauth/authorize is a browser
	// navigation and gets its own auth middleware below, because a JSON 401 on a page navigation is a
	// dead end for a human.
	oauth.RegisterRoutes(Router)
	oauthAuthorize := Router.PathPrefix("").Subrouter()
	oauthAuthorize.Use(oauth.AuthorizeAuthMiddleware())
	oauth.RegisterAuthorizeRoutes(oauthAuthorize)

	// Private surface, reachable only inside the compose network (nginx does not route
	// /internal/*): the Inference API sends the mails it triggers through here (C0/C9a).
	internalmail.RegisterRoutes(Router)

	// Authenticated routes
	authenticated := Router.PathPrefix("").Subrouter()
	authenticated.Use(authMiddleware)
	authenticated.Use(requireJSONOnWrites)
	auth.RegisterAuthenticatedRoutes(authenticated)
	orgs.New().RegisterRoutes(authenticated)
	runnerHandler.RegisterRoutes(authenticated)
	RegisterNotificationsRoutes(authenticated)
	authenticated.HandleFunc("/api/v1/workflows/stats", handleWorkflowStats).Methods("GET")
	authenticated.HandleFunc("/api/v1/workflows/events", handleWorkflowEvents).Methods("GET")
	authenticated.HandleFunc("/api/v1/workflows/executions/{executionId}/events", handleExecutionEventsByID).Methods("GET")
	authenticated.HandleFunc("/api/v1/projects/{projectId}/workflows/{workflowId}/executions/{executionId}/events", handleExecutionEvents).Methods("GET")

	// Admin routes (auth + admin required)
	adminRouter := Router.PathPrefix("/api/v1/admin").Subrouter()
	adminRouter.Use(authMiddleware)
	adminRouter.Use(middleware.PlatformAdmin)
	adminRouter.Use(requireJSONOnWrites)
	admin.RegisterAdminRoutes(adminRouter)
}

// requireJSONOnWrites applies middleware.RequireJSON (the content-type CSRF layer, C11 F-6) to
// every state-changing request of a subrouter, so a route added later cannot miss it. It sits
// behind middleware.Auth, as the per-route wrapping in handlers/auth does. Reads are passed
// through untouched, and so is a write with no body (RequireJSON's own rule).
func requireJSONOnWrites(next http.Handler) http.Handler {
	guarded := middleware.RequireJSON(next.ServeHTTP)
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.Method {
		case http.MethodGet, http.MethodHead, http.MethodOptions:
			next.ServeHTTP(w, r)
		default:
			guarded(w, r)
		}
	})
}
