'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { fetcher } from '@/lib/api/client';
import { createGradeLutStore } from '@/lib/editing/color/grade-lut-store';
import type { GradeLutResolver } from '@/lib/editing/preview/grade-pass-plan';
import type { AssetRef } from '@/lib/editing/types';
import { useProjectFiles } from '@/lib/hooks/use-files';
import type { ProjectFileContent } from '@/lib/types/project';

export interface GradeLuts {
  /** The resolver the live session reads for LUT nodes (`gradeLutFor`). */
  gradeLutFor: GradeLutResolver;
  /** For the parity classifier: where a LUT file is. Its identity changes when a file arrives, so memos recompute. */
  gradeLutStatus: (file: AssetRef) => 'loading' | 'ready' | 'failed';
}

/**
 * The grade-node LUTs of a project: project `.cube` files are fetched through the project-file content route on first sight.
 * `onReady` runs when one arrives (re-present the frame); the returned `gradeLutStatus` also changes identity then.
 */
export function useGradeLuts(projectId: string, onReady: () => void): GradeLuts {
  const { data: files } = useProjectFiles(projectId);
  const filesRef = useRef(files);
  const readyRef = useRef(onReady);
  const [version, setVersion] = useState(0);
  useEffect(() => {
    filesRef.current = files;
    readyRef.current = onReady;
  }, [files, onReady]);
  const store = useMemo(
    () =>
        // eslint-disable-next-line react-hooks/refs -- called from a fetch callback, never during render
      createGradeLutStore({
        fetchText: async (fileId) => (await fetcher<ProjectFileContent>(`/api/v1/projects/${projectId}/files/${fileId}/content`)).content,
        onReady: () => {
          setVersion((v) => v + 1);
          readyRef.current();
        },
        sizeOf: (fileId) => filesRef.current?.find((f) => f.id === fileId)?.sizeBytes ?? null,
      }),
    [projectId],
  );
  // eslint-disable-next-line react-hooks/exhaustive-deps -- `version` is the point: a new identity when a file arrives
  const gradeLutStatus = useCallback((file: AssetRef) => store.status(file), [store, version]);
  return useMemo(() => ({ gradeLutFor: store.resolve, gradeLutStatus }), [store, gradeLutStatus]);
}
