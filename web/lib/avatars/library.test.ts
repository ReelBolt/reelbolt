import { describe, expect, it } from 'vitest';
import type { AvatarLibraryRequirements } from '../types/avatars';
import type { AvatarLibraryItem } from '../types/avatar-library';
import {
  LIBRARY_FALLBACK_LIMITS, canSaveToLibrary, groupLibraryItems, importBlock, isLibraryOff, isLibraryVariant, isLinkedAvatar,
  libraryDisplayName, libraryFileCounts, libraryItemStatus, libraryItemsOf, libraryItemsOfKind, libraryLimits, libraryOf,
  libraryRequirementsOf, libraryStatements, libraryStatusColor, retentionDaysOk, statementsFromRefusal,
} from './library';

const LIBRARY: AvatarLibraryRequirements = {
  enabled: true, canManage: true,
  consentStatementVersion: 'avatar-consent-workspace-2026-11', consentStatements: { en: 'person en', it: 'person it' },
  objectStatementVersion: 'avatar-object-workspace-2026-11', objectStatements: { en: 'object en', it: 'object it' },
  maxItemsPerKind: 100,
};

const item = (over: Partial<AvatarLibraryItem> = {}): AvatarLibraryItem => ({
  id: 'l1', subjectKind: 'Person', name: 'Anna', description: null, usable: true, unusableReason: null,
  references: { images: 2, videos: 1, audio: 0 }, primaryReferenceId: 'r1', retentionUntil: '2027-01-01T00:00:00Z',
  importCount: 0, imported: false, canManage: false, ...over,
});

const BOTH = { Person: { enabled: true, entitled: true }, Object: { enabled: true, entitled: true } };
const ROOM = { Person: false, Object: false };

