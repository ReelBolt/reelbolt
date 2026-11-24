'use client';

import React, { useState } from 'react';
import { Alert, Badge, Box, Button, Checkbox, Group, ScrollArea, Select, Stack, Text, Tooltip } from '@mantine/core';
import { IconArrowDown, IconArrowUp, IconGripVertical } from '@tabler/icons-react';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline } from '@/lib/editing/types';
import { framesToTimecode } from '@/lib/editing/time';
import {
  compileMoveSpan,
  compileRippleCut,
  findFillerCandidates,
  readTranscript,
  sourceWindowToRecordSpans,
  wordRecordFrame,
  wordsToRecordSpans,
  type FillerCandidate,
  type TranscriptParagraph,
  type TranscriptWord,
} from '@/lib/editing/transcript/transcript';
import { TranscriptStart } from './TranscriptStart';
import { sourceMatcher, useTimelineAnalysis } from '@/lib/editing/api/analysis';
import { NeedsLargerScreen } from '../shell/LargerScreen';
import { useDeviceClass } from '@/lib/editing/use-device-class';
import { AnalysisHelpLink } from './AnalysisHelpLink';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { useFormat, useT } from '@/i18n';

export interface TranscriptPanelProps {
  doc: EditTimeline;
  projectId: string;
  /** The step result the timeline was seeded from; the analysis it was cut from carries the transcript. */
  seedStepResultId: string | null;
  readOnly: boolean;
  newItemId: () => string;
  /** Applies a compiled program as ONE history entry. */
  onApply: (ops: readonly EditOp[], label: string) => void;
  /** R09.3: play the result of the proposed cut in the viewer first; Apply/Cancel happen there. */
  onAudition?: (ops: readonly EditOp[], label: string) => void;
  onSeek: (frame: number) => void;
}

/**
 * Text-based editing (P6). The transcript is read from the seed analysis and laid out as
 * paragraphs of words; every word that is still on the chosen track is a button. Click selects a
 * word, shift-click extends the run, and **Delete words** ripple-cuts exactly those frames out of
 * the track in one undo step. **Find fillers** lists "um"/"uh" and stutters as checkboxes, each
 * individually rejectable before the batch is cut. Paragraphs can be dragged (or moved with the
 * arrows) to re-order the material on the track, which compiles to blades, extracts and inserts.
 */
