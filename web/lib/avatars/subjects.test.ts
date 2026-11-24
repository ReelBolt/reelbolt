import { describe, expect, it } from 'vitest';
import {
  MAX_SUBJECTS, canAddSubject, chosenSubjects, hasUnusableChosen, sendableSubjectIds, subjectBlock, subjectCounts, subjectGroups,
  subjectLimit, subjectNames, subjectProblems, subjectUnusable, talkingSubject, toggleSubject, usableSubjects,
} from './subjects';
import { testAvatar, testObject } from '@/test/avatars';

const anna = testAvatar();
const bea = testAvatar({ id: 'bea', name: 'Bea' });
const bottle = testObject();
const car = testObject({ id: 'car', name: 'Car' });
const shop = testObject({ id: 'shop', name: 'Shop' });
const tower = testObject({ id: 'tower', name: 'Tower' });
const revoked = testAvatar({ id: 'old', name: 'Old', selectable: false, unselectableReason: 'consent_revoked' });
const videoOnly = testAvatar({ id: 'vid', name: 'Vid', coversPictures: false });
const all = [bottle, anna, car, bea, shop, tower, revoked, videoOnly];

describe('subject rules: at most three, at most one person', () => {
  it('allows three objects, or one person with two objects, and nothing beyond', () => {
    expect(subjectProblems(['bottle', 'car', 'shop'], all)).toEqual([]);
    expect(subjectProblems(['anna', 'bottle', 'car'], all)).toEqual([]);
    expect(subjectProblems(['bottle', 'car', 'shop', 'tower'], all)).toEqual(['tooMany']);
    expect(subjectProblems(['anna', 'bea'], all)).toEqual(['secondPerson']);
    expect(subjectProblems(['anna', 'bea', 'bottle', 'car'], all)).toEqual(['tooMany', 'secondPerson']);
    expect(subjectProblems([], all)).toEqual([]);
  });

  it('says why one more cannot be added', () => {
    expect(canAddSubject(bottle, [], all)).toBe(true);
    expect(canAddSubject(bea, ['bottle'], all)).toBe(true);
    expect(subjectBlock(bea, ['anna'], all)).toBe('secondPerson');
    expect(subjectBlock(tower, ['bottle', 'car', 'shop'], all)).toBe('tooMany');
    expect(subjectBlock(bea, ['anna', 'bottle', 'car'], all)).toBe('tooMany');
    expect(subjectBlock(revoked, [], all)).toBe('unusable');
    expect(canAddSubject(revoked, [], all)).toBe(false);
  });

  it('never blocks one that is already chosen, so it can be unticked', () => {
    expect(subjectBlock(anna, ['anna', 'bottle', 'car'], all)).toBeNull();
    expect(subjectBlock(revoked, ['old'], all)).toBeNull();
    expect(toggleSubject(['anna', 'bottle'], anna, all)).toEqual(['bottle']);
  });

  it('adds in the order chosen and leaves the list alone when the rules say no', () => {
    expect(toggleSubject([], bottle, all)).toEqual(['bottle']);
    expect(toggleSubject(['bottle'], anna, all)).toEqual(['bottle', 'anna']);
    expect(toggleSubject(['anna'], bea, all)).toEqual(['anna']);
    expect(toggleSubject(['bottle', 'car', 'shop'], tower, all)).toEqual(['bottle', 'car', 'shop']);
  });

  it('takes a smaller limit from the caller and never a larger one', () => {
    expect(subjectLimit(undefined)).toBe(MAX_SUBJECTS);
    expect(subjectLimit(null)).toBe(3);
    expect(subjectLimit(2)).toBe(2);
    expect(subjectLimit(9)).toBe(3);
    expect(subjectLimit(0)).toBe(1);
    expect(subjectLimit(Number.NaN)).toBe(3);
    expect(subjectBlock(car, ['bottle'], all, { max: 1 })).toBe('tooMany');
    expect(subjectProblems(['bottle', 'car'], all, { max: 1 })).toEqual(['tooMany']);
  });
});

describe('subjects for pictures', () => {
  it('leaves out a person whose consent does not cover pictures, only when asked for pictures', () => {
    expect(subjectUnusable(videoOnly)).toBeNull();
    expect(subjectUnusable(videoOnly, { forPictures: true })).toBe('noPictureConsent');
    expect(subjectBlock(videoOnly, [], all, { forPictures: true })).toBe('noPictureConsent');
    expect(usableSubjects(all).map((a) => a.id)).toContain('vid');
    expect(usableSubjects(all, { forPictures: true }).map((a) => a.id)).not.toContain('vid');
  });

  it('says nothing about pictures for an object, or when an older server does not say', () => {
    expect(subjectUnusable(testObject({ coversPictures: false }), { forPictures: true })).toBeNull();
    expect(subjectUnusable(testAvatar({ coversPictures: undefined }), { forPictures: true })).toBeNull();
    expect(subjectUnusable(testAvatar({ subjectKind: undefined, coversPictures: undefined }), { forPictures: true })).toBeNull();
  });
});

describe('what is listed and what is sent', () => {
  it('lists people first, then objects, usable ones before the rest', () => {
    const groups = subjectGroups(all);
    expect(groups.map((g) => g.kind)).toEqual(['Person', 'Object']);
    expect(groups[0].avatars.map((a) => a.id)).toEqual(['anna', 'bea', 'vid', 'old']);
    expect(usableSubjects(all).map((a) => a.id)).toEqual(['anna', 'bea', 'vid', 'bottle', 'car', 'shop', 'tower']);
    expect(subjectGroups([bottle])).toHaveLength(1);
    expect(subjectGroups(undefined)).toEqual([]);
    expect(subjectCounts(all)).toEqual({ Person: 4, Object: 4 });
  });

  it('sends only what the project still has and can use', () => {
    expect(chosenSubjects(['bottle', 'gone', 'anna'], all).map((a) => a.id)).toEqual(['bottle', 'anna']);
    expect(sendableSubjectIds(['bottle', 'gone', 'old', 'anna', 'bottle'], all)).toEqual(['bottle', 'anna']);
    expect(hasUnusableChosen(['bottle', 'anna'], all)).toBe(false);
    expect(hasUnusableChosen(['bottle', 'old'], all)).toBe(true);
    expect(hasUnusableChosen(['gone'], all)).toBe(true);
    expect(hasUnusableChosen(['vid'], all)).toBe(false);
    expect(hasUnusableChosen(['vid'], all, { forPictures: true })).toBe(true);
    expect(subjectNames(['bottle', 'anna'], all)).toEqual(['Bottle', 'Anna']);
  });

  it('lets a speech file drive exactly one person and nothing else', () => {
    expect(talkingSubject(['anna'], all)?.id).toBe('anna');
    expect(talkingSubject(['anna', 'bottle'], all)).toBeNull();
    expect(talkingSubject(['bottle'], all)).toBeNull();
    expect(talkingSubject([], all)).toBeNull();
  });
});
