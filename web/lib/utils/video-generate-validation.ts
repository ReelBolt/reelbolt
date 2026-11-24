import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';
import type { ExtractInputRef, VideoGenerateStepConfig } from '@/lib/types/workflow';

/**
 * Client-side mirror of the backend's static VideoGenerate rules
 * (`VideoGenerateStepConfig.Validate()` + the `VideoGenerateStepExecutor` prompt checks).
 * Model-capability checks (resolution/ratio/duration range per model) stay server-side.
 */

export const MAX_SPEND_ERROR = enT('libLabels.videoGenerate.maxSpend');
export const INLINE_PROMPT_REQUIRED_ERROR = enT('libLabels.videoGenerate.promptRequired');
export const INLINE_PROMPT_MAX_LENGTH = 7000;
export const INLINE_PROMPT_TOO_LONG_ERROR = enT('libLabels.videoGenerate.promptTooLong', { max: String(INLINE_PROMPT_MAX_LENGTH) });
export const INLINE_PROMPT_CONTROL_CHARS_ERROR = enT('libLabels.videoGenerate.promptControl');
export const TAKES_RANGE_ERROR = enT('libLabels.videoGenerate.takesRange');
export const MAX_CLIPS_RANGE_ERROR = enT('libLabels.videoGenerate.maxClipsRange');
export const TAKES_EXCEED_MAX_CLIPS_ERROR = enT('libLabels.videoGenerate.takesExceed');
export const DURATION_ERROR = enT('libLabels.videoGenerate.duration');
/**
 * A `planSource: 'StepRef'` step with no `plan` reference. Mirrors the C# `Validate()` rule
 * ("Plan must reference the planner step's GeneratedShotPlanOutput when PlanSource is StepRef") and
 * is checked LAST, so an Inline config reports byte-identically what it always did.
 */
export const PLAN_SOURCE_STEP_REF_REQUIRED_ERROR = enT('libLabels.videoGenerate.planRequired');

export const MIN_POLL_INTERVAL_SECONDS = 1;
export const MAX_POLL_INTERVAL_SECONDS = 300;
export const MIN_TIMEOUT_SECONDS = 60;
export const MAX_TIMEOUT_SECONDS = 7200;
export const POLL_INTERVAL_RANGE_ERROR = enT('libLabels.videoGenerate.pollRange', { min: String(MIN_POLL_INTERVAL_SECONDS), max: String(MAX_POLL_INTERVAL_SECONDS) });
export const TIMEOUT_RANGE_ERROR = enT('libLabels.videoGenerate.timeoutRange', { min: String(MIN_TIMEOUT_SECONDS), max: String(MAX_TIMEOUT_SECONDS) });
export const TIMEOUT_BELOW_POLL_INTERVAL_ERROR = enT('libLabels.videoGenerate.timeoutBelowPoll');

const MIN_CLIPS = 1;
const MAX_CLIPS = 64;

/** True when maxSpendCredits is a finite number strictly greater than 0. */
export function isValidMaxSpendCredits(value: unknown): boolean {
  return typeof value === 'number' && Number.isFinite(value) && value > 0;
}

function isIntBetween(value: unknown, min: number, max: number): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= min && value <= max;
}

function isIntInRange(value: unknown): boolean {
  return isIntBetween(value, MIN_CLIPS, MAX_CLIPS);
}

/** True when pollIntervalSeconds is an integer within the backend's bounds. */
export function isValidPollIntervalSeconds(value: unknown): boolean {
  return isIntBetween(value, MIN_POLL_INTERVAL_SECONDS, MAX_POLL_INTERVAL_SECONDS);
}

/** True when timeoutSeconds is an integer within the backend's bounds. */
export function isValidTimeoutSeconds(value: unknown): boolean {
  return isIntBetween(value, MIN_TIMEOUT_SECONDS, MAX_TIMEOUT_SECONDS);
}

/**
 * Inline errors for the editor's Poll Interval / Timeout inputs: the range message on its own
 * field, and the cross-field message (only when both are individually valid) on Timeout.
 */
export function getVideoGeneratePollingErrors(
  config: Pick<VideoGenerateStepConfig, 'pollIntervalSeconds' | 'timeoutSeconds'>,
  t: T = enT,
): { pollIntervalSeconds: string | null; timeoutSeconds: string | null } {
  const pollValid = isValidPollIntervalSeconds(config.pollIntervalSeconds);
  const timeoutValid = isValidTimeoutSeconds(config.timeoutSeconds);
  let timeoutError: string | null = timeoutValid ? null : t('libLabels.videoGenerate.timeoutRange', { min: String(MIN_TIMEOUT_SECONDS), max: String(MAX_TIMEOUT_SECONDS) });
  if (pollValid && timeoutValid && config.timeoutSeconds < config.pollIntervalSeconds) {
    timeoutError = t('libLabels.videoGenerate.timeoutBelowPoll');
  }
  return {
    pollIntervalSeconds: pollValid ? null : t('libLabels.videoGenerate.pollRange', { min: String(MIN_POLL_INTERVAL_SECONDS), max: String(MAX_POLL_INTERVAL_SECONDS) }),
    timeoutSeconds: timeoutError,
  };
}

/**
 * Mirrors .NET `char.IsControl` (U+0000–U+001F, U+007F–U+009F), allowing \n, \r and \t.
 * A loop rather than a regex so no control-character literal lands in source.
 */
