// Package orgs implements /api/v1/orgs: organizations, memberships and invites
// (docs/design/tenancy.md). Rules enforced here:
//
//   - A resource in another organization, an unknown one, or the Platform org
//     is 404 (existence never leaks); a too-low role inside the caller's own
//     organization is 403.
//   - The caller's authority in org {id} is read from the database membership,
//     not from the token, so a user can manage any org they belong to, not only
//     the active one.
//   - Personal orgs have one Owner and cannot be invited into, left, or deleted.
//   - A Team org always keeps at least one Owner.
package orgs

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"log"
	"net/http"
	"net/mail"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
	"gorm.io/gorm"
)

const (
	// InviteTTL is how long an invite link stays valid.
	InviteTTL  = 7 * 24 * time.Hour
	maxNameLen = 100
)

// Handler holds the collaborators of the organizations API; tests replace them.
type Handler struct {
	Store  services.OrgStore
	Lookup services.MembershipLookup
	Mailer func() services.Mailer
	// Invalidate evicts a cached membership (auth middleware, 30s TTL) the
	// moment a role changes or a member leaves, so it takes effect at once.
	Invalidate func(userID, orgID uuid.UUID)
	Now        func() time.Time
	// BaseURL returns the public origin used to build invite links.
	BaseURL func() string
}

// New returns the production handler.
func New() *Handler {
	return &Handler{
		Store:      services.GormOrgStore{},
		Lookup:     services.GormMembershipLookup{},
		Mailer:     services.DefaultMailer,
		Invalidate: middleware.InvalidateMembership,
		Now:        time.Now,
		BaseURL:    func() string { return config.Cfg.LinkBaseURL },
	}
}

// RegisterRoutes mounts the API on a router that already runs middleware.Auth.
func (h *Handler) RegisterRoutes(r *mux.Router) {
	r.HandleFunc("/api/v1/orgs", h.listOrgs).Methods("GET")
	r.HandleFunc("/api/v1/orgs", h.createOrg).Methods("POST")
	// Registered before /{id} routes so "invites" is never read as an org id.
	r.HandleFunc("/api/v1/orgs/invites/accept", h.acceptInvite).Methods("POST")
	r.HandleFunc("/api/v1/orgs/{id}", h.getOrg).Methods("GET")
	r.HandleFunc("/api/v1/orgs/{id}", h.updateOrg).Methods("PATCH")
	r.HandleFunc("/api/v1/orgs/{id}", h.deleteOrg).Methods("DELETE")
	r.HandleFunc("/api/v1/orgs/{id}/members", h.listMembers).Methods("GET")
	r.HandleFunc("/api/v1/orgs/{id}/members/{userId}", h.updateMember).Methods("PATCH")
	r.HandleFunc("/api/v1/orgs/{id}/members/{userId}", h.removeMember).Methods("DELETE")
	r.HandleFunc("/api/v1/orgs/{id}/invites", h.createInvite).Methods("POST")
	r.HandleFunc("/api/v1/orgs/{id}/invites", h.listInvites).Methods("GET")
	r.HandleFunc("/api/v1/orgs/{id}/invites/{inviteId}", h.revokeInvite).Methods("DELETE")
}

// ---- role rules (pure, tested directly) ----

// CanManageMembers: list members, change roles, remove others, invites, rename.
func CanManageMembers(role string) bool {
	return role == models.OrgRoleOwner || role == models.OrgRoleAdmin
}

// CanViewMembers: Owners and Admins manage; Members may list; Billing sees none.
func CanViewMembers(role string) bool {
	return CanManageMembers(role) || role == models.OrgRoleMember
}

// ValidRole reports whether s is a role an organization member can hold.
func ValidRole(s string) bool {
	switch s {
	case models.OrgRoleOwner, models.OrgRoleAdmin, models.OrgRoleMember, models.OrgRoleBilling:
		return true
	}
	return false
}

