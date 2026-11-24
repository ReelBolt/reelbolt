// @vitest-environment jsdom
/**
 * Avatars and objects in the picture editor: the request carries the chosen ids, the one-time consent is asked
 * and the run repeated, refusals read in plain words, a layer says who it shows, and an export that shows a
 * person who asks for it gets the "AI-generated" label.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { ApiError } from '@/lib/api/client';
import type { EditImageDocument, EditImageLayer } from '@/lib/types/image-document';
import { RecordingContext } from '@/lib/editor/__tests__/recording-context';
import { testAvatar, testObject } from '@/test/avatars';

vi.mock('next/navigation', () => ({ usePathname: () => '/editor/thumbnails/d', useRouter: () => ({ push: vi.fn() }) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal(
  'PointerEvent',
  class extends MouseEvent {
    pointerId: number;
    constructor(type: string, init: MouseEventInit & { pointerId?: number } = {}) {
      super(type, init);
      this.pointerId = init.pointerId ?? 1;
    }
  },
);
Element.prototype.scrollIntoView = vi.fn();

const api = vi.hoisted(() => ({
  getImageDocument: vi.fn(),
  updateImageDocument: vi.fn(),
  updateImageLayer: vi.fn(),
  deleteImageLayer: vi.fn(),
  runImageOperation: vi.fn(),
  uploadEditorAsset: vi.fn(),
  createImageExport: vi.fn(),
  getOperationCapabilities: vi.fn(),
  getImageGraphics: vi.fn(),
}));
vi.mock('@/lib/api/image-documents', async (orig) => ({ ...(await orig<typeof import('@/lib/api/image-documents')>()), ...api }));

const dom = vi.hoisted(() => ({ renderExport: vi.fn(), downloadBlob: vi.fn(), canEncode: vi.fn(), renderScaledCopy: vi.fn() }));
vi.mock('@/lib/editor/canvas-dom', async () => {
  const { RecordingContext: Ctx, fakeBitmap: bitmap } = await import('@/lib/editor/__tests__/recording-context');
  return {
    loadBitmap: async () => bitmap(1280, 720),
    ensureFont: async () => {},
    ensureFontsFor: async () => {},
    domScratch: () => null,
    domTextMeasurer: () => (_font: string, text: string) => text.length * 10,
    domMaskCanvas: (width: number, height: number) => {
      const ctx = new Ctx();
      return { width, height, getContext: () => ctx.as(), toBlob: (cb: (b: Blob | null) => void) => cb(new Blob(['mask'], { type: 'image/png' })) };
    },
    renderExport: dom.renderExport,
    downloadBlob: dom.downloadBlob,
    canEncode: dom.canEncode,
    renderScaledCopy: dom.renderScaledCopy,
  };
});
vi.mock('@/lib/hooks/use-execution-stream', () => ({
  useExecutionStream: () => ({ events: [], connectionState: 'connected', lastError: null, lastEventAt: null, metrics: {} }),
}));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: [], isLoading: false, mutate: async () => undefined }) }));

const av = vi.hoisted(() => ({ requirements: undefined as unknown, requirementsError: undefined as unknown, avatars: [] as unknown[], grant: vi.fn() }));
vi.mock('@/lib/hooks/use-avatars', () => ({
  useAvatarRequirements: () => ({ data: av.requirements, error: av.requirementsError }),
  useProjectAvatars: (_p: string, enabled = true) => ({ data: enabled ? av.avatars : undefined }),
}));
vi.mock('@/lib/api/avatars', async (orig) => ({ ...(await orig<typeof import('@/lib/api/avatars')>()), grantAvatarMediaConsent: (...a: unknown[]) => av.grant(...a) }));

import { ThumbnailEditor } from './ThumbnailEditor';

const baseLayer = (over: Partial<EditImageLayer> & { id: string }): EditImageLayer => ({
  zOrder: 1, role: 'background', kind: 'Source' as unknown as EditImageLayer['kind'], label: over.id, sourceProjectFileId: 'file-1', imageUrl: `/img/${over.id}`, matteUrl: null,
  mimeType: 'image/png', width: 1280, height: 720, opacity: 1, transform: { x: 0, y: 0, scale: 1, rotation: 0, flipX: false, flipY: false }, segmentationModel: null,
  segmentationPrompt: null, createdAt: '2026-11-16T00:00:00Z', visible: true, locked: false, blendMode: 'normal', content: null, provenance: null, ...over,
});
const SOURCE = baseLayer({ id: 'src', label: 'Original' });
const withAnna = (over: Partial<EditImageLayer> = {}) =>
  baseLayer({
    id: 'gen', label: 'Anna on a beach', kind: 'Generated' as never, role: 'overlay', zOrder: 2,
    content: { subjects: [{ avatarId: 'anna', name: 'Anna', kind: 'Person' }, { avatarId: 'bottle', name: 'Bottle', kind: 'Object' }], aiGenerated: true, disclosure: 'Visible' },
    ...over,
  });
const docDto = (layers: EditImageLayer[], revision = 3): EditImageDocument => ({
  id: 'd', projectId: 'p', name: 'Cover', sourceProjectFileId: 'file-1', revision, createdAt: '', updatedAt: '', layers, canvasWidth: 1280, canvasHeight: 720, backgroundColor: null,
});
const opResult = (layer: EditImageLayer) => ({ layers: [layer], operation: 'generate', provider: { kind: 'FalAi', model: 'google/nano-banana-2.1/edit' }, usage: { unit: 'images', quantity: 1 }, revision: 4 });
const refusal = (status: number, body: Record<string, unknown>) => new ApiError(String(body.message ?? 'refused'), status, [], body);

beforeEach(() => {
  for (const fn of Object.values(api)) fn.mockReset();
  for (const fn of Object.values(dom)) fn.mockReset();
  av.grant.mockReset().mockResolvedValue({ consented: true, consentedAt: '2026-11-16T10:00:00Z' });
  av.requirements = { statementVersion: 'avatar-consent-2026-12' };
  av.requirementsError = undefined;
  av.avatars = [testAvatar(), testObject()];
  window.localStorage.clear();
  dom.canEncode.mockResolvedValue(true);
  api.getOperationCapabilities.mockRejectedValue(new Error('not described in this test'));
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  HTMLCanvasElement.prototype.getContext = (() => new RecordingContext().as()) as unknown as typeof HTMLCanvasElement.prototype.getContext;
  api.getImageDocument.mockResolvedValue(docDto([SOURCE]));
  api.getImageGraphics.mockResolvedValue({ graphics: [], estimate: null, available: true, unavailableReason: null });
});
afterEach(cleanup);

async function ready() {
  render(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <MantineProvider>
        <ThumbnailEditor projectId="p" documentId="d" />
      </MantineProvider>
    </SWRConfig>,
  );
  await screen.findByRole('application', { name: 'Thumbnail canvas' });
}
const openTab = (name: string) => fireEvent.click(screen.getByRole('tab', { name }));
async function pickTool(title: string) {
  openTab('AI');
  fireEvent.click(screen.getByRole('button', { name: /Choose an AI tool|Change tool/ }));
  const dialog = await screen.findByRole('dialog', { name: 'Choose an AI tool' });
  fireEvent.click(within(dialog).getByRole('button', { name: title }));
  await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Choose an AI tool' })).toBeNull());
}
/** "Create a picture" with Anna and the bottle ticked and a description typed, ready to run. */
async function readyToCreate() {
  await ready();
  await pickTool('Create a picture');
  const tool = within(screen.getByTestId('ai-tool-generate'));
  fireEvent.change(tool.getByLabelText('Describe it'), { target: { value: 'Anna holds the bottle' } });
  fireEvent.click(tool.getByTestId('subject-option-anna'));
  fireEvent.click(tool.getByTestId('subject-option-bottle'));
  return () => fireEvent.click(tool.getByRole('button', { name: 'Create the picture' }));
}
const REQUEST = { operation: 'generate', prompt: 'Anna holds the bottle', subjectIds: ['anna', 'bottle'], params: { width: 1280, height: 720 } };

