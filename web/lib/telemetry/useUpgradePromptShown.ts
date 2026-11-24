'use client';

import { useEffect } from 'react';
import type { UpgradeSurface } from './allowlist';
import { emitUpgradeEvent } from './upgrade';

/** Fires `billing.upgrade_prompt_shown` once per mount of the surface (`active` false = not shown). */
export function useUpgradePromptShown(surface: UpgradeSurface, active = true): void {
  useEffect(() => {
    if (active) emitUpgradeEvent('billing.upgrade_prompt_shown', surface);
  }, [surface, active]);
}
