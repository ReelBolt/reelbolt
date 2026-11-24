import type {
  AppFileRole,
  AppManifest,
  AppManifestInput,
  LintFinding,
  LintFix,
  RunBrief,
  WorkflowExecution,
} from '@/lib/types/workflow';
import type { ProjectFile } from '@/lib/types/project';
import { msg, type Msg } from '@/i18n/msg';
import type { T } from '@/i18n/translator';
import { docsHomeHref } from '@/lib/onboarding/guide-links';
import { findingMessage, fixLabel, type LintTranslator } from '@/lib/lint-messages';

/** Text that is either a server-supplied string or a catalogue message. */
export type MaybeMsg = string | Msg;
export function resolveText(t: T, value: MaybeMsg): string {
  return typeof value === 'string' ? value : t.msg(value);
}

/** The most inputs an app shows (spec 4.1). */
export const MAX_APP_INPUTS = 8;

/** The languages offered by the language input; `auto` sends no language (the source decides). */
export const APP_LANGUAGES: { value: string; label: MaybeMsg }[] = [
  { value: 'auto', label: msg('apps.run.languages.sameAsSource') },
  { value: 'en', label: 'English' },
  { value: 'it', label: 'Italiano' },
  { value: 'es', label: 'Español' },
  { value: 'fr', label: 'Français' },
  { value: 'de', label: 'Deutsch' },
  { value: 'pt', label: 'Português' },
  { value: 'nl', label: 'Nederlands' },
  { value: 'pl', label: 'Polski' },
  { value: 'ja', label: '日本語' },
  { value: 'zh', label: '中文' },
];

/** The same languages for the footage's spoken language, where `auto` means "detect it". */
export const SOURCE_LANGUAGES = APP_LANGUAGES.map((l) => (l.value === 'auto' ? { ...l, label: msg('apps.run.languages.detect') } : l));

export const LENGTH_MIN_SEC = 5;
export const LENGTH_MAX_SEC = 600;

/** The ordered inputs of a manifest, capped like the validator caps them. */
export function inputsOf(manifest: AppManifest | null | undefined): AppManifestInput[] {
  return (manifest?.inputs ?? []).slice(0, MAX_APP_INPUTS);
}

export function briefInput(inputs: AppManifestInput[], field: string): AppManifestInput | undefined {
  return inputs.find((i) => i.kind === 'brief' && i.field === field);
}

/** The words for a file role in a sentence ("Add at least one video clip"). */
const ROLE_NOUN: Record<AppFileRole, { one: Msg; other: Msg }> = {
  video: { one: msg('apps.run.roles.video.one'), other: msg('apps.run.roles.video.other') },
  code: { one: msg('apps.run.roles.code.one'), other: msg('apps.run.roles.code.other') },
  music: { one: msg('apps.run.roles.music.one'), other: msg('apps.run.roles.music.other') },
  sfx: { one: msg('apps.run.roles.sfx.one'), other: msg('apps.run.roles.sfx.other') },
  image: { one: msg('apps.run.roles.image.one'), other: msg('apps.run.roles.image.other') },
  document: { one: msg('apps.run.roles.document.one'), other: msg('apps.run.roles.document.other') },
  insertContent: { one: msg('apps.run.roles.insertContent.one'), other: msg('apps.run.roles.insertContent.other') },
  overlay: { one: msg('apps.run.roles.overlay.one'), other: msg('apps.run.roles.overlay.other') },
};

/** The noun for a file role as a message, for lists. Whole sentences use a per-role `select` message instead. */
export function roleNoun(role: AppFileRole, plural = false): Msg {
  return plural ? ROLE_NOUN[role].other : ROLE_NOUN[role].one;
}

/** The `accept` attribute of the one-click upload for a role. */
export function acceptFor(role: AppFileRole): string {
  switch (role) {
    case 'video':
      return 'video/*';
    case 'music':
    case 'sfx':
      return 'audio/*';
    case 'image':
      return 'image/*';
    case 'insertContent':
    case 'overlay':
      return 'image/*,video/*';
    case 'document':
      return '.pdf,.doc,.docx,.txt,.md,.rtf';
    default:
      return '';
  }
}

