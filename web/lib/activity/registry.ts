import type { Icon } from '@tabler/icons-react';
import { IconActivity } from '@tabler/icons-react';
import type { ActivityJob } from './types';

/**
 * The extension point for background work. A feature that starts something slow registers a
 * definition for its job type once (module scope, or from `builtins.ts`) and the tray, the
 * in-place status and the finish notifier all pick it up.
 *
 * - `type`: the string carried by `ActivityJob.type`.
 * - `icon`: a Tabler icon component.
 * - `labelKey`: an i18n key under the `activity` namespace, e.g. `types.render`.
 * - `href`: where "Open" goes, WITHOUT the `/app` basePath (use it with `next/link`); `null` = nowhere.
 * - `cancel`: stops the work; only offered when the job says it is cancellable.
 * - `notifyOnFinish`: default true; false mutes the finish toast for successes AND failures.
 */
export interface JobTypeDef {
  type: string;
  icon: Icon;
  labelKey: string;
  href?: (job: ActivityJob) => string | null;
  cancel?: (job: ActivityJob) => Promise<void>;
  notifyOnFinish?: boolean;
}

const defs = new Map<string, JobTypeDef>();

export const GENERIC_JOB_TYPE: JobTypeDef = {
  type: 'generic',
  icon: IconActivity,
  labelKey: 'types.generic',
};

/** Registers (or replaces) the definition of a job type. */
export function registerJobType(def: JobTypeDef): void {
  defs.set(def.type, def);
}

/** The definition for `type`, or the generic one for a type nobody registered. */
export function getJobType(type: string): JobTypeDef {
  return defs.get(type) ?? GENERIC_JOB_TYPE;
}

/** Where "Open" goes for a job: its own `href`, else the registry's, else null. */
export function jobHref(job: ActivityJob): string | null {
  if (job.href !== undefined) return job.href;
  return getJobType(job.type).href?.(job) ?? null;
}
