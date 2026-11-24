// @vitest-environment jsdom
/**
 * The per-user "Allow admin tools over MCP" card (mcp-oauth.md §11).
 *
 * What these tests are actually protecting (the honest-state contract the Lead asked for):
 *
 *   - the switch shows the STORED value the API returns, not an optimistic guess
 *     (off when the server says off, on when it says on);
 *   - a value the server REFUSES (returns a different value than requested) is honoured: the card
 *     re-renders from the value the API returns, never from the value it asked for;
 *   - a failed write surfaces an error and keeps the last confirmed value -- it never optimistically
 *     flips the switch on a write that did not go through;
 *   - a setting that fails to LOAD surfaces an error, not a blank card;
 *   - the switch is a real, labelled, keyboard-reachable control (a native role="switch").
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { useEffect, useState } from 'react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';

const H = vi.hoisted(() => ({
  data: undefined as unknown,
  isLoading: false,
  error: null as null | Error,
  mutate: vi.fn(),
  update: vi.fn(),
  bump: () => {},
}));

vi.mock('@/lib/hooks/use-auth-settings', () => ({
  useAuthSettings: () => ({
    data: H.data,
    isLoading: H.isLoading,
    error: H.error,
    mutate: (val: unknown, opts?: { revalidate?: boolean }) => {
      if (val && typeof val === 'object' && val !== null) H.data = val;
      H.mutate(val, opts);
      setTimeout(() => H.bump(), 0);
    },
  }),
}));
vi.mock('@/lib/api/auth-settings', () => ({
  updateAuthSettings: (...args: unknown[]) => H.update(...args),
}));

import { McpAdminToolsCard } from './McpAdminToolsCard';

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

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);

// Wraps the card in a stateful component so a mutate() (which updates H.data) triggers a re-render,
// exactly as production SWR does.
function Rerenderable({ children }: { children: React.ReactNode }) {
  const [, force] = useState(0);
  useEffect(() => {
    H.bump = () => force((n) => n + 1);
  }, []);
  return <>{children}</>;
}

const renderCard = (offOnly = false) =>
  render(
    <MantineProvider defaultColorScheme="auto">
      <Rerenderable>
        <McpAdminToolsCard offOnly={offOnly} />
      </Rerenderable>
    </MantineProvider>,
  );

const theSwitch = () => screen.getByRole('switch', { name: 'Allow admin tools over MCP' }) as HTMLInputElement;

beforeEach(() => {
  H.data = { mcpAdminToolsEnabled: false };
  H.isLoading = false;
  H.error = null;
  H.mutate = vi.fn();
  H.update = vi.fn().mockResolvedValue({ mcpAdminToolsEnabled: false });
  H.bump = () => {};
});

afterEach(cleanup);

describe('MCP admin-tools card: shows the stored value', () => {
  it('shows the switch off when the stored value is off (the default)', () => {
    H.data = { mcpAdminToolsEnabled: false };
    renderCard();
    expect(theSwitch().checked).toBe(false);
    expect(screen.getByTestId('mcp-admin-tools-state').textContent).toMatch(/^Off/);
  });

  it('shows the switch on when the stored value is on', () => {
    H.data = { mcpAdminToolsEnabled: true };
    renderCard();
    expect(theSwitch().checked).toBe(true);
    expect(screen.getByTestId('mcp-admin-tools-state').textContent).toMatch(/^On/);
  });

  it('shows a loader while the setting is loading, not a switch', () => {
    H.data = undefined;
    H.isLoading = true;
    renderCard();
    expect(screen.queryByRole('switch')).toBeNull();
  });

  it('surfaces an error when the setting fails to load, not a blank card', () => {
    H.data = undefined;
    H.error = new Error('boom');
    renderCard();
    expect(screen.getByTestId('mcp-admin-tools-load-error')).toBeTruthy();
    expect(screen.getByText(/Could not load your MCP settings/i)).toBeTruthy();
  });
});

describe('MCP admin-tools card: the write is honest', () => {
  it('flipping the switch PATCHes the new value', async () => {
    H.data = { mcpAdminToolsEnabled: false };
    H.update = vi.fn().mockResolvedValue({ mcpAdminToolsEnabled: true });
    renderCard();
    fireEvent.click(theSwitch());
    await waitFor(() => expect(H.update).toHaveBeenCalledTimes(1));
    expect(H.update).toHaveBeenCalledWith({ mcpAdminToolsEnabled: true });
  });

  it('re-renders from the value the API returns, not the value it asked for', async () => {
    // The user flips it ON, but the server refuses (stores false). The card must end on the
    // returned value (false), not the requested one (true).
    H.data = { mcpAdminToolsEnabled: false };
    H.update = vi.fn().mockResolvedValue({ mcpAdminToolsEnabled: false });
    renderCard();
    fireEvent.click(theSwitch());
    await waitFor(() => expect(H.update).toHaveBeenCalledWith({ mcpAdminToolsEnabled: true }));
    await waitFor(() =>
      expect(H.mutate).toHaveBeenCalledWith({ mcpAdminToolsEnabled: false }, expect.anything()),
    );
    await waitFor(() => expect(theSwitch().checked).toBe(false));
  });

  it('a successful write that the server accepts re-renders on the returned value (true)', async () => {
    H.data = { mcpAdminToolsEnabled: false };
    H.update = vi.fn().mockResolvedValue({ mcpAdminToolsEnabled: true });
    renderCard();
    fireEvent.click(theSwitch());
    await waitFor(() =>
      expect(H.mutate).toHaveBeenCalledWith({ mcpAdminToolsEnabled: true }, expect.anything()),
    );
    await waitFor(() => expect(theSwitch().checked).toBe(true));
  });

  it('a failed write surfaces an error and keeps the stored value (no optimistic flip)', async () => {
    H.data = { mcpAdminToolsEnabled: false };
    H.update = vi.fn().mockRejectedValue(new ApiError('Could not save your change.', 500));
    renderCard();
    fireEvent.click(theSwitch());
    await waitFor(() =>
      expect(screen.getByTestId('mcp-admin-tools-write-error').textContent).toMatch(
        /Could not save your change\./,
      ),
    );
    // The switch never optimistically flipped to true: it still shows the last confirmed value.
    expect(theSwitch().checked).toBe(false);
  });
});

describe('MCP admin-tools card: accessibility', () => {
  it('is a labelled, keyboard-reachable role="switch" control', () => {
    H.data = { mcpAdminToolsEnabled: false };
    renderCard();
    const sw = theSwitch();
    // Mantine renders its Switch as <input type="checkbox" role="switch">, so it is a native,
    // focusable, keyboard-operable control whose accessible name is the visible label.
    expect(sw.getAttribute('role')).toBe('switch');
    expect(sw.tagName).toBe('INPUT');
    sw.focus();
    expect(document.activeElement).toBe(sw);
  });

  it('announces the state in words, not colour alone', () => {
    H.data = { mcpAdminToolsEnabled: true };
    renderCard();
    expect(screen.getByTestId('mcp-admin-tools-state').textContent).toMatch(
      /On — an MCP agent can use the admin tools\./,
    );
  });
});

describe('MCP admin-tools card: for someone without administrative authority (offOnly)', () => {
  it('is not shown while the setting is off', () => {
    H.data = { mcpAdminToolsEnabled: false };
    renderCard(true);
    expect(screen.queryByTestId('mcp-admin-tools-card')).toBeNull();
  });

  it('is shown while the setting is still on, so it can be turned off', async () => {
    H.data = { mcpAdminToolsEnabled: true };
    renderCard(true);
    expect(theSwitch().disabled).toBe(false);
    fireEvent.click(theSwitch());
    await waitFor(() => expect(H.update).toHaveBeenCalledWith({ mcpAdminToolsEnabled: false }));
  });
});
