import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// `apiFetch` is stubbed so each assertion is on the method, path and body a function builds: if a
// route drifts from DesignSystemsController / OrgDesignLibrariesController, one of these fails.
vi.mock('./client', async () => {
  const actual = await vi.importActual<typeof import('./client')>('./client');
  return { ...actual, apiFetch: vi.fn() };
});

import { ApiError, apiFetch } from './client';
import {
  approveDesignLibraryVersion,
  approveProposal,
  approveVersion,
  buildProfile,
  checkForDrift,
  createDesignLibrary,
  deleteDesignLibrary,
  designExportFileName,
  exportDesignDocument,
  getDesignSystemsSummary,
  getDesignVersion,
  getEditorDefaults,
  getVersionDiff,
  importBrandPdf,
  importDesign,
  importFigmaVariables,
  importZip,
  ingestDesignSystem,
  linkProjectLibrary,
  listDesignLibraries,
  listDesignLibraryVersions,
  listDesignProposals,
  listDesignSources,
  listDesignVersions,
  lockVersion,
  rejectProposal,
  rejectVersion,
  renameDesignLibrary,
  restoreVersion,
} from './design-system';

const apiFetchMock = vi.mocked(apiFetch);
const P = '/api/v1/projects/p1/design-system';
const L = '/api/v1/org/design-libraries';

beforeEach(() => {
  apiFetchMock.mockReset();
  apiFetchMock.mockResolvedValue(undefined as never);
});

/** The `[url, init]` of the single call a function made. */
function call(): [string, RequestInit | undefined] {
  expect(apiFetchMock).toHaveBeenCalledTimes(1);
  const [url, init] = apiFetchMock.mock.calls[0];
  return [url, init];
}

describe('design-system reads', () => {
  it('summary', async () => {
    await getDesignSystemsSummary('p1');
    expect(call()).toEqual([P, undefined]);
  });

  it('versions list carries the kind', async () => {
    await listDesignVersions('p1');
    expect(call()[0]).toBe(`${P}/versions?kind=design`);
  });

  it('versions list for the product map', async () => {
    await listDesignVersions('p1', 'product_map');
    expect(call()[0]).toBe(`${P}/versions?kind=product_map`);
  });

  it('one version', async () => {
    await getDesignVersion('p1', 'v1');
    expect(call()).toEqual([`${P}/versions/v1`, undefined]);
  });

  it('sources carry the kind', async () => {
    await listDesignSources('p1', 'product_map');
    expect(call()[0]).toBe(`${P}/sources?kind=product_map`);
  });

  it('editor defaults maps a 204 (undefined) to null', async () => {
    await expect(getEditorDefaults('p1')).resolves.toBeNull();
    expect(call()[0]).toBe(`${P}/editor-defaults`);
  });

  it('proposals carry kind and optional status', async () => {
    await listDesignProposals('p1', 'design', 'Pending');
    expect(call()[0]).toBe(`${P}/proposals?kind=design&status=Pending`);
  });

  it('diff passes the older version as against', async () => {
    await getVersionDiff('p1', 'new', 'old');
    expect(call()[0]).toBe(`${P}/versions/new/diff?against=old`);
  });
});

describe('design-system mutations', () => {
  it('build posts the body as given', async () => {
    await buildProfile('p1', { templateKey: 'product-profile-from-code', acceptEstimateCredits: 12 });
    const [url, init] = call();
    expect(url).toBe(`${P}/builds`);
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({
      templateKey: 'product-profile-from-code',
      acceptEstimateCredits: 12,
    });
  });

  it('import project files sends ids and kinds', async () => {
    await importDesign('p1', ['f1', 'f2'], ['design', 'product_map']);
    const [url, init] = call();
    expect(url).toBe(`${P}/import`);
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({
      projectFileIds: ['f1', 'f2'],
      kinds: ['design', 'product_map'],
    });
  });

  it('figma import sends the key and token', async () => {
    await importFigmaVariables('p1', 'KEY', 'tok');
    const [url, init] = call();
    expect(url).toBe(`${P}/import/figma`);
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({ fileKey: 'KEY', accessToken: 'tok' });
  });

  it('approve a version is a bodiless POST', async () => {
    await approveVersion('p1', 'v1');
    expect(call()).toEqual([`${P}/versions/v1/approve`, { method: 'POST' }]);
  });

  it('reject a version sends an empty body, or the note', async () => {
    await rejectVersion('p1', 'v1');
    expect(call()[0]).toBe(`${P}/versions/v1/reject`);
    expect(JSON.parse(String(call()[1]?.body))).toEqual({});

    apiFetchMock.mockClear();
    await rejectVersion('p1', 'v1', 'no thanks');
    expect(JSON.parse(String(call()[1]?.body))).toEqual({ note: 'no thanks' });
  });

  it('restore is a bodiless POST', async () => {
    await restoreVersion('p1', 'v1');
    expect(call()).toEqual([`${P}/versions/v1/restore`, { method: 'POST' }]);
  });

  it('approve a proposal sends the note when given and an empty body otherwise', async () => {
    await approveProposal('p1', 'pr1', 'looks right');
    let [url, init] = call();
    expect(url).toBe(`${P}/proposals/pr1/approve`);
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({ note: 'looks right' });

    apiFetchMock.mockClear();
    await approveProposal('p1', 'pr1');
    [url, init] = call();
    expect(JSON.parse(String(init?.body))).toEqual({});
  });

  it('reject a proposal sends the note', async () => {
    await rejectProposal('p1', 'pr1', 'nope');
    const [url, init] = call();
    expect(url).toBe(`${P}/proposals/pr1/reject`);
    expect(JSON.parse(String(init?.body))).toEqual({ note: 'nope' });
  });

  it('locks is a PATCH with lock and unlock', async () => {
    await lockVersion('p1', 'v1', ['color.a'], ['color.b']);
    const [url, init] = call();
    expect(url).toBe(`${P}/versions/v1/locks`);
    expect(init?.method).toBe('PATCH');
    expect(JSON.parse(String(init?.body))).toEqual({ lock: ['color.a'], unlock: ['color.b'] });
  });

  it('check now is a bodiless POST', async () => {
    await checkForDrift('p1');
    expect(call()).toEqual([`${P}/check`, { method: 'POST' }]);
  });

  it('ingest posts the execution id and step order', async () => {
    await ingestDesignSystem('p1', 'exec-1', 3);
    const [url, init] = call();
    expect(url).toBe(`${P}/ingest`);
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({ executionId: 'exec-1', stepOrder: 3 });
  });

  it('library link is a PUT with the library id, or null to clear', async () => {
    await linkProjectLibrary('p1', 'lib-1');
    let [url, init] = call();
    expect(url).toBe(`${P}/library-link`);
    expect(init?.method).toBe('PUT');
    expect(JSON.parse(String(init?.body))).toEqual({ libraryId: 'lib-1' });

    apiFetchMock.mockClear();
    await linkProjectLibrary('p1', null);
    [url, init] = call();
    expect(JSON.parse(String(init?.body))).toEqual({ libraryId: null });
  });
});

