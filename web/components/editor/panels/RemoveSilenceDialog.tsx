'use client';

import React, { useState } from 'react';
import { Alert, Button, Checkbox, Group, Modal, NumberInput, ScrollArea, Select, SimpleGrid, Stack, Text, Tooltip } from '@mantine/core';
import { useIsTouchSurface } from '@/lib/editing/use-device-class';
import { IconWaveSine } from '@tabler/icons-react';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline, MediaClip } from '@/lib/editing/types';
import { framesToTimecode } from '@/lib/editing/time';
import { compileRemoveSilence, mapSourceSilenceToRecord, type RecordSpan, type SourceSilenceSpan } from '@/lib/editing/silence/remove-silence';
import { sourceMatcher, useTimelineAnalysis } from '@/lib/editing/api/analysis';
import { AnalysisHelpLink } from './AnalysisHelpLink';
import { EditorIcon } from '../ui';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { useFormat, useT } from '@/i18n';

export interface RemoveSilenceDialogProps {
  opened: boolean;
  onClose: () => void;
  doc: EditTimeline;
  projectId: string;
  /** The step result the timeline was seeded from; the analysis it was cut from carries the silence gaps. */
  seedStepResultId: string | null;
  /** Fresh item ids for the blades. */
  newItemId: () => string;
  /** Applies the compiled program as ONE history entry. */
  onApply: (ops: readonly EditOp[], label: string) => void;
  /** R09.3: play the result of the proposed cuts in the viewer first; Apply/Cancel happen there. */
  onAudition?: (ops: readonly EditOp[], label: string) => void;
}

/** A finger-sized target (44 px) for every control on a coarse pointer or a phone width. */
const TOUCH_TARGET = 44;

interface SpanRow {
  id: string;
  startSec: number;
  endSec: number;
  record: RecordSpan | null;
}

/** The `silenceSpans` of a VideoAnalyze artifact, read defensively: anything unreadable is simply not offered. */
export function readSilenceSpans(artifact: unknown): SourceSilenceSpan[] {
  if (typeof artifact !== 'object' || artifact === null) return [];
  const raw = (artifact as Record<string, unknown>).silenceSpans ?? (artifact as Record<string, unknown>).SilenceSpans;
  if (!Array.isArray(raw)) return [];
  const spans: SourceSilenceSpan[] = [];
  raw.forEach((entry, index) => {
    if (typeof entry !== 'object' || entry === null) return;
    const e = entry as Record<string, unknown>;
    const startSec = typeof e.startSec === 'number' ? e.startSec : typeof e.StartSec === 'number' ? e.StartSec : null;
    const endSec = typeof e.endSec === 'number' ? e.endSec : typeof e.EndSec === 'number' ? e.EndSec : null;
    if (startSec === null || endSec === null || endSec <= startSec) return;
    const id = typeof e.id === 'string' ? e.id : typeof e.Id === 'string' ? e.Id : `g${index}`;
    const source = typeof e.sourceIndex === 'number' ? e.sourceIndex : typeof e.SourceIndex === 'number' ? e.SourceIndex : 0;
    spans.push({ id, startSec, endSec, sourceIndex: source });
  });
  return spans;
}

/**
 * The timeline toolbar's entry to the dialog (tablet and desktop). The phone reaches the same dialog from the
 * Captions sheet (RM.12), since the phone has no timeline toolbar.
 */
export function RemoveSilenceButton({ disabled, onOpen, iconOnly = false }: { disabled: boolean; onOpen: () => void; /** The toolbar is out of room: the icon alone, named. */ iconOnly?: boolean }) {
  const tx = useT('editor.panels.removeSilenceDialog');
  if (iconOnly) {
    return <EditorIcon icon={IconWaveSine} label={tx('removeSilence')} tooltip={tx('reviewTheMeasuredSilences')} onClick={onOpen} disabled={disabled} data-testid="remove-silence-open" />;
  }
  return (
    <Tooltip label={tx('reviewTheMeasuredSilences')} openDelay={300}>
      <Button size="compact-sm" fz="xs" variant="subtle" color="gray" onClick={onOpen} leftSection={<IconWaveSine size={15} stroke={1.7} />} data-testid="remove-silence-open" disabled={disabled} fw={500}>
        {tx('removeSilence')}
      </Button>
    </Tooltip>
  );
}

