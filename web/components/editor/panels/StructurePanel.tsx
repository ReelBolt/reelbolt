'use client';

import React, { useMemo, useState } from 'react';
import { Alert, Badge, Button, Divider, Group, NumberInput, Select, Stack, Text, TextInput, Tooltip } from '@mantine/core';
import { IconBoxMultiple, IconCamera, IconStack2, IconWaveSine } from '@tabler/icons-react';
import type { EditOp } from '@/lib/editing/engine';
import { refusalSentence } from '@/lib/editing/refusal';
import type { EditTimeline, MediaClip, MulticamClip } from '@/lib/editing/types';
import { compileCreateCompound, compileDecompose } from '@/lib/editing/structure/compound';
import { compileCreateMulticam, compileLiveCut, compileSwitchAngle } from '@/lib/editing/structure/multicam';
import { angleAt } from '@/lib/editing/structure/flatten';
import { estimateSyncOffset, isConfidentSync, offsetToFrames, type SyncResult } from '@/lib/editing/audio/sync';
import { syncConfidenceWord } from './syncConfidence';
import { framesToTimecode } from '@/lib/editing/time';
import { SequenceEditorModal } from './SequenceEditorModal';
import { LargerScreenOnly } from '../shell/LargerScreen';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { DEFAULT_COMPOUND_NAME } from '../shell/default-names';
import { useFormat, useT } from '@/i18n';
import { refusalSentenceMsg } from '@/lib/editing/labels-msg';

export interface StructurePanelProps {
  doc: EditTimeline;
  readOnly: boolean;
  selection: readonly string[];
  playheadFrame: number;
  playbackSpeed: number;
  newId: () => string;
  onOp: (op: EditOp, label?: string) => boolean | void;
  onApply: (ops: readonly EditOp[], label: string) => void;
  onSelect: (itemId: string) => void;
  /** Decodes a media clip's audio to an envelope (WebAudio in the browser; injectable for tests). */
  decodeEnvelope?: (clip: MediaClip) => Promise<Float32Array>;
}

type SyncState = { kind: 'idle' } | { kind: 'running' } | { kind: 'done'; results: Record<string, SyncResult> } | { kind: 'error'; message: string };

/**
 * Advanced structure (P8): compound clips (make from selection, open, decompose) and multicam
 * (make from synchronised clips, cut live while playing, switch a run later). Every action compiles
 * to a program of the closed op catalogue and lands as one undo step; a refusal says why.
 */
