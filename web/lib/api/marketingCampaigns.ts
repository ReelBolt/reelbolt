import { apiFetch, ApiError } from './client';

/** Platform-admin marketing campaigns (`/api/v1/marketing/admin/campaigns`). 404 while the feature is off. */
export const CAMPAIGNS_URL = '/api/v1/marketing/admin/campaigns';
export const CAMPAIGN_SETTINGS_URL = `${CAMPAIGNS_URL}/settings`;
export const MAX_SUBJECT_LENGTH = 200;

export type CampaignStatus = 'draft' | 'queued' | 'syncing' | 'sending' | 'sent' | 'failed' | 'sendUnknown';

/** Statuses in which a send is in flight, so the page keeps polling. */
export const ACTIVE_STATUSES: readonly CampaignStatus[] = ['queued', 'syncing', 'sending'];

export interface Campaign {
  id: string;
  subject: string;
  html: string | null;
  status: CampaignStatus;
  createdAt: string;
  updatedAt: string;
  testSentAt: string | null;
  testCurrent: boolean;
  confirmedRecipientCount: number | null;
  finalRecipientCount: number | null;
  dispatchStartedAt: string | null;
  failureCode: string | null;
  stats: Record<string, unknown> | null;
  statsAt: string | null;
  /** Saved content with the footer, from the single-campaign route only. */
  previewHtml?: string | null;
  previewError?: string | null;
}

export interface CampaignSettings {
  footerOk: boolean;
  listSendEnabled: boolean;
  testRecipientMasked: string | null;
  brevoConfigured: boolean;
}

export interface CampaignDryRun {
  recipientCount: number;
  suppressedCount: number;
  sampleMasked: string[];
  footerOk: boolean;
  errors: string[];
  listSendEnabled: boolean;
}

export const listCampaigns = () => apiFetch<Campaign[]>(`${CAMPAIGNS_URL}?limit=50`);
export const getCampaignSettings = () => apiFetch<CampaignSettings>(CAMPAIGN_SETTINGS_URL);
export const getCampaign = (id: string) => apiFetch<Campaign>(`${CAMPAIGNS_URL}/${id}`);

const body = (subject: string, html: string) => JSON.stringify({ subject, html });
export const createCampaign = (subject: string, html: string) =>
  apiFetch<Campaign>(CAMPAIGNS_URL, { method: 'POST', body: body(subject, html) });
export const updateCampaign = (id: string, subject: string, html: string) =>
  apiFetch<Campaign>(`${CAMPAIGNS_URL}/${id}`, { method: 'PUT', body: body(subject, html) });
export const dryRunCampaign = (id: string) =>
  apiFetch<CampaignDryRun>(`${CAMPAIGNS_URL}/${id}/dry-run`, { method: 'POST' });
export const testSendCampaign = (id: string) =>
  apiFetch<{ sent: boolean }>(`${CAMPAIGNS_URL}/${id}/test-send`, { method: 'POST' });
export const sendCampaign = (id: string, confirmRecipientCount: number) =>
  apiFetch<Campaign>(`${CAMPAIGNS_URL}/${id}/send`, { method: 'POST', body: JSON.stringify({ confirmRecipientCount }) });
export const refreshCampaignResults = (id: string) =>
  apiFetch<Campaign>(`${CAMPAIGNS_URL}/${id}/refresh-results`, { method: 'POST' });

export const resolveCampaign = (id: string, outcome: 'sent' | 'notSent') =>
  apiFetch<Campaign>(`${CAMPAIGNS_URL}/${id}/resolve`, { method: 'POST', body: JSON.stringify({ outcome }) });
export const duplicateCampaign = (id: string) =>
  apiFetch<Campaign>(`${CAMPAIGNS_URL}/${id}/duplicate`, { method: 'POST' });

export const STATUS_LABELS: Record<CampaignStatus, string> = {
  draft: 'Draft',
  queued: 'Queued',
  syncing: 'Syncing',
  sending: 'Sending',
  sent: 'Sent',
  failed: 'Failed',
  sendUnknown: 'Send unknown',
};

