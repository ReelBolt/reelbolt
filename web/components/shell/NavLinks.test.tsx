// @vitest-environment jsdom
/**
 * The dashboard's left navigation, after the compaction pass.
 *
 * The whole point of this file is the property the task named: a collapsed section must not be a
 * regression. These tests pin, in order:
 *
 *   - the pinned band survives (the destinations the user guide calls top-level are one click away);
 *   - the default is compact, with the group holding the current page already open;
 *   - a collapsed group's links are out of the ACCESSIBILITY TREE (getByRole refuses them) while
 *     still being in the DOM (getByRole with hidden:true finds them) -- i.e. hidden, not deleted;
 *   - the toggle that reverses it is a real focusable <button> carrying aria-expanded and an
 *     aria-controls that names a real element, so Tab + Enter always gets the items back;
 *   - collapsing the group you are IN is not a dead control, but navigating into a collapsed group
 *     opens it;
 *   - the nav adds no heading, which is the outline fix from f7d55d643.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const H = vi.hoisted(() => ({
  pathname: '/dashboard',
  isPlatformAdmin: true,
  isOrgAdmin: true,
  avatarLibrary: false,
}));

vi.mock('next/navigation', () => ({ usePathname: () => H.pathname }));
vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({ isPlatformAdmin: H.isPlatformAdmin, isOrgAdmin: H.isOrgAdmin }),
}));

// The workspace avatar library answers (or not) per test: no request leaves the unit test.
vi.mock('@/lib/hooks/use-avatar-library', () => ({ useAvatarLibraryAvailable: () => H.avatarLibrary }));

import { NavLinks } from './NavLinks';

// MantineProvider reads matchMedia('(prefers-color-scheme: dark)') on mount to set the colour-scheme
// attribute; jsdom has no implementation of it.
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (q: string) => ({
    matches: false,
    media: q,
    addEventListener() {},
    removeEventListener() {},
    addListener() {},
    removeListener() {},
    dispatchEvent: () => false,
    onchange: null,
  }),
});

const renderNav = () =>
  render(
    <MantineProvider>
      <NavLinks />
    </MantineProvider>,
  );

const toggleFor = (label: string) => screen.getByRole('button', { name: label });

/** The panel a toggle owns, via the id in its aria-controls. */
const panelFor = (label: string) => {
  const id = toggleFor(label).getAttribute('aria-controls');
  expect(id).toBeTruthy();
  const panel = document.getElementById(id as string);
  expect(panel).toBeTruthy();
  return panel as HTMLElement;
};

beforeEach(() => {
  H.pathname = '/dashboard';
  H.isPlatformAdmin = true;
  H.isOrgAdmin = true;
  H.avatarLibrary = false;
});

afterEach(cleanup);

describe('NavLinks pinned band', () => {
  it('keeps the top-level destinations out of every disclosure', () => {
    renderNav();
    for (const label of ['Dashboard', 'Projects', 'Editor', 'Publishing']) {
      expect(screen.getByRole('link', { name: label })).toBeTruthy();
    }
  });
});

describe('NavLinks default state', () => {
  it('starts compact: every group is collapsed when the current page is in none of them', () => {
    renderNav();
    for (const label of ['More', 'Settings', 'Workspace', 'Platform']) {
      expect(toggleFor(label).getAttribute('aria-expanded')).toBe('false');
    }
  });

  it('opens the group that holds the current page, so "you are here" is never hidden on arrival', () => {
    H.pathname = '/settings/usage';
    renderNav();
    expect(toggleFor('Settings').getAttribute('aria-expanded')).toBe('true');
    expect(toggleFor('Workspace').getAttribute('aria-expanded')).toBe('false');
    expect(screen.getByRole('link', { name: 'Usage' })).toBeTruthy();
  });

  it('matches an entry exactly where one href is a prefix of another', () => {
    H.pathname = '/settings/billing';
    renderNav();
    // Account is '/settings' and must not read as the current page while you are on billing.
    expect(screen.getByRole('link', { name: 'Account' }).getAttribute('aria-current')).not.toBe('page');
  });

  it('links Your data to /settings/data and marks it current there, not Account (S-22)', () => {
    H.pathname = '/settings/data';
    renderNav();
    const link = screen.getByRole('link', { name: 'Your data' });
    expect(link.getAttribute('href')).toContain('/settings/data');
    expect(link.hasAttribute('data-active')).toBe(true);
    expect(screen.getByRole('link', { name: 'Account' }).hasAttribute('data-active')).toBe(false);
  });
});

