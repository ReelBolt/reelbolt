import { describe, expect, it } from 'vitest';
import type { MediaPoolItem } from '@/lib/editing/media/types';
import type { UploadEntry } from '@/lib/hooks/use-upload-queue';
import { READINESS_LABEL, tileReadiness, uploadText } from '../media-readiness';

const probe = { durationSec: 4, fpsNum: 30, fpsDen: 1, width: 10, height: 10, videoCodec: 'h264', audioCodec: null, container: 'mp4' } as unknown as MediaPoolItem['probe'];
const video = (p: MediaPoolItem['probe'] = probe): MediaPoolItem => ({ mediaRef: 'file:1', kind: 'video', fileName: 'a.mp4', mimeType: 'video/mp4', sizeBytes: 1, category: 'upload', probe: p, bins: [], filmstripUrl: '' }) as unknown as MediaPoolItem;
const proxy = { id: 'd', kind: 'proxy', mimeType: null, sizeBytes: 1, durationSec: 1, width: 1, height: 1, contentUrl: '/x' } as never;

describe('RM.8 tileReadiness', () => {
  it('a proxy row means Ready', () => {
    expect(tileReadiness(video(), { rows: [proxy], error: null }, undefined)).toBe('ready');
  });
  it('not probed or still reading is Processing preview', () => {
    expect(tileReadiness(video(null), { rows: [], error: null }, undefined)).toBe('processing');
    expect(tileReadiness(video(), { rows: null, error: null }, undefined)).toBe('processing');
    expect(tileReadiness(video(), { rows: [], error: null }, 'Proxy')).toBe('processing');
  });
  it('probed, no proxy, nothing running is the not-ready line that still allows adding', () => {
    expect(tileReadiness(video(), { rows: [], error: null }, undefined)).toBe('notReady');
    expect(tileReadiness(video(), { rows: [], error: null }, 'Failed')).toBe('notReady');
    expect(READINESS_LABEL.notReady).toBe('Preview not ready - you can still add it');
  });
  it('a picture is ready once known', () => {
    expect(tileReadiness({ ...video(), kind: 'image' }, undefined, undefined)).toBe('ready');
  });
});

describe('RM.8 uploadText', () => {
  const e = (status: UploadEntry['status'], progress = 0): UploadEntry => ({ id: '1', name: 'a', destination: '', size: 1, progress, status });
  it('says it in words', () => {
    expect(uploadText(e('uploading', 42))).toBe('Uploading 42%');
    expect(uploadText(e('queued'))).toBe('Waiting to upload');
    expect(uploadText(e('done'))).toBe('Added');
    expect(uploadText(e('error'))).toBe('Did not upload');
  });
});
