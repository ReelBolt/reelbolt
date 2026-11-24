// @vitest-environment jsdom
/** T-003: Deliver is a page in the shared frame — settings | result, no centre, no timeline. */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { DeliverWorkspace } from '../DeliverWorkspace';

let estimateImpl: () => Promise<unknown> = async () => ({});
vi.mock('@/lib/editing/sync/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/sync/api')>()),
  estimateEditTimelineRender: () => estimateImpl(),
}));
vi.mock('@/lib/editing/use-device-class', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/use-device-class')>()),
  useDeviceClass: () => 'desktop',
}));
vi.mock('@/lib/hooks/use-execution-stream', () => ({ useExecutionStream: () => ({ events: [] }) }));
vi.mock('@/lib/hooks/use-billing', () => ({ useOutputHeightCap: () => null }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('../../presets/PresetPicker', () => ({ PresetPicker: () => null }));
vi.mock('next/link', () => ({ default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a> }));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;

beforeEach(() => {
  estimateImpl = async () => ({});
});
afterEach(() => cleanup());

describe('Deliver in the frame', () => {
  it('renders the settings column and the result column into the frame’s two zones', () => {
    const controls = document.createElement('div');
    const result = document.createElement('div');
    document.body.append(controls, result);
    try {
      render(
        <MantineProvider>
          <DeliverWorkspace
            projectId="p"
            timelineId="t"
            revision={1}
            dirty={false}
            readOnly={false}
            inOut={{ inFrame: null, outFrame: null }}
            renders={[]}
            rendersLoading={false}
            refreshRenders={() => {}}
            zoneTargets={{ controls, result }}
          />
        </MantineProvider>,
      );
      const settings = controls.querySelector('[data-testid="deliver-controls"]');
      expect(settings, 'the settings column fills the left zone').not.toBeNull();
      expect(settings?.querySelector('[data-testid="render-settings"]')).not.toBeNull();
      expect(settings?.querySelector('[data-testid="render-start"]')).not.toBeNull();
      // Wave 5: presets lead the settings column and the cost statement sits with Render at its foot.
      expect(settings?.querySelector('[data-testid="render-preset"]')).not.toBeNull();
      expect(settings?.querySelector('[data-testid="render-cost"]')).not.toBeNull();
      expect(settings?.querySelector('[data-testid="render-queue-empty"]'), 'the queue moved to the result column').toBeNull();

      const output = result.querySelector('[data-testid="deliver-result"]');
      expect(output, 'the result column fills the rest').not.toBeNull();
      expect(output?.querySelector('[data-testid="film-empty"]')).not.toBeNull();
      expect(output?.querySelector('[data-testid="render-queue-empty"]')).not.toBeNull();
      expect(output?.querySelector('[data-testid="review-links"]')).not.toBeNull();
      expect(output?.querySelector('[data-testid="export-panel"]')).not.toBeNull();
      expect(output?.querySelector('[data-testid="handoff-card"]')).not.toBeNull();
      // The framed page does not draw its own panel header or a second workspace root.
      expect(screen.queryByTestId('deliver-workspace')).toBeNull();
    } finally {
      controls.remove();
      result.remove();
    }
  });
});