const CODE_MIMES = ['application/json', 'application/javascript', 'application/xml'];

/** The most files one slot sends when the manifest sets no maximum (the brief's own cap per role). */
export const DEFAULT_MAX_FILES = 50;

/**
 * Whether a project file fills a role. Roles are worked out from the file type (there is no stored
 * role), with the same counts the linter's source check uses: audio counts as both music and sound
 * effects, and code is source files, text, archives and files synced from GitHub.
 */
export function fileHasRole(file: Pick<ProjectFile, 'mimeType' | 'originalFileName'> & Partial<Pick<ProjectFile, 'category' | 'sourceKind'>>, role: AppFileRole): boolean {
  // The linter and the gallery count only the project's own uploads (`userFiles`), not agent or output files.
  if (file.category !== undefined && file.category !== 'userFiles') return false;
  const mime = (file.mimeType ?? '').toLowerCase();
  const name = file.originalFileName ?? '';
  switch (role) {
    case 'video':
      return mime.startsWith('video/');
    case 'music':
    case 'sfx':
      return mime.startsWith('audio/');
    case 'image':
      return mime.startsWith('image/');
    case 'insertContent':
    case 'overlay':
      return mime.startsWith('image/') || mime.startsWith('video/');
    case 'document':
      return mime === 'application/pdf' || mime.includes('word') || mime === 'text/plain' || /\.(pdf|docx?|rtf)$/i.test(name);
    case 'code':
      // The same definition as the readiness facts: GitHub files, text, and these archive/data types.
      return file.sourceKind === 'github' || mime.startsWith('text/') || CODE_MIMES.includes(mime);
    default:
      return false;
  }
}

/** `brief.sourceFiles` roles are capitalized on the wire. */
export function wireRole(role: AppFileRole): 'Video' | 'Code' | 'Music' | 'Sfx' | 'Image' | 'Document' | 'InsertContent' | 'Overlay' {
  const map = { video: 'Video', code: 'Code', music: 'Music', sfx: 'Sfx', image: 'Image', document: 'Document', insertContent: 'InsertContent', overlay: 'Overlay' } as const;
  return map[role];
}

export function appRoleOf(wire: string | null | undefined): AppFileRole | null {
  const lower = (wire ?? '').toLowerCase();
  return (['video', 'code', 'music', 'sfx', 'image', 'document', 'insertContent', 'overlay'] as const).find((r) => r.toLowerCase() === lower) ?? null;
}

// --------------------------------------------------------------------------- readiness strip

export type ReadinessLevel = 'fix' | 'check' | 'note';
export type ReadinessState = ReadinessLevel | 'ready';

/** What a fix button does. Anything that edits the graph is done in the Advanced view. */
export type FixAction = 'upload' | 'voice' | 'plan' | 'design' | 'providers' | 'advanced' | 'notes' | 'input' | 'model' | 'email';

export interface ReadinessItem {
  /** Stable key, so a local item is not shown twice next to the linter's finding. */
  key: string;
  level: ReadinessLevel;
  message: MaybeMsg;
  /** `input`/`model` fixes name the app input they go to (docs/design/app-inputs.md 11.2). */
  fix?: { label: MaybeMsg; action: FixAction; role?: AppFileRole; inputId?: string } | undefined;
  /** The field a blocked press moves focus to. */
  focusId?: string | undefined;
}

/** Icon and word go together; the words are exactly the spec's ("Fix needed", "Check", "Ready", "Note"). */
export const READINESS_WORD: Record<ReadinessState, Msg> = {
  fix: msg('apps.run.readiness.fix'),
  check: msg('apps.run.readiness.check'),
  ready: msg('apps.run.readiness.ready'),
  note: msg('apps.run.readiness.note'),
};

const SEVERITY_LEVEL = { error: 'fix', warning: 'check', info: 'note' } as const;

/** What a linter fix means on this page. */
export function fixActionFor(fix: LintFix | null | undefined): FixAction | null {
  switch (fix?.kind) {
    case 'uploadFiles':
      return 'upload';
    case 'chooseVoice':
      return 'voice';
    case 'changePlanOrChoice':
      return 'plan';
    case 'approveDesignSystem':
      return 'design';
    case 'chooseProvider':
      return 'providers';
    case 'fillInput':
      return 'input';
    case 'chooseModel':
      return 'model';
    case 'retargetReference':
    case 'changeSetting':
    case 'addStep':
    case 'allowEgress':
    case 'removeReference':
      return 'advanced';
    default:
      return null;
  }
}

