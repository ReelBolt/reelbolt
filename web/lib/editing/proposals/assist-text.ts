/** Plain-language lines for the Assist panel: the price, the hourly allowance, and refusals. Pure. */
import { ApiError } from '@/lib/api/client';
import { creditsText } from '@/lib/editor/credit-estimate';
import { PROPOSAL_AGENTS, type ProposalAgent, type ProposalAvailability, type ProposalEstimate, type ProposalGrounding } from '@/lib/editing/sync/api';

/** "Included in your plan" / "about 3 credits" / null (billing off). */
export function proposalPriceLine(e: ProposalEstimate | null | undefined): string | null {
  if (!e || !e.billingEnabled) return null;
  if (e.included) return 'Included: this workspace uses its own AI provider, so no credits are used.';
  if (e.credits === undefined || e.credits === null || !(e.credits > 0)) return null;
  return `Asking costs about ${creditsText(e.credits)}.`;
}

/** "12 of 20 suggestions left this hour" (or the wait when none are left). */
export function proposalAllowanceLine(e: ProposalEstimate | null | undefined): string | null {
  if (!e || !(e.limit > 0)) return null;
  const left = Math.max(0, e.limit - e.used);
  if (left > 0) return `${left} of ${e.limit} suggestions left this hour.`;
  const mins = e.retryAfterSeconds ? Math.max(1, Math.ceil(e.retryAfterSeconds / 60)) : null;
  return `You have used all ${e.limit} suggestions for this hour.${mins ? ` Try again in about ${mins} minute${mins === 1 ? '' : 's'}.` : ''}`;
}

export function proposalCapReached(e: ProposalEstimate | null | undefined): boolean {
  return !!e && e.limit > 0 && e.used >= e.limit;
}

export function proposalUnaffordableLine(e: ProposalEstimate | null | undefined): string | null {
  if (!e || !e.billingEnabled || e.affordable) return null;
  const need = e.credits ? `about ${creditsText(e.credits)}` : 'more credits';
  const have = e.balanceCredits !== undefined && e.balanceCredits !== null ? ` You have ${creditsText(e.balanceCredits)}.` : '';
  return e.unavailableReason ?? `This needs ${need}.${have} Add credits to ask.`;
}

/** A refused ask in words (429 cap, 424 failed/timeout, 503 no provider); null for anything else. */
export function proposalErrorSentence(e: unknown): string | null {
  if (!(e instanceof ApiError)) return null;
  const body = (e.body ?? {}) as { message?: unknown; creditsUsed?: unknown; retryAfterSeconds?: unknown };
  const message = typeof body.message === 'string' && body.message.trim() ? body.message.trim() : null;
  if (e.status === 429 && e.code === 'PROPOSAL_RATE_LIMITED') {
    if (message) return message;
    const s = typeof body.retryAfterSeconds === 'number' ? Math.max(1, Math.ceil(body.retryAfterSeconds / 60)) : null;
    return `You have reached this hour's limit of suggestions.${s ? ` Try again in about ${s} minute${s === 1 ? '' : 's'}.` : ''}`;
  }
  if ((e.status === 424 || e.status === 502) && e.code === 'PROPOSAL_FAILED') {
    const used = typeof body.creditsUsed === 'number' && body.creditsUsed > 0 ? ` ${creditsText(body.creditsUsed)} used for the attempt.` : ' Nothing was charged.';
    return `${message ?? 'The assistant did not give a usable answer.'}${used}`;
  }
  if (e.status === 424 && e.code === 'PROPOSAL_TIMEOUT') return message ?? 'The AI took too long to answer. You were not charged; please try again.';
  if (e.status === 410 && e.code === 'PROPOSAL_EXPIRED') return message ?? 'This suggestion expired before it was collected. You were not charged; please ask again.';
  if (e.status === 429 && e.code === 'PROPOSALS_BUSY') return message ?? 'You already have suggestions being prepared. Wait for one to finish, then try again.';
  if (e.status === 409 && e.code === 'PROPOSAL_NEEDS_ANALYSIS') {
    return message ?? 'Analyse this footage first so the helper has something to go on. You were not charged.';
  }
  if (e.status === 503) return 'No chat provider is configured for this agent. An admin can add one under Admin → Inference providers.';
  return null;
}

/**
 * R11.2: the one line that says what the helper could see. The server's `note` is the truth for the
 * "length only" case; otherwise it counts the clips whose words and shots were used.
 */
export function groundingLine(g: ProposalGrounding | null | undefined): string | null {
  if (!g) return null;
  if (g.level === 'namesOnly') return g.note || 'Cutting by length only: analyse this footage first for a smarter cut.';
  const what = g.level === 'transcriptAndShots' ? 'the spoken words and shots' : g.level === 'transcriptOnly' ? 'the spoken words' : 'the shots';
  return `Used ${what} of ${g.clipsWithFacts} of ${g.clipsTotal} clip${g.clipsTotal === 1 ? '' : 's'}`;
}

/** R11.2: the helpers that cannot run on this range, each with the server's plain reason. */
export function unavailableHelpers(a: ProposalAvailability | null | undefined): Array<{ agent: ProposalAgent; reason: string }> {
  if (!a) return [];
  return PROPOSAL_AGENTS.flatMap((agent) => {
    const entry = a.agents[agent];
    return entry && !entry.available ? [{ agent, reason: entry.reason ?? 'This helper is not available for this range.' }] : [];
  });
}
