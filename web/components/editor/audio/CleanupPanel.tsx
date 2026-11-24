'use client';

import React, { useEffect, useRef, useState } from 'react';
import Link from 'next/link';
import { Alert, Anchor, Badge, Button, Group, Loader, Modal, Stack, Text } from '@mantine/core';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline } from '@/lib/editing/types';
import { originalContentUrl } from '@/lib/editing/api/media';
import { cleanupSubject, planApply, planRevert, type CleanupRecord, type CleanupSubject } from '@/lib/editing/cleanup/plan';
import { NOISE_CHOICES, isAiTarget, targetOf, type CleanupChoice, type CleanupEstimate, type CleanupOutputKind, type CleanupJob } from '@/lib/editing/cleanup/types';
import { BILLING_SETTINGS_PATH } from '@/components/styles/style-options';
import { CheckedAudio } from '@/components/outputs/CheckedVideo';
import { rebaseAnchor } from '@/lib/editing/background/anchor';
import { cleanupRecordFor, entryForCleanup, landCleanupResult, settleCleanup } from '@/lib/editing/background/audio-cleanup';
import { useBackgroundResults } from '@/lib/editing/background/results-store';
import { useAuth } from '@/lib/hooks/use-auth';
import { useT } from '@/i18n';
import { useCreditAmount } from '@/components/billing/useCreditAmount';
import { useCleanupJob, defaultCleanupJobApi, type CleanupJobApi, type CleanupJobState } from './useCleanupJob';

export interface CleanupPanelProps {
  doc: EditTimeline;
  selection: readonly string[];
  projectId: string;
  readOnly: boolean;
  /** A batch of ops as ONE undo step. */
  /** `false` = the editor did not take it now (held or refused); nothing or void = it landed. */
  onApply: (ops: readonly EditOp[], label: string) => boolean | void;
  /** The phone sheet: 44 px touch targets. */
  phone?: boolean;
  api?: CleanupJobApi;
  /** Overrides the signed-in user for the consent memory (tests). */
  userId?: string;
  /** Overrides the active organisation for the consent memory (tests). */
  orgId?: string;
}

const AUDITION_SECONDS = 10;

/** One A/B player of the original and the cleaned sound over the same time range. */
function CleanupAudition({ beforeSrc, afterSrc, startSec, seconds, phone }: { beforeSrc: string; afterSrc: string; startSec: number; seconds: number; phone: boolean }) {
  const t = useT('editor-audio.cleanup');
  const [side, setSide] = useState<'before' | 'after'>('after');
  const [playing, setPlaying] = useState(false);
  const before = useRef<HTMLAudioElement | null>(null);
  const after = useRef<HTMLAudioElement | null>(null);
  const current = () => (side === 'before' ? before.current : after.current);
  const endSec = startSec + seconds;

  const play = () => {
    const el = current();
    if (!el) return;
    if (el.currentTime < startSec || el.currentTime >= endSec) el.currentTime = startSec;
    void el.play();
    setPlaying(true);
  };
  const pause = () => {
    before.current?.pause();
    after.current?.pause();
    setPlaying(false);
  };
  const choose = (next: 'before' | 'after') => {
    if (next === side) return;
    const from = current();
    const to = next === 'before' ? before.current : after.current;
    const at = from?.currentTime ?? startSec;
    from?.pause();
    if (to) {
      to.currentTime = at;
      if (playing) void to.play();
    }
    setSide(next);
  };
  const onTime = (el: HTMLAudioElement) => {
    if (el.currentTime >= endSec) {
      pause();
      el.currentTime = startSec;
    }
  };

  return (
    <Stack gap={6} data-testid="cleanup-audition">
      <Text size="sm" fw={600}>{t('audition.heading')}</Text>
      <CheckedAudio ref={before} src={beforeSrc} preload="auto" style={{ display: 'none' }} onTimeUpdate={(e) => side === 'before' && onTime(e.currentTarget)} data-testid="cleanup-audio-before" />
      <CheckedAudio ref={after} src={afterSrc} preload="auto" style={{ display: 'none' }} onTimeUpdate={(e) => side === 'after' && onTime(e.currentTarget)} data-testid="cleanup-audio-after" />
      <Group gap={6} wrap="wrap">
        <Button h={phone ? 44 : 34} miw={phone ? 44 : undefined} variant={side === 'before' ? 'filled' : 'default'} aria-pressed={side === 'before'} onClick={() => choose('before')} data-testid="cleanup-side-before">
          {t('audition.before')}
        </Button>
        <Button h={phone ? 44 : 34} miw={phone ? 44 : undefined} variant={side === 'after' ? 'filled' : 'default'} aria-pressed={side === 'after'} onClick={() => choose('after')} data-testid="cleanup-side-after">
          {t('audition.after')}
        </Button>
        <Button h={phone ? 44 : 34} variant="light" onClick={playing ? pause : play} data-testid="cleanup-play">
          {playing ? t('audition.pause') : t('audition.play')}
        </Button>
      </Group>
      <Text size="xs" c="dimmed">{t('audition.range', { seconds: Math.round(seconds) })}</Text>
    </Stack>
  );
}

