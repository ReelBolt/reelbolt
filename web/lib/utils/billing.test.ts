import { describe, expect, it } from 'vitest';
import {
  canRetirePrice,
  creditEstimateHeadline,
  creditRange,
  creditsNeededLine,
  displayUnitNoun,
  effectiveStatus,
  estimateBasisLabel,
  estimateBasisSentence,
  fixedPriceLine,
  formatCreditAmount,
  stepChargeLabel,
  formatCreditsFromMillis,
  formatPercent,
  formatPriceCost,
  formatPriceCredits,
  formatUsd,
  formatUsdPerUnit,
  fromDisplayUnit,
  isTokenUnit,
  lowBalance,
  mayRunOutPartway,
  parseDateTimeLocalValue,
  planAllowanceCredits,
  spreadReasonLines,
  stepEstimateLine,
  toDateTimeLocalValue,
  toDisplayUnit,
  formatPreviewMoney,
  previewAmounts,
} from './billing';
import type {
  BillingPlan,
  BillingSubscriptionSummary,
  BillingSummary,
  WorkflowStepCostEstimate,
} from '../types/billing';

/** The fixtures each new case starts from; only what the case is about is overridden. */
const PLAN_PRO: BillingPlan = {
  key: 'pro', name: 'Pro', kind: 'Subscription', priceMonthlyUsd: 79,
  minSeats: null, includedCredits: 3500, priceId: 'pri_pro', current: true,
};

const PLAN_TEAM: BillingPlan = {
  key: 'team', name: 'Team', kind: 'PerSeat', priceMonthlyUsd: 49,
  minSeats: 3, includedCredits: 1500, priceId: 'pri_team', current: true,
};

const SUB: BillingSubscriptionSummary = {
  planKey: 'pro', planName: 'Pro', status: 'Active', seats: 1,
  periodStart: '2026-09-10T00:00:00Z', periodEnd: '2026-10-10T00:00:00Z',
  cancelAtPeriodEnd: false, hasBillingCustomer: true,
};

const NOW = new Date('2026-06-01T12:00:00.000Z');

describe('effectiveStatus', () => {
  it('is Open between its start and its end', () => {
    expect(effectiveStatus({ effectiveFrom: '2026-01-01T00:00:00.000Z', effectiveTo: null }, NOW)).toBe('Open');
  });

  it('is Scheduled before its start', () => {
    expect(effectiveStatus({ effectiveFrom: '2026-07-01T00:00:00.000Z', effectiveTo: null }, NOW)).toBe('Scheduled');
  });

  it('is Ended once a superseding row has closed it', () => {
    expect(effectiveStatus({ effectiveFrom: '2025-01-01T00:00:00.000Z', effectiveTo: '2026-05-01T00:00:00.000Z' }, NOW)).toBe('Ended');
  });

  it('is Ended exactly at the end instant, because the interval is half-open', () => {
    expect(effectiveStatus({ effectiveFrom: '2025-01-01T00:00:00.000Z', effectiveTo: NOW.toISOString() }, NOW)).toBe('Ended');
  });
});

describe('canRetirePrice', () => {
  it('allows retiring an open price', () => {
    expect(canRetirePrice({ effectiveFrom: '2026-01-01T00:00:00.000Z', effectiveTo: null }, NOW)).toBe(true);
  });

  it('refuses a price that has not started, which the API rejects with 400', () => {
    expect(canRetirePrice({ effectiveFrom: '2026-07-01T00:00:00.000Z', effectiveTo: null }, NOW)).toBe(false);
  });

  it('refuses a price that already ended, so history is not re-ended', () => {
    expect(canRetirePrice({ effectiveFrom: '2025-01-01T00:00:00.000Z', effectiveTo: '2026-05-01T00:00:00.000Z' }, NOW)).toBe(false);
  });
});

describe('formatters', () => {
  it('formats dollars to two decimals', () => {
    expect(formatUsd(1234.5)).toBe('$1,234.50');
    expect(formatUsd(0)).toBe('$0.00');
  });

  it('does not round a per-unit price below a cent to zero', () => {
    expect(formatUsdPerUnit(0.000002)).toBe('$0.000002');
    expect(formatUsdPerUnit(3)).toBe('$3');
  });

  it('converts millicredits to credits', () => {
    expect(formatCreditsFromMillis(1_500_000)).toBe('1,500');
    expect(formatCreditsFromMillis(250)).toBe('0.25');
  });

  it('formats a percent from a number', () => {
    expect(formatPercent(86.2069)).toBe('86.2%');
    expect(formatPercent(0)).toBe('0.0%');
  });
});