describe('organization libraries', () => {
  it('list', async () => {
    await listDesignLibraries();
    expect(call()).toEqual([L, undefined]);
  });

  it('create with and without a document', async () => {
    await createDesignLibrary('Brand');
    let [url, init] = call();
    expect(url).toBe(L);
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({ name: 'Brand' });

    apiFetchMock.mockClear();
    await createDesignLibrary('Brand', { color: {} });
    [url, init] = call();
    expect(JSON.parse(String(init?.body))).toEqual({ name: 'Brand', document: { color: {} } });
  });

  it('rename is a PATCH', async () => {
    await renameDesignLibrary('lib-1', 'New');
    const [url, init] = call();
    expect(url).toBe(`${L}/lib-1`);
    expect(init?.method).toBe('PATCH');
    expect(JSON.parse(String(init?.body))).toEqual({ name: 'New' });
  });

  it('delete confirms in the query string', async () => {
    await deleteDesignLibrary('lib-1');
    expect(call()).toEqual([`${L}/lib-1?confirm=true`, { method: 'DELETE' }]);
  });

  it('versions list', async () => {
    await listDesignLibraryVersions('lib-1');
    expect(call()).toEqual([`${L}/lib-1/versions`, undefined]);
  });

  it('approve a library version is a bodiless POST', async () => {
    await approveDesignLibraryVersion('lib-1', 'v1');
    expect(call()).toEqual([`${L}/lib-1/versions/v1/approve`, { method: 'POST' }]);
  });
});

describe('raw fetch calls', () => {
  const fetchMock = vi.fn();
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });
  afterEach(() => vi.unstubAllGlobals());

  it('pdf import posts multipart under the field "file"', async () => {
    fetchMock.mockResolvedValue({ ok: true, json: async () => ({}) });
    const file = new File(['x'], 'brand.pdf', { type: 'application/pdf' });
    await importBrandPdf('p1', file);

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe(`${P}/import/pdf`);
    expect(init.method).toBe('POST');
    expect((init.body as FormData).get('file')).toBe(file);
    expect(init.headers).toBeUndefined();
  });

  it('zip import posts multipart under the field "file"', async () => {
    fetchMock.mockResolvedValue({ ok: true, json: async () => ({}) });
    const file = new File(['x'], 'export.zip');
    await importZip('p1', file);

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe(`${P}/import-zip`);
    expect((init.body as FormData).get('file')).toBe(file);
  });

  it('a refused pdf import throws an ApiError carrying status and code', async () => {
    fetchMock.mockResolvedValue({
      ok: false,
      status: 415,
      statusText: 'Unsupported',
      json: async () => ({ code: 'NOT_A_PDF', message: 'not a pdf' }),
    });
    await expect(importBrandPdf('p1', new File(['x'], 'a.pdf'))).rejects.toMatchObject({
      status: 415,
      code: 'NOT_A_PDF',
    });
    await expect(importBrandPdf('p1', new File(['x'], 'a.pdf'))).rejects.toBeInstanceOf(ApiError);
  });

  it('export puts kind and format in the query and returns the blob', async () => {
    const blob = new Blob(['x']);
    fetchMock.mockResolvedValue({ ok: true, blob: async () => blob });
    await expect(exportDesignDocument('p1', 'design', 'css-tailwind')).resolves.toBe(blob);
    expect(fetchMock.mock.calls[0][0]).toBe(`${P}/export?kind=design&format=css-tailwind`);
  });

  it('export file names mirror the server', () => {
    expect(designExportFileName('design', 'css-tailwind')).toBe('design-system.tailwind.css');
    expect(designExportFileName('design', 'designmd')).toBe('DESIGN.md');
    expect(designExportFileName('product_map', 'json')).toBe('product-map.json');
  });
});
