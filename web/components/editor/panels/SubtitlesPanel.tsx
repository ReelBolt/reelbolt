'use client';

import { OverlayTextInput } from './OverlayTextInput';
import React, { useState } from 'react';
import { Alert, Anchor, Button, Divider, Group, NumberInput, ScrollArea, SegmentedControl, Select, Stack, Switch, Text, TextInput, Textarea, Tooltip } from '@mantine/core';
import { useIsTouchSurface } from '@/lib/editing/use-device-class';
import { IconArrowMerge, IconChevronLeft, IconChevronRight, IconPlus, IconScissors } from '@tabler/icons-react';
import type { EditOp, PropertyKey, PropertyValue } from '@/lib/editing/engine';
import type { CaptionLook, EditTimeline, TextPosition } from '@/lib/editing/types';
import { framesToTimecode } from '@/lib/editing/time';
import { buildCues, CAPTION_LOOK_ORDER, compileAddCaptionAt, compileGenerateCaptions, compileMergeCaptionWithNext, compileNudgeCaption, compileSplitCaption, defaultCaptionStyle, findCaptionTrack, listCaptions, type TimedWord } from '@/lib/editing/subtitles/subtitles';
import { readTranscript, sourceWindowToRecordSpans } from '@/lib/editing/transcript/transcript';
import { editTimelineExportUrl } from '@/lib/editing/sync/api';
import { sourceMatcher, useTimelineAnalysis } from '@/lib/editing/api/analysis';
import { AnalysisHelpLink } from './AnalysisHelpLink';
import { CaptionLookPicker } from './CaptionLookPicker';
import { SubtitleImportDialog } from './SubtitleImportDialog';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { EditorIcon } from '../ui';
import { useT } from '@/i18n';
import { captionLookLabelMsg } from '@/lib/editing/labels-msg';

export interface SubtitlesPanelProps {
  doc: EditTimeline;
  projectId: string;
  timelineId: string;
  seedStepResultId: string | null;
  readOnly: boolean;
  playheadFrame: number;
  newItemId: () => string;
  newTrackId: () => string;
  onApply: (ops: readonly EditOp[], label: string) => void;
  onSetProperty: (itemId: string, key: PropertyKey, value: PropertyValue) => void;
  onSeek: (frame: number) => void;
}

/**
 * Subtitles (P6). Generates `Caption` items from the transcript (words mapped onto the chosen
 * media track, packed into cues under a characters-per-line and lines-per-cue budget), edits each
 * caption's text in place, splits one at the playhead and merges one with the next. Captions are
 * ordinary timeline items: the render burns in every enabled caption on an enabled track, and the
 * SRT/VTT sidecar links export every enabled caption — disable the subtitle track for sidecar-only.
 */
