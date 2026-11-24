'use client';

import React, { useMemo } from 'react';
import { Button, Stack, Text } from '@mantine/core';
import { documentMediaRefs, useDocumentSourceStatuses, type MediaAnalysisStatus } from '@/lib/editing/api/media-analysis';
import type { EditTimeline } from '@/lib/editing/types';
import { useT } from '@/i18n';
import { AnalyseFirst } from './AnalyseFirst';

export type TranscriptStartState =
  | { kind: 'none' }
  | { kind: 'loading' }
  | { kind: 'notAnalysed'; refs: string[] }
  | { kind: 'running' }
  | { kind: 'unreadable' }
  | { kind: 'failed'; reason: string | null; refs: string[] };

/** What to say about the transcripts of the sources the timeline shows, from their statuses (null = unreadable). */
export function transcriptStartState(refs: readonly string[], statuses: ReadonlyArray<MediaAnalysisStatus | null> | undefined): TranscriptStartState {
  if (refs.length === 0) return { kind: 'none' };
  if (!statuses) return { kind: 'loading' };
  const of = (i: number) => statuses[i]?.kinds.transcript;
  if (refs.some((_, i) => ['queued', 'running'].includes(of(i)?.state ?? 'none'))) return { kind: 'running' };
  if (statuses.length < refs.length || refs.some((_, i) => !statuses[i])) return { kind: 'unreadable' };
  const failed = refs.map((r, i) => ({ r, k: of(i) })).filter((x) => x.k?.state === 'failed');
  if (failed.length > 0) return { kind: 'failed', reason: failed[0].k?.reason ?? null, refs: failed.map((x) => x.r) };
  const todo = refs.filter((_, i) => (of(i)?.state ?? 'none') === 'none');
  return todo.length > 0 ? { kind: 'notAnalysed', refs: todo } : { kind: 'none' };
}

export interface TranscriptStartProps {
  doc: EditTimeline;
  projectId: string;
  readOnly: boolean;
  /** Shown when there is nothing to start (no media, or every source already has its words). */
  emptyText: string;
  /** Phone: every button is at least 44 px. */
  tapStyle?: { minHeight: number; minWidth: number };
}

/** The empty transcript: never silent. Offers the words with their price, shows progress, or says why it failed and offers a retry. */
export function TranscriptStart({ doc, projectId, readOnly, emptyText, tapStyle }: TranscriptStartProps) {
  const t = useT('editor.panels.transcriptStart');
  const refs = useMemo(() => documentMediaRefs(doc), [doc]);
  const { data: statuses, mutate } = useDocumentSourceStatuses(projectId, refs);
  const state = transcriptStartState(refs, statuses);
  const kinds = ['transcript'] as const;

  if (state.kind === 'loading') return <Text size="xs" c="dimmed">{t('lookingForTheWords')}</Text>;
  if (state.kind === 'none') return <Text size="xs" c="dimmed" data-testid="transcript-empty">{emptyText}</Text>;
  if (state.kind === 'running') return <Text size="xs" c="dimmed" role="status" data-testid="transcript-running">{t('findingTheWords')}</Text>;
  if (state.kind === 'unreadable') {
    return (
      <Stack gap={4} data-testid="transcript-unreadable">
        <Text size="xs" c="red" role="alert">{t('couldNotReadTheState')}</Text>
        <Button size="compact-sm" fz="xs" variant="light" style={tapStyle} onClick={() => void mutate()}>{t('tryAgain')}</Button>
      </Stack>
    );
  }
  return (
    <Stack gap={4} data-testid={state.kind === 'failed' ? 'transcript-failed' : 'transcript-start'}>
      {state.kind === 'failed' ? (
        <Text size="xs" c="red" role="alert">{state.reason && state.reason.length > 0 ? state.reason : t('couldNotFindTheWords')}</Text>
      ) : (
        <Text size="xs" c="dimmed">{t('noWordsYet')}</Text>
      )}
      <AnalyseFirst
        doc={doc}
        projectId={projectId}
        range={null}
        readOnly={readOnly}
        tapStyle={tapStyle}
        kinds={kinds}
        mediaRefs={state.refs}
        buttonLabel={state.kind === 'failed' ? t('tryAgain') : t('findTheWords')}
        confirmLabel={t('findTheWordsNow')}
        onStarted={() => void mutate()}
      />
    </Stack>
  );
}
