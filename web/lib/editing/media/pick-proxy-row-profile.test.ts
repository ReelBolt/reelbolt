import { describe, expect, it } from 'vitest';
import { pickProxyRow, type MediaDerivativeRow } from './types';

const row = (id: string, w: number, cameraProfile?: string | null): MediaDerivativeRow => ({
  id, kind: 'proxy', width: w, height: w / 2, mimeType: 'video/mp4', sizeBytes: 1, durationSec: 1, contentUrl: `/c/${id}`, cameraProfile,
});

describe('pickProxyRow with a wanted camera profile', () => {
  it('returns the converted row for that profile when ready', () => {
    expect(pickProxyRow([row('plain', 960), row('conv', 640, 'sony-slog3')], 'sony-slog3')?.id).toBe('conv');
  });
  it('falls back to the plain row when no converted row exists for it (the caller runs the GL stage)', () => {
    expect(pickProxyRow([row('plain', 960), row('other', 960, 'arri-logc3')], 'sony-slog3')?.id).toBe('plain');
  });
  it('never offers a converted row to a reader that did not ask for it', () => {
    expect(pickProxyRow([row('conv', 960, 'sony-slog3')])).toBeNull();
    expect(pickProxyRow([row('plain', 640), row('conv', 960, 'sony-slog3')])?.id).toBe('plain');
  });
});
