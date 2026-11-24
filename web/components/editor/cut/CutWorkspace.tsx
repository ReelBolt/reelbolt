'use client';

import React, { useEffect, useRef, useState } from 'react';
import { Alert, Badge, Box, Button, Divider, Group, ScrollArea, Select, Stack, Switch, Text } from '@mantine/core';
import { IconPlayerPlay, IconTrash } from '@tabler/icons-react';
import { PageEmptyState } from '../ui/PageEmptyState';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline } from '@/lib/editing/types';
import { framesToTimecode } from '@/lib/editing/time';
import { nearestEditPoint, overviewPixelsPerFrame, rankBoringShots, readShots, reviewPlan, reviewStopAt, speechAwareShots, type SpeechAwareShot } from '@/lib/editing/cut/cut';
import { readTranscript } from '@/lib/editing/transcript/transcript';
import { compileRippleCut } from '@/lib/editing/transcript/transcript';
import { compilePoolPlacement } from '@/lib/editing/media/pool-insert';
import type { MediaPoolItem } from '@/lib/editing/media/types';
import { useMediaPool } from '@/lib/hooks/use-media';
import { sourceMatcher, useTimelineAnalysis } from '@/lib/editing/api/analysis';
import { TimelineCanvas } from '../timeline/TimelineCanvas';
import { AnalysisHelpLink } from '../panels/AnalysisHelpLink';
import { PlaceModeButton } from '../place/PlaceMenu';
import type { PaintUi } from '../timeline/paint';
import type { ParityClass } from '@/lib/editing/preview';
import { EditorPanel } from '../ui/EditorPanel';
import { useFormat, useT } from '@/i18n';

export interface CutWorkspaceProps {
  doc: EditTimeline;
  projectId: string;
  seedStepResultId: string | null;
  readOnly: boolean;
  playheadFrame: number;
  playbackSpeed: number;
  selection: readonly string[];
  /** The track smart insert and the boring-shot tools act on (the first targeted video track). */
  trackId: string | null;
  trackFlags: PaintUi['trackFlags'];
  parityOf: (itemId: string) => ParityClass;
  newItemId: () => string;
  onSeek: (frame: number) => void;
  onSelect: (itemId: string | null) => void;
  onSetSpeed: (speed: number) => void;
  onOp: (op: EditOp, label?: string) => void;
  onApply: (ops: readonly EditOp[], label: string) => void;
  /** Opens a tape source in the viewer's Source monitor (Source | Record switch); absent = no Watch button. */
  onOpenSource?: (mediaRef: string) => void;
  /** The source the monitor has open, so its tape row reads as open. */
  openSourceRef?: string | null;
}

/**
 * The Cut workspace (P6): a source tape of the project's media with one-click **smart insert** at
 * the edit point nearest the playhead; an **overview strip** — the whole timeline at a zoom that
 * fits, always in sync with the detail timeline because both read the same store; **fast review**,
 * which plays each clip at a speed scaled to its length; and the **boring shots** the analysis
 * measured as long and still, selectable or removable in one ripple cut.
 */
