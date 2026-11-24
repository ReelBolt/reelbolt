import { msg, type Msg } from '@/i18n/msg';
import { ApiError } from '@/lib/api/client';
import { ANALYSIS_KINDS, type AnalysisKind } from '@/lib/editing/api/media-analysis';
import type { EditProposalResponse, ProposalAvailability, ProposalEstimate, ProposalGrounding } from '@/lib/editing/sync/api';
import { PROPOSAL_AGENTS, type ProposalAgent } from '@/lib/editing/sync/api';
import { rawMsg } from '../sentence';

/** i18n counterparts of assist-text.ts. Catalogue `editor-lib.assist.*`; money figures go through ICU plurals. */

const K = 'editor-lib.assist';
const rounded = (n: number): number => Math.round(n * 100) / 100;

export function proposalPriceLineMsg(e: ProposalEstimate | null | undefined): Msg | null {
  if (!e || !e.billingEnabled) return null;
  if (e.included) return msg(`${K}.included`);
  if (e.credits === undefined || e.credits === null || !(e.credits > 0)) return null;
  return msg(`${K}.price`, { credits: rounded(e.credits) });
}
export function proposalAllowanceLineMsg(e: ProposalEstimate | null | undefined): Msg | null {
  if (!e || !(e.limit > 0)) return null;
  const left = Math.max(0, e.limit - e.used);
  if (left > 0) return msg(`${K}.left`, { left, limit: e.limit });
  const mins = e.retryAfterSeconds ? Math.max(1, Math.ceil(e.retryAfterSeconds / 60)) : null;
  return mins ? msg(`${K}.allUsedWait`, { limit: e.limit, mins }) : msg(`${K}.allUsed`, { limit: e.limit });
}
export function proposalUnaffordableLineMsg(e: ProposalEstimate | null | undefined): Msg | null {
  if (!e || !e.billingEnabled || e.affordable) return null;
  if (e.unavailableReason) return rawMsg(e.unavailableReason);
  const hasBalance = e.balanceCredits !== undefined && e.balanceCredits !== null;
  const key = e.credits ? (hasBalance ? 'unaffordableHave' : 'unaffordable') : hasBalance ? 'unaffordableMoreHave' : 'unaffordableMore';
  return msg(`${K}.${key}`, { credits: e.credits ? rounded(e.credits) : undefined, balance: hasBalance ? rounded(e.balanceCredits as number) : undefined });
}
/** proposalErrorSentence(e): null for anything that is not one of the listed refusals. */
export function proposalErrorMsg(e: unknown): Msg | null {
  if (!(e instanceof ApiError)) return null;
  const body = (e.body ?? {}) as { message?: unknown; creditsUsed?: unknown; retryAfterSeconds?: unknown };
  const message = typeof body.message === 'string' && body.message.trim() ? body.message.trim() : null;
  if (e.status === 429 && e.code === 'PROPOSAL_RATE_LIMITED') {
    if (message) return rawMsg(message);
    const mins = typeof body.retryAfterSeconds === 'number' ? Math.max(1, Math.ceil(body.retryAfterSeconds / 60)) : null;
    return mins ? msg(`${K}.rateWait`, { mins }) : msg(`${K}.rate`);
  }
  if ((e.status === 424 || e.status === 502) && e.code === 'PROPOSAL_FAILED') {
    const text: Msg | string = message ?? msg(`${K}.failedDefault`);
    return typeof body.creditsUsed === 'number' && body.creditsUsed > 0 ? msg(`${K}.failedUsed`, { message: text, credits: rounded(body.creditsUsed) }) : msg(`${K}.failedFree`, { message: text });
  }
  if (e.status === 424 && e.code === 'PROPOSAL_TIMEOUT') return message ? rawMsg(message) : msg(`${K}.timeout`);
  if (e.status === 410 && e.code === 'PROPOSAL_EXPIRED') return message ? rawMsg(message) : msg(`${K}.expired`);
  if (e.status === 429 && e.code === 'PROPOSALS_BUSY') return message ? rawMsg(message) : msg(`${K}.busy`);
  if (e.status === 409 && e.code === 'PROPOSAL_NEEDS_ANALYSIS') return message ? rawMsg(message) : msg(`${K}.needsAnalysis`);
  if (e.status === 503) return msg(`${K}.noProvider`);
  return null;
}
export function groundingLineMsg(g: ProposalGrounding | null | undefined): Msg | null {
  if (!g) return null;
  if (g.level === 'namesOnly') return g.note ? rawMsg(g.note) : msg(`${K}.lengthOnly`);
  const what = msg(`${K}.what.${g.level === 'transcriptAndShots' ? 'transcriptAndShots' : g.level === 'transcriptOnly' ? 'transcriptOnly' : 'shots'}`);
  return msg(`${K}.grounding`, { what, with: g.clipsWithFacts, total: g.clipsTotal });
}
export function unavailableHelpersMsg(a: ProposalAvailability | null | undefined): Array<{ agent: ProposalAgent; reason: Msg }> {
  if (!a) return [];
  return PROPOSAL_AGENTS.flatMap((agent) => {
    const entry = a.agents[agent];
    return entry && !entry.available ? [{ agent, reason: entry.reason ? rawMsg(entry.reason) : msg(`${K}.helperUnavailable`) }] : [];
  });
}

