// @vitest-environment jsdom
/**
 * Token-tree locks (P3-04, SPEC §9): a Draft version carries a lock toggle per token leaf, toggling
 * calls `lockVersion`, and any other state shows a read-only "Locked" marker and no control.
 */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('@/lib/api/design-system', () => ({ lockVersion: vi.fn() }));

import { lockVersion } from '@/lib/api/design-system';
import type { DesignDocument } from '@/lib/types/design-system';
import { TokenTree } from './TokenTree';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

const doc: DesignDocument = {
  color: { primary: { $type: 'color', $value: { hex: '#6d28d9' } } },
};

const lockedDoc: DesignDocument = {
  color: {
    primary: {
      $type: 'color',
      $value: { hex: '#6d28d9' },
      $extensions: { 'com.reelbolt': { locked: true } },
    },
  },
};

function renderTree(version: { id: string; state: 'Draft' | 'Approved' }, document = doc, onLocked = vi.fn()) {
  render(
    <MantineProvider>
      <TokenTree projectId="p1" doc={document} version={version} canEdit onLocked={onLocked} />
    </MantineProvider>,
  );
  return onLocked;
}

describe('TokenTree locks', () => {
  it('toggles a lock on a Draft version', async () => {
    vi.mocked(lockVersion).mockResolvedValue({} as never);
    const onLocked = renderTree({ id: 'v-draft', state: 'Draft' });

    fireEvent.click(screen.getByLabelText('Lock color.primary'));

    await waitFor(() => expect(lockVersion).toHaveBeenCalledWith('p1', 'v-draft', ['color.primary'], []));
    expect(onLocked).toHaveBeenCalled();
  });

  it('shows no toggle on an Approved version', () => {
    renderTree({ id: 'v-1', state: 'Approved' });

    expect(screen.queryByLabelText('Lock color.primary')).toBeNull();
    expect(screen.queryByLabelText('Unlock color.primary')).toBeNull();
    expect(screen.queryByTestId('token-locked-color.primary')).toBeNull();
  });

  it('shows a read-only Locked marker on an Approved locked token', () => {
    renderTree({ id: 'v-1', state: 'Approved' }, lockedDoc);

    expect(screen.getByTestId('token-locked-color.primary')).toBeTruthy();
    expect(screen.queryByLabelText('Lock color.primary')).toBeNull();
    expect(screen.queryByLabelText('Unlock color.primary')).toBeNull();
  });
});

describe('TokenTree disabled lock reason', () => {
  it('points a disabled draft switch at a visible sentence that says why', () => {
    render(
      <MantineProvider>
        <TokenTree projectId="p1" doc={doc} version={{ id: 'v-draft', state: 'Draft' }} canEdit={false} />
      </MantineProvider>,
    );

    const toggle = screen.getByLabelText('Lock color.primary') as HTMLInputElement;
    expect(toggle.disabled).toBe(true);
    const id = toggle.getAttribute('aria-describedby');
    expect(id).toBeTruthy();
    expect(document.getElementById(id as string)?.textContent).toContain('edit access');
  });
});

describe('TokenTree font samples', () => {
  const fontDoc: DesignDocument = {
    typography: {
      family: {
        heading: { $type: 'fontFamily', $value: ['Inter', 'sans-serif'] },
        accent: { $type: 'fontFamily', $value: ['Comic Neue'] },
      },
    },
  };

  it('shows a compact sample for a fontFamily token, in the first bundled family or flagged unavailable', () => {
    renderTree({ id: 'v-1', state: 'Approved' }, fontDoc);

    const statuses = screen.getAllByTestId('type-specimen-sample').map((node) => node.getAttribute('data-font-status'));
    expect(statuses.sort()).toEqual(['available', 'unavailable']);
    expect(screen.getByTestId('type-specimen-note').textContent).toContain('Preview unavailable');
  });

  it('shows no sample for a non-font token', () => {
    renderTree({ id: 'v-1', state: 'Approved' });

    expect(screen.queryByTestId('type-specimen-sample')).toBeNull();
  });
});
