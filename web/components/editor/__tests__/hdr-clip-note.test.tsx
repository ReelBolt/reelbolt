// @vitest-environment jsdom
/** R10.7: an HDR clip carries a small "HDR clip" note and the approximate-preview reason in the Inspector. */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { InspectorPanel } from '../panels/InspectorPanel';
import { standardDoc } from '@/lib/editing/__tests__/fixtures';
import { HDR_PREVIEW_REASON } from '@/lib/editing/media/hdr';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
global.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver;

afterEach(() => cleanup());

const show = (hdr: boolean) =>
  render(
    <MantineProvider>
      <InspectorPanel doc={standardDoc()} selection={['c1']} rate={{ num: 30, den: 1 }} readOnly={false} onSetProperty={() => {}} hdrKindFor={() => (hdr ? 'hlg' : null)} />
    </MantineProvider>,
  );

describe('Inspector HDR note', () => {
  it('names an HDR clip and explains the preview', () => {
    show(true);
    expect(screen.getByTestId('inspector-hdr-note').textContent).toBe('HDR clip');
    expect(screen.getByTestId('inspector-parity').getAttribute('data-parity')).toBe('approximate');
    expect(screen.getByTestId('inspector-parity-reason').textContent).toBe(HDR_PREVIEW_REASON);
  });

  it('says nothing for a standard clip', () => {
    show(false);
    expect(screen.queryByTestId('inspector-hdr-note')).toBeNull();
  });
});
