// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { doc, media, track } from '../../__tests__/fixtures';
import type { MediaDerivativeKind, MediaDerivativeRow } from '../../media/types';
import { buildSessionHooks, stabilizedPictureUrl, wantDocumentStabilized } from '@/components/editor/viewer/session-wiring';
import { getExternalJobMarks, resetJobMarks } from '../../background/job-marks';
import { StabilizeJobs, stabilizeMarkId } from '../../background/stabilize-job';
import { parseMediaDerivativeProgress } from '@/lib/hooks/use-media-derivatives';
import { ProxyUrlResolver, type ProxyResolverDeps } from '../proxy-urls';
import { pickStabilizedRow, STABILIZE_READY_REASON, stabilizePreparingReason, stabilizedSourcesIn } from '../stabilize-proxy';
import { mediaRefOf } from '../scheduler';

const REF = 'file:00000000-0000-4000-8000-000000000001';
const row = (over: Partial<MediaDerivativeRow> & { kind: MediaDerivativeKind }): MediaDerivativeRow => ({ id: 'r', mimeType: 'video/mp4', sizeBytes: 1, durationSec: 5, width: 960, height: 540, contentUrl: '/x', ...over }) as unknown as MediaDerivativeRow;
const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

function resolverWith(rowsFor: () => MediaDerivativeRow[]) {
  const requestStabilized = vi.fn();
  const deps: ProxyResolverDeps = {
    fetchDerivatives: () => Promise.resolve(rowsFor()),
    proxyUrl: (r) => `/u/${r.id}`,
    originalUrl: () => '/original',
    requestStabilized,
  };
  return { resolver: new ProxyUrlResolver(deps), requestStabilized };
}

const stabilizedDoc = (on = true) => doc([track('v1', 'video', [media('c1', 0, 60, 0, { stabilize: on ? true : undefined }), media('c2', 60, 120, 0, { stabilize: on ? true : undefined })])]);

beforeEach(() => resetJobMarks());

describe('picking the row', () => {
  it('takes the largest stabilizedProxy row and ignores other kinds', () => {
    const small = row({ id: 's', kind: 'stabilizedProxy', width: 480, height: 270 });
    const big = row({ id: 'b', kind: 'stabilizedProxy' });
    expect(pickStabilizedRow([small, big, row({ id: 'p', kind: 'proxy', width: 1920, height: 1080 })])?.id).toBe('b');
    expect(pickStabilizedRow([row({ id: 'p', kind: 'proxy' })])).toBeNull();
    expect(pickStabilizedRow(null)).toBeNull();
  });
  it('finds the stabilised sources of a document with their clips', () => {
    expect([...stabilizedSourcesIn(stabilizedDoc()).entries()]).toEqual([[REF, ['c1', 'c2']]]);
    expect(stabilizedSourcesIn(stabilizedDoc(false)).size).toBe(0);
  });
  it('the two parity sentences', () => {
    expect(stabilizePreparingReason(41.6)).toBe('Stabilised preview is being prepared (42%).');
    expect(stabilizePreparingReason(null)).toBe('Stabilised preview is being prepared.');
    expect(STABILIZE_READY_REASON).toContain('first and last second');
  });
});

