'use client';

import { IconAlertTriangle, IconCoin } from '@tabler/icons-react';
import { costBadgeFor } from '@/components/workflow-apps/gallery-model';
import { useT } from '@/i18n';
import type { AppCostClass } from '@/lib/types/workflow';
import styles from './marketplace.module.css';

/** The app gallery's cost vocabulary ("Free", "Uses credits", ...): icon and words together, never colour alone. */
export function CostBadge({ costClass, withHint = false }: { costClass: AppCostClass; withHint?: boolean }) {
  const t = useT();
  const badge = costBadgeFor(costClass);
  return (
    <>
      <span className={`${styles.badge} ${badge.tone === 'amber' ? styles.badgeAmber : ''}`}>
        {badge.tone === 'amber' ? <IconAlertTriangle size={14} aria-hidden /> : <IconCoin size={14} aria-hidden />}
        {t.msg(badge.label)}
      </span>
      {withHint && badge.hint && <span className={styles.costHint}>{t.msg(badge.hint)}</span>}
    </>
  );
}
