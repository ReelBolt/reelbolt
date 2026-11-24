// @vitest-environment jsdom
/**
 * Settings: the workspace avatar library page (docs/design/avatar-objects.md "The workspace avatar library").
 * The hooks are mocked and the mutating API functions are mocks, so the tests pin the page's own behaviour:
 * what a member and a manager see, each manager action with the exact body it sends, the delete report, the
 * empty state, and a server whose library is off.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import type { AvatarLibraryRequirements } from '@/lib/types/avatars';
import type { AvatarLibraryItem } from '@/lib/types/avatar-library';

const H = vi.hoisted(() => ({
  role: 'Owner' as string,
  items: [] as unknown[] | undefined,
  envelope: null as unknown,
  error: undefined as unknown,
  off: false,
  listArgs: [] as unknown[][],
  fresh: undefined as unknown,
  itemArgs: [] as unknown[],
  projects: [{ id: 'p1', name: 'Launch film' }, { id: 'p2', name: 'Other' }] as unknown[] | undefined,
  projectsArgs: [] as unknown[],
  requirements: undefined as unknown,
  requirementsArgs: [] as unknown[],
  mutate: vi.fn(),
  rename: vi.fn(),
  reconfirm: vi.fn(),
  revoke: vi.fn(),
  remove: vi.fn(),
  notify: vi.fn(),
}));

vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({ user: { orgRole: H.role }, isOrgAdmin: H.role === 'Owner' || H.role === 'Admin', isPlatformAdmin: false }),
}));
vi.mock('@/lib/hooks/use-avatar-library', () => ({
  useAvatarLibrary: (...args: unknown[]) => {
    H.listArgs.push(args);
    return { items: H.items, library: H.envelope, error: H.error, isLoading: false, mutate: H.mutate, off: H.off };
  },
  useAvatarLibraryItem: (id: unknown) => {
    H.itemArgs.push(id);
    return { data: id ? H.fresh : undefined };
  },
}));
vi.mock('@/lib/hooks/use-projects', () => ({
  useProjects: (enabled: unknown = true) => {
    H.projectsArgs.push(enabled);
    return { data: enabled ? H.projects : undefined };
  },
}));
vi.mock('@/lib/hooks/use-avatars', () => ({
  useAvatarRequirements: (projectId: unknown) => {
    H.requirementsArgs.push(projectId);
    return { data: projectId ? H.requirements : undefined, error: undefined };
  },
}));
vi.mock('@/lib/api/avatar-library', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/api/avatar-library')>()),
  updateAvatarLibraryItem: (...a: unknown[]) => H.rename(...a),
  reconfirmAvatarLibraryItem: (...a: unknown[]) => H.reconfirm(...a),
  revokeAvatarLibraryConsent: (...a: unknown[]) => H.revoke(...a),
  deleteAvatarLibraryItem: (...a: unknown[]) => H.remove(...a),
}));
vi.mock('@mantine/notifications', () => ({ notifications: { show: (...a: unknown[]) => H.notify(...a) } }));

import AvatarLibraryPage from './page';

const WS_EN = 'I confirm that this person agreed to be used in any project of this workspace.';
const WS_IT = 'Confermo che questa persona ha accettato di essere usata in qualsiasi progetto di questo spazio di lavoro.';
const WS_OBJ_EN = 'I confirm that these files show an object and may be used in any project of this workspace.';

const LIBRARY: AvatarLibraryRequirements = {
  enabled: true, canManage: true,
  consentStatementVersion: 'avatar-consent-workspace-2026-11', consentStatements: { en: WS_EN, it: WS_IT },
  objectStatementVersion: 'avatar-object-workspace-2026-11', objectStatements: { en: WS_OBJ_EN, it: 'Oggetto.' },
  maxItemsPerKind: 100,
};
/** What a project's requirements route answers, as far as the page reads it. */
const REQUIREMENTS = {
  locales: ['en', 'it'],
  limits: { minRetentionDays: 1, maxRetentionDays: 730, defaultRetentionDays: 365, revokedGraceDays: 30, maxNameLength: 200, maxDescriptionLength: 2000 },
  library: LIBRARY,
};

