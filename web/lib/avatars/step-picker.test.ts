import { describe, expect, it } from 'vitest';
import {
  audioFiles, avatarBlock, effectiveRoles, keepsPicture, pickerGroups, pickerName, pickerOrder, rolesPresent, rolesToStore, speechDrive,
  speechSource, voiceoverStepOrders,
} from './step-picker';
import type { ProjectAvatar } from '../types/avatars';
import type { ProjectFile } from '../types/project';

const av = (id: string, name: string, selectable: boolean, roles: string[] = ['Face']): ProjectAvatar => ({
  id, name, selectable,
  references: roles.map((role, i) => ({ id: `${id}-${i}`, role, kind: 'Image', state: i === 9 ? 'missing' : 'ok' })),
} as unknown as ProjectAvatar);

describe('avatar step picker helpers', () => {
  it('lists the roles present in the server order, ignoring files that are not ok', () => {
    const a = av('a', 'A', true, ['Speech', 'Face', 'HalfBody']);
    a.references.push({ ...a.references[0], role: 'FullBody', state: 'missing' } as ProjectAvatar['references'][number]);
    expect(rolesPresent(a)).toEqual(['Face', 'HalfBody', 'Speech']);
    expect(rolesPresent(undefined)).toEqual([]);
  });

  it('stores null for every role, a subset otherwise, and requires a picture', () => {
    const present = ['Face', 'HalfBody', 'Speech'] as const;
    expect(rolesToStore(['Speech', 'HalfBody', 'Face'], present)).toBeNull();
    expect(rolesToStore(['Speech', 'Face'], present)).toEqual(['Face', 'Speech']);
    expect(effectiveRoles(null, present)).toEqual(['Face', 'HalfBody', 'Speech']);
    expect(effectiveRoles([], present)).toEqual([]);
    expect(keepsPicture(['Speech'])).toBe(false);
    expect(keepsPicture(['FullBody', 'Speech'])).toBe(true);
  });

  it('blocks an avatar on a plan-sourced step and next to a frame', () => {
    expect(avatarBlock({ planSource: 'Inline' })).toBeNull();
    expect(avatarBlock({ planSource: 'StepRef' })).toBe('plan');
    expect(avatarBlock({ planSource: 'ProjectFile' as never })).toBe('plan');
    expect(avatarBlock({ planSource: 'Inline', lastFrame: { from: 'Previous' } })).toBe('frames');
    expect(avatarBlock({ planSource: 'Inline', firstFrameProjectFileId: 'f' })).toBe('frames');
  });

  it('knows which models make the person say a speech track', () => {
    expect(speechDrive('minimax/h3-max/lip-sync/image-to-video')).toBe('yes');
    expect(speechDrive('fal-ai/kling-video/ai-avatar/v2/standard')).toBe('yes');
    expect(speechDrive('MiniMax-H3')).toBe('no');
    expect(speechDrive('MiniMax-H3-Max')).toBe('no');
    expect(speechDrive('wan2.2-ti2v-5b-t2v')).toBe('no');
    expect(speechDrive('something-else')).toBe('unknown');
  });

  it('reads the speech source, the earlier Voiceover steps and the audio files', () => {
    expect(speechSource({})).toBe('none');
    expect(speechSource({ avatarAudio: { from: 'Step', stepOrder: 2 } })).toBe('step');
    expect(speechSource({ avatarAudioProjectFileId: 'f' })).toBe('file');
    expect(voiceoverStepOrders([{ stepType: 'Voiceover' }, { stepType: 'Agent' }, { stepType: 'Voiceover' }, { stepType: 'Voiceover' }], 3)).toEqual([1, 3]);
    const files = [
      { id: '2', originalFileName: 'b.mp3', mimeType: 'audio/mpeg' },
      { id: '1', originalFileName: 'a.wav', mimeType: 'audio/wav' },
      { id: '3', originalFileName: 'c.png', mimeType: 'image/png' },
    ] as ProjectFile[];
    expect(audioFiles(files).map((f) => f.id)).toEqual(['1', '2']);
  });

  it('puts the usable avatars first', () => {
    expect(pickerOrder([av('1', 'Zed', true), av('2', 'Amy', false), av('3', 'Bob', true)]).map((a) => a.name)).toEqual(['Bob', 'Zed', 'Amy']);
  });

  it('groups the picker by kind, people first, and leaves out a kind with nobody', () => {
    const shoe = { ...av('4', 'Shoe', true), subjectKind: 'Object' } as ProjectAvatar;
    const car = { ...av('5', 'Car', false), subjectKind: 'Object' } as ProjectAvatar;
    const groups = pickerGroups([shoe, av('1', 'Zed', true), car, av('2', 'Amy', false)]);
    expect(groups.map((g) => [g.kind, g.avatars.map((a) => a.name)])).toEqual([['Person', ['Zed', 'Amy']], ['Object', ['Shoe', 'Car']]]);
    expect(pickerGroups([av('1', 'Zed', true)]).map((g) => g.kind)).toEqual(['Person']);
    expect(pickerGroups([shoe]).map((g) => g.kind)).toEqual(['Object']);
    expect(pickerGroups(undefined)).toEqual([]);
  });

  it('lists a variant right after its original, by label, and names it "Car · Red"', () => {
    const obj = (id: string, name: string, selectable: boolean, extra: Partial<ProjectAvatar> = {}) =>
      ({ ...av(id, name, selectable), subjectKind: 'Object', ...extra } as ProjectAvatar);
    const car = obj('car', 'Car', false);
    const shoe = obj('shoe', 'Shoe', true);
    const red = obj('red', 'Car · Red', true, { variantOfAvatarId: 'car', variantLabel: 'Red' });
    const blue = obj('blue', 'Car · Blue', false, { variantOfAvatarId: 'car', variantLabel: 'Blue' });
    const lost = obj('lost', 'Bottle · Green', true, { variantOfAvatarId: 'gone', variantLabel: 'Green' });
    const all = [red, lost, car, blue, shoe, av('p', 'Anna', true)];
    // Usable originals first; a variant follows its original whatever its own state, and keeps that state.
    expect(pickerOrder([red, lost, car, blue, shoe]).map((a) => a.id)).toEqual(['lost', 'shoe', 'car', 'blue', 'red']);
    const groups = pickerGroups(all);
    expect(groups.map((g) => [g.kind, g.avatars.map((a) => pickerName(a, all))])).toEqual([
      ['Person', ['Anna']],
      ['Object', ['Bottle · Green', 'Shoe', 'Car', 'Car · Blue', 'Car · Red']],
    ]);
    expect(groups[1].avatars.map((a) => a.selectable)).toEqual([true, true, false, false, true]);
  });

  it('knows an object\'s roles: its pictures count as pictures, in the server order', () => {
    const shoe = av('s', 'Shoe', true, ['Motion', 'Detail', 'Front', 'Angle']);
    expect(rolesPresent(shoe)).toEqual(['Motion', 'Front', 'Angle', 'Detail']);
    expect(keepsPicture(['Motion'])).toBe(false);
    expect(keepsPicture(['Motion', 'Detail'])).toBe(true);
    expect(rolesToStore(['Front', 'Motion'], ['Motion', 'Front', 'Angle', 'Detail'])).toEqual(['Motion', 'Front']);
    expect(rolesToStore(['Detail', 'Angle', 'Front', 'Motion'], ['Motion', 'Front', 'Angle', 'Detail'])).toBeNull();
  });
});
