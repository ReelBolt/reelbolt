'use client';

import React, { useEffect, useRef, useState } from 'react';
import { Box, Loader } from '@mantine/core';
import { IconMusic, IconPhoto, IconVideo } from '@tabler/icons-react';
import { originalContentUrl } from '@/lib/editing/api/media';
import type { MediaPoolItem } from '@/lib/editing/media/types';
import { useOutputCheck } from '@/lib/outputs/output-check';
import { useT } from '@/i18n';

const ICON = { video: IconVideo, audio: IconMusic, image: IconPhoto } as const;

/**
 * R05.2: the tile's poster. A picture shows itself; a video shows its first frame (the browser decodes
 * it from the clip's own bytes, metadata only); sound shows an icon. The element is mounted only once the
 * tile scrolls near the viewport, so a long pool does not open every file at once. A source that cannot be
 * read falls back to the kind icon, never a broken frame.
 */
export function TilePoster({ projectId, item, height }: { projectId: string; item: MediaPoolItem; height: number }) {
  const t = useT('editor-media.tile');
  const tOut = useT('outputs.check');
  const ref = useRef<HTMLDivElement | null>(null);
  // Without IntersectionObserver (old browsers, tests) the poster simply loads at once.
  const [near, setNear] = useState(() => typeof IntersectionObserver === 'undefined');
  useEffect(() => {
    const el = ref.current;
    if (!el) return undefined;
    if (typeof IntersectionObserver === 'undefined') return undefined;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          setNear(true);
          observer.disconnect();
        }
      },
      { rootMargin: '200px' },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  const url = originalContentUrl(projectId, item.mediaRef);
  // A clip that was only just made can answer "still being checked": the icon holds the place, then the picture loads by itself.
  const check = useOutputCheck(url);
  const waiting = check.status === 'checking' || check.status === 'pending';
  const Icon = ICON[item.kind];
  const showMedia = near && check.status === 'unknown' && url !== null && item.kind !== 'audio';
  return (
    <Box
      ref={ref}
      data-testid="media-tile-poster"
      role="img"
      aria-label={t('preview', { name: item.fileName })}
      data-poster={showMedia ? item.kind : 'icon'}
      style={{ height, borderRadius: 6, overflow: 'hidden', background: 'var(--mantine-color-dark-8)', display: 'flex', alignItems: 'center', justifyContent: 'center' }}
    >
      {showMedia && item.kind === 'image' ? (
        // eslint-disable-next-line @next/next/no-img-element
        <img key={check.remountKey} src={url} alt="" loading="lazy" draggable={false} onError={check.onError} style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
      ) : showMedia ? (
        <video key={check.remountKey} src={`${url}#t=0.1`} preload="metadata" muted playsInline draggable={false} onError={check.onError} style={{ width: '100%', height: '100%', objectFit: 'cover', pointerEvents: 'none' }} />
      ) : (
        <>
          <Icon size={28} color="var(--mantine-color-gray-5)" />
          {waiting && <Loader size={14} color="gray" ml={6} aria-label={tOut('cue')} />}
        </>
      )}
    </Box>
  );
}
