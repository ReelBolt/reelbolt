'use client';

import React, { useMemo, useState } from 'react';
import { Alert, Button, Group, Stack, Text } from '@mantine/core';
import { IconWaveSine } from '@tabler/icons-react';
import { clipMediaRef, estimateMediaAnalysis, startMediaAnalysis, type AnalysisEstimate, type AnalysisKind } from '@/lib/editing/api/media-analysis';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import type { EditTimeline } from '@/lib/editing/types';
import { useT } from '@/i18n';

const DEFAULT_KINDS: readonly AnalysisKind[] = ['shots', 'transcript'];
const REF_SHAPE = /^(file|step_result):[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The media sources (project files and render outputs) the media clips in range play (the range is null for the whole timeline). */
export function mediaRefsInRange(doc: EditTimeline, range: { inFrame: number; outFrame: number } | null): string[] {
  const refs = new Set<string>();
  for (const track of doc.tracks) {
    for (const item of track.items) {
      if (item.type !== 'mediaClip') continue;
      if (range && (item.recordOut <= range.inFrame || item.recordIn >= range.outFrame)) continue;
      const ref = clipMediaRef(item);
      if (ref && REF_SHAPE.test(ref)) refs.add(ref);
    }
  }
  return [...refs];
}

/** The price line shown before anything starts. */
export function analyseFirstPriceLine(estimates: readonly AnalysisEstimate[]): string {
  const blocked = estimates.find((e) => e.unavailableReason);
  if (blocked?.unavailableReason) return blocked.unavailableReason;
  if (estimates.some((e) => !e.billingEnabled)) return 'Analysing this footage is free on this account.';
  const credits = estimates.reduce((sum, e) => sum + (e.cached ? 0 : (e.credits ?? 0)), 0);
  const files = `${estimates.length} file${estimates.length === 1 ? '' : 's'}`;
  return credits > 0 ? `Analysing ${files} costs about ${credits} credit${credits === 1 ? '' : 's'}.` : `Analysing ${files} costs nothing.`;
}

export interface AnalyseFirstProps {
  doc: EditTimeline;
  projectId: string;
  range: { inFrame: number; outFrame: number } | null;
  readOnly: boolean;
  tapStyle?: { minHeight: number; minWidth: number };
  /** Called once the analysis is queued, so the panel can look again at what the helpers can see. */
  onStarted: () => void;
  /** Limits the offer to these analyses (default: shots and transcript). */
  kinds?: readonly AnalysisKind[];
  /** Limits the offer to these files (`file:<guid>`; default: every file in range). */
  mediaRefs?: readonly string[];
  /** Replaces the first button's text (the transcript surface says what it will do). */
  buttonLabel?: string;
  /** Replaces the confirm button's text. */
  confirmLabel?: string;
  /** Injectable for tests. */
  estimate?: typeof estimateMediaAnalysis;
  start?: typeof startMediaAnalysis;
}

/** R11.2: "Analyse first" reuses the analysis-on-demand flow (R09.1) for the files in range, with the price shown before it starts. */
export function AnalyseFirst({ doc, projectId, range, readOnly, tapStyle, onStarted, kinds: kindsProp, mediaRefs, buttonLabel, confirmLabel, estimate = estimateMediaAnalysis, start = startMediaAnalysis }: AnalyseFirstProps) {
  const t = useT('editor.panels.analyseFirst');
  const KINDS = kindsProp ?? DEFAULT_KINDS;
  const refs = useMemo(() => (mediaRefs ? [...mediaRefs] : mediaRefsInRange(doc, range)), [doc, range, mediaRefs]);
  const [estimates, setEstimates] = useState<AnalysisEstimate[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [started, setStarted] = useState(false);

  const price = async () => {
    setBusy(true);
    setProblem(null);
    try {
      setEstimates(await Promise.all(refs.map((ref) => estimate(projectId, ref, KINDS))));
    } catch (e) {
      setProblem(humanizeErrorT(t, e, t('thePriceCouldNot')));
    } finally {
      setBusy(false);
    }
  };
  const run = async () => {
    setBusy(true);
    setProblem(null);
    try {
      await Promise.all(refs.map((ref) => start(projectId, ref, KINDS)));
      setStarted(true);
      setEstimates(null);
      onStarted();
    } catch (e) {
      setProblem(humanizeErrorT(t, e, t('theAnalysisCouldNot')));
    } finally {
      setBusy(false);
    }
  };

  if (refs.length === 0) return null;
  if (started) {
    return (
      <Text size="12px" data-testid="analyse-first-started">
        {t('analysisStartedAskAgain')}
      </Text>
    );
  }
  const canStart = estimates !== null && estimates.every((e) => !e.unavailableReason && e.affordable);
  return (
    <Stack gap={4}>
      {estimates === null ? (
        <Button size="compact-sm" fz="xs" variant="light" style={tapStyle} leftSection={<IconWaveSine size={14} />} loading={busy} disabled={readOnly} onClick={() => void price()} data-testid="analyse-first">
          {buttonLabel ?? t('analyseFirst')}
        </Button>
      ) : (
        <Alert color={canStart ? 'blue' : 'orange'} p="xs" data-testid="analyse-first-price">
          <Stack gap={4}>
            <Text size="12px">{analyseFirstPriceLine(estimates)}</Text>
            <Group gap={4}>
              <Button size="compact-sm" fz="xs" style={tapStyle} disabled={!canStart || readOnly} loading={busy} onClick={() => void run()} data-testid="analyse-first-confirm">
                {confirmLabel ?? t('startAnalysing')}
              </Button>
              <Button size="compact-sm" fz="xs" variant="subtle" color="gray" style={tapStyle} onClick={() => setEstimates(null)}>
                {t('notNow')}
              </Button>
            </Group>
          </Stack>
        </Alert>
      )}
      {problem && (
        <Text size="12px" c="red" role="alert" data-testid="analyse-first-problem">
          {problem}
        </Text>
      )}
    </Stack>
  );
}
