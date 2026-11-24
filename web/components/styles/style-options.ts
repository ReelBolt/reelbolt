import type {
  RunChoices,
  RunOutputFormat,
  RunResolutionChoice,
  StyleConfig,
  StylePreset,
  EditingStyle,
} from '@/lib/types/styles';
import { msg, type Msg } from '@/i18n/msg';
import type { T } from '@/i18n/translator';

/** One style axis: its key on {@link StyleConfig}, a plain label, and its choices in display order. */
export interface StyleAxis {
  key: Exclude<keyof StyleConfig, 'notes'>;
  label: Msg;
  description: Msg;
  options: { value: string; label: Msg }[];
}

/** Every axis a style can set, worded for the people choosing — no internal names. */
export const STYLE_AXES: StyleAxis[] = [
  {
    key: 'pacing',
    label: msg('styles.axes.pacing.label'),
    description: msg('styles.axes.pacing.description'),
    options: [
      { value: 'Slow', label: msg('styles.axes.pacing.options.slow') },
      { value: 'Medium', label: msg('styles.axes.pacing.options.medium') },
      { value: 'Fast', label: msg('styles.axes.pacing.options.fast') },
      { value: 'BeatSynced', label: msg('styles.axes.pacing.options.beatSynced') },
    ],
  },
  {
    key: 'storytelling',
    label: msg('styles.axes.storytelling.label'),
    description: msg('styles.axes.storytelling.description'),
    options: [
      { value: 'Chronological', label: msg('styles.axes.storytelling.options.chronological') },
      { value: 'HookFirst', label: msg('styles.axes.storytelling.options.hookFirst') },
      { value: 'ProblemSolution', label: msg('styles.axes.storytelling.options.problemSolution') },
      { value: 'Montage', label: msg('styles.axes.storytelling.options.montage') },
    ],
  },
  {
    key: 'editing',
    label: msg('styles.axes.editing.label'),
    description: msg('styles.axes.editing.description'),
    options: [
      { value: 'JumpCuts', label: msg('styles.axes.editing.options.jumpCuts') },
      { value: 'SmoothTransitions', label: msg('styles.axes.editing.options.smooth') },
      { value: 'Mixed', label: msg('styles.axes.editing.options.mixed') },
    ],
  },
  {
    key: 'tone',
    label: msg('styles.axes.tone.label'),
    description: msg('styles.axes.tone.description'),
    options: [
      { value: 'Calm', label: msg('styles.axes.tone.options.calm') },
      { value: 'Upbeat', label: msg('styles.axes.tone.options.upbeat') },
      { value: 'Energetic', label: msg('styles.axes.tone.options.energetic') },
      { value: 'Serious', label: msg('styles.axes.tone.options.serious') },
      { value: 'Playful', label: msg('styles.axes.tone.options.playful') },
      { value: 'Inspirational', label: msg('styles.axes.tone.options.inspirational') },
    ],
  },
  {
    key: 'captions',
    label: msg('styles.axes.captions.label'),
    description: msg('styles.axes.captions.description'),
    options: [
      { value: 'Clean', label: msg('styles.axes.captions.options.clean') },
      { value: 'Karaoke', label: msg('styles.axes.captions.options.karaoke') },
      { value: 'Punch', label: msg('styles.axes.captions.options.punch') },
      { value: 'Bold', label: msg('styles.axes.captions.options.bold') },
    ],
  },
  {
    key: 'color',
    label: msg('styles.axes.color.label'),
    description: msg('styles.axes.color.description'),
    options: [
      { value: 'Natural', label: msg('styles.axes.color.options.natural') },
      { value: 'Warm', label: msg('styles.axes.color.options.warm') },
      { value: 'Cool', label: msg('styles.axes.color.options.cool') },
      { value: 'Filmic', label: msg('styles.axes.color.options.filmic') },
      { value: 'Vibrant', label: msg('styles.axes.color.options.vibrant') },
      { value: 'Muted', label: msg('styles.axes.color.options.muted') },
      { value: 'Mono', label: msg('styles.axes.color.options.mono') },
    ],
  },
  {
    key: 'music',
    label: msg('styles.axes.music.label'),
    description: msg('styles.axes.music.description'),
    options: [
      { value: 'Low', label: msg('styles.axes.music.options.low') },
      { value: 'Medium', label: msg('styles.axes.music.options.medium') },
      { value: 'High', label: msg('styles.axes.music.options.high') },
    ],
  },
  {
    key: 'graphics',
    label: msg('styles.axes.graphics.label'),
    description: msg('styles.axes.graphics.description'),
    options: [
      { value: 'None', label: msg('styles.axes.graphics.options.none') },
      { value: 'Minimal', label: msg('styles.axes.graphics.options.minimal') },
      { value: 'Moderate', label: msg('styles.axes.graphics.options.moderate') },
      { value: 'Rich', label: msg('styles.axes.graphics.options.rich') },
    ],
  },
  {
    key: 'dynamics',
    label: msg('styles.axes.dynamics.label'),
    description: msg('styles.axes.dynamics.description'),
    options: [
      { value: 'Calm', label: msg('styles.axes.dynamics.options.calm') },
      { value: 'Medium', label: msg('styles.axes.dynamics.options.medium') },
      { value: 'Fast', label: msg('styles.axes.dynamics.options.fast') },
    ],
  },
];

