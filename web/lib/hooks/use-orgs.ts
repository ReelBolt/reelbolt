'use client';

import { useCallback } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import { fetcher } from '../api/client';
import {
  acceptInvitation,
  switchOrg,
  type InvitationPreview,
  type Org,
  type OrgInvite,
  type OrgMember,
} from '../api/orgs';
import { notifyAuthChange } from './use-auth';

/** The caller's organizations: their Personal workspace plus every Team org they belong to. */
export function useOrgs() {
  return useSWR<Org[]>('/api/v1/orgs', fetcher);
}

/** One of the caller's organizations. A foreign, unknown or Platform org is 404. */
export function useOrg(id: string | null | undefined) {
  return useSWR<Org>(id ? `/api/v1/orgs/${id}` : null, fetcher);
}

/**
 * The org's members (A16-UI). The endpoint needs Owner, Admin or Member — a Billing caller gets
 * 403 — so the members page must not fetch for a role that cannot read it.
 */
export function useOrgMembers(orgId: string | null | undefined) {
  return useSWR<OrgMember[]>(orgId ? `/api/v1/orgs/${orgId}/members` : null, fetcher);
}

/** The org's pending invitations. Owner/Admin only. */
export function useOrgInvites(orgId: string | null | undefined) {
  return useSWR<OrgInvite[]>(orgId ? `/api/v1/orgs/${orgId}/invites` : null, fetcher);
}

/**
 * What an invitation link points at, read before anyone signs in. 404 means unknown, revoked or
 * already used; 410 means expired — both are terminal answers for this token, so the preview is
 * not retried on error and not revalidated on focus (the token itself never changes).
 */
export function useInvitationPreview(token: string | null | undefined) {
  return useSWR<InvitationPreview>(token ? `/api/v1/auth/invitations/${encodeURIComponent(token)}` : null, fetcher, {
    shouldRetryOnError: false,
    revalidateOnFocus: false,
  });
}

/**
 * Switch the active organization and refresh the client state that depends on it.
 *
 * `POST /api/v1/auth/switch-org` re-issues the whole session — it sets the httpOnly
 * `reelbolt_token` and the readable `reelbolt_user` cookies — so once it resolves we:
 *   1. refresh the shared auth snapshot from the new readable cookie (`notifyAuthChange`), so
 *      `useAuth` reports the new `activeOrgId` / `orgRole`;
 *   2. kick off a background revalidation of every SWR key (`mutate(() => true)`), so the
 *      mounted org-scoped queries refetch under the new token. This is not awaited: the switch
 *      resolves as soon as the session is re-issued, and the refetches stream in as they land.
 *
 * It returns the re-issued session so the caller (the org switcher) can then reconnect the SSE
 * streams and route to /dashboard — those are the switcher's job, not this data hook's.
 */
export function useSwitchOrg() {
  const { mutate } = useSWRConfig();

  return useCallback(
    async (orgId: string) => {
      const session = await switchOrg({ organizationId: orgId });
      notifyAuthChange();
      mutate(() => true).catch(() => {});
      return session;
    },
    [mutate],
  );
}

/**
 * Accept a team invitation for the signed-in user and adopt the session it issues.
 *
 * `POST /api/v1/auth/invitations/{token}/accept` re-issues the session for the joined org
 * (cookies included), so this does exactly what `useSwitchOrg` does once its call resolves —
 * same two steps, same reason. The caller routes to /dashboard afterwards.
 */
export function useAcceptInvite() {
  const { mutate } = useSWRConfig();

  return useCallback(
    async (token: string) => {
      const session = await acceptInvitation(token);
      notifyAuthChange();
      mutate(() => true).catch(() => {});
      return session;
    },
    [mutate],
  );
}
