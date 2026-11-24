import { describe, expect, it } from 'vitest';
import { ApiError } from '@/lib/api/client';
import type { ProposalEstimate } from '@/lib/editing/sync/api';
import { proposalAllowanceLine, proposalCapReached, proposalErrorSentence, proposalPriceLine, proposalUnaffordableLine } from '../assist-text';

const base: ProposalEstimate = { billingEnabled: true, credits: 3, affordable: true, used: 8, limit: 20 };

describe('assist text', () => {
  it('prices, and says nothing when billing is off', () => {
    expect(proposalPriceLine(base)).toBe('Asking costs about 3 credits.');
    expect(proposalPriceLine({ ...base, billingEnabled: false })).toBeNull();
    expect(proposalPriceLine({ ...base, included: true })).toMatch(/Included/);
  });
  it('shows the allowance and the wait when it is spent', () => {
    expect(proposalAllowanceLine(base)).toBe('12 of 20 suggestions left this hour.');
    const spent = { ...base, used: 20, retryAfterSeconds: 600 };
    expect(proposalCapReached(spent)).toBe(true);
    expect(proposalAllowanceLine(spent)).toMatch(/about 10 minutes/);
  });
  it('explains an unaffordable ask', () => {
    expect(proposalUnaffordableLine({ ...base, affordable: false, balanceCredits: 1 })).toMatch(/needs about 3 credits.*You have 1 credit/);
    expect(proposalUnaffordableLine(base)).toBeNull();
  });
  it('words 429 and 502', () => {
    const e429 = new ApiError('x', 429, [], { code: 'PROPOSAL_RATE_LIMITED', message: 'Slow down.', retryAfterSeconds: 60 });
    expect(proposalErrorSentence(e429)).toBe('Slow down.');
    const e502 = new ApiError('x', 502, [], { code: 'PROPOSAL_FAILED', message: 'No usable answer.', creditsUsed: 0.5 });
    expect(proposalErrorSentence(e502)).toMatch(/0\.5 credits used/);
    expect(proposalErrorSentence(new ApiError('x', 500))).toBeNull();
  });
});

describe('proposal refusals after the 424 move', () => {
  it('words PROPOSAL_FAILED on 424 and 502, plus timeout and busy', async () => {
    const { ApiError } = await import('../../../api/client');
    const { proposalErrorSentence } = await import('../assist-text');
    const mk = (status: number, code: string) => new ApiError('m', status, [], { code, message: 'srv' });
    expect(proposalErrorSentence(mk(424, 'PROPOSAL_FAILED'))).toContain('srv');
    expect(proposalErrorSentence(mk(502, 'PROPOSAL_FAILED'))).toContain('srv');
    expect(proposalErrorSentence(mk(424, 'PROPOSAL_TIMEOUT'))).toBe('srv');
    expect(proposalErrorSentence(mk(410, 'PROPOSAL_EXPIRED'))).toBe('srv');
    expect(proposalErrorSentence(mk(429, 'PROPOSALS_BUSY'))).toBe('srv');
  });
});
