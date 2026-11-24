// @vitest-environment jsdom
/**
 * The media pool's "Avatars" tab: the panel itself, the tab that appears only while the project's avatars answer,
 * and the same panel on the phone's media sheet.
 */
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { BINS, POOL } from '@/lib/editing/media/fixtures';
import type { SubjectClipStatus } from '@/lib/editing/subjects/subject-clip-api';
import { SubjectClipFailedError } from '@/lib/editing/subjects/subject-clip-api';
import type { ProjectAvatar } from '@/lib/types/avatars';
import { testAvatar, testObject } from '@/test/avatars';
import { renderWithI18n } from '@/test/i18n';
import { resetSubjectClipStore, startSubjectClipJob, type SubjectClipApi } from '../../subjects/subject-clip-store';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();
Object.defineProperty(document, 'fonts', { configurable: true, value: { addEventListener: vi.fn(), removeEventListener: vi.fn() } });

const h = vi.hoisted(() => ({
  requirements: undefined as unknown,
  requirementsError: undefined as unknown,
  avatars: undefined as unknown,
  avatarsError: undefined as unknown,
  avatarsLoading: false,
  avatarsAsked: [] as boolean[],
  pool: { data: [] as unknown[], error: undefined as unknown, isLoading: false, mutate: undefined as unknown },
  bins: { data: [] as unknown[], error: undefined as unknown, isLoading: false, mutate: undefined as unknown },
}));
vi.mock('@/lib/hooks/use-avatars', () => ({
  useAvatarRequirements: () => ({ data: h.requirements, error: h.requirementsError }),
  useProjectAvatars: (_p: string, enabled = true) => {
    h.avatarsAsked.push(enabled);
    return enabled ? { data: h.avatars, error: h.avatarsError, isLoading: h.avatarsLoading } : { data: undefined, error: undefined, isLoading: false };
  },
}));
vi.mock('@/lib/hooks/use-inference-providers', () => ({ useSelectableProviders: () => ({ data: [] }) }));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: [] }) }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { orgRole: 'Member' }, activeOrgId: 'org-1' }) }));
vi.mock('@/lib/hooks/use-media', () => {
  const annotations = { data: [] as unknown[], mutate: async () => undefined };
  return { useMediaPool: () => h.pool, useMediaBins: () => h.bins, useMediaAnnotations: () => annotations };
});
vi.mock('@/lib/hooks/use-upload-queue', () => ({ useUploadQueue: () => ({ entries: [], enqueue: vi.fn(), retry: vi.fn(), clearFinished: vi.fn() }) }));
vi.mock('@/lib/hooks/use-media-derivatives', async (orig) => ({ ...(await orig<typeof import('@/lib/hooks/use-media-derivatives')>()), useMediaDerivativeProgress: vi.fn() }));
vi.mock('@/lib/editing/api/media', async (orig) => ({ ...(await orig<typeof import('@/lib/editing/api/media')>()), fetchMediaDerivatives: vi.fn().mockResolvedValue([]), fetchMediaFilmstrip: vi.fn().mockResolvedValue(null) }));
vi.mock('@/lib/hooks/use-sfx-library', () => ({ useSfxLibrary: () => ({ data: { items: [], total: 0, page: 1, pageSize: 50, categories: [] }, error: undefined, isLoading: false }) }));

import { MediaPool } from '../MediaPool';
import { SubjectsPanel } from '../SubjectsPanel';
import { AddMediaSheet } from '../../phone/AddMediaSheet';

const PROJECT = '00000000-0000-4000-8000-000000000009';
const anna = testAvatar();
const bottle = testObject();
const revoked = testAvatar({ id: 'old', name: 'Old', selectable: false, unselectableReason: 'consent_revoked' });
const AVATARS: ProjectAvatar[] = [bottle, revoked, anna];
const REQUEST = { subjects: [{ avatarId: 'anna' }], prompt: 'x', durationSeconds: 6, ratio: '16:9', resolution: '768P', takes: 1 };

function fakeApi(executionId = 'e1') {
  let settle: { resolve: (s: SubjectClipStatus) => void; reject: (e: unknown) => void } | null = null;
  const api = {
    estimate: vi.fn().mockResolvedValue({ billingEnabled: false, affordable: true }),
    start: vi.fn().mockResolvedValue({ executionId, state: 'running' }),
    wait: vi.fn(() => new Promise<SubjectClipStatus>((resolve, reject) => { settle = { resolve, reject }; })),
    cancel: vi.fn().mockResolvedValue({}),
    grantConsent: vi.fn(),
    refreshPool: vi.fn(),
  } satisfies SubjectClipApi;
  return { api, finish: (s: SubjectClipStatus) => settle?.resolve(s), fail: (e: unknown) => settle?.reject(e) };
}