describe('per-unit prices', () => {
  it('treats the four token units as tokens and nothing else', () => {
    expect(isTokenUnit('LlmInputTokens')).toBe(true);
    expect(isTokenUnit('EmbeddingTokens')).toBe(true);
    expect(isTokenUnit('VideoGenSeconds')).toBe(false);
    expect(isTokenUnit('ImageEditImages')).toBe(false);
  });

  it('shows token prices per million tokens and stores them per token, without float noise', () => {
    expect(toDisplayUnit(0.0000025, 'LlmInputTokens')).toBe(2.5);
    expect(fromDisplayUnit(2.5, 'LlmInputTokens')).toBe(0.0000025);
    expect(fromDisplayUnit(0.05, 'VideoGenSeconds')).toBe(0.05);
    expect(displayUnitNoun('LlmOutputTokens')).toBe('1M tokens');
    expect(displayUnitNoun('ImageGenImages')).toBe('image');
  });

  it('formats a cost with its unit', () => {
    expect(formatPriceCost(0.0000025, 'LlmInputTokens')).toBe('$2.5 / 1M tokens');
    expect(formatPriceCost(0.05, 'VideoGenSeconds')).toBe('$0.05 / second');
  });

  it('formats effective credits in credits, keeping a small charge readable rather than 0', () => {
    // 0.0005 millicredits per token = 0.5 credits per million tokens.
    expect(formatPriceCredits(0.0005, 'LlmInputTokens')).toBe('0.5 credits / 1M tokens');
    // 10,000 millicredits per second = 10 credits per second.
    expect(formatPriceCredits(10_000, 'VideoGenSeconds')).toBe('10 credits / second');
    expect(formatPriceCredits(1.23456, 'ImageGenImages')).toBe('0.001235 credits / image');
  });
});

describe('datetime-local helpers', () => {
  it('round-trips a date through the local-time representation', () => {
    const date = new Date('2026-03-04T05:06:00.000Z');

    expect(parseDateTimeLocalValue(toDateTimeLocalValue(date))).toBe(date.toISOString());
  });

  it('uses the local wall clock, so the value never means a different instant', () => {
    const date = new Date(2026, 2, 4, 5, 6);

    expect(toDateTimeLocalValue(date)).toBe('2026-03-04T05:06');
  });

  it('returns null for an empty or unparseable value rather than posting an invalid date', () => {
    expect(parseDateTimeLocalValue('')).toBeNull();
    expect(parseDateTimeLocalValue('   ')).toBeNull();
    expect(parseDateTimeLocalValue('not a date')).toBeNull();
  });
});

// ------------------------------------------------------------------ estimate and low balance (B10c-UI)

describe('creditRange', () => {
  it('shows an approximate range when the ends differ', () => {
    expect(creditRange(12.5, 20)).toBe('≈ 12.5–20 credits');
  });

  it('drops the range when both ends agree, rather than repeating the number', () => {
    expect(creditRange(20, 20)).toBe('≈ 20 credits');
    expect(creditRange(20, 20.001)).toBe('≈ 20 credits');
  });

  it('never renders NaN, whatever the API sends', () => {
    expect(creditRange(Number.NaN, 5)).toBe('≈ 0–5 credits');
  });
});

describe('estimateBasisLabel', () => {
  it('says a cold-start default is a default, never a measurement', () => {
    expect(estimateBasisLabel('Default')).toMatch(/typical figure/);
    expect(estimateBasisLabel('Default')).toMatch(/not your own history/);
  });

  it('distinguishes the two history bases the backend reports', () => {
    expect(estimateBasisLabel('History')).toMatch(/this step/);
    expect(estimateBasisLabel('StepTypeHistory')).toMatch(/this kind of step/);
  });

  it('passes an unknown basis through rather than hiding it', () => {
    // The enum is append-only: a newer backend's basis must still render as something.
    expect(estimateBasisLabel('SomethingNew' as never)).toBe('SomethingNew');
  });

  it('names the agent-kind and own-provider bases in plain words', () => {
    expect(estimateBasisLabel('AgentTypeHistory')).toBe('from your own past runs of this kind of agent');
    expect(estimateBasisLabel('OwnProvider')).toBe('on your own provider key, not charged');
  });

  it('names the audio-generation basis (T-108) instead of passing the raw member through', () => {
    expect(estimateBasisLabel('AudioGeneration')).toBe('priced from the takes and length this step asks for');
  });
});

