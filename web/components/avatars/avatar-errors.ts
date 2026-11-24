'use client';

import { useCallback } from 'react';
import { useT } from '@/i18n';
import { ApiError } from '@/lib/api/client';
import { describeError } from '@/lib/api/describe-error';
import { refusedFileId } from '@/lib/avatars/model';
import type { AvatarSubjectKind } from '@/lib/types/avatars';

/**
 * The sentence for a refused avatar call, in the active language: a known avatar code reads
 * `avatars.errors.<code>` (with the file it names, when it names one), anything else falls back to
 * the shared `describeError` (generic codes, the server's own sentence, the HTTP status).
 * For an object (`subject`), a code whose person sentence talks about "the avatar", the person or a
 * recording reads `avatars.objectErrors.<code>` instead, when there is one.
 */
export function useAvatarErrorMessage(
  fileName: (fileId: string) => string | null, subject: AvatarSubjectKind = 'Person',
): (err: unknown, fallback?: string) => string {
  const t = useT();
  return useCallback((err: unknown, fallback?: string) => {
    if (err instanceof ApiError && err.code) {
      const own = `avatars.objectErrors.${err.code}`;
      const shared = `avatars.errors.${err.code}`;
      const key = subject === 'Object' && t.has(own) ? own : shared;
      if (t.has(key)) {
        const id = refusedFileId(err.body);
        const file = (id && fileName(id)) || t('avatars.errors.aFile');
        return t(key, { file });
      }
    }
    return describeError(t, err, fallback);
  }, [t, fileName, subject]);
}
