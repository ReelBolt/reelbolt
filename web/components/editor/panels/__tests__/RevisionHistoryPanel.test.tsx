// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const api = vi.hoisted(() => ({ list: vi.fn(), create: vi.fn(), name: vi.fn(), unname: vi.fn(), revision: vi.fn() }));
vi.mock('@/lib/editing/sync', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/sync')>()),
  listEditTimelineVersions: (...a: unknown[]) => api.list(...a),
  createEditTimelineVersion: (...a: unknown[]) => api.create(...a),
  nameEditTimelineVersion: (...a: unknown[]) => api.name(...a),
  removeEditTimelineVersionName: (...a: unknown[]) => api.unname(...a),
  getEditTimelineRevision: (...a: unknown[]) => api.revision(...a),
}));

import { RevisionHistoryPanel } from '../RevisionHistoryPanel';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };

const soon = (days: number) => new Date(Date.now() + days * 86_400_000 - 60_000).toISOString();
const now = new Date().toISOString();
const VERSIONS = [
  { revision: 5, cause: 'beforeRestore', createdAt: now, expiresAt: soon(30), createdByName: 'Ada' },
  { revision: 3, cause: 'named', name: 'Client cut', createdAt: now },
  { revision: 4, cause: 'idle', createdAt: now, expiresAt: soon(12) },
  { revision: 1, cause: 'milestone', createdAt: now },
];

beforeEach(() => {
  api.list.mockReset().mockResolvedValue(VERSIONS);
  api.create.mockReset().mockResolvedValue({});
  api.name.mockReset().mockResolvedValue({});
  api.unname.mockReset().mockResolvedValue({});
  api.revision.mockReset().mockResolvedValue({ document: { id: 'd' } });
});
afterEach(cleanup);

const mount = (onRestore = vi.fn()) => {
  render(
    <MantineProvider>
      <RevisionHistoryPanel projectId="p" timelineId="t" currentRevision={5} readOnly={false} onRestore={onRestore} />
    </MantineProvider>,
  );
  return onRestore;
};

describe('R01.2 Versions panel', () => {
  it('states the rule and lists each version in plain words with its retention', async () => {
    mount();
    await screen.findByTestId('revision-row-5');
    expect(screen.getByTestId('versions-rule').textContent).toBe('Auto-saved checkpoints are kept for 30 days. Name a version to keep it forever.');
    expect(screen.getByTestId('version-title-5').textContent).toBe('Before you restored');
    expect(screen.getByTestId('version-meta-5').textContent).toContain('Ada');
    expect(screen.getByTestId('version-retention-5').textContent).toBe('deleted in 30 days');
    expect(screen.getByTestId('version-title-3').textContent).toBe('Client cut');
    expect(screen.getByTestId('version-retention-3').textContent).toBe('kept forever');
    expect(screen.getByTestId('version-title-1').textContent).toBe('Original version');
    expect(screen.getByTestId('version-retention-4').textContent).toBe('deleted in 12 days');
  });

  it('saves a named version and names or unnames a row', async () => {
    mount();
    await screen.findByTestId('revision-row-5');
    fireEvent.click(screen.getByTestId('version-save-named'));
    fireEvent.change(screen.getByTestId('version-name-input'), { target: { value: 'Rough cut' } });
    fireEvent.click(screen.getByTestId('version-name-save'));
    await waitFor(() => expect(api.create).toHaveBeenCalledWith('p', 't', { name: 'Rough cut', cause: 'named' }));
    fireEvent.click(await screen.findByTestId('version-name-1'));
    fireEvent.change(screen.getByTestId('version-name-input'), { target: { value: 'Start' } });
    fireEvent.click(screen.getByTestId('version-name-save'));
    await waitFor(() => expect(api.name).toHaveBeenCalledWith('p', 't', 1, 'Start'));
    fireEvent.click(await screen.findByTestId('version-unname-3'));
    await waitFor(() => expect(api.unname).toHaveBeenCalledWith('p', 't', 3));
  });

  it('asks before restoring any row and hands the stored document to the parent', async () => {
    const onRestore = mount();
    fireEvent.click(await screen.findByTestId('revision-restore-1'));
    expect((await screen.findByTestId('version-restore-text')).textContent).toContain('Before you restored');
    expect(api.revision).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTestId('revision-restore-confirm-1'));
    await waitFor(() => expect(onRestore).toHaveBeenCalledWith({ id: 'd' }, 1));
  });
});

describe('actorKind marker', () => {
  const row = (over: Record<string, unknown> = {}) => ({ revision: 7, cause: 'idle', createdAt: now, createdByName: 'Nightly bot', ...over });

  it('marks a version written by an API key with the key name', async () => {
    api.list.mockResolvedValue([row({ actorKind: 'apiKey' })]);
    mount();
    expect((await screen.findByTestId('version-meta-7')).textContent).toContain('API key: Nightly bot');
  });

  it('leaves a person and a missing/unknown actorKind unmarked', async () => {
    api.list.mockResolvedValue([row({ revision: 8, actorKind: 'person' }), row({ revision: 9, actorKind: undefined }), row({ revision: 10, actorKind: 'serviceAccount' })]);
    mount();
    const person = await screen.findByTestId('version-meta-8');
    const missing = screen.getByTestId('version-meta-9');
    const unknown = screen.getByTestId('version-meta-10');
    expect(person.textContent).toContain('Nightly bot');
    expect(person.textContent).not.toContain('API key:');
    expect(missing.textContent).toContain('Nightly bot');
    expect(missing.textContent).not.toContain('API key:');
    expect(unknown.textContent).toContain('Nightly bot');
    expect(unknown.textContent).not.toContain('API key:');
  });
});
