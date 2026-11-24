// @vitest-environment jsdom
/**
 * P4-06: the org brand-library link on the project profile tab (SPEC §16.1). The library list is
 * mocked and the link call is a mock, so the tests pin set and clear.
 */
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { DesignLibrarySummary } from '@/lib/types/design-system';

const H = vi.hoisted(() => ({
  libraries: [] as unknown[],
  link: vi.fn(),
}));

vi.mock('@/lib/hooks/use-design-libraries', () => ({
  useDesignLibraries: () => ({ data: H.libraries, error: undefined, isLoading: false }),
  useDesignLibraryVersions: () => ({ data: [], error: undefined, isLoading: false }),
}));
vi.mock('@/lib/api/design-system', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/design-system')>();
  return {
    ...actual,
    linkProjectLibrary: (...args: unknown[]) => H.link(...args),
  };
});

import { LibraryLinkSelect } from './LibraryLinkSelect';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({
      matches: false,
      addListener() {},
      removeListener() {},
      addEventListener() {},
      removeEventListener() {},
    }),
  });
  global.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as never;
  Element.prototype.scrollIntoView = vi.fn();
});

beforeEach(() => {
  H.libraries = [library()];
  H.link.mockReset().mockResolvedValue(undefined);
});

afterEach(cleanup);

function library(): DesignLibrarySummary {
  return {
    id: 'lib-1',
    organizationId: 'org-1',
    name: 'Acme',
    designSystemId: 'ds-1',
    designSystem: {
      id: 'ds-1',
      kind: 'design',
      name: 'Acme',
      currentVersion: {
        id: 'v-2',
        versionNumber: 2,
        digest: 'digest',
        approvedAt: '2026-01-01T00:00:00Z',
      },
      latestDraft: null,
      pendingProposals: 0,
      parentLibrary: null,
    },
  };
}

function renderSelect(props: Partial<Parameters<typeof LibraryLinkSelect>[0]> = {}) {
  render(
    <MantineProvider>
      <LibraryLinkSelect
        projectId="p1"
        current={null}
        canWrite
        onChanged={vi.fn()}
        {...props}
      />
    </MantineProvider>,
  );
}

describe('LibraryLinkSelect', () => {
  it('links the selected organization library', async () => {
    const onChanged = vi.fn();
    renderSelect({ onChanged });

    fireEvent.click(screen.getByRole('combobox', { name: 'Brand library' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Acme (v2)' }));

    await waitFor(() => expect(H.link).toHaveBeenCalledWith('p1', 'lib-1'));
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
  });

  it('clears the link with Unlink', async () => {
    const onChanged = vi.fn();
    renderSelect({ current: { id: 'lib-1', name: 'Acme' }, onChanged });

    fireEvent.click(screen.getByRole('button', { name: 'Unlink' }));

    await waitFor(() => expect(H.link).toHaveBeenCalledWith('p1', null));
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
  });

  it('shows the current link read-only for a Member', () => {
    renderSelect({ current: { id: 'lib-1', name: 'Acme' }, canWrite: false });

    expect(screen.queryByRole('combobox')).toBeNull();
    expect(screen.getByText('Brand library: Acme')).toBeTruthy();
  });

  // WEB-3: until the summary refetch returns (or if it does not carry parentLibrary), the parent
  // keeps passing the OLD `current`. The control must keep showing what the user just did.
  describe('keeps the control in sync with the user\'s action', () => {
    it('shows the linked library and an Unlink button after a link even while `current` is still null', async () => {
      renderSelect({ current: null });

      fireEvent.click(screen.getByRole('combobox', { name: 'Brand library' }));
      fireEvent.click(await screen.findByRole('option', { name: 'Acme (v2)' }));
      await waitFor(() => expect(H.link).toHaveBeenCalledWith('p1', 'lib-1'));

      await waitFor(() =>
        expect((screen.getByRole('combobox', { name: 'Brand library' }) as HTMLInputElement).value).toBe('Acme (v2)'),
      );
      expect(screen.getByRole('button', { name: 'Unlink' })).toBeTruthy();
    });

    it('shows the current library and Unlink when parentLibrary is populated', () => {
      renderSelect({ current: { id: 'lib-1', name: 'Acme' } });

      expect((screen.getByRole('combobox', { name: 'Brand library' }) as HTMLInputElement).value).toBe('Acme (v2)');
      expect(screen.getByRole('button', { name: 'Unlink' })).toBeTruthy();
    });

    it('goes back to Not linked after Unlink even while `current` is still the old link', async () => {
      renderSelect({ current: { id: 'lib-1', name: 'Acme' } });

      fireEvent.click(screen.getByRole('button', { name: 'Unlink' }));
      await waitFor(() => expect(H.link).toHaveBeenCalledWith('p1', null));

      await waitFor(() => expect(screen.queryByRole('button', { name: 'Unlink' })).toBeNull());
      expect((screen.getByRole('combobox', { name: 'Brand library' }) as HTMLInputElement).value).toBe('');
    });

    it('leaves the control on the previous value when the link call fails', async () => {
      H.link.mockRejectedValue(new Error('nope'));
      renderSelect({ current: null });

      fireEvent.click(screen.getByRole('combobox', { name: 'Brand library' }));
      fireEvent.click(await screen.findByRole('option', { name: 'Acme (v2)' }));
      await waitFor(() => expect(H.link).toHaveBeenCalled());

      await waitFor(() =>
        expect((screen.getByRole('combobox', { name: 'Brand library' }) as HTMLInputElement).value).toBe(''),
      );
      expect(screen.queryByRole('button', { name: 'Unlink' })).toBeNull();
    });
  });
});
