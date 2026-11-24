// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { EditorLayer } from '@/lib/editor/model';
import { LayersPanel } from './LayersPanel';

vi.stubGlobal('matchMedia', (q: string) => ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {} }));
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

const layer = (over: Partial<EditorLayer>): EditorLayer =>
  ({
    id: 'a', kind: 'source', role: 'background', label: 'Photo', zOrder: 1, visible: true, locked: false, opacity: 1,
    blendMode: 'normal', transform: { x: 0, y: 0, scale: 1, rotation: 0, flipX: false, flipY: false },
    width: 10, height: 10, imageUrl: '/i', matteUrl: null, content: null, segmentationPrompt: null, ...over,
  }) as EditorLayer;

function mount(layers: EditorLayer[], masks = { busyId: null, paintingId: null, maskOnlyId: null, onAction: vi.fn() }, menuRows = false) {
  render(
    <MantineProvider>
      <LayersPanel layers={layers} selectedId={null} onSelect={vi.fn()} onPatch={vi.fn()} onReorder={vi.fn()} onDelete={vi.fn()} masks={masks} menuRows={menuRows} />
    </MantineProvider>,
  );
  return masks;
}

afterEach(cleanup);

describe('LayersPanel: masks', () => {
  it('shows a mask chip only on a layer that has a mask, and pressing it edits the mask', () => {
    const masks = mount([layer({ id: 'a', matteUrl: '/m/a' }), layer({ id: 'b', label: 'Other', zOrder: 2 })]);
    expect(screen.getByTestId('layer-mask-chip-a')).toBeTruthy();
    expect(screen.queryByTestId('layer-mask-chip-b')).toBeNull();
    fireEvent.click(screen.getByTestId('layer-mask-chip-a'));
    expect(masks.onAction).toHaveBeenCalledWith('edit', 'a');
  });

  it('a layer without a mask offers only Add mask, which asks for it', async () => {
    const masks = mount([layer({ id: 'b' })]);
    fireEvent.click(screen.getByTestId('layer-mask-menu-b'));
    const add = await screen.findByTestId('layer-mask-add-b');
    expect(screen.queryByTestId('layer-mask-edit-b')).toBeNull();
    fireEvent.click(add);
    expect(masks.onAction).toHaveBeenCalledWith('add', 'b');
  });

  it('a layer with a mask offers edit, invert, delete and show mask only', async () => {
    mount([layer({ id: 'a', matteUrl: '/m/a' })]);
    fireEvent.click(screen.getByTestId('layer-mask-menu-a'));
    await screen.findByTestId('layer-mask-edit-a');
    for (const action of ['edit', 'invert', 'delete', 'only']) expect(screen.getByTestId(`layer-mask-${action}-a`)).toBeTruthy();
    expect(screen.queryByTestId('layer-mask-add-a')).toBeNull();
  });

  it('a text layer has no mask menu', () => {
    mount([layer({ id: 't', kind: 'text', imageUrl: '' })]);
    expect(screen.queryByTestId('layer-mask-menu-t')).toBeNull();
  });

  it('on a phone the mask entries live in the row menu', async () => {
    mount([layer({ id: 'a', matteUrl: '/m/a' })], undefined, true);
    expect(screen.queryByTestId('layer-mask-menu-a')).toBeNull();
    fireEvent.click(screen.getByTestId('layer-menu-a'));
    expect(await screen.findByTestId('layer-mask-invert-a')).toBeTruthy();
  });
});