describe('request once', () => {
  it('asks once per source per session, only after the lookup answered, never twice', async () => {
    const { resolver, requestStabilized } = resolverWith(() => [row({ id: 'p', kind: 'proxy' })]);
    wantDocumentStabilized(resolver, stabilizedDoc());
    expect(requestStabilized).not.toHaveBeenCalled(); // the lookup has not answered
    resolver.request([REF]);
    await flush();
    expect(requestStabilized).toHaveBeenCalledTimes(1);
    expect(requestStabilized).toHaveBeenCalledWith(REF);
    wantDocumentStabilized(resolver, stabilizedDoc());
    resolver.request([REF], { refresh: true });
    await flush();
    expect(requestStabilized).toHaveBeenCalledTimes(1);
  });
  it('does not ask for a document without stabilisation, or when the proxy is already listed', async () => {
    const a = resolverWith(() => []);
    wantDocumentStabilized(a.resolver, stabilizedDoc(false));
    a.resolver.request([REF]);
    await flush();
    expect(a.requestStabilized).not.toHaveBeenCalled();
    const b = resolverWith(() => [row({ id: 'p', kind: 'proxy' }), row({ id: 'st', kind: 'stabilizedProxy' })]);
    wantDocumentStabilized(b.resolver, stabilizedDoc());
    b.resolver.request([REF]);
    await flush();
    expect(b.requestStabilized).not.toHaveBeenCalled();
    expect(b.resolver.stabilizedUrlFor(REF)).toBe('/u/st');
  });
  it('a refresh that finds the row notifies and a retry asks again', async () => {
    let rows: MediaDerivativeRow[] = [row({ id: 'p', kind: 'proxy' })];
    const { resolver, requestStabilized } = resolverWith(() => rows);
    const heard = vi.fn();
    resolver.subscribe(heard);
    wantDocumentStabilized(resolver, stabilizedDoc());
    resolver.request([REF]);
    await flush();
    expect(resolver.stabilizedUrlFor(REF)).toBeNull();
    resolver.retryStabilized(REF);
    expect(requestStabilized).toHaveBeenCalledTimes(2);
    await flush();
    rows = [...rows, row({ id: 'st', kind: 'stabilizedProxy' })];
    resolver.request([REF], { refresh: true });
    await flush();
    expect(resolver.stabilizedUrlFor(REF)).toBe('/u/st');
    expect(heard).toHaveBeenCalled();
  });
});

describe('the picture swap', () => {
  const clipOf = (d: ReturnType<typeof stabilizedDoc>) => d.tracks[0]!.items[0] as never;
  it('a stabilised clip decodes the stabilised proxy once listed, its sound keeps the plain proxy', async () => {
    const { resolver } = resolverWith(() => [row({ id: 'p', kind: 'proxy' }), row({ id: 'st', kind: 'stabilizedProxy' })]);
    resolver.request([REF]);
    await flush();
    const hooks = buildSessionHooks(resolver, () => null);
    const clip = clipOf(stabilizedDoc());
    expect(mediaRefOf((clip as { assetRef: never }).assetRef)).toBe(REF);
    expect(hooks.pictureUrlFor(clip)).toBe('/u/st');
    expect(hooks.proxyUrlFor(clip)).toBe('/u/p');
  });
  it('until it is ready, or with stabilisation off, the plain proxy plays', async () => {
    const { resolver } = resolverWith(() => [row({ id: 'p', kind: 'proxy' })]);
    resolver.request([REF]);
    await flush();
    const hooks = buildSessionHooks(resolver, () => null);
    expect(hooks.pictureUrlFor(clipOf(stabilizedDoc()))).toBe('/u/p');
    expect(stabilizedPictureUrl(resolver, clipOf(stabilizedDoc(false)))).toBeNull();
  });
  it('the stabilised picture carries no camera conversion', async () => {
    const { resolver } = resolverWith(() => [row({ id: 'p', kind: 'proxy' }), row({ id: 'st', kind: 'stabilizedProxy' })]);
    resolver.request([REF]);
    await flush();
    const hooks = buildSessionHooks(resolver, () => null);
    const clip = { ...(clipOf(stabilizedDoc()) as object), inputProfile: 'appleLog' } as never;
    const stage = hooks.inputStageFor(clip);
    expect(stage === null || stage.baked === false).toBe(true);
  });
});