export function itemsFromFindings(findings: readonly LintFinding[], slotRoles: readonly AppFileRole[], t: LintTranslator): ReadinessItem[] {
  return findings.map((f, i) => {
    const action = fixActionFor(f.fix);
    const inputId = (action === 'input' || action === 'model') && f.args?.inputId ? f.args.inputId : undefined;
    return {
      key: `lint:${f.ruleId}:${f.stepOrder ?? ''}:${i}`,
      level: SEVERITY_LEVEL[f.severity] ?? 'note',
      message: findingMessage(f, t),
      fix: f.fix && action
        ? { label: fixLabel(f.fix, t), action, ...(action === 'upload' && slotRoles[0] ? { role: slotRoles[0] } : {}), ...(inputId ? { inputId } : {}) }
        : undefined,
    };
  });
}

const LEVEL_RANK: Record<ReadinessLevel, number> = { fix: 0, check: 1, note: 2 };

/** The strip's overall state: the worst item, or Ready when there is none. */
export function readinessStateOf(items: readonly ReadinessItem[]): ReadinessState {
  if (items.length === 0) return 'ready';
  return [...items].sort((a, b) => LEVEL_RANK[a.level] - LEVEL_RANK[b.level])[0]!.level;
}

export function sortItems(items: readonly ReadinessItem[]): ReadinessItem[] {
  return [...items].sort((a, b) => LEVEL_RANK[a.level] - LEVEL_RANK[b.level]);
}

// --------------------------------------------------------------------------- failures

const FAILURE_SENTENCES: Record<string, Msg> = {
  INPUT_MISSING: msg('apps.failures.INPUT_MISSING'),
  INPUT_TOO_LARGE: msg('apps.failures.INPUT_TOO_LARGE'),
  SOURCE_TOO_LARGE: msg('apps.failures.SOURCE_TOO_LARGE'),
  SOURCE_TOO_LONG: msg('apps.failures.SOURCE_TOO_LONG'),
  SOURCE_TOO_DEMANDING: msg('apps.failures.SOURCE_TOO_DEMANDING'),
  SCRATCH_BUSY: msg('apps.failures.SCRATCH_BUSY'),
  SOURCE_UNRESOLVED: msg('apps.failures.SOURCE_UNRESOLVED'),
  SOURCE_NOT_FOUND: msg('apps.failures.SOURCE_NOT_FOUND'),
  SOURCE_INVALID: msg('apps.failures.SOURCE_INVALID'),
  CONFIG_INVALID: msg('apps.failures.CONFIG_INVALID'),
  NO_AUDIO_PROVIDER: msg('apps.failures.NO_AUDIO_PROVIDER'),
  VOICE_CONSENT_MISSING: msg('apps.failures.VOICE_CONSENT_MISSING'),
  VOICE_NOT_FOUND: msg('apps.failures.VOICE_NOT_FOUND'),
  VOICE_RESOLVER_UNAVAILABLE: msg('apps.failures.VOICE_RESOLVER_UNAVAILABLE'),
  RUN_RATE_LIMITED: msg('apps.failures.RUN_RATE_LIMITED'),
  INSUFFICIENT_CREDITS: msg('apps.failures.INSUFFICIENT_CREDITS'),
  PLAN_NOT_ENTITLED: msg('apps.failures.PLAN_NOT_ENTITLED'),
  CONTENT_BLOCKED: msg('apps.failures.CONTENT_BLOCKED'),
  TRANSCRIPTION_FAILED: msg('apps.failures.TRANSCRIPTION_FAILED'),
  AUDIO_GENERATE_FAILED: msg('apps.failures.AUDIO_GENERATE_FAILED'),
  IMAGE_GENERATE_FAILED: msg('apps.failures.IMAGE_GENERATE_FAILED'),
  NARRATION_OUTPUT_INVALID: msg('apps.failures.NARRATION_OUTPUT_INVALID'),
  SCRIPTWRITER_OUTPUT_INVALID: msg('apps.failures.SCRIPTWRITER_OUTPUT_INVALID'),
  DELIVERY_FAILED: msg('apps.failures.DELIVERY_FAILED'),
  INSERT_NOT_APPLIED: msg('apps.failures.INSERT_NOT_APPLIED'),
  INSERT_TRACKING_FAILED: msg('apps.failures.INSERT_TRACKING_FAILED'),
  INSERT_CONTENT_UNUSABLE: msg('apps.failures.INSERT_CONTENT_UNUSABLE'),
  INSERT_CONTENT_MISSING: msg('apps.failures.INSERT_CONTENT_MISSING'),
  INSERT_FILTER_UNAVAILABLE: msg('apps.failures.INSERT_FILTER_UNAVAILABLE'),
  UNEXPECTED_ERROR: msg('apps.failures.UNEXPECTED_ERROR'),
};