const mountPanel = (props: Partial<React.ComponentProps<typeof SubjectsPanel>> = {}) =>
  render(<MantineProvider><SubjectsPanel projectId={PROJECT} {...props} /></MantineProvider>);
const tabs = () => within(screen.getByTestId('pool-tabs')).getAllByRole('radio').map((r) => (r as HTMLInputElement).value);
const featureOff = () => {
  h.requirements = undefined;
  h.requirementsError = new ApiError('Not Found', 404, [], {});
};

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, onchange: null, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  h.requirements = { statementVersion: 'avatar-consent-2026-12', kinds: { person: { enabled: true, entitled: true }, object: { enabled: true, entitled: true } } };
  h.requirementsError = undefined;
  h.avatars = AVATARS;
  h.avatarsError = undefined;
  h.avatarsLoading = false;
  h.avatarsAsked = [];
  h.pool = { data: POOL, error: undefined, isLoading: false, mutate: vi.fn(async () => POOL) };
  h.bins = { data: BINS, error: undefined, isLoading: false, mutate: vi.fn() };
});
afterEach(() => {
  cleanup();
  resetSubjectClipStore();
});

describe('SubjectsPanel', () => {
  it('lists people and objects with their kind and status, and why one cannot be used', () => {
    mountPanel();
    const people = within(screen.getByTestId('subjects-group-people'));
    const objects = within(screen.getByTestId('subjects-group-objects'));
    expect(people.getByText('People (2)')).toBeTruthy();
    expect(objects.getByText('Objects (1)')).toBeTruthy();
    const rows = screen.getAllByTestId('subjects-row');
    expect(rows.map((r) => r.textContent)).toEqual([
      'AnnaPersonReady',
      'OldPersonConsent withdrawnConsent was withdrawn. The avatar is deleted automatically after the waiting period.',
      'BottleObjectReady',
    ]);
    expect(rows.map((r) => r.getAttribute('data-usable'))).toEqual(['true', 'false', 'true']);
    expect(screen.getByTestId('early-preview-label')).toBeTruthy();
  });

  it('links to the project\'s Avatars tab, where they are added and managed', () => {
    mountPanel();
    expect(screen.getByTestId('subjects-manage').getAttribute('href')).toBe(`/projects/${PROJECT}?tab=avatars`);
    expect(screen.getByTestId('subjects-manage').textContent).toBe('Add or manage them in the Avatars tab');
  });

  it('"Generate clip" opens the dialog with the project\'s avatars', async () => {
    mountPanel();
    expect(screen.queryByTestId('subject-clip-dialog')).toBeNull();
    fireEvent.click(screen.getByTestId('subjects-generate'));
    const dialog = within(await screen.findByRole('dialog', { name: /Generate a clip/ }));
    expect(dialog.getByTestId('subject-option-anna')).toBeTruthy();
    expect(dialog.getByTestId('subject-option-bottle')).toBeTruthy();
  });

  it('a view-only editor gets a plain line instead of the button: nothing disabled is shown', () => {
    mountPanel({ readOnly: true });
    expect(screen.queryByTestId('subjects-generate')).toBeNull();
    expect(screen.getByTestId('subjects-read-only').textContent).toBe('You can view this project but not make clips in it.');
    expect(screen.getByTestId('subjects-panel').querySelectorAll('button:disabled')).toHaveLength(0);
    // the list is still there to look at
    expect(screen.getAllByTestId('subjects-row')).toHaveLength(3);
  });

  it('says so when the project has none, and when none of them can be used', () => {
    h.avatars = [];
    mountPanel();
    expect(screen.getByTestId('subjects-empty').textContent).toContain('No avatars or objects yet');
    expect(screen.queryByTestId('subjects-generate')).toBeNull();
    expect(screen.getByTestId('subjects-manage')).toBeTruthy();
    cleanup();
    h.avatars = [revoked];
    mountPanel();
    expect(screen.queryByTestId('subjects-generate')).toBeNull();
    expect(screen.getByTestId('subjects-none-usable').textContent).toBe('None of these can be used right now. Open the Avatars tab to fix them.');
  });

  it('says so when the list cannot be loaded', () => {
    h.avatars = undefined;
    h.avatarsError = new Error('offline');
    mountPanel();
    expect(screen.getByTestId('subjects-load-error').textContent).toBe('The avatars could not be loaded. Try again.');
    expect(screen.queryByTestId('subjects-empty')).toBeNull();
  });

  it('shows a running clip with a way to stop it, and where finished clips go', async () => {
    const { api } = fakeApi();
    await startSubjectClipJob(PROJECT, REQUEST, { names: ['Anna', 'Bottle'], api });
    mountPanel({ api });
    const job = within(screen.getByTestId('subjects-job'));
    expect(screen.getByTestId('subjects-job').getAttribute('data-state')).toBe('running');
    expect(job.getByText('Anna and Bottle')).toBeTruthy();
    expect(job.getByTestId('subjects-job-state').textContent).toBe('Making the clip');
    expect(screen.getByTestId('subjects-jobs-note').textContent).toBe('Finished clips appear in the Media tab.');
    fireEvent.click(job.getByRole('button', { name: 'Stop making the clip with Anna and Bottle' }));
    await waitFor(() => expect(api.cancel).toHaveBeenCalledWith(PROJECT, 'e1'));
    expect(screen.getByTestId('subjects-job').getAttribute('data-state')).toBe('cancelled');
    expect(screen.getByTestId('subjects-job-state').textContent).toBe('Stopped');
    expect(screen.queryByTestId('subjects-job-cancel')).toBeNull();
  });

  it('shows a finished clip as done and a failed one with the server\'s sentence; either can be cleared', async () => {
    const ok = fakeApi('e1');
    await startSubjectClipJob(PROJECT, REQUEST, { names: ['Anna'], api: ok.api });
    const bad = fakeApi('e2');
    await startSubjectClipJob(PROJECT, REQUEST, { names: ['Bottle'], api: bad.api });
    mountPanel();
    await act(async () => ok.finish({ executionId: 'e1', state: 'ready', outputFileIds: ['f1'] }));
    await act(async () => bad.fail(new SubjectClipFailedError({ executionId: 'e2', state: 'failed', outputFileIds: [], message: 'The model refused this description.' })));
    const states = () => screen.getAllByTestId('subjects-job').map((j) => `${j.getAttribute('data-state')}:${within(j).getByTestId('subjects-job-state').textContent}`);
    expect(states().sort()).toEqual(['failed:Did not work', 'ready:Done']);
    expect(screen.getByTestId('subjects-job-message').textContent).toBe('The model refused this description.');
    expect(ok.api.refreshPool).toHaveBeenCalledWith(PROJECT);
    fireEvent.click(screen.getByRole('button', { name: 'Clear the clip with Bottle from this list' }));
    expect(states()).toEqual(['ready:Done']);
  });

  it('a view-only editor sees running clips but cannot stop them', async () => {
    const { api } = fakeApi();
    await startSubjectClipJob(PROJECT, REQUEST, { names: ['Anna'], api });
    mountPanel({ readOnly: true });
    expect(screen.getByTestId('subjects-job-state').textContent).toBe('Making the clip');
    expect(screen.queryByTestId('subjects-job-cancel')).toBeNull();
  });

  it('gives touch controls a 44 px height', async () => {
    const { api } = fakeApi();
    await startSubjectClipJob(PROJECT, REQUEST, { names: ['Anna'], api });
    mountPanel({ touch: true });
    expect((screen.getByTestId('subjects-generate') as HTMLElement).style.height).toBe('calc(2.75rem * var(--mantine-scale))'); // 44 px
    expect((screen.getByTestId('subjects-job-cancel') as HTMLElement).style.height).toBe('calc(2.75rem * var(--mantine-scale))'); // 44 px
    expect((screen.getByTestId('subjects-manage') as HTMLElement).style.minHeight).toBe('44px');
  });

  it('reads in Italian', () => {
    renderWithI18n(<SubjectsPanel projectId={PROJECT} />, { locale: 'it' });
    expect(screen.getByText('Avatar e oggetti')).toBeTruthy();
    expect(screen.getByTestId('subjects-generate').textContent).toBe('Genera clip');
    expect(screen.getByText('Persone (2)')).toBeTruthy();
    expect(screen.getByText('Oggetti (1)')).toBeTruthy();
    expect(screen.getByTestId('subjects-manage').textContent).toBe('Aggiungili o gestiscili nella scheda Avatar');
  });
});

