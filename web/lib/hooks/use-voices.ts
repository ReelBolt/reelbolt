'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { voiceLibraryUrl } from '../api/voices';
import type { ProjectVoice, VoiceLibraryPage } from '../types/voices';

/** The project's voice library (see docs/voiceover.md "Voices and consent"). */
export function useProjectVoices(projectId: string | null | undefined) {
  return useSWR<ProjectVoice[]>(projectId ? `/api/v1/projects/${projectId}/voices` : null, fetcher);
}

/**
 * One page of the speech provider's voice library. Only fetched while the browser is open, and never
 * retried on error: a 404 `voice_library_not_supported` is an answer, not a transient failure.
 */
export function useVoiceLibrary(
  projectId: string | null | undefined,
  enabled: boolean,
  search: string,
  language: string | null,
  page: number,
) {
  return useSWR<VoiceLibraryPage>(
    projectId && enabled ? voiceLibraryUrl(projectId, search, language, page) : null,
    fetcher,
    { shouldRetryOnError: false, revalidateOnFocus: false, keepPreviousData: true },
  );
}