/**
 * What to do next after an insert failure; null for any other failure. `chooseClip`: another clip can fix it;
 * `slot`: which input to bring the person to (the footage, or what goes on the screen).
 */
export function insertFailureAdvice(
  code: string | null | undefined,
): { chooseClip: boolean; slot: 'video' | 'insertContent' | null } | null {
  switch (code?.toUpperCase()) {
    case 'INSERT_NOT_APPLIED':
      return { chooseClip: true, slot: 'video' };
    case 'INSERT_CONTENT_UNUSABLE':
    case 'INSERT_CONTENT_MISSING':
      return { chooseClip: true, slot: 'insertContent' };
    case 'INSERT_TRACKING_FAILED':
    case 'INSERT_FILTER_UNAVAILABLE':
      return { chooseClip: false, slot: null };
    default:
      return null;
  }
}

/** The user guide's "Which clips work" section, in the active language (outside the app's base path). */
export function insertGuideHref(locale: string): string {
  return `${docsHomeHref(locale)}recipes/tracked-screen-insert#which-clip-works`;
}

/** True only when the run has step results and every one recorded a charge of exactly zero. */
export function runChargedNothing(execution: Pick<WorkflowExecution, 'stepResults'>): boolean {
  const results = execution.stepResults ?? [];
  return results.length > 0 && results.every((r) => typeof r.creditsCharged === 'number' && r.creditsCharged <= 0);
}

export interface RunCreditsSummary {
  /** Credits charged by steps that finished. */
  finished: number;
  /** How many finished steps charged something. */
  finishedSteps: number;
  /** Credits recorded against steps that failed. */
  failed: number;
  /** True when every failed step recorded a charge of exactly zero (so it is safe to say it charged nothing). */
  failedAllZero: boolean;
}

/** What each step recorded as charged, split by whether the step finished; null when no step recorded a figure. */
export function runCreditsSummary(execution: Pick<WorkflowExecution, 'stepResults'>): RunCreditsSummary | null {
  const results = execution.stepResults ?? [];
  if (!results.some((r) => typeof r.creditsCharged === 'number')) return null;
  const summary: RunCreditsSummary = { finished: 0, finishedSteps: 0, failed: 0, failedAllZero: results.filter((r) => r.status === 'Failed').every((r) => r.creditsCharged === 0) };
  for (const r of results) {
    const c = typeof r.creditsCharged === 'number' && r.creditsCharged > 0 ? r.creditsCharged : 0;
    if (c === 0) continue;
    if (r.status === 'Failed') summary.failed += c;
    else {
      summary.finished += c;
      summary.finishedSteps += 1;
    }
  }
  return summary;
}

/** Longest plain (first-party, user-facing) engine message shown as the failure summary. */
export const MAX_PLAIN_FAILURE_CHARS = 600;

/**
 * Rows stored before the source limits were written in plain words carry a byte count and a setting name
 * ("Source video is N bytes, exceeding MaxInputBytes=..."). Such text is never shown.
 */
export const OLD_LIMIT_TEXT = /\b(?:MaxInputBytes|MaxDurationSeconds)\b|\bis \d+ bytes\b/i;

/** True for an engine message that still speaks in bytes or setting names. */
export function isOldLimitText(text: string | null | undefined): boolean {
  return !!text && OLD_LIMIT_TEXT.test(text);
}

