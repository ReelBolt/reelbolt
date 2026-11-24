// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const startTour = vi.fn();
vi.mock('@/components/onboarding/OnboardingProvider', () => ({ useTourLauncher: () => ({ startTour }) }));

import { HelpMenu } from './HelpMenu';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();
afterEach(cleanup);

describe('HelpMenu', () => {
  it('offers "Take the tour"', async () => {
    window.matchMedia = ((q: string) => ({
      matches: false, media: q, onchange: null,
      addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false,
    })) as unknown as typeof matchMedia;
    render(<MantineProvider><HelpMenu /></MantineProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Help' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Take the tour' }));
    expect(startTour).toHaveBeenCalledTimes(1);
  });

  it('opens the written guides in a new tab', async () => {
    window.matchMedia = ((q: string) => ({
      matches: false, media: q, onchange: null,
      addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false,
    })) as unknown as typeof matchMedia;
    render(<MantineProvider><HelpMenu /></MantineProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Help' }));
    const guides = await screen.findByRole('menuitem', { name: 'Read the guides' });
    expect(guides.getAttribute('href')).toBe('/docs/basics/');
    expect(guides.getAttribute('target')).toBe('_blank');
    const docs = screen.getByRole('menuitem', { name: 'Documentation', hidden: true });
    expect(docs.getAttribute('href')).toBe('/docs/');
    expect(docs.getAttribute('target')).toBe('_blank');
  });
});
