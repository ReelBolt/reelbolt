// @vitest-environment jsdom
/**
 * The workspace avatar library inside a project's Avatars tab (docs/design/avatar-objects.md "The workspace
 * avatar library"): the picker, an import's card and its two actions, "Save to the workspace library", the
 * permission to send media to AI services, and a server from before the library.
 * Hooks and mutating calls are mocked like `ProjectAvatarsTab.test.tsx`, so the tests pin the tab's own behaviour.
 */
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { findSmallFonts } from '@/components/editor/__tests__/no-small-fonts';
import type { AvatarLibraryRequirements, AvatarRequirements, ProjectAvatar } from '@/lib/types/avatars';
import type { AvatarLibraryItem } from '@/lib/types/avatar-library';

const H = vi.hoisted(() => ({
  avatars: [] as unknown[],
  voices: [] as unknown[],
  library: [] as unknown[] | undefined,
  libraryError: undefined as unknown,
  libraryArgs: [] as unknown[][],
  consent: undefined as unknown,
  mutate: vi.fn(),
  mutateFiles: vi.fn(),
  libraryMutate: vi.fn(),
  consentMutate: vi.fn(),
  update: vi.fn(),
  del: vi.fn(),
  grant: vi.fn(),
  withdraw: vi.fn(),
  importItem: vi.fn(),
  publish: vi.fn(),
  notify: vi.fn(),
}));

vi.mock('@/lib/hooks/use-avatars', () => ({
  useProjectAvatars: () => ({ data: H.avatars, error: undefined, isLoading: false, mutate: H.mutate }),
  useAvatarUsage: () => ({ data: { avatarId: 'a1', takeCount: 0, runs: [], picturesUsingReferences: 0 }, error: undefined, isLoading: false }),
  useAvatarRequirements: () => ({ data: undefined, error: undefined }),
  useAvatarMediaConsent: () => ({ data: H.consent, mutate: H.consentMutate }),
}));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: [], mutate: H.mutateFiles }) }));
// Builds in progress have their own tests (ProjectAvatarsVariants.test.tsx); here the project has none.
vi.mock('@/lib/hooks/use-avatar-builds', () => ({ useAvatarBuilds: () => ({ builds: [], off: false, refresh: async () => undefined }) }));
vi.mock('@/lib/hooks/use-voices', () => ({ useProjectVoices: () => ({ data: H.voices }) }));
vi.mock('@/lib/hooks/use-avatar-library', () => ({
  useAvatarLibrary: (...args: unknown[]) => {
    H.libraryArgs.push(args);
    return { items: H.library, library: null, error: H.libraryError, isLoading: false, mutate: H.libraryMutate, off: false };
  },
}));
vi.mock('@/lib/api/avatars', () => ({
  createAvatar: vi.fn(),
  replaceAvatarReferences: vi.fn(),
  revokeAvatarConsent: vi.fn(),
  updateAvatar: (...a: unknown[]) => H.update(...a),
  deleteAvatar: (...a: unknown[]) => H.del(...a),
  grantAvatarMediaConsent: (...a: unknown[]) => H.grant(...a),
  withdrawAvatarMediaConsent: (...a: unknown[]) => H.withdraw(...a),
}));
vi.mock('@/lib/api/avatar-library', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/api/avatar-library')>()),
  importLibraryAvatar: (...a: unknown[]) => H.importItem(...a),
  publishToAvatarLibrary: (...a: unknown[]) => H.publish(...a),
}));
vi.mock('@mantine/notifications', () => ({ notifications: { show: (...a: unknown[]) => H.notify(...a) } }));
vi.mock('@/components/outputs/CheckedVideo', () => ({ CheckedImage: ({ alt }: { alt: string }) => <span role="img" aria-label={alt} /> }));

import { ProjectAvatarsTab } from '../ProjectAvatarsTab';

const WS_EN = 'I confirm that this person agreed to be used in any project of this workspace.';
const WS_IT = 'Confermo che questa persona ha accettato di essere usata in qualsiasi progetto di questo spazio di lavoro.';
const WS_OBJ_EN = 'I confirm that these files show an object and may be used in any project of this workspace.';
const WS_OBJ_IT = 'Confermo che questi file mostrano un oggetto e si possono usare in qualsiasi progetto di questo spazio di lavoro.';

const LIBRARY: AvatarLibraryRequirements = {
  enabled: true, canManage: true,
  consentStatementVersion: 'avatar-consent-workspace-2026-11', consentStatements: { en: WS_EN, it: WS_IT },
  objectStatementVersion: 'avatar-object-workspace-2026-11', objectStatements: { en: WS_OBJ_EN, it: WS_OBJ_IT },
  maxItemsPerKind: 100,
};

