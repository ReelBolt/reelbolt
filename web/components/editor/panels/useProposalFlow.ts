import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { msg, type Msg } from '@/i18n/msg';
import type { EditOp } from '@/lib/editing/engine';
import { refusalSentence } from '@/lib/editing/refusal';
import type { EditTimeline, Item } from '@/lib/editing/types';
import { createEditTimelineVersion, estimateEditProposal, fetchProposalAvailability, requestEditProposal, type ProposalAvailability, type ProposalEstimate, type EditProposalRequest, type EditProposalResponse, type ProposalAgent } from '@/lib/editing/sync/api';
import { ghostDiff, planProposal, ProposalNeedsAcknowledgement, ProposalTooManyCuts, checkCutCaps, type ProposalGhost } from '@/lib/editing/proposals/apply-proposal';
import { notifyInsufficientCredits } from '@/components/billing/insufficientCreditsNotification';
import { ApiError } from '@/lib/api/client';
import { useDocumentSourceStatuses, type AnalysisKind } from '@/lib/editing/api/media-analysis';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { proposalCapReached } from '@/lib/editing/proposals/assist-text';
import { analysisNeedOf, didLineMsg, swapsRefusedMsg, groundingLineMsg, proposalAllowanceLineMsg, proposalErrorMsg, proposalPriceLineMsg, proposalUnaffordableLineMsg, unavailableHelpersMsg } from '@/lib/editing/proposals/labels-msg';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { onBatchLanded } from '@/lib/editing/store/editor-store';
import { clearProposalReview, groupFit, failProposal, keepRemainingGroups, landProposal, onProposalReviewRequested, peekProposalReview, proposalIntentOf, proposalLaunchEntry } from '@/lib/editing/background/proposal';
import { getResultsStore } from '@/lib/editing/background/results-store';
import { refusalSentenceMsg } from '@/lib/editing/labels-msg';

export const AGENT_LABELS: Record<ProposalAgent, { label: Msg; hint: Msg }> = {
  recut: { label: msg('editor.panels.agents.recut.label'), hint: msg('editor.panels.agents.recut.hint') },
  graphics: { label: msg('editor.panels.agents.graphics.label'), hint: msg('editor.panels.agents.graphics.hint') },
  grade: { label: msg('editor.panels.agents.grade.label'), hint: msg('editor.panels.agents.grade.hint') },
  sfx: { label: msg('editor.panels.agents.sfx.label'), hint: msg('editor.panels.agents.sfx.hint') },
  review: { label: msg('editor.panels.agents.review.label'), hint: msg('editor.panels.agents.review.hint') },
};

export interface ProposalFlowOptions {
  doc: EditTimeline;
  projectId: string;
  timelineId: string;
  readOnly: boolean;
  inOut: { inFrame: number | null; outFrame: number | null };
  onGhosts: (ghosts: { ghosts: ProposalGhost[]; newTracks: { id: string; name: string; kind: 'video' | 'audio' }[] } | null) => void;
  onApply: (ops: readonly EditOp[], label: string) => void;
  request?: (projectId: string, timelineId: string, body: EditProposalRequest) => Promise<EditProposalResponse>;
  estimate?: (projectId: string, timelineId: string, body: EditProposalRequest) => Promise<ProposalEstimate>;
  /** R11.2: injectable for tests. */
  availability?: (projectId: string, timelineId: string, range: { inFrame: number; outFrame: number } | null) => Promise<ProposalAvailability>;
  /** RM.13: when set, only this group's ghost is painted (the phone sheet highlights the group being looked at). */
  focusGroup?: number | null;
  /** The helper selected on mount (the AI tools list opens a specific one). */
  initialAgent?: ProposalAgent;
  /** The request's words, known before the panel opens (the Simple shell's ask box). Nothing is asked until the user presses Ask. */
  initialBrief?: string;
}

/**
 * The AI-assist flow (ask, price, ghost diff, accept, skip) shared by the desktop panel and the phone
 * review sheet, so the two never drift. State and actions only; each surface draws its own controls.
 */
/** How long the document must hold still before a content-cut proposal's ghosts are recomputed. */
export const GHOST_SETTLE_MS = 300;

/**
 * The ghost diff of an open proposal. A content-cut proposal is expanded against the document (up to 300
 * cuts), which is too heavy to redo on every timeline edit, so for those the document is settled for
 * {@link GHOST_SETTLE_MS} first; the view of the proposal or the selection changing recomputes right away.
 */
