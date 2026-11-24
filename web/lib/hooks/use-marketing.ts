'use client';

import useSWR from 'swr';
import {
  getMarketingConsent,
  getMarketingListSummary,
  getMarketingWording,
  MARKETING_ADMIN_CONSENTS_URL,
  MARKETING_CONSENT_URL,
  MARKETING_WORDING_URL,
  type MarketingConsent,
  type MarketingListSummary,
  type MarketingWording,
} from '../api/marketing';
import {
  getContactSyncStatus,
  getMarketingContacts,
  MARKETING_ADMIN_CONTACT_SYNC_URL,
  type ContactSyncStatus,
  type MarketingContactsPage,
} from '../api/marketingContacts';

/**
 * The current marketing consent wording. `data` stays undefined while loading and when the read
 * fails, and every caller then hides the box: an opt-in without a known wording version cannot be
 * recorded, and the form it sits in must work without it.
 */
export function useMarketingWording(enabled = true) {
  return useSWR<MarketingWording>(enabled ? MARKETING_WORDING_URL : null, getMarketingWording, {
    revalidateOnFocus: false,
    shouldRetryOnError: false,
  });
}

/** The signed-in person's own consent record (keyed by their email, never by workspace). */
export function useMarketingConsent(enabled = true) {
  return useSWR<MarketingConsent>(enabled ? MARKETING_CONSENT_URL : null, getMarketingConsent, {
    revalidateOnFocus: false,
    shouldRetryOnError: false,
  });
}

/** Platform admin: the marketing list's counts and the last export (never the addresses). */
export function useMarketingListSummary(enabled = true) {
  return useSWR<MarketingListSummary>(enabled ? MARKETING_ADMIN_CONSENTS_URL : null, getMarketingListSummary, {
    revalidateOnFocus: true,
    shouldRetryOnError: false,
  });
}

/** Platform admin: one page of marketing contacts (names + Brevo state). `url` is null while disabled. */
export function useMarketingContacts(url: string | null) {
  return useSWR<MarketingContactsPage>(url, getMarketingContacts, {
    revalidateOnFocus: false,
    shouldRetryOnError: false,
    keepPreviousData: true,
  });
}

/** Platform admin: the Brevo contact-sync status card. */
export function useContactSyncStatus(enabled = true) {
  return useSWR<ContactSyncStatus>(enabled ? MARKETING_ADMIN_CONTACT_SYNC_URL : null, getContactSyncStatus, {
    revalidateOnFocus: true,
    shouldRetryOnError: false,
  });
}
