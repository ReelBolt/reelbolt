import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

// The approval toast AppShell now mounts reads the pending list, so the stub answers for it too.
const assistant = { opened: false, close: vi.fn(), pendingApprovals: [], revealPendingApproval: vi.fn() };
vi.mock('@/components/assistant/AssistantShellProvider', () => ({ useAssistant: () => assistant }));
vi.mock('@/components/assistant/AssistantTrigger', () => ({ AssistantTrigger: () => null }));
vi.mock('@/components/activity', () => ({ ActivityIndicator: () => <button type="button" data-testid="activity-indicator">activity</button> }));
vi.mock('./InstallPrompt', () => ({ InstallPrompt: () => null }));
vi.mock('./UserMenu', () => ({ UserMenu: () => null }));
// Real NavLinks (the full platform-admin set, 20 rows + 2 labels) so the scroll-container and
// full-list assertions below test the real component; useAuth is stubbed to the admin shape and
// next/navigation stubbed so no router round-trip is needed.
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ isPlatformAdmin: true, isOrgAdmin: true }) }));
vi.mock('@/lib/hooks/use-avatar-library', () => ({ useAvatarLibraryAvailable: () => false }));
let pathname = '/dashboard';
vi.mock('next/navigation', () => ({ usePathname: () => pathname }));
vi.mock('./OrgSwitcher', () => ({ OrgSwitcher: () => <div>Switcher</div> }));

let deviceClass = 'desktop';
vi.mock('@/lib/editing/use-device-class', () => ({ useDeviceClass: () => deviceClass, useIsTouchSurface: () => deviceClass !== 'desktop' }));

import { AppShell } from './AppShell';

Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (q: string) => ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null }),
});

// Mantine's ScrollArea (the navbar's links section) observes its viewport with ResizeObserver,
// which does not exist in jsdom.
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });


const renderShell = () =>
  render(
    <MantineProvider>
      <AppShell>x</AppShell>
    </MantineProvider>,
  );

describe('AppShell burger', () => {
  afterEach(cleanup);
  it('mounts the activity indicator in the header', () => {
    assistant.opened = false;
    renderShell();
    expect(screen.getByTestId('activity-indicator')).toBeTruthy();
  });

  it('toggles the navbar', () => {
    assistant.opened = false;
    renderShell();
    const b = screen.getByRole('button', { name: 'Toggle navigation' });
    fireEvent.click(b);
    expect(b.querySelector('[data-opened]') ?? b.hasAttribute('data-opened')).toBeTruthy();
    expect(assistant.close).not.toHaveBeenCalled();
  });

  it('closes the navbar when the page changes by any route, not only its own links', () => {
    assistant.opened = false;
    pathname = '/dashboard';
    const view = renderShell();
    const b = screen.getByRole('button', { name: 'Toggle navigation' });
    fireEvent.click(b);
    expect(b.querySelector('[data-opened]') ?? b.hasAttribute('data-opened')).toBeTruthy();

    // e.g. the browser's back button, or a link inside the page
    pathname = '/projects';
    view.rerender(
      <MantineProvider>
        <AppShell>x</AppShell>
      </MantineProvider>,
    );
    expect(b.hasAttribute('data-opened') || b.querySelector('[data-opened]') !== null).toBe(false);
    pathname = '/dashboard';
  });

  it('starts closed again when the app comes back from the background', () => {
    assistant.opened = false;
    renderShell();
    const b = screen.getByRole('button', { name: 'Toggle navigation' });
    fireEvent.click(b);
    // A resume within ~300ms of the tap is the tap's own gesture and is ignored; this one is later.
    const realNow = Date.now;
    Date.now = () => realNow() + 1000;
    act(() => {
      window.dispatchEvent(new Event('pageshow'));
    });
    Date.now = realNow;
    expect(b.hasAttribute('data-opened') || b.querySelector('[data-opened]') !== null).toBe(false);
  });

  it('closes an open assistant sheet instead of being swallowed by its overlay', () => {
    assistant.opened = true;
    assistant.close.mockClear();
    renderShell();
    fireEvent.click(screen.getByRole('button', { name: 'Toggle navigation' }));
    expect(assistant.close).toHaveBeenCalledTimes(1);
  });
});

