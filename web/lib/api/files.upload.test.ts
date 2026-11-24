/**
 * The browser's direct upload (docs/direct-upload.md, docs/design/local-folder-sync-and-storage.md
 * Part 3) and — the part that matters most — the ONE rule that decides when the multipart ROUTE
 * (`POST …/files`) is used instead. Both the API and object storage are stubbed: what is asserted is
 * the REQUESTS the client makes, because "the bytes left the API's path" is a property of the
 * requests and of nothing else.
 *
 * A ticket is one of two modes. `singlePut` is the original path, unchanged. `multipart` is the new
 * one: part URLs in batches of at most 100, three PUTs in flight at most, an ETag read per part, a
 * failed part retried up to three times, the part list sent to completion, and an abort on failure.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { uploadFile, uploadFileWithProgress } from './files';
import { ApiError } from './client';

const PROJECT = '11111111-1111-1111-1111-111111111111';
const FILE_ID = '22222222-2222-2222-2222-222222222222';
const KEY = `projects/${PROJECT}/userFiles/${FILE_ID}.mp4`;
const PUT_URL = 'https://account.r2.cloudflarestorage.com/reelforge/projects/x?X-Amz-Signature=abc';

/**
 * The server's ceiling, as `DirectUploadLimits.MaxObjectBytes` states it: 5 TiB, S3's own object
 * limit. It replaced the old 20 GB `MaxDirectUploadBytes`, and is the value the ticket's `maxBytes`
 * carries. The plan's storage quota, not this, is the real limit.
 */
const MAX_OBJECT_BYTES = 5_497_558_138_880;

const TICKET = {
  uploadToken: 'token-1',
  key: KEY,
  fileId: FILE_ID,
  url: PUT_URL,
  expiresAtUnix: 1_800_000_000,
  contentType: 'video/mp4',
  maxBytes: MAX_OBJECT_BYTES,
  mode: 'singlePut' as const,
};

const RECORDED = { id: FILE_ID, storageKey: KEY, sizeBytes: 3, mimeType: 'video/mp4' };

/** A multipart ticket: no `url`, and the upload id / part geometry the parts are signed against. */
function multipartTicket(partCount: number, partSizeBytes = 1024): Record<string, unknown> {
  return {
    uploadToken: 'token-mp',
    key: KEY,
    fileId: FILE_ID,
    url: null,
    expiresAtUnix: 1_800_000_000,
    contentType: 'video/mp4',
    maxBytes: MAX_OBJECT_BYTES,
    mode: 'multipart',
    uploadId: 'upload-1',
    partSizeBytes,
    partCount,
    fileName: 'clip.mp4',
    relativePath: null,
    sizeBytes: partCount * partSizeBytes,
  };
}

/** Records every request and lets a test answer each leg separately. */
class FakeXhr {
  static DONE = 4;
  static instances: FakeXhr[] = [];

  method = '';
  url = '';
  headers: Record<string, string> = {};
  body: unknown;
  status = 0;
  responseText = '';
  readyState = 0;
  sentBytes = 0;
  /** What the store answers `getResponseHeader('ETag')` with; '' means the header was absent. */
  responseHeaders: Record<string, string> = {};

  upload: { onprogress: ((event: { lengthComputable: boolean; loaded: number; total: number }) => void) | null } = { onprogress: null };
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  onabort: (() => void) | null = null;
  onreadystatechange: (() => void) | null = null;

  open(method: string, url: string) {
    this.method = method;
    this.url = url;
    FakeXhr.instances.push(this);
  }

  setRequestHeader(name: string, value: string) {
    this.headers[name] = value;
  }

  getResponseHeader(name: string): string | null {
    return this.responseHeaders[name] ?? null;
  }

  /** The blob of a part PUT, so a test can assert each part carried its own byte range. */
  get sent(): unknown {
    return this.body;
  }

  abort() {
    this.onabort?.();
  }

