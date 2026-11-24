'use client';

import { useEffect, useMemo, useState } from 'react';
import { useOnboardingContext } from './onboarding-context';
import Link from 'next/link';
import { Button, CloseButton, Progress, Text, VisuallyHidden } from '@mantine/core';
import { useIsPhoneClass } from '@/lib/hooks/use-device-class';
import { IconCheck, IconCircle } from '@tabler/icons-react';
import { useBillingSummary } from '@/lib/hooks/use-billing';
import { checklistFallback, mergeChecklist, useOnboarding } from '@/lib/hooks/use-onboarding';
import { useLocale, useT } from '@/i18n';
import { msg, type Msg } from '@/i18n/msg';
import { CHECKLIST_ITEM_GUIDES, guideHrefFor } from '@/lib/onboarding/guide-links';
import type { ChecklistKey } from '@/lib/onboarding/state';
import type { DashboardOverview } from '@/lib/types/dashboard';
import { executionHref } from '@/lib/types/dashboard';
import type { Project } from '@/lib/types/project';
import classes from './onboarding.module.css';

const ALL_DONE_MS = 6000;

const LABELS: Record<ChecklistKey, Msg> = {
  create_project: msg('onboarding.checklist.items.create_project'),
  upload_file: msg('onboarding.checklist.items.upload_file'),
  run_video: msg('onboarding.checklist.items.run_video'),
  open_result: msg('onboarding.checklist.items.open_result'),
  try_assistant: msg('onboarding.checklist.items.try_assistant'),
  open_editor: msg('onboarding.checklist.items.open_editor'),
};

interface Props {
  projects: Project[] | undefined;
  overview: DashboardOverview | undefined;
  /** Opens the assistant (the "Try the assistant" row is a button, not a page). */
  onAskAssistant: () => void;
}

/**
 * The "Getting started" card on the dashboard home. It ticks itself from the server's view of the
 * account, falls back to what the dashboard already loaded when the onboarding API is unavailable,
 * can be dismissed, and goes away on its own (after a short "All done") when everything is ticked.
 * It never blocks anything.
 */