/** The longest note the server keeps (StyleNotesSanitizer.MaxLength). */
export const STYLE_NOTES_MAX = 500;

/** Run-time output shapes, named for where the video goes (translated; use these in components of this area). */
export const RUN_FORMAT_CHOICES: { value: RunOutputFormat; label: Msg; hint: Msg }[] = [
  { value: 'Original', label: msg('styles.format.original.label'), hint: msg('styles.format.original.hint') },
  { value: 'Vertical', label: msg('styles.format.vertical.label'), hint: msg('styles.format.vertical.hint') },
  { value: 'Square', label: msg('styles.format.square.label'), hint: msg('styles.format.square.hint') },
  { value: 'Portrait', label: msg('styles.format.portrait.label'), hint: msg('styles.format.portrait.hint') },
  { value: 'Landscape', label: msg('styles.format.landscape.label'), hint: msg('styles.format.landscape.hint') },
];

/**
 * @deprecated English-only legacy table, kept so call sites outside this area (workflows, editor,
 * execution page) keep compiling until they resolve `RUN_FORMAT_CHOICES` with `useT().msg()`; delete it then.
 */
export const RUN_FORMAT_OPTIONS: { value: RunOutputFormat; label: string; hint: string }[] = [
  { value: 'Original', label: 'As set up', hint: 'Keep the shape the workflow is set up with' },
  { value: 'Vertical', label: '9:16', hint: 'Vertical — Reels, TikTok, Shorts' },
  { value: 'Square', label: '1:1', hint: 'Square — feed posts' },
  { value: 'Portrait', label: '4:5', hint: 'Portrait — feed posts that fill more of the screen' },
  { value: 'Landscape', label: '16:9', hint: 'Landscape — YouTube and websites' },
];

/** Run-time output resolutions (translated; use these in components of this area). */
export const RUN_RESOLUTION_CHOICES: { value: RunResolutionChoice; label: Msg; hint: Msg; shortSide: number | null }[] = [
  { value: 'Auto', label: msg('styles.resolution.auto.label'), hint: msg('styles.resolution.auto.hint'), shortSide: null },
  { value: '720p', label: msg('styles.resolution.p720.label'), hint: msg('styles.resolution.p720.hint'), shortSide: 720 },
  { value: '1080p', label: msg('styles.resolution.p1080.label'), hint: msg('styles.resolution.p1080.hint'), shortSide: 1080 },
  { value: '2160p', label: msg('styles.resolution.p2160.label'), hint: msg('styles.resolution.p2160.hint'), shortSide: 2160 },
];

/**
 * @deprecated English-only legacy table (see RUN_FORMAT_OPTIONS). `shortSide` is still the source for
 * the plan-cap checks outside this area. Run-time output resolutions, by short side. `shortSide` is null for Auto, which keeps today's
 * output (up to 1080p, or the plan's cap if lower) and is never refused by the plan.
 */