const DID_CUT_KEYS: Record<NonNullable<NonNullable<EditProposalResponse['did']>['cuts']>[number]['kind'], string> = {
  speech: 'editor-ai-tools.recut.did.cut',
  quiet: 'editor-ai-tools.recut.did.quiet',
  fillers: 'editor-ai-tools.recut.did.fillers',
  lines: 'editor-ai-tools.recut.did.lines',
  keep: 'editor-ai-tools.recut.did.keep',
};

/**
 * The plain "what it did" line of a recut, from the server's counts (`did`): "What it did: Cut 7 spoken
 * passages (12.4 s), muted 2 clips." Null when nothing was counted.
 */
export function didLineMsg(did: EditProposalResponse['did']): Msg | null {
  if (!did) return null;
  const parts: Msg[] = [];
  const r1 = (n: number): number => Math.round(n * 10) / 10;
  if (did.cuts && did.cuts.length > 0) {
    for (const c of did.cuts) if (c.spans > 0) parts.push(msg(DID_CUT_KEYS[c.kind], { count: c.spans, seconds: r1(c.seconds) }));
  } else if (did.cut > 0) parts.push(msg('editor-ai-tools.recut.did.cut', { count: did.cut, seconds: r1(did.cutSeconds) }));
  if (did.trim > 0) parts.push(msg('editor-ai-tools.recut.did.trim', { count: did.trim }));
  if (did.mute > 0) parts.push(msg('editor-ai-tools.recut.did.mute', { count: did.mute }));
  if (did.remove > 0) parts.push(msg('editor-ai-tools.recut.did.remove', { count: did.remove }));
  if (did.other > 0) parts.push(msg('editor-ai-tools.recut.did.other', { count: did.other }));
  if (parts.length === 0) return null;
  const joined = parts.slice(1).reduce<Msg>((head, tail) => msg('editor-ai-tools.recut.did.join', { head, tail }), parts[0]);
  return msg('editor-ai-tools.recut.did.line', { parts: joined });
}

/** "Skipped changes that did not match your request." when the server dropped swapped-in actions. */
export function swapsRefusedMsg(proposal: Pick<EditProposalResponse, 'swapsRefused'> | null | undefined): Msg | null {
  return proposal && typeof proposal.swapsRefused === 'number' && proposal.swapsRefused > 0 ? msg('editor-ai-tools.recut.swapsRefused') : null;
}

const ANALYSIS_KIND_SET: ReadonlySet<string> = new Set<AnalysisKind>(ANALYSIS_KINDS);
const MEDIA_REF = /^(file|step_result):[0-9a-f-]{36}$/i;

/** What to analyse first, from a 409 `analysis` body or a 200 `needsAnalysis`: known kinds and well-formed refs only. */
export function analysisNeedOf(raw: unknown): { kinds: AnalysisKind[]; mediaRefs: string[] } | null {
  if (typeof raw !== 'object' || raw === null) return null;
  const { kinds, mediaRefs: refsA, missingRefs } = raw as { kinds?: unknown; mediaRefs?: unknown; missingRefs?: unknown };
  const mediaRefs = refsA ?? missingRefs;
  const k = Array.isArray(kinds) ? kinds.filter((x): x is AnalysisKind => typeof x === 'string' && ANALYSIS_KIND_SET.has(x)) : [];
  const refs = Array.isArray(mediaRefs) ? mediaRefs.filter((x): x is string => typeof x === 'string' && MEDIA_REF.test(x)) : [];
  return k.length > 0 && refs.length > 0 ? { kinds: [...new Set(k)], mediaRefs: [...new Set(refs)] } : null;
}