describe('AppShell header branding', () => {
  afterEach(cleanup);

  it('draws the inline brand mark rather than loading /app/logo.svg', () => {
    assistant.opened = false;
    const { container } = renderShell();
    expect(container.querySelector('img[src="/app/logo.svg"]')).toBeNull();
    const mark = container.querySelector('svg[viewBox="0 0 120 120"]');
    expect(mark?.getAttribute('aria-hidden')).toBe('true');
    expect(screen.getByText('ReelBolt')).toBeTruthy();
  });

  // The app name is chrome, not a page section: it must still name the header (the mark beside it
  // is aria-hidden and relies on this text) without putting an h1-h6 on every page in the app.
  it('names the header without putting a heading into the document outline', () => {
    assistant.opened = false;
    const { container } = renderShell();
    const brand = screen.getByText('ReelBolt');
    expect(brand.closest('header')).not.toBeNull();
    expect(brand.tagName).not.toMatch(/^H[1-6]$/);
    const headingText = Array.from(container.querySelectorAll('h1, h2, h3, h4, h5, h6')).map(
      (h) => h.textContent ?? '',
    );
    expect(headingText.filter((text) => text.includes('ReelBolt'))).toHaveLength(0);
  });
});

describe('AppShell navbar', () => {
  afterEach(cleanup);

  it('puts the workspace switcher above the navigation, so the active workspace is always visible', () => {
    assistant.opened = false;
    renderShell();
    const switcher = screen.getByText('Switcher');
    // The switcher sits in its own non-growing section, above the (scrolling) links section.
    const linksScroll = screen.getByTestId('nav-links-scroll');
    expect(switcher).toBeTruthy();
    expect(switcher.compareDocumentPosition(linksScroll) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('scrolls its link region (a ScrollArea) so a long platform-admin list never becomes unreachable', () => {
    assistant.opened = false;
    renderShell();
    // The growing section hosts a ScrollArea: the section carries the test id and its
    // viewport (the actual scroll container) is its [data-scrollarea-viewport] child.
    const section = screen.getByTestId('nav-links-scroll');
    const viewport = section.querySelector('[data-scrollarea-viewport]');
    expect(viewport).toBeTruthy();
    // The switcher is OUTSIDE the scroll region: it is pinned at the top of the navbar.
    expect(section.contains(screen.getByText('Switcher'))).toBe(false);
    // And the links are INSIDE it.
    expect(section.contains(screen.getByText('Dashboard'))).toBe(true);
  });

  it('shows the platform-admin menu compactly, and every one of its links stays reachable', () => {
    assistant.opened = false;
    renderShell();

    // The six destinations the shipped user guide calls top-level, on screen with nothing to open.
    for (const label of ['Dashboard', 'Projects', 'Editor', 'Publishing']) {
      expect(screen.getByRole('link', { name: label })).toBeTruthy();
    }

    // The three groups start closed. Twenty flat rows is the crowding the compaction removed, and a
    // closed group is what buys it back.
    for (const label of ['More', 'Settings', 'Workspace', 'Platform']) {
      const toggle = screen.getByRole('button', { name: label });
      expect(toggle.tagName).toBe('BUTTON');
      expect(toggle.getAttribute('aria-expanded')).toBe('false');
    }

    // ...and one activation each puts every link back. If collapsing ever stranded an item, this is
    // where it would show: the same 19 labels, all reachable by role, are asserted here.
    for (const label of ['More', 'Settings', 'Workspace', 'Platform']) {
      fireEvent.click(screen.getByRole('button', { name: label }));
    }
    for (const label of [
      'Dashboard', 'Projects', 'Editor', 'Publishing', 'Styles', 'Runners', 'Agents (advanced)',
      'Account', 'Usage', 'Plan & billing',
      'Settings', 'Members', 'Providers',
      'Overview', 'Workflow Service', 'Inference', 'Billing', 'Promotions', 'Calibration', 'Skills', 'Users',
    ]) {
      expect(screen.getByRole('link', { name: label })).toBeTruthy();
    }
    // Group labels are plain Text inside a button, not disabled NavLinks: they read as disclosures,
    // not as dead controls, and they put no heading into the document outline.
    expect(screen.getAllByText('Workspace')).toHaveLength(1);
    expect(screen.getAllByText('Platform')).toHaveLength(1);
  }, 30_000);
});

describe('AppShell alpha banner on the editor', () => {
  afterEach(() => {
    cleanup();
    pathname = '/dashboard';
  });

  it('shows the early-access banner on the dashboard but not on the timeline editor, which needs the space', () => {
    assistant.opened = false;
    pathname = '/dashboard';
    renderShell();
    expect(screen.queryByTestId('alpha-banner')).not.toBeNull();
    cleanup();
    pathname = '/editor/p1/timeline/t1';
    renderShell();
    expect(screen.queryByTestId('alpha-banner')).toBeNull();
  });
});

describe('AppShell skip link', () => {
  afterEach(cleanup);

  it('renders a skip link as the first tab stop that targets the main landmark', () => {
    pathname = '/projects';
    renderShell();
    const link = screen.getByRole('link', { name: 'Skip to main content' });
    expect(link.getAttribute('href')).toBe('#main-content');
    expect(document.querySelector('main')?.id).toBe('main-content');
    expect(document.querySelector('a, button')).toBe(link);
    pathname = '/dashboard';
  });
});

describe('AppShell phone touch targets', () => {
  afterEach(cleanup);

  it('opts the chrome and the page into the 44px touch-target rules, except on the editor', () => {
    pathname = '/projects';
    renderShell();
    expect(screen.getByTestId('app-header').classList.contains('touch-targets')).toBe(true);
    expect(document.querySelector('main')?.classList.contains('touch-targets')).toBe(true);
    cleanup();
    pathname = '/editor/p1';
    renderShell();
    expect(screen.getByTestId('app-header').classList.contains('touch-targets')).toBe(false);
    pathname = '/dashboard';
  });

  it('keeps the closed navbar out of the tab order on a phone and releases it when opened', () => {
    const mq = window.matchMedia;
    window.matchMedia = ((q: string) => ({ ...mq(q), matches: q.includes('max-width: 1100px') })) as typeof window.matchMedia;
    try {
      pathname = '/dashboard';
      renderShell();
      const nav = document.querySelector('nav');
      expect(nav?.hasAttribute('inert')).toBe(true);
      fireEvent.click(screen.getByRole('button', { name: 'Toggle navigation' }));
      expect(document.querySelector('nav')?.hasAttribute('inert')).toBe(false);
    } finally {
      window.matchMedia = mq;
    }
  });

  describe('phone open state', () => {
    const mq = window.matchMedia;
    const setVisibility = (v: string) =>
      Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => v });
    const navEl = () => document.querySelector('nav') as HTMLElement;
    const burger = () => screen.getByRole('button', { name: 'Toggle navigation' });
    beforeEach(() => {
      window.matchMedia = ((q: string) => ({ ...mq(q), matches: q.includes('max-width: 1100px') })) as typeof window.matchMedia;
      assistant.opened = false;
      pathname = '/dashboard';
      vi.useFakeTimers({ toFake: ['Date'] });
    });
    afterEach(() => {
      window.matchMedia = mq;
      setVisibility('visible');
      vi.useRealTimers();
      pathname = '/dashboard';
    });
    const openThenWait = () => {
      renderShell();
      expect(navEl().hasAttribute('data-opened')).toBe(false);
      fireEvent.click(burger());
      expect(navEl().hasAttribute('data-opened')).toBe(true);
      act(() => {
        vi.setSystemTime(Date.now() + 1000);
      });
    };

    it('marks the navbar data-opened only while open on a phone', () => {
      renderShell();
      expect(navEl().hasAttribute('data-opened')).toBe(false);
      expect(navEl().getAttribute('aria-hidden')).toBe('true');
      fireEvent.click(burger());
      expect(navEl().hasAttribute('data-opened')).toBe(true);
      expect(navEl().hasAttribute('aria-hidden')).toBe(false);
      expect(navEl().hasAttribute('inert')).toBe(false);
    });

    it('keeps data-opened in step with the burger even when the JS width query lags the viewport', () => {
      // The CSS that shows the menu is phone-scoped by @media; if data-opened also waited for the JS
      // query, a stale `narrow` would hide an open menu behind an X burger (the reported symptom).
      window.matchMedia = mq;
      renderShell();
      fireEvent.click(burger());
      expect(navEl().hasAttribute('data-opened')).toBe(true);
    });

    it('closes on pageshow, resume and visibility', () => {
      for (const fire of [
        () => window.dispatchEvent(new Event('pageshow')),
        () => document.dispatchEvent(new Event('resume')),
        () => document.dispatchEvent(new Event('visibilitychange')),
      ]) {
        openThenWait();
        act(fire);
        expect(navEl().hasAttribute('data-opened')).toBe(false);
        cleanup();
      }
    });

    it('closes on focus only after the document was hidden', () => {
      openThenWait();
      act(() => {
        window.dispatchEvent(new Event('focus'));
      });
      expect(navEl().hasAttribute('data-opened')).toBe(true);
      setVisibility('hidden');
      act(() => {
        document.dispatchEvent(new Event('visibilitychange'));
        window.dispatchEvent(new Event('focus'));
      });
      expect(navEl().hasAttribute('data-opened')).toBe(false);
    });

    it('does not let a resume right after a tap undo the tap', () => {
      renderShell();
      fireEvent.click(burger());
      act(() => {
        document.dispatchEvent(new Event('visibilitychange'));
      });
      expect(navEl().hasAttribute('data-opened')).toBe(true);
    });

    it('keeps the navbar collapsed in the phone editor', () => {
      pathname = '/editor/p1/timeline/t1';
      deviceClass = 'phone';
      try {
        // The header (and so the burger) is hidden here; the navbar is collapsed and inert regardless.
        renderShell();
        expect(navEl().hasAttribute('data-opened')).toBe(false);
        expect(navEl().hasAttribute('inert')).toBe(true);
      } finally {
        deviceClass = 'desktop';
      }
    });

    it('closes an open assistant when the navbar opens', () => {
      assistant.opened = true;
      assistant.close.mockClear();
      renderShell();
      fireEvent.click(burger());
      expect(assistant.close).toHaveBeenCalled();
    });

    it('scopes the open and closed rules to the phone breakpoint', () => {
      const css = readFileSync(join(__dirname, 'chrome.module.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
      const phone = css.slice(css.indexOf('@media (max-width: 1100.98px)'));
      expect(phone).toMatch(/\.navbar\[data-opened\]\s*\{[^}]*transform: none !important[^}]*visibility: visible[^}]*z-index: 190/);
      expect(phone).toMatch(/\.navbar:not\(\[data-opened\]\)\s*\{[^}]*visibility: hidden[^}]*visibility 0s linear/);
      expect(phone).toContain('prefers-reduced-motion');
      expect(css.slice(0, css.indexOf('@media (max-width: 1100.98px)'))).not.toContain('data-opened');
    });
  });
});

