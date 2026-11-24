import { msg, type Msg } from '@/i18n/msg';
import type { EditImageOperation, ImageGraphicEstimate } from '@/lib/types/image-document';
import { reasonMsg } from '@/lib/editing/reason-msg';
import type { PictureEstimate } from './credit-estimate';
import { OPERATION_INFO, hasShape, type OperationInput, shapeRatio } from './ai-operations';
import type { AiToolId } from './ai-tools';
import { GRAPHIC_DESCRIPTION_MAX, GRAPHIC_FRAME_MAX, GRAPHIC_FRAME_MIN, type GraphicForm } from './graphics';

/**
 * i18n: catalogue-key counterparts of the English labels and sentences in lib/editor. Each returns a
 * `Msg` that the component resolves with `useT().msg(m)`. The English-returning originals are unchanged.
 * English lives in messages/en/editor-lib.json (namespace `editor-lib`).
 */

const K = 'editor-lib';

/** ai-operations.ts: OPERATION_INFO[op].title / summary / action / promptLabel / promptPlaceholder. Empty for ops without a prompt (check `info.prompt`). */
export const aiOpTitleMsg = (op: EditImageOperation): Msg => msg(`${K}.aiOps.${op}.title`);
export const aiOpSummaryMsg = (op: EditImageOperation): Msg => msg(`${K}.aiOps.${op}.summary`);
export const aiOpActionMsg = (op: EditImageOperation): Msg => msg(`${K}.aiOps.${op}.action`);
export const aiOpPromptLabelMsg = (op: EditImageOperation): Msg => msg(`${K}.aiOps.${op}.promptLabel`);
export const aiOpPromptPlaceholderMsg = (op: EditImageOperation): Msg => msg(`${K}.aiOps.${op}.promptPlaceholder`);
/** ParamField.label by its `key` (top/right/bottom/left/factor/width/height). */
export const aiOpParamLabelMsg = (key: string): Msg => msg(`${K}.aiOps.param.${key}`);

const SHAPE_KEYS: Readonly<Record<string, string>> = { '9:16': 'reel', '4:5': 'feed', '1:1': 'square', '16:9': 'landscape', '3:4': 'portrait', '4:3': 'classic', '2:3': 'tall', '3:2': 'wide' };
/** EXPAND_SHAPE_LABELS[aspect]; an unknown shape shows its ratio. */
export const expandShapeLabelMsg = (aspect: string): Msg => (SHAPE_KEYS[aspect] ? msg(`${K}.aiOps.shapes.${SHAPE_KEYS[aspect]}`) : msg(`${K}.common.raw`, { text: aspect }));

/** expandShapeSummary(aspect, layerSize) */
export function expandShapeSummaryMsg(aspect: string, layerSize: { width: number; height: number } | null): Msg {
  const ratio = shapeRatio(aspect);
  if (ratio === null) return msg(`${K}.aiOps.expandSummary.chooseShape`);
  if (!layerSize) return msg(`${K}.aiOps.expandSummary.grows`, { aspect });
  const { width, height } = layerSize;
  if (hasShape(layerSize, aspect)) {
    return msg(`${K}.aiOps.expandSummary.already`, { aspect });
  }
  if (width / height > ratio) return msg(`${K}.aiOps.expandSummary.vertical`, { width, height, to: Math.round(width / ratio) });
  return msg(`${K}.aiOps.expandSummary.horizontal`, { width, height, to: Math.round(height * ratio) });
}

/** operationBlocker(op, input, selectedIsBitmap): same decision, as a message (null = can run). */
export function operationBlockerMsg(op: EditImageOperation, input: OperationInput, selectedIsBitmap: boolean): Msg | null {
  const info = OPERATION_INFO[op];
  if (info.needsLayer) {
    if (!input.layerId) return msg(`${K}.aiOps.blocker.selectLayer`);
    if (!selectedIsBitmap) return msg(`${K}.aiOps.blocker.textLayer`);
  }
  if (info.needsMask && !input.hasMask) return msg(`${K}.aiOps.blocker.paintFirst`);
  if (info.prompt === 'required' && input.prompt.trim().length === 0) return msg(`${K}.aiOps.${op}.needsPrompt`);
  if (op === 'expand') {
    if (input.aspect) return shapeRatio(input.aspect) === null ? msg(`${K}.aiOps.expandSummary.chooseShape`) : null;
    const total = ['top', 'right', 'bottom', 'left'].reduce((sum, k) => sum + (input.params[k] ?? 0), 0);
    if (total <= 0) return msg(`${K}.aiOps.blocker.expandSide`);
  }
  return null;
}

