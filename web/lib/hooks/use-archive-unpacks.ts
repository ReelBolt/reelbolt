'use client';

import { useEffect, useRef } from 'react';
import useSWR from 'swr';
import { fetcher } from '../api/client';
import type { ArchiveUnpack } from '../api/files';

export function hasActiveUnpack(rows: ArchiveUnpack[] | undefined | null): boolean {
  return !!rows && rows.some((r) => r.status === 'Pending' || r.status === 'Running');
}

/**
 * The project's recent zip unpacks. Polls every 3 s while one is active, and calls `onDone` once
 * when an unpack seen active turns Done so the file list can refresh.
 */
export function useArchiveUnpacks(projectId: string, onDone?: () => void) {
  const swr = useSWR<ArchiveUnpack[]>(
    projectId ? `/api/v1/projects/${projectId}/archive-unpacks` : null,
    fetcher,
    { refreshInterval: (data) => (hasActiveUnpack(data) ? 3000 : 0) },
  );
  const seen = useRef<Set<string> | null>(null);
  const cb = useRef(onDone);
  useEffect(() => { cb.current = onDone; });
  const data = swr.data;
  useEffect(() => {
    if (!data) return;
    // The first answer is the baseline: unpacks that finished before this tab looked are not news. After that, any
    // unpack that shows up already Done (a small zip can finish between two checks) or turns Done counts once.
    if (seen.current === null) {
      seen.current = new Set(data.filter((r) => r.status === 'Done').map((r) => r.id));
      return;
    }
    let finished = false;
    for (const r of data) {
      if (r.status === 'Done' && !seen.current.has(r.id)) {
        seen.current.add(r.id);
        finished = true;
      }
    }
    if (finished) cb.current?.();
  }, [data]);
  return swr;
}
