// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';

const H = vi.hoisted(() => ({
  list: { data: undefined, error: undefined, isLoading: false, mutate: vi.fn() } as {
    data: unknown; error: unknown; isLoading: boolean; mutate: ReturnType<typeof vi.fn>;
  },
  create: vi.fn(),
  update: vi.fn(),
  del: vi.fn(),
  notify: vi.fn(),
}));

vi.mock('@/lib/hooks/use-pronunciation', () => ({ useProjectPronunciation: () => H.list }));
vi.mock('@/lib/api/pronunciation', () => ({
  createPronunciation: (...a: unknown[]) => H.create(...a),
  updatePronunciation: (...a: unknown[]) => H.update(...a),
  deletePronunciation: (...a: unknown[]) => H.del(...a),
}));
vi.mock('@mantine/notifications', () => ({ notifications: { show: (...a: unknown[]) => H.notify(...a) } }));

import { ProjectPronunciationTab } from './ProjectPronunciationTab';

const ENTRY = { id: 'e1', term: 'Novara', spelling: 'Novàra', caseSensitive: false, updatedAt: '2026-01-01T00:00:00Z' };

function setList(entries: unknown[]) {
  H.list.data = { entries, max: 200 };
}
function renderTab() {
  return render(<MantineProvider><ProjectPronunciationTab projectId="p1" /></MantineProvider>);
}

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
  H.list.data = undefined;
  H.list.error = undefined;
  H.list.mutate = vi.fn().mockResolvedValue(undefined);
  H.create.mockReset().mockResolvedValue(ENTRY);
  H.update.mockReset().mockResolvedValue(ENTRY);
  H.del.mockReset().mockResolvedValue(undefined);
  H.notify.mockReset();
});
afterEach(cleanup);

describe('ProjectPronunciationTab', () => {
  it('shows the explanation, the empty state and the count', () => {
    setList([]);
    renderTab();
    expect(screen.getByText(/Tell the narrator how to say a word/)).toBeTruthy();
    expect(screen.getByText('No words yet')).toBeTruthy();
    expect(screen.getByText('Your words (0 of 200)')).toBeTruthy();
  });

  it('renders the list with 44px action buttons', () => {
    setList([ENTRY]);
    renderTab();
    expect(screen.getByText('Novara -> Novàra')).toBeTruthy();
    expect(screen.getByText('Your words (1 of 200)')).toBeTruthy();
    const edit = screen.getByRole('button', { name: 'Edit Novara' });
    expect(edit.style.getPropertyValue('--ai-size')).toContain('2.75rem');
  });

  it('adds a word', async () => {
    setList([]);
    renderTab();
    fireEvent.change(screen.getByLabelText('Word'), { target: { value: ' Novara ' } });
    fireEvent.change(screen.getByLabelText(/How to say it/), { target: { value: 'Novàra' } });
    fireEvent.click(screen.getByLabelText('Match capital letters exactly'));
    fireEvent.click(screen.getByRole('button', { name: 'Add word' }));
    await waitFor(() => expect(H.create).toHaveBeenCalledWith('p1', { term: 'Novara', spelling: 'Novàra', caseSensitive: true }));
    await waitFor(() => expect(H.list.mutate).toHaveBeenCalled());
  });

  it('shows the API message when the word already exists (409)', async () => {
    setList([]);
    H.create.mockRejectedValue(new ApiError('That word is already in the list.', 409));
    renderTab();
    fireEvent.change(screen.getByLabelText('Word'), { target: { value: 'Novara' } });
    fireEvent.change(screen.getByLabelText(/How to say it/), { target: { value: 'Novàra' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add word' }));
    expect((await screen.findByRole('alert')).textContent).toContain('already in the list');
  });

  it('lists validation errors from a 400', async () => {
    setList([]);
    H.create.mockRejectedValue(new ApiError('Invalid', 400, ['Word is too short.']));
    renderTab();
    fireEvent.change(screen.getByLabelText('Word'), { target: { value: 'No' } });
    fireEvent.change(screen.getByLabelText(/How to say it/), { target: { value: 'x' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add word' }));
    expect((await screen.findByRole('alert')).textContent).toContain('Word is too short.');
  });

  it('says so when a viewer cannot change the list (403)', async () => {
    setList([]);
    H.create.mockRejectedValue(new ApiError('Forbidden', 403));
    renderTab();
    fireEvent.change(screen.getByLabelText('Word'), { target: { value: 'Novara' } });
    fireEvent.change(screen.getByLabelText(/How to say it/), { target: { value: 'Novàra' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add word' }));
    expect((await screen.findByRole('alert')).textContent).toContain('do not have permission');
  });

  it('edits an entry inline', async () => {
    setList([ENTRY]);
    renderTab();
    fireEvent.click(screen.getByRole('button', { name: 'Edit Novara' }));
    const row = screen.getByTestId('pronunciation-edit-row');
    const inputs = row.querySelectorAll('input:not([type="checkbox"])');
    fireEvent.change(inputs[1], { target: { value: 'Nòvara' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(H.update).toHaveBeenCalledWith('p1', 'e1', { term: 'Novara', spelling: 'Nòvara', caseSensitive: false }));
  });

  it('deletes only after confirmation', async () => {
    setList([ENTRY]);
    renderTab();
    fireEvent.click(screen.getByRole('button', { name: 'Delete Novara' }));
    expect(H.del).not.toHaveBeenCalled();
    fireEvent.click(await screen.findByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(H.del).toHaveBeenCalledWith('p1', 'e1'));
  });

  it('shows a load error', () => {
    H.list.error = new ApiError('Boom', 500);
    renderTab();
    expect(screen.getByRole('alert').textContent).toContain('Boom');
  });
});