/** A server from before the library: both kinds, no `library`. */
const OLD: AvatarRequirements = {
  statementVersion: 'avatar-consent-2026-12',
  statements: { en: 'Project consent.', it: 'Consenso del progetto.' },
  locales: ['en', 'it'],
  limits: {
    maxAvatarsPerProject: 20, maxImages: 8, maxVideos: 3, maxAudio: 3, maxImageBytes: 30 * 1024 * 1024,
    minImageSidePx: 256, maxImageSidePx: 5760, minImageAspect: 0.4, maxImageAspect: 2.5, maxVideoBytes: 50 * 1024 * 1024,
    minVideoMs: 2000, maxVideoMs: 15000, minVideoFps: 23.976, maxVideoFps: 60, videoCodecs: ['h264'],
    maxAudioBytes: 15 * 1024 * 1024, minAudioMs: 2000, maxAudioMs: 15000, minRetentionDays: 1, maxRetentionDays: 730,
    defaultRetentionDays: 365, revokedGraceDays: 30, maxNameLength: 200, maxDescriptionLength: 2000, maxSubjectNameLength: 200,
  },
  mimeTypes: { image: ['image/jpeg', 'image/png'], video: ['video/mp4'], audio: ['audio/wav'] },
  roles: ['Face', 'HalfBody', 'FullBody', 'Motion', 'Speech'],
  bases: ['Self', 'WrittenPermission'],
  disclosureLabels: ['Visible', 'MetadataOnly'],
  entitled: true,
  moderationEnabled: false,
  csamScanningEnabled: false,
  kinds: { person: { enabled: true, entitled: true }, object: { enabled: true, entitled: true } },
  objectStatementVersion: 'avatar-object-2026-11',
  objectStatements: { en: 'Project object statement.', it: 'Dichiarazione del progetto.' },
};

const withLibrary = (over: Partial<AvatarLibraryRequirements> = {}, req: Partial<AvatarRequirements> = {}): AvatarRequirements =>
  ({ ...OLD, ...req, library: { ...LIBRARY, ...over } });
const MANAGER = withLibrary();
const MEMBER = withLibrary({ canManage: false });

const PERSON: ProjectAvatar = {
  id: 'a1', projectId: 'p1', subjectKind: 'Person', coversPictures: true, name: 'Anna', description: null,
  selectable: true, unselectableReason: null, projectVoiceId: null, projectVoiceName: null, projectVoiceSelectable: null,
  consent: {
    attestedByUserId: 'u1', attestedAt: '2026-11-01T00:00:00Z', statement: 'Project consent.', statementVersion: 'avatar-consent-2026-12',
    locale: 'en', subjectName: 'Anna Rossi', basis: 'Self', subjectIsAdult: true, documentFileId: null, revokedAt: null, revokedByUserId: null,
  },
  retentionUntil: '2099-01-01T00:00:00Z', disclosureLabel: 'Visible', primaryReferenceId: 'r1',
  references: [
    { id: 'r1', projectFileId: 'f1', fileName: 'anna.jpg', kind: 'Image', role: 'Face', ordinal: 0, width: 800, height: 800, durationMs: null, attachedAt: '2026-11-01T00:00:00Z', state: 'ok' },
  ],
  createdAt: '2026-11-01T00:00:00Z', createdByUserId: 'u1', updatedAt: '2026-11-01T00:00:00Z',
};
const OBJECT: ProjectAvatar = {
  ...PERSON, id: 'o1', name: 'Red shoe', subjectKind: 'Object', retentionUntil: null, disclosureLabel: 'MetadataOnly',
  consent: { ...PERSON.consent, statementVersion: 'avatar-object-2026-11', subjectName: '', subjectIsAdult: false },
  references: [
    { id: 'or1', projectFileId: 'f5', fileName: 'shoe.png', kind: 'Image', role: 'Front', ordinal: 0, width: 800, height: 800, durationMs: null, attachedAt: '2026-11-01T00:00:00Z', state: 'ok' },
  ],
  primaryReferenceId: 'or1',
};
const LINKED: ProjectAvatar = { ...PERSON, fromLibrary: true, libraryItemId: 'l1' };
const LINKED_OBJECT: ProjectAvatar = { ...OBJECT, fromLibrary: true, libraryItemId: 'l2' };

const item = (over: Partial<AvatarLibraryItem> = {}): AvatarLibraryItem => ({
  id: 'l1', subjectKind: 'Person', name: 'Marta', description: 'Our presenter', usable: true, unusableReason: null,
  references: { images: 2, videos: 1, audio: 0 }, primaryReferenceId: 'lr1', retentionUntil: '2099-01-01T00:00:00Z',
  importCount: 1, imported: false, canManage: true, ...over,
});
const SHOE = item({ id: 'l2', subjectKind: 'Object', name: 'Blue shoe', description: null, references: { images: 3, videos: 0, audio: 0 }, primaryReferenceId: null, retentionUntil: null });

function renderTab(requirements: AvatarRequirements = MANAGER) {
  return render(<MantineProvider><ProjectAvatarsTab projectId="p1" requirements={requirements} /></MantineProvider>);
}

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
  window.HTMLElement.prototype.scrollIntoView = () => {};
  H.avatars = [];
  H.voices = [];
  H.library = [];
  H.libraryError = undefined;
  H.libraryArgs = [];
  H.consent = undefined;
  H.mutate = vi.fn().mockResolvedValue(undefined);
  H.mutateFiles = vi.fn().mockResolvedValue(undefined);
  H.libraryMutate = vi.fn().mockResolvedValue(undefined);
  H.consentMutate = vi.fn().mockResolvedValue(undefined);
  H.update.mockReset().mockResolvedValue(LINKED);
  H.del.mockReset().mockResolvedValue({ id: 'a1', rowDeleted: true, references: { deleted: 1, failed: 0, kept: 0 }, vendorAssets: [], consentDocumentDeleted: false });
  H.grant.mockReset().mockResolvedValue({ consented: true, consentedAt: '2026-11-16T10:00:00Z' });
  H.withdraw.mockReset().mockResolvedValue({ consented: false, consentedAt: null });
  H.importItem.mockReset().mockResolvedValue(LINKED);
  H.publish.mockReset().mockResolvedValue(item());
  H.notify.mockReset();
});
afterEach(cleanup);

