import type { BillingPlan } from '@/lib/types/billing';

/** One line of the "what is included" list: a `plans.card` message key and its values. */
export interface PlanFeatureLine {
  key: string;
  values?: Record<string, string | number>;
}

/**
 * The plain-words list of what a plan includes, derived only from the summary DTO
 * (`storageGb`, `entitlements`). Anything the API did not send is left out, never guessed.
 */
export function planFeatureLines(plan: BillingPlan): PlanFeatureLine[] {
  const lines: PlanFeatureLine[] = [];
  const e = plan.entitlements;

  if (plan.storageGb === null) lines.push({ key: 'includes.storageUnlimited' });
  else if (typeof plan.storageGb === 'number') lines.push({ key: 'includes.storage', values: { gb: plan.storageGb } });

  if (!e) return lines;

  lines.push(
    e.maxOutputHeight === null || e.maxOutputHeight >= 2160
      ? { key: 'includes.quality4k' }
      : { key: 'includes.quality', values: { height: e.maxOutputHeight } },
  );
  lines.push({ key: e.watermark ? 'includes.watermark' : 'includes.noWatermark' });
  if (e.segmentationAllowed) lines.push({ key: 'includes.cutouts' });
  if (e.audioGenAllowed) lines.push({ key: 'includes.audioGen' });
  if (e.audioCleanupAllowed) lines.push({ key: 'includes.audioCleanup' });
  if (e.byoAllowed) lines.push({ key: 'includes.ownProvider' });
  if (e.maxConcurrentExecutions !== null && e.maxConcurrentExecutions > 0) lines.push({ key: 'includes.concurrent', values: { count: e.maxConcurrentExecutions } });
  if (e.assistantUnlimited === true) lines.push({ key: 'includes.assistantUnlimited' });
  else if (typeof e.assistantMonthlyCreditsPerSeat === 'number' && e.assistantMonthlyCreditsPerSeat > 0) lines.push({ key: 'includes.assistantPerSeat', values: { credits: e.assistantMonthlyCreditsPerSeat } });
  else if (typeof e.assistantMonthlyCredits === 'number' && e.assistantMonthlyCredits > 0) lines.push({ key: 'includes.assistant', values: { credits: e.assistantMonthlyCredits } });
  return lines;
}
