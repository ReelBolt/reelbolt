import { describe, expect, it } from 'vitest';
import en from '@/messages/en/editor-lib.json';
import it_ from '@/messages/it/editor-lib.json';
import type { Msg } from '@/i18n/msg';
import { KEY_ACTIONS } from '../keymap';
import { TRANSITION_TYPES } from '../engine/ops';
import { MORE_TRANSITIONS, TRANSITION_WORDS, transitionLabel, transitionParity, addTransitionOp, makeRoomOps, findSeams } from '../clip-depth/transitions';
import { MOTION_PRESETS } from '../clip-depth/motion-presets';
import { FIX_SPEC, GALLERY_LOOKS } from '../color/look-gallery';
import { COLOR_PRESETS, TEXT_POSITION_CHOICES, TITLE_TEMPLATE_CHOICES } from '../structure/graphics';
import * as L from '../labels-msg';
import { transitionReasonMsg } from '../reason-msg';

const lookup = (cat: unknown, key: string): unknown => key.replace(/^editor-lib\./, '').split('.').reduce<unknown>((o, k) => (o && typeof o === 'object' ? (o as Record<string, unknown>)[k] : undefined), cat);
/** Every Msg must resolve to a string in BOTH catalogues. */
export function expectResolves(m: Msg): void {
  expect(typeof lookup(en, m.$msg), `en ${m.$msg}`).toBe('string');
  expect(typeof lookup(it_, m.$msg), `it ${m.$msg}`).toBe('string');
  for (const v of Object.values(m.values ?? {})) if (typeof v === 'object' && v !== null && '$msg' in v) expectResolves(v as Msg);
}

describe('editor-lib label tables', () => {
  it('has a key for every keymap action, transition, motion preset, look and graphic choice', () => {
    for (const a of KEY_ACTIONS) { expectResolves(L.keyActionLabelMsg(a.id)); expectResolves(L.keyGroupLabelMsg(a.group)); }
    for (const t of TRANSITION_TYPES) { expectResolves(L.transitionLabelMsg(t)); expect(L.transitionLabelMsg(t).$msg).not.toBe('editor-lib.transitions.unknown'); }
    for (const w of [...TRANSITION_WORDS, ...MORE_TRANSITIONS]) expectResolves(L.transitionLabelMsg(w.type));
    for (const p of MOTION_PRESETS) { expectResolves(L.motionLabelMsg(p.id)); expectResolves(L.motionHintMsg(p.id)); if (p.strength) expectResolves(L.motionStrengthLabelMsg(p.strength.label)); }
    for (const l of GALLERY_LOOKS) expectResolves(L.lookNameMsg(l));
    for (const k of Object.keys(FIX_SPEC)) { expectResolves(L.fixLabelMsg(k)); expectResolves(L.fixHintMsg(k)); }
    for (const c of TITLE_TEMPLATE_CHOICES) { expectResolves(L.titleTemplateLabelMsg(c.id)); expectResolves(L.titleTemplateHintMsg(c.id)); }
    for (const c of TEXT_POSITION_CHOICES) expectResolves(L.textPositionLabelMsg(c.id));
    for (const c of COLOR_PRESETS) expectResolves(L.colorPresetNameMsg(c.name));
    expectResolves(L.graphicAddLabelMsg('title')); expectResolves(L.graphicAddLabelMsg('colorCard'));
    expectResolves(transitionParityLine('fade')); expectResolves(transitionParityLine('wipetl'));
    void transitionLabel; void transitionParity;
  });

  it('maps every transition refusal sentence the lib can return', () => {
    const clip = (id: string, a: number, b: number, extra: object = {}) => ({ id, type: 'mediaClip', recordIn: a, recordOut: b, sourceIn: 0, ...extra });
    const doc = { tracks: [{ id: 't', kind: 'video', items: [clip('a', 0, 2), clip('b', 2, 100)] }] } as never;
    const seam = findSeams(doc)[0];
    const r = addTransitionOp(seam, { transitionType: 'dissolve', frames: 10 }, () => 'x');
    expect('reason' in r).toBe(true);
    if ('reason' in r) expect(transitionReasonMsg(r.reason).$msg).not.toBe('editor-lib.common.raw');
    const m = makeRoomOps(doc, seam, 30);
    if ('reason' in m) expect(transitionReasonMsg(m.reason).$msg).not.toBe('editor-lib.common.raw');
    expect(transitionReasonMsg('something else').$msg).toBe('editor-lib.common.raw');
  });
});

const transitionParityLine = L.transitionParityLineMsg;
