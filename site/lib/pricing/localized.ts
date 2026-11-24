// Locale-aware display text for the plan catalogue. The English derivations in `./offers` stay the
// source of the logic (what is deduplicated, what is shared); this module renders the same facts
// through the `plans` message catalogue. Numbers and money go through the active locale's formats,
// never typed here. The English output matches `./offers` word for word.
import type { Plan } from './index';
import {
  benefitLines,
  featureLines,
  cardHighlights,
  planMaxResolution,
  sharedPaidLines,
  planCardLines,
} from './offers';

export type PlansT = (key: string, values?: Record<string, string | number>) => string;
export type PlansFormat = {
  number: (n: number) => string;
  currency: (amount: number, currency: string) => string;
};


/** The catalogue index of a highlight, so its translation is addressed by plan id and position. */
function highlightKey(plan: Plan, english: string): string {
  return `highlights.${plan.id}.${plan.highlights.indexOf(english)}`;
}

export function creditsLabelT(t: PlansT, f: PlansFormat, plan: Plan): string {
  if (plan.kind === 'trial') return t('credits.trial', { n: f.number(plan.creditsOneTime ?? 0) });
  if (plan.kind === 'perSeat') return t('credits.perSeat', { n: f.number(plan.includedCredits) });
  if (plan.kind === 'subscription') return t('credits.monthly', { n: f.number(plan.includedCredits) });
  return t('credits.arrangement');
}

export function planCtaLabelT(t: PlansT, plan: Plan, signupEnabled: boolean): string {
  if (!signupEnabled || plan.cta === 'contact') return t('cta.talk');
  if (plan.kind === 'trial') return t('cta.startFree');
  return t('cta.choose', { name: plan.name });
}

export function assistantAllowanceT(t: PlansT, f: PlansFormat, plan: Plan): string {
  const e = plan.entitlements;
  if (e.assistantUnlimited) return t('table.unlimited');
  if (e.assistantMonthlyCredits !== null) return f.number(e.assistantMonthlyCredits);
  if (e.assistantMonthlyCreditsPerSeat !== null) {
    return t('table.perSeat', { n: f.number(e.assistantMonthlyCreditsPerSeat) });
  }
  return t('table.notIncluded');
}

export function storageLabelT(t: PlansT, plan: Plan): string | undefined {
  const gb = plan.entitlements.storageGb;
  if (gb === null) return undefined;
  return plan.kind === 'perSeat' ? t('table.storageShared', { gb }) : t('table.storage', { gb });
}

/** Benefit lines in the same order and under the same conditions as `benefitLines`. */
function benefitLinesT(t: PlansT, f: PlansFormat, plan: Plan): string[] {
  const e = plan.entitlements;
  const lines: string[] = [];
  const exportLimit = planMaxResolution(plan);
  if (e.maxOutputHeight === 2160) lines.push(t('benefits.exportFull', { res: exportLimit }));
  else lines.push(t(plan.limits.watermark ? 'benefits.exportWatermark' : 'benefits.exportUpTo', { res: exportLimit }));
  if (e.assistantMonthlyCredits !== null) {
    lines.push(t('benefits.assistant', { n: f.number(e.assistantMonthlyCredits) }));
  }
  if (e.assistantMonthlyCreditsPerSeat !== null) {
    lines.push(t('benefits.assistantPerSeat', { n: f.number(e.assistantMonthlyCreditsPerSeat) }));
  }
  if (e.assistantUnlimited) lines.push(t('benefits.assistantUnlimited'));
  if (e.storageGb !== null) {
    lines.push(t(plan.kind === 'perSeat' ? 'benefits.storageShared' : 'benefits.storage', { gb: e.storageGb }));
  }
  if (plan.kind === 'subscription' || plan.kind === 'perSeat') lines.push(t('benefits.cancel'));
  if (plan.topUpAllowed) lines.push(t('benefits.topUp'));
  return lines;
}

/** Feature lines in the same order and under the same conditions as `featureLines`. */
function featureLinesT(t: PlansT, f: PlansFormat, plan: Plan): string[] {
  const e = plan.entitlements;
  const lines: string[] = [];
  if (!plan.limits.watermark) lines.push(t('features.noWatermark'));
  if (e.segmentationAllowed) lines.push(t('features.segmentation'));
  if (e.audioGenAllowed) lines.push(t('features.audioGen'));
  if (e.audioCleanupAllowed) lines.push(t('features.audioCleanup'));
  if (e.byoAllowed) lines.push(t('features.byo'));
  if (e.maxConcurrentExecutions !== null) {
    lines.push(
      e.maxConcurrentExecutions === 1
        ? t('features.oneJob')
        : t('features.jobs', { n: f.number(e.maxConcurrentExecutions) }),
    );
  }
  if (e.storageGb === null && plan.kind === 'contact') lines.push(t('features.storageUnlimited'));
  return lines;
}

/** Shared paid lines, localized: derived from the English comparison, rendered per locale. */
export function sharedPaidLinesT(t: PlansT, f: PlansFormat, plans: readonly Plan[]): string[] {
  const shared = sharedPaidLines(plans);
  const paid = plans.find((plan) => plan.kind === 'subscription' || plan.kind === 'perSeat');
  if (!paid) return [];
  const en = benefitLines(paid);
  const loc = benefitLinesT(t, f, paid);
  return shared.map((line) => loc[en.indexOf(line)]);
}

/** A card's bullets, localized: the English logic picks which lines survive, the catalogue words them. */
export function planCardLinesT(t: PlansT, f: PlansFormat, plan: Plan, sharedEn: readonly string[] = []): string[] {
  const survivors = planCardLines(plan, sharedEn);
  const enBenefits = [...benefitLines(plan), ...featureLines(plan)];
  const locBenefits = [...benefitLinesT(t, f, plan), ...featureLinesT(t, f, plan)];
  const highlights = new Set(cardHighlights(plan));
  return survivors.map((line) => {
    const b = enBenefits.indexOf(line);
    if (b >= 0 && !highlights.has(line)) return locBenefits[b];
    return t(highlightKey(plan, line));
  });
}


/** `Pro, Team and Enterprise`, `Pro and Team`, `Pro`: the join word comes from the catalogue. */
export function joinNamesT(t: PlansT, names: readonly string[]): string {
  if (names.length <= 1) return names[0] ?? '';
  return t('list.join', { head: names.slice(0, -1).join(', '), last: names[names.length - 1] });
}

/** `$29` in English, `29 $` in Italian: whole amounts in the active locale, never a typed symbol. */
export function formatMoneyT(
  f: { number: (n: number, options?: Intl.NumberFormatOptions) => string },
  amount: number,
  currency: string,
  maxFractionDigits = 0,
): string {
  return f.number(amount, { style: 'currency', currency, minimumFractionDigits: 0, maximumFractionDigits: maxFractionDigits });
}
