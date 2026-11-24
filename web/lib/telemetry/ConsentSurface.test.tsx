// @vitest-environment jsdom
/**
 * The app-side consent surface (alpha-launch T-018). What these protect:
 *   - it renders nothing when the build has not enabled telemetry (self-host unchanged);
 *   - it renders nothing once a choice exists;
 *   - it asks once, as a named, portaled, non-modal dialog with a payload preview;
 *   - Allow grants, Not now denies, and either way it stops rendering;
 *   - ESC or the close button dismisses it for the page it was dismissed on, every other page
 *     asks again, and a dismissal writes nothing.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { TELEMETRY_CONSENT_STORAGE_KEY, readTelemetryConsent } from './consent';
import {
  TELEMETRY_CONSENT_ALLOW,
  TELEMETRY_CONSENT_DENY,
  TELEMETRY_CONSENT_TITLE,
  TELEMETRY_CONSENT_WORDING,
  TelemetryConsentSurface,
} from './ConsentSurface';

// The pathname the surface reads. The next-navigation test flips it and re-renders, which is what a
// route change looks like to a client component. Mirrors components/shell/AppShell.test.tsx.
let pathname = '/dashboard';
vi.mock('next/navigation', () => ({ usePathname: () => pathname }));

// MantineProvider and useReducedMotion read window.matchMedia, which jsdom does not implement. The
// only query the surface asks about is reduced motion, so that one reports true and the rest false —
// the state the reduced-motion test below pins.
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (q: string) => ({
    matches: q === '(prefers-reduced-motion: reduce)',
    media: q,
    addEventListener() {},
    removeEventListener() {},
    addListener() {},
    removeListener() {},
    dispatchEvent: () => false,
    onchange: null,
  }),
});

function renderSurface() {
  return render(
    <MantineProvider>
      <TelemetryConsentSurface />
    </MantineProvider>,
  );
}

beforeEach(() => {
  window.localStorage.clear();
  pathname = '/dashboard';
  process.env.NEXT_PUBLIC_TELEMETRY_ENABLED = 'true';
  // A choice now posts the decision; keep the test hermetic (the send is best-effort anyway).
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({} as Response)));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  delete process.env.NEXT_PUBLIC_TELEMETRY_ENABLED;
});

describe('TelemetryConsentSurface', () => {
  it('renders nothing when telemetry is not enabled for the build', () => {
    process.env.NEXT_PUBLIC_TELEMETRY_ENABLED = 'false';
    renderSurface();
    // Absence of the *output*, not just of the role: a regression that rendered some other element
    // would still leave the title on screen, and that is what the user would see.
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.queryByText(TELEMETRY_CONSENT_TITLE)).toBeNull();
  });

  it('renders nothing when a choice has already been made', () => {
    window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, 'denied');
    renderSurface();
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.queryByText(TELEMETRY_CONSENT_TITLE)).toBeNull();
  });

  it('asks once, as a named dialog with a payload preview, when there is no choice yet', () => {
    renderSurface();
    const dialog = screen.getByRole('dialog', { name: TELEMETRY_CONSENT_TITLE });
    expect(dialog).toBeTruthy();
    expect(screen.getByText(TELEMETRY_CONSENT_WORDING)).toBeTruthy();
    expect(screen.getByTestId('telemetry-payload-preview').textContent).toContain('alpha.signup');
    expect(screen.getByTestId('telemetry-error-preview').textContent).toContain('TypeError');
    // The disclosure names the decision itself, which is the one thing a declining browser still
    // sends. It collapses with the rest of the preview — it must not vanish (PR #437).
    const consentPreview = screen.getByTestId('telemetry-consent-preview').textContent ?? '';
    expect(consentPreview).toContain('telemetry.consent.granted');
    expect(consentPreview).toContain('telemetry.consent.declined');
  });

  it('grants on Allow and stops asking', () => {
    renderSurface();
    fireEvent.click(screen.getByText(TELEMETRY_CONSENT_ALLOW));
    expect(readTelemetryConsent()).toBe('granted');
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('denies on Not now and stops asking', () => {
    renderSurface();
    fireEvent.click(screen.getByText(TELEMETRY_CONSENT_DENY));
    expect(readTelemetryConsent()).toBe('denied');
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('is portaled: the dialog is not inside the component\'s own subtree', () => {
    const { container } = renderSurface();
    const dialog = screen.getByRole('dialog', { name: TELEMETRY_CONSENT_TITLE });
    expect(container.contains(dialog)).toBe(false);
  });

  it('closes on Escape without writing a choice, and asks again on the next navigation', () => {
    const view = renderSurface();
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(readTelemetryConsent()).toBeNull();

    pathname = '/projects';
    view.rerender(
      <MantineProvider>
        <TelemetryConsentSurface />
      </MantineProvider>,
    );
    expect(screen.getByRole('dialog', { name: TELEMETRY_CONSENT_TITLE })).toBeTruthy();
  });

  it('does not reappear on the page it was dismissed on', () => {
    const view = renderSurface();
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    view.rerender(
      <MantineProvider>
        <TelemetryConsentSurface />
      </MantineProvider>,
    );
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('closes on the close button too, and still writes no choice', () => {
    renderSurface();
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss for now' }));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(readTelemetryConsent()).toBeNull();
  });

  it('renders without a transition when the visitor asks for reduced motion', () => {
    // matchMedia stub returns matches:true only for '(prefers-reduced-motion: reduce)'
    renderSurface();
    const dialog = screen.getByRole('dialog', { name: TELEMETRY_CONSENT_TITLE });
    expect(dialog.style.transitionDuration || '0ms').toBe('0ms');
  });
});
