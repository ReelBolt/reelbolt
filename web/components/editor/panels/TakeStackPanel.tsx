'use client';

import React, { useState } from 'react';
import { Alert, Badge, Button, Divider, Group, Radio, ScrollArea, Select, Stack, Text, Tooltip } from '@mantine/core';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline } from '@/lib/editing/types';
import { framesToTimecode } from '@/lib/editing/time';
import { readShots } from '@/lib/editing/cut/cut';
import { buildTakeStacks, compileSwapTake, readDuplicateGroups, swapTakeRefusal } from '@/lib/editing/takes/take-stacks';
import { compileKeepTake, findRepeatedTakes, takeRecordSpans, type TakeGroup } from '@/lib/editing/takes/repeated-takes';
import { readTranscript } from '@/lib/editing/transcript/transcript';
import { sourceMatcher, useTimelineAnalysis } from '@/lib/editing/api/analysis';
import { AnalysisHelpLink } from './AnalysisHelpLink';
import { useFormat, useT } from '@/i18n';

export interface TakeStackPanelProps {
  doc: EditTimeline;
  projectId: string;
  seedStepResultId: string | null;
  readOnly: boolean;
  onOp: (op: EditOp, label?: string) => void;
  onSeek: (frame: number) => void;
  onSelect: (itemId: string) => void;
  /** R09.9: write a program of ops as one undo step. */
  onApply?: (ops: readonly EditOp[], label: string) => void;
  /** R09.3: play the result before writing it. */
  onAudition?: (ops: readonly EditOp[], label: string) => void;
  newItemId?: () => string;
}

/**
 * Take stacking (P6): for every clip whose shot the analysis grouped with near-duplicates, the
 * alternates as a stack. "Use this take" swaps the clip's source window for the alternate's start —
 * one `replace`, same slot and length — so the timeline never changes shape.
 */
