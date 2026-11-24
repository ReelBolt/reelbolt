import { msg, type Msg } from '@/i18n/msg';
import type { KeyAction, KeyActionInfo } from './keymap';
import type { CaptionLook } from './types';
import type { PoolSort, PoolStateFilter } from './media/pool-browse';
import type { PlaceMode } from './media/place';
import { reasonMsg } from './reason-msg';

/**
 * i18n: catalogue-key counterparts of the English labels in the editor libraries. Each function here
 * returns a `Msg` (resolved by the component with `useT().msg(m)`); the English-returning originals stay
 * as they are for tests, logs and the server contract. English text lives in messages/en/editor-lib.json.
 */

export { rawMsg, sentenceMsg, type ReasonEntry } from './sentence';
import { rawMsg } from './sentence';

/** keymap.ts: the action's label (callers: keymap editor, shortcut help). Shortcut keys are never translated. */
export const keyActionLabelMsg = (id: KeyAction): Msg => msg(`editor-lib.keymap.actions.${id}`);
export const keyGroupLabelMsg = (group: KeyActionInfo['group']): Msg => msg(`editor-lib.keymap.groups.${group}`);

/** clip-depth/transitions.ts: `transitionLabel(type)`, `TRANSITION_WORDS[].label`, `MORE_TRANSITIONS[].label`. */
export const transitionLabelMsg = (type: string): Msg => (TRANSITION_KEYS.has(type) ? msg(`editor-lib.transitions.${type}`) : msg('editor-lib.transitions.unknown', { type }));

const TRANSITION_KEYS: ReadonlySet<string> = new Set([
  'fade', 'dissolve', 'fadeblack', 'fadewhite', 'fadegrays', 'distance', 'pixelize', 'wipeleft', 'wiperight', 'wipeup', 'wipedown', 'wipetl', 'wipetr', 'wipebl', 'wipebr',
  'slideleft', 'slideright', 'slideup', 'slidedown', 'smoothleft', 'smoothright', 'smoothup', 'smoothdown', 'circlecrop', 'rectcrop', 'circleopen', 'circleclose',
  'vertopen', 'vertclose', 'horzopen', 'horzclose', 'diagtl', 'diagtr', 'diagbl', 'diagbr', 'hlslice', 'hrslice', 'vuslice', 'vdslice', 'radial', 'zoomin', 'squeezev', 'squeezeh', 'hblur',
]);

/** `transitionParity(type).line` as a message. */
export const transitionParityLineMsg = (type: string): Msg =>
  type === 'dissolve' || type === 'fade' ? msg('editor-lib.transitions.parity.faithful') : msg('editor-lib.transitions.parity.approximate', { name: transitionLabelMsg(type) });

/** clip-depth/motion-presets.ts: label / hint / strength label of a preset. */
export const motionLabelMsg = (id: string): Msg => msg(`editor-lib.motion.${id}.label`);
export const motionHintMsg = (id: string): Msg => msg(`editor-lib.motion.${id}.hint`);
const MOTION_STRENGTH: Readonly<Record<string, string>> = { 'How much': 'howMuch', 'How big': 'howBig', Distance: 'distance' };
export const motionStrengthLabelMsg = (englishLabel: string): Msg => (MOTION_STRENGTH[englishLabel] ? msg(`editor-lib.motion.strength.${MOTION_STRENGTH[englishLabel]}`) : rawMsg(englishLabel));

/** color/grade.ts + look-gallery.ts: the look, strength and tone names the pickers show (the node names stay English). */
export const lookNameMsg = (look: string): Msg => msg(`editor-lib.looks.names.${look === 'Teal & orange' ? 'tealOrange' : look}`);
export const lookStrengthMsg = (s: string): Msg => msg(`editor-lib.looks.strengths.${s}`);
export const shadowToneMsg = (s: string): Msg => msg(`editor-lib.looks.shadows.${s}`);
export const highlightToneMsg = (s: string): Msg => msg(`editor-lib.looks.highlights.${s}`);
/** FIX_SPEC[key].label / .hint */
export const fixLabelMsg = (key: string): Msg => msg(`editor-lib.looks.fix.${key}.label`);
export const fixHintMsg = (key: string): Msg => msg(`editor-lib.looks.fix.${key}.hint`);