describe('the media pool\'s Avatars tab', () => {
  const mountPool = (props: Partial<React.ComponentProps<typeof MediaPool>> = {}) =>
    render(<MantineProvider><MediaPool projectId={PROJECT} {...props} /></MantineProvider>);

  it('is not there while avatars are switched off: the pool has its two tabs exactly as before', () => {
    featureOff();
    mountPool();
    expect(tabs()).toEqual(['media', 'sfx']);
    expect(within(screen.getByTestId('pool-tabs')).queryByText('Avatars')).toBeNull();
    expect(screen.getByTestId('media-pool-dropzone')).toBeTruthy();
    // the project's avatars are never asked for
    expect(h.avatarsAsked).toEqual([]);
  });

  it('is not there while the answer is still loading', () => {
    h.requirements = undefined;
    mountPool();
    expect(tabs()).toEqual(['media', 'sfx']);
  });

  it('appears once the project\'s avatars answer, and shows the panel when picked', () => {
    mountPool();
    expect(tabs()).toEqual(['media', 'sfx', 'avatars']);
    expect(screen.queryByTestId('subjects-panel')).toBeNull();
    fireEvent.click(within(screen.getByTestId('pool-tabs')).getByText('Avatars'));
    expect(within(screen.getByTestId('media-pool-avatars')).getByTestId('subjects-panel')).toBeTruthy();
    expect(screen.queryByTestId('media-pool-dropzone')).toBeNull();
    expect(screen.getByTestId('subjects-generate')).toBeTruthy();
    // and back
    fireEvent.click(within(screen.getByTestId('pool-tabs')).getByText('Media'));
    expect(screen.getByTestId('media-pool-dropzone')).toBeTruthy();
  });

  it('passes a view-only editor on to the panel', () => {
    mountPool({ readOnly: true });
    fireEvent.click(within(screen.getByTestId('pool-tabs')).getByText('Avatars'));
    expect(screen.getByTestId('subjects-read-only')).toBeTruthy();
    expect(screen.queryByTestId('subjects-generate')).toBeNull();
  });

  it('falls back to Media if the feature goes away while its tab is open', () => {
    const view = mountPool();
    fireEvent.click(within(screen.getByTestId('pool-tabs')).getByText('Avatars'));
    expect(screen.getByTestId('media-pool-avatars')).toBeTruthy();
    featureOff();
    view.rerender(<MantineProvider><MediaPool projectId={PROJECT} /></MantineProvider>);
    expect(screen.queryByTestId('media-pool-avatars')).toBeNull();
    expect(tabs()).toEqual(['media', 'sfx']);
    expect(screen.getByTestId('media-pool-dropzone')).toBeTruthy();
  });
});