/**
 * Remove silence as a reviewable dialog (P4): the analysis artifact's silence gaps are mapped onto
 * the chosen track's clips, listed with checkboxes and timecodes, padded and length-filtered, and
 * the resulting cut list is shown BEFORE anything changes. Apply dispatches the compiled blades and
 * extracts as one undo step. No gap is ever cut without being listed here first.
 */
export function RemoveSilenceDialog({ opened, onClose, doc, projectId, seedStepResultId, newItemId, onApply, onAudition }: RemoveSilenceDialogProps) {
  const tx = useT('editor.panels.removeSilenceDialog');
  const nf = useFormat();
  const { data: analysis, isLoading, error } = useTimelineAnalysis(projectId, opened ? seedStepResultId : null, opened ? doc : undefined);
  const artifact = analysis?.artifact;
  const matches = sourceMatcher(analysis, doc);
  const fps = doc.settings.rate.den === 0 ? 30 : doc.settings.rate.num / doc.settings.rate.den;
  const touch = useIsTouchSurface();
  const inputStyles = touch ? { input: { minHeight: TOUCH_TARGET }, option: { minHeight: TOUCH_TARGET } } : undefined;
  const buttonStyle = touch ? { minHeight: TOUCH_TARGET, minWidth: TOUCH_TARGET } : undefined;

  const mediaTracks = doc.tracks.filter((t) => t.items.some((i) => i.type === 'mediaClip'));
  const [trackId, setTrackId] = useState<string | null>(null);
  const effectiveTrackId = trackId ?? mediaTracks[0]?.id ?? null;
  const [pad, setPad] = useState(2);
  const [minFrames, setMinFrames] = useState(6);
  const [excluded, setExcluded] = useState<Set<string>>(new Set());

  // Plain derivations (the React compiler memoises them): the spans mapped onto this track's clips.
  const track = doc.tracks.find((t) => t.id === effectiveTrackId);
  const clips = track ? track.items.filter((i): i is MediaClip => i.type === 'mediaClip') : [];
  const rows: SpanRow[] = track
    ? readSilenceSpans(artifact).map((span) => {
      // The first clip of the silence's own file it lands in supplies the record mapping; a
      // silence spanning a cut is listed once per the clip it starts in.
      let record: RecordSpan | null = null;
      for (const c of clips) {
        record = mapSourceSilenceToRecord(c, span, fps, matches);
        if (record) break;
      }
      return { id: span.id, startSec: span.startSec, endSec: span.endSec, record };
    })
    : [];

  const selectedSpans = rows.filter((r) => r.record && !excluded.has(r.id)).map((r) => r.record as RecordSpan);
  let previewCounter = 0;
  const plan = effectiveTrackId
    ? compileRemoveSilence(doc, { trackId: effectiveTrackId, spans: selectedSpans, padFrames: pad, minFrames, newItemId: () => `preview-${++previewCounter}` })
    : null;

  const apply = () => {
    if (!effectiveTrackId) return;
    const real = compileRemoveSilence(doc, { trackId: effectiveTrackId, spans: selectedSpans, padFrames: pad, minFrames, newItemId });
    if (real.ops.length === 0) return;
    onApply(real.ops, tx('removeSilenceOther', { cuts: real.cuts }));
    onClose();
  };

  const audition = () => {
    if (!effectiveTrackId || !onAudition) return;
    const real = compileRemoveSilence(doc, { trackId: effectiveTrackId, spans: selectedSpans, padFrames: pad, minFrames, newItemId });
    if (real.ops.length === 0) return;
    onAudition(real.ops, tx('removeSilenceOther2', { cuts: real.cuts }));
  };
  const noAnalysis = !isLoading && !error && rows.length === 0 && (analysis === null || analysis === undefined);
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={tx('removeSilence2')}
      size="lg"
      fullScreen={touch}
      closeButtonProps={{ size: touch ? TOUCH_TARGET : undefined, 'aria-label': 'Close' }}
      data-testid="remove-silence-dialog"
    >
      <Stack gap="sm">
        {noAnalysis && (
          <Alert color="yellow" title={tx('noAnalysisToRead')} data-testid="remove-silence-no-analysis">
            {tx('nothingHasBeenAnalysed')}
          </Alert>
        )}
        {noAnalysis && seedStepResultId === null && <AnalysisHelpLink projectId={projectId} />}
        {error && <Alert color="red" title={tx('theAnalysisCouldNot')}>{humanizeErrorT(tx, error, tx('tryAgainInA'))}</Alert>}
        <SimpleGrid cols={touch ? 1 : 3}>
          <Select
            label={tx('track')}
            size={touch ? 'md' : 'xs'}
            styles={inputStyles}
            data={mediaTracks.map((t) => ({ value: t.id, label: t.name }))}
            value={effectiveTrackId}
            onChange={setTrackId}
            allowDeselect={false}
            data-testid="remove-silence-track"
          />
          <NumberInput label={tx('keepOnEachSide')} size={touch ? 'md' : 'xs'} styles={inputStyles} min={0} max={60} value={pad} onChange={(v) => setPad(typeof v === 'number' ? v : 0)} data-testid="remove-silence-pad" />
          <NumberInput label={tx('ignoreGapsShorterThan')} size={touch ? 'md' : 'xs'} styles={inputStyles} min={1} max={300} value={minFrames} onChange={(v) => setMinFrames(typeof v === 'number' ? v : 1)} data-testid="remove-silence-min" />
        </SimpleGrid>
        <ScrollArea.Autosize mah={260}>
          <Stack gap={4}>
            {isLoading && <Text size="xs" c="dimmed">{tx('readingTheAnalysis')}</Text>}
            {!isLoading && rows.length === 0 && analysis && !error && (
              <Text size="xs" c="dimmed" data-testid="remove-silence-none">{tx('theAnalysisListsNo')}</Text>
            )}
            {rows.map((row) => (
              <Checkbox
                key={row.id}
                size={touch ? 'md' : 'xs'}
                // The label is the checkbox's click target: finger-sized on touch.
                styles={touch ? { body: { alignItems: 'center' }, label: { minHeight: TOUCH_TARGET, display: 'flex', alignItems: 'center' } } : undefined}
                checked={row.record !== null && !excluded.has(row.id)}
                disabled={row.record === null}
                onChange={(e) => {
                  const next = new Set(excluded);
                  if (e.currentTarget.checked) next.delete(row.id);
                  else next.add(row.id);
                  setExcluded(next);
                }}
                label={
                  row.record
                    ? `${row.id} · ${framesToTimecode(row.record.startFrame, doc.settings.rate)} → ${framesToTimecode(row.record.endFrame, doc.settings.rate)} (${row.record.endFrame - row.record.startFrame} f)`
                    : tx('sToSNot', { id: row.id, v2: nf.number(row.startSec, { minimumFractionDigits: 2, maximumFractionDigits: 2 }), v3: nf.number(row.endSec, { minimumFractionDigits: 2, maximumFractionDigits: 2 }) })
                }
                data-testid={`remove-silence-span-${row.id}`}
              />
            ))}
          </Stack>
        </ScrollArea.Autosize>
        <Group justify="space-between">
          <Text size="xs" c="dimmed" data-testid="remove-silence-summary">
            {plan ? tx('summary', { cuts: plan.cuts, frames: plan.removedFrames, linked: plan.linkedCuts }) : tx('chooseATrack')}
          </Text>
          {/* Neutral and destructive actions stay >= 8 px apart (sm = 12 px) and finger-sized on touch. */}
          <Group gap={touch ? 'sm' : 6}>
            <Button size={touch ? 'md' : 'compact-xs'} style={buttonStyle} variant="subtle" onClick={onClose} data-testid="remove-silence-cancel">
              {tx('cancel')}
            </Button>
            {onAudition && (
              <Button size={touch ? 'md' : 'compact-xs'} style={buttonStyle} variant="light" onClick={audition} disabled={!plan || plan.ops.length === 0} data-testid="remove-silence-audition">
                {tx('playTheResult')}
              </Button>
            )}
            <Button size={touch ? 'md' : 'compact-xs'} style={buttonStyle} onClick={apply} disabled={!plan || plan.ops.length === 0} data-testid="remove-silence-apply">
              {tx('remove')}
            </Button>
          </Group>
        </Group>
      </Stack>
    </Modal>
  );
}
