'use client';

import { useCallback } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import { notifications } from '@mantine/notifications';
import { useT } from '@/i18n';
import {
  ASSISTANT_PREFERENCES_URL,
  getAssistantPreferences,
  setAssistantPreferences,
} from '../api/assistant';
import type { AssistantApprovalMode, AssistantPreferences } from '../types/assistant';

/**
 * The caller's assistant preferences: whether it asks before it changes things.
 *
 * Until the first answer arrives — or when there never is one — the mode reads `ask`, the safe
 * default: nothing this hook says can switch a check off that the server has not already switched
 * off. `loaded` says whether the answer is the server's own, for chrome that would rather wait than
 * claim something (a switch that must not be flipped before it knows what it is flipping).
 */
export function useAssistantPreferences() {
  const t = useT();
  const { data, error, mutate } = useSWR<AssistantPreferences>(ASSISTANT_PREFERENCES_URL, getAssistantPreferences, {
    // A preference the person only changes here; a refetch when the tab regains focus has nothing new to say.
    revalidateOnFocus: false,
  });

  const mode: AssistantApprovalMode = data?.approvalMode ?? 'ask';

  /**
   * Switches the mode. The switch moves at once and moves back if the server refuses, with one calm
   * line saying so. Answers whether the change stuck, so a caller can leave its own UI as it was
   * when it did not.
   */
  const setMode = useCallback(
    async (next: AssistantApprovalMode): Promise<boolean> => {
      try {
        await mutate(setAssistantPreferences({ approvalMode: next }), {
          optimisticData: { ...data, approvalMode: next },
          rollbackOnError: true,
          populateCache: true,
          revalidate: false,
        });
        return true;
      } catch {
        notifications.show({
          title: t('libLabels.hooks.prefsFailTitle'),
          message: t('libLabels.hooks.prefsFailBody'),
          color: 'yellow',
        });
        return false;
      }
    },
    [mutate, data, t],
  );

  const autoApproveCostLimitCredits = data?.autoApproveCostLimitCredits;

  return { mode, autoApproveCostLimitCredits, loaded: data !== undefined, error: error as Error | undefined, setMode };
}

/**
 * For code that learns the mode changed without asking for it — answering a request with "Always
 * allow" switches the server to auto mode as part of the answer. Writes it into the cache so the
 * switch and the tagline agree at once, then confirms with the server.
 */
export function useAdoptAssistantMode() {
  const { mutate } = useSWRConfig();
  return useCallback(
    (mode: AssistantApprovalMode) => {
      void mutate(ASSISTANT_PREFERENCES_URL, { approvalMode: mode } satisfies AssistantPreferences, { revalidate: true });
    },
    [mutate],
  );
}