describe('avatar library model', () => {
  it('reads a server without a library, or with it switched off, as no library', () => {
    expect(libraryOf(null)).toBeNull();
    expect(libraryOf({})).toBeNull();
    expect(libraryOf({ library: { ...LIBRARY, enabled: false } })).toBeNull();
    expect(libraryOf({ library: LIBRARY })).toBe(LIBRARY);
  });

  it('knows an import by either field, and a server from before the library as no import', () => {
    expect(isLinkedAvatar({ fromLibrary: true })).toBe(true);
    expect(isLinkedAvatar({ libraryItemId: 'l1' })).toBe(true);
    expect(isLinkedAvatar({ fromLibrary: false, libraryItemId: null })).toBe(false);
    expect(isLinkedAvatar({})).toBe(false);
    expect(isLinkedAvatar(null)).toBe(false);
  });

  it('lets only a manager save a usable avatar that is not itself an import', () => {
    const own = { selectable: true, fromLibrary: false, libraryItemId: null };
    expect(canSaveToLibrary(own, LIBRARY)).toBe(true);
    expect(canSaveToLibrary(own, { canManage: false })).toBe(false);
    expect(canSaveToLibrary(own, null)).toBe(false);
    expect(canSaveToLibrary({ ...own, selectable: false }, LIBRARY)).toBe(false);
    expect(canSaveToLibrary({ ...own, fromLibrary: true }, LIBRARY)).toBe(false);
    expect(canSaveToLibrary({ ...own, libraryItemId: 'l1' }, LIBRARY)).toBe(false);
  });

  it('reads the list as a bare array or as an envelope, with the requirements an envelope may carry', () => {
    const items = [item()];
    expect(libraryItemsOf(items)).toBe(items);
    expect(libraryItemsOf({ items })).toBe(items);
    expect(libraryItemsOf(undefined)).toEqual([]);
    expect(libraryItemsOf({} as never)).toEqual([]);
    expect(libraryRequirementsOf(items)).toBeNull();
    expect(libraryRequirementsOf({ items })).toBeNull();
    expect(libraryRequirementsOf({ items, library: LIBRARY })).toBe(LIBRARY);
    expect(libraryRequirementsOf({ items, library: { ...LIBRARY, enabled: false } })).toBeNull();
  });

  it('splits the items by kind', () => {
    const items = [item(), item({ id: 'l2', subjectKind: 'Object' })];
    expect(libraryItemsOfKind(items, 'Person').map((i) => i.id)).toEqual(['l1']);
    expect(libraryItemsOfKind(items, 'Object').map((i) => i.id)).toEqual(['l2']);
  });

  it('picks the workspace statement of the kind', () => {
    expect(libraryStatements(LIBRARY, 'Person')).toEqual({ en: 'person en', it: 'person it' });
    expect(libraryStatements(LIBRARY, 'Object')).toEqual({ en: 'object en', it: 'object it' });
    expect(libraryStatements(null, 'Person')).toEqual({});
  });

  it('maps an item to one status: ready, near its end, withdrawn, expired, or unusable for a new reason', () => {
    const now = new Date('2026-12-20T00:00:00Z');
    expect(libraryItemStatus(item(), now)).toBe('expiresSoon');
    expect(libraryItemStatus(item(), new Date('2026-01-01T00:00:00Z'))).toBe('ready');
    // An object has no keep-until date and never expires.
    expect(libraryItemStatus(item({ subjectKind: 'Object', retentionUntil: null }), now)).toBe('ready');
    expect(libraryItemStatus(item({ usable: false, unusableReason: 'consent_revoked' }), now)).toBe('withdrawn');
    expect(libraryItemStatus(item({ usable: false, unusableReason: 'retention_expired' }), now)).toBe('expired');
    expect(libraryItemStatus(item({ usable: false, unusableReason: 'something_new' }), now)).toBe('unavailable');
    expect(libraryStatusColor('ready')).toBe('green');
    expect(libraryStatusColor('expiresSoon')).toBe('yellow');
    expect(libraryStatusColor('withdrawn')).toBe('gray');
    expect(libraryStatusColor('unavailable')).toBe('red');
  });

  it('blocks an import for the first reason that applies: already there, unusable, not in the plan, no room', () => {
    expect(importBlock(item(), BOTH, ROOM)).toBeNull();
    expect(importBlock(item({ imported: true, usable: false }), BOTH, ROOM)).toBe('imported');
    expect(importBlock(item({ usable: false }), BOTH, ROOM)).toBe('unusable');
    expect(importBlock(item(), { ...BOTH, Person: { enabled: true, entitled: false } }, ROOM)).toBe('notInPlan');
    expect(importBlock(item(), BOTH, { Person: true, Object: false })).toBe('projectFull');
    // Each kind has its own plan feature and its own room.
    expect(importBlock(item({ subjectKind: 'Object' }), { ...BOTH, Person: { enabled: true, entitled: false } }, { Person: true, Object: false })).toBeNull();
    // A variant whose original is in the project lands under it and needs no room of its own; everything else still applies.
    expect(importBlock(item(), BOTH, { Person: true, Object: false }, true)).toBeNull();
    expect(importBlock(item({ usable: false }), BOTH, { Person: true, Object: false }, true)).toBe('unusable');
  });

  it('groups variants under their original by label, and leaves a variant whose original is not listed on its own', () => {
    const car = item({ id: 'car', subjectKind: 'Object', name: 'Car', variantCount: 2 });
    const red = item({ id: 'red', subjectKind: 'Object', name: 'Car · Red', variantOfItemId: 'car', variantLabel: 'Red' });
    const blue = item({ id: 'blue', subjectKind: 'Object', name: 'Blue car', variantOfItemId: 'car', variantLabel: 'Blue' });
    const lone = item({ id: 'lone', subjectKind: 'Object', name: 'Bike · Green', variantOfItemId: 'bike', variantLabel: 'Green' });
    const anna = item();

    const groups = groupLibraryItems([red, anna, car, lone, blue]);

    expect(groups.map((g) => [g.item.id, g.variants.map((v) => v.id)])).toEqual([
      [anna.id, []], ['car', ['blue', 'red']], ['lone', []],
    ]);
    expect(isLibraryVariant(red)).toBe(true);
    expect(isLibraryVariant(car)).toBe(false);
    expect(isLibraryVariant(item({ variantOfItemId: null, variantLabel: 'Red' }))).toBe(false);
    expect(groupLibraryItems(undefined)).toEqual([]);
  });

  it('names a grouped variant after its original and its label, and anything else by its own name', () => {
    const car = item({ id: 'car', name: 'Car' });
    const red = item({ id: 'red', name: 'Red car', variantOfItemId: 'car', variantLabel: ' Red ' });
    expect(libraryDisplayName(red, [car, red])).toBe('Car · Red');
    expect(libraryDisplayName(red, [red])).toBe('Red car');
    expect(libraryDisplayName(car, [car, red])).toBe('Car');
    // A server from before library variants sends no variant fields.
    expect(libraryDisplayName(item({ id: 'x', name: 'Old' }), [car])).toBe('Old');
  });

  it('counts the files of an item, and reads missing counts as zero', () => {
    expect(libraryFileCounts(item())).toEqual({ images: 2, videos: 1, audio: 0 });
    expect(libraryFileCounts({} as never)).toEqual({ images: 0, videos: 0, audio: 0 });
  });

  it('uses a project limit when it has one and the contract figure otherwise', () => {
    expect(libraryLimits()).toEqual(LIBRARY_FALLBACK_LIMITS);
    expect(libraryLimits({ maxRetentionDays: 90, defaultRetentionDays: 30 })).toMatchObject({ minRetentionDays: 1, maxRetentionDays: 90, defaultRetentionDays: 30, revokedGraceDays: 30 });
    expect(libraryLimits({ maxNameLength: Number.NaN })).toEqual(LIBRARY_FALLBACK_LIMITS);
    expect(LIBRARY_FALLBACK_LIMITS).toMatchObject({ minRetentionDays: 1, maxRetentionDays: 730, defaultRetentionDays: 365 });
  });

  it('accepts only a whole number of days inside the range', () => {
    const limits = LIBRARY_FALLBACK_LIMITS;
    expect(retentionDaysOk(1, limits)).toBe(true);
    expect(retentionDaysOk(730, limits)).toBe(true);
    expect(retentionDaysOk(0, limits)).toBe(false);
    expect(retentionDaysOk(731, limits)).toBe(false);
    expect(retentionDaysOk(1.5, limits)).toBe(false);
    expect(retentionDaysOk(Number.NaN, limits)).toBe(false);
  });

  it('reads the statements a statement refusal carries, and nothing from any other body', () => {
    expect(statementsFromRefusal({ error: 'consent_statement_required', statements: { en: 'new en', it: 'new it', xx: '' } })).toEqual({ en: 'new en', it: 'new it' });
    expect(statementsFromRefusal({ statements: {} })).toBeNull();
    expect(statementsFromRefusal({ statements: ['a'] })).toBeNull();
    expect(statementsFromRefusal({ message: 'no' })).toBeNull();
    expect(statementsFromRefusal(null)).toBeNull();
  });

  it('reads a 404 on the list as "the library is off", and nothing else', () => {
    expect(isLibraryOff({ status: 404 })).toBe(true);
    expect(isLibraryOff({ status: 500 })).toBe(false);
    expect(isLibraryOff(undefined)).toBe(false);
  });
});
