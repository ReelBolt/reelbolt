import { describe, expect, it } from 'vitest';
import { HDR_PREVIEW_REASON, hdrKindOfProbe } from '../hdr';
import { classifyItem, classifyParity, parityAtFrame } from '../../preview/parity';
import { doc, media, track } from '../../__tests__/fixtures';
import type { MediaClip } from '../../types';
import { VIDEO_FILE_ITEM } from '../fixtures';

describe('R10.7 HDR footage in the editor', () => {
  it('reads the hdr kind from a stored probe; an old probe without the field is not HDR', () => {
    expect(hdrKindOfProbe({ hdr: 'hlg' })).toBe('hlg');
    expect(hdrKindOfProbe({ hdr: 'pq' })).toBe('pq');
    expect(hdrKindOfProbe({ hdr: null })).toBeNull();
    expect(hdrKindOfProbe({})).toBeNull();
    expect(hdrKindOfProbe(null)).toBeNull();
    expect(hdrKindOfProbe({ hdr: 'sdr' as never })).toBeNull();
  });

  it('a probe type accepts the optional colour fields and stays valid without them', () => {
    const base = VIDEO_FILE_ITEM.probe!;
    expect(base.hdr ?? null).toBeNull();
    const hdr = { ...base, colorTransfer: 'arib-std-b67', colorPrimaries: 'bt2020', hdr: 'hlg' as const };
    expect(hdrKindOfProbe(hdr)).toBe('hlg');
  });

  it('marks an HDR clip approximate with the plain reason, and leaves an SDR clip alone', () => {
    const clip = media('c1', 0, 30);
    const d = doc([track('v1', 'video', [clip])]);
    expect(classifyItem(clip, d).parity).toBe('faithful');
    const hdr = classifyItem(clip, d, { hdrKindFor: () => 'hlg' });
    expect(hdr).toMatchObject({ parity: 'approximate', reason: HDR_PREVIEW_REASON });
    expect(HDR_PREVIEW_REASON).toBe('Preview shows this HDR clip approximately; the delivered file is converted to standard colours before grading.');
    expect(classifyItem(clip, d, { hdrKindFor: () => null }).parity).toBe('faithful');
    expect(classifyParity(d, { hdrKindFor: () => 'pq' }).approximateCount).toBe(1);
    expect(parityAtFrame(d, 5, { hdrKindFor: () => 'pq' }).worst).toBe('approximate');
  });

  it('never softens a render-only feature: a stabilised HDR clip stays render-required', () => {
    const keyed = { ...(media('c1', 0, 30) as MediaClip), stabilize: true } as MediaClip;
    const d = doc([track('v1', 'video', [keyed])]);
    expect(classifyItem(keyed, d, { hdrKindFor: () => 'hlg' }).parity).toBe('renderRequired');
  });
});