// CanAssign reports whether an actor may grant `role` or change a member that
// currently holds `current` ("" for a new invite). Admins can never create or
// touch Owners; only Owners can.
func CanAssign(actor, current, role string) bool {
	if !CanManageMembers(actor) {
		return false
	}
	if actor == models.OrgRoleOwner {
		return true
	}
	return role != models.OrgRoleOwner && current != models.OrgRoleOwner
}

// HashInviteToken is the SHA-256 (hex) stored in organization_invites.token_hash.
func HashInviteToken(token string) string {
	sum := sha256.Sum256([]byte(token))
	return hex.EncodeToString(sum[:])
}

// newInviteToken returns a URL-safe token of 32 random bytes.
func newInviteToken() (string, error) {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return base64.RawURLEncoding.EncodeToString(b), nil
}

// ---- plumbing ----

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func fail(w http.ResponseWriter, status int, msg string) {
	writeJSON(w, status, map[string]string{"error": msg})
}

func serverError(w http.ResponseWriter, what string, err error) {
	log.Printf("orgs: %s: %v", what, err)
	fail(w, http.StatusInternalServerError, what)
}

type caller struct {
	userID uuid.UUID
	email  string
}

func callerOf(w http.ResponseWriter, r *http.Request) (caller, bool) {
	uc, ok := middleware.GetUserContext(r)
	if !ok {
		fail(w, http.StatusUnauthorized, "unauthorized")
		return caller{}, false
	}
	id, err := uuid.Parse(uc.UserID)
	if err != nil {
		fail(w, http.StatusUnauthorized, "unauthorized")
		return caller{}, false
	}
	return caller{userID: id, email: uc.Email}, true
}

// orgScope is the caller's standing in the org named by the {id} path segment.
type orgScope struct {
	caller
	orgID uuid.UUID
	name  string
	kind  string
	role  string
}

func (s orgScope) personal() bool { return s.kind == models.OrgKindPersonal }

// scope resolves the caller and their membership in {id}. A non-member, an
// unknown org and the Platform org are all 404.
func (h *Handler) scope(w http.ResponseWriter, r *http.Request) (orgScope, bool) {
	c, ok := callerOf(w, r)
	if !ok {
		return orgScope{}, false
	}
	orgID, err := uuid.Parse(mux.Vars(r)["id"])
	if err != nil {
		fail(w, http.StatusNotFound, "organization not found")
		return orgScope{}, false
	}
	m, err := h.Lookup.GetMembership(r.Context(), c.userID, orgID)
	if err != nil {
		serverError(w, "membership lookup failed", err)
		return orgScope{}, false
	}
	if m == nil || m.OrgKind == models.OrgKindPlatform {
		fail(w, http.StatusNotFound, "organization not found")
		return orgScope{}, false
	}
	return orgScope{caller: c, orgID: orgID, name: m.OrgName, kind: m.OrgKind, role: m.Role}, true
}

func decode(w http.ResponseWriter, r *http.Request, v any) bool {
	r.Body = http.MaxBytesReader(w, r.Body, 64<<10)
	if err := json.NewDecoder(r.Body).Decode(v); err != nil {
		fail(w, http.StatusBadRequest, "invalid request body")
		return false
	}
	return true
}

func cleanName(s string) (string, bool) {
	s = strings.TrimSpace(s)
	return s, s != "" && utf8.RuneCountInString(s) <= maxNameLen
}

// toOrgResponse renders one organization. An empty compute_preference reads as
// "Auto" — the column's own default, and the value the engine falls back to when
// it cannot read the row (DbComputeEntitlements) — so the dashboard and the
// engine agree about a row written before the column was ever set, rather than
// the UI showing a blank choice.
func toOrgResponse(o models.Organization, role string) OrgResponse {
	preference := o.ComputePreference
	if preference == "" {
		preference = models.ComputePreferenceAuto
	}
	return OrgResponse{
		ID: o.ID.String(), Name: o.Name, Slug: o.Slug, Kind: o.Kind, Role: role,
		CreatedAt: o.CreatedAt, ComputePreference: preference,
	}
}

