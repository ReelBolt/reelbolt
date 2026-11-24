// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { ApiError } from '@/lib/api/client';
import type { EditImageDocument, EditImageLayer } from '@/lib/types/image-document';
import { RecordingContext } from '@/lib/editor/__tests__/recording-context';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
// jsdom has no PointerEvent, and Testing Library falls back to a plain Event that carries no
// coordinates or button. A MouseEvent subclass is all the canvas handlers read.
vi.stubGlobal(
  'PointerEvent',
  class extends MouseEvent {
    pointerId: number;
    pointerType: string;
    constructor(type: string, init: MouseEventInit & { pointerId?: number; pointerType?: string } = {}) {
      super(type, init);
      this.pointerId = init.pointerId ?? 1;
      this.pointerType = init.pointerType ?? 'mouse';
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

const device = vi.hoisted(() => ({ cls: 'phone' as 'phone' | 'tablet' | 'desktop' }));
vi.mock('@/lib/editing/use-device-class', async (orig) => ({
  ...(await orig<typeof import('@/lib/editing/use-device-class')>()),
  useDeviceClass: () => device.cls,
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

async function ready() {
  mount();
  await screen.findByRole('application', { name: 'Thumbnail canvas' });
}


const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));
const touch = (id: number, x = 100, y = 100) => ({ button: 0, clientX: x, clientY: y, pointerId: id, pointerType: 'touch' });

describe('RM.14 picture phone shell', () => {
  beforeEach(() => {
    device.cls = 'phone';
  });

  it('a desktop window keeps the side panel and has no dock', async () => {
    device.cls = 'desktop';
    await ready();
    expect(screen.queryByTestId('picture-dock')).toBeNull();
    expect(screen.getByRole('tab', { name: 'Layers' })).toBeTruthy();
  });

  it('a phone shows the stage, a four-item dock and no sheet until one is chosen', async () => {
    await ready();
    expect(screen.getByTestId('picture-stage')).toBeTruthy();
    const dock = screen.getByTestId('picture-dock');
    expect(within(dock).getAllByRole('button').map((b) => b.textContent)).toEqual(['Layers', 'Text', 'AI', 'Canvas']);
    expect(screen.queryByTestId('picture-sheet')).toBeNull();
    expect(screen.queryByRole('tab', { name: 'Layers' })).toBeNull();
    expect(screen.getByTestId('picture-export')).toBeTruthy();
  });

  it('the dock opens the sheet over a stage that stays; the detent changes without losing the selection', async () => {
    await ready();
    fireEvent.click(screen.getByTestId('picture-dock-layers'));
    const sheet = screen.getByTestId('picture-sheet');
    expect(sheet.getAttribute('data-detent')).toBe('half');
    expect(screen.getByRole('application', { name: 'Thumbnail canvas' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Select Person' }));
    expect(screen.getByRole('button', { name: 'Select Person' }).getAttribute('aria-current')).toBe('true');
    fireEvent.click(screen.getByTestId('picture-sheet-lower'));
    expect(screen.getByTestId('picture-sheet').getAttribute('data-detent')).toBe('peek');
    expect(screen.getByRole('button', { name: 'Select Person' }).getAttribute('aria-current')).toBe('true');
    fireEvent.click(screen.getByTestId('picture-dock-text'));
    expect(screen.getByRole('dialog', { name: 'Text' })).toBeTruthy();
    fireEvent.click(screen.getByTestId('picture-sheet-close'));
    expect(screen.queryByTestId('picture-sheet')).toBeNull();
  });

  it('the layer row has an overflow menu; Send to back reorders and offers Undo; Delete is apart from the arrows', async () => {
    await ready();
    fireEvent.click(screen.getByTestId('picture-dock-layers'));
    expect(screen.queryByRole('button', { name: 'Move Person forward' })).toBeNull();
    fireEvent.click(screen.getByTestId('layer-menu-cut'));
    const labels = (await screen.findAllByRole('menuitem')).map((m) => m.textContent);
    expect(labels).toEqual(['Bring forward', 'Send back', 'Bring to front', 'Send to back', 'Add mask', 'Delete layer']);
    fireEvent.click(screen.getByTestId('layer-menu-backmost-cut'));
    await waitFor(() => expect(screen.getAllByRole('listitem')[0].textContent).toContain('Original'));
    expect(screen.getByTestId('undo-snackbar').textContent).toContain('Layer moved');
    fireEvent.click(screen.getByTestId('undo-snackbar-undo'));
    await waitFor(() => expect(screen.getAllByRole('listitem')[0].textContent).toContain('Person'));
  });

  it('a pinch (two fingers) on the text tool never adds a text layer; a single tap does', async () => {
    api.createTextLayer.mockResolvedValue(baseLayer({ id: 'txt', label: 'Text', kind: 'Text' as never, zOrder: 3 }));
    await ready();
    fireEvent.click(within(screen.getByRole('toolbar', { name: 'Tools' })).getByRole('button', { name: 'Text' }));
    const canvas = screen.getByRole('img', { name: 'Your picture' });
    fireEvent.pointerDown(canvas, touch(1, 100, 100));
    await wait(20);
    fireEvent.pointerDown(canvas, touch(2, 160, 100));
    fireEvent.pointerMove(canvas, touch(2, 220, 100));
    fireEvent.pointerUp(canvas, touch(2, 220, 100));
    fireEvent.pointerUp(canvas, touch(1, 100, 100));
    await wait(250);
    expect(api.createTextLayer).not.toHaveBeenCalled();
    fireEvent.pointerDown(canvas, touch(3, 100, 100));
    fireEvent.pointerUp(canvas, touch(3, 100, 100));
    await waitFor(() => expect(api.createTextLayer).toHaveBeenCalledTimes(1));
  });

  it('the nudge pad moves the selected layer 1 px, or 10 px when the big step is on', async () => {
    api.updateImageLayer.mockResolvedValue(CUTOUT);
    await ready();
    fireEvent.click(screen.getByTestId('picture-dock-layers'));
    fireEvent.click(screen.getByRole('button', { name: 'Select Person' }));
    fireEvent.click(screen.getByTestId('nudge-ArrowRight'));
    fireEvent.click(screen.getByRole('radio', { name: '10 px' }));
    fireEvent.click(screen.getByTestId('nudge-ArrowDown'));
    await waitFor(() => expect(api.updateImageLayer).toHaveBeenCalled(), { timeout: 3000 });
    const sent = api.updateImageLayer.mock.calls.at(-1)?.[3];
    expect(sent.transform).toMatchObject({ x: 1, y: 10 });
  });
});
