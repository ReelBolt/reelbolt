'use client';

import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import Link from 'next/link';
import { Alert, Anchor, Box, Button, Group, List, Stack, Text } from '@mantine/core';
import { IconInfoCircle } from '@tabler/icons-react';
import { notifications } from '@mantine/notifications';
import { estimateEditTimelineRender, renderEditTimeline, type EditTimelineRenderSummary, type RenderEditTimelineRequest } from '@/lib/editing/sync/api';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { BILLING_SETTINGS_PATH } from '@/components/styles/style-options';
import type { Item } from '@/lib/editing/types';
import { clipExportRange } from '@/lib/editing/stills/stills';
import { stopExecution } from '@/lib/api/executions';
import { deleteOutputTake, getOutputDownloadUrl, getOutputVideoUrl } from '@/lib/api/outputs';
import { useExecutionStream } from '@/lib/hooks/use-execution-stream';
import { useOutputHeightCap } from '@/lib/hooks/use-billing';
import { RenderSettings } from './RenderSettings';
import { ExportPanel } from './ExportPanel';
import { RenderQueue, type RenderProgress } from './RenderQueue';
import { FilmCard, filesPath } from './FilmCard';
import { ReviewLinksBody, ShareForReview } from '../review/ShareForReview';
import { creditsMissing, formatEstimateMsg, formatSizeMsg, latestFinished, missingMsg, type RenderEstimate } from './deliver-model';
import { describeRenderRefusalMsg, type RenderRefusalMsg } from './render-refusal';
import { HandOffCard } from './HandOffCard';
import type { CostState } from './CostStatement';
import { readSentRenders, rememberSentRender, type RenderFormSettings, type SentRender } from './render-presets';
import type { Rational } from '@/lib/editing/types';
import classes from './Deliver.module.css';
import { itemNamesOf, type NamedItemsSource } from './render-notes';
import { CaptionDelivery } from './CaptionDelivery';
import type { CaptionDelivery as CaptionChoice, CaptionTrackInfo } from '@/lib/editing/subtitles/delivery';
import { EditorPanel } from '../ui/EditorPanel';
import { useDeviceClass } from '@/lib/editing/use-device-class';
import { ApiError } from '@/lib/api/client';
import { useForegroundEpoch } from './deliver-phone';
import { useT } from '@/i18n';
import type { Msg } from '@/i18n/msg';

/** The document shape the caption check reads (the server writes SRT/WebVTT from enabled caption items with text). */
export interface CaptionSource {
  readonly tracks: readonly { readonly items: readonly { readonly type?: string; readonly text?: string; readonly enabled?: boolean; readonly recordOut?: number }[] }[];
}

/** The last frame anything on the timeline reaches (0 for an empty timeline), so "whole timeline" can be said in timecode. */
export function programEndFrame(doc: CaptionSource): number {
  let end = 0;
  for (const track of doc.tracks) {
    for (const item of track.items) {
      if (typeof item.recordOut === 'number' && Number.isFinite(item.recordOut) && item.recordOut > end) end = item.recordOut;
    }
  }
  return end;
}

/** True when no track holds anything: there is nothing to render yet. */
export const timelineIsEmpty = (doc: CaptionSource): boolean => doc.tracks.every((track) => track.items.length === 0);

/** How many caption items an SRT/WebVTT export would contain (enabled, non-blank text), as `TimelineInterchange.Captions` counts. */
export function countCaptions(doc: CaptionSource): number {
  let n = 0;
  for (const track of doc.tracks) {
    for (const item of track.items) {
      if (item.type === 'caption' && item.enabled !== false && (item.text ?? '').trim() !== '') n++;
    }
  }
  return n;
}