describe('NavLinks collapsed sections stay reachable', () => {
  it('takes a collapsed group out of the accessibility tree while leaving it in the DOM', () => {
    renderNav();
    // Not reachable by role: dom-accessibility-api treats [hidden] as display:none, so this is the
    // same question a screen reader asks.
    expect(screen.queryByRole('link', { name: 'Members' })).toBeNull();
    // Still present, just hidden -- the item was not deleted, only folded away.
    expect(screen.getByRole('link', { name: 'Members', hidden: true })).toBeTruthy();
    expect(panelFor('Workspace').hasAttribute('hidden')).toBe(true);
  });

  it('exposes the toggle as a focusable button that names the panel it controls', () => {
    renderNav();
    const toggle = toggleFor('Platform');
    expect(toggle.tagName).toBe('BUTTON');
    // Not taken out of the tab order -- this is the path back to the hidden items.
    expect(toggle.getAttribute('tabindex')).not.toBe('-1');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(panelFor('Platform').hasAttribute('hidden')).toBe(true);
  });

  it('restores the whole group on one activation, and every documented item with it', () => {
    renderNav();
    fireEvent.click(toggleFor('More'));
    fireEvent.click(toggleFor('Settings'));
    fireEvent.click(toggleFor('Workspace'));
    fireEvent.click(toggleFor('Platform'));

    for (const label of ['More', 'Settings', 'Workspace', 'Platform']) {
      expect(toggleFor(label).getAttribute('aria-expanded')).toBe('true');
      expect(panelFor(label).hasAttribute('hidden')).toBe(false);
    }

    // Every entry the navigation offers, reachable at once. If an item ever goes missing, this is
    // the assertion that says so.
    for (const label of [
      'Dashboard', 'Projects', 'Editor', 'Publishing', 'Styles', 'Runners', 'Agents (advanced)',
      'Account', 'Usage', 'Plan & billing',
      'Settings', 'Members', 'Providers',
      'Overview', 'Workflow Service', 'Inference', 'Billing', 'Promotions', 'Links', 'Calibration', 'Skills', 'Users',
    ]) {
      expect(screen.getByRole('link', { name: label })).toBeTruthy();
    }
  });

  it('shows the group label and its link separately, so "Settings" is never ambiguous by role', () => {
    renderNav();
    fireEvent.click(toggleFor('Settings'));
    fireEvent.click(toggleFor('Workspace'));
    expect(screen.getAllByRole('button', { name: 'Settings' })).toHaveLength(1);
    expect(screen.getAllByRole('link', { name: 'Settings' })).toHaveLength(1);
  });
});

describe('NavLinks following the route', () => {
  it('is not a dead control on the current page: an explicit collapse sticks', () => {
    H.pathname = '/settings/usage';
    renderNav();
    fireEvent.click(toggleFor('Settings'));
    expect(toggleFor('Settings').getAttribute('aria-expanded')).toBe('false');
    // ...and the collapsed header still says the page you are on is inside it.
    expect(toggleFor('Settings').getAttribute('data-current')).toBe('true');
  });

  it('re-opens a group you collapsed once you navigate into it', () => {
    H.pathname = '/settings/usage';
    const { rerender } = renderNav();
    fireEvent.click(toggleFor('Settings'));
    expect(toggleFor('Settings').getAttribute('aria-expanded')).toBe('false');

    H.pathname = '/settings/billing';
    rerender(
      <MantineProvider>
        <NavLinks />
      </MantineProvider>,
    );
    expect(toggleFor('Settings').getAttribute('aria-expanded')).toBe('true');
  });
});

