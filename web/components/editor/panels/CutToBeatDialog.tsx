'use client';

import React, { useState } from 'react';
import {
  Alert,
  Button,
  Group,
  Loader,
  Modal,
  MultiSelect,
  NumberInput,
  SegmentedControl,
  Select,
  SimpleGrid,
  Stack,
  Switch,
  Text,
} from '@mantine/core';
import { IconMusic } from '@tabler/icons-react';
import { useIsTouchSurface } from '@/lib/editing/use-device-class';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline, MediaClip } from '@/lib/editing/types';
import {
  buildBeatGrid,
  gridLines,
  NO_CORRECTION,
  type BeatCorrection,
  type BeatTempo,
  type GridUnit,
} from '@/lib/editing/beats/beat-grid';
import {
  compileCutToBeat,
  DEFAULT_MAX_MARKERS,
  HARD_MAX_MARKERS,
  type CutToBeatMode,
  type CutToBeatSummary,
  type SkipReason,
} from '@/lib/editing/beats/cut-to-beat';
import { motionRankForClips, sourceFramesFromProbe, type MotionByFileId, type ProbeDurationsByFileId } from '@/lib/editing/beats/feeds';
import { beatSecondsToRecordFrames } from '@/lib/editing/beats/map-to-record';
import { fileMediaRef } from '@/lib/editing/api/media-analysis';
import { setBeatCorrection, setBeatMusicClip, useBeatChoice } from '@/lib/editing/beats/beat-choice-store';
import { useBeatAnalysis } from '@/lib/editing/beats/use-beat-analysis';
import { useFormat, useT } from '@/i18n';
import { useJobFor } from '@/lib/activity/ActivityProvider';
import { InlineJobStatus } from '@/components/activity/InlineJobStatus';

export interface CutToBeatDialogProps {
  opened: boolean;
  onClose: () => void;
  doc: EditTimeline;
  projectId: string;
  /** Fresh item ids for the blades and markers. */
  newItemId: () => string;
  /** What is selected on the timeline; the music clip and the clips to split default from it. */
  selectedItemIds: readonly string[];
  /** Applies the compiled program as ONE history entry. */
  onApply: (ops: readonly EditOp[], label: string) => void;
  /** R09.3: play the result of the proposed cuts in the viewer first; Apply/Cancel happen there. */
  onAudition?: (ops: readonly EditOp[], label: string) => void;
  /** Probed source lengths, keyed by project file id (see `lib/editing/beats/feeds`). */
  probeDurations?: ProbeDurationsByFileId;
  /** Motion windows, keyed by project file id (see `lib/editing/beats/feeds`). */
  motion?: MotionByFileId;
  /** A read-only document shows the plan but cannot apply it. */
  readOnly?: boolean;
}

/** A finger-sized target (44 px) for every control on a coarse pointer or a phone width. */
const TOUCH_TARGET = 44;
const NUDGE_STEP_MS = 10;
const NUDGE_LIMIT_MS = 500;
const TEMPO_OFFER_SCORE = 0.6;
const LOW_CONFIDENCE = 0.3;

const isMedia = (item: { type: string }): item is MediaClip => item.type === 'mediaClip';

const clampNudge = (value: number): number => Math.max(-NUDGE_LIMIT_MS, Math.min(NUDGE_LIMIT_MS, value));

/**
 * "Cut to the music" as a reviewable dialog (T-007): the free beat analysis of one music clip is read
 * and corrected in the browser (half/double tempo, 10 ms nudges, bar shift), a grid unit is chosen, and
 * one of four programs is compiled from it — snap the cuts, split every, add markers, or fit the clips
 * to the music. The resulting plan is shown as a live summary line before anything changes; Apply
 * dispatches it as one undo step, and Audition plays it in the viewer first.
 */