/** structure/graphics.ts: TITLE_TEMPLATE_CHOICES, TEXT_POSITION_CHOICES, COLOR_PRESETS[].name, GraphicResult.label. */
export const titleTemplateLabelMsg = (id: string): Msg => msg(`editor-lib.graphics.template.${id}.label`);
export const titleTemplateHintMsg = (id: string): Msg => msg(`editor-lib.graphics.template.${id}.hint`);
export const textPositionLabelMsg = (id: string): Msg => msg(`editor-lib.graphics.position.${id}`);
export const colorPresetNameMsg = (englishName: string): Msg => msg(`editor-lib.graphics.color.${englishName}`);
export const graphicAddLabelMsg = (kind: 'title' | 'colorCard'): Msg => msg(kind === 'title' ? 'editor-lib.graphics.add.title' : 'editor-lib.graphics.add.colorCard');

/** A locale-aware "a, b and c" (type 'conjunction') or "a, b or c" (type 'disjunction') for strings the caller has already resolved. */
export function listText(items: readonly string[], locale: string, type: 'conjunction' | 'disjunction' | 'unit' = 'conjunction'): string {
  return new Intl.ListFormat(locale, { style: 'long', type }).format(items);
}

// ── small label tables and one-line sentences (catalogue `editor-lib.*`) ─────────────────────────


/** refusal.ts FIELD_LABELS[key] (Inspector/mixer field names); an unknown key shows as it is. */
export const fieldLabelMsg = (key: string): Msg => (FIELD_KEYS.has(key) ? msg(`editor-lib.refusal.fields.${key}`) : rawMsg(key));
const FIELD_KEYS: ReadonlySet<string> = new Set('name enabled text fontSize fontFamily color language playbackRate sourceIn gainDb transform crop reverse sourceRotation hold fadeInFrames fadeOutFrames transitionType easing generatorType generatorConfig assetRef grade mask keyer effects regionEffects stabilize reframe retimeQuality cornerPin matte quad template channelMap sequenceId activeAngleId cuts angles positionX positionY scale rotation opacity height locked mute solo busId pan eq dynamics denoise leveler'.split(' '));
/** refusalSentence(...) / humanizeError(...) / statusSentence(...) results: the fixed sentences become keys, the rest show as they are. */
export const refusalSentenceMsg = (sentence: string): Msg => reasonMsg('refusal', sentence);
export const humanizeErrorMsg = (sentence: string): Msg => reasonMsg('humanize', sentence);
/** history entry label (editor-store `describeOp`, `Reorder layers`, `Move picture`, …) as shown in the History/Undo UI. */
export const historyLabelMsg = (label: string): Msg => reasonMsg('history', label);
/** store/undo-result.ts `StepResult` refused: `title` / `message`. */
export const stepRefusedTitleMsg = (title: string): Msg => reasonMsg('undo', title);
export const stepRefusedMessageMsg = (message: string): Msg => reasonMsg('undo', message);
/** sync `SaveState.message` and the sync constants (READ_ONLY_MESSAGE, UNCONFIRMED_SAVE, reasons): `reasonMsg('sync', …)`. */
export const syncMessageMsg = (text: string): Msg => reasonMsg('sync', text);

/** time.ts */
export const timecodeProblemMsg = (reason: string): Msg => reasonMsg('time', reason);
export const timeFormatLabelMsg = (key: 'landscape' | 'landscape4k' | 'vertical' | 'square' | 'portrait'): Msg => msg(`editor-lib.time.formats.${key}`);
/** clip-depth/units.ts `{ ok:false, sentence }` and the `Clip is now …` line. */
export const unitsSentenceMsg = (sentence: string): Msg => reasonMsg('units', sentence);