// ---- organizations ----

func (h *Handler) listOrgs(w http.ResponseWriter, r *http.Request) {
	c, ok := callerOf(w, r)
	if !ok {
		return
	}
	list, err := h.Store.ListUserOrgs(r.Context(), c.userID)
	if err != nil {
		serverError(w, "failed to list organizations", err)
		return
	}
	out := make([]OrgResponse, len(list))
	for i, s := range list {
		out[i] = toOrgResponse(s.Org, s.Role)
	}
	writeJSON(w, http.StatusOK, out)
}

func (h *Handler) createOrg(w http.ResponseWriter, r *http.Request) {
	c, ok := callerOf(w, r)
	if !ok {
		return
	}
	var req CreateOrgRequest
	if !decode(w, r, &req) {
		return
	}
	name, valid := cleanName(req.Name)
	if !valid {
		fail(w, http.StatusBadRequest, "name is required (1-100 characters)")
		return
	}
	org, err := h.Store.CreateTeamOrg(r.Context(), c.userID, name)
	if err != nil {
		serverError(w, "failed to create organization", err)
		return
	}
	writeJSON(w, http.StatusCreated, toOrgResponse(*org, models.OrgRoleOwner))
}

func (h *Handler) getOrg(w http.ResponseWriter, r *http.Request) {
	s, ok := h.scope(w, r)
	if !ok {
		return
	}
	org, err := h.Store.GetOrganization(r.Context(), s.orgID)
	if err != nil {
		serverError(w, "failed to load organization", err)
		return
	}
	if org == nil {
		fail(w, http.StatusNotFound, "organization not found")
		return
	}
	writeJSON(w, http.StatusOK, toOrgResponse(*org, s.role))
}

func (h *Handler) updateOrg(w http.ResponseWriter, r *http.Request) {
	s, ok := h.scope(w, r)
	if !ok {
		return
	}
	if !CanManageMembers(s.role) {
		fail(w, http.StatusForbidden, "insufficient role")
		return
	}
	var req UpdateOrgRequest
	if !decode(w, r, &req) {
		return
	}
	if req.Name == nil && req.ComputePreference == nil {
		fail(w, http.StatusBadRequest, "nothing to update")
		return
	}
	if req.Name != nil {
		name, valid := cleanName(*req.Name)
		if !valid {
			fail(w, http.StatusBadRequest, "name is required (1-100 characters)")
			return
		}
		if err := h.Store.UpdateOrgName(r.Context(), s.orgID, name); err != nil {
			serverError(w, "failed to update organization", err)
			return
		}
	}
	if req.ComputePreference != nil {
		// Refuse anything this build does not know rather than storing it: the
		// engine switches over these four values exhaustively, so an invented one
		// would be read as "no runner" and never as what the user asked for.
		if !models.ValidComputePreference(*req.ComputePreference) {
			fail(w, http.StatusBadRequest, "computePreference must be Auto, CloudOnly, PreferRunner or RunnerOnly")
			return
		}
		if err := h.Store.UpdateOrgComputePreference(r.Context(), s.orgID, *req.ComputePreference); err != nil {
			serverError(w, "failed to update organization", err)
			return
		}
	}
	org, err := h.Store.GetOrganization(r.Context(), s.orgID)
	if err != nil || org == nil {
		serverError(w, "failed to load organization", err)
		return
	}
	writeJSON(w, http.StatusOK, toOrgResponse(*org, s.role))
}

