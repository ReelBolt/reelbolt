/**
 * One place that turns the server's terse error strings into plain sentences, so every screen
 * (sign-in, account, invites, billing, runs) says the same thing. Matching is on the lowercased,
 * trimmed message or the machine code; anything unknown is passed through unchanged.
 */

import { msg, type Msg } from '@/i18n/msg';
import type { T } from '@/i18n/translator';
import { enT } from '../i18n-default';

export type FriendlyKind = 'invalid-credentials' | 'wrong-current-password' | 'link-expired' | 'already-member' | 'session' | 'other';

interface Rule {
  kind: FriendlyKind;
  test: RegExp;
  message: Msg;
}

const RULES: Rule[] = [
  {
    kind: 'invalid-credentials',
    test: /^(invalid[ _]credentials|invalid[ _]email or password|incorrect[ _]password)$/,
    message: msg('errors.friendly.invalidCredentials'),
  },
  {
    kind: 'wrong-current-password',
    test: /^current[ _]password (is )?incorrect$|^current[ _]password is (wrong|invalid)$/,
    message: msg('errors.friendly.wrongCurrentPassword'),
  },
  {
    kind: 'link-expired',
    test: /^invalid or expired (token|link)$|^(token|link) (is )?(invalid|expired)$|^invalid[ _]token$|^expired[ _]token$/,
    message: msg('errors.friendly.linkExpired'),
  },
  {
    kind: 'already-member',
    test: /^(that person|this person|user) is already a member$|^already[ _]a[ _]member$/,
    message: msg('errors.friendly.alreadyMember'),
  },
  {
    kind: 'session',
    test: /^unauthorized$|^missing or invalid authorization header$|^unauthenticated$/,
    message: msg('errors.friendly.session'),
  },
];

export interface FriendlyError {
  kind: FriendlyKind;
  message: string;
}

/**
 * Classify and rewrite an error (or raw message). `fallback` is used when there is no message at all.
 * `t` is an unscoped translator (`useT()`); without one the sentence is English.
 */
export function friendlyError(error: unknown, fallback?: string, t: T = enT): FriendlyError {
  const raw =
    typeof error === 'string' ? error : error instanceof Error ? error.message : '';
  const code =
    error && typeof error === 'object' && 'code' in error && typeof (error as { code: unknown }).code === 'string'
      ? ((error as { code: string }).code)
      : '';
  for (const candidate of [raw, code]) {
    const key = candidate.trim().toLowerCase().replace(/[.!]+$/, '');
    if (!key) continue;
    const rule = RULES.find((r) => r.test.test(key));
    if (rule) return { kind: rule.kind, message: t.msg(rule.message) };
  }
  return { kind: 'other', message: raw.trim() || fallback || t('errors.generic.unknown') };
}

/** Shorthand when only the sentence is needed. */
export function friendlyMessage(error: unknown, fallback?: string, t: T = enT): string {
  return friendlyError(error, fallback, t).message;
}
