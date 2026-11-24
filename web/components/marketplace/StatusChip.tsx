'use client';

import { useT } from '@/i18n';
import type { MarketplacePreviewStatus, MarketplaceRecipeStatus } from '@/lib/types/marketplace';
import { statusLabel } from './marketplace-model';
import styles from './marketplace.module.css';

/** The status words (preparing, waiting for review, live, ...). Words carry the meaning; the tint only helps. */
export function StatusChip({ status, preview }: { status: MarketplaceRecipeStatus; preview?: MarketplacePreviewStatus }) {
  const t = useT();
  const bad = status === 'rejected' || status === 'removed' || (preview === 'failed' && (status === 'draft' || status === 'pending'));
  return (
    <span className={`${styles.badge} ${bad ? styles.badgeBad : status === 'listed' ? styles.badgeOk : ''}`}>
      {t.msg(statusLabel(status, preview))}
    </span>
  );
}
