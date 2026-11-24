'use client';

import { useEffect, useSyncExternalStore } from 'react';
import { clipsUsingSource } from '@/lib/editing/enhance/plan';
import { enhanceEntry, enhanceVersion, subscribeEnhance, defaultEnhanceJobApi, type EnhanceJobApi, type EnhanceJobState } from './enhance-store';
import { getEnhanceHost } from './enhance-host';

export { defaultEnhanceJobApi };
export type { EnhanceJobApi, EnhanceJobState, EnhanceAnchor } from './enhance-store';

export interface EnhanceJobContext {
  projectId: string;
  /** The source the choices apply to (`file:<guid>` | `step_result:<guid>`). */
  sourceMediaRef: string;
  /** The clips that play the source right now (read live). Absent = nothing is applied to a clip (the media pool). */
  clipIdsUsing?: () => readonly string[];
  /** Name of the clip shown, kept with a run so another clip's panel can say whose job it is. */
  clipName?: string;
  readOnly?: boolean;
}

/**
 * The job for this source. Its state and polling live in a store outside the component, so a run survives the
 * section unmounting and shows again when it mounts for that source. A finished run no clip plays any more is
 * shown as `needs-ok`: nothing is ever applied by itself.
 */
export function useEnhanceJob(ctx: EnhanceJobContext, api: EnhanceJobApi = defaultEnhanceJobApi) {
  const pool = ctx.clipIdsUsing === undefined;
  const entry = enhanceEntry(ctx.projectId, ctx.sourceMediaRef, pool);
  useSyncExternalStore(subscribeEnhance, enhanceVersion, enhanceVersion);
  const using = ctx.clipIdsUsing;
  const readOnly = ctx.readOnly ?? false;
  const clipName = ctx.clipName ?? '';
  useEffect(() => entry.attach({ api, readOnly, clipName, using: using ?? null }), [entry, api, readOnly, clipName, using]);

  let state: EnhanceJobState = entry.state;
  if (state.kind === 'done') {
    const live = using ? using() : getEnhanceHost() ? clipsUsingSource(getEnhanceHost()!.getDoc(), ctx.sourceMediaRef).map((c) => c.id) : [];
    if (!pool && !entry.applied && live.length === 0) state = { kind: 'needs-ok', anchor: state.anchor, job: state.job };
  }
  return { state, estimate: entry.estimate, start: entry.start, grantConsent: entry.grantConsent, declineConsent: entry.declineConsent, cancel: entry.cancel, reset: entry.reset };
}
