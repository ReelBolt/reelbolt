'use client';

import { useCallback } from 'react';
import { useT } from '@/i18n';
import { describeError } from './describe-error';

/** `const describe = useApiErrorMessage(); describe(err, 'Could not save.')` — the active-language sentence for an API error. */
export function useApiErrorMessage(): (err: unknown, fallback?: string) => string {
  const t = useT();
  return useCallback((err, fallback) => describeError(t, err, fallback), [t]);
}
