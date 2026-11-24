// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { ApiError } from '@/lib/api/client';
import type { EditImageDocument, EditImageLayer } from '@/lib/types/image-document';
import { renderWithI18n } from '@/test/i18n';
import itEditor from '../../../messages/it/editor-thumbnail-a.json';
import itText from '../../../messages/it/editor-thumbnail-b.json';
import { ActivityProvider, useActivity, type ActivityContextValue } from '@/lib/activity/ActivityProvider';
import { FakeEventSource, signInAs } from '@/lib/activity/__tests__/helpers';
import { loadPictureReview, savePictureReview } from './picture-review-store';
import { RecordingContext } from '@/lib/editor/__tests__/recording-context';

vi.mock('next/navigation', () => ({ usePathname: () => '/editor/thumbnails/d', useRouter: () => ({ push: vi.fn() }) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
// jsdom has no PointerEvent, and Testing Library falls back to a plain Event that carries no
// coordinates or button. A MouseEvent subclass is all the canvas handlers read.
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
  createTextLayer: vi.fn(),
  createLayerFromFile: vi.fn(),
  segmentSubjects: vi.fn(),
  runImageOperation: vi.fn(),
  uploadEditorAsset: vi.fn(),
  createImageExport: vi.fn(),
  getOperationCapabilities: vi.fn(),
  getImageGraphics: vi.fn(),
  createImageGraphic: vi.fn(),
}));
vi.mock('@/lib/api/image-documents', async (orig) => ({ ...(await orig<typeof import('@/lib/api/image-documents')>()), ...api }));

const files = vi.hoisted(() => ({ uploadFile: vi.fn(), uploadFileWithProgress: vi.fn() }));
vi.mock('@/lib/api/files', () => ({ uploadFile: files.uploadFile, uploadFileWithProgress: files.uploadFileWithProgress }));

const dom = vi.hoisted(() => ({ renderExport: vi.fn(), downloadBlob: vi.fn(), canEncode: vi.fn(), renderScaledCopy: vi.fn() }));
// Layer images: every URL decodes to 1280 × 720 unless a test gives it a size, or makes it missing (404).
const pixels = vi.hoisted(() => ({ sizes: new Map<string, [number, number]>(), missing: new Set<string>() }));
vi.mock('@/lib/editor/canvas-dom', async () => {
  const { RecordingContext: Ctx, fakeBitmap: bitmap } = await import('@/lib/editor/__tests__/recording-context');
  return {
    loadBitmap: async (url: string) => {
      if (pixels.missing.has(url)) throw new Error('A layer image could not be loaded.');
      const [w, h] = pixels.sizes.get(url) ?? [1280, 720];
      return bitmap(w, h);
    },
    ensureFont: async () => {},
    ensureFontsFor: async () => {},
    domScratch: () => null,
    domTextMeasurer: () => (_font: string, text: string) => text.length * 10,
    domMaskCanvas: (width: number, height: number) => {
      const ctx = new Ctx();
      return {
        width,
        height,
        getContext: () => ctx.as(),
        toBlob: (cb: (b: Blob | null) => void) => cb(new Blob(['mask'], { type: 'image/png' })),
      };
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

vi.mock('@/lib/hooks/use-files', () => ({
  useProjectFiles: () => ({
    data: [{ id: 'logo', originalFileName: 'logo.png', mimeType: 'image/png' }],
    isLoading: false,
    mutate: async () => undefined,
  }),
}));

import { ThumbnailEditor } from './ThumbnailEditor';
import { announceImageDocumentChange, resetAnnouncedImageDocumentChanges } from '@/lib/editor/assistant-events';

const baseLayer = (over: Partial<EditImageLayer> & { id: string }): EditImageLayer => ({
  zOrder: 1,
  role: 'background',
  kind: 'Source' as unknown as EditImageLayer['kind'],
  label: over.id,
  sourceProjectFileId: 'file-1',
  imageUrl: `/img/${over.id}`,
  matteUrl: null,
  mimeType: 'image/png',
  width: 1280,
  height: 720,
  opacity: 1,
  transform: { x: 0, y: 0, scale: 1, rotation: 0, flipX: false, flipY: false },
  segmentationModel: null,
  segmentationPrompt: null,
  createdAt: '2026-10-10T00:00:00Z',
  visible: true,
  locked: false,
  blendMode: 'normal',
  content: null,
  provenance: null,
  ...over,
});

const SOURCE = baseLayer({ id: 'src', label: 'Original' });
const CUTOUT = baseLayer({ id: 'cut', label: 'Person', kind: 'Cutout' as never, role: 'subject', zOrder: 2 });

const docDto = (layers: EditImageLayer[], revision = 3): EditImageDocument => ({
  id: 'd',
  projectId: 'p',
  name: 'Cover',
  sourceProjectFileId: 'file-1',
  revision,
  createdAt: '',
  updatedAt: '',
  layers,
  canvasWidth: 1280,
  canvasHeight: 720,
  backgroundColor: null,
});

beforeEach(() => {
  for (const fn of Object.values(api)) fn.mockReset();
  for (const fn of Object.values(dom)) fn.mockReset();
  files.uploadFile.mockReset();
  pixels.sizes.clear();
  pixels.missing.clear();
  window.localStorage.clear();
  dom.canEncode.mockResolvedValue(true);
  api.getOperationCapabilities.mockRejectedValue(new Error('not described in this test'));
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  HTMLCanvasElement.prototype.getContext = (() => new RecordingContext().as()) as unknown as typeof HTMLCanvasElement.prototype.getContext;
  api.getImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT]));
  api.getImageGraphics.mockResolvedValue({ graphics: [], estimate: null, available: true, unavailableReason: null });
  resetAnnouncedImageDocumentChanges();
});
const realFetch = global.fetch;
afterEach(() => {
  cleanup();
  global.fetch = realFetch;
});

const mount = () =>
  render(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <MantineProvider>
        <ThumbnailEditor projectId="p" documentId="d" />
      </MantineProvider>
    </SWRConfig>,
  );
const mountIt = () =>
  renderWithI18n(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <ThumbnailEditor projectId="p" documentId="d" />
    </SWRConfig>,
    { locale: 'it' },
  );

async function ready() {
  mount();
  await screen.findByRole('application', { name: 'Thumbnail canvas' });
}

const openTab = (name: string) => fireEvent.click(screen.getByRole('tab', { name }));
const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));

describe('loading', () => {
  it('shows the picture size and every layer, front first, with plain kind names', async () => {
    await ready();
    expect(screen.getByText('1280 × 720')).toBeTruthy();
    const rows = screen.getAllByRole('listitem').map((r) => r.textContent);
    expect(rows[0]).toContain('Person');
    expect(rows[0]).toContain('cut-out'); // the server sends "Cutout"; the editor reads it
    expect(rows[1]).toContain('Original');
    expect(rows[1]).toContain('original');
    expect(screen.getByRole('status').textContent).toBe('All changes saved');
  });

  it('says so, with a retry, when the document cannot be opened', async () => {
    api.getImageDocument.mockRejectedValueOnce(new ApiError('No such picture.', 404, [], { message: 'No such picture.' }));
    mount();
    expect(await screen.findByText('No such picture.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await screen.findByRole('application', { name: 'Thumbnail canvas' });
  });
});

describe('editing is local first, then written with the revision', () => {
  it('hides a layer, then sends one PATCH carrying baseRevision', async () => {
    api.updateImageLayer.mockResolvedValue(CUTOUT);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Hide Person' }));
    expect(screen.getByRole('status').textContent).toBe('Unsaved changes');
    await waitFor(() => expect(api.updateImageLayer).toHaveBeenCalledTimes(1), { timeout: 3000 });
    expect(api.updateImageLayer).toHaveBeenCalledWith('p', 'd', 'cut', { visible: false, baseRevision: 3 });
    await waitFor(() => expect(screen.getByRole('status').textContent).toBe('All changes saved'));
  });

  it('writes nothing when an edit is undone before the write goes out', async () => {
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Hide Person' }));
    fireEvent.click(screen.getByRole('button', { name: 'Undo' }));
    await wait(1000);
    expect(api.updateImageLayer).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Hide Person' })).toBeTruthy();
  });

  it('replays an undo after the first write: the reverse PATCH goes out', async () => {
    api.updateImageLayer.mockResolvedValue(CUTOUT);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Lock Person' }));
    await waitFor(() => expect(api.updateImageLayer).toHaveBeenCalledTimes(1), { timeout: 3000 });
    await waitFor(() => expect(screen.getByRole('status').textContent).toBe('All changes saved'));
    fireEvent.click(screen.getByRole('button', { name: 'Undo' }));
    await waitFor(() => expect(api.updateImageLayer).toHaveBeenCalledTimes(2), { timeout: 3000 });
    expect(api.updateImageLayer.mock.calls[1][3]).toMatchObject({ locked: false });
  });

  it('moves a layer with the arrow button as a 1-based position', async () => {
    api.updateImageLayer.mockResolvedValue(SOURCE);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Move Original forward' }));
    await waitFor(() => expect(api.updateImageLayer).toHaveBeenCalled(), { timeout: 3000 });
    // "Original forward" and "Person back" are the same result; the diff sends the shorter move
    expect(api.updateImageLayer).toHaveBeenCalledWith('p', 'd', 'cut', { zOrder: 1, baseRevision: 3 });
  });

  it('reorders by dragging a row onto another', async () => {
    api.updateImageLayer.mockResolvedValue(SOURCE);
    await ready();
    const source = screen.getByTestId('layer-row-src');
    const cutout = screen.getByTestId('layer-row-cut');
    fireEvent.dragStart(source);
    fireEvent.dragOver(cutout);
    fireEvent.drop(cutout);
    await waitFor(() => expect(api.updateImageLayer).toHaveBeenCalled(), { timeout: 3000 });
    // "Original forward" and "Person back" are the same result; the diff sends the shorter move
    expect(api.updateImageLayer).toHaveBeenCalledWith('p', 'd', 'cut', { zOrder: 1, baseRevision: 3 });
  });

  it('reloads with a plain notice when the server says the picture changed elsewhere', async () => {
    api.updateImageLayer.mockRejectedValue(new ApiError('stale', 409, [], { code: 'REVISION_CONFLICT', message: 'stale' }));
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Hide Person' }));
    expect(await screen.findByText(/changed somewhere else/, undefined, { timeout: 3000 })).toBeTruthy();
    expect(api.getImageDocument).toHaveBeenCalledTimes(2);
    expect(screen.getByRole('button', { name: 'Hide Person' })).toBeTruthy(); // the local edit was dropped
  });

  it('sends a shape as both sides at the current size, with the revision', async () => {
    api.updateImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT], 4));
    await ready();
    openTab('Canvas');
    fireEvent.click(screen.getByRole('button', { name: '1:1 Square' }));
    fireEvent.click(await screen.findByTestId('canvas-refit-keep'));
    await waitFor(() => expect(api.updateImageDocument).toHaveBeenCalled(), { timeout: 3000 });
    // 1280 × 720 is 16:9 at 720p, so the square keeps 720p
    expect(api.updateImageDocument.mock.calls[0].slice(0, 3)).toEqual([
      'p',
      'd',
      { canvasWidth: 720, canvasHeight: 720, baseRevision: 3 },
    ]);
    expect(screen.getByText('720 × 720')).toBeTruthy();
  });

  it('changes the size within the shape, and takes an exact size without truncating the button', async () => {
    api.updateImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT], 4));
    await ready();
    openTab('Canvas');
    fireEvent.click(screen.getByRole('radio', { name: '4K' }));
    expect(screen.getByText('3840 × 2160')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Width'), { target: { value: '1000' } });
    fireEvent.change(screen.getByLabelText('Height'), { target: { value: '500' } });
    fireEvent.click(screen.getByRole('button', { name: 'Use 1000 × 500' }));
    fireEvent.click(await screen.findByTestId('canvas-refit-keep'));
    expect(await screen.findByText('1000 × 500')).toBeTruthy();
    await waitFor(() => expect(api.updateImageDocument).toHaveBeenCalled(), { timeout: 3000 });
  });
});

