import { apiFetch } from './client';
import type { OrgKind, OrgRole } from '../types/auth';

// Org data layer (WP A15-UI). Shapes mirror the Go DTOs in api/handlers/orgs/dto.go
// (and the shared auth TokenResponse for switch-org) — Go json tags are camelCase, so
// the field names below are exactly what the API returns.
//
// `role` is the OrgRole from ../types/auth — 'Owner' | 'Admin' | 'Member' | 'Billing',
// which is exactly the Go enum (api/models/organization.go).

/** One organization as seen by a member. `role` is the caller's role in it. */
export interface Org {
  id: string;
  name: string;
  slug: string;
  kind: OrgKind;
  role: OrgRole;
  createdAt: string;
  /**
   * Where this workspace prefers its media work to run (E8-UI), from
   * `organizations.compute_preference`: 'Auto' | 'CloudOnly' | 'PreferRunner' |
   * 'RunnerOnly'. The API reports 'Auto' for a workspace whose stored value is
   * empty, so an absent field and a stored 'Auto' mean the same thing here —
   * narrow it with `computePreferenceOrDefault` (lib/utils/compute-preference.ts)
   * rather than trusting the string.
   */
  computePreference?: string;
}

export interface CreateOrgRequest {
  name: string;
}

/** A partial update: a field left out is not touched, but at least one must be present. */
export interface UpdateOrgRequest {
  name?: string;
  computePreference?: string;
}

/** One member, as seen by a caller who may list the org's members. */
export interface OrgMember {
  userId: string;
  email: string;
  displayName: string;
  role: OrgRole;
  joinedAt: string;
}

export interface UpdateMemberRequest {
  role: OrgRole;
}

/** A pending invitation. The invite token is never returned — it is only ever emailed. */
export interface OrgInvite {
  id: string;
  email: string;
  role: OrgRole;
  createdAt: string;
  expiresAt: string;
  /** Absent when SMTP is unconfigured or the send failed; the invite exists either way. */
  emailSent?: boolean;
}

export interface CreateInviteRequest {
  email: string;
  role: OrgRole;
}

export interface AcceptInviteRequest {
  token: string;
}

export interface AcceptInviteResponse {
  organizationId: string;
  name: string;
  role: OrgRole;
}

/**
 * The shared session body of every endpoint that issues a session — login, switch-org and
 * accepting a team invitation (Go auth.TokenResponse). Every one of them also (re)writes the
 * `reelbolt_token` and `reelbolt_user` cookies, so the client must refresh its auth snapshot.
 */
export interface SessionResponse {
  accessToken: string;
  tokenType: string;
  expiresIn: number;
  email: string;
  isAdmin: boolean;
  isPlatformAdmin: boolean;
  mustChangePassword: boolean;
  emailVerified: boolean;
  org: string;
  orgName: string;
  orgKind: OrgKind;
  orgRole: OrgRole;
}

export interface SwitchOrgRequest {
  organizationId: string;
}

// ---- organizations ----

/** GET /api/v1/orgs — the caller's organizations: their Personal workspace plus every Team org they belong to. */
export function getOrgs(): Promise<Org[]> {
  return apiFetch<Org[]>('/api/v1/orgs');
}

/** POST /api/v1/orgs — create a Team org the caller owns (created as its Owner). */
export function createOrg(data: CreateOrgRequest): Promise<Org> {
  return apiFetch<Org>('/api/v1/orgs', { method: 'POST', body: JSON.stringify(data) });
}

/** GET /api/v1/orgs/{id} — one of the caller's organizations. A foreign, unknown or Platform org is 404. */
export function getOrg(id: string): Promise<Org> {
  return apiFetch<Org>(`/api/v1/orgs/${id}`);
}

/** PATCH /api/v1/orgs/{id} — rename; requires an org admin role. */
export function updateOrg(id: string, data: UpdateOrgRequest): Promise<Org> {
  return apiFetch<Org>(`/api/v1/orgs/${id}`, { method: 'PATCH', body: JSON.stringify(data) });
}

/** DELETE /api/v1/orgs/{id} — delete; Owner only, and never a Personal workspace. Returns 204. */
export function deleteOrg(id: string): Promise<void> {
  return apiFetch<void>(`/api/v1/orgs/${id}`, { method: 'DELETE' });
}