describe('NavLinks outline', () => {
  it('adds no heading, so the menu cannot pollute the document outline', () => {
    const { container } = renderNav();
    expect(container.querySelectorAll('h1, h2, h3, h4, h5, h6')).toHaveLength(0);
  });
});

describe('NavLinks by authority', () => {
  it('hides the admin groups from a plain member but keeps the settings surface', () => {
    H.isPlatformAdmin = false;
    H.isOrgAdmin = false;
    renderNav();
    expect(screen.queryByRole('button', { name: 'Workspace' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Platform' })).toBeNull();
    expect(toggleFor('Settings')).toBeTruthy();
  });

  it('shows the Workspace group to an org admin who is not a platform admin', () => {
    H.isPlatformAdmin = false;
    renderNav();
    expect(toggleFor('Workspace')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Platform' })).toBeNull();
  });
});

/**
 * The workspace avatar library (docs/design/avatar-objects.md): a settings entry every member sees, listed only
 * while the server answers the library. With both avatar switches off there is no library and so no link.
 */
describe('NavLinks avatar library', () => {
  it('offers no link while the server has no avatar library', () => {
    H.pathname = '/settings/usage';
    renderNav();
    expect(screen.queryByRole('link', { name: 'Avatar library' })).toBeNull();
    expect(document.querySelector('a[href="/settings/avatar-library"]')).toBeNull();
  });

  it('links a plain member to /settings/avatar-library and marks it current there, not Account', () => {
    H.avatarLibrary = true;
    H.isPlatformAdmin = false;
    H.isOrgAdmin = false;
    H.pathname = '/settings/avatar-library';
    renderNav();
    const link = screen.getByRole('link', { name: 'Avatar library' });
    expect(link.getAttribute('href')).toContain('/settings/avatar-library');
    expect(link.hasAttribute('data-active')).toBe(true);
    expect(screen.getByRole('link', { name: 'Account' }).hasAttribute('data-active')).toBe(false);
    expect(toggleFor('Settings').getAttribute('aria-expanded')).toBe('true');
  });
});

/**
 * T-014: the video editor is off in the alpha, so the nav must not offer a way in. This is the
 * CLIENT render (jsdom) on purpose — the flag arrives as a prop threaded from the server shell, and
 * the defect being pinned is exactly what a client component reading the environment gets wrong.
 */
describe('NavLinks video editor flag', () => {
  const renderWithFlag = (videoEditorEnabled: boolean) =>
    render(
      <MantineProvider>
        <NavLinks videoEditorEnabled={videoEditorEnabled} />
      </MantineProvider>,
    );

  it('offers the Editor link while the video editor is on', () => {
    renderWithFlag(true);
    expect(screen.getByRole('link', { name: 'Editor' }).getAttribute('href')).toContain('/editor');
    expect(screen.queryByText('Video editor · Coming soon')).toBeNull();
  });

  it('shows the coming-soon label, and no link into the gated path, while it is off', () => {
    renderWithFlag(false);
    expect(screen.getByText('Video editor · Coming soon')).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Editor' })).toBeNull();
    expect(document.querySelector('a[href="/editor"]')).toBeNull();
  });
});

describe('NavLinks More section', () => {
  it('collapses Agents, Styles and Runners by default and opens on click', () => {
    renderNav();
    expect(toggleFor('More').getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByRole('link', { name: 'Styles' })).toBeNull();
    fireEvent.click(toggleFor('More'));
    for (const label of ['Styles', 'Runners', 'Agents (advanced)']) {
      expect(screen.getByRole('link', { name: label })).toBeTruthy();
    }
  });

  it('auto-expands when the current route is inside it', () => {
    H.pathname = '/runners/pair';
    renderNav();
    expect(toggleFor('More').getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByRole('link', { name: 'Runners' })).toBeTruthy();
  });
});
