/**
 * Wave 1 phase 2 (T-003) — `getVoiceoverSourceError`, the single source of truth for whether a
 * Voiceover source is configured.
 *
 * The pair that matters is `NarrationPlan` + no chosen step: the editor and the canvas node must
 * BOTH report that source as unconfigured, because the failure it prevents is a step that looks
 * green in the builder and only fails at execution time, when there is nothing to synthesize.
 */

import { describe, it, expect } from 'vitest';
import { PICKUP_PLAN_STEP_REQUIRED_ERROR } from '@/lib/utils/voiceover-validation';
import type { VoiceoverSource } from '@/lib/types/workflow';
import {
  NARRATION_PLAN_STEP_REQUIRED_ERROR,
  getVoiceoverSourceError,
} from './voiceover-validation';

/** A `NarrationPlan` source carrying only the fields that kind owns. */
function narrationPlan(narrationWriterStepOrder: number | null | undefined): VoiceoverSource {
  return {
    kind: 'NarrationPlan',
    lines: undefined,
    scriptwriterStepOrder: undefined,
    narrationWriterStepOrder,
  };
}

describe('getVoiceoverSourceError', () => {
  describe('NarrationPlan', () => {
    it('reports an unchosen step (null) as unconfigured', () => {
      expect(getVoiceoverSourceError(narrationPlan(null))).toBe(NARRATION_PLAN_STEP_REQUIRED_ERROR);
    });

    it('reports an absent step field (undefined) as unconfigured', () => {
      expect(getVoiceoverSourceError(narrationPlan(undefined))).toBe(
        NARRATION_PLAN_STEP_REQUIRED_ERROR,
      );
      // A config persisted before this kind existed carries neither field at all.
      expect(getVoiceoverSourceError({ kind: 'NarrationPlan' })).toBe(
        NARRATION_PLAN_STEP_REQUIRED_ERROR,
      );
    });

    it('reports step order 0 as unconfigured — step orders are 1-based, so 0 is no step', () => {
      // Regression guard for T-004: the node summary used to render a `Step N` fallback label for
      // order 0 and so read as configured, while the editor called the same source unconfigured.
      expect(getVoiceoverSourceError(narrationPlan(0))).toBe(NARRATION_PLAN_STEP_REQUIRED_ERROR);
    });

    it('accepts a valid 1-based step order', () => {
      expect(getVoiceoverSourceError(narrationPlan(1))).toBeNull();
      expect(getVoiceoverSourceError(narrationPlan(7))).toBeNull();
    });

    it.each([-1, -7, 1.5, Number.NaN, Number.POSITIVE_INFINITY])(
      'reports a non 1-based order (%j) as unconfigured',
      (order) => {
        expect(getVoiceoverSourceError(narrationPlan(order))).toBe(
          NARRATION_PLAN_STEP_REQUIRED_ERROR,
        );
      },
    );

    it('names the missing step in the message the editor renders', () => {
      expect(NARRATION_PLAN_STEP_REQUIRED_ERROR).toBe('No NarrationWriter step selected.');
    });
  });

  describe('Inline and ScriptScenes', () => {
    it('returns null for an Inline source, with or without lines', () => {
      expect(getVoiceoverSourceError({ kind: 'Inline', lines: [] })).toBeNull();
      expect(
        getVoiceoverSourceError({ kind: 'Inline', lines: [{ text: 'Hi', startSec: 0 }] }),
      ).toBeNull();
      // No `lines` key at all is still an Inline source; its own rules live elsewhere.
      expect(getVoiceoverSourceError({ kind: 'Inline' })).toBeNull();
    });

    it('returns null for a ScriptScenes source, with or without a chosen step', () => {
      expect(getVoiceoverSourceError({ kind: 'ScriptScenes', scriptwriterStepOrder: 2 })).toBeNull();
      expect(getVoiceoverSourceError({ kind: 'ScriptScenes', scriptwriterStepOrder: null })).toBeNull();
    });

    it('ignores a stray narrationWriterStepOrder on a non-NarrationPlan source', () => {
      // The validator branches on `kind` alone, so leftover state from a mode the user left behind
      // can never mark an Inline source unconfigured.
      expect(
        getVoiceoverSourceError({ kind: 'Inline', lines: [], narrationWriterStepOrder: 0 }),
      ).toBeNull();
      expect(
        getVoiceoverSourceError({ kind: 'ScriptScenes', narrationWriterStepOrder: 0 }),
      ).toBeNull();
    });
  });

  describe('PickupPlan', () => {
    it('reports an unchosen PickupPlanner step as unconfigured and accepts a 1-based one', () => {
      expect(getVoiceoverSourceError({ kind: 'PickupPlan', pickupPlannerStepOrder: null })).toBe(PICKUP_PLAN_STEP_REQUIRED_ERROR);
      expect(getVoiceoverSourceError({ kind: 'PickupPlan' })).toBe(PICKUP_PLAN_STEP_REQUIRED_ERROR);
      expect(getVoiceoverSourceError({ kind: 'PickupPlan', pickupPlannerStepOrder: 0 })).toBe(PICKUP_PLAN_STEP_REQUIRED_ERROR);
      expect(getVoiceoverSourceError({ kind: 'PickupPlan', pickupPlannerStepOrder: 3 })).toBeNull();
    });
  });
});
