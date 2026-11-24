'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import type {
  DesignDiff,
  DesignProposal,
  DesignProposalStatus,
  DesignSource,
  DesignSystemsSummary,
  DesignSystemKind,
  DesignVersion,
  DesignVersionSummary,
} from '../types/design-system';

function base(projectId: string): string {
  return `/api/v1/projects/${projectId}/design-system`;
}

/** Both kinds' summaries for one project. `projectId` null/undefined disables the request. */
export function useDesignSystem(projectId: string | null | undefined) {
  return useSWR<DesignSystemsSummary>(projectId ? base(projectId) : null, fetcher);
}

/** Versions of one kind, newest first. `kind` defaults to the design system, like the API. */
export function useDesignVersions(projectId: string | null | undefined, kind: DesignSystemKind = 'design') {
  return useSWR<DesignVersionSummary[]>(
    projectId ? `${base(projectId)}/versions?kind=${kind}` : null,
    fetcher,
  );
}

/** One full version. A missing `versionId` leaves the key null, so nothing is fetched. */
export function useDesignVersion(
  projectId: string | null | undefined,
  versionId: string | null | undefined,
) {
  return useSWR<DesignVersion>(
    projectId && versionId ? `${base(projectId)}/versions/${versionId}` : null,
    fetcher,
  );
}

/** The sources recorded for one kind. */
export function useDesignSources(projectId: string | null | undefined, kind: DesignSystemKind = 'design') {
  return useSWR<DesignSource[]>(
    projectId ? `${base(projectId)}/sources?kind=${kind}` : null,
    fetcher,
  );
}

/**
 * The current system's change proposals, newest first (SPEC §9). `kind` scopes the proposal to one
 * system, since the route lists proposals of the current system only.
 */
export function useDesignProposals(
  projectId: string | null | undefined,
  kind: DesignSystemKind = 'design',
  status?: DesignProposalStatus,
) {
  const query = new URLSearchParams({ kind });
  if (status) query.set('status', status);
  return useSWR<DesignProposal[]>(
    projectId ? `${base(projectId)}/proposals?${query.toString()}` : null,
    fetcher,
  );
}

/**
 * The two-version diff (`versionId` newer, `againstId` older). Either id missing leaves the key
 * null, so nothing is fetched — the very first draft has no current version to compare with.
 */
export function useVersionDiff(
  projectId: string | null | undefined,
  versionId: string | null | undefined,
  againstId: string | null | undefined,
) {
  return useSWR<DesignDiff>(
    projectId && versionId && againstId
      ? `${base(projectId)}/versions/${versionId}/diff?against=${againstId}`
      : null,
    fetcher,
  );
}