describe('R14.1 / R14.5 credits and availability on the paid tools', () => {
  const caps = (estimate: unknown, extra: Record<string, unknown> = {}) => ({
    operation: 'upscale',
    provider: { kind: 'FalAi', model: 'm' },
    takesPrompt: false,
    maxPromptLength: 0,
    expand: null,
    estimate,
    canConfigure: false,
    ...extra,
  });
  const openUpscale = async () => {
    openTab('AI');
    fireEvent.click(screen.getByRole('button', { name: /Choose an AI tool/ }));
    const dialog = await screen.findByRole('dialog', { name: 'Choose an AI tool' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Upscale' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Choose an AI tool' })).toBeNull());
  };

  it('shows "about N credits" before the run', async () => {
    api.getOperationCapabilities.mockResolvedValue(caps({ billingEnabled: true, credits: 4.5, balanceCredits: 10, affordable: true }));
    await ready();
    await openUpscale();
    expect((await screen.findByTestId('credits-upscale-estimate')).textContent).toContain('about 4.5 credits');
  });

  it('disables the run with an Add credits link when the balance is too low', async () => {
    api.getOperationCapabilities.mockResolvedValue(caps({ billingEnabled: true, credits: 4.5, balanceCredits: 1, affordable: false }));
    await ready();
    await openUpscale();
    expect(await screen.findByRole('link', { name: 'Add credits' })).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Upscale the picture' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('shows no price when billing is off', async () => {
    api.getOperationCapabilities.mockResolvedValue(caps({ billingEnabled: false, affordable: true }));
    await ready();
    await openUpscale();
    await screen.findByRole('button', { name: 'Upscale the picture' });
    expect(screen.queryByTestId('credits-upscale')).toBeNull();
    expect(screen.queryByText(/0 credits/)).toBeNull();
  });

  it('tells a member to ask an admin, and offers an admin the setup link', async () => {
    api.getOperationCapabilities.mockRejectedValue(
      new ApiError('none', 409, [], { code: 'IMAGE_EDIT_PROVIDER_UNAVAILABLE', reason: 'NO_PROVIDER', canConfigure: false }),
    );
    await ready();
    await openUpscale();
    expect(await screen.findByText('Ask your workspace admin to set up picture tools.')).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Set up picture tools' })).toBeNull();
    cleanup();
    api.getOperationCapabilities.mockRejectedValue(
      new ApiError('none', 409, [], { code: 'IMAGE_EDIT_PROVIDER_UNAVAILABLE', reason: 'NO_PROVIDER', canConfigure: true }),
    );
    await ready();
    await openUpscale();
    expect(await screen.findByRole('link', { name: 'Set up picture tools' })).toBeTruthy();
  });
});

describe('R13.3 changing the shape refits the layers', () => {
  it('Fit what is there moves every layer with the frame and one undo puts it all back', async () => {
    api.updateImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT], 4));
    api.updateImageLayer.mockResolvedValue(SOURCE);
    await ready();
    openTab('Canvas');
    fireEvent.click(screen.getByRole('button', { name: '1:1 Square' }));
    fireEvent.click(await screen.findByTestId('canvas-refit-fit'));
    expect(await screen.findByText('720 × 720')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /^Undo/ }));
    expect(await screen.findByText('1280 × 720')).toBeTruthy();
  });
});

describe('R13.4 upload an image as a layer', () => {
  it('uploads into the project files, then adds that file as a layer', async () => {
    files.uploadFileWithProgress.mockResolvedValue({ id: 'up-1', originalFileName: 'logo.png' });
    api.createLayerFromFile.mockResolvedValue(baseLayer({ id: 'logo-layer', label: 'logo', zOrder: 3 }));
    await ready();
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [new File(['x'], 'logo.png', { type: 'image/png' })] } });
    await waitFor(() =>
      expect(api.createLayerFromFile).toHaveBeenCalledWith('p', 'd', { projectFileId: 'up-1', role: 'overlay' }),
    );
    expect(files.uploadFileWithProgress).toHaveBeenCalled();
  });

  it('says plainly when the upload fails', async () => {
    files.uploadFileWithProgress.mockRejectedValue(new Error('Too big'));
    await ready();
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [new File(['x'], 'logo.png', { type: 'image/png' })] } });
    expect(await screen.findByText(/could not be uploaded: Too big/)).toBeTruthy();
    expect(api.createLayerFromFile).not.toHaveBeenCalled();
  });
});

describe('the canvas is a workspace', () => {

  it('has zoom controls and a hand tool, and no reveal preview anywhere', async () => {
    await ready();
    expect(screen.getByRole('button', { name: 'Fit the picture on screen' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Zoom in' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Move around the canvas' })).toBeTruthy();
    openTab('Canvas');
    expect(screen.queryByRole('button', { name: /reveal/i })).toBeNull();
    expect(screen.queryByText(/reveal/i)).toBeNull();
  });
});

