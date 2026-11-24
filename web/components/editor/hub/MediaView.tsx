'use client';

import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Stack, Text } from '@mantine/core';
import { MediaPool, type MediaPoolProps, type MediaZoneTargets } from '../media/MediaPool';
import { MetadataPanel } from '../media/MetadataPanel';
import { AnnotationEditor } from '../media/AnnotationEditor';
import { PrepareDerivatives } from '../media/PrepareDerivatives';
import { SourceViewer } from '../media/SourceViewer';
import { upsertMediaAnnotation } from '@/lib/editing/api/media';
import { useMediaAnnotations, useMediaPool } from '@/lib/hooks/use-media';
import { isTerminalStage, progressSentence, useMediaDerivativeProgress, type MediaDerivativeProgressEvent } from '@/lib/hooks/use-media-derivatives';
import { NO_MARKS, type Marks } from '@/lib/editing/media/in-out';
import type { SubclipDescriptor } from '@/lib/editing/media/subclip';
import type { MediaPoolItem, MediaRef } from '@/lib/editing/media/types';
import { PageEmptyState } from '../ui/PageEmptyState';
import { useT } from '@/i18n';

export interface MediaViewProps {
  projectId: string;
  /**
   * Extra actions for the selected clip, rendered at the top of the detail pane — the editor's
   * Media workspace uses it to put the clip on the timeline. The hub passes nothing: there is no
   * timeline open there.
   */
  clipActions?: (selected: MediaPoolItem, marks: Marks) => React.ReactNode;
  onSelectClip?: MediaPoolProps['onSelectClip'];
  /** Places a created subclip (the editor puts the marked range on the timeline). */
  onCreateSubclip?: (selected: MediaPoolItem, descriptor: SubclipDescriptor, marks: Marks) => void;
  /** What "Create subclip" does, shown under the descriptor. */
  createSubclipHint?: string;
  /** R05.4: tiles get a Place menu (the editor puts the source on the timeline). */
  onPlace?: MediaPoolProps['onPlace'];
  /** A view-only editor: no Enhance on a video's menu (the pool's dialog cannot start a run). */
  readOnly?: boolean;
  placeDisabled?: boolean;
  /** R05.4: the open clip's marks, so a tile dragged onto the timeline carries the same range the buttons do. */
  onMarksChange?: (mediaRef: MediaRef, marks: Marks) => void;
  /** T-003: on the Media page, the pool's bins and viewer+metadata render in the frame's zones. */
  zoneTargets?: MediaZoneTargets | null;
}

/** How long progress events are coalesced before the pool (and its probes) is re-read. */
const POOL_REFRESH_DELAY_MS = 800;

/**
 * The media workspace for one project: the pool with its bins, and the selected clip's viewer,
 * derivatives, metadata and annotations. Used by the editor's own Media workspace.
 *
 * The detail pane is KEYED by the mediaRef: selecting another clip mounts a fresh viewer, which is
 * what discards the previous clip's in/out marks and its already-read derivatives. Mounting
 * `PrepareDerivatives` is "the user opened this asset", the sanctioned trigger for the derivatives
 * request (never a hover, never a list render).
 */
