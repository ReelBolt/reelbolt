'use client';

import { Button } from '@mantine/core';
import { IconAlertCircle, IconAlertTriangle, IconChevronDown, IconCircleCheck, IconInfoCircle } from '@tabler/icons-react';
import { READINESS_WORD, readinessStateOf, resolveText, sortItems, type ReadinessItem, type ReadinessState } from './run-model';
import { useT } from '@/i18n';
import styles from './AppRunPage.module.css';

const ICONS: Record<ReadinessState, typeof IconAlertCircle> = {
  fix: IconAlertCircle,
  check: IconAlertTriangle,
  ready: IconCircleCheck,
  note: IconInfoCircle,
};

const STRIP_CLASS: Record<ReadinessState, string> = {
  fix: styles.stripFix!,
  check: styles.stripCheck!,
  ready: styles.stripReady!,
  note: styles.stripNote!,
};

const WORD_CLASS: Record<ReadinessState, string> = {
  fix: styles.wordFix!,
  check: styles.wordCheck!,
  ready: styles.wordReady!,
  note: styles.wordNote!,
};

interface ReadinessStripProps {
  items: readonly ReadinessItem[];
  /** The check could not be made: said plainly, never shown as Ready. */
  unavailable?: boolean;
  /** The first check has not come back yet: say so instead of showing Ready. */
  checking?: boolean;
  /** On a phone the strip is one summary line (the worst item); tapping it opens the whole list. */
  compact?: boolean;
  onFix: (item: ReadinessItem) => void;
}

/**
 * The readiness strip: what the pre-run check found, each state with an icon AND a word (never
 * colour alone). A labelled `role="status"` region, so a new blocker is announced politely. Colours
 * are fixed, checked pairings (4.5:1 or better) on the pane background.
 */
export function ReadinessStrip({ items, unavailable = false, checking = false, compact = false, onFix }: ReadinessStripProps) {
  const t = useT('apps.run.strip');
  const sorted = sortItems(items);
  const state = unavailable && sorted.length === 0 ? 'note' : readinessStateOf(sorted);
  // Only errors stand in the open; warnings and notes (some in builder words) wait behind a disclosure.
  const errors = sorted.filter((i) => i.level === 'fix');
  const folded = compact && sorted.length > 0;
  const shown = compact ? [] : errors;
  const rest = compact ? [] : sorted.filter((i) => i.level !== 'fix');

  const renderItem = (item: ReadinessItem) => {
    const Icon = ICONS[item.level];
    return (
      <div key={item.key} className={styles.stripItem}>
        <Icon size={18} aria-hidden="true" />
        <span className={`${styles.word} ${WORD_CLASS[item.level]}`}>{t.msg(READINESS_WORD[item.level])}:</span>
        <span>{resolveText(t, item.message)}</span>
        {item.fix && (
          <Button size="compact-sm" variant="default" onClick={() => onFix(item)} style={{ minHeight: compact ? 44 : 32 }}>
            {resolveText(t, item.fix.label)}
          </Button>
        )}
      </div>
    );
  };

  const Icon = ICONS[state];
  const worst = sorted[0];
  const WorstIcon = worst ? ICONS[worst.level] : Icon;
  return (
    <div
      role="status"
      aria-live="polite"
      aria-atomic="false"
      aria-relevant="additions text"
      aria-label={t('label')}
      className={`${styles.strip} ${STRIP_CLASS[state]}`}
      data-state={state}
      data-testid="readiness-strip"
    >
      {sorted.length === 0 && checking && !unavailable ? (
        <div className={styles.stripItem}>
          <span className={`${styles.word} ${WORD_CLASS.note}`}>{t('checking')}:</span>
          <span>{t('lookingOver')}</span>
        </div>
      ) : folded && worst ? (
        <details data-testid="readiness-details">
          <summary className={styles.stripSummary}>
            <WorstIcon size={18} aria-hidden="true" style={{ flexShrink: 0 }} />
            <span className={`${styles.word} ${WORD_CLASS[worst.level]}`}>{t.msg(READINESS_WORD[worst.level])}:</span>
            <span className={styles.stripSummaryText}>{resolveText(t, worst.message)}</span>
            <IconChevronDown size={16} aria-hidden="true" className={styles.stripChevron} />
          </summary>
          {worst.fix && (
            <Button size="compact-sm" variant="default" onClick={() => onFix(worst)} style={{ minHeight: 44 }}>
              {resolveText(t, worst.fix.label)}
            </Button>
          )}
          {sorted.slice(1).map(renderItem)}
        </details>
      ) : shown.length === 0 && rest.length === 0 ? (
        <div className={styles.stripItem}>
          <Icon size={18} aria-hidden="true" />
          <span className={`${styles.word} ${WORD_CLASS[state]}`}>{t.msg(READINESS_WORD[state])}:</span>
          <span>{unavailable ? t('unavailable') : t('allGood')}</span>
        </div>
      ) : (
        shown.map(renderItem)
      )}
      {rest.length > 0 && (
        <details>
          <summary className={styles.summary}>{t('more', { count: rest.length })}</summary>
          {rest.map(renderItem)}
        </details>
      )}
    </div>
  );
}
