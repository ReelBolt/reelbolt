import type { ReactNode } from 'react';

export const FIGURE_WIDTHS = [640, 1280] as const;

export interface FigureProps {
  /** Image name stem, e.g. `timeline-editor`. Files live in `public/blog/img/`. */
  name: string;
  /** Required. One sentence describing what the picture shows. */
  alt: string;
  width: number;
  height: number;
  caption?: ReactNode;
  /** The hero image: loads eagerly with high fetch priority. Everything else lazy-loads. */
  priority?: boolean;
  sizes?: string;
}

/**
 * Responsive art-directed image: AVIF and WebP sources at 640 and 1280 wide, with the 1280 WebP as
 * the `<img>` fallback. `<picture>` cannot come from `next/image` in a static export, so the
 * `<img>` is written by hand; `no-img-element` already exempts an `<img>` inside `<picture>`.
 */
export function Figure({
  name,
  alt,
  width,
  height,
  caption,
  priority = false,
  sizes = '(min-width: 1024px) 720px, 100vw',
}: FigureProps) {
  const srcSet = (ext: 'avif' | 'webp') =>
    FIGURE_WIDTHS.map((w) => `/blog/img/${name}-${w}.${ext} ${w}w`).join(', ');

  return (
    <figure className="mx-auto my-10 max-w-[40rem]">
      <a
        href={`/blog/img/${name}-1280.webp`}
        aria-label="Open full-size screenshot"
        className="block focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong"
      >
        <picture>
          <source type="image/avif" srcSet={srcSet('avif')} sizes={sizes} />
          <source type="image/webp" srcSet={srcSet('webp')} sizes={sizes} />
          <img
            src={`/blog/img/${name}-1280.webp`}
            alt={alt}
            width={width}
            height={height}
            loading={priority ? 'eager' : 'lazy'}
            decoding="async"
            fetchPriority={priority ? 'high' : 'auto'}
            className="h-auto w-full rounded-[2px] border border-line"
          />
        </picture>
      </a>
      {caption && (
        <figcaption className="mt-3 font-mono text-[11px] uppercase tracking-eyebrow text-ink-faint">
          {caption}
        </figcaption>
      )}
    </figure>
  );
}
