'use client';

import { useMemo } from 'react';
import { useFormat, useT } from '@/i18n';
import { avatarStatus, isObjectAvatar, type AvatarStatus } from '@/lib/avatars/model';
import type { ProjectAvatar } from '@/lib/types/avatars';

export interface AvatarWords {
  /** The status of the avatar, as its badge reads ("Ready", "Expires on ...", "File missing"). */
  statusLabel: (avatar: ProjectAvatar, status?: AvatarStatus) => string;
  /** Why the avatar cannot be used, in plain words. */
  reasonOf: (avatar: ProjectAvatar) => string;
}

/**
 * The status and the reason of an avatar in the active language, the same words in the Avatars tab and in every
 * picker. An object reads its own sentence where the person's one talks about consent or "the person"
 * (`avatars.objectStatus`, `avatars.objectReasons`), and the shared one everywhere else.
 */
export function useAvatarWords(): AvatarWords {
  const t = useT('avatars');
  const f = useFormat();
  return useMemo(() => ({
    statusLabel: (avatar, status = avatarStatus(avatar)) => {
      if (status === 'expiresSoon' && avatar.retentionUntil) return t('status.expiresSoon', { date: f.date(avatar.retentionUntil) });
      if (isObjectAvatar(avatar) && t.has(`objectStatus.${status}`)) return t(`objectStatus.${status}`);
      return t(`status.${status}`);
    },
    reasonOf: (avatar) => {
      const reason = avatar.unselectableReason;
      if (!reason) return t('reasons.unknown');
      if (isObjectAvatar(avatar) && t.has(`objectReasons.${reason}`)) return t(`objectReasons.${reason}`);
      return t.has(`reasons.${reason}`) ? t(`reasons.${reason}`) : t('reasons.unknown');
    },
  }), [t, f]);
}
