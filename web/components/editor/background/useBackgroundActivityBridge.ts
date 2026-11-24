'use client';

import { useContext, useEffect, useRef } from 'react';
// The context, not useRouter(): the editor also mounts without an app router (tests, embedded previews), where
// useRouter() throws. With no router, "open the editor to decide" falls back to a full page load.
import { AppRouterContext } from 'next/dist/shared/lib/app-router-context.shared-runtime';
import { useActivity } from '@/lib/activity/ActivityProvider';
import { isAudioCleanupEntry, isCleanupReady, settleCleanup } from '@/lib/editing/background/audio-cleanup';
import { resolveObjectOnServer, settleLanded } from '@/lib/editing/background/object-effect';
import { applyWhatFits, isProposalEntry, proposalIntentOf, requestProposalReview } from '@/lib/editing/background/proposal';
import { classifyProposalEntry } from '@/lib/editing/background/proposal-review';
import { createEditTimelineVersion } from '@/lib/editing/sync/api';
import { AGENT_LABELS } from '../panels/useProposalFlow';
import { useBackgroundResults, type BackgroundEntry } from '@/lib/editing/background/results-store';
import type { EditorStore } from '@/lib/editing/store/editor-store';
import { useT } from '@/i18n';

interface Mirror {
  jobId: string;
  phase: 'running' | 'review';
  settle: { resolve: () => void; reject: (error: Error) => void };
  /** The store already moved on (or the bridge is the one ending it): the job's own callbacks must do nothing. */
  silent: boolean;
  cancelling: boolean;
  /** The tool's label, for the "open the editor" row after the editor is left. */
  tool: string;
}

/**
 * Mirrors every background result of the open timeline into the activity model so the global tray shows it:
 * a running entry is a client job, a needsReview entry is "Needs your OK" (apply to what is left / discard call the
 * results store), and an applied / discarded / failed entry ends the job. The store stays the source of truth.
 * Mounted once by the editor workspace.
 */