export interface DeliverWorkspaceProps {
  projectId: string;
  timelineId: string;
  revision: number;
  dirty: boolean;
  readOnly: boolean;
  inOut: { inFrame: number | null; outFrame: number | null };
  renders: readonly EditTimelineRenderSummary[] | undefined;
  rendersLoading: boolean;
  /** Re-fetch the queue (after a render was started or stopped). */
  refreshRenders: () => void;
  /** P6: the single selected clip, for "export this clip" (sets the range to its span). */
  selectedClip?: Item | null;
  onSetInOut?: (range: { inFrame: number; outFrame: number }) => void;
  /** The timeline's own size, which the `Master` preset renders at; checked against the plan's cap. */
  timelineSize?: { width: number; height: number; rate?: Rational } | null;
  /** RD-5: the document on screen, so a render's skipped items are named as the user named them. */
  doc?: (NamedItemsSource & CaptionSource) | null;
  /**
   * R06.6: saves the editor's pending edits and says how it went. Render calls it first, so the film always
   * contains what is on screen; `reason` is a plain sentence. Without it the panel renders what is saved.
   */
  flushSave?: () => Promise<FlushResult>;
  /** R09.7: the tracks that hold captions, and what happens when the delivery choice changes. Absent = no choice offered. */
  captionDelivery?: { tracks: readonly CaptionTrackInfo[] };
  /** T-003: on the Deliver page, the shared frame's zones carry the settings and the result columns. */
  zoneTargets?: DeliverZoneTargets | null;
  /** Wave 5: the way out of an empty timeline (opens the page where clips are placed). Absent = no action offered. */
  onOpenEdit?: () => void;
  /**
   * The Simple shell's Export place: one column with the film, where it is going, what it costs, Render and the
   * queue. Review links, timeline files and the hand-off package stay in the full editor, behind this button.
   */
  simple?: { onMoreOptions: () => void };
}

/** The Deliver page's two frame zones: settings (420) and the result column that fills the rest. */
export interface DeliverZoneTargets {
  controls: HTMLElement;
  result: HTMLElement;
}

/** How a save-before-render went: the revision that is now stored, or why it could not be stored. */
export type FlushResult = { ok: true; revision: number } | { ok: false; reason: string };

/** The queue rows a person still sees: a finished render whose film was deleted is gone, and a removed failure stays removed. */
export function visibleRenders(renders: readonly EditTimelineRenderSummary[], dismissed: ReadonlySet<string>): EditTimelineRenderSummary[] {
  return renders.filter((r) => !dismissed.has(r.executionId) && !(r.status === 'Passed' && !r.hasOutput));
}

const RENDER_CAPTIONS = { burn: 'burnIn', sidecar: 'sidecar', both: 'both' } as const;

/** One block of the result column: a bordered panel with the page's one header language. */
function DeliverSection({ title, description, testId, children }: { title: string; description?: string; testId: string; children: React.ReactNode }) {
  return (
    <div className={classes.section}>
      <EditorPanel title={title} description={description} scroll={false} testId={testId}>
        {children}
      </EditorPanel>
    </div>
  );
}

const dismissedKey = (timelineId: string): string => `reelbolt:deliver:removed:${timelineId}`;

/**
 * The Deliver workspace (P4): render settings over the closed preset list, the render queue over
 * this timeline's `TimelineRender` executions, live progress from the `step.progress` stream of the
 * render being followed, and stop/download per row. Starting a render is `POST …/render`; the queue
 * is `GET …/renders`; nothing here knows a storage key.
 */