/** A failure code in plain words; the run page keeps the technical detail one click away. */
export function failureMessage(code: string | null | undefined, fallback?: string | null): MaybeMsg {
  const known = code ? FAILURE_SENTENCES[code.toUpperCase()] : undefined;
  if (known) return known;
  if (isOldLimitText(fallback)) return FAILURE_SENTENCES.INPUT_TOO_LARGE!;
  const text = fallback?.trim();
  if (text && text.length <= MAX_PLAIN_FAILURE_CHARS) return text;
  return msg('apps.failures.generic');
}

const FAILURE_SENTENCES_EN: Record<string, string> = {
  INPUT_MISSING: 'Something this app needs was missing: a clip you chose is no longer in the project, or an earlier step produced nothing to work with.',
  INPUT_TOO_LARGE: 'Something you gave this app was too big for a step to handle, such as a very large video or a very long text. Use a smaller copy and run again.',
  SOURCE_TOO_LARGE: 'A video you chose is larger than we can edit. Trim it or export a smaller copy, then run again.',
  SOURCE_TOO_LONG: 'A video you chose is longer than we can edit. Trim it or export a shorter copy, then run again.',
  SOURCE_TOO_DEMANDING: 'This video is too demanding for us to analyse in the time we allow. Try a shorter clip or a smaller export, for example 1080p, then run again. You were not charged.',
  SCRATCH_BUSY: 'Our servers were busy with other large videos and had no room to work on yours. Nothing was lost. Try again in a few minutes.',
  SOURCE_UNRESOLVED: 'A clip or file this app needs could not be found in the project.',
  SOURCE_NOT_FOUND: 'A clip or file this app needs could not be found in the project.',
  SOURCE_INVALID: 'One of the clips could not be read.',
  CONFIG_INVALID: 'One of the app\'s steps is set up in a way that cannot run. Open the steps to fix it.',
  NO_AUDIO_PROVIDER: 'No voice service is connected. Ask your admin to connect one.',
  VOICE_CONSENT_MISSING: 'The chosen voice has no consent on file. Choose another voice.',
  VOICE_NOT_FOUND: 'The chosen voice could not be found.',
  VOICE_RESOLVER_UNAVAILABLE: 'The narration service was not available.',
  RUN_RATE_LIMITED: 'Too many runs are going at once. Try again in a few minutes.',
  INSUFFICIENT_CREDITS: 'There were not enough credits to finish.',
  PLAN_NOT_ENTITLED: 'Your plan does not include a feature this run uses, such as a higher resolution, AI-generated shots or a voice. Open the plans to compare what each includes.',
  CONTENT_BLOCKED: 'The content was blocked by a safety check.',
  TRANSCRIPTION_FAILED: 'The speech in the footage could not be transcribed.',
  AUDIO_GENERATE_FAILED: 'The music or sound could not be made.',
  IMAGE_GENERATE_FAILED: 'The picture could not be made.',
  NARRATION_OUTPUT_INVALID: 'The narration script could not be used. Try again.',
  SCRIPTWRITER_OUTPUT_INVALID: 'The script could not be used. Try again.',
  DELIVERY_FAILED: 'The finished video could not be saved.',
  INSERT_NOT_APPLIED: "We could not find a screen we could follow in your clip, so nothing was changed. Screens that are small or hand-held, have a pattern or markers across them, or leave the frame are hard to follow. You can run it again with a clip where the screen is large, steady and stays in view, or put your video in a corner or full screen instead; the assistant can do that for you.",
  INSERT_TRACKING_FAILED: 'Scanning your clip for the screen did not finish. This was on our side, and nothing was changed. Run it again.',
  INSERT_CONTENT_UNUSABLE: 'The picture or video you chose for the screen could not be used, so nothing was changed. Choose a different picture or video (a common format such as PNG, JPG or MP4) and run it again.',
  INSERT_CONTENT_MISSING: 'Nothing was chosen to put on the screen. Choose a picture or video for the screen, then run it again.',
  INSERT_FILTER_UNAVAILABLE: 'Our video tools are missing a feature needed to put content on a screen. This was on our side. Try again later or ask the assistant.',
  UNEXPECTED_ERROR: 'Something unexpected went wrong while making your video.',
};