describe('AI operations', () => {
  const generated = baseLayer({ id: 'up', label: 'Upscale', kind: 'Generated' as never, role: 'overlay', zOrder: 3 });

  const pick = async (title: string) => {
    openTab('AI');
    fireEvent.click(screen.getByRole('button', { name: /Choose an AI tool|Change tool/ }));
    const dialog = await screen.findByRole('dialog', { name: 'Choose an AI tool' });
    fireEvent.click(within(dialog).getByRole('button', { name: title }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Choose an AI tool' })).toBeNull());
  };

  it('starts from one compact entry: a picker of every tool, each with a sentence about what it does', async () => {
    await ready();
    openTab('AI');
    expect(screen.queryByRole('button', { name: 'Upscale the picture' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: /Choose an AI tool/ }));
    const dialog = await screen.findByRole('dialog', { name: 'Choose an AI tool' });
    for (const title of ['Cut out a subject', 'Remove background', 'New background', 'Erase', 'Fill', 'Edit with words', 'Expand', 'Upscale', 'Relight', 'Create a picture']) {
      expect(within(dialog).getByRole('button', { name: title })).toBeTruthy();
    }
    expect(within(dialog).getByText('Make the picture sharper and larger.')).toBeTruthy();
  });

  it('shows only the chosen tool, runs it on the selected layer and adopts the new layer from the server', async () => {
    api.runImageOperation.mockResolvedValue({
      layers: [generated],
      operation: 'upscale',
      provider: { kind: 'FalAi', model: 'fal-ai/esrgan' },
      usage: { unit: 'images', quantity: 1 },
      revision: 4,
    });
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Original' }));
    await pick('Upscale');
    const tool = within(screen.getByTestId('ai-tool-upscale'));
    expect(screen.queryByRole('button', { name: 'Erase the painted area' })).toBeNull();
    // the server's document after the operation, which the editor reads once it has run
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT, generated], 4));
    fireEvent.click(tool.getByRole('button', { name: 'Upscale the picture' }));
    await waitFor(() => expect(api.runImageOperation).toHaveBeenCalled());
    expect(api.runImageOperation).toHaveBeenCalledWith('p', 'd', { operation: 'upscale', layerId: 'src', params: { factor: 2 } });
    expect((await screen.findByTestId('result-bar-changed')).textContent).toMatch(/added a new layer/);
    expect(screen.queryByText(/FalAi/)).toBeNull();
    openTab('Layers');
    expect(await screen.findByRole('button', { name: 'Select Upscale' })).toBeTruthy();
  });

  it('tells the user in plain words when a cut-out\'s transparency could not be kept', async () => {
    api.runImageOperation.mockResolvedValue({
      layers: [generated],
      operation: 'upscale',
      provider: { kind: 'FalAi', model: 'fal-ai/seedvr/upscale/image', capability: 'ImageUpscale' },
      usage: { unit: 'images', quantity: 1 },
      revision: 4,
      notice: 'The AI returned a picture with a different shape, so the transparent background could not be carried over.',
    });
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Person' }));
    await pick('Upscale');
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT, generated], 4));
    fireEvent.click(within(screen.getByTestId('ai-tool-upscale')).getByRole('button', { name: 'Upscale the picture' }));
    expect(await screen.findByText(/Upscale done\. The AI returned a picture with a different shape/)).toBeTruthy();
  });

  it('disables a tool with the server\'s own reason when no provider is set up', async () => {
    api.runImageOperation.mockRejectedValue(
      new ApiError('No image editing provider is set up for this workspace.', 409, [], {
        code: 'IMAGE_EDIT_PROVIDER_UNAVAILABLE',
        message: 'No image editing provider is set up for this workspace.',
      }),
    );
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Original' }));
    await pick('Upscale');
    fireEvent.click(within(screen.getByTestId('ai-tool-upscale')).getByRole('button', { name: 'Upscale the picture' }));
    const tool = within(screen.getByTestId('ai-tool-upscale'));
    expect(await tool.findByText('Not available here')).toBeTruthy();
    expect(tool.getByText('No image editing provider is set up for this workspace.')).toBeTruthy();
    expect(tool.queryByRole('button', { name: 'Upscale the picture' })).toBeNull();
    // the picker says so too; other tools are unaffected
    fireEvent.click(screen.getByRole('button', { name: 'Change tool' }));
    const dialog = await screen.findByRole('dialog', { name: 'Choose an AI tool' });
    expect(within(within(dialog).getByTestId('ai-tool-option-upscale')).getByText('Not set up in this workspace')).toBeTruthy();
    expect(within(within(dialog).getByTestId('ai-tool-option-removeBackground')).queryByText('Not set up in this workspace')).toBeNull();
  });

  it('edits against a picture uploaded from the device, sending it as the reference', async () => {
    files.uploadFile.mockResolvedValue({ id: 'ref-9', originalFileName: 'dana.jpg' });
    api.runImageOperation.mockResolvedValue({
      layers: [generated],
      operation: 'instruct',
      provider: { kind: 'OpenAi', model: 'gpt-image-1' },
      usage: { unit: 'images', quantity: 1 },
      revision: 4,
    });
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Original' }));
    await pick('Edit with words');
    const tool = within(screen.getByTestId('ai-tool-instruct'));
    fireEvent.change(tool.getByLabelText('What to change'), { target: { value: 'replace the face with this person' } });
    // The upload goes through the project's ordinary file endpoint and becomes an ordinary file.
    fireEvent.change(tool.getByLabelText('Upload a reference picture'), {
      target: { files: [new File(['x'], 'dana.jpg', { type: 'image/jpeg' })] },
    });
    await waitFor(() => expect(files.uploadFile).toHaveBeenCalledWith('p', expect.any(File)));
    expect(tool.getByTestId('reference-picture').textContent).toContain('1st: dana.jpg');

    api.getImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT, generated], 4));
    fireEvent.click(tool.getByRole('button', { name: 'Make the change' }));
    await waitFor(() => expect(api.runImageOperation).toHaveBeenCalled());
    expect(api.runImageOperation).toHaveBeenCalledWith('p', 'd', {
      operation: 'instruct',
      layerId: 'src',
      prompt: 'replace the face with this person',
      referenceProjectFileIds: ['ref-9'],
    });
  });

  it('sends several reference pictures in the order they were added, and drops the key when the list is emptied', async () => {
    files.uploadFile
      .mockResolvedValueOnce({ id: 'ref-9', originalFileName: 'dana.jpg' })
      .mockResolvedValueOnce({ id: 'ref-10', originalFileName: 'badge.png' });
    api.runImageOperation.mockResolvedValue({
      layers: [generated],
      operation: 'instruct',
      provider: { kind: 'OpenAi', model: 'gpt-image-1' },
      revision: 5,
    });
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Original' }));
    await pick('Edit with words');
    const tool = within(screen.getByTestId('ai-tool-instruct'));
    fireEvent.change(tool.getByLabelText('What to change'), {
      target: { value: 'put the badge from the second picture on her jacket' },
    });
    for (const file of ['dana.jpg', 'badge.png']) {
      fireEvent.change(tool.getByLabelText('Upload a reference picture'), {
        target: { files: [new File(['x'], file, { type: 'image/jpeg' })] },
      });
      await waitFor(() => expect(files.uploadFile).toHaveBeenCalledWith('p', expect.any(File)));
    }
    await waitFor(() => expect(files.uploadFile).toHaveBeenCalledTimes(2));
    const reference = tool.getByTestId('reference-picture');
    expect(within(reference).getByText('1st:').parentElement?.textContent).toContain('dana.jpg');
    expect(within(reference).getByText('2nd:').parentElement?.textContent).toContain('badge.png');

    api.getImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT, generated], 5));
    fireEvent.click(tool.getByRole('button', { name: 'Make the change' }));
    await waitFor(() => expect(api.runImageOperation).toHaveBeenCalled());
    expect(api.runImageOperation).toHaveBeenLastCalledWith('p', 'd', {
      operation: 'instruct',
      layerId: 'src',
      prompt: 'put the badge from the second picture on her jacket',
      referenceProjectFileIds: ['ref-9', 'ref-10'],
    });

    // Remove both: the body then carries no reference key at all.
    fireEvent.click(tool.getByRole('button', { name: 'Remove dana.jpg' }));
    fireEvent.click(tool.getByRole('button', { name: 'Remove badge.png' }));
    expect(tool.getByTestId('reference-picture')).toBeTruthy();
    expect(within(reference).queryByText('1st:')).toBeNull();
    // The first run left its own new layer selected, so only the body's shape is asserted here.
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT, generated], 6));
    fireEvent.click(tool.getByRole('button', { name: 'Make the change' }));
    await waitFor(() => expect(api.runImageOperation).toHaveBeenCalledTimes(2));
    const emptied = api.runImageOperation.mock.calls.at(-1)?.[2] as Record<string, unknown>;
    expect(emptied).toMatchObject({ operation: 'instruct', prompt: 'put the badge from the second picture on her jacket' });
    expect('referenceProjectFileIds' in emptied).toBe(false);
  });

  it('sends no reference picture at all when none is chosen', async () => {
    api.runImageOperation.mockResolvedValue({
      layers: [generated],
      operation: 'instruct',
      provider: { kind: 'OpenAi', model: 'gpt-image-1' },
      revision: 4,
    });
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Original' }));
    await pick('Edit with words');
    const tool = within(screen.getByTestId('ai-tool-instruct'));
    fireEvent.change(tool.getByLabelText('What to change'), { target: { value: 'make the shirt red' } });

    api.getImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT, generated], 4));
    fireEvent.click(tool.getByRole('button', { name: 'Make the change' }));
    await waitFor(() => expect(api.runImageOperation).toHaveBeenCalled());
    expect(api.runImageOperation).toHaveBeenCalledWith('p', 'd', {
      operation: 'instruct',
      layerId: 'src',
      prompt: 'make the shirt red',
    });
  });

  it("shows the server's own reason when it refuses the reference pictures, and leaves the tool usable", async () => {
    files.uploadFile.mockResolvedValue({ id: 'ref-9', originalFileName: 'dana.jpg' });
    const refusal = 'At most 4 reference pictures are allowed.';
    api.runImageOperation.mockRejectedValue(
      new ApiError(refusal, 400, [], { code: 'TOO_MANY_REFERENCE_PICTURES', message: refusal }),
    );
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Original' }));
    await pick('Edit with words');
    const tool = within(screen.getByTestId('ai-tool-instruct'));
    fireEvent.change(tool.getByLabelText('What to change'), { target: { value: 'replace the face with this person' } });
    fireEvent.change(tool.getByLabelText('Upload a reference picture'), {
      target: { files: [new File(['x'], 'dana.jpg', { type: 'image/jpeg' })] },
    });
    await waitFor(() => expect(files.uploadFile).toHaveBeenCalled());

    fireEvent.click(tool.getByRole('button', { name: 'Make the change' }));
    expect(await screen.findByText(refusal)).toBeTruthy();
    // A refusal about what was sent, not "no provider here": nothing is disabled and the tool stays usable.
    expect(tool.queryByText('Not available here')).toBeNull();
    expect(tool.getByRole('button', { name: 'Make the change' })).toBeTruthy();
    const reference = tool.getByTestId('reference-picture');
    expect(within(reference).getByRole('button', { name: 'Remove dana.jpg' })).toBeTruthy();
    expect((within(reference).getByRole('combobox', { name: 'Add a reference picture' }) as HTMLInputElement).disabled).toBe(false);
  });

  const FLUX_EXPAND = {
    operation: 'expand',
    provider: { kind: 'FalAi', model: 'fal-ai/flux-2-pro/outpaint', capability: 'ImageExpand' },
    takesPrompt: false,
    maxPromptLength: 0,
    expand: { geometry: 'margins', maxMarginPx: 2048, maxOutputPixels: 4_000_000, maxInputPixels: 4_000_000, basis: null },
  };

  it('expands to a shape, offers no description for a service that takes none, and sends the displayed-size copy the server asks for', async () => {
    api.getOperationCapabilities.mockResolvedValue(FLUX_EXPAND);
    const notice =
      'This picture was larger than the AI service accepts, so it used the picture at the size it appears in your design (640\u00d7360).';
    api.runImageOperation
      .mockRejectedValueOnce(
        new ApiError(notice, 422, [], { code: 'SOURCE_TOO_LARGE', message: notice, width: 640, height: 360, source: 'displayed' }),
      )
      .mockResolvedValueOnce({
        layers: [generated],
        operation: 'expand',
        provider: { kind: 'FalAi', model: 'fal-ai/flux-2-pro/outpaint', capability: 'ImageExpand' },
        usage: { unit: 'images', quantity: 1 },
        revision: 4,
        notice,
      });
    const copy = new Blob(['copy'], { type: 'image/png' });
    dom.renderScaledCopy.mockResolvedValue(copy);
    api.uploadEditorAsset.mockResolvedValue('source-key');

    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Original' }));
    await pick('Expand');
    const tool = within(screen.getByTestId('ai-tool-expand'));
    await tool.findByText(/does not take a description/);
    expect(tool.queryByRole('textbox', { name: /What is out there/ })).toBeNull();
    // the picture is 16:9 like the canvas, so the first shape it does not already have is offered
    expect((tool.getByLabelText('Shape') as HTMLInputElement).value).toMatch(/^9:16/);
    expect(tool.getByText(/Adds picture on the left and right|Adds picture above and below/)).toBeTruthy();

    api.getImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT, generated], 4));
    fireEvent.click(tool.getByRole('button', { name: 'Expand the picture' }));

    await waitFor(() => expect(api.runImageOperation).toHaveBeenCalledTimes(2));
    expect(api.runImageOperation).toHaveBeenNthCalledWith(1, 'p', 'd', { operation: 'expand', layerId: 'src', params: { aspect: '9:16' } });
    expect(dom.renderScaledCopy).toHaveBeenCalledWith(expect.anything(), 640, 360);
    expect(api.uploadEditorAsset).toHaveBeenCalledWith('p', 'd', 'source', copy, 'image/png');
    expect(api.runImageOperation).toHaveBeenNthCalledWith(2, 'p', 'd', {
      operation: 'expand',
      layerId: 'src',
      params: { aspect: '9:16' },
      sourceKey: 'source-key',
    });
    expect(await screen.findByText(/Expand done\. This picture was larger than the AI service accepts, so it used the picture at the size it appears in your design \(640×360\)\./)).toBeTruthy();
  });

  it('keeps pixels per side within what the expand service adds to one side, and says so', async () => {
    api.getOperationCapabilities.mockResolvedValue(FLUX_EXPAND);
    api.runImageOperation.mockResolvedValue({
      layers: [generated],
      operation: 'expand',
      provider: { kind: 'FalAi', model: 'fal-ai/flux-2-pro/outpaint' },
      revision: 4,
    });
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Original' }));
    await pick('Expand');
    const tool = within(screen.getByTestId('ai-tool-expand'));
    await tool.findByText(/does not take a description/);
    fireEvent.click(tool.getByText('Pixels per side'));
    expect(await tool.findByText(/at most 2,048 per side \(the most this AI service adds to one side\)/)).toBeTruthy();
    const top = tool.getByRole('textbox', { name: 'Top (px)' });
    fireEvent.change(top, { target: { value: '5000' } });
    fireEvent.blur(top);
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT, generated], 4));
    fireEvent.click(tool.getByRole('button', { name: 'Expand the picture' }));
    await waitFor(() => expect(api.runImageOperation).toHaveBeenCalled());
    expect(api.runImageOperation).toHaveBeenCalledWith('p', 'd', { operation: 'expand', layerId: 'src', params: { top: 2048 } });
  });

  it('says why a button is disabled instead of leaving the user guessing', async () => {
    await ready();
    await pick('Upscale');
    const upscale = within(screen.getByTestId('ai-tool-upscale'));
    expect(upscale.getByText('Select a layer first.')).toBeTruthy();
    expect((upscale.getByRole('button', { name: 'Upscale the picture' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('arms the brush on the original when no layer is selected, and says what is missing next (PE-044a)', async () => {
    await ready();
    await pick('Erase');
    const tool = within(screen.getByTestId('ai-tool-erase'));
    // the brush is never left with nothing to paint on: the original was selected for it
    expect(tool.getByText(/Paint over the part of "Original" you want to change/)).toBeTruthy();
    expect(tool.getByText('Paint over the area on the picture first.')).toBeTruthy();
    expect((tool.getByRole('button', { name: 'Erase the painted area' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('uploads the brush mask first, then runs erase with its key', async () => {
    api.uploadEditorAsset.mockResolvedValue('masks/m-1.png');
    api.runImageOperation.mockResolvedValue({
      layers: [baseLayer({ id: 'er', label: 'Erase', kind: 'Generated' as never, zOrder: 3 })],
      operation: 'erase',
      provider: { kind: 'FalAi', model: 'fal-ai/lama' },
    });
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Original' }));
    // the brush belongs to Erase and Fill: arming it opens the AI tab on Erase
    fireEvent.click(screen.getByRole('button', { name: 'Brush mask' }));
    expect(screen.getByTestId('ai-tool-erase')).toBeTruthy();
    const canvas = screen.getByRole('img', { name: 'Your picture' });
    fireEvent.pointerDown(canvas, { button: 0, clientX: 100, clientY: 100, pointerId: 1 });
    fireEvent.pointerMove(canvas, { clientX: 140, clientY: 120, pointerId: 1 });
    fireEvent.pointerUp(canvas, { clientX: 140, clientY: 120, pointerId: 1 });
    expect(screen.getByText(/Mask ready/)).toBeTruthy();
    fireEvent.click(within(screen.getByTestId('ai-tool-erase')).getByRole('button', { name: 'Erase the painted area' }));
    await waitFor(() => expect(api.runImageOperation).toHaveBeenCalled());
    expect(api.uploadEditorAsset).toHaveBeenCalledWith('p', 'd', 'mask', expect.any(Blob));
    expect(api.runImageOperation).toHaveBeenCalledWith('p', 'd', { operation: 'erase', layerId: 'src', maskKey: 'masks/m-1.png' });
    expect(api.uploadEditorAsset.mock.invocationCallOrder[0]).toBeLessThan(api.runImageOperation.mock.invocationCallOrder[0]);
  });

  it('refuses a pixel operation on a text layer', async () => {
    const text = baseLayer({
      id: 'txt',
      label: 'Title',
      kind: 'Text' as never,
      zOrder: 3,
      content: {
        text: 'Hi', fontFamily: 'Anton', fontWeight: 400, fontSize: 80, lineHeight: 1, letterSpacing: 0, align: 'left',
        color: '#fff', stroke: null, shadow: null, maxWidth: null, uppercase: false,
      },
    });
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, text]));
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Title' }));
    await pick('Upscale');
    expect(within(screen.getByTestId('ai-tool-upscale')).getByText(/Text layers have no pixels/)).toBeTruthy();
  });
});

describe('subject, text, images, delete and export', () => {
  it('cuts out a subject from a click on the picture, in the original layer\'s pixels when nothing is selected', async () => {
    api.segmentSubjects.mockResolvedValue([baseLayer({ id: 'p1', label: 'the person', kind: 'Cutout' as never, zOrder: 3 })]);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Cut out a subject' }));
    expect(screen.getByTestId('ai-tool-subject')).toBeTruthy();
    const canvas = screen.getByRole('img', { name: 'Your picture' });
    fireEvent.pointerDown(canvas, { button: 0, clientX: 300, clientY: 200, pointerId: 1 });
    fireEvent.pointerUp(canvas, { clientX: 300, clientY: 200, pointerId: 1 });
    fireEvent.click(screen.getByRole('button', { name: 'Cut out' }));
    await waitFor(() => expect(api.segmentSubjects).toHaveBeenCalled());
    expect(api.segmentSubjects).toHaveBeenCalledWith('p', 'd', { layerId: 'src', pointPrompts: [{ x: 300, y: 200, label: 1 }] }, expect.any(Function));
  });

  it('cuts a subject out of the SELECTED picture layer, whatever kind it is', async () => {
    api.segmentSubjects.mockResolvedValue([baseLayer({ id: 'p2', label: 'the car', kind: 'Cutout' as never, zOrder: 3 })]);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Person' }));
    fireEvent.click(screen.getByRole('button', { name: 'Cut out a subject' }));
    expect(screen.getByText('Person', { selector: 'b' })).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Or describe it'), { target: { value: 'the car' } });
    fireEvent.click(screen.getByRole('button', { name: 'Cut out' }));
    await waitFor(() => expect(api.segmentSubjects).toHaveBeenCalled());
    expect(api.segmentSubjects).toHaveBeenCalledWith('p', 'd', { layerId: 'cut', prompt: 'the car' }, expect.any(Function));
  });

  it('can cut a subject out even after the original layer was deleted, from the selected layer', async () => {
    api.getImageDocument.mockResolvedValue(docDto([CUTOUT], 3));
    api.segmentSubjects.mockResolvedValue([baseLayer({ id: 'p3', label: 'x', kind: 'Cutout' as never, zOrder: 2 })]);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Cut out a subject' }));
    expect(screen.getByText('Select a picture layer to cut a subject out of.')).toBeTruthy();
    openTab('Layers');
    fireEvent.click(screen.getByRole('button', { name: 'Select Person' }));
    openTab('AI');
    fireEvent.change(screen.getByLabelText('Or describe it'), { target: { value: 'x' } });
    fireEvent.click(screen.getByRole('button', { name: 'Cut out' }));
    await waitFor(() => expect(api.segmentSubjects).toHaveBeenCalledWith('p', 'd', { layerId: 'cut', prompt: 'x' }, expect.any(Function)));
  });

  it('turns a drag into a box prompt and a text field into a prompt, and asks for several masks on request', async () => {
    api.segmentSubjects.mockResolvedValue([
      baseLayer({ id: 'a', label: 'A', kind: 'Cutout' as never, zOrder: 3 }),
      baseLayer({ id: 'b', label: 'B', kind: 'Cutout' as never, zOrder: 4 }),
    ]);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Cut out a subject' }));
    const canvas = screen.getByRole('img', { name: 'Your picture' });
    fireEvent.pointerDown(canvas, { button: 0, clientX: 400, clientY: 300, pointerId: 1 });
    fireEvent.pointerMove(canvas, { clientX: 100, clientY: 50, pointerId: 1 });
    fireEvent.pointerUp(canvas, { clientX: 100, clientY: 50, pointerId: 1 });
    fireEvent.change(screen.getByLabelText('Or describe it'), { target: { value: 'the dog' } });
    fireEvent.click(screen.getByRole('switch', { name: /up to 4 options/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Cut out' }));
    await waitFor(() => expect(api.segmentSubjects).toHaveBeenCalled());
    expect(api.segmentSubjects).toHaveBeenCalledWith('p', 'd', {
      layerId: 'src',
      prompt: 'the dog',
      boxPrompts: [{ xMin: 100, yMin: 50, xMax: 400, yMax: 300 }],
      returnMultipleMasks: true,
      maxMasks: 4,
    }, expect.any(Function));
    expect(await screen.findByText('Added 2 cut-out layers.')).toBeTruthy();
  });

  it('adds a text layer with the brand font by default', async () => {
    const text = baseLayer({ id: 'txt', label: 'Text', kind: 'Text' as never, zOrder: 3 });
    api.createTextLayer.mockResolvedValue(text);
    await ready();
    openTab('Text');
    fireEvent.click(screen.getByRole('button', { name: 'Add text' }));
    await waitFor(() => expect(api.createTextLayer).toHaveBeenCalled());
    const [, , request] = api.createTextLayer.mock.calls[0];
    expect(request.content).toMatchObject({ fontFamily: 'Chakra Petch', fontWeight: 700, fontSize: 96 });
    expect(request.transform).toMatchObject({ x: 77, y: 274 });
  });

  it('with the interface in Italian the text layer is still sent to the API with the English name "Text" (stored data)', async () => {
    const text = baseLayer({ id: 'txt', label: 'Text', kind: 'Text' as never, zOrder: 3 });
    api.createTextLayer.mockResolvedValue(text);
    mountIt();
    await screen.findByRole('application', { name: itEditor.canvas.label });
    fireEvent.click(screen.getByRole('tab', { name: itEditor.editor.dock.text }));
    fireEvent.click(screen.getByRole('button', { name: itText.text.add }));
    await waitFor(() => expect(api.createTextLayer).toHaveBeenCalled());
    const [, , request] = api.createTextLayer.mock.calls[0];
    expect(request.label).toBe('Text');
  });

  it('fits a picture larger than the canvas inside it, centred, in the same step as adding it', async () => {
    const big = baseLayer({ id: 'logo-layer', label: 'logo.png', kind: 'Image' as never, role: 'overlay', zOrder: 3, width: 2560, height: 2880 });
    api.createLayerFromFile.mockResolvedValue(big);
    api.updateImageLayer.mockResolvedValue(big);
    await ready();
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT, big], 4));
    fireEvent.click(screen.getByPlaceholderText('Pick an image'));
    // the dropdown is still mid-transition in jsdom, so the option counts as hidden
    fireEvent.click(await screen.findByRole('option', { name: 'logo.png', hidden: true }));
    fireEvent.click(screen.getByRole('button', { name: 'Add to the picture' }));
    await waitFor(() => expect(api.createLayerFromFile).toHaveBeenCalledWith('p', 'd', { projectFileId: 'logo', role: 'overlay' }));
    await waitFor(() => expect(api.updateImageLayer).toHaveBeenCalled(), { timeout: 3000 });
    // 2560 × 2880 into 1280 × 720: scale 0.25 (the height decides), centred horizontally
    expect(api.updateImageLayer).toHaveBeenCalledWith('p', 'd', 'logo-layer', {
      transform: { x: 320, y: 0, scale: 0.25, rotation: 0, flipX: false, flipY: false },
      baseRevision: 4,
    });
  });

  it('deletes a layer only after confirmation and clears undo', async () => {
    api.deleteImageLayer.mockResolvedValue(undefined);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Hide Person' }));
    expect((screen.getByRole('button', { name: 'Undo' }) as HTMLButtonElement).disabled).toBe(false);
    api.getImageDocument.mockResolvedValue(docDto([SOURCE], 5));
    fireEvent.click(screen.getByRole('button', { name: 'Delete Person' }));
    expect(api.deleteImageLayer).not.toHaveBeenCalled();
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(api.deleteImageLayer).toHaveBeenCalledWith('p', 'd', 'cut'), { timeout: 3000 });
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Select Person' })).toBeNull());
    expect((screen.getByRole('button', { name: 'Undo' }) as HTMLButtonElement).disabled).toBe(true);
  });

  const exportResult = (name: string) => ({
    export: { id: 'x', documentId: 'd', projectFileId: 'pf', fileUrl: '/f', width: 1280, height: 720, revision: 3, createdAt: '' },
    projectFile: { id: 'pf', originalFileName: name },
    projectThumbnailApplied: false,
  });

  const openExport = async () => {
    fireEvent.click(screen.getByRole('button', { name: 'Export' }));
    return within(await screen.findByRole('dialog', { name: 'Export your picture' }));
  };

  it('exports a PNG to the project: renders at canvas size, uploads with a fresh ticket, then registers the file', async () => {
    const png = new Blob(['png'], { type: 'image/png' });
    dom.renderExport.mockResolvedValue(png);
    api.uploadEditorAsset.mockResolvedValue('exports/e-1.png');
    api.createImageExport.mockResolvedValue(exportResult('cover.png'));
    await ready();
    const dialog = await openExport();
    fireEvent.change(dialog.getByLabelText('File name'), { target: { value: 'cover' } });
    const button = dialog.getByRole('button', { name: 'Save to project' }) as HTMLButtonElement;
    await waitFor(() => expect(button.disabled).toBe(false));
    fireEvent.click(button);
    await waitFor(() => expect(api.createImageExport).toHaveBeenCalled());
    expect(dom.renderExport).toHaveBeenCalledWith('png', expect.objectContaining({ canvasWidth: 1280, canvasHeight: 720 }), expect.anything(), 0.9);
    expect(api.uploadEditorAsset).toHaveBeenCalledWith('p', 'd', 'export', png, 'image/png');
    expect(api.createImageExport).toHaveBeenCalledWith('p', 'd', { key: 'exports/e-1.png', width: 1280, height: 720, fileName: 'cover.png' });
    expect(await dialog.findByText(/Saved as cover.png in your project's files/)).toBeTruthy();
    // the server does not apply an export as the project thumbnail yet, so the UI promises nothing of the kind
    expect(screen.queryByText(/project's thumbnail/i)).toBeNull();
  });

  it('exports a layered Photoshop file with its own type and extension', async () => {
    const psd = new Blob(['8BPS'], { type: 'image/vnd.adobe.photoshop' });
    dom.renderExport.mockResolvedValue(psd);
    api.uploadEditorAsset.mockResolvedValue('exports/e-2.psd');
    api.createImageExport.mockResolvedValue(exportResult('Cover.psd'));
    await ready();
    const dialog = await openExport();
    fireEvent.click(dialog.getByRole('button', { name: /More formats/ }));
    fireEvent.click(await dialog.findByRole('radio', { name: /Photoshop \(PSD\)/ }));
    expect(dialog.getByText(/Saved as Cover\.psd/)).toBeTruthy();
    fireEvent.click(dialog.getByRole('button', { name: 'Save to project' }));
    await waitFor(() => expect(api.createImageExport).toHaveBeenCalled());
    expect(dom.renderExport.mock.calls.some((c) => c[0] === 'psd')).toBe(true); // not calls[0]: a size estimate for the default format can render first under load
    expect(api.uploadEditorAsset).toHaveBeenCalledWith('p', 'd', 'export', psd, 'image/vnd.adobe.photoshop');
    expect(api.createImageExport.mock.calls[0][2]).toMatchObject({ fileName: 'Cover.psd' });
  });

  it('downloads without touching the server', async () => {
    const jpg = new Blob(['jpg'], { type: 'image/jpeg' });
    dom.renderExport.mockResolvedValue(jpg);
    await ready();
    const dialog = await openExport();
    fireEvent.click(dialog.getByRole('radio', { name: /JPEG/ }));
    fireEvent.click(dialog.getByRole('button', { name: 'Download' }));
    await waitFor(() => expect(dom.downloadBlob).toHaveBeenCalledWith(jpg, 'Cover.jpg'));
    expect(api.uploadEditorAsset).not.toHaveBeenCalled();
    expect(api.createImageExport).not.toHaveBeenCalled();
    expect(await dialog.findByText(/Downloaded Cover\.jpg/)).toBeTruthy();
  });

  it('offers AVIF only when this browser can make it', async () => {
    dom.canEncode.mockResolvedValue(false);
    await ready();
    const dialog = await openExport();
    fireEvent.click(dialog.getByRole('button', { name: /More formats/ }));
    expect(await dialog.findByText(/Your browser cannot make AVIF files/)).toBeTruthy();
    expect((await dialog.findByRole('radio', { name: /AVIF/ })).getAttribute('aria-disabled')).toBe('true');
  });
});

describe('designed graphics', () => {
  const DESIGNED = baseLayer({
    id: 'gfx',
    label: 'Sale badge',
    kind: 'Generated' as never,
    role: 'overlay',
    zOrder: 3,
    provenance: { operation: 'designGraphic', graphicId: 'g1', workspaceVersionId: 'v1', prompt: 'A red sale badge' },
  });
  const job = {
    id: 'g1',
    documentId: 'd',
    executionId: 'e1',
    workflowId: 'w',
    status: 'Passed' as const,
    description: 'A red sale badge',
    frameWidth: 400,
    frameHeight: 200,
    placement: 'topRight' as const,
    target: { x: 800, y: 40, width: 400, height: 200 },
    revisesLayerId: null,
    layerId: 'gfx',
    layer: DESIGNED,
    summary: 'A red badge, tilted slightly.',
    error: null,
    createdAt: '',
    completedAt: '',
  };

  it('brings a finished design in as a new, undoable layer, once', async () => {
    api.getImageGraphics.mockResolvedValue({ graphics: [job], estimate: null, available: true, unavailableReason: null });
    // The first read is the editor opening; the design was inserted on the server meanwhile.
    api.getImageDocument.mockResolvedValueOnce(docDto([SOURCE, CUTOUT])).mockResolvedValue(docDto([SOURCE, CUTOUT, DESIGNED], 4));
    await ready();
    expect(await screen.findByText('Your graphic is ready. A red badge, tilted slightly.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Select Sale badge' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Undo' }).hasAttribute('disabled')).toBe(false);
    await wait(50);
    expect(api.getImageDocument).toHaveBeenCalledTimes(2);
  });

  it('offers "Change this graphic" for a designed layer, which opens the design tool to change it', async () => {
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT, DESIGNED]));
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Sale badge' }));
    fireEvent.click(screen.getByRole('button', { name: 'Change this graphic' }));
    expect(await screen.findByTestId('design-graphic-revise')).toBeTruthy();
    expect(screen.getByText(/A red sale badge/)).toBeTruthy();
    // a layer that is not a design has no such action
    openTab('Layers');
    fireEvent.click(screen.getByRole('button', { name: 'Select Person' }));
    expect(screen.queryByRole('button', { name: 'Change this graphic' })).toBeNull();
  });

  it('starts a design from the AI tab with the canvas size', async () => {
    api.createImageGraphic.mockResolvedValue({ ...job, status: 'Queued', layer: null, layerId: null, summary: null });
    await ready();
    openTab('AI');
    fireEvent.click(screen.getByRole('button', { name: /Choose an AI tool/ }));
    const dialog = await screen.findByRole('dialog', { name: 'Choose an AI tool' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Design a graphic' }));
    fireEvent.change(await screen.findByLabelText('Describe the graphic'), { target: { value: 'A sale badge' } });
    fireEvent.click(screen.getByRole('button', { name: /Design it/ }));
    await waitFor(() => expect(api.createImageGraphic).toHaveBeenCalled());
    expect(api.createImageGraphic).toHaveBeenCalledWith('p', 'd', {
      description: 'A sale badge',
      placement: 'center',
      canvasWidth: 1280,
      canvasHeight: 720,
    });
  });
});

describe('changes the assistant makes', () => {
  const ADDED = baseLayer({ id: 'new', label: 'Title', kind: 'Generated' as never, role: 'overlay', zOrder: 3 });

  it('brings in a layer the assistant added, selects it and says so', async () => {
    await ready();
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT, ADDED], 5));
    announceImageDocumentChange({
      changeId: 'c1',
      projectId: 'p',
      documentId: 'd',
      kind: 'layersAdded',
      layerIds: ['new'],
      summary: 'Added a title.',
    });
    expect(await screen.findByText('The assistant: Added a title.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Select Title' }).getAttribute('aria-current')).toBe('true');
    expect(screen.getByRole('button', { name: 'Undo' }).hasAttribute('disabled')).toBe(false);
  });

  it('reloads after any other change and clears the undo history', async () => {
    api.updateImageLayer.mockResolvedValue(CUTOUT);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Hide Person' }));
    await waitFor(() => expect(screen.getByRole('status').textContent).toBe('All changes saved'), { timeout: 3000 });
    expect(screen.getByRole('button', { name: 'Undo' }).hasAttribute('disabled')).toBe(false);
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, { ...CUTOUT, label: 'Hero' }], 7));
    announceImageDocumentChange({
      changeId: 'c2',
      projectId: 'p',
      documentId: 'd',
      kind: 'layersChanged',
      layerIds: ['cut'],
      summary: 'Renamed the subject.',
    });
    expect(await screen.findByText('The assistant: Renamed the subject.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Select Hero' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Undo' }).hasAttribute('disabled')).toBe(true);
  });

  it('ignores a change to another picture', async () => {
    await ready();
    const reads = api.getImageDocument.mock.calls.length;
    announceImageDocumentChange({
      changeId: 'c3',
      projectId: 'p',
      documentId: 'other',
      kind: 'layersAdded',
      layerIds: ['x'],
      summary: 'Elsewhere.',
    });
    await wait(50);
    expect(api.getImageDocument.mock.calls.length).toBe(reads);
    expect(screen.queryByText(/Elsewhere/)).toBeNull();
  });

  it('re-reads the designs when the assistant queued one', async () => {
    await ready();
    await waitFor(() => expect(api.getImageGraphics).toHaveBeenCalled());
    const reads = api.getImageGraphics.mock.calls.length;
    announceImageDocumentChange({
      changeId: 'c4',
      projectId: 'p',
      documentId: 'd',
      kind: 'graphicQueued',
      layerIds: [],
      summary: 'Started designing a sale badge.',
      graphicId: 'g2',
    });
    expect(await screen.findByText('The assistant: Started designing a sale badge.')).toBeTruthy();
    expect(api.getImageGraphics.mock.calls.length).toBeGreaterThan(reads);
  });
});

describe('picture editor remediation (audit top-10 #10)', () => {
  const touchScreen = () => {
    global.matchMedia = ((q: string) => ({ matches: q.includes('pointer: coarse'), media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  };
  const pickTool = async (title: string) => {
    openTab('AI');
    fireEvent.click(screen.getByRole('button', { name: /Choose an AI tool|Change tool/ }));
    const dialog = await screen.findByRole('dialog', { name: 'Choose an AI tool' });
    fireEvent.click(within(dialog).getByRole('button', { name: title }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Choose an AI tool' })).toBeNull());
  };

  it('T1: "Original size" in the Canvas tab sets the canvas to the photo\'s own size', async () => {
    // An older picture: opened at 1280 × 720 and its photo's row states no size, so the decoded photo says it.
    const photo = baseLayer({ id: 'src', label: 'Original', width: null, height: null });
    pixels.sizes.set('/img/src', [1080, 1920]);
    api.getImageDocument.mockResolvedValue(docDto([photo]));
    api.updateImageDocument.mockResolvedValue(docDto([photo], 4));
    await ready();
    openTab('Canvas');
    const button = (await screen.findByRole('button', { name: /Original size/ })) as HTMLButtonElement;
    await waitFor(() => expect(button.textContent).toContain('1080 × 1920'));
    expect(button.disabled).toBe(false);
    fireEvent.click(button);
    await waitFor(() => expect(api.updateImageDocument).toHaveBeenCalled(), { timeout: 3000 });
    expect(api.updateImageDocument.mock.calls[0].slice(0, 3)).toEqual(['p', 'd', { canvasWidth: 1080, canvasHeight: 1920, baseRevision: 3 }]);
    // the photo already sits at the top-left corner at full size, so no layer write is needed
    expect(api.updateImageLayer).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Undo' }).hasAttribute('disabled')).toBe(false);
  });

  it('T8: going to another page right after an edit still saves it', async () => {
    api.updateImageLayer.mockResolvedValue(CUTOUT);
    const view = mount();
    await screen.findByRole('application', { name: 'Thumbnail canvas' });
    fireEvent.click(screen.getByRole('button', { name: 'Hide Person' }));
    view.unmount();
    await waitFor(() => expect(api.updateImageLayer).toHaveBeenCalledTimes(1));
    expect(api.updateImageLayer).toHaveBeenCalledWith('p', 'd', 'cut', { visible: false, baseRevision: 3 }, { keepalive: true });
  });

  it('T13: what was typed for an AI tool is still there after looking at another tab', async () => {
    api.getOperationCapabilities.mockResolvedValue({ operation: 'removeBackground', provider: { kind: 'FalAi', model: 'm' }, takesPrompt: false, maxPromptLength: 0, expand: null });
    await ready();
    await pickTool('Cut out a subject');
    fireEvent.change(screen.getByLabelText('Or describe it'), { target: { value: 'the dog on the left' } });
    openTab('Layers');
    openTab('AI');
    expect((screen.getByLabelText('Or describe it') as HTMLInputElement).value).toBe('the dog on the left');
  });

  it('T16: "Cut out a subject" asks up front whether a cut-out service is set up, and says so when none is', async () => {
    const reason = 'No segmentation provider is set up for this workspace.';
    api.getOperationCapabilities.mockRejectedValue(new ApiError(reason, 409, [], { code: 'SEGMENTATION_PROVIDER_UNAVAILABLE', message: reason }));
    await ready();
    await pickTool('Cut out a subject');
    await waitFor(() => expect(api.getOperationCapabilities).toHaveBeenCalledWith('p', 'removeBackground', expect.anything()));
    const tool = within(screen.getByTestId('ai-tool-subject'));
    expect(await tool.findByText('Not available here')).toBeTruthy();
    expect(tool.getByText(reason)).toBeTruthy();
    expect(tool.queryByRole('button', { name: 'Cut out' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Change tool' }));
    const dialog = await screen.findByRole('dialog', { name: 'Choose an AI tool' });
    expect(within(within(dialog).getByTestId('ai-tool-option-subject')).getByText('Not set up in this workspace')).toBeTruthy();
  });

  it('m4 N1: on a touch screen the project\'s images are a list to tap, not a dropdown', async () => {
    touchScreen();
    const logo = baseLayer({ id: 'logo-layer', label: 'logo.png', kind: 'Image' as never, role: 'overlay', zOrder: 3 });
    api.createLayerFromFile.mockResolvedValue(logo);
    api.updateImageLayer.mockResolvedValue(logo);
    await ready();
    expect(screen.queryByPlaceholderText('Pick an image')).toBeNull();
    const list = screen.getByRole('group', { name: 'Add an image from this project' });
    const option = within(list).getByRole('button', { name: 'logo.png' });
    fireEvent.click(option);
    expect(option.getAttribute('aria-pressed')).toBe('true');
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT, logo], 4));
    fireEvent.click(screen.getByRole('button', { name: 'Add to the picture' }));
    await waitFor(() => expect(api.createLayerFromFile).toHaveBeenCalledWith('p', 'd', { projectFileId: 'logo', role: 'overlay' }));
  });

  it('m4 N3: the layer buttons and the zoom buttons are 44 px targets on a touch screen', async () => {
    touchScreen();
    await ready();
    for (const name of ['Hide Person', 'Lock Person', 'Move Person back', 'Delete Person', 'Zoom in', 'Fit the picture on screen']) {
      // The hit area comes from the density token (44 px under data-ed-touch), never a hand-set inline size.
      const button = screen.getByRole('button', { name });
      expect(button.className).toMatch(/iconButton/);
      expect(button.getAttribute('style') ?? '').not.toContain('--ai-size: calc(1.');
    }
  });

  it('m4 N3: with a mouse the layer buttons keep their compact size', async () => {
    await ready();
    expect(screen.getByRole('button', { name: 'Hide Person' }).getAttribute('style') ?? '').not.toContain('2.75rem');
    expect(screen.getByRole('button', { name: 'Hide Person' }).className).toMatch(/iconButton/);
  });

  it('T4: a layer whose image is gone shows a placeholder, and Export still works', async () => {
    pixels.missing.add('/img/cut');
    global.fetch = vi.fn(async () =>
      new Response(JSON.stringify({ code: 'LAYER_IMAGE_MISSING', message: 'gone' }), { status: 404 }));
    const png = new Blob(['png'], { type: 'image/png' });
    dom.renderExport.mockResolvedValue(png);
    await ready();
    expect(await screen.findByTestId('missing-layer-cut')).toBeTruthy();
    expect(within(screen.getByTestId('layer-row-cut')).getByText('Image missing')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Export' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Export your picture' }));
    expect(dialog.queryByText(/Waiting for/)).toBeNull();
    // the export says plainly which layer it leaves out
    expect(dialog.getByText('The image of "Person" is gone, so the export leaves it out.')).toBeTruthy();
    const download = dialog.getByRole('button', { name: 'Download' }) as HTMLButtonElement;
    await waitFor(() => expect(download.disabled).toBe(false));
    fireEvent.click(download);
    await waitFor(() => expect(dom.downloadBlob).toHaveBeenCalled());
  });

  it('C-330-1: a layer that will not load for another reason (5xx, network) is not left out: Export refuses and names it', async () => {
    pixels.missing.add('/img/cut');
    global.fetch = vi.fn(async () => new Response('busy', { status: 503 }));
    dom.renderExport.mockResolvedValue(new Blob(['png'], { type: 'image/png' }));
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Export' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Export your picture' }));
    expect(
      await dialog.findByText(/"Person" could not be loaded, so it cannot be exported yet\./, undefined, { timeout: 4000 }),
    ).toBeTruthy();
    expect(screen.queryByTestId('missing-layer-cut')).toBeNull();
    const download = dialog.getByRole('button', { name: 'Download' }) as HTMLButtonElement;
    expect(download.disabled).toBe(true);
    expect((dialog.getByRole('button', { name: 'Save to project' }) as HTMLButtonElement).disabled).toBe(true);
    // once the connection is back, "Try again" loads it and Export goes ahead with every layer
    pixels.missing.delete('/img/cut');
    fireEvent.click(dialog.getByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(download.disabled).toBe(false));
    fireEvent.click(download);
    await waitFor(() => expect(dom.downloadBlob).toHaveBeenCalled());
    expect(dom.renderExport.mock.calls[0][2].has('cut')).toBe(true);
  }, 10_000);

  it('d6 N1: the layout sizes its stage to end at the bottom of the window, and follows a resize', async () => {
    await ready();
    const layout = screen.getByRole('toolbar', { name: 'Tools' }).parentElement as HTMLElement;
    const height = () => parseFloat(layout.style.getPropertyValue('--stage-height'));
    const below = 37;
    layout.parentElement!.style.paddingBottom = `${below}px`;
    const before = window.innerHeight;
    try {
      Object.defineProperty(window, 'innerHeight', { configurable: true, value: 900 });
      fireEvent(window, new Event('resize'));
      // jsdom puts the layout at the top of the page, so all that is taken away is the padding below it
      const at900 = height();
      expect(at900).toBeLessThanOrEqual(900 - below);
      expect(at900).toBeGreaterThan(900 - below - 20);
      Object.defineProperty(window, 'innerHeight', { configurable: true, value: 1000 });
      fireEvent(window, new Event('resize'));
      expect(height()).toBe(at900 + 100);
    } finally {
      Object.defineProperty(window, 'innerHeight', { configurable: true, value: before });
    }
  });

  it('R14.6: reads the picture again after the AI service failed (424), since it may have written something', async () => {
    const message = 'The AI service did not answer in time. Nothing was charged.';
    api.runImageOperation.mockRejectedValue(new ApiError(message, 424, [], { code: 'OPERATION_TIMEOUT', message }));
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Original' }));
    await pickTool('Upscale');
    expect(api.getImageDocument).toHaveBeenCalledTimes(1);
    fireEvent.click(within(screen.getByTestId('ai-tool-upscale')).getByRole('button', { name: 'Upscale the picture' }));
    await waitFor(() => expect(api.getImageDocument).toHaveBeenCalledTimes(2));
    expect(await screen.findByText(message)).toBeTruthy();
  });
});

describe('Wave 2: delete key, arrange, undo ghosts', () => {
  it('Delete removes the selected layer as an undo step; undo brings it back and nothing is deleted on the server', async () => {
    api.updateImageLayer.mockResolvedValue(CUTOUT);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Person' }));
    fireEvent.keyDown(window, { key: 'Delete' });
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Select Person' })).toBeNull());
    expect((screen.getByRole('button', { name: 'Undo' }) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Undo' }));
    expect(await screen.findByRole('button', { name: 'Select Person' })).toBeTruthy();
    await wait(1000);
    expect(api.deleteImageLayer).not.toHaveBeenCalled();
  });

  it('Delete is ignored while typing in a field and for a locked layer', async () => {
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Person' }));
    fireEvent.keyDown(screen.getByTestId('arrange-left'), { key: 'Delete' });
    expect(screen.getByRole('button', { name: 'Select Person' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Lock Person' }));
    fireEvent.keyDown(window, { key: 'Delete' });
    expect(screen.getByRole('button', { name: 'Select Person' })).toBeTruthy();
  });

  it('typing a left position moves the layer, and the stored layer follows', async () => {
    api.updateImageLayer.mockResolvedValue(CUTOUT);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Person' }));
    const input = screen.getByTestId('arrange-left') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '120' } });
    fireEvent.blur(input);
    await waitFor(() => expect(api.updateImageLayer).toHaveBeenCalled(), { timeout: 3000 });
    expect(api.updateImageLayer.mock.calls[0][3]).toMatchObject({ transform: { x: 120 } });
  });

  it('aligns the selected layer to the picture centre in one step', async () => {
    api.updateImageLayer.mockResolvedValue(CUTOUT);
    pixels.sizes.set('/img/cut', [200, 100]);
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, { ...CUTOUT, width: 200, height: 100 }]));
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Person' }));
    fireEvent.click(screen.getByTestId('align-center'));
    await waitFor(() => expect(api.updateImageLayer).toHaveBeenCalled(), { timeout: 3000 });
    expect(api.updateImageLayer.mock.calls[0][3].transform?.x).toBe(540);
  });

  it('shows a tick box per layer once several can be chosen, and merge needs two', async () => {
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Person' }));
    expect((screen.getByTestId('merge-layers') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByTestId('arrange-pick-switch'));
    fireEvent.click(screen.getByTestId('layer-pick-src'));
    expect((screen.getByTestId('merge-layers') as HTMLButtonElement).disabled).toBe(false);
  });

  it('an undone AI result leaves the layers list and is deleted on the server once a new edit drops the redo', async () => {
    const generated = baseLayer({ id: 'gen', label: 'Upscale', kind: 'Generated' as never, role: 'overlay', zOrder: 3 });
    api.runImageOperation.mockResolvedValue({
      layers: [generated],
      operation: 'upscale',
      provider: { kind: 'FalAi', model: 'm', capability: 'ImageUpscale' },
      usage: null,
      chargedCredits: 4.5,
    });
    api.updateImageLayer.mockResolvedValue(CUTOUT);
    api.deleteImageLayer.mockResolvedValue(undefined);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Original' }));
    openTab('AI');
    fireEvent.click(screen.getByRole('button', { name: /Choose an AI tool|Change tool/ }));
    fireEvent.click(within(await screen.findByRole('dialog', { name: 'Choose an AI tool' })).getByRole('button', { name: 'Upscale' }));
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT, generated], 4));
    fireEvent.click(await screen.findByRole('button', { name: 'Upscale the picture' }));
    expect(await screen.findByTestId('result-bar')).toBeTruthy();
    expect(screen.getByTestId('result-bar-cost').textContent).toMatch(/4\.5/);
    fireEvent.click(screen.getByTestId('result-undo'));
    openTab('Layers');
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Select Upscale' })).toBeNull());
    expect(screen.queryByTestId('result-bar')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Lock Person' }));
    await waitFor(() => expect(api.deleteImageLayer).toHaveBeenCalledWith('p', 'd', 'gen'), { timeout: 4000 });
  });

  it('a run that timed out (424) but finished late shows the layer once, with a plain message, and never runs twice', async () => {
    const late = baseLayer({ id: 'late', label: 'Upscale', kind: 'Generated' as never, role: 'overlay', zOrder: 3 });
    api.runImageOperation.mockRejectedValue(new ApiError('The AI service took too long.', 424, [], { code: 'OPERATION_TIMEOUT' }));
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Original' }));
    openTab('AI');
    fireEvent.click(screen.getByRole('button', { name: /Choose an AI tool|Change tool/ }));
    fireEvent.click(within(await screen.findByRole('dialog', { name: 'Choose an AI tool' })).getByRole('button', { name: 'Upscale' }));
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT, late], 5));
    fireEvent.click(await screen.findByRole('button', { name: 'Upscale the picture' }));
    expect(await screen.findByText(/took longer than expected, but it finished/)).toBeTruthy();
    openTab('Layers');
    expect(await screen.findByRole('button', { name: 'Select Upscale' })).toBeTruthy();
    expect(api.runImageOperation).toHaveBeenCalledTimes(1);
  });
});

describe('background work on a layer', () => {
  const generated = baseLayer({ id: 'up', label: 'Upscale', kind: 'Generated' as never, role: 'overlay', zOrder: 3 });
  const hiddenOut = { ...generated, visible: false };

  const startUpscale = async (alreadyReady = false) => {
    if (!alreadyReady) await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Select Person' }));
    openTab('AI');
    fireEvent.click(screen.getByRole('button', { name: /Choose an AI tool|Change tool/ }));
    const dialog = await screen.findByRole('dialog', { name: 'Choose an AI tool' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Upscale' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Choose an AI tool' })).toBeNull());
    fireEvent.click(within(screen.getByTestId('ai-tool-upscale')).getByRole('button', { name: 'Upscale the picture' }));
    await waitFor(() => expect(api.runImageOperation).toHaveBeenCalled());
  };
  const answer = {
    operation: 'upscale',
    provider: { kind: 'FalAi', model: 'fal-ai/esrgan' },
    revision: 4,
  };

  it('marks the layer busy while its operation runs, asks before deleting it, and goes on after "Go on"', async () => {
    let finish: (v: unknown) => void = () => {};
    api.runImageOperation.mockReturnValue(new Promise((r) => { finish = r; }));
    api.deleteImageLayer.mockResolvedValue(undefined);
    await startUpscale();
    openTab('Layers');
    expect(await screen.findByTestId('layer-busy-cut')).toBeTruthy();
    expect(screen.getByTestId('canvas-busy')).toBeTruthy();
    expect(screen.queryByTestId('layer-busy-src')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Delete Person' }));
    const ask = await screen.findByRole('dialog', { name: 'This layer is busy' });
    expect(within(ask).getByText(/still being worked on/)).toBeTruthy();
    expect(within(ask).queryByTestId('busy-guard-stop')).toBeNull();
    // Back closes it and changes nothing
    fireEvent.click(within(ask).getByTestId('busy-guard-back'));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'This layer is busy' })).toBeNull());
    expect(screen.getByRole('button', { name: 'Select Person' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Delete Person' }));
    fireEvent.click(within(await screen.findByRole('dialog', { name: 'This layer is busy' })).getByTestId('busy-guard-go-on'));
    // Go on leads to the usual delete confirmation
    fireEvent.click(within(await screen.findByRole('dialog', { name: 'Delete this layer?' })).getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(api.deleteImageLayer).toHaveBeenCalledWith('p', 'd', 'cut'), { timeout: 3000 });
    finish({ ...answer, layers: [generated] });
  });

  it('keeps a normal answer unchanged: the result bar, no review bar, no busy mark afterwards', async () => {
    api.runImageOperation.mockResolvedValue({ ...answer, layers: [generated] });
    await startUpscale();
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT, generated], 4));
    expect((await screen.findByTestId('result-bar-changed')).textContent).toMatch(/added a new layer/);
    expect(screen.queryByTestId('review-bar')).toBeNull();
    expect(screen.queryByTestId('canvas-busy')).toBeNull();
  });

  const reviewed = async () => {
    api.runImageOperation.mockResolvedValue({ ...answer, layers: [hiddenOut], needsReview: { reason: 'layerGone', layerId: 'cut' } });
    await ready();
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, hiddenOut as unknown as EditImageLayer], 4));
    await startUpscale(true);
    return screen.findByTestId('review-bar');
  };

  it('keeps a result whose layer was deleted hidden and asks, instead of showing it as applied', async () => {
    const bar = await reviewed();
    expect(bar.textContent).toMatch(/The layer this was made for was deleted\. The result is kept, hidden\./);
    expect(screen.queryByTestId('result-bar-changed')).toBeNull();
  });

  it('"Show it" sends visible: true with baseRevision', async () => {
    api.updateImageLayer.mockResolvedValue(generated);
    const bar = await reviewed();
    fireEvent.click(within(bar).getByTestId('review-show'));
    await waitFor(() => expect(api.updateImageLayer).toHaveBeenCalledWith('p', 'd', 'up', { visible: true, baseRevision: 4 }), { timeout: 3000 });
    await waitFor(() => expect(screen.queryByTestId('review-bar')).toBeNull());
  });

  it('"Discard" removes the kept layer as one undo step', async () => {
    const bar = await reviewed();
    fireEvent.click(within(bar).getByTestId('review-discard'));
    await waitFor(() => expect(screen.queryByTestId('review-bar')).toBeNull());
    expect((screen.getByRole('button', { name: 'Undo' }) as HTMLButtonElement).disabled).toBe(false);
    openTab('Layers');
    expect(screen.queryByRole('button', { name: 'Select Upscale' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Undo' }));
    expect(await screen.findByRole('button', { name: 'Select Upscale' })).toBeTruthy();
  });

  it('undo after a needsReview result does not reveal it (the hold is not an undo step)', async () => {
    await reviewed();
    openTab('Layers');
    fireEvent.click(screen.getByRole('button', { name: 'Undo' }));
    await wait(200);
    expect(screen.queryByRole('button', { name: 'Hide Upscale' })).toBeNull();
    expect(api.updateImageLayer).not.toHaveBeenCalled();
  });

  it('remembers a kept-hidden result across a reload and offers it again', async () => {
    await reviewed();
    expect(loadPictureReview('d')).toEqual(['up']);
    cleanup();
    api.runImageOperation.mockReset();
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, hiddenOut as unknown as EditImageLayer], 4));
    await ready();
    expect(await screen.findByTestId('review-bar')).toBeTruthy();
  });

  it('forgets a remembered layer that is gone or shown', async () => {
    savePictureReview('d', ['ghost', 'src']);
    await ready();
    await waitFor(() => expect(loadPictureReview('d')).toEqual([]));
    expect(screen.queryByTestId('review-bar')).toBeNull();
  });

  it('asks before merging a layer a job is working on', async () => {
    api.runImageOperation.mockReturnValue(new Promise(() => {}));
    await startUpscale();
    openTab('Layers');
    fireEvent.click(screen.getByTestId('arrange-pick-switch'));
    fireEvent.click(screen.getByTestId('layer-pick-src'));
    fireEvent.click(screen.getByTestId('merge-layers'));
    expect(await screen.findByRole('dialog', { name: 'This layer is busy' })).toBeTruthy();
  });
});

