'use client';

import { useCallback, useEffect, useRef } from 'react';
import { useActivity } from '@/lib/activity/ActivityProvider';
import { isActiveState } from '@/lib/activity/notifier';
import { cancelAvatarBuild } from '@/lib/api/avatar-variants';
import { watchAvatarBuild } from '@/lib/avatars/build-watch';
import { buildFraction, isBuildRunning } from '@/lib/avatars/builds';
import type { AvatarBuild } from '@/lib/types/avatar-builds';

/** The job type of an avatar build in the header tray (`lib/activity/builtins.ts`). */
export const AVATAR_BUILD_JOB_TYPE = 'avatarBuild';

/** The tray id of a build: stable, so the same build is one row however often it is seen. */
export function avatarBuildJobId(jobId: string): string {
  return `avatar-build:${jobId}`;
}

// Builds this page already follows, so two screens that list the same build start one watcher between them.
const watching = new Set<string>();

export interface AvatarBuildTray {
  /** Lists a running build in the header tray and follows it to its end. Asking again for the same build does nothing. */
  track: (build: AvatarBuild) => void;
  /** Stops a build: through its tray row when it has one (so the row reads "cancelled"), else directly. */
  cancel: (build: AvatarBuild) => Promise<void>;
}

/**
 * Shows the project's running avatar builds in the header tray too (docs/background-activity.md), so the person
 * sees them from any page and is told when one ends while they are elsewhere. The tray row follows the build's own
 * route; the Avatars tab keeps its own list.
 */
export function useAvatarBuildTray(projectId: string): AvatarBuildTray {
  const activity = useActivity();
  const latest = useRef(activity);
  // The callbacks below read the newest tray when they are called; they never render from it.
  useEffect(() => { latest.current = activity; }, [activity]);

  const track = useCallback((build: AvatarBuild) => {
    if (!isBuildRunning(build) || watching.has(build.jobId)) return;
    watching.add(build.jobId);
    const id = avatarBuildJobId(build.jobId);
    const jobId = build.jobId;
    void latest.current.trackJob(
      {
        type: AVATAR_BUILD_JOB_TYPE,
        id,
        title: build.name ?? null,
        projectId,
        href: `/projects/${projectId}?tab=avatars`,
        cancel: async () => { await cancelAvatarBuild(projectId, jobId); },
      },
      (signal) => watchAvatarBuild(projectId, jobId, {
        signal,
        initial: build,
        onUpdate: (b) => latest.current.updateTrackedJob(id, { progress: buildFraction(b), ...(b.name ? { title: b.name } : {}) }),
      }),
    ).catch(() => undefined).finally(() => { watching.delete(jobId); });
  }, [projectId]);

  const cancel = useCallback(async (build: AvatarBuild) => {
    const id = avatarBuildJobId(build.jobId);
    const job = latest.current.allJobs.find((j) => j.id === id && isActiveState(j.state));
    if (job) await latest.current.cancel(job);
    else await cancelAvatarBuild(projectId, build.jobId);
  }, [projectId]);

  return { track, cancel };
}
