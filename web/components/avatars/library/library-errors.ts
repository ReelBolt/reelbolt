'use client';

import { useCallback } from 'react';
import { useT } from '@/i18n';
import { ApiError } from '@/lib/api/client';
import { describeError } from '@/lib/api/describe-error';

/**
 * The sentence for a refused library call, in the active language: a library code reads
 * `avatars.library.errors.<code>`, a code the Avatars tab already words reads `avatars.errors.<code>`
 * (the statement, the adult tick, the keep-for range), anything else falls back to the shared
 * `describeError` (generic codes, the server's own sentence, the HTTP status).
 */
export function useLibraryErrorMessage(): (err: unknown, fallback?: string) => string {
  const t = useT();
  return useCallback((err: unknown, fallback?: string) => {
    if (err instanceof ApiError && err.code) {
      for (const key of [`avatars.library.errors.${err.code}`, `avatars.errors.${err.code}`]) {
        if (t.has(key)) return t(key, { file: t('avatars.errors.aFile') });
      }
    }
    return describeError(t, err, fallback);
  }, [t]);
}
