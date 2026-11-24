'use client';

import type { AudioHTMLAttributes, CSSProperties, ImgHTMLAttributes, Ref, VideoHTMLAttributes } from 'react';
import { Loader } from '@mantine/core';
import { useOutputCheck, type OutputCheck } from '@/lib/outputs/output-check';
import { outputMediaKind } from '@/lib/outputs/media-kind';

type Shared = {
  src: string;
  /** Share the check with sibling UI (a download link); otherwise the player keeps its own. */
  check?: OutputCheck;
  /** Worded for a file that is not a video ("Checking this file"). */
  neutral?: boolean;
};

/** The player's own box with a short note (and a small spinner while it waits), in place of a broken player. */
function CheckNote({ check, className, style }: { check: OutputCheck; className?: string; style?: CSSProperties }) {
  const waiting = check.status === 'pending' || check.status === 'checking';
  return (
    <div
      role="status"
      className={className}
      style={{
        ...style, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
        padding: 12, minHeight: 72, textAlign: 'center', fontSize: 14,
        background: 'var(--mantine-color-dark-8)', color: 'var(--mantine-color-gray-4)',
      }}
    >
      {waiting && <Loader size="sm" color="gray" aria-hidden />}
      <span>{check.message}</span>
    </div>
  );
}

/**
 * A `<video>` for an output URL that keeps the caller's own markup and styling. While the output is
 * still being checked it shows the same box with a short note, then loads by itself; a refused or
 * missing output says so in plain words instead of a broken player.
 */
export function CheckedVideo({ src, check: shared, neutral, ...rest }: Shared & Omit<VideoHTMLAttributes<HTMLVideoElement>, 'src' | 'onError'> & { ref?: Ref<HTMLVideoElement> }) {
  const own = useOutputCheck(shared ? null : src, { neutral });
  const check = shared ?? own;
  if (check.message) return <CheckNote check={check} className={rest.className} style={rest.style} />;
  return <video key={check.remountKey} {...rest} src={src} onError={check.onError} />;
}

/** The audio twin of `CheckedVideo`. */
export function CheckedAudio({ src, check: shared, neutral = true, ...rest }: Shared & Omit<AudioHTMLAttributes<HTMLAudioElement>, 'src' | 'onError'> & { ref?: Ref<HTMLAudioElement> }) {
  const own = useOutputCheck(shared ? null : src, { neutral });
  const check = shared ?? own;
  if (check.message) return <CheckNote check={check} className={rest.className} style={{ ...rest.style, minHeight: 48 }} />;
  return <audio key={check.remountKey} {...rest} src={src} onError={check.onError} />;
}

/** The picture twin: an `<img>` that shows a short note instead of a broken picture, then loads by itself. */
export function CheckedImage({ src, check: shared, neutral = true, alt, ...rest }: Shared & Omit<ImgHTMLAttributes<HTMLImageElement>, 'src' | 'onError' | 'alt'> & { alt: string }) {
  const own = useOutputCheck(shared ? null : src, { neutral });
  const check = shared ?? own;
  if (check.message) return <CheckNote check={check} className={rest.className} style={rest.style} />;
  // eslint-disable-next-line @next/next/no-img-element -- authenticated same-origin stream, not an optimizable static asset
  return <img key={check.remountKey} {...rest} alt={alt} src={src} onError={check.onError} />;
}

/**
 * A step output shown as what it is: a picture as `<img>`, a sound as `<audio>`, anything else as `<video>`.
 * The kind comes from the file name or storage key (`outputMediaKind`); `alt` labels a picture.
 */
export function CheckedOutputMedia({
  src, fileName, alt, className, style, controls, preload, playsInline, 'aria-label': ariaLabel, 'data-testid': testId,
}: {
  src: string;
  fileName: string | null | undefined;
  alt: string;
  className?: string;
  style?: CSSProperties;
  controls?: boolean;
  preload?: 'none' | 'metadata' | 'auto';
  playsInline?: boolean;
  'aria-label'?: string;
  'data-testid'?: string;
}) {
  const kind = outputMediaKind(fileName);
  if (kind === 'image') {
    return <CheckedImage src={src} alt={alt} className={className} style={{ objectFit: 'contain', ...style }} data-testid={testId} />;
  }
  if (kind === 'audio') {
    return <CheckedAudio src={src} className={className} style={style} controls={controls} preload={preload} aria-label={ariaLabel ?? alt} data-testid={testId} />;
  }
  return <CheckedVideo src={src} className={className} style={style} controls={controls} preload={preload} playsInline={playsInline} aria-label={ariaLabel} data-testid={testId} />;
}
