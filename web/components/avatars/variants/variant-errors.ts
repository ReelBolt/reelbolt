'use client';

import { useCallback } from 'react';
import { useT } from '@/i18n';
import { ApiError } from '@/lib/api/client';
import type { AvatarSubjectKind } from '@/lib/types/avatars';
import { useAvatarErrorMessage } from '../avatar-errors';

/** The plan feature a `402 PLAN_NOT_ENTITLED` names (`avatar_objects`, `video_generation`, `avatars`), when it names one. */
export function refusedFeature(body: unknown): string | null {
  if (typeof body !== 'object' || body === null) return null;
  const feature = (body as Record<string, unknown>).feature;
  return typeof feature === 'string' && feature ? feature : null;
}

/** True for the refusal that asks for the project's permission to send media (`409 CONSENT_REQUIRED`). */
export function needsMediaConsent(err: unknown): boolean {
  return err instanceof ApiError && err.code === 'CONSENT_REQUIRED';
}

/**
 * The sentence for a refused variant call, in the active language. A variant's own codes read
 * `avatars.variants.errors.<code>`; a plan refusal reads the sentence of the feature it names; too few credits
 * shows the server's own sentence (it carries the figures); a validation refusal shows the server's message and
 * each of its points. Everything else is an ordinary avatar refusal (`useAvatarErrorMessage`), and so is every
 * refusal while `forVariant` is false (a create form that is not making a variant).
 */
export function useVariantErrorMessage(
  fileName: (fileId: string) => string | null, subject: AvatarSubjectKind = 'Object', forVariant = true,
): (err: unknown, fallback?: string) => string {
  const t = useT();
  const describeAvatar = useAvatarErrorMessage(fileName, subject);
  return useCallback((err: unknown, fallback?: string) => {
    // A form that is making an ordinary avatar keeps the ordinary sentences.
    if (forVariant && err instanceof ApiError) {
      const code = err.code;
      if (code === 'PLAN_NOT_ENTITLED') {
        const key = `avatars.variants.planErrors.${refusedFeature(err.body) ?? ''}`;
        if (t.has(key)) return t(key);
      }
      if (code === 'INSUFFICIENT_CREDITS' && err.message.trim()) return err.message;
      if (code && t.has(`avatars.variants.errors.${code}`)) return t(`avatars.variants.errors.${code}`);
      if (err.status === 429 && !(code && t.has(`errors.${code}`))) return t('avatars.variants.errors.RUN_RATE_LIMITED');
      if (err.status === 400 && err.errors.length > 0 && !(code && (t.has(`avatars.errors.${code}`) || t.has(`avatars.objectErrors.${code}`)))) {
        const points = err.errors.filter((e) => e.trim() && e.trim() !== err.message.trim());
        return [err.message.trim(), ...points].filter(Boolean).join(' ');
      }
    }
    return describeAvatar(err, fallback);
  }, [t, describeAvatar, forVariant]);
}