/**
 * "Clean voice": a clip-level audio cleanup. Pick Basic, Studio or one noise to remove; the estimate
 * says what it costs (or Free); the first AI run asks to send the audio to an outside service; the
 * result is auditioned before and after, and Apply switches the clip to the cleaned file (one undo step).
 * "Use original" puts the source back. The existing one-switch track denoise is a different control.
 */
export function CleanupPanel(props: CleanupPanelProps) {
  const subject = cleanupSubject(props.doc, props.selection);
  return <CleanupPanelBody {...props} subject={subject} />;
}

function CleanupPanelBody({ doc, projectId, readOnly, onApply, phone = false, api = defaultCleanupJobApi, userId, orgId, subject }: CleanupPanelProps & { subject: CleanupSubject | null }) {
  const t = useT('editor-audio.cleanup');
  const { user, activeOrgId } = useAuth();
  const credits = useCreditAmount();
  // Consent is per user + organisation + project. No resolvable user or org means it is never
  // remembered (no shared "anonymous" key): the dialog shows every time.
  // The editor's background record: the run is registered here so it outlives this panel, follows the user's edits
  // and lands on every piece of the clip (the editor's watcher applies it). No open editor: the panel works alone.
  const { store: results } = useBackgroundResults();
  const [pinned, setPinned] = useState<CleanupSubject | null>(null);
  const live = useRef({ doc, clipId: null as string | null });
  useEffect(() => {
    live.current = { doc, clipId: (pinned ?? subject)?.clip.id ?? null };
  });
  const onStarted = (info: { executionId: string; outputKind: CleanupOutputKind }) => {
    const { doc: current, clipId } = live.current;
    const entry = results && clipId ? entryForCleanup({ doc: current, clipId, executionId: info.executionId, outputKind: info.outputKind }) : null;
    if (entry) results?.launch(entry);
  };
  const job = useCleanupJob({ projectId, userId: userId ?? user?.email ?? null, orgId: orgId ?? activeOrgId, outputKind: subject?.outputKind ?? 'Audio', readOnly, onStarted }, api);
  const [record, setRecord] = useState<CleanupRecord | null>(null);
  const [reverted, setReverted] = useState(false);
  // The clip this panel's job and applied record belong to. It is pinned when a run starts, so a
  // selection change mid-run (or the linked pair breaking on Apply) never remounts the body: that
  // would abort polling, hide a charged result, and lose "Use original".
  const { state, reset: resetJob } = job;
  const activeSubject = pinned ?? subject;

  // Back to idle on the clip now selected: drop the pinned job and its record too.
  const reset = () => {
    setPinned(null);
    setRecord(null);
    setReverted(false);
    resetJob();
  };

  const disabled = readOnly || !activeSubject || state.kind === 'estimating' || state.kind === 'running';
  const h = phone ? 44 : 34;
  const pick = (choice: CleanupChoice) => {
    if (!subject || disabled) return;
    setPinned(subject);
    setRecord(null);
    setReverted(false);
    void job.begin(choice, subject.mediaRef);
  };

  const choiceButton = (choice: CleanupChoice, label: string, badge: string, hint: string) => {
    const active = 'choice' in state && state.choice === choice && state.kind !== 'done' && state.kind !== 'error';
    return (
      <Stack key={choice} gap={2}>
        <Button h={h} miw={phone ? 44 : undefined} justify="space-between" fullWidth variant={active ? 'filled' : 'default'} disabled={disabled} aria-pressed={active} onClick={() => pick(choice)} data-testid={`cleanup-choice-${choice}`}
          rightSection={<Badge variant={isAiTarget(targetOf(choice)) ? 'light' : 'default'} color={isAiTarget(targetOf(choice)) ? 'violet' : 'gray'} tt="none" size="md" fz="xs">{badge}</Badge>}>
          {label}
        </Button>
        <Text size="xs" c="dimmed">{hint}</Text>
      </Stack>
    );
  };

  const estimateLine = (e: CleanupEstimate): string => {
    const free = !e.billingEnabled || !e.hosted || !(typeof e.credits === 'number' && e.credits > 0);
    if (free) return e.cached ? t('estimateFreeCached') : t('estimateFree');
    const n = e.credits as number;
    return n <= 1 ? t('estimateCreditsOne') : t('estimateCredits', { credits: credits(n) });
  };

  const nothingText = (s: Extract<CleanupJobState, { kind: 'done' }>): string => {
    switch (s.choice) {
      case 'hum': return t('nothingHum');
      case 'wind': return t('nothingWind');
      case 'hiss': return t('nothingHiss');
      case 'echo': return t('nothingEcho');
      default: return t('nothing');
    }
  };

  const doApply = (finished: CleanupJob) => {
    // Apply to the clip the job ran for, not whatever the selection points at now.
    const outputFileId = finished.outputFileId;
    if (!activeSubject || readOnly || !outputFileId) return;
    const plan = planApply(doc, activeSubject, outputFileId);
    const tracked = results ? results.getEntries().find((e) => e.id === `cleanup:${finished.executionId}`) : undefined;
    if (results && tracked && (tracked.state === 'running' || tracked.state === 'needsReview')) {
      const editor = { getState: () => ({ doc: live.current.doc, readOnly }), dispatchBatch: (ops: EditOp[], label: string) => {
          return onApply(ops, label) !== false;
        } };
      // The same path as the tray: every piece of the clip in one batch, and the store entry resolves.
      const outcome = rebaseAnchor(live.current.doc, tracked.anchor);
      const pieces = cleanupRecordFor({ ...live.current.doc }, { ...tracked, intent: { ...tracked.intent, outputFileId } }, outcome.kind === 'apply' ? outcome.clipIds : outcome.applicable);
      if (tracked.state === 'running') landCleanupResult({ results, editor, entry: tracked, job: finished });
      const landed = results.getEntries().find((e) => e.id === tracked.id);
      if (landed?.state === 'needsReview') settleCleanup({ results, editor, entry: landed, autoApply: false });
      if (results.getEntries().find((e) => e.id === tracked.id)?.state !== 'applied') return;
      setRecord(pieces ?? plan?.record ?? null);
      setReverted(false);
      return;
    }
    if (!plan) return;
    if (onApply(plan.ops, plan.label) === false) return;
    setRecord(plan.record);
    setReverted(false);
  };
  const doRevert = () => {
    if (!record || readOnly) return;
    const plan = planRevert(doc, record);
    if (plan) onApply(plan.ops, plan.label);
    setRecord(null);
    setReverted(true);
  };

  const fps = doc.settings.rate.den > 0 ? doc.settings.rate.num / doc.settings.rate.den : 30;
  const auditionStart = activeSubject ? Math.max(0, (activeSubject.clip.sourceIn ?? 0) / fps) : 0;
  const auditionLen = activeSubject ? Math.max(1, Math.min(AUDITION_SECONDS, (activeSubject.clip.recordOut - activeSubject.clip.recordIn) / fps)) : AUDITION_SECONDS;

  const refusalBlock = (s: Extract<CleanupJobState, { kind: 'error' }>) => {
    const r = s.refusal;
    let text = t('errorFailed');
    if (s.failureCode === 'timeout') text = t('errorTooLong');
    if (r) {
      switch (r.kind) {
        case 'credits': text = r.missing !== undefined ? t('errorRefusal.credits', { missing: credits(r.missing) }) : t('errorRefusal.creditsPlain'); break;
        case 'plan': text = t('errorRefusal.plan'); break;
        case 'egress': text = t('errorRefusal.egress'); break;
        case 'tooLong': text = t('errorRefusal.tooLong'); break;
        case 'unavailable': text = t('errorRefusal.unavailable'); break;
        case 'busy': text = t('errorRefusal.busy'); break;
        case 'notFound': text = t('errorRefusal.notFound'); break;
        default: text = t('errorRefusal.other');
      }
    }
    return (
      <Alert color="red" variant="light" role="alert" data-testid="cleanup-refusal" data-kind={r?.kind ?? 'failed'}>
        <Text size="sm">{text}</Text>
        {r?.kind === 'credits' && (
          <Anchor component={Link} href={BILLING_SETTINGS_PATH} size="sm" data-testid="cleanup-add-credits">{t('errorRefusal.addCredits')}</Anchor>
        )}
        {r?.kind === 'plan' && (
          <Anchor component={Link} href={`${BILLING_SETTINGS_PATH}#plans`} size="sm" data-testid="cleanup-see-plans">{t('errorRefusal.seePlans')}</Anchor>
        )}
        <Button mt={6} h={h} variant="default" onClick={reset} data-testid="cleanup-try-again">{t('tryAgain')}</Button>
      </Alert>
    );
  };

  return (
    <Stack gap="sm" data-testid="cleanup-panel">
      <Stack gap={2}>
        <Text size="sm" fw={600}>{t('title')}</Text>
        <Text size="xs" c="dimmed">{readOnly ? t('readOnly') : subject ? t('intro') : t('pickClip')}</Text>
      </Stack>

      <Stack gap="xs">
        <Text size="xs" fw={600}>{t('voiceHeading')}</Text>
        {choiceButton('basic', t('basic.label'), t('basic.badge'), t('basic.hint'))}
        {choiceButton('studio', t('studio.label'), t('studio.badge'), t('studio.hint'))}
      </Stack>

      <Stack gap="xs" role="group" aria-label={t('noiseHeading')} data-testid="cleanup-noise-picker">
        <Text size="xs" fw={600}>{t('noiseHeading')}</Text>
        <Text size="xs" c="dimmed">{t('noiseHint')}</Text>
        {NOISE_CHOICES.map((c) => choiceButton(c, t(`noise.${c}.label`), t(`noise.${c}.badge`), t(`noise.${c}.hint`)))}
      </Stack>

      {state.kind === 'estimating' && (
        <Group gap={6} data-testid="cleanup-estimating"><Loader size="xs" /><Text size="sm">{t('estimating')}</Text></Group>
      )}

      {(state.kind === 'confirm' || state.kind === 'consent') && (
        <Stack gap={6} data-testid="cleanup-confirm">
          <Text size="sm" data-testid="cleanup-estimate">{estimateLine(state.estimate)}</Text>
          {typeof state.estimate.seconds === 'number' && state.estimate.seconds > 0 && <Text size="xs" c="dimmed">{t('estimateSeconds', { seconds: Math.ceil(state.estimate.seconds) })}</Text>}
          {state.estimate.billingEnabled && typeof state.estimate.balanceCredits === 'number' && isAiTarget(targetOf(state.choice)) && <Text size="xs" c="dimmed">{t('balance', { balance: credits(state.estimate.balanceCredits) })}</Text>}
          <Group gap={6}>
            <Button h={h} onClick={job.confirm} disabled={readOnly} data-testid="cleanup-run">{t('clean')}</Button>
            <Button h={h} variant="default" onClick={reset} data-testid="cleanup-cancel">{t('cancel')}</Button>
          </Group>
        </Stack>
      )}

      <Modal opened={state.kind === 'consent'} onClose={job.declineConsent} title={t('consent.title')} centered data-testid="cleanup-consent">
        <Stack gap="sm">
          <Text size="sm">{t('consent.body')}</Text>
          <Text size="xs" c="dimmed">{t('consent.remember')}</Text>
          <Group gap={6} justify="flex-end">
            <Button h={44} variant="default" onClick={job.declineConsent} data-testid="cleanup-consent-decline">{t('consent.decline')}</Button>
            <Button h={44} onClick={job.grantConsent} data-testid="cleanup-consent-accept">{t('consent.accept')}</Button>
          </Group>
        </Stack>
      </Modal>

      {state.kind === 'running' && (
        <Group gap={6} role="status" data-testid="cleanup-running"><Loader size="xs" /><Text size="sm">{t('running')}</Text></Group>
      )}

      {state.kind === 'error' && refusalBlock(state)}

      {state.kind === 'done' && activeSubject && (
        state.job.applied === false || !state.job.outputFileId ? (
          <Text size="sm" data-testid="cleanup-nothing">{nothingText(state)}</Text>
        ) : (
          <Stack gap="sm" data-testid="cleanup-done">
            <Text size="sm">{t('done')}</Text>
            {(() => {
              // `activeSubject` is the clip as it was when the run started, so "Before" keeps the
              // original source even after Apply switches the clip to the cleaned file.
              const before = originalContentUrl(projectId, activeSubject.mediaRef);
              const afterUrl = originalContentUrl(projectId, `file:${state.job.outputFileId}`);
              return before && afterUrl ? <CleanupAudition beforeSrc={before} afterSrc={afterUrl} startSec={auditionStart} seconds={auditionLen} phone={phone} /> : null;
            })()}
            {record ? (
              <Stack gap={6}>
                <Text size="sm" data-testid="cleanup-applied">{t('applied')}</Text>
                <Button h={h} variant="default" disabled={readOnly} onClick={doRevert} data-testid="cleanup-use-original">{t('useOriginal')}</Button>
              </Stack>
            ) : (
              <Stack gap={6}>
                {reverted && <Text size="sm" data-testid="cleanup-reverted">{t('reverted')}</Text>}
                <Button h={h} disabled={readOnly} onClick={() => doApply(state.job)} data-testid="cleanup-apply">{reverted ? t('useCleanAgain') : t('apply')}</Button>
              </Stack>
            )}
          </Stack>
        )
      )}
    </Stack>
  );
}