export function DeliverWorkspace({
  projectId, timelineId, revision, dirty, readOnly, inOut, renders, rendersLoading, refreshRenders, selectedClip = null, onSetInOut, timelineSize = null, doc = null, flushSave, captionDelivery, zoneTargets = null, onOpenEdit, simple,
}: DeliverWorkspaceProps) {
  const t = useT('editor.deliver.deliverWorkspace');
  const phone = useDeviceClass() === 'phone';
  // Phone: the Render button and its price live in the sheet's sticky footer (a portal target).
  const [footerEl, setFooterEl] = useState<HTMLElement | null>(null);
  // Phone tabs suspend in the background: the render stream is reopened when the page comes back.
  const foregroundEpoch = useForegroundEpoch();
  const [busy, setBusy] = useState(false);
  // R09.7: where the captions go in the next render.
  const [captionChoice, setCaptionChoice] = useState<CaptionChoice>('burn');
  // The last refused start, in plain words, shown in the panel until the next attempt.
  const [refusal, setRefusal] = useState<RenderRefusalMsg | null>(null);
  // R06.6: why the save before a render failed; Render stays off while the edits are still unsaved.
  const [saveFailure, setSaveFailure] = useState<string | null>(null);
  const maxOutputHeight = useOutputHeightCap();
  const masterShortSide = timelineSize ? Math.min(timelineSize.width, timelineSize.height) : null;
  const itemNames = useMemo(() => (doc ? itemNamesOf(doc) : undefined), [doc]);
  // WS-169: SRT/WebVTT of a timeline without captions would be an empty file; say so instead of offering it.
  const captionCount = useMemo(() => (doc ? countCaptions(doc) : null), [doc]);
  // Bumped by "Export only <clip>" so the range switch follows the marks it sets (WS-160).
  const [rangeRequest, setRangeRequest] = useState(0);
  const [following, setFollowing] = useState<{ executionId: string; workflowId: string } | null>(null);
  // R09.10: the clip an "Export only <clip>" render is for, said while it renders.
  const [clipExporting, setClipExporting] = useState<{ executionId: string; name: string } | null>(null);
  const requestRef = useRef<RenderEditTimelineRequest | null>(null);
  const settingsRef = useRef<RenderFormSettings | null>(null);
  const [requestKey, setRequestKey] = useState('');
  // The answer of the estimate endpoint, tagged with the settings it is for: a figure, a 402 in plain words, or a failure.
  const [answer, setAnswer] = useState<{ key: string; estimate: RenderEstimate | null; refusal: RenderRefusalMsg | null; failed: boolean } | null>(null);
  // Bumped by "Try again" under a failed estimate.
  const [estimateRetry, setEstimateRetry] = useState(0);
  // What this device started each render with, for "Render again" (the queue row carries only the format).
  const [sent, setSent] = useState<ReadonlyMap<string, SentRender>>(() => readSentRenders());
  const [prefill, setPrefill] = useState<{ token: number; settings: RenderFormSettings; useRange: boolean; fileName: string; from: string } | null>(null);
  const [focusPresets, setFocusPresets] = useState(0);
  const [dismissed, setDismissed] = useState<ReadonlySet<string>>(() => {
    try {
      const raw = typeof window === 'undefined' ? null : window.localStorage.getItem(dismissedKey(timelineId));
      return new Set(raw ? (JSON.parse(raw) as string[]) : []);
    } catch {
      return new Set();
    }
  });

  const onRequestChange = useCallback((request: RenderEditTimelineRequest, settings?: RenderFormSettings) => {
    requestRef.current = request;
    if (settings) settingsRef.current = settings;
    setRequestKey(JSON.stringify(request));
  }, []);

  // R06.7: the credit estimate follows the settings, debounced; billing off (or no answer) shows nothing.
  const estimateKey = readOnly || requestKey === '' ? '' : JSON.stringify([requestKey, revision, captionCount ? captionChoice : null, estimateRetry]);
  useEffect(() => {
    if (estimateKey === '') return;
    let live = true;
    const timer = window.setTimeout(() => {
      estimateEditTimelineRender(projectId, timelineId, { ...(JSON.parse(requestKey) as RenderEditTimelineRequest), ...(captionCount ? { captions: RENDER_CAPTIONS[captionChoice] } : {}) })
        .then((estimate) => {
          if (live) setAnswer({ key: estimateKey, estimate, refusal: null, failed: false });
        })
        .catch((error: unknown) => {
          if (!live) return;
          const refused = error instanceof ApiError && error.status === 402 ? describeRenderRefusalMsg(error) : null;
          setAnswer({ key: estimateKey, estimate: null, refusal: refused, failed: refused === null });
        });
    }, 400);
    return () => {
      live = false;
      window.clearTimeout(timer);
    };
    // The key holds every input of the request (settings, revision, captions, retry).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, timelineId, estimateKey]);

  // The phone keeps the last answer on screen while the next one loads (its footer is one short line); the page
  // reads only an answer that is for the settings on screen, so nothing about other settings blocks or prices them.
  const estimate = (phone || answer?.key === estimateKey ? answer?.estimate : null) ?? null;
  const estimateProblem = answer?.refusal?.message ?? null;
  const missing = creditsMissing(estimate);
  const estimateMsg = formatEstimateMsg(estimate) ?? estimateProblem;
  const showMsg = (m: Msg | string | null): string | null => (m === null || typeof m === 'string' ? m : t.msg(m));
  const estimateLine = showMsg(estimateMsg);
  const planReason = estimate && !estimate.affordable && missing === null && estimate.unavailableReason ? estimate.unavailableReason : null;

  // Wave 5: the one cost statement. While the answer on hand is for other settings it says so instead of showing a stale figure.
  const cost: CostState =
    estimateKey === '' ? { kind: 'idle' }
    : !answer || answer.key !== estimateKey ? { kind: 'loading' }
    : answer.failed ? { kind: 'error', onRetry: () => setEstimateRetry((n) => n + 1) }
    : answer.refusal ? { kind: 'refused', message: showMsg(answer.refusal.message) ?? '', link: answer.refusal.planLink ? 'plans' : answer.refusal.missingCredits !== undefined ? 'credits' : null }
    : answer.estimate && answer.estimate.billingEnabled ? { kind: 'ready', estimate: answer.estimate }
    : { kind: 'billingOff', estimate: answer.estimate };

  // Follow the newest active render by default, or the one just started.
  const active = useMemo(() => (renders ?? []).find((r) => r.status === 'Queued' || r.status === 'Running') ?? null, [renders]);
  const followed = following ?? (active ? { executionId: active.executionId, workflowId: active.workflowId } : null);

  const stream = useExecutionStream({
    projectId,
    workflowId: followed?.workflowId ?? '',
    executionId: followed?.executionId ?? '',
    enabled: followed !== null,
    reconnectKey: phone ? foregroundEpoch : 0,
  });

  const progress: RenderProgress | null = useMemo(() => {
    if (!followed) return null;
    const last = stream.events.find((e) => e.type === 'step.progress' && e.executionId === followed.executionId);
    if (!last) return null;
    const payload = last.payload;
    const stage = typeof payload.stage === 'string' ? payload.stage : typeof payload.message === 'string' ? payload.message : 'Working…';
    const raw = payload.percent ?? payload.progressPercent ?? payload.progress;
    const percent = typeof raw === 'number' && Number.isFinite(raw) ? Math.max(0, Math.min(100, raw)) : null;
    return { executionId: followed.executionId, stage, percent };
  }, [stream.events, followed]);

  // A terminal event on the followed render refreshes the queue so the row flips to its final state.
  const terminalId = stream.events.find((e) => e.type === 'execution.completed' || e.type === 'execution.failed')?.id ?? null;
  useEffect(() => {
    if (terminalId !== null) refreshRenders();
  }, [terminalId, refreshRenders]);

  const saveFirst = useCallback(async (): Promise<FlushResult> => {
    if (!flushSave) return { ok: true, revision };
    try {
      return await flushSave();
    } catch (error) {
      return { ok: false, reason: humanizeErrorT(t, error, t('yourLatestEditsCould')) };
    }
  }, [flushSave, revision, t]);

  /** Saves, then starts the render pinned to the revision that was just stored. Returns the new execution id, or null. */
  const startRender = async (request: RenderEditTimelineRequest, settings?: RenderFormSettings): Promise<string | null> => {
    setBusy(true);
    setRefusal(null);
    setSaveFailure(null);
    try {
      const saved = await saveFirst();
      if (!saved.ok) {
        setSaveFailure(saved.reason);
        return null;
      }
      const response = await renderEditTimeline(projectId, timelineId, { ...request, ...(captionCount ? { captions: RENDER_CAPTIONS[captionChoice] } : {}), revision: saved.revision });
      setFollowing({ executionId: response.executionId, workflowId: response.workflowId });
      const started = settings ?? settingsRef.current;
      if (started) {
        const record: SentRender = { settings: { ...started, preset: request.preset ?? started.preset }, range: request.range ?? null, fileName: request.outputFileName ?? '', captions: captionCount ? captionChoice : null };
        setSent((known) => new Map([...known, ...rememberSentRender(response.executionId, record)]));
      }
      notifications.show({ color: 'blue', title: t('renderStarted'), message: t('isOnItsWay', { outputFileName: response.outputFileName }) });
      refreshRenders();
      return response.executionId;
    } catch (error) {
      const refused = describeRenderRefusalMsg(error);
      setRefusal(refused);
      notifications.show({ color: 'red', title: t('renderNotStarted'), message: showMsg(refused.message) ?? '' });
      return null;
    } finally {
      setBusy(false);
    }
  };

  const onRender = async (request: RenderEditTimelineRequest, settings?: RenderFormSettings) => {
    setClipExporting(null);
    setPrefill((current) => (current ? { ...current, from: '' } : current));
    await startRender(request, settings);
  };

  /** R09.10: one action from a selected clip: the range becomes the clip, then the (saved) render starts. */
  const exportClip = async () => {
    if (!selectedClip || !onSetInOut || !requestRef.current) return;
    const range = clipExportRange(selectedClip);
    onSetInOut(range);
    setRangeRequest((n) => n + 1);
    const id = await startRender({ ...requestRef.current, range });
    if (id) setClipExporting({ executionId: id, name: selectedClip.name });
  };

  const onRetryRender = (render: EditTimelineRenderSummary) => {
    const request = requestRef.current;
    if (!request) return;
    void onRender({ ...request, preset: (render.preset as RenderEditTimelineRequest['preset']) ?? request.preset });
  };

  /**
   * "Render again": puts the settings that render was started with back into the form (format, quality, encoder,
   * loudness, captions, range and file name), so the cost of exactly that render is on screen before anything
   * starts. Only offered for renders this device remembers; a row from elsewhere carries too little to rebuild.
   */
  const onRenderAgain = (render: EditTimelineRenderSummary) => {
    const record = sent.get(render.executionId);
    if (!record) return;
    if (record.range && onSetInOut) onSetInOut(record.range);
    if (record.captions) setCaptionChoice(record.captions);
    setPrefill((current) => ({ token: (current?.token ?? 0) + 1, settings: record.settings, useRange: record.range !== null && !!onSetInOut, fileName: record.fileName, from: render.outputFileName }));
  };

  const onRemoveRender = (render: EditTimelineRenderSummary) => {
    const next = new Set(dismissed).add(render.executionId);
    setDismissed(next);
    try {
      window.localStorage.setItem(dismissedKey(timelineId), JSON.stringify([...next]));
    } catch { /* the row stays hidden for this visit */ }
  };

  const onCancel = async (render: EditTimelineRenderSummary) => {
    try {
      await stopExecution(projectId, render.workflowId, render.executionId);
      notifications.show({ color: 'orange', title: t('stopping'), message: t('willStopShortly', { outputFileName: render.outputFileName }) });
      refreshRenders();
    } catch (error) {
      notifications.show({ color: 'red', title: t('couldNotStop'), message: humanizeErrorT(t, error, t('theRenderCouldNot')) });
    }
  };

  const onDeleteFilm = async (render: EditTimelineRenderSummary): Promise<boolean> => {
    if (!render.stepResultId) return false;
    try {
      await deleteOutputTake(projectId, render.stepResultId);
      notifications.show({ color: 'blue', title: t('filmDeleted'), message: t('wasRemovedFromYour', { outputFileName: render.outputFileName }) });
      refreshRenders();
      return true;
    } catch (error) {
      notifications.show({ color: 'red', title: t('couldNotDeleteThe'), message: humanizeErrorT(t, error, t('theFilmCouldNot')) });
      return false;
    }
  };

  const onCopyLink = async (render: EditTimelineRenderSummary) => {
    if (!render.outputFileId) return;
    // There is no public share link for a render: the link is the in-app address of the file, which opens for people who can open the project.
    const link = `${window.location.origin}${filesPath(projectId, render.outputFileId)}`;
    try {
      await navigator.clipboard.writeText(link);
      notifications.show({ color: 'blue', title: t('linkCopied'), message: t('itOpensThisFilm') });
    } catch {
      notifications.show({ color: 'red', title: t('couldNotCopyThe'), message: t('copyItFromThe') });
    }
  };

  const shown = useMemo(() => visibleRenders(renders ?? [], dismissed), [renders, dismissed]);
  const latest = useMemo(() => latestFinished(shown), [shown]);
  // Render stays off only while the edits are unsaved AND the last save attempt failed.
  const blockedReason = saveFailure !== null && dirty ? t('saveFirst', { saveFailure }) : planReason;
  const exportingNow = clipExporting && (renders ?? []).some((r) => r.executionId === clipExporting.executionId && (r.status === 'Queued' || r.status === 'Running'));
  const hasCaptionChoice = !!captionDelivery && captionDelivery.tracks.length > 0;
  const timelineEmpty = doc ? timelineIsEmpty(doc) : false;
  const programFrames = doc ? programEndFrame(doc) : null;
  // Where the captions of the next render go, for the statement; nothing to say when the timeline has no caption with words.
  const captionsSummary = captionCount ? t(`captionsSummary.${captionChoice}`) : null;

  const filmCard = (pageReadOnly: boolean) =>
    latest && latest.stepResultId ? (
      <FilmCard
        projectId={projectId}
        render={latest}
        revision={revision}
        playUrl={getOutputVideoUrl(projectId, latest.stepResultId)}
        downloadUrl={getOutputDownloadUrl(projectId, latest.stepResultId, latest.outputFileName)}
        onDelete={onDeleteFilm}
        onCopyLink={(r) => void onCopyLink(r)}
        share={phone}
        readOnly={pageReadOnly}
      />
    ) : null;
  const film = phone ? filmCard(false) : null;
  const filmEmpty = (
    <Stack gap={4} p="md" style={{ border: '1px dashed var(--mantine-color-default-border)', borderRadius: 8 }} data-testid="film-empty">
      <Text size="sm" fw={600}>
        {t('yourFilmWillShow')}
      </Text>
      <Text size="sm" c="dimmed">
        {t('pickAFormatAnd')}
      </Text>
    </Stack>
  );
  const clipBlock = (
    <>
      {selectedClip && onSetInOut && (
        <Group gap="xs" data-testid="deliver-clip-export">
          <Button variant="light" onClick={() => void exportClip()} disabled={readOnly || busy} style={{ minHeight: 44 }} data-testid="deliver-use-clip-range">
            {t('exportOnly', { name: selectedClip.name })}
          </Button>
        </Group>
      )}
      {exportingNow && clipExporting && (
        <Text size="sm" fw={500} data-testid="deliver-clip-rendering">
          {t('rendering', { name: clipExporting.name })}
        </Text>
      )}
    </>
  );
  const captionChoiceBlock = hasCaptionChoice ? <CaptionDelivery projectId={projectId} timelineId={timelineId} value={captionChoice} readOnly={readOnly} onChoose={setCaptionChoice} /> : null;
  const settingsBlock = (footer: HTMLElement | null, more: React.ReactNode, page: boolean) => (
    <RenderSettings
      inOut={inOut}
      revision={revision}
      readOnly={readOnly}
      busy={busy}
      onRender={(request, settings) => void onRender(request, settings)}
      projectId={projectId}
      maxOutputHeight={maxOutputHeight}
      masterShortSide={masterShortSide}
      timelineSize={timelineSize}
      rangeRequest={rangeRequest}
      onRequestChange={onRequestChange}
      blockedReason={blockedReason}
      blockedAction={saveFailure !== null && dirty ? { label: t('trySavingAgain'), onClick: () => void (async () => { const r = await saveFirst(); setSaveFailure(r.ok ? null : r.reason); })() } : null}
      estimateLine={estimateLine}
      sizeLine={showMsg(formatSizeMsg(estimate))}
      needMoreLine={showMsg(missingMsg(missing))}
      footerSlot={footer}
      moreSlot={more}
      cost={cost}
      captionsSlot={page ? captionChoiceBlock : null}
      captionsSummary={captionsSummary}
      programFrames={programFrames}
      timelineEmpty={timelineEmpty}
      onOpenEdit={onOpenEdit}
      prefill={prefill}
      focusPresetsRequest={focusPresets}
    />
  );
  const refusalBlock = (
    <>
      {refusal && (
        <Alert color="red" variant="light" title={t('renderNotStarted2')} withCloseButton onClose={() => setRefusal(null)} data-testid="render-refusal">
          <Text size="sm">{showMsg(refusal.message)}</Text>
          {refusal.missingCredits !== undefined && (
            <Anchor component={Link} href={BILLING_SETTINGS_PATH} size="sm" data-testid="render-refusal-add-credits">
              {t('addCredits')}
            </Anchor>
          )}
          {refusal.planLink && (
            <Anchor component={Link} href={`${BILLING_SETTINGS_PATH}#plans`} size="sm" data-testid="render-refusal-see-plans">
              {t('seePlans')}
            </Anchor>
          )}
          {refusal.details.length > 0 && (
            <List size="sm" mt={4}>
              {refusal.details.map((detail) => (
                <List.Item key={detail}>{detail}</List.Item>
              ))}
            </List>
          )}
        </Alert>
      )}
    </>
  );

  if (phone) {
    return (
      <div data-testid="deliver-workspace" data-phone="true" style={{ display: 'flex', flexDirection: 'column', flex: '1 1 auto', minHeight: 0, height: '100%' }}>
        <div style={{ flex: '1 1 auto', minHeight: 0, overflowY: 'auto', padding: '4px 14px 12px' }} data-testid="deliver-phone-scroll">
          <Stack gap="sm">
            <Group gap={6} wrap="nowrap" align="flex-start" data-testid="deliver-parity">
              <IconInfoCircle size={16} aria-hidden style={{ flexShrink: 0, marginTop: 2 }} />
              <Text size="sm">
                {t('previewIsPausedWhile')}
              </Text>
            </Group>
            <Stack gap="xs" data-testid="deliver-result">
              {film ? (
                <Stack gap="xs">
                  {film}
                  {!readOnly && latest && <ShareForReview projectId={projectId} timelineId={timelineId} defaultExecutionId={latest.executionId} />}
                </Stack>
              ) : (
                filmEmpty
              )}
            </Stack>
            {clipBlock}
            {settingsBlock(footerEl, (
              <Stack gap="xs">
                {captionChoiceBlock}
                <ExportPanel projectId={projectId} timelineId={timelineId} doc={doc} captionCount={captionCount} dirty={dirty} />
              </Stack>
            ), false)}
            {refusalBlock}
            <Text size="sm" fw={600}>
              {t('renderQueue')}
            </Text>
            <Box style={{ maxHeight: 'min(24vh, 220px)', overflowY: 'auto' }}>
              <RenderQueue
                renders={shown}
                progress={progress}
                outputUrl={(stepResultId, fileName) => getOutputDownloadUrl(projectId, stepResultId, fileName)}
                onCancel={onCancel}
                onRetry={onRetryRender}
                onRemove={onRemoveRender}
                loading={rendersLoading}
                itemNames={itemNames}
              />
            </Box>
          </Stack>
        </div>
        <div
          ref={setFooterEl}
          data-testid="deliver-sticky-footer"
          style={{ flex: '0 0 auto', padding: '8px 14px calc(8px + env(safe-area-inset-bottom, 0px))', borderTop: '1px solid var(--mantine-color-default-border)', background: 'var(--mantine-color-body)' }}
        />
      </div>
    );
  }

  // Wave 5: the settings column. Presets on top, the options of the chosen one under it, the statement and Render last.
  const controlsColumn = (
    <Stack gap="xs" data-testid="deliver-controls">
      {clipBlock}
      {prefill && prefill.from !== '' && (
        <Text size="xs" role="status" data-testid="render-prefilled">
          {t('settingsLoadedFrom', { outputFileName: prefill.from })}
        </Text>
      )}
      {settingsBlock(null, null, true)}
      {refusalBlock}
    </Stack>
  );

  // Wave 5: the result column, in the order the work goes: watch the film, follow the queue, then pass it on.
  const resultColumn = (
    <div className={classes.resultGrid} data-testid="deliver-result">
      <div className={classes.resultGroup}>
        <DeliverSection title={t('sections.film')} testId="deliver-film-section">
          {filmCard(readOnly) ?? filmEmpty}
        </DeliverSection>
        <DeliverSection title={t('sections.queue')} description={t('sections.queueHint')} testId="deliver-queue-section">
          <RenderQueue
            renders={shown}
            progress={progress}
            outputUrl={(stepResultId, fileName) => getOutputDownloadUrl(projectId, stepResultId, fileName)}
            captionFileUrl={(fileId) => `/api/v1/projects/${projectId}/files/${fileId}/download`}
            onCancel={onCancel}
            onRetry={readOnly ? undefined : onRetryRender}
            onRemove={onRemoveRender}
            onRenderAgain={readOnly ? undefined : onRenderAgain}
            canRenderAgain={(render) => sent.has(render.executionId)}
            onFirstAction={readOnly ? undefined : () => setFocusPresets((n) => n + 1)}
            featuredExecutionId={latest?.executionId ?? null}
            loading={rendersLoading}
            itemNames={itemNames}
          />
        </DeliverSection>
      </div>
      <div className={classes.resultGroup}>
        <DeliverSection title={t('sections.review')} description={t('sections.reviewHint')} testId="deliver-review-section">
          <ReviewLinksBody projectId={projectId} timelineId={timelineId} defaultExecutionId={latest?.executionId ?? null} renders={renders} readOnly={readOnly} inline />
        </DeliverSection>
        <DeliverSection title={t('sections.export')} description={t('sections.exportHint')} testId="deliver-export-section">
          <ExportPanel projectId={projectId} timelineId={timelineId} doc={doc} captionCount={captionCount} dirty={dirty} layout="page" />
        </DeliverSection>
        <DeliverSection title={t('sections.handOff')} description={t('sections.handOffHint')} testId="deliver-handoff-section">
          <HandOffCard projectId={projectId} timelineId={timelineId} doc={doc} dirty={dirty} heading={false} />
        </DeliverSection>
      </div>
    </div>
  );

  if (simple) {
    return (
      <Stack gap="sm" p="sm" data-testid="deliver-workspace" data-simple="true">
        <div data-testid="deliver-result">{filmCard(readOnly) ?? filmEmpty}</div>
        {controlsColumn}
        <RenderQueue
          renders={shown}
          progress={progress}
          outputUrl={(stepResultId, fileName) => getOutputDownloadUrl(projectId, stepResultId, fileName)}
          captionFileUrl={(fileId) => `/api/v1/projects/${projectId}/files/${fileId}/download`}
          onCancel={onCancel}
          onRetry={readOnly ? undefined : onRetryRender}
          onRemove={onRemoveRender}
          featuredExecutionId={latest?.executionId ?? null}
          loading={rendersLoading}
          itemNames={itemNames}
        />
        <Button variant="default" onClick={simple.onMoreOptions} data-testid="simple-more-export">
          {t('moreExportOptions')}
        </Button>
      </Stack>
    );
  }

  if (zoneTargets) {
    // T-003: the page frame owns the geometry. Settings fill the 420 px left zone; the result column
    // takes the rest (the frame drops the centre and the timeline for this page).
    return (
      <>
        {createPortal(controlsColumn, zoneTargets.controls)}
        {createPortal(resultColumn, zoneTargets.result)}
      </>
    );
  }

  return (
    <EditorPanel title={t('deliver')} testId="deliver-workspace" scroll={false}>
      <Stack gap="sm" pb="calc(var(--mantine-spacing-sm) + 28px + env(safe-area-inset-bottom, 0px))">
        <Box style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(min(100%, 380px), 1fr))', gap: 'var(--mantine-spacing-md)', alignItems: 'start' }}>
          {controlsColumn}
          {resultColumn}
        </Box>
      </Stack>
    </EditorPanel>
  );
}