/** ai-tools.ts: aiToolInfo(id).title / .description; AI_TOOL_GROUPS[].title by the English title. */
export const aiToolTitleMsg = (id: AiToolId): Msg => (id === 'subject' ? msg(`${K}.aiTools.subject.title`) : id === 'designGraphic' ? msg(`${K}.aiTools.designGraphic.title`) : aiOpTitleMsg(id));
export const aiToolDescriptionMsg = (id: AiToolId): Msg => (id === 'subject' ? msg(`${K}.aiTools.subject.description`) : id === 'designGraphic' ? msg(`${K}.aiTools.designGraphic.description`) : aiOpSummaryMsg(id));
const GROUP_KEYS = ['cutOut', 'fixChange', 'sizeLight', 'makeNew'] as const;
/** By position in AI_TOOL_GROUPS (0..3). */
export const aiToolGroupTitleMsg = (index: number): Msg => msg(`${K}.aiTools.groups.${GROUP_KEYS[index] ?? 'makeNew'}`);

/** export-formats.ts: EXPORT_FORMATS[].description by id ('png' | 'jpeg' | 'webp' | 'avif' | 'psd' | 'ora'). The labels are product/format names and stay. */
export const exportFormatDescriptionMsg = (id: string): Msg => msg(`${K}.exportFormats.${id}.description`);

/** canvas-size.ts: CANVAS_SHAPES[].hint by id ('16:9' ...). Labels such as 16:9 and 1080p are not words. */
export const canvasShapeHintMsg = (id: string): Msg => msg(`${K}.canvasSize.hints.${id.replace(':', 'x')}`);

/** graphics.ts */
export const graphicPlacementLabelMsg = (value: string): Msg => msg(`${K}.graphicPanel.placement.${value}`);
export const graphicStatusLabelMsg = (status: string): Msg => msg(`${K}.graphicPanel.status.${status}`);
export function graphicFormBlockerMsg(form: GraphicForm, revising: boolean): Msg | null {
  const text = form.description.trim();
  if (!text) return msg(revising ? `${K}.graphicPanel.blocker.sayChange` : `${K}.graphicPanel.blocker.describe`);
  if (text.length > GRAPHIC_DESCRIPTION_MAX) return msg(`${K}.graphicPanel.blocker.tooLong`, { max: GRAPHIC_DESCRIPTION_MAX });
  if (form.size === 'custom') {
    const ok = (v: number) => Number.isFinite(v) && v >= GRAPHIC_FRAME_MIN && v <= GRAPHIC_FRAME_MAX;
    if (!ok(form.width) || !ok(form.height)) return msg(`${K}.graphicPanel.blocker.frame`, { min: GRAPHIC_FRAME_MIN, max: GRAPHIC_FRAME_MAX });
  }
  return null;
}
const round1 = (value: number): number => (!Number.isFinite(value) ? 0 : value >= 10 ? Math.round(value) : Math.round(value * 10) / 10);
/**
 * graphicCostLine(estimate), with the text as a message. `needs` stays the string from `creditsNeededLine`
 * (lib/utils/billing, owned by the billing lane): switch it to that lane's Msg counterpart when it exists.
 */
export function graphicCostLineMsg(estimate: ImageGraphicEstimate | null | undefined, needs: string | null): { text: Msg; needs: string | null; affordable: boolean } | null {
  if (!estimate) return null;
  if (!estimate.billingEnabled) return { text: msg(`${K}.graphicPanel.cost.billingOff`), needs: null, affordable: true };
  const low = round1(estimate.lowCredits);
  const high = round1(estimate.highCredits);
  const same = low === high;
  if (!estimate.affordable) {
    return { text: msg(`${K}.graphicPanel.cost.${same ? 'aboutOneUnaffordable' : 'aboutRangeUnaffordable'}`, { low, high, balance: round1(estimate.balanceCredits) }), needs, affordable: false };
  }
  return { text: msg(`${K}.graphicPanel.cost.${same ? 'aboutOne' : 'aboutRange'}`, { low, high }), needs, affordable: true };
}



/** new-picture.ts: NEW_PICTURE_SHAPES[].label / .hint by id; defaultPictureName(shape). */
export const newPictureShapeLabelMsg = (id: string): Msg => msg(`${K}.newPicture.shapes.${id}.label`);
export const newPictureShapeHintMsg = (id: string): Msg => msg(`${K}.newPicture.shapes.${id}.hint`);
export const defaultPictureNameMsg = (id: string): Msg => msg(`${K}.newPicture.defaultName.${id}`);

