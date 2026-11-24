import { describe, expect, it } from 'vitest';
import { isZipFile, isZipName, zipsOverLimit } from './zip-warning';

const MIB = 1024 * 1024;
const f = (name: string, mb: number) => ({ name, size: mb * MIB });

describe('zipsOverLimit', () => {
  it('flags only zips above the served limit', () => {
    const files = [f('code.zip', 173), f('small.ZIP', 10), f('exact.zip', 64), f('clip.mp4', 900), f('big.zip.txt', 200)];
    expect(zipsOverLimit(files, 64).map((x) => x.name)).toEqual(['code.zip']);
  });
  it('uses whatever limit the API serves', () => {
    expect(zipsOverLimit([f('a.zip', 20)], 16)).toHaveLength(1);
    expect(zipsOverLimit([f('a.zip', 20)], 32)).toHaveLength(0);
  });
  it('never warns when the limit is unknown', () => {
    expect(zipsOverLimit([f('a.zip', 500)], null)).toEqual([]);
    expect(zipsOverLimit([f('a.zip', 500)], undefined)).toEqual([]);
  });
  it('matches the extension case-insensitively', () => {
    expect(isZipName('A.Zip')).toBe(true);
    expect(isZipName('zip')).toBe(false);
  });
  it('also treats a zip MIME type as a zip, as the server does', () => {
    const typed = (name: string, type: string) => ({ ...f(name, 100), type });
    expect(zipsOverLimit([typed('code', 'application/zip'), typed('b.bin', 'Application/X-Zip-Compressed'), typed('c', 'application/x-zip')], 64)).toHaveLength(3);
    expect(zipsOverLimit([typed('clip.mp4', 'video/mp4'), typed('d', '')], 64)).toEqual([]);
    expect(isZipFile({ name: 'a.ZIP', type: '' })).toBe(true);
    expect(isZipFile({ name: 'a.bin', type: 'application/zip' })).toBe(true);
    expect(isZipFile({ name: 'a.bin' })).toBe(false);
  });
});