func (h *Handler) deleteOrg(w http.ResponseWriter, r *http.Request) {
	s, ok := h.scope(w, r)
	if !ok {
		return
	}
	if s.role != models.OrgRoleOwner {
		fail(w, http.StatusForbidden, "only an owner can delete the organization")
		return
	}
	if s.personal() {
		fail(w, http.StatusBadRequest, "a personal workspace cannot be deleted")
		return
	}
	// Collect members first: their cached memberships must be evicted so the
	// deletion takes effect now rather than after the cache TTL.
	members, err := h.Store.ListMembers(r.Context(), s.orgID)
	if err != nil {
		serverError(w, "failed to delete organization", err)
		return
	}
	if err := h.Store.DeleteOrg(r.Context(), s.orgID); err != nil {
		serverError(w, "failed to delete organization", err)
		return
	}
	for _, m := range members {
		h.Invalidate(m.UserID, s.orgID)
	}
	w.WriteHeader(http.StatusNoContent)
}

// ---- members ----

func (h *Handler) listMembers(w http.ResponseWriter, r *http.Request) {
	s, ok := h.scope(w, r)
	if !ok {
		return
	}
	if !CanViewMembers(s.role) {
		fail(w, http.StatusForbidden, "insufficient role")
		return
	}
	members, err := h.Store.ListMembers(r.Context(), s.orgID)
	if err != nil {
		serverError(w, "failed to list members", err)
		return
	}
	out := make([]MemberResponse, len(members))
	for i, m := range members {
		out[i] = MemberResponse{UserID: m.UserID.String(), Email: m.Email, DisplayName: m.DisplayName, Role: m.Role, JoinedAt: m.JoinedAt}
	}
	writeJSON(w, http.StatusOK, out)
}

func (h *Handler) targetMember(w http.ResponseWriter, r *http.Request, s orgScope) (uuid.UUID, string, bool) {
	target, err := uuid.Parse(mux.Vars(r)["userId"])
	if err != nil {
		fail(w, http.StatusNotFound, "member not found")
		return uuid.Nil, "", false
	}
	role, found, err := h.Store.GetMemberRole(r.Context(), s.orgID, target)
	if err != nil {
		serverError(w, "failed to load member", err)
		return uuid.Nil, "", false
	}
	if !found {
		fail(w, http.StatusNotFound, "member not found")
		return uuid.Nil, "", false
	}
	return target, role, true
}

// guardLastOwner refuses an operation that would remove or demote the only
// Owner. The store re-checks atomically; this gives the clean early answer.
func (h *Handler) guardLastOwner(ctx context.Context, w http.ResponseWriter, orgID uuid.UUID, currentRole string) bool {
	if currentRole != models.OrgRoleOwner {
		return true
	}
	n, err := h.Store.CountOwners(ctx, orgID)
	if err != nil {
		serverError(w, "failed to check owners", err)
		return false
	}
	if n <= 1 {
		fail(w, http.StatusConflict, "the last owner cannot be removed or demoted")
		return false
	}
	return true
}

func (h *Handler) updateMember(w http.ResponseWriter, r *http.Request) {
	s, ok := h.scope(w, r)
	if !ok {
		return
	}
	if !CanManageMembers(s.role) {
		fail(w, http.StatusForbidden, "insufficient role")
		return
	}
	var req UpdateMemberRequest
	if !decode(w, r, &req) {
		return
	}
	if !ValidRole(req.Role) {
		fail(w, http.StatusBadRequest, "role must be Owner, Admin, Member or Billing")
		return
	}
	if s.personal() {
		fail(w, http.StatusBadRequest, "a personal workspace has a single owner")
		return
	}
	target, current, ok := h.targetMember(w, r, s)
	if !ok {
		return
	}
	if !CanAssign(s.role, current, req.Role) {
		fail(w, http.StatusForbidden, "insufficient role")
		return
	}
	if req.Role != models.OrgRoleOwner && !h.guardLastOwner(r.Context(), w, s.orgID, current) {
		return
	}
	if err := h.Store.SetMemberRole(r.Context(), s.orgID, target, req.Role); err != nil {
		if errors.Is(err, services.ErrLastOwner) {
			fail(w, http.StatusConflict, "the last owner cannot be removed or demoted")
			return
		}
		if errors.Is(err, gorm.ErrRecordNotFound) {
			fail(w, http.StatusNotFound, "member not found")
			return
		}
		serverError(w, "failed to update member", err)
		return
	}
	h.Invalidate(target, s.orgID)
	w.WriteHeader(http.StatusNoContent)
}