const item = (over: Partial<AvatarLibraryItem> = {}): AvatarLibraryItem => ({
  id: 'l1', subjectKind: 'Person', name: 'Marta', description: 'Our presenter', usable: true, unusableReason: null,
  references: { images: 2, videos: 1, audio: 1 }, primaryReferenceId: 'lr1', retentionUntil: '2099-01-01T00:00:00Z',
  importCount: 3, imported: false, canManage: true, ...over,
});
const SHOE = item({ id: 'l2', subjectKind: 'Object', name: 'Blue shoe', description: null, references: { images: 3, videos: 0, audio: 0 }, primaryReferenceId: null, retentionUntil: null, importCount: 0 });
const asMember = (i: AvatarLibraryItem): AvatarLibraryItem => ({ ...i, canManage: false });

const renderPage = () => render(<MantineProvider><AvatarLibraryPage /></MantineProvider>);

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
  window.HTMLElement.prototype.scrollIntoView = () => {};
  H.role = 'Owner';
  H.items = [];
  H.envelope = null;
  H.error = undefined;
  H.off = false;
  H.listArgs = [];
  H.fresh = undefined;
  H.itemArgs = [];
  H.projects = [{ id: 'p1', name: 'Launch film' }, { id: 'p2', name: 'Other' }];
  H.projectsArgs = [];
  H.requirements = REQUIREMENTS;
  H.requirementsArgs = [];
  H.mutate = vi.fn().mockResolvedValue(undefined);
  H.rename.mockReset().mockResolvedValue(item());
  H.reconfirm.mockReset().mockResolvedValue(item());
  H.revoke.mockReset().mockResolvedValue(item({ usable: false, unusableReason: 'consent_revoked' }));
  H.remove.mockReset().mockResolvedValue({ imports: { deleted: 3, failed: 0 }, objects: { deleted: 4, failed: 0 } });
  H.notify.mockReset();
});
afterEach(cleanup);

const refusal = (code: string, status = 409, extra: Record<string, unknown> = {}) =>
  new ApiError(`server said ${code}`, status, [], { code, message: `server said ${code}`, ...extra });
async function menuOf(name: string) {
  fireEvent.click(screen.getByRole('button', { name: `Actions for ${name}` }));
  return (await screen.findAllByRole('menuitem')).map((m) => m.textContent);
}
async function openAction(name: string, label: string) {
  fireEvent.click(screen.getByRole('button', { name: `Actions for ${name}` }));
  fireEvent.click(await screen.findByRole('menuitem', { name: label }));
  return screen.findByRole('dialog');
}