const button = (name: string | RegExp) => screen.getByRole('button', { name }) as HTMLButtonElement;
const radio = (name: string | RegExp) => screen.getByRole('radio', { name }) as HTMLInputElement;
const refusal = (code: string, status = 409, extra: Record<string, unknown> = {}) =>
  new ApiError(`server said ${code}`, status, [], { code, message: `server said ${code}`, ...extra });

async function openPicker() {
  fireEvent.click(button('From the workspace library'));
  return screen.findByTestId('library-picker');
}
async function menuOf(name: string) {
  fireEvent.click(screen.getByRole('button', { name: `Actions for ${name}` }));
  return (await screen.findAllByRole('menuitem')).map((m) => m.textContent);
}
async function openAction(name: string, label: string) {
  fireEvent.click(screen.getByRole('button', { name: `Actions for ${name}` }));
  fireEvent.click(await screen.findByRole('menuitem', { name: label }));
  return screen.findByRole('dialog');
}

describe('Avatars tab on a server from before the library', () => {
  it('shows nothing of the library when the requirements carry none', async () => {
    H.avatars = [PERSON];
    renderTab(OLD);
    expect(screen.queryByTestId('avatars-from-library')).toBeNull();
    expect(screen.queryByTestId('avatar-from-library')).toBeNull();
    expect(await menuOf('Anna')).toEqual(['Details and usage', 'Edit', 'Change files', 'Withdraw consent', 'Delete']);
    // The list is never asked.
    expect(H.libraryArgs).toEqual([]);
  });

  it('shows nothing of the library when it is switched off', async () => {
    H.avatars = [PERSON];
    renderTab(withLibrary({ enabled: false }));
    expect(screen.queryByTestId('avatars-from-library')).toBeNull();
    expect(await menuOf('Anna')).not.toContain('Save to the workspace library');
  });
});