func (h *Handler) removeMember(w http.ResponseWriter, r *http.Request) {
	s, ok := h.scope(w, r)
	if !ok {
		return
	}
	// Viewers who may not even list members still may leave (self-removal).
	target, current, ok := h.targetMember(w, r, s)
	if !ok {
		return
	}
	self := target == s.userID
	if !self && !CanAssign(s.role, current, current) {
		fail(w, http.StatusForbidden, "insufficient role")
		return
	}
	if s.personal() {
		fail(w, http.StatusBadRequest, "a personal workspace cannot be left or have members removed")
		return
	}
	if !h.guardLastOwner(r.Context(), w, s.orgID, current) {
		return
	}
	if err := h.Store.RemoveMember(r.Context(), s.orgID, target); err != nil {
		if errors.Is(err, services.ErrLastOwner) {
			fail(w, http.StatusConflict, "the last owner cannot be removed or demoted")
			return
		}
		if errors.Is(err, gorm.ErrRecordNotFound) {
			fail(w, http.StatusNotFound, "member not found")
			return
		}
		serverError(w, "failed to remove member", err)
		return
	}
	h.Invalidate(target, s.orgID)
	w.WriteHeader(http.StatusNoContent)
}

// ---- invites ----

func toInviteResponse(i models.OrganizationInvite) InviteResponse {
	return InviteResponse{ID: i.ID.String(), Email: i.Email, Role: i.Role, CreatedAt: i.CreatedAt, ExpiresAt: i.ExpiresAt}
}

func (h *Handler) createInvite(w http.ResponseWriter, r *http.Request) {
	s, ok := h.scope(w, r)
	if !ok {
		return
	}
	if !CanManageMembers(s.role) {
		fail(w, http.StatusForbidden, "insufficient role")
		return
	}
	if s.personal() {
		fail(w, http.StatusBadRequest, "a personal workspace cannot have invitations")
		return
	}
	var req CreateInviteRequest
	if !decode(w, r, &req) {
		return
	}
	addr, err := mail.ParseAddress(strings.TrimSpace(req.Email))
	if err != nil || addr.Name != "" || len(addr.Address) > 320 {
		fail(w, http.StatusBadRequest, "a valid email is required")
		return
	}
	email := strings.ToLower(addr.Address)
	// The reserved service-account domain is not a person and cannot be invited.
	if strings.HasSuffix(email, models.ServiceAccountEmailDomain) {
		fail(w, http.StatusBadRequest, "a valid email is required")
		return
	}
	if !ValidRole(req.Role) {
		fail(w, http.StatusBadRequest, "role must be Owner, Admin, Member or Billing")
		return
	}
	if !CanAssign(s.role, "", req.Role) {
		fail(w, http.StatusForbidden, "insufficient role")
		return
	}
	already, err := h.Store.IsEmailMember(r.Context(), s.orgID, email)
	if err != nil {
		serverError(w, "failed to create invitation", err)
		return
	}
	if already {
		fail(w, http.StatusConflict, "that person is already a member")
		return
	}

	token, err := newInviteToken()
	if err != nil {
		serverError(w, "failed to create invitation", err)
		return
	}
	now := h.Now().UTC()
	inviter := s.userID
	inv := models.OrganizationInvite{
		ID: uuid.New(), OrganizationID: s.orgID, Email: email, Role: req.Role,
		TokenHash: HashInviteToken(token), InvitedByUserID: &inviter,
		CreatedAt: now, ExpiresAt: now.Add(InviteTTL),
	}
	if err := h.Store.CreateInvite(r.Context(), &inv); err != nil {
		serverError(w, "failed to create invitation", err)
		return
	}

	_, inviterName, nameErr := h.Store.GetUserIdentity(r.Context(), s.userID)
	if nameErr != nil || inviterName == "" {
		inviterName = s.email
	}
	sendErr := h.Mailer().SendTemplate(email, services.TemplateInvite, services.MailData{
		Link:        strings.TrimRight(h.BaseURL(), "/") + "/app/invite/" + token,
		OrgName:     s.name,
		InviterName: inviterName,
		Role:        req.Role,
		ExpiresIn:   "7 days",
		Locale:      inviteLocale(r, email, req.Locale),
	})
	if sendErr != nil {
		log.Printf("orgs: invite email to %s failed: %s", services.MailLogRef(email), services.SanitizeMailError(sendErr))
	}
	// Provider-agnostic on purpose: with Cloudflare Email Sending configured, an SMTP-only
	// check would report every delivered invitation as unsent.
	sent := sendErr == nil && services.MailerConfigured()
	resp := toInviteResponse(inv)
	resp.EmailSent = &sent
	writeJSON(w, http.StatusCreated, resp)
}