describe('progress events to job marks', () => {
  const deps = { label: 'Preparing', failedMessage: 'Could not be made', retry: vi.fn() };
  const wanted = new Map([[REF, ['c1', 'c2']]]);
  const ev = (stage: string, percentComplete: number | null = null) => ({ mediaRef: REF, stage, percentComplete });

  it('the stream parser accepts the Stabilize stage with the source file id', () => {
    const raw = JSON.stringify({ type: 'media.derivative.progress', projectId: 'p1', data: { projectId: 'p1', sourceProjectFileId: '00000000-0000-4000-8000-000000000001', stage: 'Stabilize', percentComplete: 40 } });
    expect(parseMediaDerivativeProgress(raw, 'p1')).toEqual({ mediaRef: REF, stage: 'Stabilize', percentComplete: 40 });
  });
  it('a wanted source gets a running mark on every clip using it; progress follows the percent', () => {
    const jobs = new StabilizeJobs(deps);
    jobs.sync(wanted, () => false);
    expect(getExternalJobMarks()).toEqual([expect.objectContaining({ id: stabilizeMarkId(REF), jobKind: 'stabilize', clipIds: ['c1', 'c2'], state: 'running' })]);
    expect(getExternalJobMarks()[0]!.progress).toBeUndefined();
    expect(jobs.apply(ev('Stabilize', 40), () => false)).toBe(true);
    expect(getExternalJobMarks()[0]!.progress).toBeCloseTo(0.4);
  });
  it('a ready proxy clears the mark; a source nobody stabilises any more drops it', () => {
    const jobs = new StabilizeJobs(deps);
    jobs.sync(wanted, () => false);
    jobs.sync(wanted, () => true);
    expect(getExternalJobMarks()).toEqual([]);
    jobs.sync(wanted, () => false);
    jobs.sync(new Map(), () => false);
    expect(getExternalJobMarks()).toEqual([]);
  });
  it('Failed after the Stabilize stage marks the job failed with a plain message; a sync does not undo it', () => {
    const jobs = new StabilizeJobs(deps);
    jobs.sync(wanted, () => false);
    jobs.apply(ev('Stabilize', 10), () => false);
    expect(jobs.apply(ev('Failed'), () => false)).toBe(true);
    expect(getExternalJobMarks()[0]).toMatchObject({ state: 'failed', message: 'Could not be made' });
    jobs.sync(wanted, () => false);
    expect(getExternalJobMarks()[0]!.state).toBe('failed');
  });
  it('a Failed of the plain proxy run, before any Stabilize stage, is not ours', () => {
    const jobs = new StabilizeJobs(deps);
    jobs.sync(wanted, () => false);
    expect(jobs.apply(ev('Failed'), () => false)).toBe(false);
    expect(getExternalJobMarks()[0]!.state).toBe('running');
    // ... unless the plain proxy is already there: then the failure can only be the stabilisation's.
    expect(jobs.apply(ev('Failed'), () => true)).toBe(true);
    expect(getExternalJobMarks()[0]!.state).toBe('failed');
  });
  it('Retry runs the mark again and asks the service again', () => {
    const retry = vi.fn();
    const jobs = new StabilizeJobs({ ...deps, retry });
    jobs.sync(wanted, () => false);
    jobs.apply(ev('Stabilize', 10), () => false);
    jobs.apply(ev('Failed'), () => false);
    jobs.retry(REF);
    expect(retry).toHaveBeenCalledWith(REF);
    expect(getExternalJobMarks()[0]).toMatchObject({ state: 'running' });
    expect(getExternalJobMarks()[0]!.message).toBeUndefined();
    expect(StabilizeJobs.sourceOf(stabilizeMarkId(REF))).toBe(REF);
    expect(StabilizeJobs.sourceOf('follow:c1')).toBeNull();
  });
  it('events for other sources, or without a source, are ignored', () => {
    const jobs = new StabilizeJobs(deps);
    jobs.sync(wanted, () => false);
    expect(jobs.apply({ mediaRef: 'file:other', stage: 'Stabilize', percentComplete: 5 }, () => false)).toBe(false);
    expect(jobs.apply({ mediaRef: null, stage: 'Stabilize', percentComplete: 5 }, () => false)).toBe(false);
  });
});