export function CutToBeatDialog({
  opened,
  onClose,
  doc,
  projectId,
  newItemId,
  selectedItemIds,
  onApply,
  onAudition,
  probeDurations,
  motion,
  readOnly = false,
}: CutToBeatDialogProps) {
  const tx = useT('editor.panels.cutToBeatDialog');
  // The analyse panel owns the translated tempo words ("slow", "medium", …).
  const at = useT('editor.panels.analysePanel');
  const nf = useFormat();
  const touch = useIsTouchSurface();
  const inputStyles = touch ? { input: { minHeight: TOUCH_TARGET }, option: { minHeight: TOUCH_TARGET } } : undefined;
  const buttonStyle = touch ? { minHeight: TOUCH_TARGET, minWidth: TOUCH_TARGET } : undefined;
  const inputSize = touch ? 'md' : 'xs';

  const choice = useBeatChoice(projectId);
  const musicClipId = choice?.musicClipId ?? null;
  const setMusicClipId = (id: string | null): void => {
    if (id !== null) setBeatMusicClip(projectId, id);
  };
  const [modeKind, setModeKind] = useState<CutToBeatMode['kind']>('snapCuts');
  const [unit, setUnit] = useState<GridUnit>('bar');
  const [snapTrackIds, setSnapTrackIds] = useState<string[] | null>(null);
  const [toleranceFrames, setToleranceFrames] = useState(6);
  const [splitItemIds, setSplitItemIds] = useState<string[] | null>(null);
  const [minPieceFrames, setMinPieceFrames] = useState(6);
  const [maxMarkers, setMaxMarkers] = useState(DEFAULT_MAX_MARKERS);
  const [fitTrackIdState, setFitTrackId] = useState<string | null>(null);
  const [barsPerClip, setBarsPerClip] = useState<string>('auto');
  const [startAt, setStartAt] = useState<'musicStart' | 'firstDownbeat'>('musicStart');
  const [strongestOnDrop, setStrongestOnDrop] = useState(false);
  const [stopAtMusicEnd, setStopAtMusicEnd] = useState(true);

  const fps = doc.settings.rate.den === 0 ? 30 : doc.settings.rate.num / doc.settings.rate.den;

  // ── The music clip: a media clip on an audio track, or a video clip with sound ──────────────
  const mediaTracks = doc.tracks.filter((t) => t.items.some(isMedia));
  const musicCandidates = mediaTracks.flatMap((t) =>
    t.items.filter(isMedia).filter((i) => i.hold !== true).map((clip) => ({ clip, track: t })),
  );
  const selected = new Set(selectedItemIds);
  const preferred = musicCandidates.find((c) => selected.has(c.clip.id) && c.track.kind === 'audio')
    ?? musicCandidates.find((c) => selected.has(c.clip.id))
    ?? musicCandidates.find((c) => c.track.kind === 'audio')
    ?? musicCandidates[0];
  const effectiveMusicId = musicClipId !== null && musicCandidates.some((c) => c.clip.id === musicClipId) ? musicClipId : preferred?.clip.id ?? null;
  const musicEntry = musicCandidates.find((c) => c.clip.id === effectiveMusicId) ?? null;
  const music = musicEntry?.clip ?? null;

  const correction: BeatCorrection = choice && choice.musicClipId === effectiveMusicId ? choice.correction : NO_CORRECTION;
  const setCorrection = (next: BeatCorrection): void => {
    if (effectiveMusicId !== null) setBeatCorrection(projectId, effectiveMusicId, next);
  };
  const beat = useBeatAnalysis(projectId, music);
  const musicFileId = music?.assetRef?.kind === 'projectFile' ? (music.assetRef.projectFileId ?? null) : null;
  const beatJob = useJobFor((j) => j.type === 'mediaAnalysis' && musicFileId !== null && j.subject?.mediaRef === fileMediaRef(musicFileId) && (j.subject?.analysisKind ?? 'beats') === 'beats');

  // ── The grid, recomputed in the browser from the analysis and the manual correction ─────────
  const grid = beat.analysis ? buildBeatGrid(beat.analysis, correction) : null;
  const mappedLines = music && grid ? beatSecondsToRecordFrames(music, gridLines(grid, unit), fps) : [];
  const lineCount = Array.isArray(mappedLines) ? mappedLines.length : 0;

  const tempoWord = (word: string): string => {
    switch (word) {
      case 'slow': return at('tempoSlow');
      case 'medium': return at('tempoMedium');
      case 'fast': return at('tempoFast');
      case 'very fast': return at('tempoVeryFast');
      default: return word;
    }
  };

  const skipReasonText = (reason: SkipReason): string => {
    switch (reason) {
      case 'locked': return tx('skipLocked');
      case 'hold': return tx('skipHold');
      case 'noMusic': return tx('skipNoMusic');
      case 'outsideMusic': return tx('skipOutsideMusic');
      case 'noHandle': return tx('skipNoHandle');
      case 'tooShort': return tx('skipTooShort');
      case 'markerCap': return tx('skipMarkerCap');
      case 'shortClip': return tx('skipShortClip');
      case 'pastMusicEnd': return tx('skipPastMusicEnd');
      case 'beforeDrop': return tx('skipBeforeDrop');
      case 'invalid': return tx('skipInvalid');
      default: return tx('skipInvalid');
    }
  };

  // ── Mode controls and the compiled plan ─────────────────────────────────────────────────────
  const selectedMediaIds = selectedItemIds.filter((id) => musicCandidates.some((c) => c.clip.id === id));
  const splitIds = (splitItemIds ?? selectedMediaIds).filter((id) => musicCandidates.some((c) => c.clip.id === id));
  const targetTrackIds = (snapTrackIds ?? mediaTracks.map((t) => t.id)).filter((id) => mediaTracks.some((t) => t.id === id));

  const fitTracks = mediaTracks;
  const defaultFitTrackId = fitTracks.find((t) => t.id !== musicEntry?.track.id)?.id ?? fitTracks[0]?.id ?? null;
  const fitTrackId = fitTrackIdState !== null && fitTracks.some((t) => t.id === fitTrackIdState) ? fitTrackIdState : defaultFitTrackId;

  const rank = fitTrackId ? motionRankForClips(doc, fitTrackId, motion ?? {}) : {};
  const hasRank = Object.keys(rank).length > 0;
  const dropSec = beat.analysis?.dropSec ?? null;
  const dropOffered = hasRank && dropSec !== null;

  const mode: CutToBeatMode | null = music
    ? modeKind === 'snapCuts'
      ? { kind: 'snapCuts', trackIds: targetTrackIds, toleranceFrames }
      : modeKind === 'split'
        ? { kind: 'split', itemIds: splitIds, minPieceFrames }
        : modeKind === 'markers'
          ? { kind: 'markers', maxMarkers }
          : fitTrackId
            ? {
              kind: 'fit',
              trackId: fitTrackId,
              barsPerClip: barsPerClip === 'auto' ? 'auto' : Number(barsPerClip),
              startAt,
              onDrop: strongestOnDrop && dropOffered ? 'strongestClip' : 'none',
              rank,
              stopAtMusicEnd,
              dropSec,
            }
            : null
    : null;

  const compile = (makeId: () => string) => (music && grid && mode
    ? compileCutToBeat(doc, {
      musicClipId: music.id,
      grid,
      unit,
      newItemId: makeId,
      mode,
      sourceFrames: sourceFramesFromProbe(probeDurations ?? {}, fps),
    })
    : null);

  let previewCounter = 0;
  const plan = compile(() => `preview-${++previewCounter}`);

  const labelFor = (summary: CutToBeatSummary): string => {
    switch (modeKind) {
      case 'snapCuts': return tx('labelSnap', { cuts: summary.cutsMoved });
      case 'split': return tx('labelSplit', { cuts: summary.splits });
      case 'markers': return tx('labelMarkers', { count: summary.markers });
      default: return tx('labelFit', { count: summary.clipsFitted });
    }
  };

  const skipped = plan ? plan.summary.skipped.length : 0;
  const reasons = plan ? [...new Set(plan.summary.skipped.map((s) => s.reason))].map(skipReasonText).join(', ') : '';

  const summary = (() => {
    if (!plan) return tx('chooseMusic');
    if (modeKind === 'snapCuts') return tx('summarySnap', { cuts: plan.summary.cutsMoved, skipped, reason: reasons });
    if (modeKind === 'split') return tx('summarySplit', { cuts: plan.summary.splits, skipped, reason: reasons });
    if (modeKind === 'markers') return tx('summaryMarkers', { count: plan.summary.markers, skipped, reason: reasons });
    return tx('summaryFit', { fitted: plan.summary.clipsFitted, dropped: plan.summary.clipsDropped, skipped, reason: reasons });
  })();

  const apply = () => {
    if (!plan || plan.ops.length === 0 || readOnly) return;
    const real = compile(newItemId);
    if (!real || real.ops.length === 0) return;
    onApply(real.ops, labelFor(real.summary));
    onClose();
  };

  const audition = () => {
    if (!plan || plan.ops.length === 0 || !onAudition) return;
    const real = compile(newItemId);
    if (!real || real.ops.length === 0) return;
    onAudition(real.ops, labelFor(real.summary));
  };

  const setNudge = (delta: number) => setCorrection({ ...correction, nudgeMs: clampNudge(correction.nudgeMs + delta) });

  const unitOptions = [
    { value: 'beat', label: tx('unitBeat') },
    { value: 'twoBeats', label: tx('unitTwoBeats') },
    { value: 'bar', label: tx('unitBar') },
    { value: 'twoBars', label: tx('unitTwoBars') },
    { value: 'fourBars', label: tx('unitFourBars') },
  ];

  const analysis = beat.analysis;
  const tempoLine = analysis ? tx('tempoLine', { bpm: Math.round(analysis.bpm), tempoWord: tempoWord(analysis.tempoWord) }) : '';
  const toleranceSeconds = nf.number(toleranceFrames / fps, { maximumFractionDigits: 2 });

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={tx('cutToTheMusic')}
      size="lg"
      fullScreen={touch}
      closeButtonProps={{ size: touch ? TOUCH_TARGET : undefined, 'aria-label': 'Close' }}
      data-testid="cut-to-beat-dialog"
    >
      <Stack gap="sm">
        {/* ── Music clip and its beat analysis ─────────────────────────────────────────────── */}
        <Select
          label={tx('music')}
          size={inputSize}
          styles={inputStyles}
          leftSection={<IconMusic size={15} stroke={1.7} />}
          data={musicCandidates.map((c) => ({ value: c.clip.id, label: `${c.track.name} · ${c.clip.name}` }))}
          value={effectiveMusicId}
          onChange={setMusicClipId}
          allowDeselect={false}
          disabled={musicCandidates.length === 0}
          placeholder={musicCandidates.length === 0 ? tx('noMusicClip') : undefined}
          data-testid="cut-to-beat-music"
        />

        {!music && (
          <Alert color="yellow" title={tx('noMusicTitle')} data-testid="cut-to-beat-no-music">
            {tx('noMusicClip')}
          </Alert>
        )}

        {music && beat.state === 'unsupported' && (
          <Alert color="yellow" title={tx('cannotFindBeat')} data-testid="cut-to-beat-unsupported">
            {tx('unsupported')}
          </Alert>
        )}

        {music && (beat.state === 'none' || beat.state === 'failed') && (
          <Alert color={beat.state === 'failed' ? 'red' : 'blue'} title={beat.state === 'failed' ? tx('failedToFind') : tx('noBeatYetTitle')} data-testid="cut-to-beat-find-panel">
            <Stack gap={6}>
              <Text size="xs">{beat.state === 'failed' ? tx('failedToFindText') : tx('noBeatYet')}</Text>
              {beat.state === 'failed' && beat.reason && <Text size="xs" data-testid="cut-to-beat-reason">{beat.reason}</Text>}
              {beat.error && <Text size="xs" c="red">{beat.error}</Text>}
              <Group>
                <Button size={touch ? 'md' : 'compact-xs'} style={buttonStyle} onClick={() => void beat.start()} loading={beat.starting} data-testid="cut-to-beat-find">
                  {beat.state === 'failed' ? tx('tryAgain') : tx('findTheBeat')}
                </Button>
              </Group>
            </Stack>
          </Alert>
        )}

        {music && (beat.state === 'queued' || beat.state === 'running') && (
          beatJob ? (
            <div data-testid="cut-to-beat-finding"><InlineJobStatus job={beatJob} /></div>
          ) : (
            <Group gap="xs" role="status" data-testid="cut-to-beat-finding">
              <Loader size={14} />
              <Text size="xs" c="dimmed">{beat.state === 'queued' ? tx('waitingForTheBeat') : tx('findingBeatWorking')}</Text>
            </Group>
          )
        )}

        {music && beat.state === 'ready' && !analysis && (
          <Alert color="red" title={tx('failedToFind')} data-testid="cut-to-beat-unreadable">
            <Stack gap={6}>
              <Text size="xs">{tx('unreadable')}</Text>
              {beat.error && <Text size="xs" c="red">{beat.error}</Text>}
              <Group>
                <Button size={touch ? 'md' : 'compact-xs'} style={buttonStyle} onClick={() => void beat.start()} loading={beat.starting} data-testid="cut-to-beat-retry">
                  {tx('tryAgain')}
                </Button>
              </Group>
            </Stack>
          </Alert>
        )}

        {analysis && grid && (
          <>
            <Group gap="md" wrap="wrap">
              <Text size="sm" fw={600} data-testid="cut-to-beat-tempo">{tempoLine}</Text>
              <Text size="xs" c="dimmed" data-testid="cut-to-beat-grid-count">{tx('gridLineCount', { count: lineCount })}</Text>
            </Group>

            {analysis.confidence < LOW_CONFIDENCE && (
              <Alert color="yellow" title={tx('beatNotClear')} data-testid="cut-to-beat-confidence-warning">
                {tx('beatNotClearText')}
              </Alert>
            )}
            {!analysis.steady && (
              <Alert color="yellow" title={tx('beatChanges')} data-testid="cut-to-beat-steady-warning">
                {tx('beatChangesText')}
              </Alert>
            )}

            {(analysis.halfTempoScore >= TEMPO_OFFER_SCORE || analysis.doubleTempoScore >= TEMPO_OFFER_SCORE) && (
              <Alert color="blue" title={tx('checkTheTempo')} data-testid="cut-to-beat-tempo-offer">
                <Stack gap={6}>
                  {analysis.halfTempoScore >= TEMPO_OFFER_SCORE && <Text size="xs">{tx('halfOffer')}</Text>}
                  {analysis.doubleTempoScore >= TEMPO_OFFER_SCORE && <Text size="xs">{tx('doubleOffer')}</Text>}
                  <Group gap={touch ? 'sm' : 6}>
                    {analysis.halfTempoScore >= TEMPO_OFFER_SCORE && (
                      <Button size={touch ? 'md' : 'compact-xs'} style={buttonStyle} variant="light" onClick={() => setCorrection({ ...correction, tempo: 'half' })} data-testid="cut-to-beat-use-half">
                        {tx('useHalfTempo')}
                      </Button>
                    )}
                    {analysis.doubleTempoScore >= TEMPO_OFFER_SCORE && (
                      <Button size={touch ? 'md' : 'compact-xs'} style={buttonStyle} variant="light" onClick={() => setCorrection({ ...correction, tempo: 'double' })} data-testid="cut-to-beat-use-double">
                        {tx('useDoubleTempo')}
                      </Button>
                    )}
                  </Group>
                </Stack>
              </Alert>
            )}

            <SimpleGrid cols={touch ? 1 : 2}>
              <Stack gap={4}>
                <Text size="xs" fw={500}>{tx('tempo')}</Text>
                <SegmentedControl
                  size={touch ? 'md' : 'xs'}
                  fullWidth
                  data={[
                    { value: 'half', label: tx('tempoHalf') },
                    { value: 'asFound', label: tx('tempoAsFound') },
                    { value: 'double', label: tx('tempoDouble') },
                  ]}
                  value={correction.tempo}
                  onChange={(value) => setCorrection({ ...correction, tempo: value as BeatTempo })}
                  data-testid="cut-to-beat-tempo-control"
                />
              </Stack>
              <Select
                label={tx('barStartsOn')}
                size={inputSize}
                styles={inputStyles}
                data={[1, 2, 3, 4].map((n) => ({ value: String(n), label: tx('beatNumber', { n }) }))}
                value={String(correction.barShift + 1)}
                onChange={(value) => setCorrection({ ...correction, barShift: (Number(value ?? '1') - 1) as BeatCorrection['barShift'] })}
                allowDeselect={false}
                data-testid="cut-to-beat-bar-shift"
              />
            </SimpleGrid>

            <Group gap="sm" wrap="wrap" align="center">
              <Text size="xs" fw={500}>{tx('nudge')}</Text>
              <Group gap={6} wrap="nowrap">
                <Button size={touch ? 'md' : 'compact-xs'} style={buttonStyle} variant="default" onClick={() => setNudge(-NUDGE_STEP_MS)} disabled={correction.nudgeMs <= -NUDGE_LIMIT_MS} data-testid="cut-to-beat-nudge-earlier">
                  {tx('earlier')}
                </Button>
                <Text size="xs" ff="monospace" data-testid="cut-to-beat-nudge">{tx('milliseconds', { ms: correction.nudgeMs })}</Text>
                <Button size={touch ? 'md' : 'compact-xs'} style={buttonStyle} variant="default" onClick={() => setNudge(NUDGE_STEP_MS)} disabled={correction.nudgeMs >= NUDGE_LIMIT_MS} data-testid="cut-to-beat-nudge-later">
                  {tx('later')}
                </Button>
              </Group>
            </Group>

            <Select
              label={tx('cutOn')}
              size={inputSize}
              styles={inputStyles}
              data={unitOptions}
              value={unit}
              onChange={(value) => setUnit((value ?? 'bar') as GridUnit)}
              allowDeselect={false}
              data-testid="cut-to-beat-unit"
            />

            {/* ── What to do ─────────────────────────────────────────────────────────────── */}
            <Select
              label={tx('whatToDo')}
              size={inputSize}
              styles={inputStyles}
              data={[
                { value: 'snapCuts', label: tx('modeSnap') },
                { value: 'split', label: tx('modeSplit') },
                { value: 'markers', label: tx('modeMarkers') },
                { value: 'fit', label: tx('modeFit') },
              ]}
              value={modeKind}
              onChange={(value) => setModeKind((value ?? 'snapCuts') as CutToBeatMode['kind'])}
              allowDeselect={false}
              data-testid="cut-to-beat-mode"
            />

            {modeKind === 'snapCuts' && (
              <Stack gap="sm">
                <MultiSelect
                  label={tx('tracksToSnap')}
                  size={inputSize}
                  styles={inputStyles}
                  data={mediaTracks.map((t) => ({ value: t.id, label: t.name }))}
                  value={targetTrackIds}
                  onChange={setSnapTrackIds}
                  hidePickedOptions
                  data-testid="cut-to-beat-tracks"
                />
                <Stack gap={2}>
                  <NumberInput
                    label={tx('snapWithinFrames')}
                    size={inputSize}
                    styles={inputStyles}
                    min={0}
                    max={120}
                    value={toleranceFrames}
                    onChange={(value) => setToleranceFrames(typeof value === 'number' ? value : 0)}
                    data-testid="cut-to-beat-tolerance"
                  />
                  <Text size="xs" c="dimmed" data-testid="cut-to-beat-tolerance-seconds">{tx('aboutSeconds', { seconds: toleranceSeconds })}</Text>
                </Stack>
              </Stack>
            )}

            {modeKind === 'split' && (
              <Stack gap="sm">
                <MultiSelect
                  label={tx('clipsToSplit')}
                  size={inputSize}
                  styles={inputStyles}
                  data={musicCandidates.map((c) => ({ value: c.clip.id, label: `${c.track.name} · ${c.clip.name}` }))}
                  value={splitIds}
                  onChange={setSplitItemIds}
                  hidePickedOptions
                  data-testid="cut-to-beat-clips"
                />
                <NumberInput
                  label={tx('shortestPieceFrames')}
                  size={inputSize}
                  styles={inputStyles}
                  min={1}
                  max={300}
                  value={minPieceFrames}
                  onChange={(value) => setMinPieceFrames(typeof value === 'number' ? value : 1)}
                  data-testid="cut-to-beat-min-piece"
                />
              </Stack>
            )}

            {modeKind === 'markers' && (
              <NumberInput
                label={tx('mostMarkers')}
                size={inputSize}
                styles={inputStyles}
                min={1}
                max={HARD_MAX_MARKERS}
                value={maxMarkers}
                onChange={(value) => setMaxMarkers(typeof value === 'number' ? value : DEFAULT_MAX_MARKERS)}
                data-testid="cut-to-beat-marker-cap"
              />
            )}

            {modeKind === 'fit' && (
              <Stack gap="sm">
                <Select
                  label={tx('trackToFit')}
                  size={inputSize}
                  styles={inputStyles}
                  data={fitTracks.map((t) => ({ value: t.id, label: t.name }))}
                  value={fitTrackId}
                  onChange={setFitTrackId}
                  allowDeselect={false}
                  data-testid="cut-to-beat-fit-track"
                />
                <Stack gap={4}>
                  <Text size="xs" fw={500}>{tx('barsPerClip')}</Text>
                  <SegmentedControl
                    size={touch ? 'md' : 'xs'}
                    fullWidth
                    data={[
                      { value: 'auto', label: tx('barsAuto') },
                      { value: '1', label: '1' },
                      { value: '2', label: '2' },
                      { value: '4', label: '4' },
                    ]}
                    value={barsPerClip}
                    onChange={setBarsPerClip}
                    data-testid="cut-to-beat-bars"
                  />
                </Stack>
                <Stack gap={4}>
                  <Text size="xs" fw={500}>{tx('startAt')}</Text>
                  <SegmentedControl
                    size={touch ? 'md' : 'xs'}
                    fullWidth
                    data={[
                      { value: 'musicStart', label: tx('startAtMusicStart') },
                      { value: 'firstDownbeat', label: tx('startAtFirstDownbeat') },
                    ]}
                    value={startAt}
                    onChange={(value) => setStartAt(value === 'firstDownbeat' ? 'firstDownbeat' : 'musicStart')}
                    data-testid="cut-to-beat-start-at"
                  />
                </Stack>
                <Stack gap={2}>
                  <Switch
                    size={touch ? 'md' : 'xs'}
                    label={tx('strongestOnDrop')}
                    checked={strongestOnDrop && dropOffered}
                    onChange={(event) => setStrongestOnDrop(event.currentTarget.checked)}
                    disabled={!dropOffered}
                    styles={touch ? { body: { alignItems: 'center' }, label: { minHeight: TOUCH_TARGET, display: 'flex', alignItems: 'center' } } : undefined}
                    data-testid="cut-to-beat-drop"
                  />
                  {!hasRank && <Text size="xs" c="dimmed" data-testid="cut-to-beat-no-motion">{tx('noMotionNote')}</Text>}
                  {hasRank && dropSec === null && <Text size="xs" c="dimmed" data-testid="cut-to-beat-no-drop">{tx('noDropNote')}</Text>}
                </Stack>
                <Switch
                  size={touch ? 'md' : 'xs'}
                  label={tx('stopAtMusicEnd')}
                  checked={stopAtMusicEnd}
                  onChange={(event) => setStopAtMusicEnd(event.currentTarget.checked)}
                  styles={touch ? { body: { alignItems: 'center' }, label: { minHeight: TOUCH_TARGET, display: 'flex', alignItems: 'center' } } : undefined}
                  data-testid="cut-to-beat-stop-at-end"
                />
              </Stack>
            )}

            <Group justify="space-between" wrap="nowrap" align="flex-end">
              <Text size="xs" c="dimmed" data-testid="cut-to-beat-summary">{summary}</Text>
              <Group gap={touch ? 'sm' : 6} wrap="nowrap">
                <Button size={touch ? 'md' : 'compact-xs'} style={buttonStyle} variant="subtle" onClick={onClose} data-testid="cut-to-beat-cancel">
                  {tx('cancel')}
                </Button>
                {onAudition && (
                  <Button size={touch ? 'md' : 'compact-xs'} style={buttonStyle} variant="light" onClick={audition} disabled={!plan || plan.ops.length === 0 || readOnly} data-testid="cut-to-beat-audition">
                    {tx('playTheResult')}
                  </Button>
                )}
                <Button size={touch ? 'md' : 'compact-xs'} style={buttonStyle} onClick={apply} disabled={!plan || plan.ops.length === 0 || readOnly} data-testid="cut-to-beat-apply">
                  {tx('apply')}
                </Button>
              </Group>
            </Group>
          </>
        )}

        {/* No grid yet: the footer still offers a way out. */}
        {!analysis && (
          <Group justify="flex-end">
            <Button size={touch ? 'md' : 'compact-xs'} style={buttonStyle} variant="subtle" onClick={onClose} data-testid="cut-to-beat-cancel">
              {tx('cancel')}
            </Button>
          </Group>
        )}
      </Stack>
    </Modal>
  );
}