/**
 * @deprecated English-only; kept for lib/utils/run-failure.ts (dashboard banner, execution page), which another lane
 * converts. New code uses `failureMessage` and resolves it with `useT().msg`/`resolveText`.
 */
export function failureSentence(code: string | null | undefined, fallback?: string | null): string {
  const known = code ? FAILURE_SENTENCES_EN[code.toUpperCase()] : undefined;
  if (known) return known;
  if (isOldLimitText(fallback)) return FAILURE_SENTENCES_EN.INPUT_TOO_LARGE!;
  const text = fallback?.trim();
  if (text && text.length <= MAX_PLAIN_FAILURE_CHARS) return text;
  return 'Something went wrong while making your video.';
}

// --------------------------------------------------------------------------- runs

export function isActive(execution: Pick<WorkflowExecution, 'status'> | undefined | null): boolean {
  return execution?.status === 'Queued' || execution?.status === 'Running';
}

function startedAtMs(e: WorkflowExecution): number {
  return e.startedAt ? Date.parse(e.startedAt) || 0 : 0;
}

/** The newest run first. */
export function newestFirst(executions: readonly WorkflowExecution[] | undefined): WorkflowExecution[] {
  return [...(executions ?? [])].sort((a, b) => startedAtMs(b) - startedAtMs(a));
}

/** The failed step's code, if one was recorded. */
export function failedStepCode(execution: WorkflowExecution): string | null {
  const failed = execution.stepResults?.find((r) => r.status === 'Failed' && r.failureCode);
  return failed?.failureCode ?? null;
}

// --------------------------------------------------------------------------- brief

export type PaceValue = 'calm' | 'medium' | 'fast';

/** A pace word in any case (`Fast` from a style, `fast` from the brief) as the brief's value, or null. */
export function paceValueOf(value: string | null | undefined): PaceValue | null {
  const v = value?.toLowerCase();
  return v === 'calm' || v === 'medium' || v === 'fast' ? v : null;
}

export interface PageBriefFields {
  language: string;
  /** The spoken language of the footage; `auto` sends nothing (the footage decides). */
  sourceLanguage?: string;
  lengthSec: number | null;
  /** Narration on or off; null when the app has no narration input. */
  narrated: boolean | null;
  voiceId: string | null;
  /** `project` or `none`; null when the app has no design input. */
  design: string | null;
  /** `agent` (or absent), `keep` or `mute`; only sent when the person overrides the planner. */
  overlayCardAudio?: string | null;
  /** The project avatar the run shows (brief `avatar`); null or absent sends none. */
  avatarId?: string | null;
  /** `calm`, `medium` or `fast`; absent when the app has no pace input. */
  pace?: 'calm' | 'medium' | 'fast' | null;
  sourceFiles: { role: AppFileRole; fileIds: string[] }[];
}

/** The page's own fields as brief members (the run form adds style, shape, resolution and notes). */
export function pageBrief(fields: PageBriefFields): RunBrief {
  const brief: RunBrief = {};
  if (fields.language && fields.language !== 'auto') brief.language = fields.language;
  if (fields.sourceLanguage && fields.sourceLanguage !== 'auto') brief.sourceLanguage = fields.sourceLanguage;
  if (fields.lengthSec !== null) brief.lengthSec = fields.lengthSec;
  if (fields.narrated === false) brief.voice = { mode: 'None' };
  else if (fields.narrated === true) {
    brief.voice = fields.voiceId ? { mode: 'Narrated', projectVoiceId: fields.voiceId } : { mode: 'Narrated' };
  }
  if (fields.design) brief.designSystem = fields.design;
  if (fields.overlayCardAudio === 'keep' || fields.overlayCardAudio === 'mute') brief.overlayCardAudio = fields.overlayCardAudio;
  if (fields.avatarId) brief.avatar = { avatarId: fields.avatarId };
  if (fields.pace) brief.pace = fields.pace;
  const files = fields.sourceFiles.filter((s) => s.fileIds.length > 0);
  if (files.length > 0) brief.sourceFiles = files.map((s) => ({ role: wireRole(s.role), fileIds: s.fileIds }));
  return brief;
}