describe('estimateBasisSentence', () => {
  it('says the figures are measured when the organization has history', () => {
    expect(estimateBasisSentence({ hasHistory: true })).toMatch(/actually cost/);
  });

  it('says they are typical figures when it has none', () => {
    expect(estimateBasisSentence({ hasHistory: false })).toMatch(/no finished runs/);
  });

  it('counts the past runs of this workflow when the range comes from them', () => {
    expect(estimateBasisSentence({ hasHistory: true, rangeBasis: 'PastRuns', pastRunCount: 6 }))
      .toBe('Based on 6 past runs of this workflow.');
    expect(estimateBasisSentence({ hasHistory: true, rangeBasis: 'PastRuns', pastRunCount: 1 }))
      .toBe('Based on 1 past run of this workflow.');
  });

  it('says the steps own history or a first run, from rangeBasis', () => {
    expect(estimateBasisSentence({ hasHistory: true, rangeBasis: 'StepHistory' }))
      .toBe('Based on what these steps have cost in this workspace.');
    expect(estimateBasisSentence({ hasHistory: false, rangeBasis: 'FirstRun' }))
      .toBe('First run, so this is a rough estimate from typical figures for these steps.');
  });

  it('falls back to the hasHistory wording for an older backend or an unknown basis', () => {
    expect(estimateBasisSentence({ hasHistory: true, rangeBasis: undefined })).toMatch(/actually cost/);
    expect(estimateBasisSentence({ hasHistory: false, rangeBasis: 'Later' as never })).toMatch(/no finished runs/);
    expect(estimateBasisSentence({ hasHistory: true, rangeBasis: 'PastRuns' })).toMatch(/actually cost/);
  });
});

describe('creditEstimateHeadline', () => {
  it('leads with the typical cost and gives the usual range after it', () => {
    expect(creditEstimateHeadline(40, 30, 52)).toBe('≈ 40 credits (usually 30–52)');
  });

  it('drops the range when its ends agree to the cent', () => {
    expect(creditEstimateHeadline(40, 40, 40.001)).toBe('≈ 40 credits');
  });

  it('never renders NaN, whatever the API sends', () => {
    expect(creditEstimateHeadline(Number.NaN, Number.NaN, 5)).toBe('≈ 0 credits (usually 0–5)');
    expect(creditEstimateHeadline(Number.POSITIVE_INFINITY, 0, 0)).toBe('≈ 0 credits');
  });
});

describe('fixedPriceLine', () => {
  it('states the generated-video ceiling as "up to"', () => {
    expect(fixedPriceLine(320, ['video_generation_max_clips']))
      .toBe('Plus up to 320 credits for generated video, priced at the most clips it can order.');
  });

  it('says it can be ordered again when the video step sits inside a review loop', () => {
    expect(fixedPriceLine(320, ['video_generation_max_clips', 'video_generation_in_review_loop']))
      .toBe('Plus up to 320 credits for generated video, priced at the most clips it can order. It can be ordered again if the review asks for another pass.');
  });

  it('names the steps a review may re-make when that worst case is part of the ceiling', () => {
    expect(fixedPriceLine(450, ['review_loop_repeats', 'review_step_reruns']))
      .toBe('Plus up to 450 credits if the review re-makes earlier steps (a narration line, a generated shot, the script) and for any generated video, priced at the most it can order.');
  });

  it('is null when there is no fixed price, or the backend is older', () => {
    expect(fixedPriceLine(0, [])).toBeNull();
    expect(fixedPriceLine(-5, [])).toBeNull();
    expect(fixedPriceLine(undefined, undefined)).toBeNull();
    expect(fixedPriceLine(Number.NaN, [])).toBeNull();
  });
});

