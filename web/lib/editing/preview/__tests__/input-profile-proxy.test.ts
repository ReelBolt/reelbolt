/** @vitest-environment node */
import { describe, expect, it, vi } from 'vitest';
import { inputProfilesIn, inputStageOf, ProxyUrlResolver } from '../proxy-urls';
import type { MediaDerivativeRow } from '../../media/types';

const row = (id: string, cameraProfile?: string): MediaDerivativeRow => ({ id, kind: 'proxy', mimeType: 'video/mp4', sizeBytes: 1, durationSec: 1, width: 640, height: 360, contentUrl: '', cameraProfile });
const tick = () => new Promise((r) => setTimeout(r, 0));

describe('converted proxies in the resolver', () => {
  it('requests a missing converted proxy once per (source, profile) and plays the plain proxy meanwhile', async () => {
    const requestConverted = vi.fn();
    const resolver = new ProxyUrlResolver({ fetchDerivatives: async () => [row('plain')], proxyUrl: (r) => `/p/${r.id}`, originalUrl: () => null, requestConverted });
    resolver.wantProfiles([{ mediaRef: 'file:a', cameraProfile: 'sony-slog3' }]);
    expect(requestConverted).not.toHaveBeenCalled();
    resolver.request(['file:a']);
    await tick();
    resolver.wantProfiles([{ mediaRef: 'file:a', cameraProfile: 'sony-slog3' }]);
    resolver.request(['file:a'], { refresh: true });
    await tick();
    expect(requestConverted).toHaveBeenCalledTimes(1);
    expect(requestConverted).toHaveBeenCalledWith('file:a', 'sony-slog3');
    expect(resolver.urlFor('file:a', 'sony-slog3')).toBe('/p/plain');
    expect(resolver.bakedFor('file:a', 'sony-slog3')).toBe(false);
  });

  it('plays the converted row once it is ready and reports it baked', async () => {
    const requestConverted = vi.fn();
    const resolver = new ProxyUrlResolver({ fetchDerivatives: async () => [row('plain'), row('conv', 'sony-slog3')], proxyUrl: (r) => `/p/${r.id}`, originalUrl: () => null, requestConverted });
    const heard = vi.fn();
    resolver.subscribe(heard);
    resolver.request(['file:a']);
    await tick();
    resolver.wantProfiles([{ mediaRef: 'file:a', cameraProfile: 'sony-slog3' }]);
    expect(requestConverted).not.toHaveBeenCalled();
    expect(resolver.urlFor('file:a', 'sony-slog3')).toBe('/p/conv');
    expect(resolver.urlFor('file:a')).toBe('/p/plain');
    expect(resolver.bakedFor('file:a', 'sony-slog3')).toBe(true);
    expect(heard).toHaveBeenCalled();
  });

  it('inputStageOf is null without a profile or for HDR, and carries the baked flag', () => {
    const resolver = { bakedFor: (_r: string | null, p: string | null | undefined) => p === 'x' };
    expect(inputStageOf({}, resolver, false)).toBeNull();
    expect(inputStageOf({ inputProfile: 'x', assetRef: { kind: 'projectFile', projectFileId: 'a' } as never }, resolver, true)).toBeNull();
    expect(inputStageOf({ inputProfile: 'x', assetRef: { kind: 'projectFile', projectFileId: 'a' } as never }, resolver, false)).toEqual({ profile: 'x', baked: true });
  });

  it('inputProfilesIn lists each (source, profile) once', () => {
    expect(inputProfilesIn({ tracks: [] } as never)).toEqual([]);
  });
});
