import { apiFetch } from './client';
import type {
  AdminTab,
  MarketplaceConsent,
  MarketplaceRejectReason,
  PublishBody,
  PublishPreview,
  PublishResult,
} from '../types/marketplace';
import type {
  MarketplaceCopyResult,
  MarketplaceFilters,
  MarketplaceReportReason,
} from '../types/marketplace';

/** Anonymous reads (no session needed; a 401 never redirects to the login page). */
export const PUBLIC_MARKETPLACE = '/api/v1/public/marketplace';
/** Signed-in calls. */
export const SIGNED_IN_MARKETPLACE = '/api/v1/marketplace';

export const MARKETPLACE_PAGE_SIZE = 24;

/** The request for one page of the list: empty filters and the default sort are left out. */
export function recipesUrl(filters: MarketplaceFilters, cursor?: string | null, limit = MARKETPLACE_PAGE_SIZE): string {
  const params = new URLSearchParams();
  const q = filters.q.trim();
  if (q) params.set('q', q);
  if (filters.category) params.set('category', filters.category);
  if (filters.cost) params.set('cost', filters.cost);
  if (filters.stepType) params.set('stepType', filters.stepType);
  if (filters.language) params.set('language', filters.language);
  if (filters.sort !== 'new') params.set('sort', filters.sort);
  if (cursor) params.set('cursor', cursor);
  params.set('limit', String(limit));
  return `${PUBLIC_MARKETPLACE}/recipes?${params.toString()}`;
}

export const recipeUrl = (id: string): string => `${PUBLIC_MARKETPLACE}/recipes/${encodeURIComponent(id)}`;
export const facetsUrl = (): string => `${PUBLIC_MARKETPLACE}/facets`;
export const posterUrl = (id: string): string => `${recipeUrl(id)}/poster`;
export const previewUrl = (id: string): string => `${recipeUrl(id)}/preview`;
export const meUrl = (id: string): string => `${SIGNED_IN_MARKETPLACE}/recipes/${encodeURIComponent(id)}/me`;

export function copyMarketplaceRecipe(projectId: string, id: string, name?: string): Promise<MarketplaceCopyResult> {
  const trimmed = name?.trim();
  return apiFetch<MarketplaceCopyResult>(
    `/api/v1/projects/${encodeURIComponent(projectId)}/marketplace/${encodeURIComponent(id)}/copy`,
    { method: 'POST', body: JSON.stringify(trimmed ? { name: trimmed } : {}) },
  );
}

export function rateMarketplaceRecipe(id: string, stars: number): Promise<void> {
  return apiFetch<void>(`${SIGNED_IN_MARKETPLACE}/recipes/${encodeURIComponent(id)}/rating`, {
    method: 'PUT',
    body: JSON.stringify({ stars }),
  });
}

export function reportMarketplaceRecipe(id: string, reason: MarketplaceReportReason, note?: string): Promise<void> {
  const trimmed = note?.trim();
  return apiFetch<void>(`${SIGNED_IN_MARKETPLACE}/recipes/${encodeURIComponent(id)}/report`, {
    method: 'POST',
    body: JSON.stringify(trimmed ? { reason, note: trimmed } : { reason }),
  });
}

/* ---- Publish, My recipes, admin ---- */

/** The reviewer's own preview of THAT version (platform admin only), never the public route: Pending and updated versions are not public. */
export const adminPreviewUrl = (versionId: string): string => `${SIGNED_IN_MARKETPLACE}/admin/versions/${encodeURIComponent(versionId)}/preview`;
export const adminPosterUrl = (versionId: string): string => `${SIGNED_IN_MARKETPLACE}/admin/versions/${encodeURIComponent(versionId)}/poster`;

export const consentUrl = (): string => `${SIGNED_IN_MARKETPLACE}/consent`;
export const mineUrl = (): string => `${SIGNED_IN_MARKETPLACE}/mine`;
const ADMIN = `${SIGNED_IN_MARKETPLACE}/admin`;
export const adminListUrl = (tab: AdminTab): string =>
  tab === 'pending' ? `${ADMIN}/queue` : tab === 'reports' ? `${ADMIN}/reports` : `${ADMIN}/recipes?status=${tab}`;

export const getConsent = (): Promise<MarketplaceConsent> => apiFetch<MarketplaceConsent>(consentUrl());

export function publishPreview(projectId: string, workflowId: string): Promise<PublishPreview> {
  return apiFetch<PublishPreview>(`${SIGNED_IN_MARKETPLACE}/publish-preview`, {
    method: 'POST',
    body: JSON.stringify({ projectId, workflowId }),
  });
}

export function publishRecipe(body: PublishBody): Promise<PublishResult> {
  return apiFetch<PublishResult>(`${SIGNED_IN_MARKETPLACE}/recipes`, { method: 'POST', body: JSON.stringify(body) });
}

export function updateRecipe(id: string, body: PublishBody): Promise<PublishResult> {
  return apiFetch<PublishResult>(`${SIGNED_IN_MARKETPLACE}/recipes/${encodeURIComponent(id)}`, {
    method: 'PUT',
    body: JSON.stringify(body),
  });
}

export function unpublishRecipe(id: string): Promise<void> {
  return apiFetch<void>(`${SIGNED_IN_MARKETPLACE}/recipes/${encodeURIComponent(id)}/unpublish`, { method: 'POST' });
}

export type AdminAction = 'approve' | 'reject' | 'remove' | 'restore';

export function adminRecipeAction(
  id: string,
  action: AdminAction,
  body?: { versionId?: string; digest?: string; reason?: MarketplaceRejectReason; note?: string },
): Promise<void> {
  return apiFetch<void>(`${ADMIN}/recipes/${encodeURIComponent(id)}/${action}`, {
    method: 'POST',
    body: JSON.stringify(body ?? {}),
  });
}

export function adminDismissReport(reportId: string): Promise<void> {
  return apiFetch<void>(`${ADMIN}/reports/${encodeURIComponent(reportId)}/dismiss`, { method: 'POST' });
}

/** The opaque output reference of the contract: `step_result:<guid>` for a render, `file:<guid>` for a project file. */
export const outputRefFor = (stepResultId: string): string => `step_result:${stepResultId}`;
export const fileOutputRef = (fileId: string): string => `file:${fileId}`;