describe('running a tool with avatars and objects', () => {
  it('sends the chosen ids with the operation and adopts the new layer', async () => {
    api.runImageOperation.mockResolvedValue(opResult(withAnna()));
    const run = await readyToCreate();
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, withAnna()], 4));
    run();
    await waitFor(() => expect(api.runImageOperation).toHaveBeenCalledWith('p', 'd', REQUEST));
    expect((await screen.findByTestId('result-bar-changed')).textContent).toMatch(/added a new layer/);
    expect(av.grant).not.toHaveBeenCalled();
  });

  it('offers no picker while the project\'s avatars do not answer, and sends no ids', async () => {
    av.requirements = undefined;
    av.requirementsError = new ApiError('Not Found', 404, [], {});
    api.runImageOperation.mockResolvedValue(opResult(baseLayer({ id: 'gen', label: 'Mountain', kind: 'Generated' as never, zOrder: 2 })));
    await ready();
    await pickTool('Create a picture');
    const tool = within(screen.getByTestId('ai-tool-generate'));
    expect(tool.queryByTestId('subject-picker')).toBeNull();
    fireEvent.change(tool.getByLabelText('Describe it'), { target: { value: 'a mountain' } });
    fireEvent.click(tool.getByRole('button', { name: 'Create the picture' }));
    await waitFor(() => expect(api.runImageOperation).toHaveBeenCalled());
    expect('subjectIds' in api.runImageOperation.mock.calls[0][2]).toBe(false);
  });
});

