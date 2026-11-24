'use client';

import { useSyncExternalStore, type ReactNode } from 'react';
import { founderStripVisible } from '@/lib/launch-offer';

const noopSubscribe = () => () => {};

/**
 * The pricing page is static, so the founder strip checks the clock after mount. The server (and
 * hydration) snapshot is the build-time state, so there is no mismatch; the client snapshot then
 * hides the strip once the offer has ended, without a rebuild.
 */
export function FounderStrip({ openAtBuild, children }: { openAtBuild: boolean; children: ReactNode }) {
  const visible = useSyncExternalStore(
    noopSubscribe,
    () => founderStripVisible(openAtBuild, new Date()),
    () => openAtBuild,
  );
  return visible ? <>{children}</> : null;
}
