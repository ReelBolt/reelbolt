package middleware

import (
	"context"
	"log"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
	"github.com/vecchiotom/reelbolt/telemetry"
)

type contextKey string

const UserContextKey contextKey = "user"

// MembershipCacheTTL bounds how long a removed member keeps working with a
// token that still names the organization (docs/design/tenancy.md).
const MembershipCacheTTL = 30 * time.Second

type UserContext struct {
	UserID string
	Email  string
	// IsAdmin is retained for one release as an alias of PlatformAdmin.
	IsAdmin            bool
	MustChangePassword bool

	// OrganizationID is the active org (uuid.Nil for a legacy token that
	// predates org claims). OrgRole comes from the database membership row,
	// never from the token.
	OrganizationID uuid.UUID
	OrgRole        string
	PlatformAdmin  bool
}

type membershipCacheKey struct{ user, org uuid.UUID }

type sessionCacheEntry struct {
	state   services.SessionState
	expires time.Time
}

type adminCacheEntry struct {
	admin   bool
	expires time.Time
}

// PlatformAdminLookup reads application_users.is_admin for a user. Platform
// authority always comes from here (or from the membership row, which reads the
// same column), never from a token claim.
type PlatformAdminLookup interface {
	IsPlatformAdmin(ctx context.Context, userID uuid.UUID) (bool, error)
}

// GormPlatformAdminLookup is the production PlatformAdminLookup.
type GormPlatformAdminLookup struct{}

// IsPlatformAdmin is false for an unknown user or a service account.
func (GormPlatformAdminLookup) IsPlatformAdmin(ctx context.Context, userID uuid.UUID) (bool, error) {
	var users []models.ApplicationUser
	err := database.DB.WithContext(ctx).Select("id", "is_admin").
		Where("id = ? AND is_service_account = false", userID).Limit(1).Find(&users).Error
	if err != nil {
		return false, err
	}
	return len(users) == 1 && users[0].IsAdmin, nil
}

type membershipCacheEntry struct {
	info    *services.ActiveMembership // nil: not a member
	expires time.Time
}

// AuthMiddleware validates the bearer token and re-checks, through a short
// in-process cache, that the caller is still a member of the token's org.
type AuthMiddleware struct {
	lookup services.MembershipLookup
	ttl    time.Duration
	now    func() time.Time

	// sessions, when set, makes the middleware reject tokens issued before the
	// user's tokens_invalidated_at (password reset/change). nil skips the check.
	sessions services.SessionLookup

	// admins resolves platform authority for a token with no org claim. nil
	// means such a token never carries platform authority (fail closed).
	admins PlatformAdminLookup

	mu            sync.Mutex
	cache         map[membershipCacheKey]membershipCacheEntry
	sessionsCache map[uuid.UUID]sessionCacheEntry
	adminCache    map[uuid.UUID]adminCacheEntry
}

// NewAuthMiddleware builds the auth middleware over a membership lookup.
func NewAuthMiddleware(lookup services.MembershipLookup, ttl time.Duration) *AuthMiddleware {
	return &AuthMiddleware{
		lookup: lookup,
		ttl:    ttl,
		now:    time.Now,
		cache:  make(map[membershipCacheKey]membershipCacheEntry),

		sessionsCache: make(map[uuid.UUID]sessionCacheEntry),
		adminCache:    make(map[uuid.UUID]adminCacheEntry),
	}
}

// WithPlatformAdminLookup sets where a no-org token's platform authority is
// read (30s cached, like the membership check) and returns the middleware.
func (a *AuthMiddleware) WithPlatformAdminLookup(l PlatformAdminLookup) *AuthMiddleware {
	a.admins = l
	return a
}

func (a *AuthMiddleware) platformAdmin(ctx context.Context, userID uuid.UUID) (bool, error) {
	if a.admins == nil {
		return false, nil
	}
	now := a.now()
	a.mu.Lock()
	if e, ok := a.adminCache[userID]; ok && now.Before(e.expires) {
		a.mu.Unlock()
		return e.admin, nil
	}
	a.mu.Unlock()

	admin, err := a.admins.IsPlatformAdmin(ctx, userID)
	if err != nil {
		return false, err // errors are never cached
	}

	a.mu.Lock()
	if len(a.adminCache) > 10000 {
		for k, e := range a.adminCache {
			if !now.Before(e.expires) {
				delete(a.adminCache, k)
			}
		}
	}
	a.adminCache[userID] = adminCacheEntry{admin: admin, expires: now.Add(a.ttl)}
	a.mu.Unlock()
	return admin, nil
}