describe('the one-time consent', () => {
  it('asks when the server needs it, records the yes, then runs the same thing again', async () => {
    api.runImageOperation.mockRejectedValueOnce(refusal(409, { code: 'CONSENT_REQUIRED', message: 'Consent is required.' })).mockResolvedValueOnce(opResult(withAnna()));
    const run = await readyToCreate();
    run();
    const dialog = within(await screen.findByRole('dialog', { name: 'Send avatar and object files to outside AI model providers (other companies)?' }));
    expect(api.runImageOperation).toHaveBeenCalledTimes(1);
    // nothing about it is shown as a failure, and the tool is not switched off
    expect(screen.queryByTestId('picture-tool-unavailable')).toBeNull();
    expect((dialog.getByTestId('media-consent-confirm') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(dialog.getByTestId('media-consent-check'));
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, withAnna()], 4));
    fireEvent.click(dialog.getByTestId('media-consent-confirm'));
    await waitFor(() => expect(api.runImageOperation).toHaveBeenCalledTimes(2));
    expect(av.grant).toHaveBeenCalledWith('p');
    expect(api.runImageOperation).toHaveBeenLastCalledWith('p', 'd', REQUEST);
    expect((await screen.findByTestId('result-bar-changed')).textContent).toMatch(/added a new layer/);
    await waitFor(() => expect(screen.queryByTestId('media-consent-dialog')).toBeNull());
  });

  it('runs nothing more when the person says not now', async () => {
    api.runImageOperation.mockRejectedValueOnce(refusal(409, { code: 'CONSENT_REQUIRED' }));
    const run = await readyToCreate();
    run();
    fireEvent.click(await screen.findByTestId('media-consent-cancel'));
    await waitFor(() => expect(screen.queryByTestId('media-consent-dialog')).toBeNull());
    expect(av.grant).not.toHaveBeenCalled();
    expect(api.runImageOperation).toHaveBeenCalledTimes(1);
  });

  it('says so when the yes could not be saved, and runs nothing', async () => {
    api.runImageOperation.mockRejectedValueOnce(refusal(409, { code: 'CONSENT_REQUIRED' }));
    av.grant.mockRejectedValue(new Error('offline'));
    const run = await readyToCreate();
    run();
    fireEvent.click(await screen.findByTestId('media-consent-check'));
    fireEvent.click(screen.getByTestId('media-consent-confirm'));
    expect((await screen.findByTestId('media-consent-error')).textContent).toBe('We could not save your choice. Try again.');
    expect(api.runImageOperation).toHaveBeenCalledTimes(1);
  });
});

describe('refusals, in plain words', () => {
  const refused = async (error: unknown) => {
    api.runImageOperation.mockRejectedValue(error);
    const run = await readyToCreate();
    run();
    await waitFor(() => expect(api.runImageOperation).toHaveBeenCalled());
  };

  it('a person whose consent does not cover pictures', async () => {
    await refused(refusal(409, { code: 'AVATAR_CONSENT_NO_PICTURES', message: 'x' }));
    expect(await screen.findByText("Confirm this person's consent again in the Avatars tab before using them in a picture.")).toBeTruthy();
  });

  it('an avatar that cannot be used, with why when the server says', async () => {
    await refused(refusal(409, { code: 'AVATAR_NOT_USABLE', reason: 'reference_held', message: 'x' }));
    expect(await screen.findByText('One of the people or objects you chose cannot be used right now. One of its files was put on hold by a safety check.')).toBeTruthy();
  });

  it('a model that cannot be shown them: names the ones that can, and the tool stays available', async () => {
    await refused(refusal(409, { code: 'OPERATION_UNSUPPORTED', message: 'x', models: ['google/nano-banana-2.1/edit'] }));
    expect(await screen.findByText('The AI model set up for this tool cannot be shown people or objects. These models can: google/nano-banana-2.1/edit.')).toBeTruthy();
    expect(screen.queryByTestId('picture-tool-unavailable')).toBeNull();
    expect((within(screen.getByTestId('ai-tool-generate')).getByRole('button', { name: 'Create the picture' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('a plan without objects', async () => {
    await refused(refusal(402, { code: 'PLAN_NOT_ENTITLED', feature: 'avatar_objects', message: 'x' }));
    expect(await screen.findByText('Your plan does not include objects. See the plans to unlock them.')).toBeTruthy();
  });
});

describe('a layer made with avatars', () => {
  it('says "AI-generated" and who it shows on its row', async () => {
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, withAnna()]));
    await ready();
    expect(screen.getByTestId('layer-ai-gen').textContent).toBe('AI-generated · Anna and Bottle');
    expect(screen.queryByTestId('layer-ai-src')).toBeNull();
  });

  it('says only "AI-generated" when the server names nobody', async () => {
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, withAnna({ content: { aiGenerated: true, disclosure: 'MetadataOnly' } })]));
    await ready();
    expect(screen.getByTestId('layer-ai-gen').textContent).toBe('AI-generated');
  });
});

