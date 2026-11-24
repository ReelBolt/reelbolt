import { msg, type Msg } from '@/i18n/msg';
import { reasonMsg } from '../reason-msg';
import type { ClipReadiness } from './readiness';
import type { BudgetReasonCode, DeviceFacts, PlaybackPlan } from './device-budget';

/** i18n counterparts of the preview lib's English (parity.ts, honesty.ts, readiness.ts, device-budget.ts, preview3s.ts). Catalogue `editor-lib.*`. */

/** parity.ts: `classify*(…).reason` -> message (the lib's sentence, unchanged). Unknown text shows as it is. */
export const parityReasonMsg = (reason: string): Msg => reasonMsg('parity', reason);

/** honesty.ts `itemTitle(itemType, name)` */
const NOUNS = new Set(['mediaClip', 'titleClip', 'caption', 'generatorClip', 'insertClip', 'adjustmentClip', 'transition', 'compoundClip', 'multicamClip']);
export function itemTitleMsg(itemType: string, name: string): Msg {
  const noun = msg(`editor-lib.honesty.noun.${NOUNS.has(itemType) ? itemType : 'item'}`);
  return name.trim() ? msg('editor-lib.honesty.itemTitle', { noun, name: name.trim() }) : noun;
}
export const aClipMsg = (): Msg => msg('editor-lib.honesty.aClip');
export const preparingPreviewMsg = (): Msg => msg('editor-lib.honesty.preparing');

/** The verdict's `label` (honesty.ts evaluate*): same decision as the English label. */
export function verdictLabelMsg(kind: 'notReady' | 'renderRequired' | 'approximate' | 'faithful', opts: { count?: number; simple?: boolean } = {}): Msg {
  switch (kind) {
    case 'notReady':
      return msg('editor-lib.honesty.verdictNotReady');
    case 'renderRequired':
      return msg('editor-lib.honesty.verdictRender', { n: opts.count ?? 0 });
    case 'approximate':
      return opts.simple ? msg('editor-lib.honesty.verdictSimple') : (opts.count ?? 0) > 0 ? msg('editor-lib.honesty.verdictApproxN', { n: opts.count ?? 0 }) : msg('editor-lib.honesty.verdictApprox');
    case 'faithful':
      return msg('editor-lib.honesty.verdictFaithful');
  }
}
/** The session lines: `sessionReason` is one of the lib's own sentences (see `previewSessionReasonMsg`). */
export const previewSessionReasonMsg = (reason: string): Msg => reasonMsg('previewSession', reason);
export const sessionUnavailableLineMsg = (reason: string | null): Msg => (reason ? previewSessionReasonMsg(reason) : msg('editor-lib.honesty.sessionUnavailable'));
export const simpleFallbackLineMsg = (reason: string | null): Msg => msg('editor-lib.honesty.simpleFallback', { reason: reason ? previewSessionReasonMsg(reason) : msg('editor-lib.honesty.simpleDefaultReason') });
export const singleClipLineMsg = (reason: string | null): Msg => (reason ? previewSessionReasonMsg(reason) : msg('editor-lib.honesty.singleClip'));
export const reducedQualityLineMsg = (reason: string): Msg => msg('editor-lib.honesty.reducedQuality', { reason: previewSessionReasonMsg(reason) });
/** The proxy/frame reasons in honesty.ts. */
export const honestyReasonMsg = (reason: string): Msg => reasonMsg('honesty', reason);

/** readiness.ts `readinessWords(readiness)`; the failure reasons are the lib's constants. */
export function readinessWordsMsg(readiness: ClipReadiness): Msg {
  switch (readiness.state) {
    case 'ready':
      return readiness.original ? msg('editor-lib.readiness.original') : msg('editor-lib.readiness.ready');
    case 'preparing':
      return readiness.percent !== null ? msg('editor-lib.readiness.preparingPercent', { percent: readiness.percent }) : msg('editor-lib.readiness.preparing');
    case 'failed':
      return msg('editor-lib.readiness.failed', { reason: reasonMsg('readiness', readiness.reason) });
    case 'offline':
      return msg('editor-lib.readiness.sourceOffline', { reason: reasonMsg('readiness', readiness.reason) });
  }
}

/** device-budget.ts `budgetOf(facts).reasons`, as messages (join the resolved strings with `listText`). */
export function budgetReasonMsgs(facts: DeviceFacts, maxCores: number, maxMemoryGb: number): Msg[] {
  const out: Msg[] = [];
  if (facts.deviceClass === 'phone') out.push(msg('editor-lib.device.phone'));
  if (facts.softwareGraphics) out.push(msg('editor-lib.device.software'));
  if (facts.cores !== null && facts.cores <= maxCores) out.push(msg('editor-lib.device.cores', { n: facts.cores }));
  if (facts.memoryGb !== null && facts.memoryGb <= maxMemoryGb) out.push(msg('editor-lib.device.memory', { n: facts.memoryGb }));
  return out;
}
/** `planPlayback(...).autoReason`: pass the resolved reasons joined with `listText(…, locale)`. */
export const autoReducedMsg = (reasonsText: string): Msg => msg('editor-lib.device.autoReduced', { reasons: reasonsText });
/** One structured budget reason (phone / software / N cores / N GB) as a message. */
export function budgetCodeMsg(code: BudgetReasonCode): Msg {
  switch (code.code) {
    case 'phone':
      return msg('editor-lib.device.phone');
    case 'software':
      return msg('editor-lib.device.software');
    case 'cores':
      return msg('editor-lib.device.cores', { n: code.n });
    case 'memory':
      return msg('editor-lib.device.memory', { n: code.n });
  }
}
/**
 * `planPlayback(...)`'s Auto sentence in the user's language: `resolve` is the translator's `t.msg`, `locale` the
 * UI locale. Falls back to the plan's English `autoReason` when the plan carries no structure.
 */
export function autoPlanText(plan: Pick<PlaybackPlan, 'autoReason' | 'autoWhy'>, resolve: (m: Msg) => string, locale: string): string | null {
  const why = plan.autoWhy;
  if (!why || why.codes.length === 0) return plan.autoReason;
  const reasons = new Intl.ListFormat(locale, { style: 'long', type: 'conjunction' }).format(why.codes.map((c) => resolve(budgetCodeMsg(c))));
  return resolve(autoReducedMsg(reasons));
}
/** PREVIEW_QUALITY_OPTIONS[].label */
export const previewQualityLabelMsg = (value: 'auto' | 'full' | 'half' | 'quarter'): Msg => msg(`editor-lib.device.quality.${value}`);

/** preview3s.ts */
export const preview3sIncludedMsg = (): Msg => msg('editor-lib.preview3s.included');
export const preview3sButtonMsg = (price: Msg | null): Msg => (price ? msg('editor-lib.preview3s.buttonPrice', { price }) : msg('editor-lib.preview3s.button'));
export const preview3sStatusMsg = (status: string): Msg => reasonMsg('preview3s', status === 'Queued' ? 'Waiting to start' : 'Rendering your preview');
export const preview3sMessageMsg = (text: string): Msg => reasonMsg('preview3s', text);
/** media/hdr.ts and proxy-urls.ts constants */
export const previewNoteMsg = (englishConstant: string): Msg => reasonMsg('previewSession', englishConstant);