export function StructurePanel({ doc, readOnly, selection, playheadFrame, playbackSpeed, newId, onOp, onApply, onSelect, decodeEnvelope }: StructurePanelProps) {
  const tx = useT('editor.panels.structurePanel');
  const nf = useFormat();
  const [compoundName, setCompoundName] = useState(DEFAULT_COMPOUND_NAME);
  const [openSequence, setOpenSequence] = useState<string | null>(null);
  const [sync, setSync] = useState<SyncState>({ kind: 'idle' });
  const [offsets, setOffsets] = useState<Record<string, number>>({});
  const [error, setError] = useState<string | null>(null);
  const [audioFrom, setAudioFrom] = useState<string>('program');

  const all = useMemo(() => doc.tracks.flatMap((t) => t.items), [doc]);
  const compounds = all.filter((i) => i.type === 'compoundClip');
  const multicams = all.filter((i): i is MulticamClip => i.type === 'multicamClip');
  const selectedMedia = selection.map((id) => all.find((i) => i.id === id)).filter((i): i is MediaClip => i?.type === 'mediaClip');
  const activeMulticam = multicams.find((m) => selection.includes(m.id)) ?? multicams.find((m) => m.recordIn <= playheadFrame && playheadFrame < m.recordOut) ?? null;
  const rate = doc.settings.rate;

  const attempt = (fn: () => void) => {
    setError(null);
    try {
      fn();
    } catch (e) {
      setError(tx.msg(refusalSentenceMsg(refusalSentence(e, doc))));
    }
  };

  const runSync = async () => {
    if (!decodeEnvelope || selectedMedia.length < 2) return;
    setSync({ kind: 'running' });
    try {
      const envelopes = await Promise.all(selectedMedia.map((c) => decodeEnvelope(c)));
      const results: Record<string, SyncResult> = {};
      const next: Record<string, number> = {};
      selectedMedia.forEach((clip, i) => {
        if (i === 0) return;
        const r = estimateSyncOffset(envelopes[0], envelopes[i]);
        results[clip.id] = r;
        // The angle's source must start `lag` later (relative to its own clip's sourceIn) to line up.
        next[clip.id] = offsetToFrames(r.offsetSeconds, rate) - ((clip.sourceIn ?? 0) - (selectedMedia[0].sourceIn ?? 0));
      });
      setOffsets(next);
      setSync({ kind: 'done', results });
    } catch (e) {
      setSync({ kind: 'error', message: humanizeErrorT(tx, e, tx('theAudioCouldNot')) });
    }
  };

  return (
    <Stack gap="xs" data-testid="structure-panel">
      <Text size="sm" fw={600}>
        {tx('compoundClips')}
      </Text>
      <LargerScreenOnly feature="compound" what={tx('makingAndEditingCompound')}>
      <Group gap={4} align="end">
        <TextInput size="xs" label={tx('name')} value={compoundName} onChange={(e) => setCompoundName(e.currentTarget.value)} disabled={readOnly} style={{ flex: 1 }} />
        <Button
          size="compact-xs" fz="xs"
          leftSection={<IconBoxMultiple size={12} />}
          disabled={readOnly || selection.length === 0}
          onClick={() => attempt(() => onApply(compileCreateCompound(doc, selection, { sequenceId: newId(), compoundId: newId() }, compoundName.trim() || DEFAULT_COMPOUND_NAME), `Make compound “${compoundName.trim() || tx('compoundClip2')}”`))}
          data-testid="compound-create"
        >
          {tx('makeCompound')}
        </Button>
      </Group>
      {compounds.length === 0 && (
        <Text size="xs" c="dimmed">
          {tx('selectClipsAndMake')}
        </Text>
      )}
      {compounds.map((c) => (
        <Group key={c.id} justify="space-between" gap={4} data-testid={`compound-row-${c.id}`}>
          <Text size="xs" style={{ cursor: 'var(--rb-cursor-pointer)' }} onClick={() => onSelect(c.id)}>
            {c.name} · {framesToTimecode(c.recordIn, rate)}
          </Text>
          <Group gap={4}>
            <Button size="compact-xs" fz="xs" variant="light" disabled={readOnly || c.type !== 'compoundClip'} onClick={() => c.type === 'compoundClip' && setOpenSequence(c.sequenceId)} data-testid={`compound-open-${c.id}`}>
              {tx('open')}
            </Button>
            <Button size="compact-xs" fz="xs" variant="subtle" disabled={readOnly} onClick={() => attempt(() => onApply(compileDecompose(doc, c.id, newId), `Decompose ${c.name}`))} data-testid={`compound-decompose-${c.id}`}>
              {tx('decompose')}
            </Button>
          </Group>
        </Group>
      ))}
      </LargerScreenOnly>

      <Divider />
      <Text size="sm" fw={600}>
        {tx('multicam')}
      </Text>
      <Text size="xs" c="dimmed">
        {tx('selectTwoOrMore')}
      </Text>
      <Group gap={4}>
        <Tooltip label={decodeEnvelope ? tx('crossCorrelateTheAngles') : tx('audioDecodingIsUnavailable')}>
          <Button size="compact-xs" fz="xs" variant="light" leftSection={<IconWaveSine size={12} />} disabled={readOnly || selectedMedia.length < 2 || !decodeEnvelope} loading={sync.kind === 'running'} onClick={() => void runSync()} data-testid="multicam-sync">
            {tx('syncByWaveform')}
          </Button>
        </Tooltip>
        <Button
          size="compact-xs" fz="xs"
          leftSection={<IconCamera size={12} />}
          disabled={readOnly || selectedMedia.length < 2}
          onClick={() =>
            attempt(() =>
              onApply(
                compileCreateMulticam(doc, selectedMedia.map((c) => c.id), { multicamId: newId(), angleIds: selectedMedia.map(() => newId()) }, offsets, 'Multicam', selectedMedia.some((c) => c.id === audioFrom) ? audioFrom : audioFrom === 'none' ? 'none' : 'program'),
                tx('makeMulticamAngles', { length: selectedMedia.length }),
              ),
            )
          }
          data-testid="multicam-create"
        >
          {tx('makeMulticam')}
        </Button>
      </Group>
      {selectedMedia.length >= 2 && (
        <Select
          size="xs"
          label={tx('soundOfTheMulticam')}
          description={tx('whichRecordingSuppliesThe')}
          data={[
            { value: 'program', label: tx('program', { name: selectedMedia[0].name }) },
            ...selectedMedia.map((c) => ({ value: c.id, label: c.name })),
            { value: 'none', label: tx('noSound') },
          ]}
          value={selectedMedia.some((c) => c.id === audioFrom) || audioFrom === 'program' || audioFrom === 'none' ? audioFrom : 'program'}
          onChange={(v) => setAudioFrom(v ?? 'program')}
          allowDeselect={false}
          disabled={readOnly}
          data-testid="multicam-audio-source"
        />
      )}
      {selectedMedia.slice(1).map((clip) => {
        const result = sync.kind === 'done' ? sync.results[clip.id] : undefined;
        return (
          <Group key={clip.id} gap={4} align="end" data-testid={`multicam-offset-row-${clip.id}`}>
            <NumberInput size="xs" label={tx('offsetFrames', { name: clip.name })} value={offsets[clip.id] ?? 0} step={1} disabled={readOnly} onChange={(v) => typeof v === 'number' && setOffsets((o) => ({ ...o, [clip.id]: Math.round(v) }))} w={170} data-testid={`multicam-offset-${clip.id}`} />
            <Button size="compact-xs" fz="xs" variant="subtle" disabled={readOnly} aria-label={tx('nudgeOneFrameEarlier', { name: clip.name })} onClick={() => setOffsets((o) => ({ ...o, [clip.id]: (o[clip.id] ?? 0) - 1 }))} data-testid={`multicam-nudge-minus-${clip.id}`}>
              {tx('1Frame')}
            </Button>
            <Button size="compact-xs" fz="xs" variant="subtle" disabled={readOnly} aria-label={tx('nudgeOneFrameLater', { name: clip.name })} onClick={() => setOffsets((o) => ({ ...o, [clip.id]: (o[clip.id] ?? 0) + 1 }))} data-testid={`multicam-nudge-plus-${clip.id}`}>
              {tx('1Frame2')}
            </Button>
            {result && (
              <Badge size="xs" fz="xs" color={isConfidentSync(result) ? 'green' : 'yellow'} data-testid={`multicam-sync-confidence-${clip.id}`}>
                {tx('syncResult', { word: syncConfidenceWord(result), offset: nf.number(Math.abs(result.offsetSeconds), { minimumFractionDigits: 2, maximumFractionDigits: 2 }), direction: result.offsetSeconds < 0 ? 'earlier' : 'later' })}
              </Badge>
            )}
          </Group>
        );
      })}
      {sync.kind === 'error' && (
        <Alert color="red" variant="light">
          {sync.message}
        </Alert>
      )}
      {activeMulticam && (
        <Stack gap={4} p={6} style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 4 }} data-testid="multicam-angles">
          <Group justify="space-between">
            <Text size="xs" fw={600}>
              {activeMulticam.name}
            </Text>
            <Badge size="xs" fz="xs" variant="light" leftSection={<IconStack2 size={10} />}>
              {tx('cutCount', { count: (activeMulticam.cuts ?? []).length })}
            </Badge>
          </Group>
          <Text size="12px" c="dimmed">
            {playbackSpeed !== 0 ? tx('playingAClickCuts') : tx('stoppedAClickSwitches')}
          </Text>
          <Select
            size="xs"
            label={tx('sound')}
            data={[{ value: 'program', label: tx('programFirstAngle') }, ...activeMulticam.angles.map((a) => ({ value: a.id, label: a.name })), { value: 'none', label: tx('noSound2') }]}
            value={activeMulticam.audioSource ?? 'program'}
            allowDeselect={false}
            disabled={readOnly}
            onChange={(v) => v && onOp({ type: 'setProperty', itemId: activeMulticam.id, key: 'audioSource', value: v === 'program' ? undefined : v }, tx('changeMulticamSound'))}
            data-testid="multicam-audio-source-active"
          />
          <Group gap={4}>
            {activeMulticam.angles.map((angle, i) => {
              const live = angleAt(activeMulticam, playheadFrame) === angle.id;
              return (
                <Button
                  key={angle.id}
                  size="compact-xs" fz="xs"
                  variant={live ? 'filled' : 'light'}
                  color={live ? 'red' : undefined}
                  disabled={readOnly || playheadFrame < activeMulticam.recordIn || playheadFrame >= activeMulticam.recordOut}
                  onClick={() =>
                    attempt(() => {
                      const op = playbackSpeed !== 0 ? compileLiveCut(activeMulticam, playheadFrame, angle.id) : compileSwitchAngle(activeMulticam, playheadFrame, angle.id);
                      if (op) onOp(op, playbackSpeed !== 0 ? tx('cutTo', { name: angle.name }) : tx('switchTo', { name: angle.name }));
                    })
                  }
                  data-testid={`multicam-angle-${i + 1}`}
                >
                  {i + 1} · {angle.name}
                </Button>
              );
            })}
          </Group>
        </Stack>
      )}
      {error && (
        <Alert color="red" variant="light" data-testid="structure-error">
          {error}
        </Alert>
      )}
      <SequenceEditorModal doc={doc} sequenceId={openSequence} newId={newId} onClose={() => setOpenSequence(null)} onCommit={(op, label) => onOp(op, label)} />
    </Stack>
  );
}