describe('export', () => {
  const png = new Blob(['png'], { type: 'image/png' });
  const exportNow = async () => {
    fireEvent.click(screen.getByRole('button', { name: 'Export' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Export your picture' }));
    fireEvent.click(dialog.getByRole('button', { name: 'Download' }));
    await waitFor(() => expect(dom.downloadBlob).toHaveBeenCalled());
    return dialog;
  };

  it('adds the label, and says so, when a visible layer shows a person who asks for a visible label', async () => {
    dom.renderExport.mockResolvedValue(png);
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, withAnna()]));
    await ready();
    const dialog = await exportNow();
    expect(dialog.getByTestId('export-ai-label-note').textContent).toBe('This picture shows an avatar, so the exported file gets a small “AI-generated” label in the bottom-right corner.');
    expect(dom.renderExport.mock.calls.length).toBeGreaterThan(0);
    for (const call of dom.renderExport.mock.calls) expect(call[4]).toEqual({ aiLabel: 'AI-generated' });
  });

  it('adds none when that layer is hidden', async () => {
    dom.renderExport.mockResolvedValue(png);
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, withAnna({ visible: false })]));
    await ready();
    const dialog = await exportNow();
    expect(dialog.queryByTestId('export-ai-label-note')).toBeNull();
    for (const call of dom.renderExport.mock.calls) expect(call).toHaveLength(4);
  });

  it('adds none for objects only (marked in the data, not on the picture) or for a picture made without avatars', async () => {
    dom.renderExport.mockResolvedValue(png);
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, withAnna({ content: { subjects: [{ avatarId: 'bottle', name: 'Bottle', kind: 'Object' }], aiGenerated: true, disclosure: 'MetadataOnly' } })]));
    await ready();
    const dialog = await exportNow();
    expect(dialog.queryByTestId('export-ai-label-note')).toBeNull();
    for (const call of dom.renderExport.mock.calls) expect(call).toHaveLength(4);
  });

  it('the label goes away once the layer is hidden in the editor', async () => {
    dom.renderExport.mockResolvedValue(png);
    api.updateImageLayer.mockResolvedValue(withAnna({ visible: false }));
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, withAnna()]));
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Hide Anna on a beach' }));
    await waitFor(() => expect(api.updateImageLayer).toHaveBeenCalled(), { timeout: 3000 });
    const dialog = await exportNow();
    expect(dialog.queryByTestId('export-ai-label-note')).toBeNull();
    expect(dom.renderExport.mock.calls.at(-1)).toHaveLength(4);
  });
});

