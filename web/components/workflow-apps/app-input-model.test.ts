import { describe, expect, it } from 'vitest';
import type { AppManifestInput } from '@/lib/types/workflow';
import {
  briefInputs,
  costWords,
  fieldRefusalOf,
  firstMissing,
  initialValues,
  isPermissionInput,
  isUnderMore,
  isVisible,
  pageLayout,
  pricingValues,
  refusalMessage,
  unavailableMessage,
  valuesFromBrief,
  withoutPermission,
} from './app-input-model';
import { fixActionFor, itemsFromFindings } from './run-model';
import { createTranslator } from 'use-intl/core';
import { loadMessages } from '@/i18n/messages';
import { stableT, type BaseTranslator, type T } from '@/i18n/translator';

/** A full translator over the real catalogue, `has` scoped to `apps` (as the run page binds the linter's). */
function translator(locale: 'en' | 'it'): T {
  const messages = loadMessages(locale);
  const root = createTranslator({ locale, messages }) as unknown as BaseTranslator;
  const scoped = createTranslator({ locale, messages, namespace: 'apps' }) as unknown as BaseTranslator;
  return stableT(scoped, root);
}
const enT = translator('en');

const text = (id: string, extra: Partial<AppManifestInput> = {}): AppManifestInput => ({ kind: 'input', id, type: 'shortText', label: id, ...extra });

describe('placement (docs/design/app-inputs.md 4.6)', () => {
  it('mirrors AppInputPlacement.IsUnderMore: the creator decides, else models and fine-tuning brief fields go under it', () => {
    expect(isUnderMore({ kind: 'input', id: 'm', type: 'model' })).toBe(true);
    expect(isUnderMore({ kind: 'input', id: 'm', type: 'model', more: false })).toBe(false);
    expect(isUnderMore({ kind: 'brief', field: 'resolution' })).toBe(true);
    expect(isUnderMore({ kind: 'brief', field: 'language' })).toBe(false);
    expect(isUnderMore({ kind: 'notes', more: true })).toBe(true);
    expect(isUnderMore(text('a'))).toBe(false);
  });

  it('a version 1 (derived) manifest uses the same layout: brief fields under "More options" per their defaults', () => {
    const inputs: AppManifestInput[] = [{ kind: 'brief', field: 'language' }, { kind: 'brief', field: 'resolution' }];
    const layout = pageLayout({ version: 1, inputs });
    expect(layout.shown.map((i) => i.field)).toEqual(['language']);
    expect(layout.more.map((i) => i.field)).toEqual(['resolution']);
  });

  it('version 2: at most 8 shown and 32 in all, extra essentials move under "More options"', () => {
    const inputs = Array.from({ length: 36 }, (_, i) => text(`t${i}`));
    const layout = pageLayout({ version: 2, inputs });
    expect(layout.shown).toHaveLength(8);
    expect(layout.more).toHaveLength(24);
    expect(layout.all).toHaveLength(32);
  });
});

describe('values', () => {
  const inputs: AppManifestInput[] = [
    text('prompt', { type: 'longText', required: true }),
    { kind: 'input', id: 'lite', type: 'toggle', label: 'Lite' },
    text('line', { required: true, showWhen: { input: 'lite', equals: false } }),
    { kind: 'input', id: 'len', type: 'duration', label: 'Length', default: '10' },
    { kind: 'input', id: 'look', type: 'choice', label: 'Look', options: [{ value: 'a' }, { value: 'b' }] },
  ];

  it('starts from the defaults: a switch with none is off, a number default is a number', () => {
    expect(initialValues(inputs)).toEqual({ prompt: null, lite: false, line: null, len: 10, look: null });
  });

  it('showWhen hides by the switch value; a hidden required field is neither missing nor sent', () => {
    const values = { prompt: ' A dog ', lite: true, line: null, len: 10, look: 'b' };
    expect(isVisible(inputs[2]!, values)).toBe(false);
    expect(firstMissing(inputs, values)).toBeNull();
    expect(briefInputs(inputs, values)).toEqual({ prompt: 'A dog', lite: true, len: 10, look: 'b' });
    expect(firstMissing(inputs, { ...values, lite: false })?.id).toBe('line');
    expect(firstMissing(inputs, { ...values, prompt: '   ' })?.id).toBe('prompt');
  });

  it('prices on everything but free text', () => {
    expect(pricingValues(inputs, { prompt: 'x', lite: false, line: 'y', len: 6, look: null })).toEqual({ lite: false, len: 6 });
  });

  it('shows a credits cap with its default and prices it for a person who never opens More options', () => {
    const cap: AppManifestInput = { kind: 'input', id: 'cap', type: 'number', label: 'Most credits to spend', default: 834, min: 1, max: 100000 };
    const manifest = { version: 2, inputs: [cap] } as unknown as Parameters<typeof pageLayout>[0];
    const layout = pageLayout(manifest);
    expect(layout.shown.map((i) => i.id)).toEqual(['cap']);
    expect(layout.more).toEqual([]);
    const values = initialValues([cap]);
    expect(values.cap).toBe(834);
    expect(pricingValues([cap], values)).toEqual({ cap: 834 });
  });

  it('puts a previous run\'s values back and resets what it did not carry', () => {
    const next = valuesFromBrief(inputs, { prompt: 'Old', lite: true }, { prompt: 'New', lite: false, line: 'x', len: 6, look: 'a' });
    expect(next).toEqual({ prompt: 'Old', lite: true, line: null, len: 10, look: null });
  });
});

