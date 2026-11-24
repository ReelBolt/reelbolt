import { describe, expect, it } from 'vitest';
import { CAMERA_PROFILE_IDS } from '../../color/camera-profiles';
import { parseViewingLut, readViewingLut, viewingLutKey, writeViewingLut } from '../viewing-lut';

const FILE = '11111111-1111-4111-8111-111111111111';

function memory() {
  const map = new Map<string, string>();
  return { getItem: (k: string) => map.get(k) ?? null, setItem: (k: string, v: string) => void map.set(k, v), removeItem: (k: string) => void map.delete(k), map };
}

describe('viewing-lut pref', () => {
  it('round-trips a file and a profile choice per timeline, and clears on null', () => {
    const s = memory();
    writeViewingLut('t1', { kind: 'file', fileId: FILE }, s);
    writeViewingLut('t2', { kind: 'profile', id: CAMERA_PROFILE_IDS[0] }, s);
    expect(readViewingLut('t1', s)).toEqual({ kind: 'file', fileId: FILE });
    expect(readViewingLut('t2', s)).toEqual({ kind: 'profile', id: CAMERA_PROFILE_IDS[0] });
    expect(s.map.has('reelbolt:editor:viewing-lut:t1')).toBe(true);
    writeViewingLut('t1', null, s);
    expect(readViewingLut('t1', s)).toBeNull();
    expect(s.map.has(viewingLutKey('t1'))).toBe(false);
  });
  it('reads anything malformed as off', () => {
    const s = memory();
    s.setItem(viewingLutKey('t'), '{not json');
    expect(readViewingLut('t', s)).toBeNull();
    expect(parseViewingLut({ kind: 'profile', id: 'bogus' })).toBeNull();
    expect(parseViewingLut({ kind: 'file' })).toBeNull();
    expect(parseViewingLut(null)).toBeNull();
    expect(readViewingLut('t', null)).toBeNull();
  });
  it('ignores a stored file id that is not a project-file GUID (it would go into a URL path)', () => {
    for (const bad of ['../../etc/passwd', 'f-1', 'not-a-guid', '11111111111141118111111111111111', '11111111-1111-4111-8111-11111111111z']) {
      expect(parseViewingLut({ kind: 'file', fileId: bad })).toBeNull();
    }
    expect(parseViewingLut({ kind: 'file', fileId: FILE })).toEqual({ kind: 'file', fileId: FILE });
    // A GUID has no reserved characters, so encoding it changes nothing.
    expect(parseViewingLut({ kind: 'file', fileId: FILE })?.kind === 'file' && (parseViewingLut({ kind: 'file', fileId: FILE }) as { fileId: string }).fileId).toBe(encodeURIComponent(FILE));
  });
});