describe('the price and availability of a picture with avatars or objects', () => {
  const genCaps = (credits: number, extra: Record<string, unknown> = {}) => ({
    operation: 'generate',
    provider: { kind: 'FalAi', model: 'm' },
    takesPrompt: true,
    maxPromptLength: 2000,
    expand: null,
    estimate: { billingEnabled: true, credits, balanceCredits: 100, affordable: true },
    canConfigure: false,
    ...extra,
  });
  const noProvider = () =>
    new ApiError('none', 409, [], { code: 'IMAGE_GENERATION_PROVIDER_UNAVAILABLE', reason: 'NO_PROVIDER', canConfigure: false });
  /** Answers the plain ask and the `subjects=true` ask of "create a picture" apart; every other tool is not described. */
  const capabilitiesOf = (plain: () => Promise<unknown>, withSubjects: () => Promise<unknown>) =>
    api.getOperationCapabilities.mockImplementation((_p: string, op: string, _size: unknown, options?: { subjects?: boolean }) => {
      if (op !== 'generate') return Promise.reject(new Error('not described in this test'));
      return options?.subjects === true ? withSubjects() : plain();
    });
  const subjectAsks = () => api.getOperationCapabilities.mock.calls.filter((c) => c[3]?.subjects === true);

  it('asks about the run with subjects only once some are chosen, and shows that price', async () => {
    capabilitiesOf(() => Promise.resolve(genCaps(2)), () => Promise.resolve(genCaps(7.5)));
    await ready();
    await pickTool('Create a picture');
    const tool = within(screen.getByTestId('ai-tool-generate'));
    expect((await tool.findByTestId('credits-generate-estimate')).textContent).toContain('about 2 credits');
    expect(subjectAsks()).toHaveLength(0);

    fireEvent.click(tool.getByTestId('subject-option-bottle'));
    await waitFor(() => expect(subjectAsks().length).toBeGreaterThan(0));
    expect(subjectAsks()[0]).toEqual(['p', 'generate', { documentId: 'd', width: 1280, height: 720 }, { subjects: true }]);
    await waitFor(() => expect(tool.getByTestId('credits-generate-estimate').textContent).toContain('about 7.5 credits'));

    // Unticked again: the plain price is back.
    fireEvent.click(tool.getByTestId('subject-option-bottle'));
    await waitFor(() => expect(tool.getByTestId('credits-generate-estimate').textContent).toContain('about 2 credits'));
  });

  it('lets a project with no picture-making service still make a picture of its avatars', async () => {
    capabilitiesOf(() => Promise.reject(noProvider()), () => Promise.resolve(genCaps(7.5)));
    api.runImageOperation.mockResolvedValue(opResult(withAnna()));
    await ready();
    await pickTool('Create a picture');
    const tool = within(screen.getByTestId('ai-tool-generate'));
    // Without subjects the tool is not set up, but the picker can still be used and says why it matters.
    expect(await tool.findByTestId('picture-tool-unavailable')).toBeTruthy();
    expect(tool.getByTestId('ai-subjects-other-service-generate').textContent).toBe(
      'A picture that shows a person or an object of this project is made by another AI service. Choose one to see whether it can run here.',
    );

    fireEvent.click(tool.getByTestId('subject-option-anna'));
    fireEvent.click(tool.getByTestId('subject-option-bottle'));
    await waitFor(() => expect(tool.queryByTestId('picture-tool-unavailable')).toBeNull());
    expect(tool.queryByTestId('ai-subjects-other-service-generate')).toBeNull();
    expect((await tool.findByTestId('credits-generate-estimate')).textContent).toContain('about 7.5 credits');

    fireEvent.change(tool.getByLabelText('Describe it'), { target: { value: 'Anna holds the bottle' } });
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, withAnna()], 4));
    fireEvent.click(tool.getByRole('button', { name: /Create the picture/ }));
    await waitFor(() => expect(api.runImageOperation).toHaveBeenCalledWith('p', 'd', REQUEST));
  });

  it('says so when the run with subjects cannot happen here either, and keeps the plain tool as it was', async () => {
    capabilitiesOf(() => Promise.resolve(genCaps(2)), () => Promise.reject(noProvider()));
    await ready();
    await pickTool('Create a picture');
    const tool = within(screen.getByTestId('ai-tool-generate'));
    await tool.findByTestId('credits-generate-estimate');

    fireEvent.click(tool.getByTestId('subject-option-bottle'));
    expect((await tool.findByTestId('picture-tool-unavailable')).textContent).toContain('Ask your workspace admin to set up picture tools.');
    // The picker stays usable, so the subject can be unticked and the plain tool used.
    fireEvent.click(tool.getByTestId('subject-option-bottle'));
    await waitFor(() => expect(tool.queryByTestId('picture-tool-unavailable')).toBeNull());
    expect(tool.getByTestId('credits-generate-estimate').textContent).toContain('about 2 credits');
  });

  it('asks about no other route for "edit with words"', async () => {
    api.getOperationCapabilities.mockResolvedValue(genCaps(3, { operation: 'instruct' }));
    await ready();
    await pickTool('Edit with words');
    const tool = within(screen.getByTestId('ai-tool-instruct'));
    fireEvent.click(await tool.findByTestId('subject-option-bottle'));
    await waitFor(() => expect(tool.getByTestId('credits-instruct-estimate').textContent).toContain('about 3 credits'));
    expect(subjectAsks()).toHaveLength(0);
  });
});
