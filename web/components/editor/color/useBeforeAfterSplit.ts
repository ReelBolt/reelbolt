'use client';

import { useState } from 'react';
import { useT } from '@/i18n';

export const settle = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

export interface BeforeAfterSplitInput {
  gradeBypass: boolean;
  onGradeBypass: (bypass: boolean) => void;
  gallery: readonly { id: string; label: string }[];
  referenceId: string | null;
  onReference: (id: string | null) => void;
  /** Captures the current picture as a still and makes it the reference. */
  onCaptureStill: (label: string) => Promise<void>;
}

export interface BeforeAfterSplit {
  /** The reference on the viewer is a "Before" still: the picture is split before / after. */
  active: boolean;
  /** The before picture is being captured. */
  busy: boolean;
  start: () => Promise<void>;
  stop: () => void;
}

/**
 * The before / after split: the picture is captured once with every grade bypassed, and that still becomes
 * the reference the viewer wipes against. Nothing is written to the document. One implementation for the
 * look panel's Compare section and the Colour page's compare bar.
 */
export function useBeforeAfterSplit({ gradeBypass, onGradeBypass, gallery, referenceId, onReference, onCaptureStill }: BeforeAfterSplitInput): BeforeAfterSplit {
  const t = useT('editor-color.look');
  const [busy, setBusy] = useState(false);
  const before = t('before');
  const active = referenceId !== null && gallery.find((s) => s.id === referenceId)?.label.startsWith(before) === true;
  const start = async () => {
    setBusy(true);
    const prior = gradeBypass;
    try {
      onGradeBypass(true);
      await settle(450);
      await onCaptureStill(before);
    } finally {
      onGradeBypass(prior);
      setBusy(false);
    }
  };
  return { active, busy, start, stop: () => onReference(null) };
}