function hasUnsupportedControlChar(text: string): boolean {
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    const isControl = code <= 0x1f || (code >= 0x7f && code <= 0x9f);
    if (isControl && code !== 0x0a && code !== 0x0d && code !== 0x09) return true;
  }
  return false;
}

/**
 * Structural errors for one optional `ExtractInputRef`, naming the field so the author knows which
 * one to fix. Mirrors the C# `ValidateInputRef` helper (Plan/Analysis accept only From=Previous or
 * From=Step, and From=Step needs a StepOrder); a null reference is valid here.
 */
function inputRefStructuralError(reference: ExtractInputRef, field: 'plan' | 'analysis', t: T): string | null {
  if (reference.from !== 'Previous' && reference.from !== 'Step') {
    return t(`libLabels.videoGenerate.${field}Source`);
  }
  if (reference.from === 'Step' && reference.stepOrder == null) {
    return t(`libLabels.videoGenerate.${field}Order`);
  }
  return null;
}

/**
 * Inline errors for the editor's Plan / Analysis step-reference pickers, mirroring the C#
 * `Validate()` rules for those two fields: `plan` is REQUIRED when `planSource` is `StepRef`, and
 * `analysis` is never required — only structurally checked when the author set one.
 */
export function getVideoGenerateRefErrors(
  config: Pick<VideoGenerateStepConfig, 'planSource' | 'plan' | 'analysis'>,
  t: T = enT,
): { plan: string | null; analysis: string | null } {
  const plan = config.planSource === 'StepRef' && config.plan == null
    ? t('libLabels.videoGenerate.planRequired')
    : (config.plan ? inputRefStructuralError(config.plan, 'plan', t) : null);
  return {
    plan,
    analysis: config.analysis ? inputRefStructuralError(config.analysis, 'analysis', t) : null,
  };
}

/**
 * Returns every rule violation for a VideoGenerate config, in the backend's check order.
 *
 * The `inlinePrompt` rules mirror `VideoGenerateStepExecutor.ExecuteInlineAsync`, which only ever
 * reads the prompt on the Inline path — so they apply to `planSource: 'Inline'` only, and a
 * plan-sourced step is not required to carry a prompt text the backend would ignore.
 */
export function validateVideoGenerateConfig(config: VideoGenerateStepConfig, t: T = enT): string[] {
  const errors: string[] = [];

  if (!isValidMaxSpendCredits(config.maxSpendCredits)) errors.push(t('libLabels.videoGenerate.maxSpend'));

  if (config.planSource === 'Inline') {
    const prompt = config.inlinePrompt;
    if (prompt == null || prompt.trim().length === 0) {
      errors.push(t('libLabels.videoGenerate.promptRequired'));
    } else {
      if (prompt.length > INLINE_PROMPT_MAX_LENGTH) errors.push(t('libLabels.videoGenerate.promptTooLong', { max: String(INLINE_PROMPT_MAX_LENGTH) }));
      if (hasUnsupportedControlChar(prompt)) errors.push(t('libLabels.videoGenerate.promptControl'));
    }
  }

  const takesValid = isIntInRange(config.takes);
  const maxClipsValid = isIntInRange(config.maxClips);
  if (!takesValid) errors.push(t('libLabels.videoGenerate.takesRange'));
  if (!maxClipsValid) errors.push(t('libLabels.videoGenerate.maxClipsRange'));
  if (maxClipsValid && config.takes > config.maxClips) errors.push(t('libLabels.videoGenerate.takesExceed'));

  const duration = config.durationSeconds;
  if (!(typeof duration === 'number' && Number.isFinite(duration) && duration > 0)) errors.push(t('libLabels.videoGenerate.duration'));

  const polling = getVideoGeneratePollingErrors(config, t);
  if (polling.pollIntervalSeconds) errors.push(polling.pollIntervalSeconds);
  if (polling.timeoutSeconds) errors.push(polling.timeoutSeconds);

  // Appended AFTER every pre-existing rule (and after MaxSpendCredits, which stays first) so an Inline
  // config produces the exact error list it always did — same discipline as the C# Validate().
  const refs = getVideoGenerateRefErrors(config, t);
  if (refs.plan) errors.push(refs.plan);
  if (refs.analysis) errors.push(refs.analysis);

  return errors;
}

/** Minimal step shape both workflow builder pages hold (`StepData` satisfies it). */
export interface VideoGenerateValidatableStep {
  stepType: string;
  videoGenerateConfig: VideoGenerateStepConfig | null;
}

/**
 * Returns `Step N: <first message>` (1-based) for the first VideoGenerate step that breaks a rule,
 * else null. A VideoGenerate step with a null config is an error: both builder pages would send
 * `videoGenerateConfigJson: null`, while the editor displays the default config for it (Max Spend
 * 0), so it reports the same first message that default config would.
 */
export function findInvalidVideoGenerateStep(steps: readonly VideoGenerateValidatableStep[], t: T = enT): string | null {
  for (const [i, step] of steps.entries()) {
    if (step.stepType !== 'VideoGenerate') continue;
    const errors = step.videoGenerateConfig ? validateVideoGenerateConfig(step.videoGenerateConfig, t) : [t('libLabels.videoGenerate.maxSpend')];
    if (errors.length > 0) return t('libLabels.videoGenerate.stepPrefix', { number: i + 1, error: errors[0] });
  }
  return null;
}