/** credit-estimate.ts */
const rounded = (credits: number): number => Math.round(credits * 100) / 100;
export const creditsTextMsg = (credits: number): Msg => msg(`${K}.credits.amount`, { credits: rounded(credits) });
export function estimateLineMsg(estimate: PictureEstimate | null | undefined): Msg | null {
  if (!estimate || !estimate.billingEnabled || estimate.credits === undefined || !(estimate.credits > 0)) return null;
  return msg(`${K}.credits.about`, { credits: rounded(estimate.credits) });
}
/** runLabel(action, estimate); pass the action as a Msg (e.g. `aiOpActionMsg(op)`). */
export function runLabelMsg(action: Msg, estimate: PictureEstimate | null | undefined, unaffordable: boolean): Msg {
  const line = estimateLineMsg(estimate);
  return line && !unaffordable ? msg(`${K}.credits.runWithPrice`, { action, price: line }) : action;
}
export function unaffordableMsg(estimate: PictureEstimate | null | undefined, unaffordable: boolean): Msg | null {
  if (!estimate || !unaffordable) return null;
  const hasCredits = estimate.credits !== undefined;
  const hasBalance = estimate.balanceCredits !== undefined;
  const key = hasCredits ? (hasBalance ? 'needsHave' : 'needs') : hasBalance ? 'needsMoreHave' : 'needsMore';
  return msg(`${K}.credits.${key}`, { credits: hasCredits ? rounded(estimate.credits as number) : undefined, balance: hasBalance ? rounded(estimate.balanceCredits as number) : undefined });
}
export function usedLineMsg(chargedCredits: number | null | undefined): Msg | null {
  if (chargedCredits === null || chargedCredits === undefined || !(chargedCredits > 0)) return null;
  return msg(`${K}.credits.used`, { credits: rounded(chargedCredits) });
}
export const noProviderMsg = (canConfigure: boolean): Msg => msg(canConfigure ? `${K}.credits.noProviderAdmin` : `${K}.credits.noProviderMember`);

/** result-bar.ts. `title` is the operation's title as a Msg (`aiOpTitleMsg(op)` / `aiToolTitleMsg(id)`). */
export function changedWordsMsg(title: Msg, layerNames: readonly string[], options: boolean): Msg {
  const n = layerNames.length;
  if (n === 0) return msg(`${K}.resultBar.empty`, { title });
  if (options && n > 1) return msg(`${K}.resultBar.options`, { title, n });
  if (n === 1) return msg(`${K}.resultBar.oneLayer`, { title, name: layerNames[0] });
  return msg(`${K}.resultBar.manyLayers`, { title, n });
}
export const chargedWordsMsg = (charged: number | null): Msg => (charged !== null && charged > 0 ? msg(`${K}.resultBar.cost`, { credits: rounded(charged) }) : msg(`${K}.resultBar.free`));
export const optionLabelMsg = (index: number, total: number): Msg => msg(`${K}.resultBar.option`, { index: index + 1, total });

/** layer-mask.ts maskSaveMessage(status, code, fallback); `fallback` (the server's text) shows as it is. */
export function maskSaveMsg(status: number | null, code: string | null, fallback: string): Msg {
  if (status === 409) return msg(`${K}.maskSave.conflict`);
  if (status === 413 || status === 402) return msg(`${K}.maskSave.storageFull`);
  if (code === 'MATTE_SIZE_MISMATCH') return msg(`${K}.maskSave.sizeMismatch`);
  if (status === 404) return msg(`${K}.maskSave.gone`);
  if (status === null || status >= 500) return msg(`${K}.maskSave.failed`);
  return msg(`${K}.common.raw`, { text: fallback });
}
/** mask.ts: the text of the "paint first" error (class at the end of mask.ts). */
export const maskPaintFirstMsg = (): Msg => msg(`${K}.maskSave.paintFirst`);

/** history.ts: the two command labels the lib sets itself. */
export const historyReorderLayersMsg = (): Msg => msg(`${K}.history.reorderLayers`);
export const historyOriginalSizeMsg = (): Msg => msg(`${K}.history.originalSize`);

/** text-content.ts: DEFAULT text, EDITOR_FONTS[].note by family. Validation problems: see reason-msg.ts `textProblemMsg`. */
export const textDefaultTitleMsg = (): Msg => msg(`${K}.text.defaultTitle`);
export const fontNoteMsg = (family: string): Msg => msg(`${K}.text.fonts.${family.replace(/ /g, '')}`);

/** Errors thrown by canvas-dom.ts / mask.ts (`error.message`): the lib's sentence maps to a key, anything else shows as it is. */
export const canvasErrorMsg = (message: string): Msg => reasonMsg('canvasErrors', message);
/** Default layer / clip / track names the libs write into a document (English stays the stored default unless the caller passes the translated one). */
export const defaultNameMsg = (key: 'title' | 'colorCard' | 'subtitles' | 'voiceover' | 'voiceoverShort' | 'cameraAudio' | 'compound' | 'otherTakes' | 'backgroundColor' | 'editedVideo' | 'stillImage'): Msg => msg(`${K}.names.${key}`);

/** export-size.ts estimateSizeText: "about 1.4 MB" as a message (the number is formatted by the active locale). */
export function estimateSizeMsg(bytes: number): Msg {
  if (bytes < 1000) return msg('editor-lib.exportSize.bytes', { n: bytes });
  if (bytes < 1_000_000) return msg('editor-lib.exportSize.kb', { n: Math.round(bytes / 1000) });
  return msg('editor-lib.exportSize.mb', { n: Number((bytes / 1_000_000).toFixed(1)) });
}