describe('picture jobs in the tray', () => {
  let ctx: ActivityContextValue;
  function Probe() {
    const value = useActivity();
    ctx = value;
    return null;
  }
  const generated = baseLayer({ id: 'up', label: 'Upscale', kind: 'Generated' as never, role: 'overlay', zOrder: 3 });
  const hiddenOut = { ...generated, visible: false };
  const answer = { operation: 'upscale', provider: { kind: 'FalAi', model: 'fal-ai/esrgan' }, revision: 4 };

  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('EventSource', FakeEventSource);
    signInAs();
  });

  const start = async (afterLoad?: () => void) => {
    render(
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <MantineProvider>
          <ActivityProvider>
            <Probe />
            <ThumbnailEditor projectId="p" documentId="d" />
          </ActivityProvider>
        </MantineProvider>
      </SWRConfig>,
    );
    await screen.findByRole('application', { name: 'Thumbnail canvas' });
    afterLoad?.();
    fireEvent.click(screen.getByRole('button', { name: 'Select Person' }));
    openTab('AI');
    fireEvent.click(screen.getByRole('button', { name: /Choose an AI tool|Change tool/ }));
    const dialog = await screen.findByRole('dialog', { name: 'Choose an AI tool' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Upscale' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Choose an AI tool' })).toBeNull());
    fireEvent.click(within(screen.getByTestId('ai-tool-upscale')).getByRole('button', { name: /^Upscale the picture/ }));
    await waitFor(() => expect(api.runImageOperation).toHaveBeenCalled());
  };
  const opJobs = () => ctx.allJobs.filter((j) => j.type === 'pictureOperation');

  it('lists the operation as a tray job while it runs and ends it done on success', async () => {
    let finish: (v: unknown) => void = () => {};
    api.runImageOperation.mockReturnValue(new Promise((r) => { finish = r; }));
    await start(() => api.getImageDocument.mockResolvedValue(docDto([SOURCE, CUTOUT, generated], 4)));
    await waitFor(() => expect(opJobs()).toHaveLength(1));
    expect(opJobs()[0].state).toBe('running');
    expect(opJobs()[0].href).toBe('/editor/thumbnails/d');
    finish({ ...answer, layers: [generated] });
    await waitFor(() => expect(opJobs()[0].state).toBe('done'));
  });

  it('shows a kept-hidden result as "needs your OK" in the tray, and tray Apply shows the layer and clears the bar', async () => {
    api.updateImageLayer.mockResolvedValue(generated);
    api.runImageOperation.mockResolvedValue({ ...answer, layers: [hiddenOut], needsReview: { reason: 'layerGone', layerId: 'cut' } });
    await start(() => api.getImageDocument.mockResolvedValue(docDto([SOURCE, hiddenOut as unknown as EditImageLayer], 4)));
    await screen.findByTestId('review-bar');
    await waitFor(() => expect(opJobs()[0]?.state).toBe('needsReview'));
    await act(async () => { await ctx.resolveTrackedJob(opJobs()[0].id, 'applied'); });
    await waitFor(() => expect(api.updateImageLayer).toHaveBeenCalledWith('p', 'd', 'up', { visible: true, baseRevision: 4 }), { timeout: 3000 });
    await waitFor(() => expect(screen.queryByTestId('review-bar')).toBeNull());
    expect(opJobs()[0].state).toBe('done');
  });

  it('resolving from the picture bar resolves the tray job too', async () => {
    api.updateImageLayer.mockResolvedValue(generated);
    api.runImageOperation.mockResolvedValue({ ...answer, layers: [hiddenOut], needsReview: { reason: 'layerGone', layerId: 'cut' } });
    await start(() => api.getImageDocument.mockResolvedValue(docDto([SOURCE, hiddenOut as unknown as EditImageLayer], 4)));
    const bar = await screen.findByTestId('review-bar');
    await waitFor(() => expect(opJobs()[0]?.state).toBe('needsReview'));
    fireEvent.click(within(bar).getByTestId('review-discard'));
    await waitFor(() => expect(opJobs()[0].state).toBe('cancelled'));
    await waitFor(() => expect(screen.queryByTestId('review-bar')).toBeNull());
  });

  it('passes the operation estimate to the tray in credits (4.5 stays 4.5)', async () => {
    api.getOperationCapabilities.mockResolvedValue({ operation: 'upscale', estimate: { billingEnabled: true, credits: 4.5, balanceCredits: 10, affordable: true } });
    api.runImageOperation.mockReturnValue(new Promise(() => {}));
    await start(() => undefined);
    await waitFor(() => expect(opJobs()).toHaveLength(1));
    expect(opJobs()[0].credits.heldCredits).toBe(4.5);
  });

  it('offers a result remembered across a reload in the tray under a stable id, once', async () => {
    savePictureReview('d', ['up']);
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, hiddenOut as unknown as EditImageLayer], 4));
    const tree = (key: number) => (
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <MantineProvider>
          <ActivityProvider>
            <Probe />
            <ThumbnailEditor key={key} projectId="p" documentId="d" />
          </ActivityProvider>
        </MantineProvider>
      </SWRConfig>
    );
    const { rerender } = render(tree(1));
    await screen.findByTestId('review-bar');
    await waitFor(() => expect(opJobs().map((j) => j.id)).toEqual(['picreview:layer:d:up']));
    expect(opJobs()[0].state).toBe('needsReview');
    rerender(tree(2));
    await screen.findByTestId('review-bar');
    await wait(100);
    expect(opJobs()).toHaveLength(1);
  });

  it('mirrors a designed graphic that needs review in the tray under its own stable id, once', async () => {
    const graphicJob = {
      id: 'g1', documentId: 'd', executionId: 'e1', workflowId: 'w1', status: 'Passed', description: 'Sale badge', frameWidth: 400, frameHeight: 200,
      placement: 'topRight', target: { x: 0, y: 0, width: 400, height: 200 }, revisesLayerId: null, layerId: 'up', layer: hiddenOut, summary: null, error: null,
      createdAt: '', completedAt: '', needsReview: { reason: 'layerGone', layerId: 'cut' },
    };
    api.getImageGraphics.mockResolvedValue({ graphics: [graphicJob], estimate: null, available: true, unavailableReason: null });
    api.getImageDocument.mockResolvedValue(docDto([SOURCE, hiddenOut as unknown as EditImageLayer], 4));
    const tree = (key: number) => (
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <MantineProvider>
          <ActivityProvider>
            <Probe />
            <ThumbnailEditor key={key} projectId="p" documentId="d" />
          </ActivityProvider>
        </MantineProvider>
      </SWRConfig>
    );
    const { rerender } = render(tree(1));
    await screen.findByTestId('review-bar');
    const graphicRows = () => ctx.allJobs.filter((j) => j.type === 'graphic');
    await waitFor(() => expect(graphicRows().map((j) => j.id)).toEqual(['picreview:graphic:g1']));
    expect(graphicRows()[0].state).toBe('needsReview');
    rerender(tree(2));
    await wait(200);
    expect(graphicRows()).toHaveLength(1);
  });
});