/** media/pool-browse.ts */
export const poolSortLabelMsg = (sort: PoolSort): Msg => msg(`editor-lib.pool.sort.${sort}`);
export const poolStateLabelMsg = (state: PoolStateFilter): Msg => msg(`editor-lib.pool.state.${state}`);
/** describeRule(filter) as parts; join the resolved parts with `listText(parts, locale, 'unit')` and upper-case the first letter. */
export function describeRuleMsgs(filter: { kinds?: readonly string[]; favourite?: boolean | null; flag?: boolean | null; colorLabel?: string | null; keywords?: readonly string[]; nameContains?: string | null }, tagsText: (tags: readonly string[]) => string): Msg[] {
  const parts: Msg[] = [];
  const rule = (key: string, values?: Record<string, string>): Msg => msg(`editor-lib.pool.rule.${key}`, values);
  if (filter.kinds && filter.kinds.length > 0) filter.kinds.forEach((k) => parts.push(rule(`kinds.${k}`)));
  if (filter.favourite === true) parts.push(rule('starred'));
  if (filter.favourite === false) parts.push(rule('notStarred'));
  if (filter.flag === true) parts.push(rule('flagged'));
  if (filter.flag === false) parts.push(rule('notFlagged'));
  if (filter.colorLabel) parts.push(rule('color', { color: filter.colorLabel }));
  if (filter.keywords && filter.keywords.length > 0) parts.push(rule('tagged', { tags: tagsText(filter.keywords) }));
  if (filter.nameContains && filter.nameContains.trim()) parts.push(rule('name', { text: filter.nameContains.trim() }));
  return parts;
}
export const poolEverythingMsg = (): Msg => msg('editor-lib.pool.rule.everything');

/** media/place.ts MODE_LABEL, track display names, refusals; media/format.ts constants; media/in-out.ts reasons. */
export const placeModeLabelMsg = (mode: PlaceMode): Msg => msg(`editor-lib.place.mode.${mode}`);
export const placeTrackNameMsg = (kind: 'video' | 'audio', n: number): Msg => msg(`editor-lib.place.trackName.${kind}`, { n });
export const placeReasonMsg = (reason: string): Msg => reasonMsg('place', reason);
export const mediaFormatNoteMsg = (englishConstant: string): Msg => reasonMsg('mediaFormat', englishConstant);

/** subtitles.ts CAPTION_LOOK_LABELS, refusals (`refusal`) and import problems. */
export const captionLookLabelMsg = (look: CaptionLook): Msg => msg(`editor-lib.subtitles.looks.${look}`);
export const subtitlesTextMsg = (text: string): Msg => reasonMsg('subtitles', text);
export const subtitlesTrackNameMsg = (): Msg => msg('editor-lib.subtitles.trackName');

/** audio/dsp-preview.ts DSP_STAGE_LABEL + the English notes/reasons. */
export const dspStageLabelMsg = (stage: 'gate' | 'deesser' | 'compressor' | 'limiter'): Msg => msg(`editor-lib.dsp.stages.${stage}`);
export const dspTextMsg = (text: string): Msg => reasonMsg('dsp', text);
/** audio/vo-recorder.ts: micErrorSentence, VO_LIMIT_MESSAGE, the recorder refusals. */
export const voTextMsg = (text: string): Msg => reasonMsg('vo', text);
export const micLabelMsg = (n: number): Msg => msg('editor-lib.vo.micLabel', { n });
export const voiceoverTrackNameMsg = (): Msg => msg('editor-lib.vo.trackNew');
/** audio/fader.ts panLabel(pan) */
export const panLabelMsg = (pan: number): Msg => (pan < 0 ? msg('editor-lib.fader.pan.left', { n: Math.round(-pan * 100) }) : msg('editor-lib.fader.pan.right', { n: Math.round(pan * 100) }));

/** Other one-sentence constants and results, by table. */
export const guestTextMsg = (text: string): Msg => reasonMsg('guest', text);
export const takesTextMsg = (text: string): Msg => reasonMsg('takes', text);
export const proposalTextMsg = (text: string): Msg => reasonMsg('proposal', text);