export function SubtitlesPanel({ doc, projectId, timelineId, seedStepResultId, readOnly, playheadFrame, newItemId, newTrackId, onApply, onSetProperty, onSeek }: SubtitlesPanelProps) {
  const tx = useT('editor.panels.subtitlesPanel');
  const { data: analysis, isLoading, error } = useTimelineAnalysis(projectId, seedStepResultId, doc);
  const touch = useIsTouchSurface();
  const fps = doc.settings.rate.den === 0 ? 30 : doc.settings.rate.num / doc.settings.rate.den;
  const transcript = readTranscript(analysis?.artifact);
  const matches = sourceMatcher(analysis, doc);
  const existingCaptionTrack = findCaptionTrack(doc);

  const mediaTracks = doc.tracks.filter((t) => t.items.some((i) => i.type === 'mediaClip'));
  const videoTracks = doc.tracks.filter((t) => t.kind === 'video');
  const [sourceTrackId, setSourceTrackId] = useState<string | null>(null);
  const [targetTrackId, setTargetTrackId] = useState<string>('__new__');
  const [maxChars, setMaxChars] = useState(42);
  const [maxLines, setMaxLines] = useState(2);
  const [language, setLanguage] = useState('en');
  const [message, setMessage] = useState<string | null>(null);
  // R09.6: the look is chosen per picture shape; the default follows the project's aspect until the user picks.
  const aspectDefault = defaultCaptionStyle(doc.settings.width, doc.settings.height);
  const [lookChoice, setLookChoice] = useState<CaptionLook | null>(null);
  const [positionChoice, setPositionChoice] = useState<TextPosition | null>(null);
  const look = lookChoice ?? aspectDefault.look;
  const position = positionChoice ?? aspectDefault.position;
  const [newText, setNewText] = useState('');
  const [nudgeEdge, setNudgeEdge] = useState<'start' | 'end' | 'both'>('both');
  const [nudgeFrames, setNudgeFrames] = useState(3);
  const [importOpen, setImportOpen] = useState(false);
  const effectiveSource = sourceTrackId ?? mediaTracks[0]?.id ?? null;

  const timedWords: TimedWord[] = (() => {
    if (!effectiveSource) return [];
    const out: TimedWord[] = [];
    for (const w of transcript.words) {
      const spans = sourceWindowToRecordSpans(doc, effectiveSource, { startSec: w.startSec, endSec: Math.max(w.endSec, w.startSec + 1 / fps), sourceIndex: w.sourceIndex }, fps, matches);
      if (spans.length > 0) out.push({ startFrame: spans[0].startFrame, endFrame: spans[0].endFrame, text: w.text });
    }
    return out;
  })();

  const cues = buildCues(timedWords, fps, { maxCharsPerLine: maxChars, maxLines });

  const generate = () => {
    const plan = compileGenerateCaptions(doc, { cues, trackId: targetTrackId === '__new__' ? null : targetTrackId, trackName: 'Subtitles', language, look, position, newItemId, newTrackId });
    if (plan.ops.length === 0) {
      setMessage(plan.skipped > 0 ? tx('theCaptionsAreAlready', { skipped: plan.skipped }) : tx('nothingToGenerateNo'));
      return;
    }
    onApply(plan.ops, tx('generateLabel', { count: plan.inserted }));
    setMessage(tx('captionsAdded', { inserted: plan.inserted, skipped: plan.skipped }));
  };

  const captions = listCaptions(doc);

  const addAtPlayhead = () => {
    const plan = compileAddCaptionAt(doc, { atFrame: playheadFrame, text: newText, trackId: targetTrackId === '__new__' ? null : targetTrackId, language, look, position, newItemId, newTrackId });
    if (plan.refusal) {
      setMessage(plan.refusal);
      return;
    }
    onApply(plan.ops, tx('addCaption'));
    setNewText('');
    setMessage(tx('captionAddedAt', { v1: framesToTimecode(playheadFrame, doc.settings.rate) }));
  };
  const nudge = (captionId: string, direction: -1 | 1) => {
    const plan = compileNudgeCaption(doc, captionId, nudgeEdge, direction * Math.max(1, nudgeFrames));
    if (plan.refusal) {
      setMessage(plan.refusal);
      return;
    }
    onApply(plan.ops, `Nudge caption ${direction < 0 ? 'earlier' : 'later'}`);
  };
  const applyLookToAll = () => {
    const ops: EditOp[] = [];
    for (const { caption } of captions) {
      ops.push({ type: 'setProperty', itemId: caption.id, key: 'look', value: look });
      ops.push({ type: 'setProperty', itemId: caption.id, key: 'position', value: position });
    }
    if (ops.length === 0) return;
    onApply(ops, tx('captionLook2', { v1: tx.msg(captionLookLabelMsg(look)) }));
    setMessage(tx('theLookIsNow', { v1: tx.msg(captionLookLabelMsg(look)).toLocaleLowerCase(), length: captions.length }));
  };
  const lastLook = (c: { look?: CaptionLook | null }): CaptionLook => c.look ?? 'clean';

  return (
    <Stack gap="xs" data-testid="subtitles-panel">
      <Text size="sm" fw={600}>
        {tx('subtitles')}
      </Text>

      {!isLoading && !error && transcript.words.length === 0 && (
        <Alert color="yellow" p="xs" title={tx('noTranscriptToGenerate')} data-testid="subtitles-no-transcript">
          {tx('openTheAnalyseTab')}
        </Alert>
      )}
      {seedStepResultId === null && !analysis && !isLoading && <AnalysisHelpLink projectId={projectId} />}
      {error && <Alert color="red" p="xs" title={tx('theAnalysisCouldNot')}>{humanizeErrorT(tx, error, tx('tryAgainInA'))}</Alert>}

      <Group grow align="flex-end">
        <Select size="xs" label={tx('speechFrom')} data={mediaTracks.map((t) => ({ value: t.id, label: t.name }))} value={effectiveSource} onChange={setSourceTrackId} allowDeselect={false} data-testid="subtitles-source-track" />
        <Select
          size="xs"
          label={tx('putCaptionsOn')}
          data={[{ value: '__new__', label: existingCaptionTrack ? tx('captionsTrack', { name: existingCaptionTrack.name }) : tx('newTrackSubtitles') }, ...videoTracks.filter((t) => t.id !== existingCaptionTrack?.id).map((t) => ({ value: t.id, label: t.name }))]}
          value={targetTrackId}
          onChange={(v) => setTargetTrackId(v ?? '__new__')}
          allowDeselect={false}
          data-testid="subtitles-target-track"
        />
      </Group>
      <Group grow align="flex-end">
        <NumberInput size="xs" label={tx('maxCharactersLine')} min={16} max={80} value={maxChars} onChange={(v) => setMaxChars(typeof v === 'number' ? v : 42)} data-testid="subtitles-max-chars" />
        <NumberInput size="xs" label={tx('linesCue')} min={1} max={3} value={maxLines} onChange={(v) => setMaxLines(typeof v === 'number' ? v : 2)} data-testid="subtitles-max-lines" />
        <TextInput size="xs" label={tx('language')} value={language} onChange={(e) => setLanguage(e.currentTarget.value)} maxLength={8} data-testid="subtitles-language" />
      </Group>
      <CaptionLookPicker look={look} position={position} onLook={setLookChoice} onPosition={setPositionChoice} disabled={readOnly} touch={touch} />
      <Group justify="space-between">
        <Text size="xs" c="dimmed" data-testid="subtitles-preview-count">
          {isLoading ? tx('readingTheAnalysis') : tx('otherFromOtherOn', { length: cues.length, length2: timedWords.length })}
        </Text>
        <Button size={touch ? 'md' : 'compact-xs'} fz="xs" style={touch ? { minHeight: 44 } : undefined} disabled={readOnly || cues.length === 0} onClick={generate} data-testid="subtitles-generate">
          {tx('generateCaptions')}
        </Button>
      </Group>

      <Divider label={tx('addByHand')} labelPosition="left" />
      <Textarea size="xs" rows={2} placeholder={tx('typeACaptionPress')} value={newText} onChange={(e) => setNewText(e.currentTarget.value)} disabled={readOnly} data-testid="subtitles-new-text" aria-label={tx('newCaptionText')} />
      <Group gap="xs">
        <Button size={touch ? 'md' : 'compact-xs'} fz="xs" style={touch ? { minHeight: 44 } : undefined} leftSection={<IconPlus size={14} />} disabled={readOnly || newText.trim().length === 0} onClick={addAtPlayhead} data-testid="subtitles-add">
          {tx('addAtThePlayhead')}
        </Button>
        <Button size={touch ? 'md' : 'compact-xs'} fz="xs" style={touch ? { minHeight: 44 } : undefined} variant="light" disabled={readOnly} onClick={() => setImportOpen(true)} data-testid="subtitles-import">
          {tx('importSrtOrWebvtt')}
        </Button>
      </Group>

      <Divider label={tx('captionsOnTheTimeline', { length: captions.length })} labelPosition="left" />
      {captions.length > 0 && (
        <Group gap="xs" align="flex-end" wrap="wrap">
          <div>
            <Text size="xs" fw={500} mb={2}>
              {tx('nudgeMoves')}
            </Text>
            <SegmentedControl size={touch ? 'md' : 'xs'} data={[{ value: 'start', label: tx('start') }, { value: 'end', label: tx('end') }, { value: 'both', label: tx('wholeCaption') }]} value={nudgeEdge} onChange={(v) => setNudgeEdge(v as 'start' | 'end' | 'both')} data-testid="subtitles-nudge-edge" />
          </div>
          <NumberInput size={touch ? 'md' : 'xs'} label={tx('frames')} min={1} max={300} w={72} value={nudgeFrames} onChange={(v) => setNudgeFrames(typeof v === 'number' ? v : 3)} data-testid="subtitles-nudge-frames" />
          <Button size={touch ? 'md' : 'compact-xs'} fz="xs" style={touch ? { minHeight: 44 } : undefined} variant="light" disabled={readOnly} onClick={applyLookToAll} data-testid="subtitles-apply-look">
            {tx('useThisLookOn')}
          </Button>
        </Group>
      )}
      {captions.length === 0 ? (
        <Text size="xs" c="dimmed" data-testid="subtitles-none">{tx('noCaptionsYetGenerate')}</Text>
      ) : (
        <ScrollArea.Autosize mah={320}>
          <Stack gap={4}>
            {captions.map(({ caption, track }, index) => {
              const next = captions[index + 1];
              const mergeable = next !== undefined && next.track.id === track.id && track.items.indexOf(next.caption) === track.items.indexOf(caption) + 1;
              const splittable = playheadFrame > caption.recordIn && playheadFrame < caption.recordOut && caption.text.split(/\s+/).filter(Boolean).length >= 2;
              return (
                <Group key={caption.id} gap={6} wrap="nowrap" align="flex-start" data-testid={`subtitles-row-${caption.id}`}>
                  <Button size="compact-xs" fz="xs" variant="subtle" ff="monospace" onClick={() => onSeek(caption.recordIn)} style={{ flexShrink: 0 }}>
                    {framesToTimecode(caption.recordIn, doc.settings.rate)}
                  </Button>
                  <OverlayTextInput
                    size="xs"
                    style={{ flex: 1 }}
                    value={caption.text.replace(/\n/g, ' / ')}
                    committed={caption.text}
                    toStored={(typed) => typed.replace(/\s*\/\s*/g, '\n').trim()}
                    key={`${caption.id}:${caption.text}`}
                    disabled={readOnly}
                    onCommit={(text) => onSetProperty(caption.id, 'text', text)}
                    noteTestId={`subtitles-text-note-${caption.id}`}
                    aria-label={tx('captionTextAt', { v1: framesToTimecode(caption.recordIn, doc.settings.rate) })}
                    data-testid={`subtitles-text-${caption.id}`}
                  />
                  <Select size="xs" w={touch ? 110 : 96} aria-label={tx('captionLook')} data={CAPTION_LOOK_ORDER.map((v) => ({ value: v, label: tx.msg(captionLookLabelMsg(v)) }))} value={lastLook(caption)} allowDeselect={false} disabled={readOnly} onChange={(v) => v && onSetProperty(caption.id, 'look', v)} data-testid={`subtitles-look-${caption.id}`} styles={touch ? { input: { minHeight: 44 } } : undefined} />
                  <Tooltip label={tx('showItEarlier')}>
                    <EditorIcon tooltip={false} icon={IconChevronLeft} size="sm" disabled={readOnly} onClick={() => nudge(caption.id, -1)} label={tx('nudgeCaptionEarlier')} data-testid={`subtitles-earlier-${caption.id}`} />
                  </Tooltip>
                  <Tooltip label={tx('showItLater')}>
                    <EditorIcon tooltip={false} icon={IconChevronRight} size="sm" disabled={readOnly} onClick={() => nudge(caption.id, 1)} label={tx('nudgeCaptionLater')} data-testid={`subtitles-later-${caption.id}`} />
                  </Tooltip>
                  <Switch size="xs" checked={caption.enabled !== false} disabled={readOnly} onChange={(e) => onSetProperty(caption.id, 'enabled', e.currentTarget.checked)} aria-label={tx('captionEnabled')} />
                  <Tooltip label={splittable ? tx('splitAtThePlayhead') : tx('putThePlayheadInside')}>
                    <EditorIcon tooltip={false} icon={IconScissors} size="sm" disabled={readOnly || !splittable} onClick={() => onApply(compileSplitCaption(doc, caption.id, playheadFrame, newItemId()), tx('splitCaption2'))} label={tx('splitCaption')} data-testid={`subtitles-split-${caption.id}`} />
                  </Tooltip>
                  <Tooltip label={mergeable ? tx('mergeWithTheNext') : tx('noCaptionFollowsThis')}>
                    <EditorIcon tooltip={false} icon={IconArrowMerge} size="sm" disabled={readOnly || !mergeable} onClick={() => onApply(compileMergeCaptionWithNext(doc, caption.id), tx('mergeCaptions'))} label={tx('mergeWithNextCaption')} data-testid={`subtitles-merge-${caption.id}`} />
                  </Tooltip>
                </Group>
              );
            })}
          </Stack>
        </ScrollArea.Autosize>
      )}

      <Divider label={tx('delivery')} labelPosition="left" />
      <Text size="xs" c="dimmed">
        {tx.rich('deliveryNote', {
          b: (c) => <b>{c}</b>,
          srt: (c) => (
            <Anchor size="xs" href={editTimelineExportUrl(projectId, timelineId, 'srt')} download data-testid="subtitles-srt">
              {c}
            </Anchor>
          ),
          vtt: (c) => (
            <Anchor size="xs" href={editTimelineExportUrl(projectId, timelineId, 'vtt')} download data-testid="subtitles-vtt">
              {c}
            </Anchor>
          ),
        })}
      </Text>
      {message && (
        <Text size="xs" c="dimmed" role="status" data-testid="subtitles-message">
          {message}
        </Text>
      )}
      <SubtitleImportDialog opened={importOpen} onClose={() => setImportOpen(false)} doc={doc} readOnly={readOnly} look={look} position={position} language={language} newItemId={newItemId} newTrackId={newTrackId} onApply={onApply} />
    </Stack>
  );
}