export function GettingStartedCard({ projects, overview, onAskAssistant }: Props) {
  const t = useT('onboarding.checklist');
  const locale = useLocale();
  const { settled, server, mirror, checklistDismissed, dismissChecklist, noteDone } = useOnboarding();
  const { data: billing, error: billingError } = useBillingSummary();
  const narrow = useIsPhoneClass();
  const { editorEnabled, cardUi, setCardUi } = useOnboardingContext();
  // The display state lives above the card (when there is a provider), so the page can move the card
  // between layouts without losing it.
  const [localUi, setLocalUi] = useState({ expanded: null as boolean | null, hidden: false });
  const ui = cardUi ?? localUi;
  const patchUi = setCardUi ?? ((p: Partial<typeof localUi>) => setLocalUi((s) => ({ ...s, ...p })));
  const { expanded, hidden } = ui;
  const setExpanded = (v: boolean) => patchUi({ expanded: v });
  const setHidden = (v: boolean) => patchUi({ hidden: v });

  const fallback = useMemo(() => checklistFallback(projects, overview), [projects, overview]);

  // A gated editor leaves the list (and the server's open_editor is ignored), so All done stays reachable.
  const items = useMemo(
    () => mergeChecklist(server, mirror, fallback).filter((i) => editorEnabled || i.key !== 'open_editor'),
    [server, mirror, fallback, editorEnabled],
  );
  // Only what the per-user answer (or this browser's own marks) shows is remembered and reported:
  // the workspace-wide fallback is for display while the API is unavailable.
  const trusted = useMemo(
    () => mergeChecklist(server, mirror).filter((i) => i.done && (editorEnabled || i.key !== 'open_editor')).map((i) => i.key),
    [server, mirror, editorEnabled],
  );
  const doneCount = items.filter((i) => i.done).length;
  const complete = doneCount === items.length;
  const ready = settled && projects !== undefined;

  // Ticks are sticky: remember (and report once) whatever has been seen done.
  const doneSignature = trusted.join(',');
  useEffect(() => {
    if (ready && trusted.length > 0) noteDone(trusted);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the signature stands for trusted
  }, [ready, doneSignature, noteDone]);

  // A card that finishes while someone watches says "All done" for a moment; one that was already
  // complete when it first loaded just stays out of the way.
  const [sawIncomplete, setSawIncomplete] = useState(false);
  if (ready && !complete && !sawIncomplete) setSawIncomplete(true);
  useEffect(() => {
    if (!ready || !complete || !sawIncomplete || checklistDismissed) return;
    const timer = window.setTimeout(() => setHidden(true), ALL_DONE_MS);
    return () => window.clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- setHidden only patches display state
  }, [ready, complete, sawIncomplete, checklistDismissed]);

  if (!ready || checklistDismissed || hidden || (complete && !sawIncomplete)) return null;

  const firstProject = projects?.[0];
  const passedRun = overview?.recentExecutions.find((e) => e.status === 'Passed');
  const hrefs: Record<Exclude<ChecklistKey, 'try_assistant'>, string> = {
    create_project: '/projects',
    upload_file: firstProject ? `/projects/${firstProject.id}?tab=files` : '/projects',
    run_video: firstProject ? `/projects/${firstProject.id}?tab=workflows` : '/projects',
    open_result: passedRun ? executionHref(passedRun) : '/projects',
    open_editor: '/editor',
  };
  const freeGrant = !billingError && (billing?.firstVideoGrant?.remaining ?? 0) > 0;
  const open = expanded ?? !narrow;
  const percent = Math.round((doneCount / items.length) * 100);

  if (complete) {
    return (
      <section className={classes.checklist} aria-label={t('title')} data-testid="getting-started" data-state="done">
        <div className={classes.checkHead}>
          <h2 className={classes.checkTitle}>{t('doneTitle')}</h2>
          <CloseButton aria-label={t('closeDone')} style={{ minWidth: 44, minHeight: 44 }} onClick={() => setHidden(true)} />
        </div>
        <Text size="sm" mt={4}>
          {t('doneBody')}
        </Text>
      </section>
    );
  }

  return (
    <section className={classes.checklist} aria-label={t('title')} data-testid="getting-started" data-state="open">
      <div className={classes.checkHead}>
        <div>
          <h2 className={classes.checkTitle}>{t('title')}</h2>
          <span className={classes.checkMeta} data-testid="getting-started-progress">
            {t('progress', { done: doneCount, total: items.length })}
          </span>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
          {narrow && (
            <Button
              variant="subtle"
              color="gray"
              style={{ minHeight: 44 }}
              aria-expanded={open}
              aria-controls="getting-started-list"
              onClick={() => setExpanded(!open)}
            >
              {open ? t('hideSteps') : t('showSteps')}
            </Button>
          )}
          <CloseButton aria-label={t('dismiss')} style={{ minWidth: 44, minHeight: 44 }} onClick={dismissChecklist} />
        </div>
      </div>
      <Progress value={percent} size="sm" mt="xs" aria-label={t('progress', { done: doneCount, total: items.length })} color="violet" />
      {open && (
        <ul id="getting-started-list" className={classes.checkList}>
          {items.map(({ key, done }) => (
            <li key={key} className={classes.checkRow} data-done={done ? 'true' : 'false'}>
              {key === 'try_assistant' ? (
                <button
                  type="button"
                  className={classes.checkLink}
                  style={{ background: 'none', border: 0, padding: 0, textAlign: 'start', cursor: 'var(--rb-cursor-pointer)' }}
                  onClick={onAskAssistant}
                >
                  <RowBody label={t.msg(LABELS[key])} done={done} />
                </button>
              ) : (
                <Link href={hrefs[key]} className={classes.checkLink}>
                  <RowBody label={t.msg(LABELS[key])} done={done} note={key === 'run_video' && freeGrant && !done ? t('freeFirstVideo') : undefined} />
                </Link>
              )}
              <a
                className={classes.how}
                href={guideHrefFor(CHECKLIST_ITEM_GUIDES[key], locale)}
                target="_blank"
                rel="noopener noreferrer"
                aria-label={t('howLabel', { label: t.msg(LABELS[key]) })}
              >
                {t('how')}
              </a>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function RowBody({ label, done, note }: { label: string; done: boolean; note?: string }) {
  const t = useT('onboarding.checklist');
  return (
    <>
      {done ? (
        <IconCheck size={18} aria-hidden="true" color="var(--rb-flow-done)" />
      ) : (
        <IconCircle size={18} aria-hidden="true" color="var(--rb-text-dim)" />
      )}
      <span className={done ? classes.checkDone : undefined}>
        {label}
        {done && <VisuallyHidden component="span">{t('doneMark')}</VisuallyHidden>}
        {note ? <span className={classes.checkMeta}>{` - ${note}`}</span> : null}
      </span>
    </>
  );
}