const MESSAGES: Record<string, string> = {
  list_send_disabled: 'Sending to the list is switched off on this deployment.',
  not_draft: 'This campaign is no longer a draft, so it cannot be changed or sent again.',
  recipient_count_changed: 'The recipient count changed since the dry run. Run the dry run again and confirm the new number.',
  campaign_active: 'Another campaign is being sent right now. Wait for it to finish.',
  rate_limited: 'Too many attempts. Wait a little and try again.',
  subject_missing: 'Add a subject.',
  subject_too_long: `The subject is longer than ${MAX_SUBJECT_LENGTH} characters.`,
  subject_invalid_characters: 'The subject contains characters that are not allowed, such as line breaks.',
  html_missing: 'Add the email HTML.',
  html_too_large: 'The email HTML is too large.',
  no_recipients: 'There are no recipients to send to.',
  test_send_required: 'Send a test of the current content first.',
  test_recipient_missing: 'No test recipient is configured on the server.',
  brevo_key_missing: 'The Brevo key is not configured on the server.',
  brevo_list_missing: 'The Brevo list is not configured on the server.',
  brevo_test_list_missing: 'The Brevo test list is not configured on the server.',
  nothing_to_refresh: 'This campaign has no results to refresh yet.',
  send_failed: 'Brevo refused the request. Nothing was sent.',
  refresh_failed: 'Brevo could not return results. Try again shortly.',
  html_too_short: 'The email HTML is too short to be a real email.',
  html_too_deep: 'The email HTML is nested too deeply to be a real email.',
  html_no_body: 'The email HTML has no body, so the required footer cannot be added.',
  footer_placement_failed: 'The required footer could not be placed safely in this email HTML. Simplify the HTML and try again.',
  footer_controller_name_missing: 'The footer cannot be built: the sender name is not configured on the server.',
  footer_address_missing: 'The footer cannot be built: the postal address is not configured on the server.',
  public_base_url_missing: 'The footer cannot be built: the public site address is not configured on the server.',
  unresolved_send: 'Another send may or may not have gone out. It must be marked as sent or not sent before anything else is sent.',
  consent_changed: 'Recipient consent changed while the send was being prepared. Nothing was sent. Run the dry run again.',
  erasure_pending: 'A recipient is waiting to be erased from Brevo. Nothing was sent. Try again later.',
  erasure_unavailable: 'Erasure is not available right now, so nothing was sent.',
  brevo_bad_response: 'Brevo answered in a way that could not be understood.',
  lease_lost: 'The send was taken over by another worker. Check the history before trying again.',
  not_sent: 'This campaign has not been sent, so it has no results.',
  list_blocked_contact: 'A blocked contact was found on the Brevo list. Nothing was sent.',
  list_changed: 'The Brevo list changed during the send. Nothing was sent.',
  list_count_mismatch: 'The Brevo list does not match the expected number of recipients. Nothing was sent.',
  list_mismatch: 'The Brevo list does not match the expected recipients. Nothing was sent.',
  interrupted: 'The send was interrupted. Check Brevo before sending again.',
  send_unknown: 'It is not known whether this send reached Brevo.',
  pipeline_error: 'The send failed unexpectedly. Nothing further was sent.',
  brevo_process_timeout: 'Brevo took too long to process the list. Nothing was sent.',
  not_send_unknown: 'This campaign does not need to be resolved.',
  outcome_invalid: 'Choose whether the campaign was sent or not.',
};

const GENERIC = 'Something went wrong. Try again.';

/** A plain sentence for a stored or returned code, or a generic one when unmapped. */
export function codeMessage(code: string | null | undefined): string {
  if (!code) return GENERIC;
  if (MESSAGES[code]) return MESSAGES[code];
  if (/^brevo_http_\d+$/.test(code)) return 'Brevo refused the request.';
  if (/^brevo_process_/.test(code)) return 'Brevo could not process the list. Nothing was sent.';
  return GENERIC;
}

/** A plain sentence for every refusal code the campaign routes answer; never a raw code. */
export function campaignMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 403 && !error.code) return 'Only platform administrators can manage campaigns.';
    const body = error.body as { error?: unknown; code?: unknown } | undefined;
    const primary = typeof body?.error === 'string' ? body.error : null;
    for (const c of [primary, error.code]) if (c && (MESSAGES[c] || /^brevo_/.test(c))) return codeMessage(c);
    if (error.status === 404) return 'That campaign no longer exists.';
    return GENERIC;
  }
  return GENERIC;
}

/** Why a send is blocked, or null when it may proceed. */
export function sendBlockReason(input: {
  listSendEnabled: boolean;
  testCurrent: boolean;
  dryRunFresh: boolean;
  dirty: boolean;
}): string | null {
  if (!input.listSendEnabled) return 'Sending to the list is switched off on this deployment.';
  if (input.dirty) return 'Save your changes first.';
  if (!input.testCurrent) return 'Send a test of the current content first.';
  if (!input.dryRunFresh) return 'Run a dry run on the current content first.';
  return null;
}

export const STAT_COLUMNS: { key: string; label: string; keys: string[] }[] = [
  { key: 'sent', label: 'Sent', keys: ['sent'] },
  { key: 'delivered', label: 'Delivered', keys: ['delivered'] },
  { key: 'opens', label: 'Opens', keys: ['uniqueViews', 'viewed'] },
  { key: 'clicks', label: 'Clicks', keys: ['uniqueClicks', 'clickers'] },
  { key: 'bounces', label: 'Bounces', keys: ['hardBounces', 'softBounces'] },
  { key: 'complaints', label: 'Complaints', keys: ['complaints'] },
  { key: 'unsubscribes', label: 'Unsubscribes', keys: ['unsubscriptions'] },
];

/** One stat column's number (bounces add hard and soft; the rest use the first key present), or null with no stats. */
export function statValue(stats: Campaign['stats'], keys: string[]): number | null {
  if (!stats) return null;
  const nums = keys.map((k) => stats[k]).filter((v): v is number => typeof v === 'number');
  if (nums.length === 0) return null;
  return keys.includes('hardBounces') ? nums.reduce((a, b) => a + b, 0) : nums[0];
}
