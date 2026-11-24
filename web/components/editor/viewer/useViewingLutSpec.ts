'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { CubeLutError, CUBE_LUT_MAX_BYTES } from '@/lib/editing/color/cube-lut';
import { lutReasonFromError, type LutReason } from '@/lib/editing/color/lut-reason';
import { loadViewingLut } from '@/lib/editing/color/cube-lut-loader';
import { viewingLutFromCube, viewingLutFromProfile, type ViewingLutSpec } from '@/lib/editing/color/viewer-color';
import { useViewingLut, type ViewingLutChoice } from '@/lib/editing/prefs/viewing-lut';
import { useProjectFileContent, useProjectFiles } from '@/lib/hooks/use-files';

/** A refusal with its numbers, or just its stable code. */
export type ViewingLutProblem = LutReason | LutReason['code'];

export interface ViewingLutState {
  choice: ViewingLutChoice;
  setChoice: (next: ViewingLutChoice) => void;
  /** The LUT the viewer applies right now (null = off, still loading, or refused). */
  spec: ViewingLutSpec | null;
  /** Why the last choice was turned off; cleared by the next choice. */
  problem: ViewingLutProblem | null;
  /** The compositor's read at every present. */
  getViewingLut: () => ViewingLutSpec | null;
}

const choiceKey = (c: ViewingLutChoice): string | null => (c ? (c.kind === 'file' ? `file:${c.fileId}` : `profile:${c.id}`) : null);

/**
 * The viewing LUT of one timeline, resolved to texels: a project `.cube` is read and parsed (off the main thread when it can
 * be), a camera profile is its table. A refused file shows a message and turns the LUT off. Phone: always off.
 */
export function useViewingLutSpec(projectId: string, timelineId: string): ViewingLutState {
  const { choice, setChoice: store } = useViewingLut(timelineId);
  const [problem, setProblem] = useState<ViewingLutProblem | null>(null);
  const [loaded, setLoaded] = useState<{ key: string; spec: ViewingLutSpec } | null>(null);
  const fileId = choice?.kind === 'file' ? choice.fileId : null;
  // The file listing carries the size and version token: refuse an oversized file BEFORE the body is requested,
  // and key the parsed table by the file's version, not its length (a same-length edit must not serve a stale table).
  const { data: files } = useProjectFiles(projectId);
  const listed = fileId ? (files?.find((f) => f.id === fileId) ?? null) : null;
  const tooLarge = listed !== null && listed.sizeBytes > CUBE_LUT_MAX_BYTES;
  const { data: content, error: contentError } = useProjectFileContent(projectId, tooLarge ? null : fileId);
  const key = choiceKey(choice);
  const version = listed?.uploadedAt ?? content?.uploadedAt ?? null;
  const cacheKey = fileId && version ? `${fileId}:${version}` : undefined;

  const setChoice = useCallback(
    (next: ViewingLutChoice) => {
      setProblem(null);
      store(next);
    },
    [store],
  );

  const profileId = choice?.kind === 'profile' ? choice.id : null;
  const profileSpec = useMemo(() => (profileId ? viewingLutFromProfile(profileId) : null), [profileId]);

  useEffect(() => {
    if (!fileId) return;
    let cancelled = false;
    const run = async () => {
      if (tooLarge) throw new CubeLutError('too_large');
      if (contentError) throw new Error('unreadable');
      if (!content) return;
      const lut = await loadViewingLut(async () => content.content, { key: cacheKey, sizeHint: listed?.sizeBytes });
      if (!cancelled) setLoaded({ key: `file:${fileId}`, spec: viewingLutFromCube(`file:${cacheKey ?? fileId}`, lut) });
    };
    run().catch((err: unknown) => {
      if (cancelled) return;
      setProblem(lutReasonFromError(err));
      store(null);
    });
    return () => {
      cancelled = true;
    };
  }, [fileId, content, contentError, store, cacheKey, listed, tooLarge]);

  const spec = profileSpec ?? (key !== null && loaded?.key === key ? loaded.spec : null);
  const ref = useRef<ViewingLutSpec | null>(null);
  useEffect(() => {
    ref.current = spec;
  }, [spec]);
  const getViewingLut = useCallback(() => ref.current, []);
  return useMemo(() => ({ choice, setChoice, spec, problem, getViewingLut }), [choice, setChoice, spec, problem, getViewingLut]);
}