describe('AppShell on the welcome questions', () => {
  afterEach(() => {
    cleanup();
    pathname = '/dashboard';
  });

  it('steps aside: no navigation to wander off to, the brand and the page stay', () => {
    assistant.opened = false;
    pathname = '/welcome';
    const { container } = renderShell();
    expect(screen.queryByRole('button', { name: 'Toggle navigation' })).toBeNull();
    const nav = container.querySelector('[data-tour="nav"]');
    expect(nav?.hasAttribute('inert')).toBe(true);
    expect(nav?.getAttribute('aria-hidden')).toBe('true');
    expect(nav?.hasAttribute('data-opened')).toBe(false);
    expect(screen.queryByTestId('activity-indicator')).toBeNull();
    expect(screen.getByText('ReelBolt')).toBeTruthy();
    expect(screen.getByText('x')).toBeTruthy();
  });

  it('is the ordinary shell on every other page, including a page under /welcome-like names', () => {
    assistant.opened = false;
    pathname = '/projects/welcome';
    const { container } = renderShell();
    expect(screen.getByRole('button', { name: 'Toggle navigation' })).toBeTruthy();
    expect(screen.getByTestId('activity-indicator')).toBeTruthy();
    expect(container.querySelector('[data-tour="nav"]')?.hasAttribute('inert')).toBe(false);
  });
});
