'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import useSWR from 'swr';
import { fetcher } from '../api/client';
import { avatarBuildsPath } from '../api/avatar-variants';
import { anyBuildRunning, buildPollDelayMs, buildsOf, buildsSignature } from '../avatars/builds';
import type { AvatarBuild, AvatarBuildListResponse } from '../types/avatar-builds';

export interface AvatarBuildsState {
  /** The project's builds as the server lists them; empty while loading, on an error and on an older server. */
  builds: AvatarBuild[];
  /** True when the list could not be read (an older server answers 404): nothing about builds is shown. */
  off: boolean;
  /** Reads the list again, e.g. right after a build was started or cancelled. */
  refresh: () => Promise<unknown>;
}

/**
 * The avatar builds of a project (variants made by the picture model, objects pulled out of a video). While any
 * is running the list is read again with growing pauses (2 s up to 15 s, starting over whenever something moved);
 * with nothing running it is not polled at all. A failed read is an answer, never retried: an older server has
 * no builds.
 */
export function useAvatarBuilds(projectId: string | null | undefined, enabled = true): AvatarBuildsState {
  const { data, error, mutate } = useSWR<AvatarBuildListResponse>(
    projectId && enabled ? avatarBuildsPath(projectId) : null, fetcher, { shouldRetryOnError: false },
  );
  const builds = useMemo(() => buildsOf(data), [data]);
  const running = anyBuildRunning(builds);
  const signature = buildsSignature(builds);
  // SWR keeps the same data object when a poll brings nothing new, so each finished poll bumps `tick` to arm the next.
  const [tick, setTick] = useState(0);
  const attempt = useRef(0);
  const lastSignature = useRef(signature);

  useEffect(() => {
    if (lastSignature.current !== signature) {
      lastSignature.current = signature;
      attempt.current = 0;
    }
    if (!running) {
      attempt.current = 0;
      return undefined;
    }
    let live = true;
    const timer = setTimeout(() => {
      attempt.current += 1;
      void Promise.resolve(mutate()).catch(() => undefined).then(() => { if (live) setTick((n) => n + 1); });
    }, buildPollDelayMs(attempt.current));
    return () => { live = false; clearTimeout(timer); };
  }, [running, signature, tick, mutate]);

  return useMemo(() => ({ builds, off: Boolean(error), refresh: () => mutate() }), [builds, error, mutate]);
}