describe('spreadReasonLines', () => {
  it('turns each known code into a plain sentence, in order', () => {
    expect(spreadReasonLines(['review_loop_repeats', 'footage_length', 'foreach_items_unknown', 'own_provider_free']))
      .toEqual([
        'The review step can send the edit back for another pass.',
        'Analysing and cutting footage cost more for longer clips.',
        'A repeating step runs once per item, and the number of items is only known during the run.',
        'Steps on your own provider key are not charged.',
      ]);
  });

  it('drops unknown codes and leaves the video codes to fixedPriceLine', () => {
    expect(spreadReasonLines(['something_new', 'video_generation_max_clips', 'video_generation_in_review_loop', 'footage_length']))
      .toEqual(['Analysing and cutting footage cost more for longer clips.']);
    expect(spreadReasonLines(undefined)).toEqual([]);
    expect(spreadReasonLines(['toString', 'constructor'])).toEqual([]);
  });
});

describe('stepEstimateLine', () => {
  const step = (over: Partial<WorkflowStepCostEstimate> = {}): WorkflowStepCostEstimate => ({
    stepOrder: 1,
    stepType: 'Agent',
    agentDefinitionId: null,
    label: 'Writes the script',
    lowCredits: 2,
    highCredits: 4,
    requiredCredits: 4,
    basis: 'History',
    ...over,
  });

  it('names the step, its range and where the range came from', () => {
    expect(stepEstimateLine(step())).toBe('Writes the script: ≈ 2–4 credits (from your own past runs of this step)');
  });

  it('falls back to the step type when the step has no label', () => {
    expect(stepEstimateLine(step({ label: null }))).toMatch(/^Agent:/);
    expect(stepEstimateLine(step({ label: '   ' }))).toMatch(/^Agent:/);
  });

  it('reports a step nothing bills as not charged rather than as a zero measurement', () => {
    expect(stepEstimateLine(step({ basis: 'NotCharged', lowCredits: 0, highCredits: 0 })))
      .toBe('Writes the script: not charged');
  });

  it('states a fixed-price step as a ceiling', () => {
    expect(stepEstimateLine(step({ label: 'Generates the clips', basis: 'Default', fixedMaxCredits: 320 })))
      .toBe('Generates the clips: up to 320 credits');
  });

  it('says a step inside a review loop may run again', () => {
    expect(stepEstimateLine(step({ expectedRuns: 1.5 })))
      .toBe('Writes the script: ≈ 2–4 credits, may run again after review (from your own past runs of this step)');
    expect(stepEstimateLine(step({ expectedRuns: 1.05 })))
      .toBe('Writes the script: ≈ 2–4 credits (from your own past runs of this step)');
  });

  it('labels an own-provider step as not charged by us', () => {
    expect(stepEstimateLine(step({ basis: 'OwnProvider', lowCredits: 0, highCredits: 0, fixedMaxCredits: 0 })))
      .toBe('Writes the script: ≈ 0 credits (on your own provider key, not charged)');
  });
});

describe('planAllowanceCredits', () => {
  it('reads the current plan out of the sellable list', () => {
    expect(planAllowanceCredits({ plans: [PLAN_PRO], subscription: SUB })).toBe(3500);
  });

  it('multiplies a per-seat plan by its seats, the way the monthly grant itself does', () => {
    expect(planAllowanceCredits({
      plans: [PLAN_TEAM],
      subscription: { ...SUB, planKey: 'team', seats: 4 },
    })).toBe(6000);
  });

  it('is null when the summary carries no current sellable plan', () => {
    // The endpoint lists only Subscription/PerSeat rows, so a Trial or Enterprise workspace has no
    // allowance on the wire — and no number is a "zero allowance", it is an unknown one.
    expect(planAllowanceCredits({ plans: [], subscription: SUB })).toBeNull();
    expect(planAllowanceCredits({ plans: [{ ...PLAN_PRO, current: false }], subscription: SUB })).toBeNull();
  });
});