export function TranscriptPanel({ doc, projectId, seedStepResultId, readOnly, newItemId, onApply, onAudition, onSeek }: TranscriptPanelProps) {
  const tx = useT('editor.panels.transcriptPanel');
  const nf = useFormat();
  const { data: analysis, isLoading, error } = useTimelineAnalysis(projectId, seedStepResultId, doc);
  // Paragraph drag is cut on a phone; the up and down arrows reorder instead.
  const phone = useDeviceClass() === 'phone';
  const fps = doc.settings.rate.den === 0 ? 30 : doc.settings.rate.num / doc.settings.rate.den;
  const transcript = readTranscript(analysis?.artifact);
  const matches = sourceMatcher(analysis, doc);

  const mediaTracks = doc.tracks.filter((t) => t.items.some((i) => i.type === 'mediaClip'));
  const [trackId, setTrackId] = useState<string | null>(null);
  const effectiveTrackId = trackId ?? mediaTracks[0]?.id ?? null;

  const [anchor, setAnchor] = useState<string | null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  const [fillersOpen, setFillersOpen] = useState(false);
  const [rejected, setRejected] = useState<Set<string>>(new Set());
  const [dragging, setDragging] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const words = transcript.words;
  const indexOf = new Map(words.map((w, i) => [w.id, i] as const));
  const onTrack = (word: TranscriptWord): boolean => effectiveTrackId !== null && wordRecordFrame(doc, effectiveTrackId, word, fps, matches) !== null;

  const selectWord = (word: TranscriptWord, extend: boolean) => {
    if (!onTrack(word)) return;
    if (extend && anchor !== null && indexOf.has(anchor)) {
      const a = indexOf.get(anchor) as number;
      const b = indexOf.get(word.id) as number;
      const [lo, hi] = a < b ? [a, b] : [b, a];
      setSelected(words.slice(lo, hi + 1).filter(onTrack).map((w) => w.id));
    } else {
      setAnchor(word.id);
      setSelected(selected.length === 1 && selected[0] === word.id ? [] : [word.id]);
      if (effectiveTrackId) {
        const frame = wordRecordFrame(doc, effectiveTrackId, word, fps, matches);
        if (frame !== null) onSeek(frame);
      }
    }
  };

  const selectedWords = words.filter((w) => selected.includes(w.id));
  const selectedSpans = effectiveTrackId ? wordsToRecordSpans(doc, effectiveTrackId, selectedWords, fps, matches) : [];
  const selectedFrames = selectedSpans.reduce((n, s) => n + s.endFrame - s.startFrame, 0);

  // R09.4: words whose timing was only estimated cannot be bulk-deleted without a yes from the user.
  const [guard, setGuard] = useState<{ kind: 'words' | 'fillers'; mode: 'apply' | 'audition'; count: number } | null>(null);
  const estimatedIn = (list: readonly TranscriptWord[]): number => list.filter((w) => w.estimated).length;

  const deleteSelected = (mode: 'apply' | 'audition' = 'apply', confirmed = false) => {
    if (!effectiveTrackId || selectedSpans.length === 0) return;
    const roughly = estimatedIn(selectedWords);
    if (roughly > 0 && !confirmed) {
      setGuard({ kind: 'words', mode, count: roughly });
      return;
    }
    setGuard(null);
    const plan = compileRippleCut(doc, { trackId: effectiveTrackId, spans: selectedSpans, newItemId });
    if (plan.ops.length === 0) return;
    const label = `Delete ${selectedWords.length} word${selectedWords.length === 1 ? '' : 's'} (${plan.removedFrames} f)`;
    if (mode === 'audition' && onAudition) {
      onAudition(plan.ops, label);
      return;
    }
    onApply(plan.ops, label);
    setSelected([]);
    setMessage(`${label}: ${plan.cuts} cut${plan.cuts === 1 ? '' : 's'}, rippled${plan.linkedCuts > 0 ? ', with the linked sound' : ''}.`);
  };

  // A plain derivation (the React compiler memoises it): only candidates whose words are still on the track.
  const fillers: FillerCandidate[] = fillersOpen ? findFillerCandidates(words).filter((c) => c.words.every(onTrack)) : [];
  const acceptedFillers = fillers.filter((c) => !rejected.has(c.id));
  const removeFillers = (mode: 'apply' | 'audition' = 'apply', confirmed = false) => {
    if (!effectiveTrackId || acceptedFillers.length === 0) return;
    const roughly = estimatedIn(acceptedFillers.flatMap((c) => c.words));
    if (roughly > 0 && !confirmed) {
      setGuard({ kind: 'fillers', mode, count: roughly });
      return;
    }
    setGuard(null);
    const spans = wordsToRecordSpans(doc, effectiveTrackId, acceptedFillers.flatMap((c) => c.words), fps, matches);
    const plan = compileRippleCut(doc, { trackId: effectiveTrackId, spans, newItemId });
    if (plan.ops.length === 0) return;
    const fillerLabel = `Remove ${acceptedFillers.length} filler${acceptedFillers.length === 1 ? '' : 's'} (${plan.removedFrames} f)`;
    if (mode === 'audition' && onAudition) {
      onAudition(plan.ops, fillerLabel);
      return;
    }
    onApply(plan.ops, fillerLabel);
    setMessage(`Removed ${acceptedFillers.length} filler${acceptedFillers.length === 1 ? '' : 's'} in ${plan.cuts} cut${plan.cuts === 1 ? '' : 's'}.`);
    setRejected(new Set());
    setFillersOpen(false);
  };

  /** A paragraph's contiguous record span on the track, or null when it is split or absent. */
  const paragraphSpan = (p: TranscriptParagraph): { startFrame: number; endFrame: number } | null => {
    if (!effectiveTrackId) return null;
    const spans = sourceWindowToRecordSpans(doc, effectiveTrackId, { startSec: p.startSec, endSec: p.endSec, sourceIndex: p.sourceIndex }, fps, matches);
    if (spans.length !== 1) return null;
    return spans[0];
  };

  const moveParagraph = (from: TranscriptParagraph, to: TranscriptParagraph, before: boolean) => {
    if (!effectiveTrackId || readOnly) return;
    const fromSpan = paragraphSpan(from);
    const toSpan = paragraphSpan(to);
    if (!fromSpan || !toSpan) {
      setMessage(tx('thatParagraphIsNot'));
      return;
    }
    const plan = compileMoveSpan(doc, { trackId: effectiveTrackId, from: fromSpan, toFrame: before ? toSpan.startFrame : toSpan.endFrame, newItemId });
    if (plan.refusal) {
      setMessage(plan.refusal);
      return;
    }
    onApply(plan.ops, tx('moveParagraphF', { v1: fromSpan.endFrame - fromSpan.startFrame }));
    setMessage(tx('paragraphMovedTo', { v1: framesToTimecode(plan.landedAt ?? 0, doc.settings.rate) }));
  };

  const paragraphs = transcript.paragraphs;
  const orderedOnTrack = paragraphs
    .map((p) => ({ p, span: paragraphSpan(p) }))
    .filter((x) => x.span !== null)
    .sort((a, b) => (a.span as { startFrame: number }).startFrame - (b.span as { startFrame: number }).startFrame);

  return (
    <Stack gap="xs" data-testid="transcript-panel">
      <Group justify="space-between" align="flex-end">
        <Text size="sm" fw={600}>
          {tx('transcript')}
        </Text>
        <Select
          size="xs"
          label={tx('track')}
          data={mediaTracks.map((t) => ({ value: t.id, label: t.name }))}
          value={effectiveTrackId}
          onChange={setTrackId}
          allowDeselect={false}
          w={140}
          data-testid="transcript-track"
        />
      </Group>

      {seedStepResultId === null && !analysis && !isLoading && (
        <Alert color="yellow" p="xs" title={tx('noTranscriptToRead')}>
          {tx('thisTimelineWasNot')}
        </Alert>
      )}
      {seedStepResultId === null && !analysis && !isLoading && <AnalysisHelpLink projectId={projectId} />}
      {error && <Alert color="red" p="xs" title={tx('theAnalysisCouldNot')}>{humanizeErrorT(tx, error, tx('tryAgainInA'))}</Alert>}
      {isLoading && <Text size="xs" c="dimmed">{tx('readingTheAnalysis')}</Text>}
      {!isLoading && !error && words.length === 0 && (
        <TranscriptStart doc={doc} projectId={projectId} readOnly={readOnly} emptyText={tx('thereIsNoTranscript')} />
      )}
      {transcript.hasEstimatedWords && (
        <Text size="12px" c="dimmed">{tx('estimateNote')}</Text>
      )}

      <Group gap={6}>
        <Tooltip label={tx('rippleCutTheSelected')}>
          <Button size="compact-xs" fz="xs" color="red" variant="light" disabled={readOnly || selectedSpans.length === 0} onClick={() => deleteSelected('apply')} data-testid="transcript-delete">
            {tx('deleteWords')} {selectedFrames > 0 ? `(${selectedFrames} f)` : ''}
          </Button>
        </Tooltip>
        {onAudition && (
          <Button size="compact-xs" fz="xs" variant="light" disabled={readOnly || selectedSpans.length === 0} onClick={() => deleteSelected('audition')} data-testid="transcript-audition">
            {tx('playTheResult')}
          </Button>
        )}
        <Button size="compact-xs" fz="xs" variant={fillersOpen ? 'filled' : 'light'} disabled={readOnly || words.length === 0} onClick={() => setFillersOpen((o) => !o)} data-testid="transcript-fillers-toggle">
          {fillersOpen ? tx('hideFillers') : tx('findFillers')}
        </Button>
        {selected.length > 0 && <Text size="xs" c="dimmed">{selected.length} {tx('selectedShiftClickExtends')}</Text>}
      </Group>

      {guard && (
        <Alert color="orange" p="xs" title={tx('someOfTheseWords')} data-testid="transcript-estimate-guard">
          <Stack gap={6}>
            <Text size="xs">
              {tx('guardMessage', { count: guard.count, kind: guard.kind })}
            </Text>
            <Group gap="xs">
              <Button size="compact-xs" fz="xs" color="orange" onClick={() => (guard.kind === 'words' ? deleteSelected(guard.mode, true) : removeFillers(guard.mode, true))} data-testid="transcript-estimate-confirm">
                {tx('cutAnyway')}
              </Button>
              <Button size="compact-xs" fz="xs" variant="subtle" onClick={() => setGuard(null)} data-testid="transcript-estimate-cancel">
                {tx('keepThem')}
              </Button>
            </Group>
          </Stack>
        </Alert>
      )}

      {fillersOpen && (
        <Box p="xs" style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 4 }} data-testid="transcript-fillers">
          {fillers.length === 0 ? (
            <Text size="xs" c="dimmed">{tx('noFillerWordsOr')}</Text>
          ) : (
            <Stack gap={4}>
              {fillers.map((c) => (
                <Checkbox
                  key={c.id}
                  size="xs"
                  checked={!rejected.has(c.id)}
                  onChange={(e) => {
                    const next = new Set(rejected);
                    if (e.currentTarget.checked) next.delete(c.id);
                    else next.add(c.id);
                    setRejected(next);
                  }}
                  label={tx('fillerLabel', { kind: c.kind, text: c.text, seconds: nf.number(c.startSec, { minimumFractionDigits: 2, maximumFractionDigits: 2 }) })}
                  data-testid={`transcript-filler-${c.id}`}
                />
              ))}
              <Group justify="flex-end">
                {onAudition && (
                  <Button size="compact-xs" fz="xs" variant="light" disabled={acceptedFillers.length === 0} onClick={() => removeFillers('audition')} data-testid="transcript-fillers-audition">
                    {tx('playTheResult2')}
                  </Button>
                )}
                <Button size="compact-xs" fz="xs" color="red" disabled={acceptedFillers.length === 0} onClick={() => removeFillers('apply')} data-testid="transcript-fillers-apply">
                  {tx('removeOf', { length: acceptedFillers.length, length2: fillers.length })}
                </Button>
              </Group>
            </Stack>
          )}
        </Box>
      )}

      {phone && paragraphs.length > 1 ? (
        <NeedsLargerScreen feature="paragraph-drag" what="Dragging paragraphs to reorder them" hint={tx('useTheUpAnd')} />
      ) : null}
      <ScrollArea.Autosize mah={420}>
        <Stack gap="sm">
          {paragraphs.map((p) => {
            const span = paragraphSpan(p);
            const position = orderedOnTrack.findIndex((x) => x.p.id === p.id);
            const previous = position > 0 ? orderedOnTrack[position - 1].p : null;
            const next = position >= 0 && position < orderedOnTrack.length - 1 ? orderedOnTrack[position + 1].p : null;
            return (
              <Box
                key={p.id}
                data-testid={`transcript-paragraph-${p.id}`}
                draggable={!phone && !readOnly && span !== null}
                onDragStart={() => setDragging(p.id)}
                onDragEnd={() => setDragging(null)}
                onDragOver={(e) => {
                  if (dragging && dragging !== p.id) e.preventDefault();
                }}
                onDrop={(e) => {
                  e.preventDefault();
                  const from = paragraphs.find((x) => x.id === dragging);
                  if (from && from.id !== p.id) moveParagraph(from, p, true);
                  setDragging(null);
                }}
                style={{ opacity: dragging === p.id ? 0.5 : 1, borderLeft: `2px solid ${span ? 'var(--mantine-color-violet-5)' : 'var(--mantine-color-default-border)'}`, paddingLeft: 6 }}
              >
                <Group gap={4} wrap="nowrap" mb={2}>
                  {phone ? null : <IconGripVertical size={12} style={{ opacity: span ? 1 : 0.3 }} />}
                  <Text size="12px" c="dimmed" ff="monospace">
                    {span ? `${framesToTimecode(span.startFrame, doc.settings.rate)} → ${framesToTimecode(span.endFrame, doc.settings.rate)}` : tx('notOnThisTrack')}
                  </Text>
                  {span && previous && (
                    <Tooltip label={tx('moveThisParagraphBefore')}>
                      <Button size="compact-xs" fz="xs" variant="subtle" disabled={readOnly} onClick={() => moveParagraph(p, previous, true)} aria-label={tx('moveParagraphUp', { id: p.id })} data-testid={`transcript-move-up-${p.id}`}>
                        <IconArrowUp size={12} />
                      </Button>
                    </Tooltip>
                  )}
                  {span && next && (
                    <Tooltip label={tx('moveThisParagraphAfter')}>
                      <Button size="compact-xs" fz="xs" variant="subtle" disabled={readOnly} onClick={() => moveParagraph(p, next, false)} aria-label={tx('moveParagraphDown', { id: p.id })} data-testid={`transcript-move-down-${p.id}`}>
                        <IconArrowDown size={12} />
                      </Button>
                    </Tooltip>
                  )}
                </Group>
                <Box style={{ lineHeight: 1.9 }}>
                  {p.words.map((w) => {
                    const present = onTrack(w);
                    const isSelected = selected.includes(w.id);
                    return (
                      <Badge
                        key={w.id}
                        component="button"
                        size="sm" fz="xs"
                        radius="sm"
                        variant={isSelected ? 'filled' : present ? 'light' : 'outline'}
                        color={isSelected ? 'red' : present ? 'gray' : 'gray'}
                        style={{ cursor: present ? 'var(--rb-cursor-pointer)' : 'var(--rb-cursor-not-allowed)', textTransform: 'none', fontWeight: 400, marginRight: 3, opacity: present ? 1 : 0.4 }}
                        onClick={(e: React.MouseEvent) => selectWord(w, e.shiftKey)}
                        disabled={!present}
                        aria-pressed={isSelected}
                        data-testid={`transcript-word-${w.id}`}
                        data-on-track={present}
                      >
                        {w.text}
                        {w.estimated ? '≈' : '' /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
                      </Badge>
                    );
                  })}
                </Box>
              </Box>
            );
          })}
        </Stack>
      </ScrollArea.Autosize>
      {message && (
        <Text size="xs" c="dimmed" role="status" data-testid="transcript-message">
          {message}
        </Text>
      )}
    </Stack>
  );
}
