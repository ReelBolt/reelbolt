'use client';

import { useMemo } from 'react';
import { useFormat, useT } from '@/i18n';
import { libraryItemStatus, type LibraryItemStatus } from '@/lib/avatars/library';
import type { AvatarLibraryItem } from '@/lib/types/avatar-library';

export interface LibraryWords {
  /** The status of a library item, as its badge reads ("Ready", "Expires on ...", "Consent withdrawn"). */
  statusLabel: (item: AvatarLibraryItem, status?: LibraryItemStatus) => string;
  /** Why the item cannot be used, in plain words. */
  reasonOf: (item: AvatarLibraryItem) => string;
}

/** The status and the reason of a library item in the active language, the same words in the picker and on the workspace page. */
export function useLibraryWords(): LibraryWords {
  const t = useT('avatars.library');
  const f = useFormat();
  return useMemo(() => ({
    statusLabel: (item, status = libraryItemStatus(item)) => (
      status === 'expiresSoon' && item.retentionUntil
        ? t('status.expiresSoon', { date: f.date(item.retentionUntil) })
        : t(`status.${status}`)
    ),
    reasonOf: (item) => {
      const reason = item.unusableReason;
      return reason && t.has(`reasons.${reason}`) ? t(`reasons.${reason}`) : t('reasons.unknown');
    },
  }), [t, f]);
}
