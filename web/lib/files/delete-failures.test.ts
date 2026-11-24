import { describe, expect, it } from 'vitest';
import { ApiError } from '@/lib/api/client';
import { describeDeleteFailures } from './delete-failures';

const QUOTA_SENTENCE =
  'This photo is used by 3 pictures. Keeping their copies needs more storage than your plan has left. Delete some pictures or free up space, then try again.';
const quota = (pictures = 3) =>
  new ApiError(QUOTA_SENTENCE, 409, [], { code: 'PICTURE_COPIES_OVER_QUOTA', message: QUOTA_SENTENCE, pictures });
const rejected = (reason: unknown): PromiseSettledResult<void> => ({ status: 'rejected', reason });
const ok: PromiseSettledResult<void> = { status: 'fulfilled', value: undefined };

describe('describeDeleteFailures', () => {
  it('returns null when nothing failed', () => {
    expect(describeDeleteFailures([ok, ok], 2)).toBeNull();
  });

  it("shows the server's sentence for one picture-copies refusal", () => {
    expect(describeDeleteFailures([ok, rejected(quota())], 2)).toBe(QUOTA_SENTENCE);
  });

  it('says several picture-copies refusals in one plain sentence', () => {
    const msg = describeDeleteFailures([rejected(quota(3)), rejected(quota(1)), rejected(quota(2)), ok], 4)!;
    expect(msg).toContain("3 photos were kept because their pictures' copies need more storage than your plan has left.");
    expect(msg).not.toContain('This photo is used by');
  });

  it('dedupes identical server messages', () => {
    const e = () => new ApiError('This file is being edited.', 409, [], { message: 'This file is being edited.' });
    expect(describeDeleteFailures([rejected(e()), rejected(e())], 2)).toBe('This file is being edited.');
  });

  it('keeps the generic count for failures without a server sentence', () => {
    expect(describeDeleteFailures([rejected(new Error('Failed to fetch')), ok, rejected(new ApiError('Internal Server Error', 500))], 3))
      .toBe('2 of 3 failed.');
  });

  it('combines a server sentence with the count of the other failures', () => {
    const msg = describeDeleteFailures([rejected(quota()), rejected(new ApiError('Bad Gateway', 502)), ok], 3)!;
    expect(msg).toBe(`${QUOTA_SENTENCE} 1 other file could not be deleted.`);
  });

  it('never shows ids or raw JSON', () => {
    const id = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';
    const withId = new ApiError(`File ${id} not found`, 404, [], { message: `File ${id} not found` });
    const json = new ApiError('{"code":"X"}', 409, [], { message: '{"code":"X"}' });
    const msg = describeDeleteFailures([rejected(withId), rejected(json)], 2)!;
    expect(msg).toBe('2 of 2 failed.');
  });

  it('names the file and the avatar when an avatar holds the file', () => {
    const fileId = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';
    const sentence = 'This file is a reference of the avatar "Anna". Remove it from the avatar, or delete the avatar, first.';
    const refusal = new ApiError(sentence, 409, [], {
      code: 'FILE_IN_USE_BY_AVATAR', message: sentence, avatarId: 'a1', avatarName: 'Anna', fileId, avatars: 2,
    });
    const msg = describeDeleteFailures([rejected(refusal), ok], 2, undefined, (id) => (id === fileId ? 'anna.jpg' : null))!;
    expect(msg).toBe('"anna.jpg" is a file of the avatar "Anna" and of 1 other avatar. Remove it from the avatar, or delete the avatar, first.');
  });
});
