'use client';

import { useCallback } from 'react';
import { useT } from '@/i18n';

/**
 * Names the editor gives things it creates (an adjustment clip, its track, a colour group, a grade node, a compound
 * clip). They are STORED in the timeline document exactly as below, in English, whatever the interface language: a
 * document is shared and must not change with who opened it. Only what is shown is translated, with
 * {@link useDefaultNameText}; a name a person typed is shown as typed.
 */
export const DEFAULT_ADJUSTMENT_NAME = 'Adjustment';
export const DEFAULT_ADJUSTMENTS_TRACK_NAME = 'Adjustments';
export const DEFAULT_SHOT_MATCH_NAME = 'Shot match';
export const DEFAULT_COMPOUND_NAME = 'Compound clip';
export const defaultGroupName = (n: number): string => `Group ${n}`;
export const defaultCorrectionName = (n: number): string => `Correction ${n}`;

/** Shows a stored default name in the person's language; anything else (a name they typed) is returned unchanged. */
export function useDefaultNameText(): (name: string) => string {
  const t = useT('editor.defaultNames');
  return useCallback(
    (name: string): string => {
      if (name === DEFAULT_ADJUSTMENT_NAME) return t('adjustment');
      if (name === DEFAULT_ADJUSTMENTS_TRACK_NAME) return t('adjustments');
      if (name === DEFAULT_SHOT_MATCH_NAME) return t('shotMatch');
      if (name === DEFAULT_COMPOUND_NAME) return t('compoundClip');
      const group = /^Group (\d+)$/.exec(name);
      if (group) return t('group', { n: Number(group[1]) });
      const correction = /^Correction (\d+)$/.exec(name);
      if (correction) return t('correction', { n: Number(correction[1]) });
      return name;
    },
    [t],
  );
}
