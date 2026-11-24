'use client';

import { useState } from 'react';
import Link from 'next/link';
import { Skeleton } from '@mantine/core';
import { PageHeader } from '@/components/shared/PageHeader';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { useFormat, useT } from '@/i18n';
import { unpublishRecipe } from '@/lib/api/marketplace';
import { useMyRecipes } from '@/lib/hooks/use-marketplace-owner';
import type { MineRecipe } from '@/lib/types/marketplace';
import { PublishDialog } from './PublishDialog';
import { PREVIEW_STATUS_LABELS, marketplaceErrorMessage, ratingDisplay, authorReasonMessage } from './marketplace-model';
import { StatusChip } from './StatusChip';
import styles from './marketplace.module.css';

/** The caller organization's recipes: status, reason category, preview status, stats; update, unpublish, public link. */
export function MyRecipes() {
  const t = useT('marketplace.mine');
  const tr = useT();
  const fm = useFormat();
  const { items, error, isLoading, mutate } = useMyRecipes();
  const [updating, setUpdating] = useState<MineRecipe | null>(null);
  const [unpublishing, setUnpublishing] = useState<MineRecipe | null>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);

  const confirmUnpublish = async () => {
    if (!unpublishing) return;
    setBusy(true);
    setFailure(null);
    try {
      await unpublishRecipe(unpublishing.id);
      setUnpublishing(null);
      await mutate();
    } catch (err: unknown) {
      setUnpublishing(null);
      setFailure(marketplaceErrorMessage(tr, err));
    } finally {
      setBusy(false);
    }
  };

  let body;
  if (isLoading) {
    body = <Skeleton height={120} radius="xs" role="status" aria-label={t('loading')} />;
  } else if (error && !items) {
    body = (
      <div className={styles.empty} role="alert">
        <p className={styles.error}>{t('loadFailed')}</p>
        <button type="button" className={styles.button} onClick={() => void mutate()}>
          {tr('marketplace.gallery.tryAgain')}
        </button>
      </div>
    );
  } else if (!items || items.length === 0) {
    body = (
      <div className={styles.empty}>
        <p className={styles.plain}>{t('empty')}</p>
        <p className={styles.hint}>{t('emptyHint')}</p>
      </div>
    );
  } else {
    body = (
      <ul className={styles.table}>
        {items.map((r) => {
          const rating = ratingDisplay(r);
          const ended = r.status === 'removed' || r.status === 'unpublished';
          return (
            <li key={r.id} className={styles.row}>
              <div className={styles.rowHead}>
                <h2 className={styles.sectionTitle}>{r.name}</h2>
                <StatusChip status={r.status} preview={r.previewStatus} />
              </div>
              {(r.status === 'rejected' || r.status === 'removed') && (
                <p className={styles.error}>{t('reason', { reason: tr.msg(authorReasonMessage(r.reasonCategory ?? 'policy')) })}</p>
              )}
              {r.previewStatus === 'failed' && (r.status === 'draft' || r.status === 'pending') && (
                <p className={styles.error} role="alert">{t('previewFailedHelp')}</p>
              )}
              {r.hiddenByReports && (
                <div className={styles.badgeRow}>
                  <span className={`${styles.badge} ${styles.badgeAmber}`}>{t('hiddenByReports')}</span>
                </div>
              )}
              <div className={styles.meta}>
                <span>{tr('marketplace.detail.version', { version: r.version })}</span>
                <span>{t('preview', { status: tr.msg(PREVIEW_STATUS_LABELS[r.previewStatus] ?? PREVIEW_STATUS_LABELS.preparing) })}</span>
                <span>{t('copies', { count: fm.number(r.copyCount) })}</span>
                <span>{t('runs', { count: fm.number(r.runCount) })}</span>
                <span>
                  {rating.kind === 'average'
                    ? t('rating', { average: fm.number(rating.average, { minimumFractionDigits: 1, maximumFractionDigits: 1 }), count: rating.count })
                    : tr('marketplace.card.new')}
                </span>
              </div>
              <div className={styles.actions}>
                {r.status === 'listed' && !r.hiddenByReports && (
                  <Link href={`/community/${r.id}`} className={styles.button}>
                    {t('viewPublic')}
                  </Link>
                )}
                {!ended && (
                  <button type="button" className={styles.button} onClick={() => setUpdating(r)}>
                    {t('update')}
                  </button>
                )}
                {(r.status === 'listed' || r.status === 'pending') && (
                  <button type="button" className={styles.button} onClick={() => setUnpublishing(r)}>
                    {t('unpublish')}
                  </button>
                )}
              </div>
            </li>
          );
        })}
      </ul>
    );
  }

  return (
    <div className={styles.root}>
      <PageHeader title={t('title')} />
      <p className={styles.hint}>{t('hint')}</p>
      {failure && <p className={styles.error} role="alert">{failure}</p>}
      {body}
      {updating && <PublishDialog existing={updating} onClose={() => setUpdating(null)} onDone={() => void mutate()} />}
      <ConfirmModal
        opened={unpublishing !== null}
        onClose={() => setUnpublishing(null)}
        onConfirm={() => void confirmUnpublish()}
        title={t('unpublishTitle')}
        message={t('unpublishMessage', { name: unpublishing?.name ?? '' })}
        confirmLabel={t('unpublish')}
        loading={busy}
      />
    </div>
  );
}
