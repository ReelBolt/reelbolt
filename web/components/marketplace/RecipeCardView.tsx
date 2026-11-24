'use client';

import { useEffect, useId, useRef, useState } from 'react';
import Link from 'next/link';
import { IconCopy, IconStarFilled } from '@tabler/icons-react';
import { VisuallyHidden } from '@mantine/core';
import { useFormat, useT } from '@/i18n';
import { posterUrl, previewUrl } from '@/lib/api/marketplace';
import type { MarketplaceRecipeCard } from '@/lib/types/marketplace';
import { CostBadge } from './CostBadge';
import { previewAllowed, ratingDisplay } from './marketplace-model';
import styles from './marketplace.module.css';

/** Read at the moment of the hover or focus, so a device that changes (a tablet with a mouse) is judged as it is now. */
function previewEnvironmentAllows(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return false;
  return previewAllowed({
    coarsePointer: window.matchMedia('(hover: none), (pointer: coarse)').matches,
    reducedMotion: window.matchMedia('(prefers-reduced-motion: reduce)').matches,
  });
}

/**
 * One recipe in the grid. The poster shows on load; the preview video is mounted only while the card is hovered or has
 * keyboard focus, muted and looping, and never on touch devices or with reduced motion (a tap opens the detail page).
 */
export function RecipeCardView({ recipe }: { recipe: MarketplaceRecipeCard }) {
  const t = useT('marketplace.card');
  const fm = useFormat();
  const titleId = useId();
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const rating = ratingDisplay(recipe);

  const start = () => {
    if (previewEnvironmentAllows()) setPreviewing(true);
  };
  const stop = () => setPreviewing(false);

  useEffect(() => {
    if (!previewing) return;
    const video = videoRef.current;
    if (!video) return;
    video.muted = true;
    const played = typeof video.play === 'function' ? video.play() : undefined;
    if (played && typeof played.catch === 'function') played.catch(() => setPreviewing(false));
  }, [previewing]);

  return (
    <li>
      <article
        className={styles.card}
        aria-labelledby={titleId}
        onMouseEnter={start}
        onMouseLeave={stop}
        onFocus={start}
        onKeyDown={(e) => {
          if (e.key === 'Escape') stop();
        }}
        onBlur={(e) => {
          if (!e.currentTarget.contains(e.relatedTarget)) stop();
        }}
      >
        <div className={styles.media}>
          {/* A plain img on purpose: the poster is a dynamic API image, not a build-time asset. Decorative: the title names the card. */}
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img className={styles.poster} src={posterUrl(recipe.id)} alt="" loading="lazy" decoding="async" />
          {previewing && (
            <video
              ref={videoRef}
              className={styles.previewVideo}
              src={previewUrl(recipe.id)}
              aria-label={t('previewLabel', { name: recipe.name })}
              muted
              loop
              playsInline
              preload="auto"
              tabIndex={-1}
            />
          )}
        </div>
        <div className={styles.cardBody}>
          <h3 className={styles.cardTitle} id={titleId}>
            <Link href={`/community/${recipe.id}`} className={styles.cardLink}>
              {recipe.name}
            </Link>
          </h3>
          <p className={styles.description}>{recipe.description}</p>
          <div className={styles.badgeRow}>
            <CostBadge costClass={recipe.costClass} />
          </div>
          <div className={styles.meta}>
            <span>{t('by', { author: recipe.authorLabel })}</span>
          </div>
          <div className={styles.meta}>
            {rating.kind === 'average' ? (
              <span className={styles.metaItem}>
                <IconStarFilled size={14} aria-hidden />
                <span aria-hidden>{fm.number(rating.average, { minimumFractionDigits: 1, maximumFractionDigits: 1 })}</span>
                <VisuallyHidden>
                  {t('rating', { average: fm.number(rating.average, { minimumFractionDigits: 1, maximumFractionDigits: 1 }), count: rating.count })}
                </VisuallyHidden>
              </span>
            ) : (
              <span className={styles.badge}>
                {t('new')}
                <VisuallyHidden>{` ${t('newHint')}`}</VisuallyHidden>
              </span>
            )}
            <span className={styles.metaItem}>
              <IconCopy size={14} aria-hidden />
              {t('copies', { count: recipe.copyCount })}
            </span>
            <span>{t('steps', { count: recipe.stepCount })}</span>
          </div>
        </div>
      </article>
    </li>
  );
}