// WithSessionLookup enables the TokensInvalidatedAt check (30s cached, like
// the membership check) and returns the middleware for chaining.
func (a *AuthMiddleware) WithSessionLookup(l services.SessionLookup) *AuthMiddleware {
	a.sessions = l
	return a
}

var defaultAuth = NewAuthMiddleware(services.GormMembershipLookup{}, MembershipCacheTTL).
	WithSessionLookup(services.GormSessionLookup{}).
	WithPlatformAdminLookup(GormPlatformAdminLookup{})

// InvalidateSession drops the production session-state cache entry for a user,
// so a password reset/change applies on this replica immediately instead of
// after the cache TTL.
func InvalidateSession(userID uuid.UUID) { defaultAuth.InvalidateSession(userID) }

// InvalidateSession drops one cached session state.
func (a *AuthMiddleware) InvalidateSession(userID uuid.UUID) {
	a.mu.Lock()
	delete(a.sessionsCache, userID)
	a.mu.Unlock()
}

func (a *AuthMiddleware) sessionState(userID uuid.UUID) (services.SessionState, error) {
	now := a.now()
	a.mu.Lock()
	if e, ok := a.sessionsCache[userID]; ok && now.Before(e.expires) {
		a.mu.Unlock()
		return e.state, nil
	}
	a.mu.Unlock()

	st, err := a.sessions.GetSessionState(userID)
	if err != nil {
		return services.SessionState{}, err // errors are never cached
	}

	a.mu.Lock()
	if len(a.sessionsCache) > 10000 {
		for k, e := range a.sessionsCache {
			if !now.Before(e.expires) {
				delete(a.sessionsCache, k)
			}
		}
	}
	a.sessionsCache[userID] = sessionCacheEntry{state: st, expires: now.Add(a.ttl)}
	a.mu.Unlock()
	return st, nil
}

// Auth is the production middleware (database-backed lookup, 30s cache).
func Auth(next http.Handler) http.Handler { return defaultAuth.Handler(next) }

// UnauthorizedResponder decides what a request the middleware could not authenticate or authorize
// receives. The default writes the JSON error every API caller expects; the OAuth authorization
// endpoint passes one that redirects a browser to sign-in instead, because a JSON 401 on a page
// navigation is a dead end for a human (docs/design/mcp-oauth.md §4).
type UnauthorizedResponder func(w http.ResponseWriter, r *http.Request, status int, body string)

// DefaultUnauthorizedResponder is the JSON response every API route has always given. It is exported
// so a caller can wrap it rather than reimplement it.
func DefaultUnauthorizedResponder(w http.ResponseWriter, _ *http.Request, status int, body string) {
	http.Error(w, body, status)
}

// AuthWithResponder is Auth with a caller-supplied answer for a refused request. Everything else —
// token validation, the session-invalidation check and the membership re-check — is the one
// implementation, so a variant can never drift from the real gate.
func AuthWithResponder(respond UnauthorizedResponder) func(http.Handler) http.Handler {
	if respond == nil {
		respond = DefaultUnauthorizedResponder
	}
	return func(next http.Handler) http.Handler {
		return defaultAuth.HandlerWithResponder(next, respond)
	}
}

// InvalidateMembership drops the production cache entry for a user/org pair,
// for callers that just changed membership (switch-org, A4 member removal).
func InvalidateMembership(userID, orgID uuid.UUID) { defaultAuth.Invalidate(userID, orgID) }

// Invalidate drops one cached membership.
func (a *AuthMiddleware) Invalidate(userID, orgID uuid.UUID) {
	a.mu.Lock()
	delete(a.cache, membershipCacheKey{userID, orgID})
	a.mu.Unlock()
}

