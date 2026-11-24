'use client';

import Image from 'next/image';
import { useRef, useState } from 'react';
import { useT } from '@/i18n';
import { useReducedMotion } from '@/lib/use-reduced-motion';

interface PreviewVideoProps {
  id: string;
  name: string;
  /** False until the recipe's preview is ready: the poster shows alone. */
  hasPreview: boolean;
  /** `hover`: plays while the pointer is over it (cards). `tap`: a button starts and stops it (detail page). */
  mode: 'hover' | 'tap';
}

/**
 * Poster plus a muted preview that never autoplays on load. The video element (and its download) only exists
 * once the visitor hovers (cards) or presses play (detail). Reduced-motion visitors get no hover playback.
 */
export function PreviewVideo({ id, name, hasPreview, mode }: PreviewVideoProps) {
  const t = useT('recipes');
  const reduced = useReducedMotion();
  const videoRef = useRef<HTMLVideoElement>(null);
  const [active, setActive] = useState(false);
  const [posterFailed, setPosterFailed] = useState(false);

  const start = () => {
    setActive(true);
    // The element mounts on this render; play once it is there.
    requestAnimationFrame(() => {
      void videoRef.current?.play().catch(() => undefined);
    });
  };
  const stop = () => {
    videoRef.current?.pause();
    setActive(false);
  };

  const hoverProps =
    mode === 'hover' && hasPreview && !reduced
      ? { onMouseEnter: start, onMouseLeave: stop }
      : {};

  return (
    <div className="relative aspect-video w-full overflow-hidden border border-line bg-paper-2" {...hoverProps}>
      {!posterFailed && (
        <Image
          src={`/api/recipes/${id}/poster`}
          alt={t('card.previewLabel', { name })}
          fill
          unoptimized
          sizes="(min-width: 1024px) 384px, (min-width: 640px) 50vw, 100vw"
          className="object-cover"
          onError={() => setPosterFailed(true)}
        />
      )}
      {active && hasPreview && (
        <video
          ref={videoRef}
          src={`/api/recipes/${id}/preview`}
          poster={`/api/recipes/${id}/poster`}
          muted
          loop
          playsInline
          preload="none"
          controls={mode === 'tap'}
          aria-label={t('card.previewLabel', { name })}
          className="absolute inset-0 h-full w-full bg-paper object-cover"
        />
      )}
      {mode === 'tap' && (
        <div className="absolute inset-x-0 bottom-0 flex justify-start p-3">
          {hasPreview ? (
            <button
              type="button"
              onClick={active ? stop : start}
              aria-pressed={active}
              className="inline-flex min-h-11 items-center bg-ink px-4 font-mono text-xs uppercase tracking-eyebrow text-paper hover:bg-ink-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong"
            >
              {active ? t('preview.pause') : t('preview.play')}
            </button>
          ) : (
            <span className="bg-ink px-3 py-2 font-mono text-xs uppercase tracking-eyebrow text-paper">
              {t('preview.none')}
            </span>
          )}
        </div>
      )}
    </div>
  );
}