describe('model cards', () => {
  const options = [{ costIndex: 1 }, { costIndex: 2.1 }, { costIndex: 0.4 }, { costIndex: 0.7 }, { costIndex: null }];

  it('says the relative price in words, never a figure of credits or money', () => {
    const words = options.map((o) => costWords(o, options));
    expect(words.map((w) => (w ? enT.msg(w) : null))).toEqual(['About the same price', 'About 2× the price', 'Cheapest', 'Cheaper', null]);
    const it2 = translator('it');
    expect(it2.msg(costWords({ costIndex: 1.5 }, options)!)).toBe('Circa 1,5× il prezzo');
  });

  it('words an unavailable model from its stable code, and an unknown code generically', () => {
    expect(enT.msg(unavailableMessage({ unavailableCode: 'wrongJob' }))).toBe('Cannot make this kind of clip');
    expect(enT.msg(unavailableMessage({ unavailableCode: 'somethingNew' }))).toBe('Cannot be used here');
  });

  it('says which part of the plan a model needs (docs/design/app-inputs.md 28), in both languages', () => {
    expect(enT.msg(unavailableMessage({ unavailableCode: 'notInPlan', planFeature: 'video_generation' }))).toBe('Your plan does not include video generation');
    expect(enT.msg(unavailableMessage({ unavailableCode: 'notInPlan', planFeature: 'avatars' }))).toBe('Your plan does not include avatars');
    expect(enT.msg(unavailableMessage({ unavailableCode: 'notInPlan', planFeature: 'somethingNew' }))).toBe('Your plan does not include this');
    expect(translator('it').msg(unavailableMessage({ unavailableCode: 'notInPlan', planFeature: 'audio_generation' })))
      .toBe('Il tuo piano non include musica ed effetti sonori');
  });

  it('names objects as their own part of the plan (docs/design/avatar-objects.md AO-4)', () => {
    expect(enT.msg(unavailableMessage({ unavailableCode: 'notInPlan', planFeature: 'avatar_objects' }))).toBe('Your plan does not include objects');
    expect(translator('it').msg(unavailableMessage({ unavailableCode: 'notInPlan', planFeature: 'avatar_objects' })))
      .toBe('Il tuo piano non include gli oggetti');
  });
});

describe('run-gate refusals and fixes', () => {
  it('places an app-input refusal on the field it names; other refusals are not field refusals', () => {
    expect(fieldRefusalOf({ code: 'APP_INPUT_MISSING', inputId: 'prompt' }, 'APP_INPUT_MISSING')).toEqual({ inputId: 'prompt', code: 'APP_INPUT_MISSING' });
    expect(fieldRefusalOf({ code: 'APP_INPUT_INVALID' }, 'APP_INPUT_INVALID')).toBeNull();
    expect(fieldRefusalOf({ code: 'INSUFFICIENT_CREDITS', inputId: 'x' }, 'INSUFFICIENT_CREDITS')).toBeNull();
    expect(enT.msg(refusalMessage('APP_INPUT_MISSING'))).toBe('Fill this in to run the app.');
    // A model the plan does not include is refused on its field too (docs/design/app-inputs.md 28).
    expect(fieldRefusalOf({ code: 'APP_INPUT_MODEL_NOT_IN_PLAN', inputId: 'videoModel', feature: 'video_generation' }, 'APP_INPUT_MODEL_NOT_IN_PLAN'))
      .toEqual({ inputId: 'videoModel', code: 'APP_INPUT_MODEL_NOT_IN_PLAN' });
    expect(enT.msg(refusalMessage('APP_INPUT_MODEL_NOT_IN_PLAN'))).toBe('Your plan does not include this kind of model. Choose another one or upgrade your plan.');
  });

  it('maps the fillInput and chooseModel fixes to the field they go to', () => {
    expect(fixActionFor({ kind: 'fillInput', label: '' })).toBe('input');
    expect(fixActionFor({ kind: 'chooseModel', label: '' })).toBe('model');
    const [item] = itemsFromFindings(
      [{ ruleId: 'WL028', severity: 'error', message: '', messageKey: 'WL028', args: { input: 'Prompt', inputId: 'prompt' }, fix: { kind: 'fillInput', label: 'Fill in', labelId: 'WL028.fillInput', args: { input: 'Prompt' } } }],
      [],
      enT,
    );
    expect(item!.fix).toMatchObject({ action: 'input', inputId: 'prompt' });
  });
});