export const RUN_RESOLUTION_OPTIONS: {
  value: RunResolutionChoice;
  label: string;
  hint: string;
  shortSide: number | null;
}[] = [
  { value: 'Auto', label: 'Auto', hint: "Same as today: up to 1080p, or your plan's limit if lower", shortSide: null },
  { value: '720p', label: '720p', hint: 'Smaller files, faster renders', shortSide: 720 },
  { value: '1080p', label: '1080p', hint: 'Full HD', shortSide: 1080 },
  { value: '2160p', label: '4K', hint: 'Ultra HD, 2160p. Uses about 4x the render credits of 1080p', shortSide: 2160 },
];

/** Where a plan refusal sends the user to upgrade. */
export const BILLING_SETTINGS_PATH = '/settings/billing';

/**
 * True when a render of this short side fits the plan's cap. A null short side (Auto, audio only,
 * or a size not known here) and a null cap (unlimited, or billing off) always fit: the server stays
 * the authority and refuses anything this misses.
 */
export function fitsOutputCap(shortSide: number | null | undefined, cap: number | null | undefined): boolean {
  if (shortSide == null || cap == null) return true;
  return shortSide <= cap;
}

/**
 * Why an option is unavailable: "Your plan renders up to 720p. Upgrade to render at 1080p or 4K."
 * below 1080p, "Your plan renders up to 1080p. Upgrade to render in 4K." from 1080p up.
 */
export function outputCapMsg(cap: number): Msg {
  return cap < 1080 ? msg('styles.cap.under1080', { cap }) : msg('styles.cap.from1080', { cap });
}

/** @deprecated English-only legacy; use `outputCapMsg` with `useT().msg()`. */
export function outputCapHint(cap: number): string {
  return cap < 1080
    ? `Your plan renders up to ${cap}p. Upgrade to render at 1080p or 4K.`
    : `Your plan renders up to ${cap}p. Upgrade to render in 4K.`;
}

/** The display label of a run's resolution as the API reports it (`2160p` → "4K"), or null. */
export function resolutionLabel(value: string | null | undefined): string | null {
  if (!value) return null;
  return RUN_RESOLUTION_OPTIONS.find((o) => o.value === value)?.label ?? value;
}

/** "Fast · Hook first · Punch captions" — the choices a style makes, in short words. */
export function describeStyle(config: StyleConfig | null | undefined, t: T): string {
  if (!config) return '';
  const parts: string[] = [];
  for (const axis of STYLE_AXES) {
    const value = config[axis.key];
    if (!value) continue;
    const option = axis.options.find((o) => o.value === value);
    const label = option ? t.msg(option.label) : String(value);
    parts.push(label.split(' — ')[0]);
  }
  if (config.notes?.trim()) parts.push(t.msg(msg('styles.describe.notes')));
  return parts.join(' · ');
}

/** True when a style sets nothing at all. */
export function isEmptyStyle(config: StyleConfig): boolean {
  return STYLE_AXES.every((axis) => !config[axis.key]) && !config.notes?.trim();
}

/**
 * A style picker's value: `none` for no style, `preset:<key>` or `style:<id>`. Encoded as one
 * string because a select holds one value.
 */
export type StyleChoiceValue = string;

/** The picker value for "no style". */
export const NO_STYLE: StyleChoiceValue = 'none';

export function choiceFor(value: StyleChoiceValue): Pick<RunChoices, 'styleId' | 'stylePresetKey'> {
  if (value.startsWith('preset:')) return { stylePresetKey: value.slice('preset:'.length) };
  if (value.startsWith('style:')) return { styleId: value.slice('style:'.length) };
  return {};
}

/** The format a preset suggests, or null for a saved style / no style. */
export function suggestedFormatFor(value: StyleChoiceValue, presets: StylePreset[] | undefined): RunOutputFormat | null {
  if (!value.startsWith('preset:')) return null;
  return presets?.find((p) => `preset:${p.key}` === value)?.suggestedFormat ?? null;
}

/** The style config behind a picker value, for showing what a choice will do. */
export function configFor(
  value: StyleChoiceValue,
  presets: StylePreset[] | undefined,
  styles: EditingStyle[] | undefined,
): StyleConfig | null {
  if (value.startsWith('preset:')) return presets?.find((p) => `preset:${p.key}` === value)?.config ?? null;
  if (value.startsWith('style:')) return styles?.find((s) => `style:${s.id}` === value)?.config ?? null;
  return null;
}
