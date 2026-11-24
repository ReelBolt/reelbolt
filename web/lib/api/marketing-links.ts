import { apiFetch, ApiError } from './client';
import type {
  CreateMarketingLinkRequest,
  FunnelAccountSteps,
  MarketingLink,
  MarketingLinkFunnel,
  MarketingLinkFunnelItem,
  MarketingLinkList,
  PatchMarketingLinkRequest,
} from '../types/marketing-links';

/**
 * Tracked links, platform admin only (docs/design/new-user-funnel.md section 6). The API answers
 * 403 for anyone else, and refuses a bad field with `{ error: "<code>", field? }`.
 */
export const MARKETING_ADMIN_LINKS_URL = '/api/v1/marketing/admin/links';

export function marketingLinkUrl(id: string): string {
  return `${MARKETING_ADMIN_LINKS_URL}/${encodeURIComponent(id)}`;
}

export function marketingLinksFunnelUrl(days: number): string {
  return `${MARKETING_ADMIN_LINKS_URL}/funnel?days=${encodeURIComponent(String(days))}`;
}

/** The refusal codes of the links admin API (`TrackedLinkRules.FirstProblem` plus the service's own). */
export type MarketingLinkErrorCode =
  | 'code_invalid'
  | 'channel_invalid'
  | 'medium_invalid'
  | 'campaign_invalid'
  | 'placement_invalid'
  | 'destination_path_invalid'
  | 'persona_invalid'
  | 'cost_invalid'
  | 'link_code_taken'
  | 'field_immutable'
  | 'invalid_body';

const ERROR_CODES: readonly MarketingLinkErrorCode[] = [
  'code_invalid',
  'channel_invalid',
  'medium_invalid',
  'campaign_invalid',
  'placement_invalid',
  'destination_path_invalid',
  'persona_invalid',
  'cost_invalid',
  'link_code_taken',
  'field_immutable',
  'invalid_body',
];

/** The API's refusal code of a failed call, or null when it carried none this page knows. */
export function marketingLinkErrorCode(error: unknown): MarketingLinkErrorCode | null {
  if (!(error instanceof ApiError) || !error.code) return null;
  return (ERROR_CODES as readonly string[]).includes(error.code) ? (error.code as MarketingLinkErrorCode) : null;
}

const count = (value: unknown): number => (typeof value === 'number' && Number.isFinite(value) ? value : 0);

function accountSteps(raw: Partial<FunnelAccountSteps> | null | undefined): FunnelAccountSteps {
  return {
    signups: count(raw?.signups),
    verified: count(raw?.verified),
    onboarded: count(raw?.onboarded),
    firstProject: count(raw?.firstProject),
    firstRun: count(raw?.firstRun),
    firstVideo: count(raw?.firstVideo),
    paid: count(raw?.paid),
  };
}

export async function getMarketingLinks(): Promise<MarketingLinkList> {
  const body = await apiFetch<Partial<MarketingLinkList> | null>(MARKETING_ADMIN_LINKS_URL);
  return { items: Array.isArray(body?.items) ? body.items : [] };
}

/** The funnel for a window. A missing count reads as 0 and a missing list as empty, so the table never renders NaN. */
export async function getMarketingLinksFunnel(url: string): Promise<MarketingLinkFunnel> {
  const body = await apiFetch<Partial<MarketingLinkFunnel> | null>(url);
  const items = Array.isArray(body?.items) ? body.items : [];
  return {
    days: count(body?.days),
    generatedAt: typeof body?.generatedAt === 'string' ? body.generatedAt : '',
    urlCaptureEnabled: body?.urlCaptureEnabled === true,
    // Only an explicit answer is passed on: an API that does not report the switch says nothing.
    ...(typeof body?.visitIngestEnabled === 'boolean' ? { visitIngestEnabled: body.visitIngestEnabled } : {}),
    items: items.map(
      (item): MarketingLinkFunnelItem => ({
        ...item,
        ...accountSteps(item),
        clicks: count(item.clicks),
        botHits: count(item.botHits),
        visitors: count(item.visitors),
      }),
    ),
    unattributed: accountSteps(body?.unattributed),
  };
}

export async function createMarketingLink(request: CreateMarketingLinkRequest): Promise<MarketingLink> {
  return apiFetch<MarketingLink>(MARKETING_ADMIN_LINKS_URL, { method: 'POST', body: JSON.stringify(request) });
}

export async function patchMarketingLink(id: string, patch: PatchMarketingLinkRequest): Promise<MarketingLink> {
  return apiFetch<MarketingLink>(marketingLinkUrl(id), { method: 'PATCH', body: JSON.stringify(patch) });
}