describe('the phone\'s media sheet', () => {
  const mountSheet = (readOnly = false) =>
    render(<MantineProvider><AddMediaSheet projectId={PROJECT} readOnly={readOnly} onPlace={() => null} onPlaced={() => undefined} /></MantineProvider>);

  it('offers Avatars next to Sound effects, opens the same panel with touch-size controls, and goes back', () => {
    mountSheet();
    const open = screen.getByTestId('add-media-avatars-open') as HTMLButtonElement;
    expect(open.textContent).toBe('Avatars');
    expect(open.disabled).toBe(false);
    fireEvent.click(open);
    const sheet = within(screen.getByTestId('add-media-avatars'));
    expect(sheet.getByTestId('subjects-panel')).toBeTruthy();
    expect((sheet.getByTestId('subjects-generate') as HTMLElement).style.height).toBe('calc(2.75rem * var(--mantine-scale))'); // 44 px
    fireEvent.click(sheet.getByTestId('add-media-avatars-back'));
    expect(screen.queryByTestId('add-media-avatars')).toBeNull();
    expect(screen.getByTestId('add-media-sheet')).toBeTruthy();
  });

  it('shows no Avatars entry while the feature is off', () => {
    featureOff();
    mountSheet();
    expect(screen.queryByTestId('add-media-avatars-open')).toBeNull();
    expect(screen.getByTestId('add-media-sfx-open')).toBeTruthy();
  });

  it('a view-only phone editor can open it and is told why it cannot make clips: no disabled control', () => {
    mountSheet(true);
    fireEvent.click(screen.getByTestId('add-media-avatars-open'));
    expect(screen.getByTestId('subjects-read-only')).toBeTruthy();
    expect(screen.getByTestId('subjects-panel').querySelectorAll('button:disabled')).toHaveLength(0);
  });
});
