'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import type { FlushResult } from '../deliver/DeliverWorkspace';
import { estimateLoudness, startAudition, startLoudness, waitForAudioJob, type AudioJob, type AudioJobStart } from '@/lib/editing/audio/audio-api';
import { useT } from '@/i18n';

export type AudioJobState =
  | { kind: 'idle' }
  | { kind: 'saving' }
  | { kind: 'confirm'; credits: number }
  | { kind: 'running'; executionId?: string }
  | { kind: 'done'; job: AudioJob }
  | { kind: 'error'; message: string };

export interface AudioJobApi {
  start: (kind: 'loudness' | 'audition', projectId: string, timelineId: string, revision: number, range?: { inFrame: number; outFrame: number }) => Promise<AudioJobStart>;
  estimate: (projectId: string, timelineId: string, revision: number) => Promise<{ billingEnabled: boolean; credits?: number | null }>;
  wait: (projectId: string, timelineId: string, executionId: string, signal: AbortSignal) => Promise<AudioJob>;
}

export const defaultAudioJobApi: AudioJobApi = {
  start: (kind, p, t, rev, range) => (kind === 'loudness' ? startLoudness(p, t, rev) : startAudition(p, t, range ?? { inFrame: 0, outFrame: 1 }, rev)),
  estimate: (p, t, rev) => estimateLoudness(p, t, rev),
  wait: (p, t, id, signal) => waitForAudioJob(p, t, id, { signal }),
};

/**
 * One audio job at a time: save the timeline, (for a measurement, show the credit estimate and
 * wait for a yes), start the job, poll it. `run` resolves with the finished job or null.
 */
export function useAudioJob(
  kind: 'loudness' | 'audition',
  ctx: { projectId: string; timelineId: string; flushSave?: () => Promise<FlushResult>; revision: number },
  api: AudioJobApi = defaultAudioJobApi,
) {
  const [state, setState] = useState<AudioJobState>({ kind: 'idle' });
  const abort = useRef<AbortController | null>(null);
  const pending = useRef<{ range?: { inFrame: number; outFrame: number }; resolve: (j: AudioJob | null) => void } | null>(null);
  const t = useT('editor-audio.job');
  const ctxRef = useRef(ctx);
  useEffect(() => {
    ctxRef.current = ctx;
  });
  useEffect(() => () => abort.current?.abort(), []);

  const launch = useCallback(
    async (range: { inFrame: number; outFrame: number } | undefined, skipEstimate: boolean): Promise<AudioJob | null> => {
      const { projectId, timelineId, flushSave, revision } = ctxRef.current;
      abort.current?.abort();
      const controller = new AbortController();
      abort.current = controller;
      try {
        setState({ kind: 'saving' });
        const flushed = flushSave ? await flushSave() : ({ ok: true, revision } as const);
        if (!flushed.ok) {
          setState({ kind: 'error', message: flushed.reason || t('saveFirstFailed') });
          return null;
        }
        if (kind === 'loudness' && !skipEstimate) {
          const est = await api.estimate(projectId, timelineId, flushed.revision).catch(() => null);
          if (est?.billingEnabled && typeof est.credits === 'number' && est.credits > 0) {
            setState({ kind: 'confirm', credits: est.credits });
            return await new Promise<AudioJob | null>((resolve) => {
              pending.current = { range, resolve };
            });
          }
        }
        setState({ kind: 'running' });
        const started = await api.start(kind, projectId, timelineId, flushed.revision, range);
        setState({ kind: 'running', executionId: started.executionId });
        const job = await api.wait(projectId, timelineId, started.executionId, controller.signal);
        setState({ kind: 'done', job });
        return job;
      } catch (error) {
        if (controller.signal.aborted) return null;
        setState({ kind: 'error', message: humanizeErrorT(t, error, kind === 'loudness' ? t('loudnessFailed') : t('previewFailed')) });
        return null;
      }
    },
    [api, kind, t],
  );

  const run = useCallback((range?: { inFrame: number; outFrame: number }) => launch(range, false), [launch]);
  const confirm = useCallback(async () => {
    const p = pending.current;
    pending.current = null;
    if (!p) return;
    p.resolve(await launch(p.range, true));
  }, [launch]);
  const cancel = useCallback(() => {
    pending.current?.resolve(null);
    pending.current = null;
    abort.current?.abort();
    setState({ kind: 'idle' });
  }, []);
  const reset = useCallback(() => setState({ kind: 'idle' }), []);

  return { state, run, confirm, cancel, reset };
}
