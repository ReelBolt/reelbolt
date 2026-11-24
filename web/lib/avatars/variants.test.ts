import { describe, expect, it } from 'vitest';
import type { AvatarKindAvailability, AvatarSubjectKind, ProjectAvatar } from '../types/avatars';
import {
  MAX_VARIANTS, addVariantBlock, aiVariantBlock, canAddVariant, changeableReferences, countedAvatars, descriptionFromInstruction,
  displayName, displayNamer, groupByOriginal, isVariant, originalOf, supportsVariants, takenVariantLabels, variantCountOf,
  variantDefaultName, variantDescriptionProblem, variantInstructionProblem, variantLabelProblem, variantsOf,
} from './variants';
import { avatarUsesByFile } from './model';

const av = (id: string, name: string, extra: Partial<ProjectAvatar> = {}): ProjectAvatar => ({
  id, name, subjectKind: 'Object', selectable: true, references: [], consent: { revokedAt: null }, variantOfAvatarId: null, variantLabel: null,
  variantDescription: null, variantCount: 0, ...extra,
} as unknown as ProjectAvatar);
const variant = (id: string, of: string, label: string, extra: Partial<ProjectAvatar> = {}) =>
  av(id, `own name of ${id}`, { variantOfAvatarId: of, variantLabel: label, variantDescription: `${label} paint`, ...extra });

const KINDS: Record<AvatarSubjectKind, AvatarKindAvailability> = {
  Person: { enabled: true, entitled: true }, Object: { enabled: true, entitled: true },
};

describe('avatar variants: names', () => {
  const car = av('car', 'Car', { variantCount: 2 });
  const red = variant('red', 'car', 'Red');
  const blue = variant('blue', 'car', ' Blue ');
  const all = [car, red, blue];

  it('shows a variant as its original and its label, and anything else under its own name', () => {
    expect(displayName(red, all)).toBe('Car · Red');
    expect(displayName(blue, all)).toBe('Car · Blue');
    expect(displayName(car, all)).toBe('Car');
    expect(displayNamer(all)(red)).toBe('Car · Red');
  });

  it('falls back to the own name when the original is not listed, the label is empty or nothing is known', () => {
    expect(displayName(red, [red])).toBe('own name of red');
    expect(displayName(red, undefined)).toBe('own name of red');
    expect(displayName(variant('x', 'car', '  '), [car])).toBe('own name of x');
    // The original was deleted: the server clears the link and the variant is an ordinary avatar again.
    expect(displayName(av('orphan', 'Car · Green', { variantLabel: 'Green' }), all)).toBe('Car · Green');
  });

  it('follows a rename of the original', () => {
    expect(displayName(red, [{ ...car, name: 'Van' }, red])).toBe('Van · Red');
  });

  it('builds the own name of a new variant, cut to the longest name allowed', () => {
    expect(variantDefaultName(car, ' Red ')).toBe('Car · Red');
    expect(variantDefaultName(car, 'Extremely long', 9)).toBe('Car · Ext');
  });

  it('names a variant in the Files badge too', () => {
    const uses = avatarUsesByFile([car, { ...red, references: [{ projectFileId: 'f1' }] } as unknown as ProjectAvatar]);
    expect(uses.get('f1')).toEqual([{ name: 'Car · Red', kind: 'Object' }]);
  });
});

describe('avatar variants: grouping', () => {
  const car = av('car', 'Car');
  const shoe = av('shoe', 'Shoe');
  const red = variant('red', 'car', 'Red');
  const blue = variant('blue', 'car', 'Blue');

  it('puts each variant under its original, by label, and keeps the originals in the order given', () => {
    const groups = groupByOriginal([red, shoe, car, blue]);
    expect(groups.map((g) => [g.avatar.id, g.variants.map((v) => v.id)])).toEqual([['shoe', []], ['car', ['blue', 'red']]]);
    expect(variantsOf(car, [red, shoe, car, blue]).map((v) => v.id)).toEqual(['blue', 'red']);
    expect(groupByOriginal(undefined)).toEqual([]);
  });

  it('shows a variant whose original is missing or filtered out as a card of its own', () => {
    const groups = groupByOriginal([shoe, red]);
    expect(groups.map((g) => [g.avatar.id, g.variants.length])).toEqual([['shoe', 0], ['red', 0]]);
  });

  it('attaches a variant of a variant to the original and survives a broken link', () => {
    const deep = variant('deep', 'red', 'Dark red');
    expect(originalOf(deep, [car, red, deep])?.id).toBe('car');
    expect(groupByOriginal([car, red, deep]).map((g) => [g.avatar.id, g.variants.map((v) => v.id)])).toEqual([['car', ['deep', 'red']]]);
    const self = variant('loop', 'loop', 'Loop');
    expect(originalOf(self, [self])).toBeNull();
    expect(groupByOriginal([self]).map((g) => g.avatar.id)).toEqual(['loop']);
  });

  it('does not count variants toward the limit of the project, and counts the variants of an avatar', () => {
    expect(countedAvatars([car, red, blue, shoe]).map((a) => a.id)).toEqual(['car', 'shoe']);
    expect(variantCountOf({ ...car, variantCount: 5 }, [car, red])).toBe(5);
    expect(variantCountOf({ ...car, variantCount: 0 }, [car, red, blue])).toBe(2);
    expect(variantCountOf({ id: 'car' }, [red])).toBe(1);
    expect(isVariant(red)).toBe(true);
    expect(isVariant(car)).toBe(false);
    expect(isVariant(null)).toBe(false);
  });
});