// ---- members ----

/** GET /api/v1/orgs/{id}/members — visible to Owner/Admin/Member (not Billing). */
export function listMembers(orgId: string): Promise<OrgMember[]> {
  return apiFetch<OrgMember[]>(`/api/v1/orgs/${orgId}/members`);
}

/** PATCH /api/v1/orgs/{id}/members/{userId} — change a member's role. Returns 204. */
export function updateMember(orgId: string, userId: string, data: UpdateMemberRequest): Promise<void> {
  return apiFetch<void>(`/api/v1/orgs/${orgId}/members/${userId}`, { method: 'PATCH', body: JSON.stringify(data) });
}

/** DELETE /api/v1/orgs/{id}/members/{userId} — remove a member (or the caller, to leave). Returns 204. */
export function removeMember(orgId: string, userId: string): Promise<void> {
  return apiFetch<void>(`/api/v1/orgs/${orgId}/members/${userId}`, { method: 'DELETE' });
}

// ---- invites ----

/** POST /api/v1/orgs/{id}/invites — invite someone by email; Team orgs only (never a Personal workspace). */
export function createInvite(orgId: string, data: CreateInviteRequest): Promise<OrgInvite> {
  return apiFetch<OrgInvite>(`/api/v1/orgs/${orgId}/invites`, { method: 'POST', body: JSON.stringify(data) });
}

/** GET /api/v1/orgs/{id}/invites — the org's pending invitations. */
export function listInvites(orgId: string): Promise<OrgInvite[]> {
  return apiFetch<OrgInvite[]>(`/api/v1/orgs/${orgId}/invites`);
}

/** DELETE /api/v1/orgs/{id}/invites/{inviteId} — revoke a pending invite. Returns 204. */
export function revokeInvite(orgId: string, inviteId: string): Promise<void> {
  return apiFetch<void>(`/api/v1/orgs/${orgId}/invites/${inviteId}`, { method: 'DELETE' });
}

/** POST /api/v1/orgs/invites/accept — an existing, signed-in user joins through an invite. Does not switch the active org; call switchOrg next. */
export function acceptInvite(data: AcceptInviteRequest): Promise<AcceptInviteResponse> {
  return apiFetch<AcceptInviteResponse>('/api/v1/orgs/invites/accept', { method: 'POST', body: JSON.stringify(data) });
}

// ---- session ----

/** POST /api/v1/auth/switch-org — re-issue the session for another org the caller belongs to; sets the session cookies. A non-member, unknown or Platform org is 404, never 403. */
export function switchOrg(data: SwitchOrgRequest): Promise<SessionResponse> {
  return apiFetch<SessionResponse>('/api/v1/auth/switch-org', { method: 'POST', body: JSON.stringify(data) });
}

// ---- invitation link ----
//
// The link the API emails is {PUBLIC_BASE_URL}/app/invite/{token}, and the token in the path is
// the credential. Unlike everything above, these two endpoints live in the auth handler (C8,
// api/handlers/auth/invitations.go) — creation, listing and revocation stay under
// /api/v1/orgs/{id}/invites.

/** What an invitation is for. Read before sign-in, so the page can offer sign-in or signup. */
export interface InvitationPreview {
  organizationName: string;
  /** Empty when the inviter's account is gone. */
  inviterName: string;
  role: OrgRole;
  /** The invited address: signup prefills it, accept requires the signed-in user to match it. */
  email: string;
  expiresAt: string;
  /** True when the invited address already has an account, so the page should offer sign-in. */
  accountExists: boolean;
}

/** GET /api/v1/auth/invitations/{token} — public preview. Unknown/revoked/used are 404, expired 410. */
export function getInvitationPreview(token: string): Promise<InvitationPreview> {
  return apiFetch<InvitationPreview>(`/api/v1/auth/invitations/${encodeURIComponent(token)}`);
}

/**
 * POST /api/v1/auth/invitations/{token}/accept — the signed-in user joins the workspace and the
 * API re-issues the session for it (cookies included), so no switch-org call is needed.
 * Idempotent for someone who is already a member.
 */
export function acceptInvitation(token: string): Promise<SessionResponse> {
  return apiFetch<SessionResponse>(`/api/v1/auth/invitations/${encodeURIComponent(token)}/accept`, {
    method: 'POST',
  });
}
