/**
 * Audio cleanup: the wire types of the clip-cleanup routes and the choices the panel offers.
 * Nothing here is an editor-contract type: applying a result is the existing `setProperty assetRef` op.
 */

/** The server's target names. */
export type CleanupTarget = 'VoiceBasic' | 'Hum' | 'Wind' | 'Hiss' | 'Echo' | 'IsolateVoice' | 'RemoveMusic';
export type CleanupOutputKind = 'Auto' | 'Audio' | 'VideoWithCleanAudio';

/** What the panel shows: Basic and Studio, and the "Remove a noise" picker. */
export type CleanupChoice = 'basic' | 'studio' | 'hum' | 'wind' | 'hiss' | 'echo' | 'music';

export const CLEANUP_CHOICES: readonly CleanupChoice[] = ['basic', 'studio', 'hum', 'wind', 'hiss', 'echo', 'music'];
/** The "Remove a noise" picker: exactly these five, four free and one AI. */
export const NOISE_CHOICES: readonly CleanupChoice[] = ['hum', 'wind', 'hiss', 'echo', 'music'];

const TARGETS: Record<CleanupChoice, CleanupTarget> = {
  basic: 'VoiceBasic',
  studio: 'IsolateVoice',
  hum: 'Hum',
  wind: 'Wind',
  hiss: 'Hiss',
  echo: 'Echo',
  music: 'RemoveMusic',
};

export const targetOf = (choice: CleanupChoice): CleanupTarget => TARGETS[choice];

/** The AI (metered, sent to an outside service) targets. Every other target runs on our own servers for free. */
export const isAiTarget = (target: CleanupTarget): boolean => target === 'IsolateVoice' || target === 'RemoveMusic';

export interface CleanupRequest {
  target: CleanupTarget;
  allowSourceMediaEgress?: boolean;
  outputKind?: CleanupOutputKind;
}

export interface CleanupStart {
  executionId: string;
  target: CleanupTarget;
  hosted: boolean;
  state?: string;
  credits?: number | null;
}

export interface CleanupEstimate {
  billingEnabled: boolean;
  hosted: boolean;
  credits?: number | null;
  seconds?: number | null;
  balanceCredits?: number | null;
  affordable: boolean;
  cached: boolean;
  unavailableReason?: string | null;
}

export type CleanupState = 'queued' | 'running' | 'ready' | 'failed' | 'cancelled';

export interface CleanupJob {
  executionId: string;
  state: CleanupState;
  target: CleanupTarget;
  method?: string | null;
  /** `false` on a ready job means there was nothing to remove (for example no hum was found). */
  applied?: boolean | null;
  durationSec?: number | null;
  outputFileId?: string | null;
  message?: string | null;
  failureCode?: string | null;
}
