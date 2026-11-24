import { describe, expect, it } from 'vitest';
import en from '@/messages/en/editor-lib.json';
import it_ from '@/messages/it/editor-lib.json';
import type { Msg } from '@/i18n/msg';
import { type OperationInput, EXPAND_SHAPES, EXPAND_SHAPE_LABELS, OPERATION_INFO, OPERATION_ORDER, operationBlocker, expandShapeSummary } from '../ai-operations';
import { AI_TOOL_GROUPS, AI_TOOL_ORDER, aiToolInfo } from '../ai-tools';
import { EXPORT_FORMATS } from '../export-formats';
import { ASPECT_PRESETS } from '../canvas-size';
import { GRAPHIC_PLACEMENT_OPTIONS, GRAPHIC_STATUS_LABEL } from '../graphics';
import { NEW_PICTURE_SHAPES, defaultPictureName } from '../new-picture';
import { EDITOR_FONTS, validateTextContent, defaultTextContent } from '../text-content';
import * as L from '../labels-msg';
import { plainGraphicErrorMsg, textProblemMsg } from '../reason-msg';

const lookup = (cat: unknown, key: string): unknown => key.replace(/^editor-lib\./, '').split('.').reduce<unknown>((o, k) => (o && typeof o === 'object' ? (o as Record<string, unknown>)[k] : undefined), cat);
function ok(m: Msg | null): void {
  expect(m).not.toBeNull();
  if (!m) return;
  expect(typeof lookup(en, m.$msg), `en ${m.$msg}`).toBe('string');
  expect(typeof lookup(it_, m.$msg), `it ${m.$msg}`).toBe('string');
  for (const v of Object.values(m.values ?? {})) if (typeof v === 'object' && v !== null && '$msg' in v) ok(v as Msg);
}

describe('lib/editor label tables', () => {
  it('has a key for every operation, tool, format, shape, status and font', () => {
    for (const op of OPERATION_ORDER) {
      const info = OPERATION_INFO[op];
      ok(L.aiOpTitleMsg(op)); ok(L.aiOpSummaryMsg(op)); ok(L.aiOpActionMsg(op));
      if (info.prompt !== 'none') { ok(L.aiOpPromptLabelMsg(op)); ok(L.aiOpPromptPlaceholderMsg(op)); }
      if (info.prompt === 'required') ok(L.operationBlockerMsg(op, { layerId: 'l', hasMask: true, prompt: '', params: {} }, true));
      for (const p of info.params) ok(L.aiOpParamLabelMsg(p.key));
    }
    for (const a of EXPAND_SHAPES) { ok(L.expandShapeLabelMsg(a)); expect(EXPAND_SHAPE_LABELS[a]).toBeTruthy(); ok(L.expandShapeSummaryMsg(a, { width: 1000, height: 1000 })); }
    for (const id of AI_TOOL_ORDER) { ok(L.aiToolTitleMsg(id)); ok(L.aiToolDescriptionMsg(id)); expect(aiToolInfo(id).title).toBeTruthy(); }
    AI_TOOL_GROUPS.forEach((_, i) => ok(L.aiToolGroupTitleMsg(i)));
    for (const f of EXPORT_FORMATS) ok(L.exportFormatDescriptionMsg(f.id));
    for (const s of ASPECT_PRESETS) ok(L.canvasShapeHintMsg(s.id));
    for (const o of GRAPHIC_PLACEMENT_OPTIONS) ok(L.graphicPlacementLabelMsg(o.value));
    for (const s of Object.keys(GRAPHIC_STATUS_LABEL)) ok(L.graphicStatusLabelMsg(s));
    for (const s of NEW_PICTURE_SHAPES) { ok(L.newPictureShapeLabelMsg(s.id)); ok(L.newPictureShapeHintMsg(s.id)); ok(L.defaultPictureNameMsg(s.id)); expect(defaultPictureName(s)).toBeTruthy(); }
    for (const f of EDITOR_FONTS) ok(L.fontNoteMsg(f.family));
    ok(L.textDefaultTitleMsg()); ok(L.maskPaintFirstMsg()); ok(L.historyReorderLayersMsg()); ok(L.historyOriginalSizeMsg());
  });

  it('keeps the same blocking decisions as the English functions', () => {
    const cases: OperationInput[] = [
      { layerId: null, hasMask: false, prompt: '', params: {} },
      { layerId: 'l', hasMask: false, prompt: '', params: {} },
      { layerId: 'l', hasMask: true, prompt: '', params: {} },
      { layerId: 'l', hasMask: true, prompt: 'x', params: { top: 16 } },
      { layerId: 'l', hasMask: true, prompt: 'x', params: {}, aspect: '9:16' },
    ];
    for (const op of OPERATION_ORDER) for (const bitmap of [true, false]) for (const c of cases) {
      expect(L.operationBlockerMsg(op, c, bitmap) === null, `${op} ${JSON.stringify(c)}`).toBe(operationBlocker(op, c, bitmap) === null);
    }
    for (const a of EXPAND_SHAPES) for (const size of [null, { width: 1000, height: 1000 }, { width: 720, height: 1280 }, { width: 1920, height: 1080 }]) {
      const m = L.expandShapeSummaryMsg(a, size);
      const e = expandShapeSummary(a, size);
      expect(m.$msg.split('.').pop() === 'already').toBe(e.includes('already'));
    }
  });

  it('maps every text-content problem', () => {
    const bad = { ...defaultTextContent(), text: '', fontFamily: '', fontSize: 0, fontWeight: 0, lineHeight: 0, letterSpacing: 9999, align: 'x', color: '', maxWidth: 1, stroke: { width: 999, color: '' }, shadow: { blur: 999, offsetX: 999, offsetY: 0, color: '' } } as never;
    const problems = validateTextContent(bad);
    expect(problems.length).toBeGreaterThan(8);
    for (const p of problems) { const m = textProblemMsg(p); expect(m.$msg, p).not.toBe('editor-lib.common.raw'); ok(m); }
    expect(plainGraphicErrorMsg('connect ECONNREFUSED x:8080').$msg).toBe('editor-lib.graphicPanel.error.infrastructure');
  });
});