describe('avatar variants: adding one', () => {
  const car = av('car', 'Car');
  const all = [car];

  it('offers it on an original of a kind that is in the plan', () => {
    expect(addVariantBlock(car, all, KINDS)).toBeNull();
    expect(canAddVariant(car, all, KINDS)).toBe(true);
  });

  it('hides it on a variant, an import from the library, a withdrawn person and a kind that is not in the plan', () => {
    const red = variant('red', 'car', 'Red');
    expect(addVariantBlock(red, [car, red], KINDS)).toBe('isVariant');
    expect(addVariantBlock({ ...car, fromLibrary: true }, all, KINDS)).toBe('linked');
    expect(addVariantBlock({ ...car, libraryItemId: 'lib1' }, all, KINDS)).toBe('linked');
    const anna = av('anna', 'Anna', { subjectKind: 'Person', consent: { revokedAt: '2026-11-02T00:00:00Z' } as ProjectAvatar['consent'] });
    expect(addVariantBlock(anna, [anna], KINDS)).toBe('withdrawn');
    expect(addVariantBlock(car, all, { ...KINDS, Object: { enabled: true, entitled: false } })).toBe('notInPlan');
    expect(canAddVariant(car, all, { ...KINDS, Object: { enabled: true, entitled: false } })).toBe(false);
  });

  it('says so at the limit of ten, by the server count or by the listed variants', () => {
    expect(addVariantBlock({ ...car, variantCount: MAX_VARIANTS }, all, KINDS)).toBe('limit');
    const ten = Array.from({ length: MAX_VARIANTS }, (_, i) => variant(`v${i}`, 'car', `L${i}`));
    expect(addVariantBlock(car, [car, ...ten], KINDS)).toBe('limit');
    expect(addVariantBlock({ ...car, variantCount: MAX_VARIANTS - 1 }, all, KINDS)).toBeNull();
  });

  it('shows no variant UI on a server from before variants', () => {
    const old = { id: 'a', name: 'Anna', selectable: true, references: [], consent: {} } as unknown as ProjectAvatar;
    expect(supportsVariants([old])).toBe(false);
    expect(supportsVariants([])).toBe(false);
    expect(supportsVariants(undefined)).toBe(false);
    expect(addVariantBlock(old, [old], KINDS)).toBe('unsupported');
    expect(supportsVariants([{ variantCount: 0 }])).toBe(true);
    expect(supportsVariants([{ variantOfAvatarId: null }])).toBe(true);
  });

  it('lets the picture model change objects only, and only a usable one', () => {
    expect(aiVariantBlock(car)).toBeNull();
    expect(aiVariantBlock({ ...car, selectable: false })).toBe('unusable');
    expect(aiVariantBlock({ subjectKind: 'Person', selectable: true })).toBe('person');
    expect(aiVariantBlock({ selectable: true })).toBe('person');
  });
});

describe('avatar variants: texts', () => {
  it('checks the label: required, at most 60 characters, not used by a sibling', () => {
    expect(variantLabelProblem('  ')).toBe('required');
    expect(variantLabelProblem('x'.repeat(61))).toBe('tooLong');
    expect(variantLabelProblem('x'.repeat(60))).toBeNull();
    expect(variantLabelProblem(' red ', ['Red', 'Blue'])).toBe('taken');
    expect(variantLabelProblem('Green', ['Red', 'Blue'])).toBeNull();
  });

  it('checks the description and the instruction: required, 500 and 1000 characters', () => {
    expect(variantDescriptionProblem(' ')).toBe('required');
    expect(variantDescriptionProblem('x'.repeat(501))).toBe('tooLong');
    expect(variantDescriptionProblem('matte red paint, black rims')).toBeNull();
    expect(variantInstructionProblem('')).toBe('required');
    expect(variantInstructionProblem('x'.repeat(1001))).toBe('tooLong');
    expect(variantInstructionProblem('Paint it red.')).toBeNull();
  });

  it('starts the description of a model-made variant from the instruction', () => {
    expect(descriptionFromInstruction('  Paint it red.  ')).toBe('Paint it red.');
    expect(descriptionFromInstruction('x'.repeat(900))).toHaveLength(500);
  });

  it('lists the labels a sibling already uses', () => {
    const car = av('car', 'Car');
    const all = [car, variant('red', 'car', 'Red'), variant('blue', 'car', 'Blue'), variant('other', 'shoe', 'Red')];
    expect(takenVariantLabels(car, all)).toEqual(['Red', 'Blue']);
    expect(takenVariantLabels(car, all, 'red')).toEqual(['Blue']);
  });

  it('offers the usable pictures and videos of the original to change', () => {
    const refs = [
      { id: 'r1', kind: 'Image', state: 'ok' }, { id: 'r2', kind: 'Image', state: 'missing' },
      { id: 'r3', kind: 'Video', state: 'ok' }, { id: 'r4', kind: 'Audio', state: 'ok' },
    ] as ProjectAvatar['references'];
    const { pictures, videos } = changeableReferences({ references: refs });
    expect(pictures.map((r) => r.id)).toEqual(['r1']);
    expect(videos.map((r) => r.id)).toEqual(['r3']);
  });
});