  send(body: unknown) {
    this.body = body;
  }

  /** Bytes actually flushed: a CORS-rejected preflight flushes none. */
  flush(loaded: number, total: number) {
    this.sentBytes = loaded;
    this.upload.onprogress?.({ lengthComputable: true, loaded, total });
  }

  succeed(status = 200, text = JSON.stringify(RECORDED)) {
    this.status = status;
    this.responseText = text;
    this.readyState = FakeXhr.DONE;
    this.onload?.();
    this.onreadystatechange?.();
  }

  /** A part PUT that worked, answering with the ETag the store gives that part. */
  succeedPart(etag = '"etag-1"') {
    this.responseHeaders['ETag'] = etag;
    this.succeed(200, '');
  }

  fail(status = 403, text = 'AccessDenied') {
    this.status = status;
    this.responseText = text;
    this.readyState = FakeXhr.DONE;
    this.onload?.();
    this.onreadystatechange?.();
  }

  networkError() {
    this.status = 0;
    this.onerror?.();
  }
}

/** The legs, each answerable independently. */
function stubApi(options: { presign?: () => Response; complete?: () => Response } = {}) {
  const presign = options.presign ?? (() => json(200, TICKET));
  const complete = options.complete ?? (() => json(201, RECORDED));
  const calls: { url: string; body: unknown }[] = [];
  /** Part numbers asked for, one entry per `upload-parts` call. */
  const partBatches: number[][] = [];

  const fetchMock = vi.fn<typeof fetch>(async (input, init) => {
    const url = String(input);
    calls.push({ url, body: init?.body ? JSON.parse(String(init.body)) : undefined });
    if (url.endsWith('/upload-url')) return presign();
    if (url.endsWith('/upload-complete')) return complete();
    if (url.endsWith('/upload-abort')) return new Response(null, { status: 204 });
    if (url.endsWith('/upload-parts')) {
      const body = JSON.parse(String(init?.body)) as { partNumbers: number[] };
      partBatches.push(body.partNumbers);
      return json(200, body.partNumbers.map((partNumber) => ({ partNumber, url: `${PUT_URL}&part=${partNumber}`, expiresAtUnix: 1_800_000_000 })));
    }
    throw new Error(`unexpected fetch: ${url}`);
  });
  vi.stubGlobal('fetch', fetchMock);
  vi.stubGlobal('XMLHttpRequest', FakeXhr);
  return { fetchMock, calls, partBatches };
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

function videoFile(name = 'clip.mp4', type = 'video/mp4'): File {
  return new File([new Uint8Array([1, 2, 3])], name, { type });
}

/** A file of exactly `size` zero bytes, so a slice's `size` is the part's byte count. */
function sizedFile(size: number, name = 'big.mp4', type = 'video/mp4'): File {
  return new File([new Uint8Array(size)], name, { type });
}

const lastXhr = () => FakeXhr.instances[FakeXhr.instances.length - 1];
/** The PUTs only — every XHR the fake records is a storage PUT in these tests. */
const puts = () => FakeXhr.instances.filter((x) => x.method === 'PUT');

beforeEach(() => {
  FakeXhr.instances = [];
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the direct route', () => {
  it('asks for a URL, PUTs the bytes to storage, then tells the API it is done', async () => {
    const { calls } = stubApi();
    const progress: number[] = [];

    const upload = uploadFileWithProgress(PROJECT, videoFile(), (percent) => progress.push(percent));

    // The PUT is issued only after the presign answers, so let the microtasks run.
    await vi.waitFor(() => expect(FakeXhr.instances).toHaveLength(1));
    const put = lastXhr();
    expect(put.method).toBe('PUT');
    expect(put.url).toBe(PUT_URL);
    expect(put.headers['Content-Type']).toBe('video/mp4');
    put.flush(1, 3);
    put.succeed(200);

    await expect(upload).resolves.toEqual(RECORDED);

    expect(calls.map((c) => c.url)).toEqual([
      `/api/v1/projects/${PROJECT}/files/upload-url`,
      `/api/v1/projects/${PROJECT}/files/upload-complete`,
    ]);
    expect(JSON.parse(String((vi.mocked(fetch).mock.calls[0][1] as RequestInit).body))).toEqual({
      fileName: 'clip.mp4',
      sizeBytes: 3,
      contentType: 'video/mp4',
      relativePath: undefined,
    });
    expect(JSON.parse(String((vi.mocked(fetch).mock.calls[1][1] as RequestInit).body))).toMatchObject({
      uploadToken: 'token-1',
      key: KEY,
      fileId: FILE_ID,
      sizeBytes: 3,
      expiresAtUnix: TICKET.expiresAtUnix,
    });
    expect(progress).toContain(100);
    // The API's own route is used for metadata only: the file never travels through it.
    expect(calls.map((c) => c.url)).not.toContain(`/api/v1/projects/${PROJECT}/files`);
  });

  it('sends a path for a folder upload so the key keeps the folder', async () => {
    const { calls } = stubApi();
    const upload = uploadFileWithProgress(PROJECT, videoFile(), () => {}, { relativePath: 'broll/day1/clip.mp4' });

    await vi.waitFor(() => expect(FakeXhr.instances).toHaveLength(1));
    lastXhr().succeed(200);
    await upload;

    expect(JSON.parse(String((vi.mocked(fetch).mock.calls[0][1] as RequestInit).body))).toMatchObject({
      relativePath: 'broll/day1/clip.mp4',
    });
    expect(JSON.parse(String((vi.mocked(fetch).mock.calls[1][1] as RequestInit).body))).toMatchObject({
      relativePath: 'broll/day1/clip.mp4',
    });
    expect(calls).toHaveLength(2);
  });

  it('reports the 5 TiB ceiling the server now sends, not the old 20 GB one', async () => {
    stubApi();
    const upload = uploadFileWithProgress(PROJECT, videoFile(), () => {});
    await vi.waitFor(() => expect(FakeXhr.instances).toHaveLength(1));
    lastXhr().succeed(200);
    await upload;

    const presign = await fetch;
    expect(presign).toBeDefined();
    // The ticket's own `maxBytes` is what a caller may check against.
    expect(TICKET.maxBytes).toBe(5_497_558_138_880);
    expect(TICKET.maxBytes).toBeGreaterThan(20_000_000_000);
  });
});

describe('the multipart route', () => {
  /** Answers `upload-url` with a multipart ticket of `partCount` parts. */
  function stubMultipart(partCount: number, partSizeBytes = 1024, complete?: () => Response) {
    return stubApi({
      presign: () => json(200, multipartTicket(partCount, partSizeBytes)),
      ...(complete ? { complete } : {}),
    });
  }

  it('asks for part URLs, PUTs every part once, and sends every ETag to completion', async () => {
    const { calls, partBatches } = stubMultipart(3);
    const upload = uploadFileWithProgress(PROJECT, sizedFile(3 * 1024), () => {});

    await vi.waitFor(() => expect(puts()).toHaveLength(3));
    for (const put of puts()) put.succeedPart(`"etag-${put.url.split('part=')[1]}"`);
    await expect(upload).resolves.toEqual(RECORDED);

    // One batch, all three parts.
    expect(partBatches).toEqual([[1, 2, 3]]);
    expect(calls.map((c) => c.url)).toEqual([
      `/api/v1/projects/${PROJECT}/files/upload-url`,
      `/api/v1/projects/${PROJECT}/files/upload-parts`,
      `/api/v1/projects/${PROJECT}/files/upload-complete`,
    ]);

    const completion = calls.find((c) => c.url.endsWith('/upload-complete'))?.body as Record<string, unknown>;
    expect(completion).toMatchObject({
      mode: 'multipart',
      uploadId: 'upload-1',
      partSizeBytes: 1024,
      partCount: 3,
      sizeBytes: 3 * 1024,
      parts: [
        { partNumber: 1, etag: '"etag-1"' },
        { partNumber: 2, etag: '"etag-2"' },
        { partNumber: 3, etag: '"etag-3"' },
      ],
    });
  });

  it('slices each part to its own byte range, the last one shorter', async () => {
    stubMultipart(3, 1024);
    const upload = uploadFileWithProgress(PROJECT, sizedFile(2500), () => {});

    await vi.waitFor(() => expect(puts()).toHaveLength(3));
    const sizes = puts().map((p) => (p.body as Blob).size);
    for (const put of puts()) put.succeedPart();
    await upload;

    expect(sizes).toEqual([1024, 1024, 452]);
  });

  it('signs parts in batches of at most 100, even when there are hundreds', async () => {
    const { partBatches } = stubMultipart(250, 8);
    const upload = uploadFileWithProgress(PROJECT, sizedFile(250 * 8), () => {});

    // Parts are signed up front (three batches), then PUT three at a time, so the pool is drained in
    // steps rather than all at once.
    let settled = 0;
    while (settled < 250) {
      await vi.waitFor(() => expect(puts().length).toBeGreaterThan(settled), { timeout: 5000, interval: 5 });
      for (const put of puts().slice(settled)) put.succeedPart();
      settled = puts().length;
    }
    await expect(upload).resolves.toEqual(RECORDED);

    expect(partBatches.map((b) => b.length)).toEqual([100, 100, 50]);
    expect(partBatches[0][0]).toBe(1);
    expect(partBatches[2][partBatches[2].length - 1]).toBe(250);
  });

  it('never has more than three part PUTs in flight at once', async () => {
    stubMultipart(6, 8);
    const upload = uploadFileWithProgress(PROJECT, sizedFile(6 * 8), () => {});

    await vi.waitFor(() => expect(puts().length).toBe(3));
    expect(puts().length).toBe(3);

    // Two finish: exactly two more open, never four.
    puts()[0].succeedPart();
    puts()[1].succeedPart();
    await vi.waitFor(() => expect(puts().length).toBe(5));
    puts()[2].succeedPart();
    await vi.waitFor(() => expect(puts().length).toBe(6));

    for (const put of puts()) put.succeedPart();
    await expect(upload).resolves.toEqual(RECORDED);
    expect(puts().length).toBe(6);
  });

  it('retries a part the store answered with a server error, up to three attempts', async () => {
    const { calls } = stubMultipart(2, 16);
    const upload = uploadFileWithProgress(PROJECT, sizedFile(2 * 16), () => {});

    await vi.waitFor(() => expect(puts().length).toBe(2));
    const first = puts()[0];
    // The store itself stumbled (5xx): worth another attempt, unlike a 403.
    first.fail(503, 'ServiceUnavailable');
    // The retry re-PUTs the SAME part number after a backoff, and the neighbouring part is untouched.
    await vi.waitFor(() => expect(puts().length).toBe(3), { timeout: 3000, interval: 5 });
    expect(puts()[2].url).toBe(first.url);
    expect(puts()[1].url).not.toBe(first.url);

    puts()[1].succeedPart('"etag-b"');
    puts()[2].succeedPart('"etag-a"');
    await expect(upload).resolves.toEqual(RECORDED);

    const completion = calls.find((c) => c.url.endsWith('/upload-complete'))?.body as Record<string, unknown>;
    expect(completion.parts).toEqual([
      { partNumber: 1, etag: '"etag-a"' },
      { partNumber: 2, etag: '"etag-b"' },
    ]);
  });

  it('gives up after three attempts when a part keeps failing', async () => {
    const { calls } = stubMultipart(1, 16);
    const upload = uploadFileWithProgress(PROJECT, sizedFile(16), () => {});

    await vi.waitFor(() => expect(puts().length).toBe(1));
    puts()[0].fail(503, 'ServiceUnavailable');
    await vi.waitFor(() => expect(puts().length).toBe(2), { timeout: 3000, interval: 5 });
    puts()[1].fail(503, 'ServiceUnavailable');
    await vi.waitFor(() => expect(puts().length).toBe(3), { timeout: 3000, interval: 5 });
    puts()[2].fail(503, 'ServiceUnavailable');

    const error = await upload.catch((cause: unknown) => cause);
    expect(String((error as Error).message)).toMatch(/503/);
    // Three attempts, no fourth, and the aborted upload is cleaned up.
    expect(puts()).toHaveLength(3);
    expect(calls.some((c) => c.url.endsWith('/upload-abort'))).toBe(true);
  });

  it('does not retry a part storage itself refused — it fails the upload, and aborts it', async () => {
    const { calls } = stubMultipart(2, 16);
    const upload = uploadFileWithProgress(PROJECT, sizedFile(2 * 16), () => {});

    await vi.waitFor(() => expect(puts().length).toBe(2));
    const refused = puts()[0];
    refused.fail(403, 'AccessDenied');
    puts()[1].succeedPart();

    const error = await upload.catch((cause: unknown) => cause);
    expect(error).toBeInstanceOf(Error);
    expect(String((error as Error).message)).toMatch(/403/);
    // One attempt only for that part.
    expect(puts().filter((p) => p.url === refused.url)).toHaveLength(1);
    // Nothing was completed, and the half-written multipart upload was aborted.
    expect(puts()).toHaveLength(2);
    expect(calls.some((c) => c.url.endsWith('/upload-complete'))).toBe(false);
    expect(calls.some((c) => c.url.endsWith('/upload-abort'))).toBe(true);
  });

  it('aborts the upload when completion itself is refused', async () => {
    const { calls } = stubMultipart(1, 8, () => json(409, { code: 'UPLOAD_INCOMPLETE', message: 'parts missing' }));
    const upload = uploadFileWithProgress(PROJECT, sizedFile(8), () => {});

    await vi.waitFor(() => expect(puts()).toHaveLength(1));
    puts()[0].succeedPart();

    const error = await upload.catch((cause: unknown) => cause);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(409);
    expect(calls.some((c) => c.url.endsWith('/upload-abort'))).toBe(true);
  });

  it('aggregates progress across all parts rather than reporting each one separately', async () => {
    stubMultipart(3, 100);
    const progress: number[] = [];
    const upload = uploadFileWithProgress(PROJECT, sizedFile(300), (percent) => progress.push(percent));

    await vi.waitFor(() => expect(puts().length).toBe(3));
    // A third of the file, flushed in two of the three parts.
    puts()[0].flush(100, 100);
    puts()[1].flush(100, 100);
    await vi.waitFor(() => expect(progress).toContain(66));

    // Early bytes cannot push the bar past 99 — completion is what says 100.
    expect(Math.max(...progress)).toBeLessThan(100);
    for (const put of puts()) put.succeedPart();
    await expect(upload).resolves.toEqual(RECORDED);
    expect(progress[progress.length - 1]).toBe(100);
    // Monotonic apart from the finish.
    expect([...progress].sort((a, b) => a - b)).toEqual(progress);
  });

  it('reports a quota refusal from the presign unchanged, with no part PUT at all', async () => {
    stubApi({
      presign: () => json(413, {
        code: 'STORAGE_QUOTA_EXCEEDED',
        feature: 'storage',
        message: 'Your workspace is out of storage.',
        limit: 15_000_000_000,
        current: 15_000_000_000,
      }),
    });

    const upload = uploadFileWithProgress(PROJECT, videoFile(), () => {});
    const error = await upload.catch((cause: unknown) => cause);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(413);
    expect((error as ApiError).code).toBe('STORAGE_QUOTA_EXCEEDED');
    expect(puts()).toHaveLength(0);
  });
});

describe('the fallback rule', () => {
  it('falls back to the multipart POST when the API answers 501 DIRECT_UPLOAD_DISABLED', async () => {
    stubApi({ presign: () => json(501, { code: 'DIRECT_UPLOAD_DISABLED', message: 'not this deployment' }) });

    const upload = uploadFile(PROJECT, videoFile());

    await vi.waitFor(() => expect(FakeXhr.instances).toHaveLength(1));
    const post = lastXhr();
    expect(post.method).toBe('POST');
    expect(post.url).toBe(`/api/v1/projects/${PROJECT}/files`);
    expect(post.body).toBeInstanceOf(FormData);
    post.status = 201;
    post.responseText = JSON.stringify(RECORDED);
    post.readyState = FakeXhr.DONE;
    post.onreadystatechange?.();

    await expect(upload).resolves.toEqual(RECORDED);
  });

  it('falls back when the PUT never reached storage at all (a missing bucket CORS policy)', async () => {
    stubApi();
    const upload = uploadFile(PROJECT, videoFile());

    await vi.waitFor(() => expect(FakeXhr.instances).toHaveLength(1));
    const put = lastXhr();
    expect(put.method).toBe('PUT');
    // A blocked preflight: status 0 and not one byte of the body flushed.
    put.networkError();

    await vi.waitFor(() => expect(FakeXhr.instances).toHaveLength(2));
    expect(lastXhr().method).toBe('POST');
    lastXhr().status = 201;
    lastXhr().responseText = JSON.stringify(RECORDED);
    lastXhr().readyState = FakeXhr.DONE;
    lastXhr().onreadystatechange?.();

    await expect(upload).resolves.toEqual(RECORDED);
  });

  it('does NOT re-upload when storage refused the write itself', async () => {
    stubApi();
    const upload = uploadFile(PROJECT, videoFile());

    await vi.waitFor(() => expect(FakeXhr.instances).toHaveLength(1));
    lastXhr().fail(403, 'AccessDenied');

    await expect(upload).rejects.toThrow(/403/);
    expect(FakeXhr.instances).toHaveLength(1);
  });

  it('does NOT re-upload when the PUT failed after bytes had already been sent', async () => {
    stubApi();
    const upload = uploadFile(PROJECT, videoFile());

    await vi.waitFor(() => expect(FakeXhr.instances).toHaveLength(1));
    const put = lastXhr();
    put.flush(2, 3);
    put.networkError();

    await expect(upload).rejects.toThrow(/interrupted/);
    expect(FakeXhr.instances).toHaveLength(1);
  });

  it('reports a refused finalisation rather than sending the file again through the API', async () => {
    stubApi({ complete: () => json(409, { code: 'UPLOAD_SIZE_MISMATCH', message: 'Storage holds 2 bytes…' }) });
    const upload = uploadFile(PROJECT, videoFile());

    await vi.waitFor(() => expect(FakeXhr.instances).toHaveLength(1));
    lastXhr().succeed(200);

    const error = await upload.catch((cause: unknown) => cause);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(409);
    expect(FakeXhr.instances).toHaveLength(1);
  });

  it('decodes the fallback route’s own quota refusal into an ApiError with its figures', async () => {
    stubApi({ presign: () => json(501, { code: 'DIRECT_UPLOAD_DISABLED', message: 'not this deployment' }) });
    const upload = uploadFile(PROJECT, videoFile());

    await vi.waitFor(() => expect(FakeXhr.instances).toHaveLength(1));
    const post = lastXhr();
    post.status = 413;
    post.responseText = JSON.stringify({ code: 'STORAGE_QUOTA_EXCEEDED', feature: 'storage', limit: 15_000_000_000, current: 15_000_000_000 });
    post.readyState = FakeXhr.DONE;
    post.onreadystatechange?.();

    const error = await upload.catch((cause: unknown) => cause);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).code).toBe('STORAGE_QUOTA_EXCEEDED');
  });
});
