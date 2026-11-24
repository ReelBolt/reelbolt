import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';

/**
 * Display name of a workspace in the UI (A15-UI). The Personal workspace is always
 * labelled "Personal" regardless of its stored name (decision D1: every user gets an
 * auto-created, single-member Personal workspace); Team and Platform orgs show their
 * name. Shared by the org switcher and the user menu so the rule lives in one place.
 */
export function orgDisplayLabel(org: { name: string; kind?: string }, t: T = enT): string {
  return org.kind === 'Personal' ? t('libLabels.org.personal') : org.name;
}
