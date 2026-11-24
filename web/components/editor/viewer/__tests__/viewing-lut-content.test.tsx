import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { SWRConfig } from 'swr';
import { parseCubeLut } from '@/lib/editing/color/cube-lut';
import { viewingLutKey } from '@/lib/editing/prefs/viewing-lut';
import { useViewingLutSpec } from '../useViewingLutSpec';

/**
 * The owner's prod bug after #998: the Samsung Log-to-Linear 1D .cube (4096 rows, CRLF, values up to ~10,
 * uploaded as application/octet-stream) parsed fine, but GET …/content answered 409 and the viewing LUT said
 * "could not open". These run the real hook over real fetch responses.
 */

const PROJECT = 'p1';
const TIMELINE = 't1';
const FILE = '01cce41b-c7b1-4514-b40d-4b1e91aa47ce';

function samsungShapedCube(): string {
  const rows = ['# Samsung Log to Linear', 'TITLE "Samsung Log to Linear"', 'LUT_1D_SIZE 4096'];
  for (let i = 0; i < 4096; i += 1) {
    const v = (10 ** ((i / 4095) * 4 - 3) - 0.00102).toFixed(7);
    rows.push(`${v} ${v} ${v}`);
  }
  return `${rows.join('\r\n')}\r\n`;
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function stubFetch(content: () => Response, sizeBytes: number) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.endsWith(`/api/v1/projects/${PROJECT}/files`)) {
        return json(200, [{ id: FILE, originalFileName: 'Samsung+Log+to+Linear+1DLUT_v1.0.cube', mimeType: 'application/octet-stream', sizeBytes, uploadedAt: '2026-11-03T08:00:00Z' }]);
      }
      if (url.endsWith(`/api/v1/projects/${PROJECT}/files/${FILE}/content`)) return content();
      return json(404, { message: 'not stubbed' });
    }),
  );
}

const wrapper = ({ children }: { children: ReactNode }) => <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0, shouldRetryOnError: false }}>{children}</SWRConfig>;

describe('viewing LUT over the real content route', () => {
  beforeEach(() => {
    window.localStorage.setItem(viewingLutKey(TIMELINE), JSON.stringify({ kind: 'file', fileId: FILE }));
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    window.localStorage.clear();
  });

  it("parses the owner's file shape as a 1D LUT (CRLF, 4096 rows, values above 1)", () => {
    const lut = parseCubeLut(samsungShapedCube());
    expect(lut.kind).toBe('1d');
    expect(lut.oneD?.size).toBe(4096);
  });

  it('applies the 1D LUT when the content route serves the .cube', async () => {
    const text = samsungShapedCube();
    stubFetch(() => json(200, { id: FILE, originalFileName: 'Samsung+Log+to+Linear+1DLUT_v1.0.cube', originalPath: 'x.cube', mimeType: 'application/octet-stream', content: text, uploadedAt: '2026-11-03T08:00:00Z' }), text.length);
    const { result } = renderHook(() => useViewingLutSpec(PROJECT, TIMELINE), { wrapper });
    await waitFor(() => expect(result.current.spec).not.toBeNull());
    expect(result.current.problem).toBeNull();
  });

  it('a 409 from the content route turns the LUT off as unreadable (the prod symptom)', async () => {
    stubFetch(() => json(409, { message: 'File is not a text-editable asset.' }), 198_000);
    const { result } = renderHook(() => useViewingLutSpec(PROJECT, TIMELINE), { wrapper });
    await waitFor(() => expect(result.current.problem).not.toBeNull());
    const problem = result.current.problem;
    expect(typeof problem === 'string' ? problem : problem?.code).toBe('unreadable');
    expect(result.current.spec).toBeNull();
  });
});