export function MediaView({ projectId, clipActions, onSelectClip, onCreateSubclip, createSubclipHint, onPlace, placeDisabled, readOnly, onMarksChange, zoneTargets = null }: MediaViewProps) {
  const t = useT('editor.hub.mediaView');
  // One version per source: bumping it re-reads that source's derivatives in EVERY view (the
  // viewer, the filmstrip and its chips), whether the bump came from "Check again" or from the
  // pipeline's own progress events.
  const [versions, setVersions] = useState<Record<MediaRef, number>>({});
  const bump = useCallback((mediaRef: MediaRef | 'all') => {
    setVersions((previous) => {
      if (mediaRef !== 'all') return { ...previous, [mediaRef]: (previous[mediaRef] ?? 0) + 1 };
      const next: Record<MediaRef, number> = {};
      for (const key of Object.keys(previous)) next[key] = previous[key] + 1;
      next.all = (previous.all ?? 0) + 1;
      return next;
    });
  }, []);
  const versionOf = (mediaRef: MediaRef): number => (versions[mediaRef] ?? 0) + (versions.all ?? 0);
  const { data: annotations, mutate: mutateAnnotations } = useMediaAnnotations(projectId);
  const { mutate: mutatePool } = useMediaPool(projectId);

  // The marks of the open clip, tagged with the clip they belong to.
  const [marksEntry, setMarksEntry] = useState<{ mediaRef: MediaRef; marks: Marks } | null>(null);
  const marksFor = (mediaRef: MediaRef): Marks => (marksEntry && marksEntry.mediaRef === mediaRef ? marksEntry.marks : NO_MARKS);

  // The pipeline's progress, per source: the pane refreshes itself when a stage moves on.
  const [progress, setProgress] = useState<Record<MediaRef, MediaDerivativeProgressEvent>>({});
  const lastStage = useRef(new Map<string, string>());
  const poolTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(
    () => () => {
      if (poolTimer.current) clearTimeout(poolTimer.current);
    },
    [],
  );
  useMediaDerivativeProgress(projectId, (event) => {
    const key = event.mediaRef ?? 'all';
    if (event.mediaRef) setProgress((previous) => ({ ...previous, [key]: event }));
    if (lastStage.current.get(key) === event.stage) return;
    lastStage.current.set(key, event.stage);
    bump(key);
    if (isTerminalStage(event.stage)) {
      // A finished run may have stored the file's probe: re-read the pool once things settle.
      if (poolTimer.current) clearTimeout(poolTimer.current);
      poolTimer.current = setTimeout(() => void mutatePool(), POOL_REFRESH_DELAY_MS);
    }
  });

  return (
    <MediaPool
      projectId={projectId}
      readOnly={readOnly}
      zoneTargets={zoneTargets}
      onSelectClip={onSelectClip}
      derivativesVersionOf={versionOf}
      onPlace={onPlace}
      placeDisabled={placeDisabled}
      detailPane={(selected) =>
        selected ? (
          <Stack gap="md" key={selected.mediaRef}>
            {clipActions?.(selected, marksFor(selected.mediaRef))}
            <SourceViewer
              projectId={projectId}
              item={selected}
              derivativesReloadKey={versionOf(selected.mediaRef)}
              onMarksChange={(marks) => {
                setMarksEntry({ mediaRef: selected.mediaRef, marks });
                onMarksChange?.(selected.mediaRef, marks);
              }}
              onCreateSubclip={onCreateSubclip ? (descriptor) => onCreateSubclip(selected, descriptor, { inFrame: descriptor.sourceInFrames, outFrame: descriptor.sourceOutFrames }) : undefined}
              subclipHint={createSubclipHint}
            />
            {progress[selected.mediaRef] ? (
              <Text size="xs" c={progress[selected.mediaRef].stage === 'Failed' ? 'red' : 'dimmed'} data-testid="media-derivative-progress" data-stage={progress[selected.mediaRef].stage}>
                {progressSentence(progress[selected.mediaRef])}
              </Text>
            ) : null}
            <PrepareDerivatives projectId={projectId} item={selected} onCheckAgain={() => bump(selected.mediaRef)} />
            <MetadataPanel item={selected} />
            <AnnotationEditor
              annotation={(annotations ?? []).find((a) => a.mediaRef === selected.mediaRef) ?? null}
              onSave={async (request) => {
                const saved = await upsertMediaAnnotation(projectId, selected.mediaRef, request);
                await mutateAnnotations((current) => [...(current ?? []).filter((a) => a.mediaRef !== saved.mediaRef), saved], { revalidate: false });
              }}
            />
          </Stack>
        ) : (
          <PageEmptyState page="media" hint={t('selectAClip')} testId="media-detail-empty" />
        )
      }
    />
  );
}
