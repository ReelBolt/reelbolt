'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { useFormat, useT } from '@/i18n';
import { useActivity } from '@/lib/activity/ActivityProvider';
import { uploadFileWithProgress } from '../api/files';
import { isStorageQuotaError, storageQuotaInfo, storageQuotaMessage } from '../api/quota';
import { joinPath } from '../files/model';
import { explainUploadError } from '../files/upload-guidance';
import type { PendingUpload } from '../files/drop';

export type UploadStatus = 'queued' | 'uploading' | 'done' | 'error';

export interface UploadEntry {
  id: string;
  name: string;
  destination: string;
  size: number;
  progress: number;
  status: UploadStatus;
  error?: string;
  /**
   * True when the entry failed because the workspace is out of storage. The tray renders that as the
   * prescribed sentence with a link to Billing instead of a bare Retry, because retrying cannot help
   * until something is deleted or the plan changes.
   */
  quotaExceeded?: boolean;
}

const CONCURRENCY = 3;

interface Job extends UploadEntry {
  file: File;
  relativePath: string;
}

/**
 * Upload queue with per-file progress. Files go three at a time (a hundred parallel XHRs would
 * starve each other and the page), and a failed file stays in the list with a retry.
 */
export function useUploadQueue(projectId: string, onFileUploaded: () => void) {
  const [entries, setEntries] = useState<UploadEntry[]>([]);
  const jobs = useRef<Map<string, Job>>(new Map());
  const running = useRef(0);
  const onUploaded = useRef(onFileUploaded);
  const t = useT();
  const f = useFormat();
  const { trackJob, updateTrackedJob } = useActivity();
  // Latest tracker, so `pump` stays stable while the activity model re-renders.
  const tracker = useRef({ trackJob, updateTrackedJob });
  // Failure sentences are worded when the upload fails, in the language active at that moment.
  const wording = useRef({ t, f });
  useEffect(() => {
    onUploaded.current = onFileUploaded;
    wording.current = { t, f };
    tracker.current = { trackJob, updateTrackedJob };
  });

  const patch = useCallback((id: string, p: Partial<UploadEntry>) => {
    setEntries((cur) => cur.map((e) => (e.id === id ? { ...e, ...p } : e)));
    const j = jobs.current.get(id);
    if (j) Object.assign(j, p);
  }, []);

  const pumpRef = useRef<() => void>(() => {});
  const pump = useCallback(() => {
    while (running.current < CONCURRENCY) {
      const next = [...jobs.current.values()].find((j) => j.status === 'queued');
      if (!next) return;
      running.current += 1;
      patch(next.id, { status: 'uploading', progress: 0, error: undefined, quotaExceeded: false });
      // Every upload is also a tracked activity job, so it shows in the global tray.
      tracker.current
        .trackJob({ type: 'upload', title: next.name, projectId }, (_signal, activityId) =>
          uploadFileWithProgress(
            projectId,
            next.file,
            (pct) => {
              patch(next.id, { progress: pct });
              tracker.current.updateTrackedJob(activityId, { progress: pct / 100 });
            },
            { relativePath: next.relativePath },
          ),
        )
        .then(() => {
          patch(next.id, { status: 'done', progress: 100 });
          onUploaded.current();
        })
        .catch((err: unknown) => patch(next.id, {
          status: 'error',
          // A full workspace gets the one prescribed sentence and its own state; anything else keeps
          // the server's own message.
          error: isStorageQuotaError(err)
            ? storageQuotaMessage(storageQuotaInfo(err), wording.current.t, wording.current.f)
            : explainUploadError(err, wording.current.t),
          quotaExceeded: isStorageQuotaError(err),
        }))
        .finally(() => {
          running.current -= 1;
          pumpRef.current();
        });
    }
  }, [patch, projectId]);
  useEffect(() => {
    pumpRef.current = pump;
  }, [pump]);

  const enqueue = useCallback(
    (items: PendingUpload[], baseDir: string) => {
      const added: UploadEntry[] = items.map((it) => {
        const relativePath = joinPath(baseDir, it.relativePath);
        const job: Job = {
          id: crypto.randomUUID(),
          name: it.file.name,
          destination: relativePath.includes('/') ? relativePath.slice(0, relativePath.lastIndexOf('/')) : '',
          size: it.file.size,
          progress: 0,
          status: 'queued',
          file: it.file,
          relativePath,
        };
        jobs.current.set(job.id, job);
        return { id: job.id, name: job.name, destination: job.destination, size: job.size, progress: 0, status: 'queued' };
      });
      setEntries((cur) => [...cur, ...added]);
      pump();
    },
    [pump],
  );

  const retry = useCallback(
    (id: string) => {
      patch(id, { status: 'queued', progress: 0, error: undefined, quotaExceeded: false });
      pump();
    },
    [patch, pump],
  );

  const clearFinished = useCallback(() => {
    setEntries((cur) => cur.filter((e) => e.status === 'queued' || e.status === 'uploading'));
    for (const [id, j] of jobs.current) if (j.status === 'done' || j.status === 'error') jobs.current.delete(id);
  }, []);

  return { entries, enqueue, retry, clearFinished };
}