func (h *Handler) listInvites(w http.ResponseWriter, r *http.Request) {
	s, ok := h.scope(w, r)
	if !ok {
		return
	}
	if !CanManageMembers(s.role) {
		fail(w, http.StatusForbidden, "insufficient role")
		return
	}
	list, err := h.Store.ListPendingInvites(r.Context(), s.orgID, h.Now().UTC())
	if err != nil {
		serverError(w, "failed to list invitations", err)
		return
	}
	out := make([]InviteResponse, len(list))
	for i, inv := range list {
		out[i] = toInviteResponse(inv)
	}
	writeJSON(w, http.StatusOK, out)
}

func (h *Handler) revokeInvite(w http.ResponseWriter, r *http.Request) {
	s, ok := h.scope(w, r)
	if !ok {
		return
	}
	if !CanManageMembers(s.role) {
		fail(w, http.StatusForbidden, "insufficient role")
		return
	}
	id, err := uuid.Parse(mux.Vars(r)["inviteId"])
	if err != nil {
		fail(w, http.StatusNotFound, "invitation not found")
		return
	}
	revoked, err := h.Store.RevokeInvite(r.Context(), s.orgID, id, h.Now().UTC())
	if err != nil {
		serverError(w, "failed to revoke invitation", err)
		return
	}
	if !revoked {
		fail(w, http.StatusNotFound, "invitation not found")
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// AcceptError is a client-facing failure of AcceptInvite; any other error is a
// server failure.
type AcceptError struct {
	Status  int
	Message string
}

func (e *AcceptError) Error() string { return e.Message }

// AcceptResult is what a successful AcceptInvite reports.
type AcceptResult struct {
	Org  *models.Organization
	Role string
	// Joined is true when this call created the membership (false for a user
	// who was already a member).
	Joined bool
}

var (
	errInviteNotFound = &AcceptError{http.StatusNotFound, "invitation not found"}
	errInviteExpired  = &AcceptError{http.StatusGone, "invitation has expired"}
)

// LookupInvite resolves an invite token to its invite and live Team organization.
// Unknown, revoked and deleted-org cases are all the same 404 so a caller cannot
// tell them apart. It does not look at acceptance or expiry; see CheckInviteUsable.
func LookupInvite(ctx context.Context, st services.OrgStore, token string) (*models.OrganizationInvite, *models.Organization, error) {
	token = strings.TrimSpace(token)
	if token == "" {
		return nil, nil, &AcceptError{http.StatusBadRequest, "token is required"}
	}
	inv, err := st.FindInviteByTokenHash(ctx, HashInviteToken(token))
	if err != nil {
		return nil, nil, err
	}
	if inv == nil || inv.RevokedAt != nil {
		return nil, nil, errInviteNotFound
	}
	org, err := st.GetOrganization(ctx, inv.OrganizationID)
	if err != nil {
		return nil, nil, err
	}
	// A deleted org, or (defence in depth) a Personal one, cannot be joined.
	if org == nil || org.Kind != models.OrgKindTeam {
		return nil, nil, errInviteNotFound
	}
	return inv, org, nil
}

// CheckInviteUsable reports why a looked-up invite can no longer be accepted:
// already used (404, like unknown) or expired (410). Nil when it is still open.
func CheckInviteUsable(inv *models.OrganizationInvite, now time.Time) error {
	if inv.AcceptedAt != nil {
		return errInviteNotFound
	}
	if !now.Before(inv.ExpiresAt) {
		return errInviteExpired
	}
	return nil
}

// AcceptInvite is the one implementation of "an existing user joins through an
// invite", shared by POST /api/v1/orgs/invites/accept and C8's
// POST /api/v1/auth/invitations/{token}/accept. Unknown, revoked and (unless
// idempotent) already-used tokens are indistinguishable (404); an expired one
// is 410; the user's email must equal the invited email (403). With idempotent
// set, re-accepting an already-accepted invite as a member of that org succeeds
// and returns the member's current role without changing anything. The seat
// hook (services.SeatChanges) fires only when a membership was really created.
func AcceptInvite(ctx context.Context, st services.OrgStore, token string, userID uuid.UUID, now time.Time, idempotent bool) (*AcceptResult, error) {
	inv, org, err := LookupInvite(ctx, st, token)
	if err != nil {
		return nil, err
	}
	email, _, err := st.GetUserIdentity(ctx, userID)
	if err != nil {
		return nil, err
	}
	if !strings.EqualFold(strings.TrimSpace(email), strings.TrimSpace(inv.Email)) {
		return nil, &AcceptError{http.StatusForbidden, "this invitation was sent to a different email address"}
	}

	role, member, err := st.GetMemberRole(ctx, inv.OrganizationID, userID)
	if err != nil {
		return nil, err
	}
	if inv.AcceptedAt != nil {
		if idempotent && member {
			return &AcceptResult{Org: org, Role: role}, nil
		}
		return nil, errInviteNotFound
	}
	if !now.Before(inv.ExpiresAt) {
		return nil, errInviteExpired
	}

	if err := st.AcceptInvite(ctx, inv, userID, now); err != nil {
		return nil, err
	}
	if !member {
		role = inv.Role
		services.NotifySeatsChanged(ctx, inv.OrganizationID)
	}
	return &AcceptResult{Org: org, Role: role, Joined: !member}, nil
}

// acceptInvite lets an existing, signed-in user join through an invite. It does
// not switch the active org: the client calls POST /api/v1/auth/switch-org next.
func (h *Handler) acceptInvite(w http.ResponseWriter, r *http.Request) {
	c, ok := callerOf(w, r)
	if !ok {
		return
	}
	var req AcceptInviteRequest
	if !decode(w, r, &req) {
		return
	}
	res, err := AcceptInvite(r.Context(), h.Store, req.Token, c.userID, h.Now().UTC(), false)
	if err != nil {
		var ae *AcceptError
		if errors.As(err, &ae) {
			fail(w, ae.Status, ae.Message)
			return
		}
		serverError(w, "failed to accept invitation", err)
		return
	}
	// The user may have a cached "not a member" entry for this org.
	h.Invalidate(c.userID, res.Org.ID)
	writeJSON(w, http.StatusOK, AcceptInviteResponse{
		OrganizationID: res.Org.ID.String(), Name: res.Org.Name, Role: res.Role,
	})
}

// inviteLocale is the language of an invitation: the invitee's saved locale when they already
// have an account, else the body field, else Accept-Language, else the default.
func inviteLocale(r *http.Request, email, explicit string) string {
	if loc := services.UserLocaleByEmail(email); loc != "" {
		return loc
	}
	return services.LocaleFromRequest(r, explicit)
}
