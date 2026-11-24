'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { avatarMediaConsentPath, avatarRequirementsPath, avatarsPath, avatarUsagePath } from '../api/avatars';
import type { AvatarMediaConsent, AvatarRequirements, AvatarUsage, ProjectAvatar } from '../types/avatars';

/**
 * What the create flows need, from the server. A 404 means both operator switches (people, objects) are
 * off (the Avatars tab is hidden), so an error is an answer and never retried.
 */
export function useAvatarRequirements(projectId: string | null | undefined) {
  return useSWR<AvatarRequirements>(projectId ? avatarRequirementsPath(projectId) : null, fetcher, {
    shouldRetryOnError: false,
    revalidateOnFocus: false,
  });
}

/** The project's avatars, people and objects; only asked once the requirements answered (`enabled`). */
export function useProjectAvatars(projectId: string | null | undefined, enabled = true) {
  return useSWR<ProjectAvatar[]>(projectId && enabled ? avatarsPath(projectId) : null, fetcher, {
    shouldRetryOnError: false,
  });
}

/** Where one avatar was used. */
export function useAvatarUsage(projectId: string | null | undefined, avatarId: string | null | undefined) {
  return useSWR<AvatarUsage>(projectId && avatarId ? avatarUsagePath(projectId, avatarId) : null, fetcher, {
    shouldRetryOnError: false,
  });
}

/**
 * Whether the project allows sending avatar and object files to AI services from the editors and the assistant.
 * Only asked while avatars answer (`enabled`). After `grantAvatarMediaConsent` / `withdrawAvatarMediaConsent`,
 * call `mutate` with the answer.
 */
export function useAvatarMediaConsent(projectId: string | null | undefined, enabled = true) {
  return useSWR<AvatarMediaConsent>(projectId && enabled ? avatarMediaConsentPath(projectId) : null, fetcher, {
    shouldRetryOnError: false,
    revalidateOnFocus: false,
  });
}
