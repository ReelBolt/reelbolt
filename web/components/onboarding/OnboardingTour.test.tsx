// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import OnboardingTour, { placeCard } from './OnboardingTour';
import { TOUR_STEPS } from './tour-steps';
import { guideHref } from '@/lib/onboarding/guide-links';

let sheet = false;
vi.mock('@/lib/hooks/use-device-class', () => ({ useIsPhoneClass: () => sheet }));
beforeEach(() => {
  sheet = false;
  window.matchMedia = ((q: string) => ({
    // The sheet is the phone class (mocked above); reduced motion stays off.
    matches: false,
    media: q, onchange: null,
    addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  Element.prototype.scrollIntoView = vi.fn();
});
afterEach(() => {
  cleanup();
  document.body.innerHTML = '';
  vi.unstubAllGlobals();
});

function renderTour(onClose = vi.fn()) {
  render(
    <MantineProvider>
      <button type="button">opener</button>
      <OnboardingTour onClose={onClose} />
    </MantineProvider>,
  );
  return onClose;
}

/** Puts a fake anchor on the page that reports a real, on-screen box. */
function addAnchor(name: string, rect = { top: 100, left: 40, width: 120, height: 40 }) {
  const el = document.createElement('div');
  el.setAttribute('data-tour', name);
  el.getClientRects = () => [rect as DOMRect] as unknown as DOMRectList;
  el.getBoundingClientRect = () => ({ ...rect, bottom: rect.top + rect.height, right: rect.left + rect.width, x: rect.left, y: rect.top, toJSON() {} }) as DOMRect;
  document.body.appendChild(el);
  return el;
}

describe('OnboardingTour', () => {
  it('is a labelled modal dialog that walks through all eight steps', async () => {
    const user = userEvent.setup();
    const onClose = renderTour();
    const dialog = screen.getByRole('dialog');
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(dialog.getAttribute('aria-labelledby')).toBeTruthy();
    expect(screen.getByRole('heading', { name: TOUR_STEPS[0]!.title })).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Back' }) as HTMLButtonElement).disabled).toBe(true);

    for (let i = 1; i < TOUR_STEPS.length; i += 1) {
      await user.click(screen.getByRole('button', { name: 'Next' }));
      expect(screen.getByRole('heading', { name: TOUR_STEPS[i]!.title })).toBeTruthy();
      expect(screen.getByRole('status').textContent).toBe(`Step ${i + 1} of 8: ${TOUR_STEPS[i]!.title}`);
    }
    await user.click(screen.getByRole('button', { name: 'Back' }));
    expect(screen.getByRole('heading', { name: TOUR_STEPS[6]!.title })).toBeTruthy();
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await user.click(screen.getByRole('button', { name: 'Finish' }));
    expect(onClose).toHaveBeenCalledWith('completed');
  });

  it('skips from the button and from Escape', async () => {
    const user = userEvent.setup();
    const a = renderTour();
    await user.click(screen.getByRole('button', { name: 'Skip tour' }));
    expect(a).toHaveBeenCalledWith('skipped');
    cleanup();
    const b = renderTour();
    fireEvent.keyDown(document.body, { key: 'Escape' });
    expect(b).toHaveBeenCalledWith('skipped');
  });

  it('links every step to its guide in a new tab, keeping the tour', async () => {
    const user = userEvent.setup();
    const onClose = renderTour();
    for (const step of TOUR_STEPS) {
      const link = screen.getByRole('link', { name: /Learn more/ });
      expect(link.getAttribute('href')).toBe(guideHref(step.guide));
      expect(link.getAttribute('target')).toBe('_blank');
      expect(link.getAttribute('rel')).toContain('noopener');
      if (step !== TOUR_STEPS[TOUR_STEPS.length - 1]) await user.click(screen.getByRole('button', { name: 'Next' }));
    }
    expect(onClose).not.toHaveBeenCalled();
  });

  it('keeps focus inside the dialog when tabbing around', async () => {
    const user = userEvent.setup();
    renderTour();
    const dialog = screen.getByRole('dialog');
    await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));
    for (let i = 0; i < 12; i += 1) {
      await user.tab();
      expect(dialog.contains(document.activeElement)).toBe(true);
    }
    for (let i = 0; i < 6; i += 1) {
      await user.tab({ shift: true });
      expect(dialog.contains(document.activeElement)).toBe(true);
    }
  });

  it('gives focus back to what had it when the tour closes', async () => {
    const opener = document.createElement('button');
    document.body.appendChild(opener);
    opener.focus();
    const user = userEvent.setup();
    const { unmount } = render(
      <MantineProvider>
        <OnboardingTour onClose={vi.fn()} />
      </MantineProvider>,
    );
    await user.keyboard('{Tab}');
    unmount();
    expect(document.activeElement).toBe(opener);
  });

  it('centres the card when the anchor is missing', () => {
    renderTour();
    expect(screen.getByRole('dialog').getAttribute('data-placement')).toBe('centered');
    expect(screen.queryByTestId('onboarding-spot')).toBeNull();
  });

  it('centres the card when the anchor is off screen', () => {
    addAnchor('nav', { top: 5000, left: 40, width: 120, height: 40 });
    renderTour();
    expect(screen.getByRole('dialog').getAttribute('data-placement')).toBe('centered');
    expect(screen.queryByTestId('onboarding-spot')).toBeNull();
  });

  it('spotlights a real on-screen anchor', async () => {
    addAnchor('nav');
    renderTour();
    await waitFor(() => expect(screen.getByRole('dialog').getAttribute('data-placement')).toBe('anchored'));
    expect(screen.getByTestId('onboarding-spot')).toBeTruthy();
  });

  it('is a bottom sheet with no spotlight on a phone, even with an anchor', () => {
    sheet = true;
    addAnchor('nav');
    renderTour();
    const dialog = screen.getByRole('dialog');
    expect(dialog.getAttribute('data-placement')).toBe('sheet');
    expect(screen.queryByTestId('onboarding-spot')).toBeNull();
  });

  it('gives every action a 44px target', () => {
    renderTour();
    for (const name of ['Skip tour', 'Back', 'Next']) {
      expect((screen.getByRole('button', { name }) as HTMLElement).style.minHeight).toBe('44px');
    }
  });
});

describe('placeCard', () => {
  const win = { width: 1000, height: 700 };
  const card = { width: 360, height: 200 };
  it('prefers below, then above, then beside, else gives up', () => {
    expect(placeCard({ top: 100, left: 40, width: 100, height: 40 }, card, win)).toEqual({ top: 152, left: 40 });
    expect(placeCard({ top: 600, left: 40, width: 100, height: 40 }, card, win)?.top).toBeLessThan(600);
    expect(placeCard({ top: 0, left: 0, width: 1000, height: 700 }, card, win)).toBeNull();
  });
});