func (a *AuthMiddleware) membership(ctx context.Context, userID, orgID uuid.UUID) (*services.ActiveMembership, error) {
	key := membershipCacheKey{userID, orgID}
	now := a.now()

	a.mu.Lock()
	if e, ok := a.cache[key]; ok && now.Before(e.expires) {
		a.mu.Unlock()
		return e.info, nil
	}
	a.mu.Unlock()

	info, err := a.lookup.GetMembership(ctx, userID, orgID)
	if err != nil {
		return nil, err // errors are never cached
	}

	a.mu.Lock()
	if len(a.cache) > 10000 { // crude bound; entries are tiny and expire anyway
		for k, e := range a.cache {
			if !now.Before(e.expires) {
				delete(a.cache, k)
			}
		}
	}
	a.cache[key] = membershipCacheEntry{info: info, expires: now.Add(a.ttl)}
	a.mu.Unlock()
	return info, nil
}

// MCPTokenScopeError is the error code returned when an MCP-scoped token reaches a route that
// accepts only sessions.
const MCPTokenScopeError = "mcp_token_scope"

// MCPTokenScopeBody is the 403 body for that refusal.
const MCPTokenScopeBody = `{"error":"` + MCPTokenScopeError + `","message":"This token was issued for MCP clients and cannot be used on this endpoint."}`

// Handler wraps next with authentication.
func (a *AuthMiddleware) Handler(next http.Handler) http.Handler {
	return a.HandlerWithResponder(next, DefaultUnauthorizedResponder)
}

// HandlerWithResponder is Handler with a caller-supplied refusal response (see AuthWithResponder).
// Every refusal below keeps the status and body it always had; only the writer changes.
func (a *AuthMiddleware) HandlerWithResponder(next http.Handler, respond UnauthorizedResponder) http.Handler {
	if respond == nil {
		respond = DefaultUnauthorizedResponder
	}
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		uc, expires, status, body := a.resolve(r)
		if status != 0 {
			respond(w, r, status, body)
			return
		}

		if uc.OrganizationID != uuid.Nil {
			telemetry.TagOrganization(r.Context(), uc.OrganizationID.String())
		}

		SetUserIDHeader(w, uc.UserID)
		ctx := context.WithValue(r.Context(), UserContextKey, uc)
		// A long-lived handler (SSE) re-asks the same question later: the request is resolved
		// again, by this same code, and must still give the same caller.
		ctx = WithSessionCheck(ctx, expires, func() bool {
			again, _, status, _ := a.resolve(r)
			if status == http.StatusServiceUnavailable {
				// A failed lookup says nothing about the session; the next check decides.
				return true
			}
			return status == 0 && again == uc
		})
		next.ServeHTTP(w, r.WithContext(ctx))
	})
}