export function CutWorkspace({ doc, projectId, seedStepResultId, readOnly, playheadFrame, playbackSpeed, trackId, trackFlags, newItemId, onSeek, onSelect, onSetSpeed, onApply, onOpenSource, openSourceRef = null }: CutWorkspaceProps) {
  const t = useT('editor-misc.cut');
  const nf = useFormat();
  const dec = (v: number, digits: number) => nf.number(v, { minimumFractionDigits: digits, maximumFractionDigits: digits });
  const fps = doc.settings.rate.den === 0 ? 30 : doc.settings.rate.num / doc.settings.rate.den;
  const { data: pool, error: poolError } = useMediaPool(projectId);
  const { data: analysis } = useTimelineAnalysis(projectId, seedStepResultId, doc);
  const artifact = analysis?.artifact;
  const matches = sourceMatcher(analysis, doc);
  const videoTracks = doc.tracks.filter((t) => t.kind === 'video');
  const [chosenTrack, setChosenTrack] = useState<string | null>(null);
  const effectiveTrackId = chosenTrack ?? trackId ?? videoTracks[0]?.id ?? null;
  const [message, setMessage] = useState<string | null>(null);

  // ── source tape ───────────────────────────────────────────────────────────
  const sources = (pool ?? []).filter((item) => item.kind === 'video' || item.kind === 'image');
  const insertAt = effectiveTrackId ? nearestEditPoint(doc, effectiveTrackId, playheadFrame) : null;

  // S9: the same placement as Media > Insert at playhead (compilePoolPlacement): the picture rippled in at the
  // edit point nearest the playhead, and - for footage with sound - the paired "Camera audio" clip in the same
  // batch, so one undo removes both and the inserted shot is not silent.
  const smartInsert = (item: MediaPoolItem) => {
    const track = effectiveTrackId ? doc.tracks.find((t) => t.id === effectiveTrackId) : undefined;
    if (!track) return;
    const placed = compilePoolPlacement(doc, {
      item,
      track,
      where: 'playhead',
      playheadFrame,
      rate: doc.settings.rate,
      newId: newItemId,
      isLocked: (t) => trackFlags(t).locked,
    });
    if (!placed) return;
    onApply(placed.ops, t('insertLabel', { label: placed.label, time: framesToTimecode(placed.atFrame, doc.settings.rate) }));
    onSelect(placed.clipId);
    onSeek(placed.atFrame);
    setMessage(
      t('inserted', { file: item.fileName, time: framesToTimecode(placed.atFrame, doc.settings.rate), sound: placed.companionId ? 'yes' : 'no', known: placed.span.lengthKnown ? 'yes' : 'no', seconds: 5 }),
    );
  };

  // ── fast review ───────────────────────────────────────────────────────────
  const [fastReview, setFastReview] = useState(false);
  const plan = (effectiveTrackId ? reviewPlan(doc, effectiveTrackId, fps) : []);
  const lastSpeedRef = useRef<number | null>(null);
  useEffect(() => {
    if (!fastReview || playbackSpeed <= 0) {
      lastSpeedRef.current = null;
      return;
    }
    const stop = reviewStopAt(plan, playheadFrame);
    if (stop && stop.speed !== lastSpeedRef.current) {
      lastSpeedRef.current = stop.speed;
      if (stop.speed !== playbackSpeed) onSetSpeed(stop.speed);
    }
  }, [fastReview, playbackSpeed, playheadFrame, plan, onSetSpeed]);
  const startReview = () => {
    if (plan.length === 0) return;
    setFastReview(true);
    const stop = reviewStopAt(plan, playheadFrame) ?? plan[0];
    onSeek(stop.startFrame);
    lastSpeedRef.current = stop.speed;
    onSetSpeed(stop.speed);
  };

  // ── boring shots ──────────────────────────────────────────────────────────
  // R09.8: with a transcript, a boring shot is only offered for the part nobody is talking over (never mid-sentence).
  const transcriptWords = readTranscript(artifact).words;
  const hasTranscript = transcriptWords.length > 0;
  const boring: SpeechAwareShot[] = (() => {
    if (!effectiveTrackId) return [];
    const ranked = rankBoringShots(readShots(artifact), {}, { doc, trackId: effectiveTrackId, fps, matches }).filter((b) => b.record !== null);
    return speechAwareShots(ranked, hasTranscript ? transcriptWords : null, {}, { doc, fps, matches });
  })();
  const removeShot = (shot: SpeechAwareShot) => {
    const span = shot.cutRecord ?? shot.record;
    if (!effectiveTrackId || !span || shot.speech === 'blocked') return;
    const cut = compileRippleCut(doc, { trackId: effectiveTrackId, spans: [{ startFrame: span.startFrame, endFrame: span.endFrame }], newItemId });
    if (cut.ops.length === 0) return;
    onApply(cut.ops, t('removeLabel', { id: shot.id, frames: cut.removedFrames }));
    setMessage(t('removed', { id: shot.id, frames: cut.removedFrames, linked: cut.linkedCuts > 0 ? 'yes' : 'no' }));
  };

  // ── overview strip ────────────────────────────────────────────────────────
  // T-003: the strip is the page's own band now (CutOverview), between the viewer row and the
  // timeline; the left column keeps the source tape and the review tools.

  return (
    <EditorPanel title={t('title')} actions={<Select size="xs" label={t('editOn')} data={videoTracks.map((t) => ({ value: t.id, label: t.name }))} value={effectiveTrackId} onChange={setChosenTrack} allowDeselect={false} w={140} data-testid="cut-track" />} testId="cut-workspace" scroll={false}>
      <Stack gap="sm">
        <Divider label={t('sourceTape')} labelPosition="left" />
        {poolError && <Alert color="red" p="xs">{t('poolFailed')}</Alert>}
        {sources.length === 0 ? (
          <PageEmptyState page="cut" hint={t('noSourcesHint')} testId="cut-sources-empty" compact />
        ) : (
          <ScrollArea.Autosize mah={180}>
            <Stack gap={2}>
              {sources.map((item) => (
                <Group key={item.mediaRef} gap={6} wrap="nowrap" justify="space-between" data-testid={`cut-source-${item.mediaRef}`}>
                  <Stack gap={0} style={{ minWidth: 0 }}>
                    <Text size="xs" truncate>
                      {item.fileName}
                    </Text>
                    <Text size="xs" c="dimmed">
                      {t('sourceMeta', { kind: t(`kinds.${item.kind}`), duration: item.probe?.durationSec ? t('seconds', { value: dec(item.probe.durationSec, 1) }) : t('notProbed') })}
                    </Text>
                  </Stack>
                  <Group gap={4} wrap="nowrap">
                  {onOpenSource ? (
                    <Button size="compact-xs" fz="xs" variant={openSourceRef === item.mediaRef ? 'filled' : 'subtle'} leftSection={<IconPlayerPlay size={12} />} aria-pressed={openSourceRef === item.mediaRef} onClick={() => onOpenSource(item.mediaRef)} data-testid={`cut-watch-${item.mediaRef}`} data-editor-shortcuts="through" data-editor-own-keys="activate">
                      {t('watch')}
                    </Button>
                  ) : null}
                  <PlaceModeButton mode="insert" variant="light" disabled={readOnly || insertAt === null} onPlace={() => smartInsert(item)} testId={`cut-insert-${item.mediaRef}`} />
                  </Group>
                </Group>
              ))}
            </Stack>
          </ScrollArea.Autosize>
        )}

        <Divider label={t('fastReview')} labelPosition="left" />
        <Group gap="xs">
          <Button size="compact-xs" fz="xs" leftSection={<IconPlayerPlay size={12} />} disabled={plan.length === 0} onClick={startReview} data-testid="cut-review-start" data-editor-shortcuts="through" data-editor-own-keys="activate">
            {t('review')}
          </Button>
          <Switch size="xs" label={t('speedByLength')} checked={fastReview} onChange={(e) => setFastReview(e.currentTarget.checked)} data-testid="cut-review-toggle" />
          <Text size="xs" c="dimmed">
            {t('planNote', { count: plan.length, min: 1, max: 8 })}
          </Text>
        </Group>

        <Divider label={t('boringShots', { count: boring.length })} labelPosition="left" />
        {seedStepResultId === null && !analysis ? (
          <Stack gap={4} align="flex-start" data-testid="cut-boring-no-analysis">
            <Text size="xs" c="dimmed">{t('noAnalysis')}</Text>
            <AnalysisHelpLink projectId={projectId} />
          </Stack>
        ) : boring.length === 0 ? (
          <Text size="xs" c="dimmed" data-testid="cut-boring-none">{t('noBoring')}</Text>
        ) : (
          <>
          {!hasTranscript && (
            <Text size="xs" c="dimmed" data-testid="cut-boring-unchecked">{t('speechUnchecked')}</Text>
          )}
          <ScrollArea.Autosize mah={200}>
            <Stack gap={2}>
              {boring.map((shot) => (
                <Group key={shot.id} gap={6} wrap="nowrap" justify="space-between" data-testid={`cut-boring-${shot.id}`}>
                  <Group gap={6} wrap="nowrap">
                    <Badge size="xs" fz="xs" variant="light" color="gray">
                      {shot.id}
                    </Badge>
                    <Text size="xs" ff="monospace">
                      {shot.record ? framesToTimecode(shot.record.startFrame, doc.settings.rate) : ''}
                    </Text>
                    <Text size="xs" c="dimmed">
                      {t('shotMeta', { seconds: dec(shot.durationSec, 1), motion: dec(shot.motionMean ?? 0, 3) })}
                    </Text>
                    {shot.speech === 'trimmed' && (
                      <Badge size="xs" fz="xs" variant="light" color="blue" data-testid={`cut-boring-speech-${shot.id}`}>
                        {t('cutsQuiet', { seconds: shot.cutSec ? dec(shot.cutSec.endSec - shot.cutSec.startSec, 1) : nf.number(0) })}
                      </Badge>
                    )}
                    {shot.speech === 'blocked' && (
                      <Badge size="xs" fz="xs" variant="light" color="orange" data-testid={`cut-boring-speech-${shot.id}`}>
                        {t('talking')}
                      </Badge>
                    )}
                  </Group>
                  <Group gap={4} wrap="nowrap">
                    <Button size="compact-xs" fz="xs" variant="subtle" onClick={() => { if (shot.record) { onSeek(shot.record.startFrame); onSelect(shot.record.clipId); } }} data-testid={`cut-boring-select-${shot.id}`}>
                      {t('show')}
                    </Button>
                    <Button size="compact-xs" fz="xs" variant="subtle" color="red" leftSection={<IconTrash size={12} />} disabled={readOnly || shot.speech === 'blocked'} onClick={() => removeShot(shot)} data-testid={`cut-boring-remove-${shot.id}`}>
                      {t('remove')}
                    </Button>
                  </Group>
                </Group>
              ))}
            </Stack>
          </ScrollArea.Autosize>
          </>
        )}
        {message && (
          <Text size="xs" c="dimmed" role="status" data-testid="cut-message">
            {message}
          </Text>
        )}
      </Stack>
    </EditorPanel>
  );
}