export function TakeStackPanel({ doc, projectId, seedStepResultId, readOnly, onOp, onSeek, onSelect, onApply, onAudition, newItemId }: TakeStackPanelProps) {
  const tx = useT('editor.panels.takeStackPanel');
  const nf = useFormat();
  const { data: analysis, isLoading, error } = useTimelineAnalysis(projectId, seedStepResultId, doc);
  const artifact = analysis?.artifact;
  const matches = sourceMatcher(analysis, doc);
  const [refusal, setRefusal] = useState<string | null>(null);
  const fps = doc.settings.rate.den === 0 ? 30 : doc.settings.rate.num / doc.settings.rate.den;
  const mediaTracks = doc.tracks.filter((t) => t.items.some((i) => i.type === 'mediaClip'));
  const [trackId, setTrackId] = useState<string | null>(null);
  const effectiveTrackId = trackId ?? mediaTracks[0]?.id ?? null;

  const stacks = (() => {
    if (!effectiveTrackId) return [];
    return buildTakeStacks(doc, effectiveTrackId, readShots(artifact), readDuplicateGroups(artifact), fps, matches);
  })();

  // R09.9: repeated lines found in the transcript, with the best take picked.
  const transcript = readTranscript(artifact);
  const groups: TakeGroup[] = findRepeatedTakes(transcript.words).filter((g) => effectiveTrackId !== null && g.takes.some((t) => takeRecordSpans(doc, effectiveTrackId, t, fps, matches).length > 0));
  const [keep, setKeep] = useState<Record<string, string>>({});
  const keepOf = (g: TakeGroup): string => keep[g.id] ?? g.bestId;
  const run = (mode: 'apply' | 'audition') => {
    if (!effectiveTrackId || !newItemId) return;
    const plan = compileKeepTake(doc, { trackId: effectiveTrackId, groups: groups.map((g) => ({ group: g, keepId: keepOf(g) })), fps, matches, newItemId });
    if (plan.ops.length === 0) {
      setRefusal(tx('nothingToSetAside'));
      return;
    }
    const label = tx('keepTheBestTake', { count: groups.length, cuts: plan.cuts });
    if (mode === 'audition' && onAudition) onAudition(plan.ops, label);
    else onApply?.(plan.ops, label);
  };

  return (
    <Stack gap="xs" data-testid="take-stack-panel">
      <Group justify="space-between" align="flex-end">
        <Text size="sm" fw={600}>
          {tx('takes')}
        </Text>
        <Select size="xs" label={tx('track')} data={mediaTracks.map((t) => ({ value: t.id, label: t.name }))} value={effectiveTrackId} onChange={setTrackId} allowDeselect={false} w={140} data-testid="takes-track" />
      </Group>
      {seedStepResultId === null && !analysis && <Alert color="yellow" p="xs">{tx('seedTheTimelineFrom')}</Alert>}
      {seedStepResultId === null && !analysis && <AnalysisHelpLink projectId={projectId} />}
      {error && <Alert color="red" p="xs">{tx('theAnalysisCouldNot')}</Alert>}
      {isLoading && <Text size="xs" c="dimmed">{tx('readingTheAnalysis')}</Text>}
      {refusal && (
        <Alert color="orange" p="xs" withCloseButton onClose={() => setRefusal(null)} data-testid="take-refusal">
          {refusal}
        </Alert>
      )}
      {!isLoading && (seedStepResultId !== null || !!analysis) && stacks.length === 0 && groups.length === 0 && <Text size="xs" c="dimmed" data-testid="takes-none">{tx('noClipOnThis')}</Text>}
      {groups.length > 0 && (
        <Stack gap="xs" data-testid="takes-repeated">
          <Divider label={tx('linesSaidMoreThan', { length: groups.length })} labelPosition="left" />
          {groups.map((g) => (
            <Stack key={g.id} gap={4} data-testid={`takes-group-${g.id}`}>
              <Text size="xs" fw={600} lineClamp={2}>{tx('quoted', { text: g.text })}</Text>
              <Radio.Group value={keepOf(g)} onChange={(v) => setKeep({ ...keep, [g.id]: v })} name={`keep-${g.id}`}>
                <Stack gap={4}>
                  {g.takes.map((t) => {
                    const span = effectiveTrackId ? takeRecordSpans(doc, effectiveTrackId, t, fps, matches)[0] : undefined;
                    return (
                      <Group key={t.id} gap={6} wrap="nowrap" justify="space-between">
                        <Radio size="xs" value={t.id} disabled={readOnly || !span} label={tx('takeLabel', { place: span ? framesToTimecode(span.startFrame, doc.settings.rate) : 'none', seconds: nf.number(t.endSec - t.startSec, { minimumFractionDigits: 1, maximumFractionDigits: 1 }) })} data-testid={`takes-keep-${t.id}`} />
                        <Group gap={4} wrap="nowrap">
                          {t.id === g.bestId && <Badge size="xs" fz="xs" color="green" variant="light">{tx('bestTake')}</Badge>}
                          {t.stumbles > 0 && <Badge size="xs" fz="xs" color="orange" variant="light">{tx('stumbleCount', { count: t.stumbles })}</Badge>}
                          {t.roughWords > 0 && <Badge size="xs" fz="xs" color="gray" variant="light">{tx('roughTiming')}</Badge>}
                          {span && <Button size="compact-xs" fz="xs" variant="subtle" onClick={() => onSeek(span.startFrame)}>{tx('show')}</Button>}
                        </Group>
                      </Group>
                    );
                  })}
                </Stack>
              </Radio.Group>
            </Stack>
          ))}
          <Text size="xs" c="dimmed">{tx('theTakesYouDo')}</Text>
          <Group gap="xs">
            {onAudition && (
              <Button size="compact-xs" fz="xs" variant="light" disabled={readOnly || !newItemId} onClick={() => run('audition')} data-testid="takes-audition">{tx('playTheResult')}</Button>
            )}
            <Button size="compact-xs" fz="xs" disabled={readOnly || !onApply || !newItemId} onClick={() => run('apply')} data-testid="takes-keep-apply">{tx('keepTheseSetThe')}</Button>
          </Group>
        </Stack>
      )}
      <ScrollArea.Autosize mah={420}>
        <Stack gap="sm">
          {stacks.map((stack) => (
            <Stack key={stack.clipId} gap={4} data-testid={`take-stack-${stack.clipId}`} style={{ borderLeft: '2px solid var(--mantine-color-violet-5)', paddingLeft: 6 }}>
              <Group gap={6} wrap="nowrap">
                <Button size="compact-xs" fz="xs" variant="subtle" onClick={() => { onSeek(stack.recordIn); onSelect(stack.clipId); }}>
                  {stack.clipName}
                </Button>
                <Text size="xs" c="dimmed" ff="monospace">
                  {framesToTimecode(stack.recordIn, doc.settings.rate)}
                </Text>
                <Badge size="xs" fz="xs" variant="light" color="gray">
                  {tx('showing', { id: stack.currentShot.id })}
                </Badge>
              </Group>
              {stack.alternates.map((alt) => {
                const op = compileSwapTake(doc, stack.clipId, alt.shot, fps);
                return (
                  <Group key={alt.shot.id} gap={6} wrap="nowrap" justify="space-between" pl="sm">
                    <Group gap={6} wrap="nowrap">
                      <Text size="xs">{alt.shot.id}</Text>
                      <Text size="xs" c="dimmed">
                        {tx('shotRange', { start: nf.number(alt.shot.startSec, { minimumFractionDigits: 1, maximumFractionDigits: 1 }), end: nf.number(alt.shot.endSec, { minimumFractionDigits: 1, maximumFractionDigits: 1 }) })}
                      </Text>
                      {alt.isBest && (
                        <Badge size="xs" fz="xs" color="green" variant="light">
                          {tx('bestTake2')}
                        </Badge>
                      )}
                      {!alt.fits && (
                        <Tooltip label={tx('shorterThanTheClip')}>
                          <Badge size="xs" fz="xs" color="orange" variant="light">
                            {tx('short')}
                          </Badge>
                        </Tooltip>
                      )}
                    </Group>
                    <Button size="compact-xs" fz="xs" variant="light" disabled={readOnly || !op} onClick={() => {
                        const why = swapTakeRefusal(doc, stack.clipId, alt.shot, fps);
                        setRefusal(why);
                        if (!why && op) onOp(op, tx('useTake', { id: alt.shot.id }));
                      }} data-testid={`take-use-${stack.clipId}-${alt.shot.id}`}>
                      {tx('useThisTake')}
                    </Button>
                  </Group>
                );
              })}
            </Stack>
          ))}
        </Stack>
      </ScrollArea.Autosize>
    </Stack>
  );
}
