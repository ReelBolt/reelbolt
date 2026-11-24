'use client';

import { useEffect, useRef, useState } from 'react';
import { Loader } from '@mantine/core';
import { IconMovie, IconPlayerPlayFilled } from '@tabler/icons-react';
import { useOutputCheck } from '@/lib/outputs/output-check';
import { outputMediaKind } from '@/lib/outputs/media-kind';
import { useT } from '@/i18n';
import classes from './outputs.module.css';

/**
 * First-frame preview of a video. The <video> element is only mounted once the thumb scrolls into
 * view, so a grid of dozens of renders does not open dozens of connections at once.
 */
export function VideoThumb({ src, showPlay = true, fileName, imageSrc }: { src: string; showPlay?: boolean; fileName?: string | null; imageSrc?: string }) {
  const isImage = outputMediaKind(fileName) === 'image';
  const ref = useRef<HTMLSpanElement>(null);
  // Without IntersectionObserver (very old browsers) mount immediately.
  const [visible, setVisible] = useState(() => typeof IntersectionObserver === 'undefined');
  // Only a video the server has cleared is mounted; one still being prepared keeps the placeholder and appears by itself.
  const check = useOutputCheck(src);
  const t = useT('outputs.check');
  const waiting = check.status === 'checking' || check.status === 'pending';

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    if (typeof IntersectionObserver === 'undefined') return;
    const io = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          setVisible(true);
          io.disconnect();
        }
      },
      { rootMargin: '200px' },
    );
    io.observe(el);
    return () => io.disconnect();
  }, []);

  return (
    <span ref={ref} className={classes.thumb} aria-hidden={waiting ? undefined : true}>
      <span className={classes.thumbFallback}>
        <IconMovie size={26} stroke={1.5} />
        {waiting && <Loader size={14} color="gray" ml={6} role="status" aria-label={t('cue')} />}
      </span>
      {visible && check.status === 'unknown' && isImage && (
        // eslint-disable-next-line @next/next/no-img-element
        <img key={check.remountKey} src={imageSrc ?? src} alt="" loading="lazy" tabIndex={-1} onError={check.onError} />
      )}
      {visible && check.status === 'unknown' && !isImage && (
        <video
          key={check.remountKey}
          src={src}
          preload="metadata"
          muted
          playsInline
          tabIndex={-1}
          onLoadedMetadata={(e) => {
            // Renders usually fade in from black, so frame 0 is a poor thumbnail.
            const v = e.currentTarget;
            if (Number.isFinite(v.duration) && v.duration > 0) v.currentTime = Math.min(1.5, v.duration * 0.25);
          }}
          onError={check.onError}
        />
      )}
      {showPlay && !isImage && (
        <span className={classes.play}>
          <IconPlayerPlayFilled size={13} />
        </span>
      )}
    </span>
  );
}
