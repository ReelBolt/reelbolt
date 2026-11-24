import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../../../api/client';
import { requestEditProposal } from '../api';

const json = (status: number, body: unknown) => ({ ok: status < 400, status, statusText: 'x', headers: new Headers(), json: async () => body }) as unknown as Response;
const RUNNING = { jobId: 'j1', state: 'running' };
const PROPOSAL = { ops: [] };
let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  vi.useFakeTimers();
  fetchMock = vi.fn();
  vi.stubGlobal('fetch', fetchMock);
});
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

const run = () => {
  const p = requestEditProposal('p1', 't1', { agent: 'recut' });
  const settled = p.then((v) => ({ v }), (e: unknown) => ({ e }));
  return settled;
};

describe('requestEditProposal', () => {
  it('polls a running job until the proposal arrives', async () => {
    fetchMock.mockResolvedValueOnce(json(202, RUNNING)).mockResolvedValueOnce(json(202, RUNNING)).mockResolvedValueOnce(json(200, PROPOSAL));
    const r = run();
    await vi.advanceTimersByTimeAsync(4000);
    expect(await r).toEqual({ v: PROPOSAL });
    expect(String(fetchMock.mock.calls[0][0])).toContain('/proposals?background=true');
    expect(String(fetchMock.mock.calls[2][0])).toContain('/proposals/jobs/j1');
  });

  it('rejects with the code of a failed job', async () => {
    fetchMock.mockResolvedValueOnce(json(202, RUNNING)).mockResolvedValueOnce(json(424, { code: 'PROPOSAL_FAILED', message: 'no' }));
    const r = run();
    await vi.advanceTimersByTimeAsync(2000);
    const out = (await r) as { e: ApiError };
    expect(out.e).toBeInstanceOf(ApiError);
    expect(out.e.status).toBe(424);
    expect(out.e.code).toBe('PROPOSAL_FAILED');
  });

  it('resolves an older server answer without polling', async () => {
    fetchMock.mockResolvedValueOnce(json(200, PROPOSAL));
    expect(await run()).toEqual({ v: PROPOSAL });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('retries one transient poll failure', async () => {
    fetchMock.mockResolvedValueOnce(json(202, RUNNING)).mockRejectedValueOnce(new TypeError('net')).mockResolvedValueOnce(json(503, {})).mockResolvedValueOnce(json(200, PROPOSAL));
    const r = run();
    await vi.advanceTimersByTimeAsync(6000);
    expect(await r).toEqual({ v: PROPOSAL });
  });

  it('rejects after three consecutive poll failures', async () => {
    fetchMock.mockResolvedValueOnce(json(202, RUNNING)).mockRejectedValue(new TypeError('net'));
    const r = run();
    await vi.advanceTimersByTimeAsync(10000);
    expect(((await r) as { e: Error }).e).toBeInstanceOf(TypeError);
  });

  it('rejects when the job is gone (404)', async () => {
    fetchMock.mockResolvedValueOnce(json(202, RUNNING)).mockResolvedValueOnce(json(404, {}));
    const r = run();
    await vi.advanceTimersByTimeAsync(2000);
    expect(((await r) as { e: ApiError }).e.status).toBe(404);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('gives up after 6 minutes with PROPOSAL_TIMEOUT', async () => {
    fetchMock.mockResolvedValueOnce(json(202, RUNNING)).mockResolvedValue(json(202, RUNNING));
    const r = run();
    await vi.advanceTimersByTimeAsync(6 * 60 * 1000 + 4000);
    const out = (await r) as { e: ApiError };
    expect(out.e.code).toBe('PROPOSAL_TIMEOUT');
    expect(out.e.status).toBe(424);
  });

  it('stops polling when aborted', async () => {
    fetchMock.mockResolvedValueOnce(json(202, RUNNING)).mockResolvedValue(json(202, RUNNING));
    const ac = new AbortController();
    const settled = requestEditProposal('p1', 't1', { agent: 'recut' }, ac.signal).then(() => 'ok', (e: unknown) => (e as Error).name);
    await vi.advanceTimersByTimeAsync(2000);
    ac.abort();
    await vi.advanceTimersByTimeAsync(2000);
    expect(await settled).toBe('AbortError');
  });
});