describe('Avatar library page: what everyone sees', () => {
  it('asks the list without a project and shows the title under Settings', () => {
    renderPage();
    expect(screen.getByRole('heading', { level: 1, name: 'Avatar library' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Settings' }).getAttribute('href')).toContain('/settings');
    expect(H.listArgs[0]).toEqual([]);
  });

  it('lists each item with its picture, kind, status, keep-until date for a person and how many projects use it', () => {
    H.items = [item(), SHOE];
    renderPage();
    const marta = screen.getByTestId('library-item-l1');
    expect(within(marta).getByText('Marta')).toBeTruthy();
    expect(within(marta).getByText('Our presenter')).toBeTruthy();
    expect(within(marta).getByRole('img', { name: 'Picture of Marta' }).getAttribute('src')).toBe('/api/v1/avatar-library/l1/references/lr1/content');
    expect(within(marta).getByTestId('library-kind').textContent).toBe('Person');
    expect(within(marta).getByTestId('library-status').textContent).toBe('Ready');
    expect(within(marta).getByText('2 pictures, 1 video, 1 recording')).toBeTruthy();
    expect(within(marta).getByText(/^Kept until .*2099/)).toBeTruthy();
    expect(within(marta).getByTestId('library-used').textContent).toBe('Used in 3 projects');

    const shoe = screen.getByTestId('library-item-l2');
    expect(within(shoe).getByTestId('library-kind').textContent).toBe('Object');
    expect(within(shoe).getByText('3 pictures, 0 videos')).toBeTruthy();
    expect(within(shoe).queryByText(/Kept until/)).toBeNull();
    expect(within(shoe).getByTestId('library-used').textContent).toBe('Not used in any project yet');
  });

  it('says why an item cannot be used', () => {
    H.items = [
      item({ usable: false, unusableReason: 'consent_revoked' }),
      item({ id: 'l3', name: 'Luca', usable: false, unusableReason: 'retention_expired', importCount: 1 }),
    ];
    renderPage();
    const revoked = screen.getByTestId('library-item-l1');
    expect(within(revoked).getByTestId('library-status').textContent).toBe('Consent withdrawn');
    expect(within(revoked).getByText('Consent was withdrawn, so it can no longer be used in any project.')).toBeTruthy();
    expect(within(revoked).queryByText(/Kept until/)).toBeNull();
    const expired = screen.getByTestId('library-item-l3');
    expect(within(expired).getByTestId('library-status').textContent).toBe('Expired');
    expect(within(expired).getByTestId('library-used').textContent).toBe('Used in 1 project');
  });

  it('says the library is not available when the server has none', () => {
    H.items = undefined;
    H.off = true;
    H.error = new ApiError('', 404);
    renderPage();
    expect(screen.getByTestId('avatar-library-off').textContent).toContain('The avatar library is not available');
    expect(screen.queryByTestId('avatar-library')).toBeNull();
  });

  it('says so when the list cannot be loaded', () => {
    H.items = undefined;
    H.error = new ApiError('boom', 500);
    renderPage();
    expect(screen.getByText('Could not load the avatar library. Try again.')).toBeTruthy();
  });
});

describe('Avatar library page: a member', () => {
  beforeEach(() => { H.role = 'Member'; });

  it('reads the list with no action on any item, and nothing is asked for the dialogs', () => {
    H.items = [asMember(item()), asMember(SHOE)];
    renderPage();
    expect(screen.getByTestId('avatar-library-intro').textContent).toContain('An Owner or Admin manages them.');
    expect(screen.getByTestId('library-item-l1')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^Actions for/ })).toBeNull();
    expect(H.projectsArgs.every((enabled) => enabled === false)).toBe(true);
    expect(H.requirementsArgs.every((id) => id === null)).toBe(true);
  });

  it('is told who adds items when the library is empty', () => {
    renderPage();
    const empty = screen.getByTestId('avatar-library-empty');
    expect(empty.textContent).toContain('Nothing in the library yet');
    expect(empty.textContent).toContain('by an Owner or Admin of the workspace');
    expect(empty.textContent).not.toContain('choose "Save to the workspace library"');
  });
});

describe('Avatar library page: a manager', () => {
  it('is told that items are added from a project when the library is empty', () => {
    renderPage();
    const empty = screen.getByTestId('avatar-library-empty');
    expect(empty.textContent).toContain('Items are added from a project.');
    expect(empty.textContent).toContain('choose "Save to the workspace library"');
    expect(screen.getByTestId('avatar-library-intro').textContent).toContain('What you change here reaches every project that uses the item.');
  });

  it('offers a person every action, an object none about consent, and a withdrawn person only rename and delete', async () => {
    H.items = [item(), SHOE, item({ id: 'l3', name: 'Paolo', usable: false, unusableReason: 'consent_revoked' })];
    renderPage();
    expect(await menuOf('Marta')).toEqual(['Rename or describe', 'Confirm again', 'Withdraw consent', 'Delete']);
    cleanup();
    renderPage();
    expect(await menuOf('Blue shoe')).toEqual(['Rename or describe', 'Confirm again', 'Delete']);
    cleanup();
    renderPage();
    expect(await menuOf('Paolo')).toEqual(['Rename or describe', 'Delete']);
  });

  it('follows the server per item: no menu on an item it says the caller cannot manage', () => {
    H.items = [item(), asMember(SHOE)];
    renderPage();
    expect(screen.getByRole('button', { name: 'Actions for Marta' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Actions for Blue shoe' })).toBeNull();
  });

  it('reads the workspace statements from the first project when the list carries none', () => {
    H.items = [item()];
    renderPage();
    expect(H.projectsArgs).toContain(true);
    expect(H.requirementsArgs).toContain('p1');
  });

  it('reads them from the list when it carries them, and asks no project', () => {
    H.items = [item()];
    H.envelope = LIBRARY;
    renderPage();
    expect(H.projectsArgs.every((enabled) => enabled === false)).toBe(true);
    expect(H.requirementsArgs.every((id) => id === null)).toBe(true);
  });

  it('renames and describes, sending only what changed', async () => {
    H.items = [item()];
    renderPage();
    const dialog = await openAction('Marta', 'Rename or describe');
    expect(within(dialog).getByText('Every project that uses it shows the new name and description.')).toBeTruthy();
    fireEvent.change(within(dialog).getByLabelText(/^Name/), { target: { value: '  Marta B  ' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(H.rename).toHaveBeenCalledWith('l1', { name: 'Marta B' }));
    await waitFor(() => expect(H.mutate).toHaveBeenCalled());
    expect(H.notify).toHaveBeenCalledWith(expect.objectContaining({ message: '"Marta B" saved.' }));
  });

  it('saves nothing when nothing changed', async () => {
    H.items = [item()];
    renderPage();
    const dialog = await openAction('Marta', 'Rename or describe');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    expect(H.rename).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('clears a description with an empty one', async () => {
    H.items = [item()];
    renderPage();
    const dialog = await openAction('Marta', 'Rename or describe');
    fireEvent.change(within(dialog).getByLabelText(/Description/), { target: { value: '' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(H.rename).toHaveBeenCalledWith('l1', { description: '' }));
  });

  it('says a taken name in plain words', async () => {
    H.items = [item()];
    H.rename.mockRejectedValue(refusal('LIBRARY_NAME_TAKEN'));
    renderPage();
    const dialog = await openAction('Marta', 'Rename or describe');
    fireEvent.change(within(dialog).getByLabelText(/^Name/), { target: { value: 'Paolo' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    expect((await screen.findByTestId('library-rename-error')).textContent).toBe(
      'The workspace library already has an item with that name. Rename one of the two, then try again.',
    );
    expect(H.mutate).not.toHaveBeenCalled();
  });

  it('confirms a person again with the workspace consent, the 18-or-older tick and the keep-for days', async () => {
    H.items = [item()];
    renderPage();
    const dialog = await openAction('Marta', 'Confirm again');
    expect(within(dialog).getByText(/renews this person's consent for the whole workspace/)).toBeTruthy();
    expect(within(dialog).getByText('The files stay the same. To use other files, save a new item from a project.')).toBeTruthy();
    expect(within(dialog).getByTestId('library-statement').textContent).toBe(WS_EN);
    const submit = within(dialog).getByTestId('library-reconfirm-submit') as HTMLButtonElement;
    expect(submit.disabled).toBe(true);
    fireEvent.click(within(dialog).getByText('Italiano'));
    fireEvent.click(within(dialog).getByLabelText('I have read this statement and I confirm it'));
    expect(submit.disabled).toBe(true);
    fireEvent.click(within(dialog).getByLabelText('The person is 18 or older'));
    fireEvent.change(within(dialog).getByLabelText(/Keep for \(days\)/), { target: { value: '180' } });
    expect(submit.disabled).toBe(false);
    fireEvent.click(submit);
    await waitFor(() => expect(H.reconfirm).toHaveBeenCalledWith('l1', {
      consentStatement: WS_IT, consentLocale: 'it', subjectIsAdult: true, retentionDays: 180,
    }));
    await waitFor(() => expect(H.mutate).toHaveBeenCalled());
    expect(H.notify).toHaveBeenCalledWith(expect.objectContaining({ message: '"Marta" was confirmed again.' }));
  });

  it('confirms an object again with its statement only', async () => {
    H.items = [SHOE];
    renderPage();
    const dialog = await openAction('Blue shoe', 'Confirm again');
    expect(within(dialog).getByTestId('library-statement').textContent).toBe(WS_OBJ_EN);
    expect(within(dialog).queryByLabelText('The person is 18 or older')).toBeNull();
    expect(within(dialog).queryByLabelText(/Keep for/)).toBeNull();
    fireEvent.click(within(dialog).getByLabelText('I have read this statement and I confirm it'));
    fireEvent.click(within(dialog).getByTestId('library-reconfirm-submit'));
    await waitFor(() => expect(H.reconfirm).toHaveBeenCalledWith('l2', { consentStatement: WS_OBJ_EN, consentLocale: 'en' }));
  });

  it.each([
    ['subject_must_be_adult', 'Avatars can only be made of people who are 18 or older.'],
    ['retention_out_of_range', 'Choose a keep-for period inside the allowed range.'],
    ['AVATAR_CONSENT_REVOKED', 'Consent was withdrawn, so this can no longer be changed. Save a new item from a project instead.'],
  ])('says a refused confirmation in plain words: %s', async (code, sentence) => {
    H.items = [item()];
    H.reconfirm.mockRejectedValue(refusal(code, 400));
    renderPage();
    const dialog = await openAction('Marta', 'Confirm again');
    fireEvent.click(within(dialog).getByLabelText('I have read this statement and I confirm it'));
    fireEvent.click(within(dialog).getByLabelText('The person is 18 or older'));
    fireEvent.click(within(dialog).getByTestId('library-reconfirm-submit'));
    expect((await screen.findByTestId('library-reconfirm-error')).textContent).toBe(sentence);
  });

  it('cannot confirm again while no statement could be loaded (a workspace with no project left)', async () => {
    H.items = [item()];
    H.projects = [];
    renderPage();
    const dialog = await openAction('Marta', 'Confirm again');
    expect(within(dialog).getByTestId('library-no-statement')).toBeTruthy();
    expect((within(dialog).getByTestId('library-reconfirm-submit') as HTMLButtonElement).disabled).toBe(true);
  });

  it('withdraws consent only after a confirmation that says it reaches every project at once', async () => {
    H.items = [item()];
    renderPage();
    const dialog = await openAction('Marta', 'Withdraw consent');
    expect(within(dialog).getByText(/stops being usable in every project of this workspace at once/)).toBeTruthy();
    expect(within(dialog).getByText(/deleted automatically after 30 days/)).toBeTruthy();
    expect(H.revoke).not.toHaveBeenCalled();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Withdraw consent' }));
    await waitFor(() => expect(H.revoke).toHaveBeenCalledWith('l1'));
    await waitFor(() => expect(H.mutate).toHaveBeenCalled());
    expect(H.notify).toHaveBeenCalledWith(expect.objectContaining({ message: 'Consent withdrawn: "Marta" can no longer be used in any project.' }));
  });

  it('cancelling the withdrawal changes nothing', async () => {
    H.items = [item()];
    renderPage();
    const dialog = await openAction('Marta', 'Withdraw consent');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(H.revoke).not.toHaveBeenCalled();
  });

  it('says how many projects a deletion reaches (read fresh), then shows the report', async () => {
    H.items = [item()];
    H.fresh = item({ importCount: 5 });
    H.remove.mockResolvedValue({ imports: { deleted: 4, failed: 1 }, objects: { deleted: 3, failed: 2 } });
    renderPage();
    const dialog = await openAction('Marta', 'Delete');
    expect(H.itemArgs).toContain('l1');
    expect(within(dialog).getByText(/removes it from the workspace library and from every project that uses it/)).toBeTruthy();
    expect(within(dialog).getByTestId('library-delete-projects').textContent).toBe('It is removed from 5 projects, with the files copied there.');
    expect(H.remove).not.toHaveBeenCalled();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete from the library' }));
    const report = await screen.findByTestId('library-delete-report');
    expect(H.remove).toHaveBeenCalledWith('l1');
    expect(report.textContent).toContain('"Marta" was deleted from the library.');
    expect(report.textContent).toContain('Removed from 4 projects.');
    expect(report.textContent).toContain('1 project could not be updated. Remove it there from the project\'s Avatars tab.');
    expect(report.textContent).toContain('3 stored files deleted.');
    expect(report.textContent).toContain('2 stored files could not be deleted. They are removed automatically later.');
    expect(H.mutate).toHaveBeenCalled();
    fireEvent.click(within(report).getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByTestId('library-delete-report')).toBeNull());
  });

  it('reports a deletion that no project was using, and tolerates a report with parts missing', async () => {
    H.items = [SHOE];
    H.remove.mockResolvedValue({ objects: { deleted: 1, failed: 0 } });
    renderPage();
    const dialog = await openAction('Blue shoe', 'Delete');
    expect(within(dialog).getByTestId('library-delete-projects').textContent).toBe('No project uses it right now.');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete from the library' }));
    const report = await screen.findByTestId('library-delete-report');
    expect(report.textContent).toContain('No project was using it.');
    expect(report.textContent).toContain('1 stored file deleted.');
    expect(report.textContent).not.toContain('could not');
  });

  it('keeps the dialog open and says why when the deletion is refused', async () => {
    H.items = [item()];
    H.remove.mockRejectedValue(new ApiError('', 403));
    renderPage();
    const dialog = await openAction('Marta', 'Delete');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete from the library' }));
    expect((await screen.findByTestId('library-delete-error')).textContent).toBeTruthy();
    expect(screen.queryByTestId('library-delete-report')).toBeNull();
    expect(H.mutate).not.toHaveBeenCalled();
  });
});

describe('Avatar library page: variants', () => {
  const CAR = item({
    id: 'car', subjectKind: 'Object', name: 'Car', description: null, references: { images: 2, videos: 0, audio: 0 },
    primaryReferenceId: null, retentionUntil: null, importCount: 2, variantCount: 2,
  });
  const RED = item({
    id: 'red', subjectKind: 'Object', name: 'Red car', description: null, references: { images: 1, videos: 0, audio: 0 },
    primaryReferenceId: null, retentionUntil: null, importCount: 1,
    variantOfItemId: 'car', variantLabel: 'Red', variantDescription: 'matte red paint, black rims',
  });
  const BLUE = item({ ...RED, id: 'blue', name: 'Car · Blue', variantLabel: 'Blue', variantDescription: 'deep blue paint', importCount: 0 });

  it('lists variants under their original as labelled entries, each with its status, what sets it apart and its projects', () => {
    H.items = [RED, CAR, BLUE];
    renderPage();
    expect(screen.queryByTestId('library-item-red')).toBeNull();
    expect(screen.queryByTestId('library-item-blue')).toBeNull();
    const car = screen.getByTestId('library-item-car');
    const variants = within(car).getByTestId('library-variants-car');
    expect(within(variants).getByText('2 variants')).toBeTruthy();
    expect(within(variants).getAllByRole('listitem').map((row) => row.getAttribute('data-testid')))
      .toEqual(['library-variant-blue', 'library-variant-red']);
    const red = within(variants).getByTestId('library-variant-red');
    expect(within(red).getByText('Car · Red')).toBeTruthy();
    expect(within(red).getByTestId('library-status').textContent).toBe('Ready');
    expect(within(red).getByText('matte red paint, black rims')).toBeTruthy();
    expect(within(red).getByText('Used in 1 project')).toBeTruthy();
    expect(within(variants).getByTestId('library-variant-blue').textContent).toContain('Not used in any project yet');
  });

  it('shows a variant whose original is not in the library as an item of its own, by its own name', () => {
    H.items = [RED];
    renderPage();
    const red = screen.getByTestId('library-item-red');
    expect(within(red).getByText('Red car')).toBeTruthy();
    expect(within(red).queryByTestId('library-variants-red')).toBeNull();
  });

  it('gives each variant its own actions, the ones any item of its kind has', async () => {
    H.items = [CAR, RED];
    H.remove.mockResolvedValue({ imports: { deleted: 1, failed: 0 }, objects: { deleted: 1, failed: 0 } });
    renderPage();
    expect(await menuOf('Car · Red')).toEqual(['Rename or describe', 'Confirm again', 'Delete']);
    cleanup();
    renderPage();
    const dialog = await openAction('Car · Red', 'Delete');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete from the library' }));
    await screen.findByTestId('library-delete-report');
    expect(H.remove).toHaveBeenCalledWith('red');
  });

  it('offers a member no action on a variant either', () => {
    H.role = 'Member';
    H.items = [asMember(CAR), asMember(RED)];
    renderPage();
    expect(within(screen.getByTestId('library-variant-red')).getByText('Car · Red')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Actions for Car · Red' })).toBeNull();
  });
});