describe('lowBalance', () => {
  const summary = (available: number, allowance = 3500): BillingSummary => ({
    billingProvider: 'paddle',
    paddle: null,
    canManageBilling: true,
    subscription: { ...SUB },
    credits: { balance: available, reserved: 0, available },
    topUp: { available: true, packs: [500] },
    plans: [{ ...PLAN_PRO, includedCredits: allowance }],
  });

  it('is low strictly below the 15% mark', () => {
    // 15% of the Pro plan's 3,500 is 525: at exactly 525 there is no warning, a hair below there is.
    expect(lowBalance(summary(525))?.low).toBe(false);
    expect(lowBalance(summary(526))?.low).toBe(false);
    expect(lowBalance(summary(524.99))?.low).toBe(true);
    expect(lowBalance(summary(524))?.low).toBe(true);
  });

  it('warns about an exhausted or negative balance, not just a small one', () => {
    expect(lowBalance(summary(0))?.low).toBe(true);
    expect(lowBalance(summary(-40))?.low).toBe(true);
  });

  it('compares available credits, the figure the run preflight refuses on', () => {
    // Held credits are not spendable, so a workspace with a full balance but everything reserved is
    // genuinely unable to start another run.
    const held: BillingSummary = { ...summary(525), credits: { balance: 3500, reserved: 3200, available: 300 } };
    expect(lowBalance(held)?.low).toBe(true);
    expect(lowBalance(held)?.available).toBe(300);
  });

  it('says nothing at all when there is no summary or no allowance to compare against', () => {
    expect(lowBalance(undefined)).toBeNull();
    expect(lowBalance({ ...summary(1), plans: [] })).toBeNull();
  });
});

describe('mayRunOutPartway', () => {
  // Starts with 60 held; typically costs 100 + up to 50 for fixed-price video.
  const base = {
    billingEnabled: true,
    affordable: true,
    balanceCredits: 120,
    typicalCredits: 100,
    fixedMaxCredits: 50,
  };

  it('is true when the balance starts the run but is below typical plus the fixed-price part', () => {
    expect(mayRunOutPartway(base)).toBe(true);
    expect(mayRunOutPartway({ ...base, balanceCredits: 149.9 })).toBe(true);
  });

  it('is false at or above typical plus the fixed-price part', () => {
    expect(mayRunOutPartway({ ...base, balanceCredits: 150 })).toBe(false);
    expect(mayRunOutPartway({ ...base, balanceCredits: 400 })).toBe(false);
  });

  it('treats a missing fixed-price part as zero', () => {
    expect(mayRunOutPartway({ ...base, fixedMaxCredits: undefined, balanceCredits: 99 })).toBe(true);
    expect(mayRunOutPartway({ ...base, fixedMaxCredits: undefined, balanceCredits: 100 })).toBe(false);
  });

  it('is false when the run cannot start, billing is off, the balance is unknown or there is no typical figure', () => {
    expect(mayRunOutPartway({ ...base, affordable: false })).toBe(false);
    expect(mayRunOutPartway({ ...base, billingEnabled: false })).toBe(false);
    expect(mayRunOutPartway({ ...base, balanceCredits: null })).toBe(false);
    expect(mayRunOutPartway({ ...base, typicalCredits: undefined })).toBe(false);
  });
});

describe('formatCreditAmount', () => {
  it('shows up to two decimals by default', () => {
    expect(formatCreditAmount(12.4)).toBe('12.4');
    expect(formatCreditAmount(1000)).toBe('1,000');
  });

  it('rounds a "needs" figure up to whole credits, never down', () => {
    expect(formatCreditAmount(12.4, { roundUp: true })).toBe('13');
    expect(formatCreditAmount(12.01, { roundUp: true })).toBe('13');
    expect(formatCreditAmount(15, { roundUp: true })).toBe('15');
    // Float noise from millicredits does not push a whole figure to the next credit.
    expect(formatCreditAmount(0.1 + 0.2 + 11.7, { roundUp: true })).toBe('12');
    expect(formatCreditAmount(1234.2, { roundUp: true })).toBe('1,235');
  });
});