describe('the permission to send media (the app\'s own field)', () => {
  const permission: AppManifestInput = { kind: 'input', id: 'allowMediaEgress', type: 'toggle', label: 'Allow sending your media to AI services', required: true, more: false, permission: 'mediaEgress' };
  const manifest = { version: 2, inputs: [text('mood'), { kind: 'input', id: 'm', type: 'model' } as AppManifestInput, permission] };

  it('sits apart from the shown fields and from More options, and is never cut by the input cap', () => {
    const layout = pageLayout(manifest);
    expect(layout.permission?.id).toBe('allowMediaEgress');
    expect(layout.shown.map((i) => i.id)).toEqual(['mood']);
    expect(layout.more.map((i) => i.id)).toEqual(['m']);
    expect(layout.all.at(-1)?.id).toBe('allowMediaEgress');
    const crowded = { version: 2, inputs: [...Array.from({ length: 16 }, (_, i) => text(`f${i}`)), permission] };
    expect(pageLayout(crowded).permission?.id).toBe('allowMediaEgress');
    expect(pageLayout({ version: 2, inputs: [text('mood')] }).permission).toBeNull();
  });

  it('is recognised only by its id together with its marker', () => {
    expect(isPermissionInput(permission)).toBe(true);
    expect(isPermissionInput({ ...permission, permission: null })).toBe(false);
    expect(isPermissionInput({ ...permission, id: 'other' })).toBe(false);
  });

  it('is sent only as a grant, is missing until switched on, and is never part of a saved page', () => {
    const inputs = pageLayout(manifest).all;
    expect(initialValues(inputs).allowMediaEgress).toBe(false);
    expect(briefInputs(inputs, { mood: 'calm', allowMediaEgress: false })).toEqual({ mood: 'calm' });
    expect(briefInputs(inputs, { mood: 'calm', allowMediaEgress: true })).toEqual({ mood: 'calm', allowMediaEgress: true });
    expect(firstMissing(inputs, { mood: 'calm', allowMediaEgress: false })?.id).toBe('allowMediaEgress');
    expect(firstMissing(inputs, { mood: 'calm', allowMediaEgress: true })).toBeNull();
    expect(firstMissing([{ ...permission, required: null }], { allowMediaEgress: false })).toBeNull();
    expect(withoutPermission(manifest).inputs.map((i) => i.id)).toEqual(['mood', 'm']);
    // "Change one thing and re-run" takes the earlier run's fields back, but never its permission.
    expect(valuesFromBrief(inputs, { mood: 'bold', allowMediaEgress: true }, {})).toMatchObject({ mood: 'bold', allowMediaEgress: false });
    const plain = { version: 2, inputs: [text('mood')] };
    expect(withoutPermission(plain)).toBe(plain);
  });

  it('WL015 sends the person to the field (fillInput), in both languages', () => {
    const finding = {
      ruleId: 'WL015', severity: 'error' as const, stepOrder: 1, message: 'x', args: { stepOrder: '1', inputId: 'allowMediaEgress' },
      fix: { kind: 'fillInput' as const, label: 'Allow sending your media on the app page', labelId: 'WL015.allowOnAppPage' },
    };
    expect(fixActionFor(finding.fix)).toBe('input');
    const [item] = itemsFromFindings([finding], [], enT);
    expect(item?.fix?.inputId).toBe('allowMediaEgress');
    for (const locale of ['en', 'it'] as const) {
      const t = translator(locale);
      expect(t.has('lint.fix.WL015.allowOnAppPage')).toBe(true);
      expect(t.has('permission.mediaEgress.required')).toBe(true);
    }
  });
});
