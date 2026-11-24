import { describe, expect, it } from 'vitest';
import type { BillingPlan } from '@/lib/types/billing';
import { planFeatureLines } from './plan-features';

const base: BillingPlan = {
  key: 'pro', name: 'Pro', kind: 'Subscription', priceMonthlyUsd: 1, minSeats: null,
  includedCredits: 10, priceId: 'pri_x', current: false,
};
const ent = {
  maxOutputHeight: 1080, watermark: false, byoAllowed: false, roomsAllowed: false, videoGenAllowed: false,
  segmentationAllowed: false, audioGenAllowed: false, audioCleanupAllowed: false, maxConcurrentExecutions: 2,
  assistantMonthlyCredits: 0, retentionDays: 30, runnerAllowed: false, priority: 1,
};
const keys = (p: BillingPlan) => planFeatureLines(p).map((l) => l.key);

describe('planFeatureLines', () => {
  it('returns nothing when the API sent neither field', () => {
    expect(planFeatureLines(base)).toEqual([]);
  });
  it('shows storage, and unlimited for null', () => {
    expect(planFeatureLines({ ...base, storageGb: 15 })).toEqual([{ key: 'includes.storage', values: { gb: 15 } }]);
    expect(keys({ ...base, storageGb: null })).toEqual(['includes.storageUnlimited']);
  });
  it('maps quality and watermark', () => {
    expect(planFeatureLines({ ...base, entitlements: ent })[0]).toEqual({ key: 'includes.quality', values: { height: 1080 } });
    expect(keys({ ...base, entitlements: { ...ent, maxOutputHeight: 2160 } })[0]).toBe('includes.quality4k');
    expect(keys({ ...base, entitlements: ent })).toContain('includes.noWatermark');
    expect(keys({ ...base, entitlements: { ...ent, watermark: true } })).toContain('includes.watermark');
  });
  it('treats null limits as unlimited or hides them', () => {
    const e = { ...ent, maxOutputHeight: null, maxConcurrentExecutions: null, assistantMonthlyCredits: null, assistantUnlimited: true };
    expect(keys({ ...base, entitlements: e })).toEqual(['includes.quality4k', 'includes.noWatermark', 'includes.assistantUnlimited']);
  });
  it('Team shows credits per seat, never unlimited; a null count alone shows nothing', () => {
    const team = { ...ent, assistantMonthlyCredits: null, assistantMonthlyCreditsPerSeat: 100, assistantUnlimited: false };
    expect(planFeatureLines({ ...base, entitlements: team }).find((l) => l.key.startsWith('includes.assistant'))).toEqual({ key: 'includes.assistantPerSeat', values: { credits: 100 } });
    expect(keys({ ...base, entitlements: { ...ent, assistantMonthlyCredits: null } })).not.toContain('includes.assistantUnlimited');
  });
  it('lists only the features that are on', () => {
    expect(keys({ ...base, entitlements: ent })).toEqual(['includes.quality', 'includes.noWatermark', 'includes.concurrent']);
    const all = { ...ent, segmentationAllowed: true, audioGenAllowed: true, audioCleanupAllowed: true, byoAllowed: true, assistantMonthlyCredits: 500 };
    expect(keys({ ...base, entitlements: all })).toEqual([
      'includes.quality', 'includes.noWatermark', 'includes.cutouts', 'includes.audioGen', 'includes.audioCleanup', 'includes.ownProvider', 'includes.concurrent', 'includes.assistant',
    ]);
  });
});