export function useGhostDiff(doc: EditTimeline, proposal: EditProposalResponse | null, selected: ReadonlySet<number>) {
  const hasCuts = !!proposal && proposal.groups.some((g) => (g.cuts?.length ?? 0) > 0);
  const [settled, setSettled] = useState(doc);
  // With no cut proposal open nothing is waiting to settle: follow the document, so one that arrives later starts from it.
  if (!hasCuts && settled !== doc) setSettled(doc);
  useEffect(() => {
    if (!hasCuts) return;
    const timer = setTimeout(() => setSettled(doc), GHOST_SETTLE_MS);
    return () => clearTimeout(timer);
  }, [doc, hasCuts]);
  const base = hasCuts ? settled : doc;
  return useMemo(() => (proposal ? ghostDiff(base, proposal, selected) : null), [base, proposal, selected]);
}

export function useProposalFlow({ doc, projectId, timelineId, readOnly, inOut, onGhosts, onApply, request = requestEditProposal, estimate = estimateEditProposal, availability: fetchAvailability = fetchProposalAvailability, focusGroup = null, initialAgent, initialBrief = '' }: ProposalFlowOptions) {
  const tx = useT('editor.panels.useProposalFlow');
  const tb = useT('editor-background');
  const tAll = useT();
  const [agent, setAgent] = useState<ProposalAgent>(initialAgent ?? 'recut');
  const [brief, setBrief] = useState(initialBrief);
  const [loading, setLoading] = useState(false);
  const [proposal, setProposalState] = useState<EditProposalResponse | null>(null);
  // The background record of the proposal being shown: it keeps the answer if this panel goes away.
  const results = getResultsStore(timelineId);
  const entryId = useRef<string | null>(null);
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  /** Setting null is a rejection: the kept copy goes with it. */
  const setProposal = useCallback(
    (next: EditProposalResponse | null) => {
      if (next === null && entryId.current) {
        keepRemainingGroups(results, entryId.current, null, 'discarded');
        entryId.current = null;
      }
      setProposalState(next);
    },
    [results],
  );
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [acknowledged, setAcknowledged] = useState<Set<string>>(new Set());
  const [error, setError] = useState<string | null>(null);
  const [showGhosts, setShowGhosts] = useState(true);
  // Footage the request needs analysed first (a 409 body's `analysis`, or a 200's `needsAnalysis`).
  const [analysisNeed, setAnalysisNeed] = useState<{ kinds: AnalysisKind[]; mediaRefs: string[] } | null>(null);

  // "Review" in the needs-your-OK list: load the kept answer into this panel (a request made before this mounted too).
  useEffect(() => {
    const load = () => {
      const id = peekProposalReview();
      if (!id) return;
      const entry = results.getEntries().find((e) => e.id === id);
      const kept = entry ? proposalIntentOf(entry).response : undefined;
      clearProposalReview();
      if (!entry || !kept) return;
      entryId.current = entry.id;
      setProposalState(kept);
      setAnalysisNeed(analysisNeedOf(kept.needsAnalysis));
      setSelected(new Set(kept.groups.map((_, i) => i)));
      setAcknowledged(new Set());
      setError(null);
    };
    const off = onProposalReviewRequested(load);
    queueMicrotask(load);
    return off;
  }, [results]);

  const items = useMemo(() => {
    const map = new Map<string, Item>();
    for (const t of doc.tracks) for (const i of t.items) map.set(i.id, i);
    return map;
  }, [doc]);

  const diff = useGhostDiff(doc, proposal, selected);
  useEffect(() => {
    onGhosts(proposal && showGhosts && diff ? { ghosts: focusGroup === null ? diff.ghosts : diff.ghosts.filter((g) => g.groupIndex === focusGroup), newTracks: diff.newTracks } : null);
  }, [proposal, showGhosts, diff, onGhosts, focusGroup]);
  useEffect(() => () => onGhosts(null), [onGhosts]);

  const range = inOut.inFrame !== null && inOut.outFrame !== null && inOut.outFrame > inOut.inFrame ? { inFrame: inOut.inFrame, outFrame: inOut.outFrame } : null;

  // R11.1: the price, the hourly allowance and what is sent, before asking. Debounced; nothing is charged or counted.
  const [quote, setQuote] = useState<ProposalEstimate | null>(null);
  const rangeKey = range ? `${range.inFrame}-${range.outFrame}` : '';
  useEffect(() => {
    if (readOnly) return;
    let live = true;
    const timer = window.setTimeout(() => {
      estimate(projectId, timelineId, { agent, range: rangeKey ? range : null, brief: brief.trim() || null })
        .then((a) => { if (live) setQuote(a); })
        .catch(() => { if (live) setQuote(null); });
    }, 400);
    return () => { live = false; window.clearTimeout(timer); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, timelineId, agent, rangeKey, brief, readOnly, estimate]);

  // R11.2: which helpers can run on this range, before anything is asked. Free; nothing is written.
  const [availability, setAvailability] = useState<ProposalAvailability | null>(null);
  const [availabilityTick, setAvailabilityTick] = useState(0);
  useEffect(() => {
    if (readOnly) return;
    let live = true;
    fetchAvailability(projectId, timelineId, rangeKey ? range : null)
      .then((a) => { if (live) setAvailability(a); })
      .catch(() => { if (live) setAvailability(null); });
    return () => { live = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, timelineId, rangeKey, readOnly, fetchAvailability, availabilityTick]);
  const refreshAvailability = () => setAvailabilityTick((n) => n + 1);
  // The footage lacks the analysis this helper reads: from the request's own answer, or from the availability hint.
  const hintNeed = analysisNeedOf(availability?.analysisHint);
  const needNow = analysisNeed ?? (hintNeed && (agent === 'recut' || agent === 'review') ? hintNeed : null);
  // After the user starts that analysis: wait for it, then ask again by itself.
  const [waitingForAnalysis, setWaitingForAnalysis] = useState(false);
  const needStatuses = useDocumentSourceStatuses(projectId, waitingForAnalysis && needNow ? needNow.mediaRefs : []);
  const analysisDone = !!needNow && !!needStatuses.data && needStatuses.data.length === needNow.mediaRefs.length && needStatuses.data.every((s) => s !== null && needNow.kinds.every((k) => s.kinds[k]?.state === 'ready'));
  const analysisFailed = !!needNow && !!needStatuses.data && needStatuses.data.some((s) => s !== null && needNow.kinds.some((k) => s.kinds[k]?.state === 'failed'));
  // A status that could not be read is a read error, not a failed analysis: say so and let the user check again.
  const analysisUnreadable = waitingForAnalysis && !analysisFailed && !!needStatuses.data && needStatuses.data.some((s) => s === null);
  const retryAnalysisCheck = () => void needStatuses.mutate();
  const onAnalysisStarted = () => {
    setWaitingForAnalysis(true);
    refreshAvailability();
  };
  const text = (m: Msg | null): string | null => (m ? tx.msg(m) : null);
  const unavailable = unavailableHelpersMsg(availability).map((u) => ({ agent: u.agent, reason: tx.msg(u.reason) }));
  const agentReason = unavailable.find((u) => u.agent === agent)?.reason ?? null;
  const analysisAvailable = (proposal?.analysisAvailable ?? quote?.analysisAvailable ?? availability?.analysisAvailable) === true;
  const grounding = text(groundingLineMsg(proposal?.grounding ?? availability?.agents[agent]?.grounding ?? quote?.grounding));

  const didLine = text(didLineMsg(proposal?.did));
  const swapsLine = text(swapsRefusedMsg(proposal));
  const priceLine = text(proposalPriceLineMsg(quote));
  const allowanceLine = text(proposalAllowanceLineMsg(quote));
  const unaffordableLine = text(proposalUnaffordableLineMsg(quote));
  const blockedLine = agentReason ?? (proposalCapReached(quote) ? allowanceLine : unaffordableLine);

  const skip = (index: number) => {
    if (!proposal) return;
    const groups = proposal.groups.filter((_, i) => i !== index);
    setSelected(new Set([...selected].filter((i) => i !== index).map((i) => (i > index ? i - 1 : i))));
    if (groups.length === 0) {
      if (entryId.current) keepRemainingGroups(results, entryId.current, null, 'discarded');
      entryId.current = null;
      setProposalState(null);
    } else {
      const next = { ...proposal, groups };
      if (entryId.current) keepRemainingGroups(results, entryId.current, next, 'discarded');
      setProposalState(next);
    }
  };

  const ask = async () => {
    setLoading(true);
    setError(null);
    setAnalysisNeed(null);
    setProposal(null);
    const launched = proposalLaunchEntry(doc, { agent, brief: brief.trim() || null, range }, `proposal-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`, new Date().toISOString());
    results.launch(launched);
    try {
      const response = await request(projectId, timelineId, { agent, range, brief: brief.trim() || null });
      landProposal(results, launched.id, response);
      if (!mounted.current) return;
      entryId.current = launched.id;
      setProposalState(response);
      setAnalysisNeed(analysisNeedOf(response.needsAnalysis));
      // The allowance moves by one; counted locally so the layout does not jump under the reviewer's hands.
      setQuote((q) => (q ? { ...q, used: q.used + 1 } : q));
      setSelected(new Set(response.groups.map((_, i) => i)));
      setAcknowledged(new Set());
    } catch (e) {
      failProposal(results, launched.id, e instanceof Error ? describeError(tAll, e) : null);
      if (!mounted.current) return;
      if (e instanceof ApiError && e.status === 409 && e.code === 'PROPOSAL_NEEDS_ANALYSIS') setAnalysisNeed(analysisNeedOf((e.body as { analysis?: unknown } | undefined)?.analysis));
      if (notifyInsufficientCredits(e)) {
        // Refused for lack of credits: the notification explains it and links to billing.
      } else setError(text(proposalErrorMsg(e)) ?? humanizeErrorT(tx, e, tx('theProposalCouldNot')));
    } finally {
      if (mounted.current) setLoading(false);
    }
  };

  const resumeAfterAnalysis = () => {
    setWaitingForAnalysis(false);
    setAnalysisNeed(null);
    refreshAvailability();
    void ask();
  };
  useEffect(() => {
    // Leaving the waiting state and asking again follows an external event (the analysis finishing).
    // eslint-disable-next-line react-hooks/set-state-in-effect
    if (waitingForAnalysis && analysisDone && !analysisFailed) resumeAfterAnalysis();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [waitingForAnalysis, analysisDone, analysisFailed]);


  const accepting = useRef(false);
  const accept = async (which: Set<number>): Promise<boolean> => {
    if (!proposal || accepting.current) return false;
    accepting.current = true;
    setError(null);
    try {
      // Groups whose part of the video changed since they were asked for, or that no longer fit, are not refused:
      // the paid answer stays in the "needs your OK" list, where "Apply what still fits" handles them.
      checkCutCaps(proposal.groups, which);
      const entry = entryId.current ? results.getEntries().find((e) => e.id === entryId.current) : undefined;
      const fit = groupFit(doc, proposal, entry ? proposalIntentOf(entry).placements : undefined, which, entry ? proposalIntentOf(entry).surroundings : undefined);
      if (fit.stale.length > 0 || fit.changed.length > 0) {
        setError(tb('panelStale'));
        return false;
      }
      const plan = planProposal(doc, proposal, which, acknowledged);
      // R02.4: keep the stored state as a version right before the suggestion lands. A failure here never blocks the edit.
      await createEditTimelineVersion(projectId, timelineId, { cause: 'beforeProposal' }).catch(() => undefined);
      const remaining = proposal.groups.filter((_, i) => !which.has(i));
      const id = entryId.current;
      let landed = false;
      // Recorded only when the batch really lands: the edit guard may hold it (Back keeps the suggestions), and
      // "Go on" lands it later through the same store call.
      onBatchLanded(plan.ops, () => {
        landed = true;
        if (id) keepRemainingGroups(results, id, remaining.length > 0 ? { ...proposal, groups: remaining } : null, 'applied');
        if (!mounted.current) return;
        if (remaining.length === 0) {
          if (entryId.current === id) entryId.current = null;
          setProposalState(null);
        } else {
          setProposalState({ ...proposal, groups: remaining });
          setSelected(new Set());
        }
      });
      onApply(plan.ops, `${AGENT_LABELS[proposal.agent]?.label ?? 'AI'} proposal: ${which.size} of ${proposal.groups.length}`);
      return landed;
    } catch (e) {
      if (e instanceof ProposalNeedsAcknowledgement) setError(e.message);
      else if (e instanceof ProposalTooManyCuts) setError(tx.msg(msg('editor-ai-tools.recut.tooManyCuts', { count: e.count, label: e.label })));
      else setError(tb('panelStale'));
      return false;
    } finally {
      accepting.current = false;
    }
  };

  return { didLine, swapsLine, analysisNeed: needNow, waitingForAnalysis: waitingForAnalysis && !analysisFailed, analysisFailed, analysisUnreadable, retryAnalysisCheck, onAnalysisStarted, unaffordableLine, agent, setAgent, brief, setBrief, loading, proposal, setProposal, selected, setSelected, acknowledged, setAcknowledged, error, setError, showGhosts, setShowGhosts, items, diff, range, quote, priceLine, allowanceLine, blockedLine, skip, ask, accept, availability, unavailable, agentReason, analysisAvailable, grounding, refreshAvailability };
}
