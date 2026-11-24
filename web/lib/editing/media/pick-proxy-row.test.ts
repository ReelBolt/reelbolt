import { describe, expect, it } from 'vitest';
import { pickProxyRow, type MediaDerivativeRow } from './types';

const row = (id: string, kind: MediaDerivativeRow['kind'], width: number | null, height: number | null): MediaDerivativeRow => ({
  id, kind, width, height, mimeType: 'video/mp4', sizeBytes: 1, durationSec: 1, contentUrl: `/c/${id}`,
});

describe('pickProxyRow', () => {
  it('returns null without a proxy row', () => {
    expect(pickProxyRow(null)).toBeNull();
    expect(pickProxyRow([row('a', 'filmstrip', 160, 90)])).toBeNull();
  });

  it('prefers the rebuilt short-side proxy over the old 540-tall portrait proxy listed first', () => {
    const old = row('old', 'proxy', 304, 540);
    const rebuilt = row('new', 'proxy', 540, 960);
    expect(pickProxyRow([old, row('f', 'filmstrip', 160, 284), rebuilt])?.id).toBe('new');
  });

  it('keeps the later row on a tie and tolerates unknown sizes', () => {
    expect(pickProxyRow([row('a', 'proxy', 960, 540), row('b', 'proxy', 960, 540)])?.id).toBe('b');
    expect(pickProxyRow([row('a', 'proxy', null, null)])?.id).toBe('a');
  });
});
