'use client';

import { Button, Collapse, UnstyledButton, VisuallyHidden } from '@mantine/core';
import { IconArrowRight, IconCheck, IconChevronDown, IconGitCompare } from '@tabler/icons-react';
import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { useT } from '@/i18n';
import type { WorkflowEditDiff } from '../../lib/types/workflow-assistant';
import {
  countByKind,
  describeDiff,
  DIFF_KIND_ORDER,
  type DiffKind,
  type DiffRow,
  type NamedStep,
} from './diff-model';
import { DIFF_KIND_ICON, DiffTag } from './DiffTag';
import diffClasses from './diff.module.css';
import classes from './WorkflowDiffPreview.module.css';

const COUNT_KEY: Record<DiffKind, string> = {
  added: 'assistant.diff.countAdded',
  removed: 'assistant.diff.countRemoved',
  changed: 'assistant.diff.countChanged',
};

interface WorkflowDiffPreviewProps {
  diff: WorkflowEditDiff;
  /** The steps currently on the canvas, used to name removed/changed steps. */
  currentSteps?: readonly NamedStep[];
  /** Which workflow this is for, when it is not the one on screen ("Creates a new workflow “X”"). */
  targetNote?: string;
  onApply: () => Promise<void>;
  onDiscard: () => void;
}

function DiffRowItem({ row }: { row: DiffRow }) {
  const t = useT();
  const Icon = DIFF_KIND_ICON[row.kind];
  return (
    <li className={`${diffClasses.kind} ${classes.row}`} data-kind={row.kind}>
      <span className={classes.gutter} aria-hidden>
        <Icon size={14} stroke={2.5} />
      </span>
      <span className={classes.order}>
        <VisuallyHidden>{t('assistant.diff.step')} </VisuallyHidden>
        {row.stepOrder}
      </span>
      <div className={classes.body}>
        <div className={classes.name}>
          {row.previousName && (
            <>
              <span className={classes.previous}>{row.previousName}</span>
              <IconArrowRight size={14} className={classes.arrow} aria-label={t('assistant.diff.becomes')} role="img" />
            </>
          )}
          <span className={row.kind === 'removed' ? classes.removedName : undefined}>{row.name}</span>
        </div>
        <div className={classes.meta}>
          {row.stepTypeLabel}
          {row.detail && <> · {row.detail}</>}
        </div>
      </div>
    </li>
  );
}

/**
 * The review surface for an assistant-proposed workflow edit. Nothing here writes: Apply hands
 * control back to the page, which submits the proposal's own `proposedSteps` with its token.
 */
export function WorkflowDiffPreview({ diff, currentSteps, targetNote, onApply, onDiscard }: WorkflowDiffPreviewProps) {
  const t = useT();
  const [isApplying, setIsApplying] = useState(false);
  const [expanded, setExpanded] = useState(true);
  const listId = useId();

  const rows = useMemo(() => describeDiff(diff, currentSteps, t), [diff, currentSteps, t]);
  const counts = countByKind(rows);
  const groups = DIFF_KIND_ORDER.map((kind) => ({ kind, rows: rows.filter((r) => r.kind === kind) })).filter(
    (g) => g.rows.length > 0,
  );
  const total = rows.length;

  // Two clicks in the same tick both see `isApplying === false`; the ref is the real guard.
  const applyingRef = useRef(false);
  const handleApply = async () => {
    if (applyingRef.current) return;
    applyingRef.current = true;
    setIsApplying(true);
    try {
      await onApply();
    } finally {
      applyingRef.current = false;
      setIsApplying(false);
    }
  };

  const countSummary = DIFF_KIND_ORDER.filter((k) => counts[k] > 0)
    .map((k) => t(COUNT_KEY[k], { count: counts[k] }))
    .join(', ');

  // A live region only announces text that CHANGES after it is in the DOM, so the region mounts
  // empty and the summary is written a tick later — once per proposal (keyed on its token).
  const [announcement, setAnnouncement] = useState('');
  const announcementText = t('assistant.diff.announcement', { total, summary: countSummary });
  useEffect(() => {
    const timer = setTimeout(() => setAnnouncement(announcementText), 150);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- announce once per proposal, not per render
  }, [diff.confirmationToken]);

  return (
    <section className={classes.tray} aria-label={t('assistant.diff.proposedChanges')}>
      <VisuallyHidden aria-live="polite" aria-atomic="true">
        {announcement}
      </VisuallyHidden>

      <UnstyledButton
        className={classes.header}
        onClick={() => setExpanded((v) => !v)}
        aria-expanded={expanded}
        aria-controls={listId}
      >
        <span className={classes.headerIcon} aria-hidden>
          <IconGitCompare size={16} />
        </span>
        <span className={classes.headerText}>
          <span className={classes.title}>{t('assistant.diff.proposedChanges')}</span>
          <span className={classes.subtitle}>
            {targetNote ? t('assistant.diff.subtitleTarget', { total, target: targetNote }) : t('assistant.diff.subtitle', { total })}
          </span>
        </span>
        <span className={classes.counts} aria-hidden>
          {DIFF_KIND_ORDER.filter((k) => counts[k] > 0).map((k) => (
            <DiffTag key={k} kind={k} label={String(counts[k])} />
          ))}
        </span>
        <IconChevronDown size={16} className={classes.chevron} data-expanded={expanded || undefined} aria-hidden />
        <VisuallyHidden>{expanded ? t('assistant.diff.hideDetails') : t('assistant.diff.showDetails')}</VisuallyHidden>
      </UnstyledButton>

      <Collapse expanded={expanded} id={listId} transitionDuration={150}>
        <div className={classes.list} tabIndex={0} aria-label={t('assistant.diff.changedSteps')}>
          {total === 0 && <p className={classes.emptyNote}>{t('assistant.diff.empty')}</p>}
          {groups.map(({ kind, rows: groupRows }) => (
            <DiffGroup key={kind} kind={kind} rows={groupRows} />
          ))}
        </div>
      </Collapse>

      <div className={classes.actions}>
        <Button data-testid="discard-button" variant="default" onClick={onDiscard} disabled={isApplying}>
          {t('assistant.diff.discard')}
        </Button>
        <Button
          data-testid="apply-button"
          color="violet"
          onClick={handleApply}
          loading={isApplying}
          disabled={isApplying}
          leftSection={<IconCheck size={16} />}
        >
          {t('assistant.diff.apply')}
        </Button>
      </div>
    </section>
  );
}

function DiffGroup({ kind, rows }: { kind: DiffKind; rows: DiffRow[] }) {
  const t = useT();
  const headingId = useId();
  return (
    <div className={classes.group}>
      <div className={classes.groupHeading} id={headingId}>
        <DiffTag kind={kind} />
        <span className={classes.groupCount}>
          {t('assistant.diff.groupCount', { count: rows.length })}
        </span>
      </div>
      <ul className={classes.rows} aria-labelledby={headingId}>
        {rows.map((row) => (
          <DiffRowItem key={row.key} row={row} />
        ))}
      </ul>
    </div>
  );
}