// QA (T-005): the "needs" figure a consent surface shows is never below what the run holds.
describe('a "needs" figure never reads lower than the amount', () => {
  it('rounds every millicredit amount up to 200 credits up, by less than one credit', () => {
    for (let millis = 1; millis <= 200_000; millis += 1) {
      const shown = Number(formatCreditAmount(millis / 1000, { roundUp: true }).replace(/,/g, ''));
      expect(Number.isInteger(shown)).toBe(true);
      if (shown * 1000 < millis || shown * 1000 - millis >= 1000) {
        throw new Error(`${millis} millicredits shown as ${shown}`);
      }
    }
  });

  it('keeps an exact figure exact through float noise and at precision edges', () => {
    expect(formatCreditAmount(9.91, { roundUp: true })).toBe('10');
    expect(formatCreditAmount(4.31, { roundUp: true })).toBe('5');
    expect(formatCreditAmount(0.001, { roundUp: true })).toBe('1');
    expect(formatCreditAmount(0.7 + 0.2 + 0.1, { roundUp: true })).toBe('1');
    expect(formatCreditAmount(10.001, { roundUp: true })).toBe('11');
    expect(formatCreditAmount(999_999.999, { roundUp: true })).toBe('1,000,000');
  });

  it('says nothing when nothing is held, and never prints a non-number', () => {
    expect(creditsNeededLine(0)).toBeNull();
    expect(creditsNeededLine(-3)).toBeNull();
    expect(creditsNeededLine(Number.NaN)).toBeNull();
    expect(creditsNeededLine(Number.POSITIVE_INFINITY)).toBeNull();
    expect(creditsNeededLine(12.4)).toBe('Needs 13 credits available to start; it can cost more.');
    // QA-EF-3: exactly one credit is singular; anything that rounds up past it stays plural.
    expect(creditsNeededLine(1)).toBe('Needs 1 credit available to start; it can cost more.');
    expect(creditsNeededLine(0.3)).toBe('Needs 1 credit available to start; it can cost more.');
    expect(creditsNeededLine(1.01)).toBe('Needs 2 credits available to start; it can cost more.');
  });
});

describe('stepChargeLabel', () => {
  it('reads as credits, singular at exactly one', () => {
    expect(stepChargeLabel(12.5)).toBe('12.5 credits');
    expect(stepChargeLabel(1)).toBe('1 credit');
    expect(stepChargeLabel(0.004)).toBe('Less than 0.01 credits');
  });

  it('says "No charge" for zero and nothing at all when no charge was recorded', () => {
    expect(stepChargeLabel(0)).toBe('No charge');
    expect(stepChargeLabel(null)).toBeNull();
    expect(stepChargeLabel(undefined)).toBeNull();
    expect(stepChargeLabel(Number.NaN)).toBeNull();
  });
});

describe('formatPreviewMoney (DEC-9)', () => {
  it('formats raw minor units with Intl, as a pure unit shift', () => {
    expect(formatPreviewMoney('$12.34', 1234, 'USD', 2, 'en-US')).toBe('$12.34');
    expect(formatPreviewMoney('x', 5, 'EUR', 2, 'en-US')).toBe('€0.05');
  });

  it('handles a zero-decimal currency', () => {
    expect(formatPreviewMoney('¥1,500', 1500, 'JPY', 0, 'en-US')).toBe('¥1,500');
  });

  it('falls back to the server string when raw fields are absent or the currency is unknown', () => {
    expect(formatPreviewMoney('$9.00', null, 'USD', 2)).toBe('$9.00');
    expect(formatPreviewMoney('$9.00', undefined, 'USD', 2)).toBe('$9.00');
    expect(formatPreviewMoney('9.00 ??', 900, 'XX', 2)).toBe('9.00 ??');
    expect(formatPreviewMoney('9.00 ??', 900, 'ZZZ', 2, 'en-US')).toContain('9.00');
    expect(formatPreviewMoney('server', 900, 'USD', null)).toBe('server');
    expect(formatPreviewMoney('server', 900, null, 2)).toBe('server');
  });

  it('previewAmounts keeps the Paddle strings verbatim when no raw fields are sent', () => {
    expect(previewAmounts({ currencyCode: 'EUR', subtotal: '10,00 €', total: '8,00 €' })).toEqual({
      subtotal: '10,00 €',
      total: '8,00 €',
    });
    expect(
      previewAmounts({ currencyCode: 'USD', subtotal: 's', total: 't', subtotalMinor: 1000, totalMinor: 800, minorUnitExponent: 2 }),
    ).toEqual({ subtotal: '$10.00', total: '$8.00' });
  });
});
