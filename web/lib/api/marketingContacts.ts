import { apiFetch } from './client';

export const MARKETING_ADMIN_CONTACTS_URL = '/api/v1/marketing/admin/contacts';
export const MARKETING_ADMIN_CONTACT_SYNC_URL = '/api/v1/marketing/admin/contact-sync';
export const MARKETING_ADMIN_CONTACT_SYNC_RUN_URL = '/api/v1/marketing/admin/contact-sync/run';

export type ContactStatusFilter = 'optedIn' | 'pending' | 'withdrawn' | 'all';
export type ContactNameSource = 'Account' | 'Form' | 'Admin';
export type ContactBrevoState = 'synced' | 'pending' | 'failed' | 'notAtBrevo';

export interface MarketingContact {
  id: string;
  email: string;
  status: string;
  firstName: string | null;
  lastName: string | null;
  nameSource: ContactNameSource | null;
  brevo: { state: ContactBrevoState; syncedAt: string | null; errorCode: string | null };
}

export interface MarketingContactsPage {
  items: MarketingContact[];
  total: number;
}

export interface ContactSyncStatus {
  enabled: boolean;
  configured: boolean;
  campaignActive: boolean;
  lastRunAt: string | null;
  lastRunOk: boolean | null;
  lastErrorCode: string | null;
  lastReconcileAt: string | null;
  counts: { synced: number; pendingPush: number; failed: number };
}

export interface ContactsQuery {
  status: ContactStatusFilter;
  q: string;
  limit: number;
  offset: number;
}

export function contactsUrl({ status, q, limit, offset }: ContactsQuery): string {
  const params = new URLSearchParams();
  params.set('status', status);
  if (q.trim()) params.set('q', q.trim());
  params.set('limit', String(limit));
  params.set('offset', String(offset));
  return `${MARKETING_ADMIN_CONTACTS_URL}?${params.toString()}`;
}

export function contactNameUrl(id: string): string {
  return `/api/v1/marketing/admin/consents/${encodeURIComponent(id)}/name`;
}

export async function getMarketingContacts(url: string): Promise<MarketingContactsPage> {
  const body = await apiFetch<Partial<MarketingContactsPage> | null>(url);
  return { items: Array.isArray(body?.items) ? body.items : [], total: typeof body?.total === 'number' ? body.total : 0 };
}

export async function getContactSyncStatus(): Promise<ContactSyncStatus> {
  return apiFetch<ContactSyncStatus>(MARKETING_ADMIN_CONTACT_SYNC_URL);
}

export async function updateContactName(
  id: string,
  name: { firstName: string | null; lastName: string | null },
): Promise<MarketingContact> {
  return apiFetch<MarketingContact>(contactNameUrl(id), { method: 'PATCH', body: JSON.stringify(name) });
}

export async function requestContactSyncRun(): Promise<{ requested: boolean }> {
  return apiFetch<{ requested: boolean }>(MARKETING_ADMIN_CONTACT_SYNC_RUN_URL, { method: 'POST' });
}