// resolve authenticates one request: token, session state, then membership. A zero status
// means it passed and uc is the caller; otherwise status and body are the refusal. expires is
// the token's exp (zero when it has none). It is the one implementation behind both the
// middleware and the re-check a stream runs (SessionCheck), so the two cannot drift.
func (a *AuthMiddleware) resolve(r *http.Request) (uc UserContext, expires time.Time, status int, body string) {
	refuse := func(status int, body string) (UserContext, time.Time, int, string) {
		return UserContext{}, time.Time{}, status, body
	}
	var tokenString string

	// Prefer Authorization header; fall back to httpOnly cookie (used by EventSource/SSE).
	if authHeader := r.Header.Get("Authorization"); strings.HasPrefix(authHeader, "Bearer ") {
		tokenString = strings.TrimPrefix(authHeader, "Bearer ")
	} else if cookie, err := r.Cookie("reelbolt_token"); err == nil {
		tokenString = cookie.Value
	}

	if tokenString == "" {
		return refuse(http.StatusUnauthorized, `{"error":"missing or invalid authorization header"}`)
	}
	claims, err := services.ValidateToken(tokenString)
	if err != nil {
		return refuse(http.StatusUnauthorized, `{"error":"invalid token"}`)
	}
	if claims.ExpiresAt != nil {
		expires = claims.ExpiresAt.Time
	}

	// An MCP OAuth access token (non-empty scope claim) is not a session. The Go API has no
	// MCP-scoped route, so it is refused everywhere, before any session or membership lookup.
	if claims.Scope != "" {
		return refuse(http.StatusForbidden, MCPTokenScopeBody)
	}

	if a.sessions != nil {
		userID, perr := uuid.Parse(claims.UserID)
		if perr != nil {
			return refuse(http.StatusUnauthorized, `{"error":"invalid token"}`)
		}
		st, serr := a.sessionState(userID)
		if serr != nil {
			log.Printf("session state lookup failed: %v", serr)
			return refuse(http.StatusServiceUnavailable, `{"error":"service unavailable"}`)
		}
		var iat *time.Time
		if claims.IssuedAt != nil {
			t := claims.IssuedAt.Time
			iat = &t
		}
		if !st.Exists || st.IsServiceAccount || services.TokenIssuedBeforeInvalidation(iat, st.TokensInvalidatedAt) {
			return refuse(http.StatusUnauthorized, `{"error":"session_invalidated"}`)
		}
	}

	uc = UserContext{
		UserID:             claims.UserID,
		Email:              claims.Email,
		MustChangePassword: claims.MustChangePassword,
	}
	// The platformAdmin/isAdmin claims are never trusted: platform authority
	// is application_users.is_admin, read below on both paths.

	if claims.Org != "" {
		userID, uerr := uuid.Parse(claims.UserID)
		orgID, oerr := uuid.Parse(claims.Org)
		if uerr != nil || oerr != nil {
			return refuse(http.StatusUnauthorized, `{"error":"invalid token"}`)
		}
		m, err := a.membership(r.Context(), userID, orgID)
		if err != nil {
			log.Printf("membership lookup failed: %v", err)
			return refuse(http.StatusServiceUnavailable, `{"error":"service unavailable"}`)
		}
		if m == nil {
			return refuse(http.StatusUnauthorized, `{"error":"org_membership_revoked"}`)
		}
		uc.OrganizationID = m.OrgID
		uc.OrgRole = m.Role
		uc.PlatformAdmin = m.PlatformAdmin
	} else {
		userID, uerr := uuid.Parse(claims.UserID)
		if uerr != nil {
			return refuse(http.StatusUnauthorized, `{"error":"invalid token"}`)
		}
		admin, aerr := a.platformAdmin(r.Context(), userID)
		if aerr != nil {
			log.Printf("platform admin lookup failed: %v", aerr)
			return refuse(http.StatusServiceUnavailable, `{"error":"service unavailable"}`)
		}
		uc.PlatformAdmin = admin
	}
	uc.IsAdmin = uc.PlatformAdmin

	if uc.MustChangePassword && r.URL.Path != "/api/v1/auth/change-password" {
		return refuse(http.StatusForbidden, `{"error":"password change required"}`)
	}
	return uc, expires, 0, ""
}

// SessionCheck lets a long-lived handler (an SSE stream) ask again, after the request was
// admitted, whether it would still be admitted: token expiry, tokens_invalidated_at, org
// membership and role, exactly as the middleware judges them (AUTHZ-GO-2, API-GO-04).
type SessionCheck struct {
	// Expires is the token's exp; zero when the token carries none.
	Expires time.Time
	valid   func() bool
}

// StillValid reports whether the request would be admitted now, as the same caller with the
// same organization and role.
func (s *SessionCheck) StillValid() bool { return s.valid == nil || s.valid() }

const sessionCheckKey contextKey = "sessionCheck"

// WithSessionCheck attaches a SessionCheck to ctx. The auth middleware does it for every
// request it admits.
func WithSessionCheck(ctx context.Context, expires time.Time, valid func() bool) context.Context {
	return context.WithValue(ctx, sessionCheckKey, &SessionCheck{Expires: expires, valid: valid})
}

// GetSessionCheck returns the request's SessionCheck, if the auth middleware admitted it.
func GetSessionCheck(r *http.Request) (*SessionCheck, bool) {
	s, ok := r.Context().Value(sessionCheckKey).(*SessionCheck)
	return s, ok
}

// InvalidateUser drops every cached entry of a user on this replica, the session state and
// each org membership, for a change that is not tied to one organization (an admin password
// reset, deletion, or platform-admin change).
func InvalidateUser(userID uuid.UUID) { defaultAuth.InvalidateUser(userID) }

// InvalidateUser drops the user's cached session state, platform-admin flag and memberships.
func (a *AuthMiddleware) InvalidateUser(userID uuid.UUID) {
	a.mu.Lock()
	delete(a.sessionsCache, userID)
	delete(a.adminCache, userID)
	for k := range a.cache {
		if k.user == userID {
			delete(a.cache, k)
		}
	}
	a.mu.Unlock()
}

func GetUserContext(r *http.Request) (UserContext, bool) {
	uc, ok := r.Context().Value(UserContextKey).(UserContext)
	return uc, ok
}
