'use client';

import { useState } from 'react';
import { IconBox, IconUserCircle } from '@tabler/icons-react';
import { avatarLibraryPictureUrl } from '@/lib/api/avatar-library';
import type { AvatarLibraryItem } from '@/lib/types/avatar-library';
import { useT } from '@/i18n';

/**
 * The picture of a library item in a square box: its primary picture, streamed by the API, or the icon of its
 * kind when it has none or the picture cannot be loaded.
 */
export function LibraryPicture({ item, size = 72 }: {
  item: Pick<AvatarLibraryItem, 'id' | 'name' | 'subjectKind' | 'primaryReferenceId'>;
  size?: number;
}) {
  const t = useT('avatars.card');
  const [failed, setFailed] = useState(false);
  const referenceId = failed ? null : item.primaryReferenceId;
  const icon = Math.round(size * 0.55);
  return (
    <div
      style={{
        width: size, height: size, flex: `0 0 ${size}px`, borderRadius: 8, overflow: 'hidden',
        background: 'var(--mantine-color-default-hover)', display: 'grid', placeItems: 'center',
      }}
    >
      {referenceId ? (
        // eslint-disable-next-line @next/next/no-img-element -- authenticated same-origin stream, not an optimizable static asset
        <img
          src={avatarLibraryPictureUrl(item.id, referenceId)} alt={t('pictureAlt', { name: item.name })} loading="lazy"
          onError={() => setFailed(true)} style={{ width: '100%', height: '100%', objectFit: 'cover' }}
        />
      ) : item.subjectKind === 'Object'
        ? <IconBox size={icon} stroke={1.3} aria-hidden />
        : <IconUserCircle size={icon} stroke={1.3} aria-hidden />}
    </div>
  );
}