export function useBackgroundActivityBridge(args: { store: EditorStore; projectId: string; timelineId: string }): void {
  const { store: editor, projectId, timelineId } = args;
  const { trackJob, markNeedsReview, resolveTrackedJob, allJobs, cancel } = useActivity();
  const { entries, store: results } = useBackgroundResults(timelineId);
  const t = useT('editor-background');
  const tp = useT('editor.panels.useProposalFlow');
  const router = useContext(AppRouterContext);
  const href = `/editor/${projectId}/timeline/${timelineId}`;
  const mirrors = useRef(new Map<string, Mirror>());
  const labels = () => ({ applyLeft: t('labelApplyLeft'), applyFits: t('labelApplyFits'), objectTool: t('tool.objectEffect'), cleanupTool: t('tool.audioCleanup'), proposalTool: t('tool.proposal'), otherTool: t('tool.other'), agent: (a: keyof typeof AGENT_LABELS) => (AGENT_LABELS[a] ? tp.msg(AGENT_LABELS[a].label) : '') });
  const latest = useRef({ allJobs, cancel, entries, results, labels: labels(), router, href, markNeedsReview });
  useEffect(() => {
    latest.current = { allJobs, cancel, entries, results, labels: labels(), router, href, markNeedsReview };
  });

  useEffect(() => {
    if (!results) return;
    const store = results;
    const live = mirrors.current;
    const entryOf = (id: string): BackgroundEntry | undefined => latest.current.entries.find((e) => e.id === id);
    const tell = (e: BackgroundEntry, action: 'applied' | 'discarded') =>
      void resolveObjectOnServer(projectId, e.anchor.assetKey, e.requestId, action, editor.getState().revision).catch(() => undefined);
    const stillWaiting = (id: string): boolean => store.getEntries().find((e) => e.id === id)?.state === 'needsReview';
    const toolName = (e: BackgroundEntry): string => {
      const l = latest.current.labels;
      return e.tool === 'objectEffect' ? l.objectTool : isAudioCleanupEntry(e) ? l.cleanupTool : isProposalEntry(e) ? l.proposalTool : l.otherTool;
    };
    /** Does what "Apply" means for this tool. The caller checks afterwards whether the entry really left needsReview. */
    const applyEntry = async (id: string): Promise<void> => {
      const entry = entryOf(id);
      if (!entry) return;
      const l = latest.current.labels;
      if (entry.tool === 'objectEffect') {
        settleLanded({ results: store, editor, entry, label: l.applyLeft, autoApply: false, onApplied: (e) => tell(e, 'applied') });
      } else if (isAudioCleanupEntry(entry)) {
        settleCleanup({ results: store, editor, entry, autoApply: false });
      } else if (isProposalEntry(entry)) {
        if (classifyProposalEntry(editor.getState().doc, entry).kind === 'ready') requestProposalReview(entry.id);
        else await applyWhatFits({ results: store, editor, entry, label: l.applyFits, beforeApply: () => createEditTimelineVersion(projectId, editor.getState().timelineId, { cause: 'beforeProposal' }) });
      }
    };
    const discardEntry = (id: string): void => {
      const entry = entryOf(id);
      store.resolve(id, 'discarded');
      if (entry?.tool === 'objectEffect') tell(entry, 'discarded');
    };
    const title = (e: BackgroundEntry): string => {
      const l = latest.current.labels;
      if (isProposalEntry(e)) return l.agent(proposalIntentOf(e).agent) || l.proposalTool;
      const prompt = typeof e.intent.prompt === 'string' ? e.intent.prompt : '';
      return prompt ? `${toolName(e)}: ${prompt}` : toolName(e);
    };

    for (const entry of entries) {
      const m = live.get(entry.id);
      if (!m) {
        if (entry.state !== 'running' && entry.state !== 'needsReview') continue;
        const created: Mirror = { jobId: '', phase: 'running', settle: { resolve: () => undefined, reject: () => undefined }, silent: false, cancelling: false, tool: toolName(entry) };
        trackJob(
          {
            type: entry.tool === 'objectEffect' ? 'objectAnalysis' : isAudioCleanupEntry(entry) ? 'audioCleanup' : isProposalEntry(entry) ? 'proposal' : 'generic',
            title: title(entry),
            projectId,
            id: `bg:${timelineId}:${entry.id}`,
            href,
            // The title carries the user's own words (the object): tray row only, never a toast or OS notification.
            quietTitle: true,
            executionId: entry.executionId ?? null,
            // Only magic blur can be stopped from here; a proposal's request and a cleanup run cannot be recalled.
            cancel:
              entry.tool === 'objectEffect'
                ? async () => {
                    created.cancelling = true;
                    if (!created.silent) results.resolve(entry.id, 'discarded');
                  }
                : undefined,
          },
          (_signal, jobId) =>
            new Promise<void>((resolve, reject) => {
              created.jobId = jobId;
              created.settle = { resolve, reject };
            }),
        ).catch(() => undefined);
        live.set(entry.id, created);
        if (entry.state === 'needsReview') toReview(created, entry);
        continue;
      }
      if (m.phase === 'running' && entry.state === 'needsReview') toReview(m, entry);
      else if (m.phase === 'running' && entry.state === 'failed') {
        m.settle.reject(new Error(entry.failure?.message ?? 'failed'));
        live.delete(entry.id);
      } else if (m.phase === 'running' && entry.state === 'applied') {
        m.settle.resolve();
        live.delete(entry.id);
      } else if (m.phase === 'running' && entry.state === 'discarded') {
        if (!m.cancelling) {
          m.cancelling = true;
          m.silent = true;
          const job = latest.current.allJobs.find((j) => j.id === m.jobId);
          if (job) void latest.current.cancel(job);
        }
        m.settle.reject(new Error('discarded'));
        live.delete(entry.id);
      } else if (m.phase === 'review' && (entry.state === 'applied' || entry.state === 'discarded')) {
        m.silent = true;
        void resolveTrackedJob(m.jobId, entry.state);
        live.delete(entry.id);
      }
    }

    function toReview(m: Mirror, entry: BackgroundEntry): void {
      m.phase = 'review';
      markNeedsReview(m.jobId, {
        summaryKey: isProposalEntry(entry) ? 'editor.proposalReady' : isCleanupReady(entry) ? 'editor.audioCleanupReady' : 'editor.needsReview',
        summaryValues: { tool: toolName(entry) },
        onApply: async () => {
          if (m.silent) return;
          await applyEntry(entry.id);
          // Not applied (read-only, refused, or Review was only opened): the job keeps waiting.
          if (!m.silent && stillWaiting(entry.id)) throw new Error('not applied');
        },
        onDiscard: () => {
          if (!m.silent) discardEntry(entry.id);
        },
      });
      m.settle.resolve();
    }
  }, [entries, results, editor, projectId, timelineId, href, trackJob, markNeedsReview, resolveTrackedJob]);

  // Leaving the editor: its store and sync are gone, so a result must not be applied from outside. Every result
  // that waits for the user's OK becomes "open the editor to decide"; reopening re-attaches to the same tray job.
  useEffect(() => {
    const live = mirrors.current;
    return () => {
      const { router: nav, href: to, markNeedsReview: mark } = latest.current;
      const go = (): never => {
        if (nav) nav.push(to);
        else if (typeof window !== 'undefined') window.location.assign(`${process.env.NEXT_PUBLIC_BASE_PATH ?? '/app'}${to}`);
        throw new Error('open the editor to decide');
      };
      for (const m of live.values()) {
        if (m.phase !== 'review') continue;
        mark(m.jobId, { summaryKey: 'editor.openToDecide', summaryValues: { tool: m.tool }, onApply: go, onDiscard: go });
      }
      live.clear();
    };
  }, []);
}
