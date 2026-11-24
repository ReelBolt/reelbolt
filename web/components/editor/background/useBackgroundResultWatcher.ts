'use client';

import { useEffect, useRef } from 'react';
import { mutate as globalMutate } from 'swr';
import { ApiError } from '@/lib/api/client';
import { fetchObject, isTerminal, POLL_ERROR_LIMIT } from '@/lib/editing/api/objects';
import { isCleanupFailed } from '@/lib/editing/cleanup/cleanup-api';
import { fetchCleanup, isAudioCleanupEntry, landCleanupResult } from '@/lib/editing/background/audio-cleanup';
import { entryFromServerItem, fetchPendingServerResults, landObjectResult, resolveObjectOnServer } from '@/lib/editing/background/object-effect';
import { sweepInterruptedProposals } from '@/lib/editing/background/proposal';
import { getResultsStore, setActiveTimeline, type BackgroundEntry } from '@/lib/editing/background/results-store';
import type { EditorStore } from '@/lib/editing/store/editor-store';
import { useT } from '@/i18n';

const POLL_MS = 1500;
const POLL_MAX_MS = 12000;
const ADOPT_MS = 30000;

/** A refusal that asking again cannot fix (404, 403, other 4xx but not 408/429). */
const isFatal = (error: unknown): boolean => error instanceof ApiError && error.status >= 400 && error.status < 500 && error.status !== 408 && error.status !== 429;

/**
 * Mounted ONCE by the editor workspace, never by a panel. It watches every running background job of this timeline
 * and, when one lands, rebases it onto the CURRENT document and applies it as ONE undo step (or parks it as "needs
 * your OK"). Because it lives with the workspace, selecting something else or closing the inspector drops nothing.
 */
export function useBackgroundResultWatcher(args: { store: EditorStore; projectId: string; timelineId: string }): void {
  const { store: editor, projectId, timelineId } = args;
  const t = useT('editor.panels.objectEffects');
  const label = t('labelAdd');
  const labelRef = useRef(label);
  useEffect(() => {
    labelRef.current = label;
  }, [label]);

  useEffect(() => {
    setActiveTimeline(timelineId, () => editor.getState().doc);
    return () => setActiveTimeline(null);
  }, [timelineId, editor]);

  useEffect(() => {
    const results = getResultsStore(timelineId);
    const timers = new Map<string, number>();
    const errors = new Map<string, number>();
    let disposed = false;
    // A proposal request that died with the previous page can never land; its clips must not stay marked busy.
    sweepInterruptedProposals(results);

    const settleOnServer = (entry: BackgroundEntry) => {
      void resolveObjectOnServer(projectId, entry.anchor.assetKey, entry.requestId, 'applied', editor.getState().revision).catch(() => undefined);
    };

    const poll = (entry: BackgroundEntry) => {
      const run = async () => {
        timers.delete(entry.id);
        if (disposed) return;
        const live = results.getEntries().find((e) => e.id === entry.id);
        if (!live || live.state !== 'running' || live.stalled) return;
        try {
          if (isAudioCleanupEntry(live)) {
            const job = await fetchCleanup(projectId, live);
            if (disposed) return;
            errors.delete(entry.id);
            if (job.state === 'ready' || isCleanupFailed(job)) {
              landCleanupResult({ results, editor, entry: live, job });
              return;
            }
          } else {
            const request = await fetchObject(projectId, live.anchor.assetKey, live.requestId);
            if (disposed) return;
            errors.delete(entry.id);
            if (isTerminal(request.status)) {
              landObjectResult({ results, editor, entry: live, request, label: labelRef.current, onApplied: settleOnServer });
              void globalMutate((key) => Array.isArray(key) && key[0] === 'object-list');
              return;
            }
          }
        } catch (error) {
          if (disposed) return;
          const count = (errors.get(entry.id) ?? 0) + 1;
          errors.set(entry.id, count);
          if (isFatal(error) || count >= POLL_ERROR_LIMIT) {
            results.update(entry.id, { stalled: true });
            return;
          }
        }
        const delay = Math.min(POLL_MAX_MS, POLL_MS * 2 ** (errors.get(entry.id) ?? 0));
        timers.set(entry.id, window.setTimeout(() => void run(), delay));
      };
      timers.set(entry.id, window.setTimeout(() => void run(), 0));
    };

    const sync = () => {
      for (const entry of results.getEntries()) {
        if (entry.tool !== 'objectEffect' && !isAudioCleanupEntry(entry)) continue;
        const wanted = entry.state === 'running' && !entry.stalled;
        if (wanted && !timers.has(entry.id)) {
          errors.delete(entry.id);
          poll(entry);
        } else if (!wanted && timers.has(entry.id)) {
          window.clearTimeout(timers.get(entry.id));
          timers.delete(entry.id);
        }
      }
    };
    const unsubscribe = results.subscribe(sync);
    sync();

    // Results the assistant started: adopt each once, as a landed entry that waits for the user's OK.
    let adoptTimer = 0;
    let absent = false;
    const adopt = async () => {
      try {
        const items = await fetchPendingServerResults(projectId, timelineId);
        if (disposed) return;
        if (items === null) {
          absent = true;
          return;
        }
        const known = new Set(results.getEntries().map((e) => e.requestId));
        for (const item of items) if (!known.has(item.requestId)) results.launch(entryFromServerItem(editor.getState().doc, item));
      } catch {
        /* a failed read is tried again on the next tick */
      }
      if (!disposed && !absent) adoptTimer = window.setTimeout(() => void adopt(), ADOPT_MS);
    };
    void adopt();

    return () => {
      disposed = true;
      unsubscribe();
      for (const id of timers.values()) window.clearTimeout(id);
      window.clearTimeout(adoptTimer);
    };
  }, [editor, projectId, timelineId]);
}
