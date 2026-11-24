'use client';

import { useState } from 'react';
import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { Skeleton } from '@mantine/core';
import { IconArrowLeft, IconCopy, IconFlag, IconStarFilled } from '@tabler/icons-react';
import { useAuth } from '@/lib/hooks/use-auth';
import { useFormat, useT } from '@/i18n';
import { ApiError } from '@/lib/api/client';
import { posterUrl, previewUrl } from '@/lib/api/marketplace';
import { useMarketplaceMe, useMarketplaceRecipe } from '@/lib/hooks/use-marketplace';
import { CopyDialog } from './CopyDialog';
import { CostBadge } from './CostBadge';
import { RatingControl } from './RatingControl';
import { ReportDialog } from './ReportDialog';
import { SLOT_KIND_LABELS, categoryLabel, languageName, ratingDisplay, signInHref } from './marketplace-model';
import styles from './marketplace.module.css';

/**
 * One recipe, in full. Everything the author wrote is rendered as plain text (React escapes it; no markdown, no links made
 * from text), and the prompts the recipe sends to AI models are shown in full so the person copying can read them.
 */
export function RecipeDetailView({ id }: { id: string }) {
  const t = useT('marketplace.detail');
  const tr = useT();
  const fm = useFormat();
  const pathname = usePathname();
  const { user } = useAuth();
  const signedIn = user !== null;
  const { data: recipe, error, isLoading, mutate } = useMarketplaceRecipe(id);
  const { data: me } = useMarketplaceMe(id, signedIn);
  const [copyOpen, setCopyOpen] = useState(false);
  const [reportOpen, setReportOpen] = useState(false);

  const back = (
    <Link href="/community" className={styles.textLink}>
      <IconArrowLeft size={14} aria-hidden /> {t('back')}
    </Link>
  );

  if (isLoading) {
    return (
      <div className={styles.detail} role="status" aria-label={tr('marketplace.gallery.loading')} aria-busy="true">
        <Skeleton height={32} width="50%" />
        <Skeleton height={320} radius="xs" />
      </div>
    );
  }

  if (error || !recipe) {
    const gone = error instanceof ApiError && error.status === 404;
    return (
      <div className={styles.detail}>
        {back}
        <div className={styles.empty} role="alert">
          <h1 className={styles.sectionTitle}>{gone ? t('notFound') : t('loadFailed')}</h1>
          <p className={styles.hint}>{gone ? t('notFoundHint') : tr('marketplace.gallery.loadFailedHint')}</p>
          {!gone && (
            <button type="button" className={styles.button} onClick={() => void mutate()}>
              {tr('marketplace.gallery.tryAgain')}
            </button>
          )}
        </div>
      </div>
    );
  }

  const rating = ratingDisplay(recipe);
  const average = rating.kind === 'average' ? fm.number(rating.average, { minimumFractionDigits: 1, maximumFractionDigits: 1 }) : '';
  const category = categoryLabel(recipe.category);

  return (
    <article className={styles.detail} aria-labelledby="recipe-title">
      {back}
      <header className={styles.section}>
        <h1 className={styles.pageTitle} id="recipe-title">{recipe.name}</h1>
        <div className={styles.meta}>
          <span>{t('by', { author: recipe.authorLabel })}</span>
          <span>{t('version', { version: recipe.version })}</span>
          <span>{tr('marketplace.card.steps', { count: recipe.stepCount })}</span>
        </div>
        <div className={styles.badgeRow}>
          <CostBadge costClass={recipe.costClass} withHint />
          <span className={styles.badge}>{category ? tr.msg(category) : recipe.category}</span>
          <span className={styles.badge}>{languageName(recipe.language, fm.locale)}</span>
        </div>
        <div className={styles.meta}>
          <span className={styles.metaItem}>
            <IconCopy size={14} aria-hidden />
            {t('copiesStat', { count: recipe.copyCount })}
          </span>
          {rating.kind === 'average' ? (
            <span className={styles.metaItem}>
              <IconStarFilled size={14} aria-hidden />
              {t('ratingStat', { average, count: rating.count })}
            </span>
          ) : (
            <span>{t('ratingNew')}</span>
          )}
        </div>
      </header>

      <div className={styles.detailGrid}>
        <div className={styles.section}>
          {/* Controls, no autoplay; the poster stands in until the person presses play. */}
          <video
            className={styles.player}
            controls
            preload="metadata"
            playsInline
            poster={posterUrl(recipe.id)}
            src={previewUrl(recipe.id)}
            aria-label={t('previewLabel', { name: recipe.name })}
          >
            {t('previewUnavailable')}
          </video>
          <p className={styles.plain}>{recipe.description}</p>

          <section className={styles.section} aria-labelledby="recipe-steps">
            <h2 className={styles.sectionTitle} id="recipe-steps">{t('stepsTitle')}</h2>
            <ol className={styles.list}>
              {[...recipe.steps]
                .sort((a, b) => a.order - b.order)
                .map((s) => (
                  <li key={s.order}>
                    {s.label}
                    {s.limits && s.limits.length > 0 && <span className={styles.hint}>{` (${s.limits.join('; ')})`}</span>}
                  </li>
                ))}
            </ol>
          </section>

          <section className={styles.section} aria-labelledby="recipe-prompts">
            <h2 className={styles.sectionTitle} id="recipe-prompts">{t('promptsTitle')}</h2>
            <p className={styles.hint}>{t('promptsHint')}</p>
            {recipe.prompts.length === 0 ? (
              <p className={styles.plain}>{t('promptsNone')}</p>
            ) : (
              recipe.prompts.map((p) => (
                <div key={`${p.stepOrder}-${p.field}`} className={styles.section}>
                  <h3 className={styles.promptHeading}>{t('promptHeading', { order: p.stepOrder, field: p.field })}</h3>
                  {/* Read-only plain text, exactly as written; never parsed as markup. */}
                  <pre className={styles.prompt} tabIndex={0} role="region" aria-label={t('promptHeading', { order: p.stepOrder, field: p.field })}>{p.text}</pre>
                </div>
              ))
            )}
          </section>
        </div>

        <div className={styles.section}>
          <div className={styles.panel}>
            {signedIn ? (
              <button type="button" className={styles.buttonPrimary} onClick={() => setCopyOpen(true)}>
                <IconCopy size={16} aria-hidden />
                {tr('marketplace.copy.button')}
              </button>
            ) : (
              <>
                <Link href={signInHref(pathname)} className={styles.buttonPrimary}>
                  {tr('marketplace.copy.signInToCopy')}
                </Link>
                <p className={styles.hint}>{tr('marketplace.copy.signInHint')}</p>
              </>
            )}
          </div>

          <section className={styles.panel} aria-labelledby="recipe-needs">
            <h2 className={styles.sectionTitle} id="recipe-needs">{t('needsTitle')}</h2>
            {recipe.slots.length === 0 ? (
              <p className={styles.plain}>{t('needsNone')}</p>
            ) : (
              <ul className={styles.list}>
                {recipe.slots.map((s) => (
                  <li key={`${s.kind}-${s.stepOrder}-${s.label}`}>{t('slotItem', { kind: tr.msg(SLOT_KIND_LABELS[s.kind] ?? SLOT_KIND_LABELS.file), label: s.label })}</li>
                ))}
              </ul>
            )}
          </section>

          <section className={styles.panel} aria-labelledby="recipe-cost">
            <h2 className={styles.sectionTitle} id="recipe-cost">{t('costTitle')}</h2>
            <p className={styles.plain}>{recipe.costNote}</p>
          </section>

          <div className={styles.panel}>
            <RatingControl recipeId={recipe.id} signedIn={signedIn} me={me} />
          </div>

          {signedIn && (
            <div className={styles.panel}>
              {me?.reported ? (
                <p className={styles.hint}>{tr('marketplace.report.reported')}</p>
              ) : (
                <button type="button" className={styles.button} onClick={() => setReportOpen(true)}>
                  <IconFlag size={16} aria-hidden />
                  {tr('marketplace.report.button')}
                </button>
              )}
            </div>
          )}
        </div>
      </div>

      {signedIn && <CopyDialog recipeId={recipe.id} recipeName={recipe.name} opened={copyOpen} onClose={() => setCopyOpen(false)} />}
      {signedIn && <ReportDialog recipeId={recipe.id} opened={reportOpen} onClose={() => setReportOpen(false)} />}
    </article>
  );
}