export interface CutOverviewProps {
  doc: EditTimeline;
  playheadFrame: number;
  selection: readonly string[];
  trackFlags: PaintUi['trackFlags'];
  parityOf: (itemId: string) => ParityClass;
  onSeek: (frame: number) => void;
  onSelect: (itemId: string | null) => void;
}

/**
 * T-003: the overview strip as the Cut page's own band, between the viewer row and the timeline.
 * It shows the whole timeline at a zoom that fits, always in sync with the detail timeline because
 * both read the same store, and it edits nothing.
 */
export function CutOverview({ doc, playheadFrame, selection, trackFlags, parityOf, onSeek, onSelect }: CutOverviewProps) {
  const t = useT('editor-misc.cut');
  const stripRef = useRef<HTMLDivElement | null>(null);
  const [stripWidth, setStripWidth] = useState(600);
  useEffect(() => {
    const el = stripRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return undefined;
    const ro = new ResizeObserver(() => setStripWidth(Math.max(120, el.clientWidth)));
    ro.observe(el);
    setStripWidth(Math.max(120, el.clientWidth));
    return () => ro.disconnect();
  }, []);
  const overviewZoom = overviewPixelsPerFrame(doc, stripWidth - 8);
  const noop = () => {};
  return (
    <Box ref={stripRef} title={t('overviewNote')} data-testid="cut-overview" style={{ height: '100%', minHeight: 0, border: '1px solid var(--mantine-color-default-border)', borderRadius: 4, overflow: 'hidden' }}>
      <TimelineCanvas
        doc={doc}
        playheadFrame={playheadFrame}
        selection={selection}
        inFrame={null}
        outFrame={null}
        pixelsPerFrame={overviewZoom}
        scrollFrame={0}
        tool="select"
        overview
        trackFlags={trackFlags}
        parityOf={parityOf}
        rationaleFrames={[]}
        snap={(f) => f}
        onSeek={onSeek}
        onSelect={(itemId) => onSelect(itemId)}
        onMarquee={noop}
        onBlade={noop}
        onMoveItem={noop}
        onTrimItem={noop}
        onZoom={noop}
        onScroll={noop}
        height={Math.max(40, Math.min(96, 20 + doc.tracks.length * 16))}
      />
    </Box>
  );
}
