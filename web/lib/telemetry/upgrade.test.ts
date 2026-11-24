// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const H = vi.hoisted(() => ({ sendTelemetry: vi.fn(() => Promise.resolve()) }));
vi.mock('./client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./client')>();
  return { ...actual, sendTelemetry: (...a: unknown[]) => H.sendTelemetry(...(a as [])) };
});

import { TELEMETRY_CONSENT_STORAGE_KEY } from './consent';
import { UPGRADE_SURFACES, isUpgradeSurface } from './allowlist';
import { emitUpgradeEvent } from './upgrade';

beforeEach(() => {
  window.localStorage.clear();
  H.sendTelemetry.mockClear();
  process.env.NEXT_PUBLIC_TELEMETRY_ENABLED = 'true';
});
afterEach(() => { delete process.env.NEXT_PUBLIC_TELEMETRY_ENABLED; });

describe('upgrade events', () => {
  it('sends name and surface detail only, with consent', () => {
    window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, 'granted');
    expect(emitUpgradeEvent('billing.upgrade_clicked', 'plan_gate')).toBe(true);
    expect(H.sendTelemetry).toHaveBeenCalledWith('/api/v1/telemetry/events', {
      events: [{ name: 'billing.upgrade_clicked', detail: 'plan_gate' }],
    });
  });

  it('sends nothing without consent or when telemetry is off', () => {
    expect(emitUpgradeEvent('billing.upgrade_prompt_shown', 'plan_gate')).toBe(false);
    window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, 'granted');
    process.env.NEXT_PUBLIC_TELEMETRY_ENABLED = 'false';
    expect(emitUpgradeEvent('billing.upgrade_prompt_shown', 'plan_gate')).toBe(false);
    expect(H.sendTelemetry).not.toHaveBeenCalled();
  });

  it('refuses an unknown surface or event', () => {
    window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, 'granted');
    expect(emitUpgradeEvent('billing.upgrade_clicked', 'my project name' as never)).toBe(false);
    expect(emitUpgradeEvent('nope' as never, 'plan_gate')).toBe(false);
    expect(isUpgradeSurface('plan_gate')).toBe(true);
    expect(UPGRADE_SURFACES).toContain('insufficient_credits_alert');
  });
});