/** "Run · about 40 credits", the range under it, or "No credits used". */
export function priceLines(
  estimate: {
    billingEnabled: boolean;
    typicalCredits?: number;
    lowCredits: number;
    highCredits: number;
    fixedMaxCredits?: number;
  },
  format: (n: number) => string,
  costClass: string | null | undefined,
): { label: Msg; range: Msg | null } {
  if (!estimate.billingEnabled) return { label: msg('apps.run.price.run'), range: null };
  const typical = typicalCredits(estimate);
  const fixed = estimate.fixedMaxCredits ?? 0;
  if (costClass === 'No credits' || (typical <= 0 && fixed <= 0 && estimate.highCredits <= 0)) {
    return { label: msg('apps.run.price.run'), range: msg('apps.run.price.noCreditsUsed') };
  }
  // A price shown to the cent promised more than an estimate knows: round to 10, and give one range.
  const label = msg('apps.run.price.about', { amount: format(roundEstimate(typical)), unit: format(roundEstimate(typical)) === '1' ? 'one' : 'other' });
  // The range never understates: its low end rounds down and its high end up.
  const low = roundDownEstimate(estimate.lowCredits);
  const high = roundUpEstimate(estimate.highCredits + fixed);
  if (low !== high) return { label, range: msg('apps.run.price.rangeUsually', { low: format(low), high: format(high) }) };
  return { label, range: null };
}

/** The run's central figure: the estimate's own, else the middle of its range. */
export function typicalCredits(estimate: { typicalCredits?: number; lowCredits: number; highCredits: number }): number {
  return estimate.typicalCredits ?? (estimate.lowCredits + estimate.highCredits) / 2;
}

/** An estimate for display: to the nearest 10 credits from 10 up; below that a whole credit would be too coarse. */
export function roundEstimate(n: number): number {
  return n >= 10 ? Math.round(n / 10) * 10 : n;
}

/** The low end of a range: down to the 10 credits below, from 10 up. */
export function roundDownEstimate(n: number): number {
  return n >= 10 ? Math.floor(n / 10) * 10 : n;
}

/** A shortfall figure for display: up to the next 10 credits from 10 up, so "needs about X" is never short. */
export function roundUpEstimate(n: number): number {
  return n >= 10 ? Math.ceil(n / 10) * 10 : Math.ceil(n);
}

/** A paid run at or above this many credits (high estimate plus fixed AI-shot maximum) asks first. */
export const RUN_CONFIRM_FROM_CREDITS = 50;

/** Whether pressing Run should first ask: billing on, a real cost, and not a small one. Free runs never ask. */
export function needsConfirm(
  estimate: { billingEnabled: boolean; highCredits: number; fixedMaxCredits?: number } | undefined,
  costClass: string | null | undefined,
): boolean {
  if (!estimate?.billingEnabled || costClass === 'No credits') return false;
  return estimate.highCredits + (estimate.fixedMaxCredits ?? 0) >= RUN_CONFIRM_FROM_CREDITS;
}

/** The most this run can cost as one figure (high estimate plus the fixed AI-shot maximum), rounded up to 2 decimals like the server. */
export function acceptTotal(estimate: { highCredits: number; fixedMaxCredits?: number }): number {
  return Math.ceil((estimate.highCredits + (estimate.fixedMaxCredits ?? 0)) * 100) / 100;
}

/** The confirm dialog's sentence, from the estimate's own fields: typical, the usual range and the one total you accept. */
export function confirmCopy(
  estimate: { typicalCredits?: number; lowCredits: number; highCredits: number; fixedMaxCredits?: number },
  format: (n: number) => string,
): Msg {
  const typical = estimate.typicalCredits ?? (estimate.lowCredits + estimate.highCredits) / 2;
  return msg('apps.run.confirm.body', {
    typical: format(typical),
    usually: Math.round(estimate.lowCredits * 100) !== Math.round(estimate.highCredits * 100) ? 'yes' : 'no',
    low: format(estimate.lowCredits),
    high: format(estimate.highCredits),
    total: format(acceptTotal(estimate)),
    shots: (estimate.fixedMaxCredits ?? 0) > 0 ? 'yes' : 'no',
  });
}