describe('Avatars tab: picking from the workspace library', () => {
  it('asks the library for this project only once the picker is open', async () => {
    renderTab();
    expect(H.libraryArgs).toEqual([]);
    await openPicker();
    expect(H.libraryArgs[0]).toEqual(['p1']);
  });

  it('lists people and objects apart, each with its picture, name, description, status and files', async () => {
    H.library = [item(), SHOE];
    renderTab();
    const picker = await openPicker();
    expect(radio('People (1)').checked).toBe(true);
    const row = within(picker).getByTestId('library-row-l1');
    expect(within(row).getByText('Marta')).toBeTruthy();
    expect(within(row).getByText('Our presenter')).toBeTruthy();
    expect(within(row).getByTestId('library-status').textContent).toBe('Ready');
    expect(within(row).getByText('2 pictures, 1 video, 0 recordings')).toBeTruthy();
    expect(within(row).getByRole('img', { name: 'Picture of Marta' }).getAttribute('src')).toBe('/api/v1/avatar-library/l1/references/lr1/content');
    expect(within(row).getByRole('button', { name: 'Import Marta' })).toBeTruthy();
    expect(within(picker).queryByTestId('library-row-l2')).toBeNull();

    fireEvent.click(radio('Objects (1)'));
    const shoe = within(picker).getByTestId('library-row-l2');
    expect(within(shoe).getByText('3 pictures, 0 videos')).toBeTruthy();
    expect(within(shoe).queryByRole('img')).toBeNull();
    expect((within(shoe).getByRole('button', { name: 'Import Blue shoe' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('opens on the objects when the library has only objects', async () => {
    H.library = [SHOE];
    renderTab();
    await openPicker();
    expect(radio('Objects (1)').checked).toBe(true);
    expect(screen.getByTestId('library-row-l2')).toBeTruthy();
  });

  it('offers only the kinds that are switched on', async () => {
    H.library = [item(), SHOE];
    renderTab(withLibrary({}, { kinds: { person: { enabled: true, entitled: true }, object: { enabled: false, entitled: false } } }));
    const picker = await openPicker();
    expect(within(picker).queryByTestId('library-kind-filter')).toBeNull();
    expect(within(picker).getByTestId('library-row-l1')).toBeTruthy();
    expect(within(picker).queryByTestId('library-row-l2')).toBeNull();
  });

  it('lists an item that cannot be used with the reason, and does not let it be imported', async () => {
    H.library = [
      item({ id: 'l3', name: 'Paolo', usable: false, unusableReason: 'consent_revoked' }),
      item({ id: 'l4', name: 'Luca', usable: false, unusableReason: 'retention_expired' }),
    ];
    renderTab();
    const picker = await openPicker();
    const revoked = within(picker).getByTestId('library-row-l3');
    expect(within(revoked).getByTestId('library-status').textContent).toBe('Consent withdrawn');
    expect(within(revoked).getByTestId('library-blocked').textContent).toBe('Consent was withdrawn, so it can no longer be used in any project.');
    expect((within(revoked).getByRole('button', { name: 'Import Paolo' }) as HTMLButtonElement).disabled).toBe(true);
    const expired = within(picker).getByTestId('library-row-l4');
    expect(within(expired).getByTestId('library-status').textContent).toBe('Expired');
    expect(within(expired).getByTestId('library-blocked').textContent).toContain('An Owner or Admin must confirm it again');
    expect((within(expired).getByRole('button', { name: 'Import Luca' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('marks an item that is already in this project and offers no second import', async () => {
    H.library = [item({ imported: true })];
    renderTab();
    const row = within(await openPicker()).getByTestId('library-row-l1');
    expect(within(row).getByTestId('library-imported').textContent).toBe('Already in this project');
    expect(within(row).queryByRole('button', { name: 'Import Marta' })).toBeNull();
  });

  it('follows the rules of making one: the kind must be in the plan and the project must have room', async () => {
    H.library = [item(), SHOE];
    H.avatars = Array.from({ length: 20 }, (_, i) => ({ ...OBJECT, id: `o${i}`, name: `Object ${i}` }));
    renderTab(withLibrary({}, { entitled: false, kinds: { person: { enabled: true, entitled: false }, object: { enabled: true, entitled: true } } }));
    const picker = await openPicker();
    const person = within(picker).getByTestId('library-row-l1');
    expect(within(person).getByTestId('library-blocked').textContent).toBe('Your current plan does not include avatars of people.');
    expect((within(person).getByRole('button', { name: 'Import Marta' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(within(picker).getByRole('radio', { name: 'Objects (1)' }));
    const shoe = within(picker).getByTestId('library-row-l2');
    expect(within(shoe).getByTestId('library-blocked').textContent).toBe('This project already has 20 objects, the most it can hold.');
    expect((within(shoe).getByRole('button', { name: 'Import Blue shoe' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('imports an item, says so, and refreshes the avatars, the files and the library', async () => {
    H.library = [item()];
    renderTab();
    await openPicker();
    fireEvent.click(button('Import Marta'));
    await waitFor(() => expect(H.importItem).toHaveBeenCalledWith('p1', 'l1'));
    await waitFor(() => expect(H.notify).toHaveBeenCalledWith(expect.objectContaining({ message: '"Marta" was added to this project.' })));
    expect(H.mutate).toHaveBeenCalled();
    expect(H.mutateFiles).toHaveBeenCalled();
    expect(H.libraryMutate).toHaveBeenCalled();
    // The picker stays open for the next one.
    expect(screen.getByTestId('library-picker')).toBeTruthy();
  });

  it.each([
    ['AVATAR_ALREADY_IMPORTED', 409, 'This one is already in this project.'],
    ['LIBRARY_ITEM_NOT_USABLE', 409, 'It cannot be used right now, so it cannot be imported. An Owner or Admin can check it in the workspace library.'],
    ['LIBRARY_IMPORT_LIMIT', 409, 'It is already used in as many projects as one library item can serve. Remove it from a project that no longer needs it, then try again.'],
    ['AVATAR_LIMIT_REACHED', 409, 'This project already holds as many of this kind as it can. Delete one first.'],
    ['PLAN_NOT_ENTITLED', 402, 'Your current plan does not include this. Upgrade your plan to use it.'],
    ['STORAGE_QUOTA_EXCEEDED', 413, 'Your workspace is out of storage, so the files could not be copied into this project. Delete files or upgrade your plan, then try again.'],
    ['EMAIL_NOT_VERIFIED', 403, 'Verify your email address first.'],
  ])('says a refused import in plain words: %s', async (code, status, sentence) => {
    H.library = [item()];
    H.importItem.mockRejectedValue(refusal(code, status));
    renderTab();
    await openPicker();
    fireEvent.click(button('Import Marta'));
    expect((await screen.findByTestId('library-import-error')).textContent).toBe(sentence);
    expect(H.notify).not.toHaveBeenCalled();
    // A refusal can mean the list is old: it is read again.
    expect(H.libraryMutate).toHaveBeenCalled();
  });

  it('falls back to the server sentence for a refusal it has no words for', async () => {
    H.library = [item()];
    H.importItem.mockRejectedValue(refusal('SOMETHING_NEW'));
    renderTab();
    await openPicker();
    fireEvent.click(button('Import Marta'));
    expect((await screen.findByTestId('library-import-error')).textContent).toBe('server said SOMETHING_NEW');
  });

  it('tells a manager how to fill an empty library, and a member who can', async () => {
    renderTab(MANAGER);
    expect((await openPicker()).textContent).toContain('choose "Save to the workspace library"');
    cleanup();
    renderTab(MEMBER);
    const empty = within(await openPicker()).getByTestId('library-picker-empty');
    expect(empty.textContent).toContain('An Owner or Admin of the workspace can add people and objects');
    expect(empty.textContent).not.toContain('choose "Save');
  });

  it('says so when the library cannot be loaded', async () => {
    H.library = undefined;
    H.libraryError = new ApiError('boom', 500);
    renderTab();
    expect(within(await openPicker()).getByText('Could not load the workspace library. Try again.')).toBeTruthy();
  });
});

describe('Avatars tab: variants in the workspace library', () => {
  const CAR = item({ id: 'car', subjectKind: 'Object', name: 'Car', description: null, primaryReferenceId: null, retentionUntil: null, variantCount: 1 });
  const RED = item({
    id: 'red', subjectKind: 'Object', name: 'Red car', description: null, primaryReferenceId: null, retentionUntil: null,
    variantOfItemId: 'car', variantLabel: 'Red', variantDescription: 'matte red paint, black rims',
  });

  it('lists a variant under its original, labelled, with what sets it apart, and imports it by itself', async () => {
    H.library = [RED, CAR];
    renderTab();
    const picker = await openPicker();
    const variants = within(picker).getByTestId('library-variants-car');
    expect(variants.getAttribute('aria-label')).toBe('Variants of Car');
    const red = within(variants).getByTestId('library-row-red');
    expect(within(red).getByText('Car · Red')).toBeTruthy();
    expect(within(red).getByTestId('library-variant-description').textContent).toBe('matte red paint, black rims');
    fireEvent.click(within(red).getByRole('button', { name: 'Import Car · Red' }));
    await waitFor(() => expect(H.importItem).toHaveBeenCalledWith('p1', 'red'));
    await waitFor(() => expect(H.notify).toHaveBeenCalledWith(expect.objectContaining({ message: '"Car · Red" was added to this project.' })));
  });

  it('lets a variant in when its original is already in a full project: it lands under it and takes no room', async () => {
    H.library = [item({ ...CAR, imported: true }), RED];
    H.avatars = Array.from({ length: 20 }, (_, i) => ({ ...OBJECT, id: `o${i}`, name: `Object ${i}` }));
    renderTab();
    const picker = await openPicker();
    expect(within(within(picker).getByTestId('library-row-car')).getByTestId('library-imported')).toBeTruthy();
    const red = within(picker).getByTestId('library-row-red');
    expect(within(red).queryByTestId('library-blocked')).toBeNull();
    expect((within(red).getByRole('button', { name: 'Import Car · Red' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it.each([
    ['VARIANT_LABEL_TAKEN', 'Its original in this project already has a variant with that label. Remove that variant from this project or change its label, then try again.'],
    ['VARIANT_LIMIT_REACHED', 'Its original in this project already has as many variants as it can have. Delete one first.'],
  ])('says a refused variant import in plain words: %s', async (code, sentence) => {
    H.library = [item({ ...CAR, imported: true }), RED];
    H.importItem.mockRejectedValue(refusal(code));
    renderTab();
    await openPicker();
    fireEvent.click(button('Import Car · Red'));
    expect((await screen.findByTestId('library-import-error')).textContent).toBe(sentence);
  });
});

describe('Avatars tab: an import from the workspace library', () => {
  it('badges the card, says where it is managed, and offers only Details, Voice and Remove', async () => {
    H.avatars = [LINKED];
    renderTab(MANAGER);
    const card = screen.getByTestId('avatar-card-a1');
    expect(within(card).getByTestId('avatar-from-library').textContent).toBe('Workspace library');
    const managed = within(card).getByTestId('avatar-library-managed');
    expect(managed.textContent).toContain('Its name, files and consent are managed in the workspace library.');
    expect(within(managed).getByRole('link', { name: 'Open the workspace library' }).getAttribute('href')).toContain('/settings/avatar-library');
    expect(await menuOf('Anna')).toEqual(['Details and usage', 'Voice', 'Remove from this project']);
  });

  it('gives a member the same card without the link to the library page', () => {
    H.avatars = [LINKED];
    renderTab(MEMBER);
    const managed = within(screen.getByTestId('avatar-card-a1')).getByTestId('avatar-library-managed');
    expect(managed.textContent).toBe('Its name, files and consent are managed in the workspace library.');
    expect(within(managed).queryByRole('link')).toBeNull();
  });

  it('offers an imported object Details and Remove only: it has no voice', async () => {
    H.avatars = [LINKED_OBJECT];
    renderTab(MANAGER);
    expect(await menuOf('Red shoe')).toEqual(['Details and usage', 'Remove from this project']);
  });

  it('never offers to fix an import here: the reason points at the workspace library', () => {
    H.avatars = [{ ...LINKED, selectable: false, unselectableReason: 'retention_expired' }];
    renderTab(MEMBER);
    const card = screen.getByTestId('avatar-card-a1');
    expect(within(card).getByText('Its keep-until date has passed. An Owner or Admin must confirm it again in the workspace library.')).toBeTruthy();
    expect(within(card).queryByRole('button', { name: 'Confirm consent again' })).toBeNull();
    expect(within(card).queryByRole('button', { name: 'Choose the files again' })).toBeNull();
  });

  it('sets the voice link and nothing else', async () => {
    H.avatars = [LINKED];
    H.voices = [{ id: 'v1', name: 'Warm voice', selectable: true }, { id: 'v2', name: 'Broken voice', selectable: false }];
    renderTab(MEMBER);
    const dialog = await openAction('Anna', 'Voice');
    expect(within(dialog).getByText(/Everything else is managed in the workspace library/)).toBeTruthy();
    // Only the voice is asked: no name, no description, no AI label.
    expect(within(dialog).queryByLabelText(/name/i)).toBeNull();
    expect(within(dialog).queryByText(/AI label/)).toBeNull();
    fireEvent.click(within(dialog).getByRole('combobox', { name: /Voice/ }));
    fireEvent.click(await screen.findByRole('option', { name: 'Warm voice', hidden: true }));
    expect(screen.queryByRole('option', { name: 'Broken voice', hidden: true })).toBeNull();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save voice' }));
    await waitFor(() => expect(H.update).toHaveBeenCalledWith('p1', 'a1', { projectVoiceId: 'v1' }));
    await waitFor(() => expect(H.mutate).toHaveBeenCalled());
  });

  it('clears the voice link with the one field the server takes', async () => {
    H.avatars = [{ ...LINKED, projectVoiceId: 'v1', projectVoiceName: 'Warm voice', projectVoiceSelectable: true }];
    H.voices = [{ id: 'v1', name: 'Warm voice', selectable: true }];
    renderTab(MEMBER);
    const dialog = await openAction('Anna', 'Voice');
    fireEvent.click(within(dialog).getByRole('combobox', { name: /Voice/ }));
    fireEvent.click(await screen.findByRole('option', { name: 'No voice', hidden: true }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save voice' }));
    await waitFor(() => expect(H.update).toHaveBeenCalledWith('p1', 'a1', { clearProjectVoice: true }));
  });

  it('removes it from the project only after a confirmation that says what stays', async () => {
    H.avatars = [LINKED];
    renderTab(MEMBER);
    const dialog = await openAction('Anna', 'Remove from this project');
    expect(within(dialog).getByText(/the copies of its files made for this project are deleted/)).toBeTruthy();
    expect(within(dialog).getByText(/It stays in the workspace library and in every other project that uses it/)).toBeTruthy();
    expect(H.del).not.toHaveBeenCalled();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove from this project' }));
    await waitFor(() => expect(H.del).toHaveBeenCalledWith('p1', 'a1'));
    await waitFor(() => expect(H.notify).toHaveBeenCalledWith(expect.objectContaining({ message: '"Anna" was removed from this project.' })));
    expect(H.mutate).toHaveBeenCalled();
    expect(H.mutateFiles).toHaveBeenCalled();
  });

  it('cancelling the removal changes nothing', async () => {
    H.avatars = [LINKED];
    renderTab(MEMBER);
    const dialog = await openAction('Anna', 'Remove from this project');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(H.del).not.toHaveBeenCalled();
  });

  it('says which copied files could not be deleted', async () => {
    H.avatars = [LINKED];
    H.del.mockResolvedValue({ id: 'a1', rowDeleted: true, references: { deleted: 0, failed: 2, kept: 0 }, vendorAssets: [], consentDocumentDeleted: false });
    renderTab(MEMBER);
    const dialog = await openAction('Anna', 'Remove from this project');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove from this project' }));
    const report = await screen.findByTestId('linked-remove-report');
    expect(report.textContent).toContain('2 copied files could not be deleted. Delete them from the project\'s files.');
  });

  it('says in plain words that an import cannot be changed here when the server refuses a stale edit', async () => {
    // The list still shows it as the project's own avatar; it was saved to the library meanwhile.
    H.avatars = [PERSON];
    H.update.mockRejectedValue(refusal('AVATAR_FROM_LIBRARY'));
    renderTab(MEMBER);
    const dialog = await openAction('Anna', 'Edit');
    fireEvent.change(within(dialog).getByLabelText(/Avatar name/), { target: { value: 'Anna B' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    expect((await screen.findByTestId('avatar-edit-error')).textContent).toBe(
      'This one comes from the workspace library, so it cannot be changed in this project. An Owner or Admin can change it in the workspace library.',
    );
  });
});

describe('Avatars tab: saving to the workspace library', () => {
  const openSave = (name = 'Anna') => openAction(name, 'Save to the workspace library');

  it('offers it to a manager on a usable avatar of the project, between the edits and the withdrawal', async () => {
    H.avatars = [PERSON];
    renderTab(MANAGER);
    expect(await menuOf('Anna')).toEqual(['Details and usage', 'Edit', 'Change files', 'Save to the workspace library', 'Withdraw consent', 'Delete']);
  });

  it('does not offer it to a member, for an avatar that cannot be used, or for an import', async () => {
    H.avatars = [PERSON];
    renderTab(MEMBER);
    expect(await menuOf('Anna')).not.toContain('Save to the workspace library');
    cleanup();
    H.avatars = [{ ...PERSON, selectable: false, unselectableReason: 'reference_missing' }];
    renderTab(MANAGER);
    expect(await menuOf('Anna')).not.toContain('Save to the workspace library');
    cleanup();
    H.avatars = [LINKED];
    renderTab(MANAGER);
    expect(await menuOf('Anna')).not.toContain('Save to the workspace library');
  });

  it('explains what it does and shows the workspace consent verbatim, in the chosen language', async () => {
    H.avatars = [PERSON];
    renderTab(MANAGER);
    const dialog = await openSave();
    const explain = within(dialog).getByTestId('library-save-explain').textContent ?? '';
    expect(explain).toContain('Every project of this workspace will be able to use "Anna".');
    expect(explain).toContain('This project\'s avatar becomes linked to the library item');
    // The workspace text, never the project one.
    expect(within(dialog).getByTestId('library-statement').textContent).toBe(WS_EN);
    expect(within(dialog).getByText('About: Anna Rossi')).toBeTruthy();
    fireEvent.click(within(dialog).getByText('Italiano'));
    await waitFor(() => expect(within(dialog).getByTestId('library-statement').textContent).toBe(WS_IT));
  });

  it('needs the tick, the 18-or-older tick and a keep-for period inside the range for a person', async () => {
    H.avatars = [PERSON];
    renderTab(MANAGER);
    const dialog = await openSave();
    const submit = within(dialog).getByTestId('library-save-submit') as HTMLButtonElement;
    expect(submit.disabled).toBe(true);
    fireEvent.click(within(dialog).getByLabelText('I have read this statement and I confirm it'));
    expect(submit.disabled).toBe(true);
    fireEvent.click(within(dialog).getByLabelText('The person is 18 or older'));
    expect(submit.disabled).toBe(false);
    const keepFor = within(dialog).getByLabelText(/Keep for \(days\)/) as HTMLInputElement;
    expect(keepFor.value).toBe('365');
    fireEvent.change(keepFor, { target: { value: '0' } });
    expect(submit.disabled).toBe(true);
    expect(within(dialog).getByText('Enter a whole number of days from 1 to 730.')).toBeTruthy();
    fireEvent.change(keepFor, { target: { value: '90' } });
    expect(submit.disabled).toBe(false);
    // Changing the language clears the tick: the person confirms the text they read.
    fireEvent.click(within(dialog).getByText('Italiano'));
    await waitFor(() => expect(submit.disabled).toBe(true));
  });

  it('sends the workspace statement of the chosen language unchanged, with a person\'s two fields', async () => {
    H.avatars = [PERSON];
    renderTab(MANAGER);
    const dialog = await openSave();
    fireEvent.click(within(dialog).getByText('Italiano'));
    fireEvent.click(within(dialog).getByLabelText('I have read this statement and I confirm it'));
    fireEvent.click(within(dialog).getByLabelText('The person is 18 or older'));
    fireEvent.click(within(dialog).getByTestId('library-save-submit'));
    await waitFor(() => expect(H.publish).toHaveBeenCalledTimes(1));
    expect(H.publish.mock.calls[0][0]).toEqual({
      projectId: 'p1', avatarId: 'a1', consentStatement: WS_IT, consentLocale: 'it', subjectIsAdult: true, retentionDays: 365,
    });
    await waitFor(() => expect(H.notify).toHaveBeenCalledWith(expect.objectContaining({ message: '"Anna" is now in the workspace library.' })));
    // The avatar is now an import: the list is read again.
    expect(H.mutate).toHaveBeenCalled();
  });

  it('asks an object only for its workspace statement, and sends none of a person\'s fields', async () => {
    H.avatars = [OBJECT];
    renderTab(MANAGER);
    const dialog = await openSave('Red shoe');
    expect(within(dialog).getByTestId('library-save-explain').textContent).toContain('This project\'s object becomes linked to the library item');
    expect(within(dialog).getByTestId('library-statement').textContent).toBe(WS_OBJ_EN);
    expect(within(dialog).queryByLabelText('The person is 18 or older')).toBeNull();
    expect(within(dialog).queryByLabelText(/Keep for/)).toBeNull();
    expect(within(dialog).queryByText(/About:/)).toBeNull();
    fireEvent.click(within(dialog).getByLabelText('I have read this statement and I confirm it'));
    fireEvent.click(within(dialog).getByTestId('library-save-submit'));
    await waitFor(() => expect(H.publish).toHaveBeenCalledTimes(1));
    expect(H.publish.mock.calls[0][0]).toEqual({ projectId: 'p1', avatarId: 'o1', consentStatement: WS_OBJ_EN, consentLocale: 'en' });
  });

  it.each([
    ['consent_statement_required', 400, 'The consent statement did not match. Reload the page and confirm it again.'],
    ['object_statement_required', 400, 'The statement did not match. Reload the page and confirm it again.'],
    ['subject_must_be_adult', 400, 'Avatars can only be made of people who are 18 or older.'],
    ['retention_out_of_range', 400, 'Choose a keep-for period inside the allowed range.'],
    ['LIBRARY_LIMIT_REACHED', 409, 'The workspace library already holds as many items of this kind as it can. Delete one first.'],
    ['LIBRARY_NAME_TAKEN', 409, 'The workspace library already has an item with that name. Rename one of the two, then try again.'],
    ['AVATAR_NAME_TAKEN', 409, 'That name is already in use. Rename one of the two, then try again.'],
    ['AVATAR_NOT_USABLE', 409, 'It cannot be used right now, so it cannot be saved to the library. Fix it in this project first.'],
  ])('says a refused save in plain words: %s', async (code, status, sentence) => {
    H.avatars = [PERSON];
    H.publish.mockRejectedValue(refusal(code, status));
    renderTab(MANAGER);
    const dialog = await openSave();
    fireEvent.click(within(dialog).getByLabelText('I have read this statement and I confirm it'));
    fireEvent.click(within(dialog).getByLabelText('The person is 18 or older'));
    fireEvent.click(within(dialog).getByTestId('library-save-submit'));
    expect((await screen.findByTestId('library-save-error')).textContent).toBe(sentence);
    expect(H.mutate).not.toHaveBeenCalled();
  });

  it('shows the server\'s newer statement for a fresh tick when a refusal carries it', async () => {
    H.avatars = [PERSON];
    H.publish.mockRejectedValueOnce(refusal('consent_statement_required', 400, { statementVersion: 'avatar-consent-workspace-2027-01', statements: { en: 'Newer text.', it: 'Testo più recente.' } }));
    renderTab(MANAGER);
    const dialog = await openSave();
    fireEvent.click(within(dialog).getByLabelText('I have read this statement and I confirm it'));
    fireEvent.click(within(dialog).getByLabelText('The person is 18 or older'));
    fireEvent.click(within(dialog).getByTestId('library-save-submit'));
    expect((await within(dialog).findByTestId('library-statement-changed')).textContent).toBe('The statement was updated. Read it and confirm it again.');
    expect(within(dialog).getByTestId('library-statement').textContent).toBe('Newer text.');
    const submit = within(dialog).getByTestId('library-save-submit') as HTMLButtonElement;
    expect(submit.disabled).toBe(true);
    fireEvent.click(within(dialog).getByLabelText('I have read this statement and I confirm it'));
    fireEvent.click(submit);
    await waitFor(() => expect(H.publish).toHaveBeenCalledTimes(2));
    expect(H.publish.mock.calls[1][0]).toMatchObject({ consentStatement: 'Newer text.', consentLocale: 'en' });
  });

  it('cannot be confirmed when the server sent no statement for the kind', async () => {
    H.avatars = [PERSON];
    renderTab(withLibrary({ consentStatements: {} }));
    const dialog = await openSave();
    expect(within(dialog).getByTestId('library-no-statement').textContent).toBe('The statement could not be loaded. Reload the page and try again.');
    expect((within(dialog).getByTestId('library-save-submit') as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('Avatars tab: sending media to AI services', () => {
  it('shows nothing on a server that does not have the permission yet', () => {
    renderTab(OLD);
    expect(screen.queryByTestId('avatar-media-consent')).toBeNull();
  });

  it('says it is not allowed, explains it, and records the permission on Allow', async () => {
    H.consent = { consented: false, consentedAt: null };
    renderTab(OLD);
    const row = screen.getByTestId('avatar-media-consent');
    expect(within(row).getByText('Sending media to outside AI model providers (other companies)')).toBeTruthy();
    expect(within(row).getByTestId('avatar-media-consent-state').textContent).toBe('Not allowed');
    expect(row.textContent).toContain('That needs your permission once for the whole project, and you can withdraw it at any time.');
    expect(row.textContent).toContain('Workflow steps are not affected: each step keeps its own switch.');
    fireEvent.click(within(row).getByRole('button', { name: 'Allow' }));
    await waitFor(() => expect(H.grant).toHaveBeenCalledWith('p1'));
    await waitFor(() => expect(H.consentMutate).toHaveBeenCalledWith({ consented: true, consentedAt: '2026-11-16T10:00:00Z' }, { revalidate: false }));
    expect(H.notify).toHaveBeenCalledWith(expect.objectContaining({ message: 'Sending avatar and object files to outside AI model providers (other companies) is now allowed in this project.' }));
  });

  it('says since when it is allowed, and withdraws it', async () => {
    H.consent = { consented: true, consentedAt: '2026-11-10T10:00:00Z' };
    renderTab(OLD);
    const row = screen.getByTestId('avatar-media-consent');
    expect(within(row).getByTestId('avatar-media-consent-state').textContent).toMatch(/^Allowed since .*2026/);
    expect(within(row).queryByRole('button', { name: 'Allow' })).toBeNull();
    fireEvent.click(within(row).getByRole('button', { name: 'Withdraw' }));
    await waitFor(() => expect(H.withdraw).toHaveBeenCalledWith('p1'));
    await waitFor(() => expect(H.consentMutate).toHaveBeenCalledWith({ consented: false, consentedAt: null }, { revalidate: false }));
  });

  it('starts from what the requirements said until its own route answers', () => {
    renderTab({ ...OLD, mediaConsent: { consented: true, consentedAt: null } });
    expect(within(screen.getByTestId('avatar-media-consent')).getByTestId('avatar-media-consent-state').textContent).toBe('Allowed');
  });

  it('says so when someone without edit rights tries to change it', async () => {
    H.consent = { consented: false, consentedAt: null };
    H.grant.mockRejectedValue(new ApiError('', 403));
    renderTab(OLD);
    fireEvent.click(within(screen.getByTestId('avatar-media-consent')).getByRole('button', { name: 'Allow' }));
    expect((await screen.findByTestId('avatar-media-consent-error')).textContent).toBeTruthy();
    expect(H.consentMutate).not.toHaveBeenCalled();
  });
});

describe('avatar library words in both languages', () => {
  const catalogue = (locale: string) => JSON.parse(readFileSync(join(__dirname, '..', '..', '..', 'messages', locale, 'avatars.json'), 'utf8'));

  it('has a sentence for every refusal of the library routes', () => {
    for (const locale of ['en', 'it']) {
      const c = catalogue(locale) as { errors: Record<string, string>; library: { errors: Record<string, string> } };
      for (const code of [
        'AVATAR_ALREADY_IMPORTED', 'LIBRARY_ITEM_NOT_USABLE', 'LIBRARY_IMPORT_LIMIT', 'AVATAR_LIMIT_REACHED', 'PLAN_NOT_ENTITLED',
        'STORAGE_QUOTA_EXCEEDED', 'LIBRARY_LIMIT_REACHED', 'LIBRARY_NAME_TAKEN', 'AVATAR_NOT_USABLE',
        'LIBRARY_VARIANT_LABEL_TAKEN', 'LIBRARY_VARIANT_LIMIT_REACHED', 'VARIANT_LABEL_TAKEN', 'VARIANT_LIMIT_REACHED',
      ]) {
        expect(c.library.errors[code], `${locale}: ${code}`).toBeTruthy();
      }
      for (const code of ['AVATAR_FROM_LIBRARY', 'consent_statement_required', 'object_statement_required', 'subject_must_be_adult', 'retention_out_of_range']) {
        expect(c.errors[code], `${locale}: ${code}`).toBeTruthy();
      }
    }
  });

  it('uses no em dash, and calls the workspace by its Italian name', () => {
    for (const locale of ['en', 'it']) {
      const c = catalogue(locale);
      expect(JSON.stringify({ library: c.library, mediaConsent: c.mediaConsent })).not.toMatch(/[—–]/);
    }
    const it = JSON.stringify(catalogue('it').library);
    expect(it).toContain('spazio di lavoro');
    expect(it).not.toMatch(/workspace/i);
  });
});

describe('avatar library sources keep the 12 px floor', () => {
  it('has no font size under 12 px', () => {
    const files = [
      ...readdirSync(__dirname).filter((n) => /\.tsx?$/.test(n) && !n.includes('.test.')).map((n) => join(__dirname, n)),
      join(__dirname, '..', 'AvatarCard.tsx'),
      join(__dirname, '..', 'MediaConsentRow.tsx'),
    ];
    expect(files.length).toBeGreaterThan(5);
    for (const file of files) expect(findSmallFonts(readFileSync(file, 'utf8')), file).toEqual([]);
  });
});
