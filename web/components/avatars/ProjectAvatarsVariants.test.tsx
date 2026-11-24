// @vitest-environment jsdom
/**
 * Avatar variants in a project's Avatars tab (docs/design/avatar-objects.md "Avatar variants"): chips under the
 * original and their actions, "Add a variant" and its rules, both create forms in variant mode, "Make with AI"
 * (estimate, start, the one-time permission, each refusal), the "Being made" list, and a server from before variants.
 * Hooks and mutating calls are mocked like `ProjectAvatarsTab.test.tsx`, so the tests pin the tab's own behaviour.
 */
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { findSmallFonts } from '@/components/editor/__tests__/no-small-fonts';
import type { AvatarBuild } from '@/lib/types/avatar-builds';
import type { AvatarRequirements, ProjectAvatar } from '@/lib/types/avatars';

const H = vi.hoisted(() => ({
  avatars: [] as unknown[],
  files: [] as unknown[],
  voices: [] as unknown[],
  builds: [] as unknown[],
  buildsOff: false,
  mutate: vi.fn(),
  refreshBuilds: vi.fn(),
  create: vi.fn(),
  replace: vi.fn(),
  update: vi.fn(),
  revoke: vi.fn(),
  del: vi.fn(),
  grant: vi.fn(),
  estimate: vi.fn(),
  start: vi.fn(),
  track: vi.fn(),
  trayCancel: vi.fn(),
  notify: vi.fn(),
}));

vi.mock('@/lib/hooks/use-avatars', () => ({
  useProjectAvatars: () => ({ data: H.avatars, error: undefined, isLoading: false, mutate: H.mutate }),
  useAvatarUsage: () => ({ data: { avatarId: 'a1', takeCount: 0, runs: [], picturesUsingReferences: 0 }, error: undefined, isLoading: false }),
  useAvatarRequirements: () => ({ data: undefined, error: undefined }),
  useAvatarMediaConsent: () => ({ data: undefined, mutate: vi.fn() }),
}));
vi.mock('@/lib/hooks/use-avatar-builds', () => ({
  useAvatarBuilds: () => ({ builds: H.builds, off: H.buildsOff, refresh: H.refreshBuilds }),
}));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: H.files, mutate: vi.fn() }) }));
vi.mock('@/lib/hooks/use-voices', () => ({ useProjectVoices: () => ({ data: H.voices }) }));
vi.mock('@/lib/api/avatars', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/api/avatars')>()),
  createAvatar: (...a: unknown[]) => H.create(...a),
  replaceAvatarReferences: (...a: unknown[]) => H.replace(...a),
  updateAvatar: (...a: unknown[]) => H.update(...a),
  revokeAvatarConsent: (...a: unknown[]) => H.revoke(...a),
  deleteAvatar: (...a: unknown[]) => H.del(...a),
  grantAvatarMediaConsent: (...a: unknown[]) => H.grant(...a),
}));
vi.mock('@/lib/api/avatar-variants', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/api/avatar-variants')>()),
  estimateAvatarVariant: (...a: unknown[]) => H.estimate(...a),
  startAvatarVariant: (...a: unknown[]) => H.start(...a),
}));
// The header tray has its own tests (variants/useAvatarBuildTray.test.tsx); here it only records what it was asked.
vi.mock('./variants/useAvatarBuildTray', () => ({
  useAvatarBuildTray: () => ({ track: H.track, cancel: H.trayCancel }),
}));
vi.mock('@mantine/notifications', () => ({ notifications: { show: (...a: unknown[]) => H.notify(...a) } }));
vi.mock('@/components/outputs/CheckedVideo', () => ({ CheckedImage: ({ alt }: { alt: string }) => <span role="img" aria-label={alt} /> }));

import { ProjectAvatarsTab } from './ProjectAvatarsTab';

const CONSENT_EN = 'I confirm that this person agreed.';
const OBJ_EN = 'I confirm that these files show an object and no identifiable person.';
const OBJ_IT = 'Confermo che questi file mostrano un oggetto e nessuna persona identificabile.';

const REQUIREMENTS: AvatarRequirements = {
  statementVersion: 'avatar-consent-2026-12',
  statements: { en: CONSENT_EN, it: 'Confermo che questa persona è d\'accordo.' },
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
  objectStatements: { en: OBJ_EN, it: OBJ_IT },
  objectRoles: ['Front', 'Angle', 'Detail', 'Motion'],
  objectLimits: { maxImages: 8, maxVideos: 3 },
};

const file = (id: string, name: string, mimeType: string) => ({
  id, projectId: 'p1', originalFileName: name, mimeType, sizeBytes: 1000, storageKey: `k/${id}`, category: 'userFiles',
  agentSummary: null, summaryStatus: 'Done', indexingStatus: 'Indexed', uploadedAt: '2026-11-01T00:00:00Z',
});

const reference = (id: string, fileId: string, fileName: string, kind: 'Image' | 'Video' | 'Audio', role: string) => ({
  id, projectFileId: fileId, fileName, kind, role, ordinal: 0, width: 800, height: 800, durationMs: null,
  attachedAt: '2026-11-01T00:00:00Z', state: 'ok',
});

const OBJECT_CONSENT = {
  attestedByUserId: 'u1', attestedAt: '2026-11-01T00:00:00Z', statement: OBJ_EN, statementVersion: 'avatar-object-2026-11',
  locale: 'en', subjectName: '', basis: 'Self', subjectIsAdult: false, documentFileId: null, revokedAt: null, revokedByUserId: null,
};

const CAR = {
  id: 'car', projectId: 'p1', subjectKind: 'Object', coversPictures: true, name: 'Car', description: 'The demo car', selectable: true,
  unselectableReason: null, projectVoiceId: null, projectVoiceName: null, projectVoiceSelectable: null, consent: OBJECT_CONSENT,
  retentionUntil: null, disclosureLabel: 'MetadataOnly', primaryReferenceId: 'r1',
  references: [
    reference('r1', 'f1', 'car-front.png', 'Image', 'Front'),
    reference('r2', 'f2', 'car-side.png', 'Image', 'Angle'),
    reference('r3', 'f3', 'car-spin.mp4', 'Video', 'Motion'),
  ],
  createdAt: '2026-11-01T00:00:00Z', createdByUserId: 'u1', updatedAt: '2026-11-01T00:00:00Z',
  variantOfAvatarId: null, variantLabel: null, variantDescription: null, variantCount: 2,
} as unknown as ProjectAvatar;

const RED = {
  ...CAR, id: 'red', name: 'Car · Red', description: null, primaryReferenceId: 'r10', variantCount: 0,
  references: [reference('r10', 'f10', 'red-front.png', 'Image', 'Front')],
  variantOfAvatarId: 'car', variantLabel: 'Red', variantDescription: 'matte red paint, black rims',
} as unknown as ProjectAvatar;

const BLUE = {
  ...RED, id: 'blue', name: 'Car · Blue', selectable: false, unselectableReason: 'reference_missing',
  variantLabel: 'Blue', variantDescription: 'deep blue paint',
} as unknown as ProjectAvatar;

const ANNA = {
  ...CAR, id: 'anna', subjectKind: 'Person', name: 'Anna', description: null, retentionUntil: '2099-01-01T00:00:00Z',
  disclosureLabel: 'Visible', primaryReferenceId: 'r20', variantCount: 1,
  consent: { ...OBJECT_CONSENT, statement: CONSENT_EN, statementVersion: 'avatar-consent-2026-12', subjectName: 'Anna Rossi', basis: 'WrittenPermission', subjectIsAdult: true },
  references: [reference('r20', 'f20', 'anna.jpg', 'Image', 'Face')],
} as unknown as ProjectAvatar;

const ANNA_HAT = {
  ...ANNA, id: 'hat', name: 'Anna · Hat', variantCount: 0, variantOfAvatarId: 'anna', variantLabel: 'Hat', variantDescription: 'wearing a straw hat',
} as unknown as ProjectAvatar;

const build = (jobId: string, state: string, extra: Partial<AvatarBuild> = {}): AvatarBuild => ({ jobId, kind: 'variant', state, name: 'Car · Green', ...extra });

const tab = (requirements: AvatarRequirements = REQUIREMENTS) => <MantineProvider><ProjectAvatarsTab projectId="p1" requirements={requirements} /></MantineProvider>;
const renderTab = (requirements?: AvatarRequirements) => render(tab(requirements));

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
  window.HTMLElement.prototype.scrollIntoView = () => {};
  // Autosizing text areas listen for font loads, which jsdom does not have.
  Object.defineProperty(document, 'fonts', { configurable: true, value: { addEventListener: vi.fn(), removeEventListener: vi.fn() } });
  window.localStorage.clear();
  H.avatars = [CAR, RED, BLUE];
  H.files = [
    file('f1', 'car-front.png', 'image/png'), file('f2', 'car-side.png', 'image/png'), file('f3', 'car-spin.mp4', 'video/mp4'),
    file('f10', 'red-front.png', 'image/png'), file('f11', 'green-front.png', 'image/png'), file('f20', 'anna.jpg', 'image/jpeg'),
    file('f21', 'anna-hat.jpg', 'image/jpeg'),
  ];
  H.voices = [];
  H.builds = [];
  H.buildsOff = false;
  H.mutate = vi.fn().mockResolvedValue(undefined);
  H.refreshBuilds = vi.fn().mockResolvedValue(undefined);
  H.create.mockReset().mockResolvedValue(RED);
  H.replace.mockReset().mockResolvedValue(RED);
  H.update.mockReset().mockResolvedValue(RED);
  H.revoke.mockReset();
  H.del.mockReset();
  H.grant.mockReset().mockResolvedValue({ consented: true, consentedAt: '2026-11-16T10:00:00Z' });
  H.estimate.mockReset().mockResolvedValue({ billingEnabled: true, credits: 12, affordable: true, balanceCredits: 500, pictures: 2, videos: 0 });
  H.start.mockReset().mockResolvedValue({ jobId: 'j1', state: 'queued' });
  H.track.mockReset();
  H.trayCancel.mockReset().mockResolvedValue(undefined);
  H.notify.mockReset();
});
afterEach(cleanup);

const button = (name: string | RegExp) => screen.getByRole('button', { name }) as HTMLButtonElement;
const ATTEST = 'I have read this statement and I confirm it';

async function chipMenu(id: string): Promise<string[]> {
  fireEvent.click(screen.getByTestId(`variant-chip-${id}`));
  return (await screen.findAllByRole('menuitem')).map((m) => m.textContent ?? '');
}

async function chipAction(id: string, name: string) {
  fireEvent.click(screen.getByTestId(`variant-chip-${id}`));
  fireEvent.click(await screen.findByRole('menuitem', { name }));
}

async function addVariant(originalId: string, choice: string) {
  fireEvent.click(screen.getByTestId(`add-variant-${originalId}`));
  fireEvent.click(await screen.findByRole('menuitem', { name: choice }));
}

describe('Avatars tab: variants under their original', () => {
  it('shows each variant as a labelled chip under its original, not as a card of its own', () => {
    renderTab();
    expect(screen.getByTestId('avatar-card-car')).toBeTruthy();
    expect(screen.queryByTestId('avatar-card-red')).toBeNull();
    expect(screen.queryByTestId('avatar-card-blue')).toBeNull();
    const chips = within(screen.getByTestId('variant-chips-car'));
    expect(chips.getByText('2 variants')).toBeTruthy();
    // By label, each with a dot for its own state.
    expect(chips.getAllByRole('listitem').map((li) => li.textContent)).toEqual(['Blue', 'Red']);
    expect(screen.getByTestId('variant-chip-red').getAttribute('data-status')).toBe('ready');
    expect(screen.getByTestId('variant-chip-blue').getAttribute('data-status')).toBe('referenceMissing');
    expect(button('Red: Ready. Actions for this variant of Car')).toBe(screen.getByTestId('variant-chip-red'));
    expect(button('Blue: File missing. Actions for this variant of Car')).toBeTruthy();
  });

  it('does not count variants in the filter or toward the limit of the project', () => {
    const originals = Array.from({ length: 19 }, (_, i) => ({ ...CAR, id: `o${i}`, name: `Object ${i}`, variantCount: 0 }));
    H.avatars = [...originals, CAR, RED, BLUE, ANNA, ANNA_HAT];
    renderTab();
    expect(screen.getByText('People (1)')).toBeTruthy();
    expect(screen.getByText('Objects (20)')).toBeTruthy();
    // 20 objects and 2 variants: the project is full of objects, by the 20 originals alone.
    expect(button('New object').disabled).toBe(true);
    expect(button('New avatar').disabled).toBe(false);
    fireEvent.click(screen.getByText('Objects (20)'));
    expect(screen.getByTestId('avatars-limit').textContent).toContain('already has 20 objects');
  });

  it('is not at the limit with 19 originals however many variants they have', () => {
    const originals = Array.from({ length: 18 }, (_, i) => ({ ...CAR, id: `o${i}`, name: `Object ${i}`, variantCount: 0 }));
    H.avatars = [...originals, CAR, RED, BLUE];
    renderTab();
    expect(button('New object').disabled).toBe(false);
    expect(screen.queryByTestId('avatars-limit')).toBeNull();
  });

  it('says what sets a variant apart, and why it cannot be used, at the top of its menu', async () => {
    renderTab();
    await chipMenu('blue');
    const summary = screen.getByTestId('variant-summary-blue');
    expect(within(summary).getByText('File missing')).toBeTruthy();
    expect(within(summary).getByText(/deleted, replaced or changed after you added it/)).toBeTruthy();
    expect(within(summary).getByText('deep blue paint')).toBeTruthy();
  });

  it('says what sets a variant apart in a tooltip on its chip, for a pointer and for the keyboard', async () => {
    renderTab();
    fireEvent.mouseEnter(screen.getByTestId('variant-chip-red'));
    expect((await screen.findByRole('tooltip')).textContent).toBe('matte red paint, black rims');
    fireEvent.mouseLeave(screen.getByTestId('variant-chip-red'));
    await waitFor(() => expect(screen.queryByRole('tooltip')).toBeNull());
    fireEvent.focus(screen.getByTestId('variant-chip-blue'));
    expect((await screen.findByRole('tooltip')).textContent).toBe('deep blue paint');
    // A chip is a real button: it can be reached and opened from the keyboard.
    expect(screen.getByTestId('variant-chip-red').tagName).toBe('BUTTON');
    expect(screen.getByTestId('variant-chip-red').getAttribute('aria-haspopup')).toBe('menu');
  });

  it('opens the normal actions of an object for an object variant: no consent to withdraw', async () => {
    renderTab();
    expect(await chipMenu('red')).toEqual(['Details and usage', 'Edit', 'Change files', 'Delete']);
  });

  it('opens the normal actions of a person for a person variant, the consent ones included', async () => {
    H.avatars = [ANNA, ANNA_HAT];
    renderTab();
    expect(await chipMenu('hat')).toEqual(['Details and usage', 'Edit', 'Change files', 'Withdraw consent', 'Delete']);
  });

  it('shows a variant\'s details under its display name, with its label and description', async () => {
    renderTab();
    await chipAction('red', 'Details and usage');
    const dialog = await screen.findByRole('dialog', { name: 'Car · Red' });
    const block = within(dialog).getByTestId('avatar-variant-details');
    expect(within(block).getByText('Variant: Red')).toBeTruthy();
    expect(within(block).getByText('matte red paint, black rims')).toBeTruthy();
    expect(within(dialog).getByText('red-front.png')).toBeTruthy();
  });

  it('edits a variant\'s description, and needs one', async () => {
    renderTab();
    await chipAction('red', 'Edit');
    const dialog = await screen.findByRole('dialog', { name: 'Edit Car · Red' });
    const description = within(dialog).getByTestId('avatar-edit-variant-description') as HTMLTextAreaElement;
    expect(description.value).toBe('matte red paint, black rims');
    expect(within(dialog).getByText(/Lists show it as "Car · Red"/)).toBeTruthy();
    fireEvent.change(description, { target: { value: '  ' } });
    expect(within(dialog).getByText('Say what sets this variant apart.')).toBeTruthy();
    expect(button('Save changes').disabled).toBe(true);
    fireEvent.change(description, { target: { value: 'gloss red paint' } });
    fireEvent.click(button('Save changes'));
    await waitFor(() => expect(H.update).toHaveBeenCalledWith('p1', 'red', { variantDescription: 'gloss red paint' }));
    await waitFor(() => expect(H.mutate).toHaveBeenCalled());
  });

  it('does not offer a variant description on an avatar that is not a variant', async () => {
    renderTab();
    fireEvent.click(button('Actions for Car'));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Edit' }));
    await screen.findByRole('dialog', { name: 'Edit Car' });
    expect(screen.queryByTestId('avatar-edit-variant-description')).toBeNull();
  });

  it('changes a variant\'s files under its display name', async () => {
    renderTab();
    await chipAction('red', 'Change files');
    expect(await screen.findByRole('dialog', { name: 'Change the files of Car · Red' })).toBeTruthy();
  });

  it('deletes a variant under its display name', async () => {
    H.del.mockResolvedValue({ id: 'red', rowDeleted: true, references: { deleted: 0, failed: 0, kept: 1 }, vendorAssets: [], consentDocumentDeleted: false });
    renderTab();
    await chipAction('red', 'Delete');
    const dialog = await screen.findByRole('dialog', { name: 'Delete Car · Red?' });
    expect(within(dialog).queryByTestId('avatar-delete-variants-stay')).toBeNull();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete object' }));
    await waitFor(() => expect(H.del).toHaveBeenCalledWith('p1', 'red'));
  });

  it('says the variants stay when their original is deleted', async () => {
    renderTab();
    fireEvent.click(button('Actions for Car'));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
    expect((await screen.findByTestId('avatar-delete-variants-stay')).textContent).toBe('Its 2 variants are not deleted: they stay in the project on their own.');
  });

  it('shows a variant whose original is gone as a normal card, with its description and no way to add a variant to it', () => {
    H.avatars = [RED];
    renderTab();
    const card = screen.getByTestId('avatar-card-red');
    expect(within(card).getByText('Car · Red')).toBeTruthy();
    expect(within(card).getByTestId('avatar-variant-description').textContent).toBe('Variant: matte red paint, black rims');
    expect(screen.queryByTestId('add-variant-red')).toBeNull();
    expect(button('Actions for Car · Red')).toBeTruthy();
  });
});

describe('Avatars tab: "Add a variant"', () => {
  it('offers both ways on an object', async () => {
    renderTab();
    expect(button('Add a variant of Car')).toBeTruthy();
    fireEvent.click(screen.getByTestId('add-variant-car'));
    expect((await screen.findAllByRole('menuitem')).map((m) => m.textContent)).toEqual(['From other files', 'Make with AI']);
    expect(screen.queryByTestId('variant-ai-note')).toBeNull();
  });

  it('offers only other files on a person, with one quiet line saying why', async () => {
    H.avatars = [ANNA, ANNA_HAT];
    renderTab();
    fireEvent.click(screen.getByTestId('add-variant-anna'));
    expect((await screen.findAllByRole('menuitem')).map((m) => m.textContent)).toEqual(['From other files']);
    expect(screen.getByTestId('variant-ai-note').textContent).toBe('A person\'s variant is made from other files.');
  });

  it('keeps "Make with AI" off for an object that cannot be used, and says why', async () => {
    H.avatars = [{ ...CAR, selectable: false, unselectableReason: 'reference_missing', variantCount: 0 }];
    renderTab();
    fireEvent.click(screen.getByTestId('add-variant-car'));
    const ai = await screen.findByRole('menuitem', { name: 'Make with AI' });
    expect(ai.hasAttribute('disabled') || ai.getAttribute('data-disabled') === 'true').toBe(true);
    expect(screen.getByTestId('variant-ai-note').textContent).toMatch(/only change an object that is ready/);
  });

  it('is hidden on an import from the workspace library', () => {
    H.avatars = [{ ...CAR, fromLibrary: true, libraryItemId: 'lib1', variantCount: 0 }];
    renderTab();
    expect(screen.getByTestId('avatar-card-car')).toBeTruthy();
    expect(screen.queryByTestId('add-variant-car')).toBeNull();
    expect(screen.queryByTestId('variant-limit')).toBeNull();
  });

  it('is hidden when the kind is not in the plan', () => {
    H.avatars = [{ ...CAR, variantCount: 0 }];
    renderTab({ ...REQUIREMENTS, kinds: { person: { enabled: true, entitled: true }, object: { enabled: true, entitled: false } } });
    expect(screen.getByTestId('avatar-card-car')).toBeTruthy();
    expect(screen.queryByTestId('add-variant-car')).toBeNull();
  });

  it('is hidden on a person whose consent was withdrawn', () => {
    H.avatars = [{ ...ANNA, variantCount: 0, selectable: false, unselectableReason: 'consent_revoked', consent: { ...ANNA.consent, revokedAt: '2026-11-10T00:00:00Z' } }];
    renderTab();
    expect(screen.queryByTestId('add-variant-anna')).toBeNull();
  });

  it('says so in a plain line at the limit of ten', () => {
    H.avatars = [{ ...CAR, variantCount: 10 }, RED];
    renderTab();
    expect(screen.queryByTestId('add-variant-car')).toBeNull();
    expect(screen.getByTestId('variant-limit').textContent).toBe('This object already has 10 variants, the most it can have.');
  });

  it('shows no variant UI at all on a server from before variants', () => {
    const old: Record<string, unknown> = { ...CAR };
    for (const field of ['variantOfAvatarId', 'variantLabel', 'variantDescription', 'variantCount']) delete old[field];
    H.avatars = [old];
    H.buildsOff = true;
    H.builds = [build('j1', 'pictures')];
    renderTab();
    expect(screen.getByTestId('avatar-card-car')).toBeTruthy();
    expect(screen.queryByTestId('add-variant-car')).toBeNull();
    expect(screen.queryByTestId('variant-chips-car')).toBeNull();
    expect(screen.queryByTestId('variant-limit')).toBeNull();
    expect(screen.queryByTestId('avatar-builds')).toBeNull();
    expect(H.track).not.toHaveBeenCalled();
  });
});

describe('Avatars tab: a variant from other files (object)', () => {
  async function openForm() {
    renderTab();
    await addVariant('car', 'From other files');
    return screen.findByRole('dialog', { name: 'New variant of Car' });
  }

  it('shows the original fixed at the top and asks for the label and the description instead of a name', async () => {
    const dialog = await openForm();
    expect(within(within(dialog).getByTestId('variant-original')).getByText('Car')).toBeTruthy();
    fireEvent.click(within(await screen.findByTestId('avatar-file-list')).getByLabelText('Use green-front.png'));
    fireEvent.click(button('Next'));
    expect(await screen.findByTestId('variant-label')).toBeTruthy();
    expect(screen.getByTestId('variant-description')).toBeTruthy();
    expect(screen.queryByLabelText(/Object name/)).toBeNull();
    expect(screen.getByTestId('avatar-object-statement').textContent).toBe(OBJ_EN);
  });

  it('creates the variant as an object of its own, grouped under the original', async () => {
    await openForm();
    fireEvent.click(within(await screen.findByTestId('avatar-file-list')).getByLabelText('Use green-front.png'));
    fireEvent.click(button('Next'));
    const submit = () => screen.getByTestId('avatar-submit') as HTMLButtonElement;
    fireEvent.click(screen.getByLabelText(ATTEST));
    expect(submit().disabled).toBe(true);
    fireEvent.change(screen.getByTestId('variant-label'), { target: { value: ' Green ' } });
    expect(screen.getByText(/It shows as "Car · Green"/)).toBeTruthy();
    expect(submit().disabled).toBe(true);
    fireEvent.change(screen.getByTestId('variant-description'), { target: { value: 'racing green paint' } });
    expect(submit().disabled).toBe(false);
    expect(submit().textContent).toBe('Add variant');
    fireEvent.click(submit());
    await waitFor(() => expect(H.create).toHaveBeenCalledTimes(1));
    expect(H.create).toHaveBeenCalledWith('p1', {
      subjectKind: 'Object',
      name: 'Car · Green',
      consentStatement: OBJ_EN,
      consentLocale: 'en',
      references: [{ projectFileId: 'f11', role: 'Front' }],
      variantOfAvatarId: 'car',
      variantLabel: 'Green',
      variantDescription: 'racing green paint',
    });
    await waitFor(() => expect(H.mutate).toHaveBeenCalled());
    expect(H.notify).toHaveBeenCalledWith(expect.objectContaining({ message: 'Variant "Car · Green" added.' }));
  });

  it('refuses a label another variant of the same original already uses', async () => {
    await openForm();
    fireEvent.click(within(await screen.findByTestId('avatar-file-list')).getByLabelText('Use green-front.png'));
    fireEvent.click(button('Next'));
    fireEvent.click(screen.getByLabelText(ATTEST));
    fireEvent.change(screen.getByTestId('variant-description'), { target: { value: 'another red' } });
    fireEvent.change(screen.getByTestId('variant-label'), { target: { value: 'red' } });
    expect(screen.getByText('Another variant of this one already uses that label.')).toBeTruthy();
    expect((screen.getByTestId('avatar-submit') as HTMLButtonElement).disabled).toBe(true);
  });

  it.each([
    ['VARIANT_LIMIT_REACHED', 409, {}, 'This one already has the most variants it can have. Delete a variant first.'],
    ['VARIANT_LABEL_TAKEN', 409, {}, 'Another variant of this one already uses that label. Choose another label.'],
    ['AVATAR_NAME_TAKEN', 409, {}, 'That name is already used in this project. Choose another label.'],
    ['PLAN_NOT_ENTITLED', 402, { feature: 'avatar_objects' }, 'Your current plan does not include objects. Upgrade your plan to make a variant.'],
    ['EMAIL_NOT_VERIFIED', 403, {}, 'Verify your email address before making a variant.'],
    ['object_statement_required', 400, {}, 'The statement did not match. Reload the page and confirm it again.'],
  ])('says a refused variant in plain words: %s', async (code, status, extra, sentence) => {
    H.create.mockRejectedValue(new ApiError('server words', status, [], { code, ...extra }));
    await openForm();
    fireEvent.click(within(await screen.findByTestId('avatar-file-list')).getByLabelText('Use green-front.png'));
    fireEvent.click(button('Next'));
    fireEvent.click(screen.getByLabelText(ATTEST));
    fireEvent.change(screen.getByTestId('variant-label'), { target: { value: 'Green' } });
    fireEvent.change(screen.getByTestId('variant-description'), { target: { value: 'racing green paint' } });
    fireEvent.click(screen.getByTestId('avatar-submit'));
    expect((await screen.findByTestId('avatar-form-error')).textContent).toBe(sentence);
    expect(H.mutate).not.toHaveBeenCalled();
  });
});

describe('Avatars tab: a variant from other files (person)', () => {
  beforeEach(() => { H.avatars = [ANNA, ANNA_HAT]; });

  it('shows the person and the basis from the original, not editable, and asks for the consent again', async () => {
    renderTab();
    await addVariant('anna', 'From other files');
    const dialog = await screen.findByRole('dialog', { name: 'New variant of Anna' });
    expect(within(dialog).getByText(/These are new files of the person, so their consent is confirmed again/)).toBeTruthy();
    fireEvent.click(within(await screen.findByTestId('avatar-file-list')).getByLabelText('Use anna-hat.jpg'));
    fireEvent.click(button('Next'));
    const fixed = await screen.findByTestId('variant-person-fixed');
    expect(fixed.textContent).toContain('Person: Anna Rossi · written permission');
    expect(fixed.textContent).toContain('come from the original and cannot be changed here');
    // Nothing about the person can be typed or chosen again.
    expect(screen.queryByLabelText(/The person's name/)).toBeNull();
    expect(screen.queryByLabelText('This is me')).toBeNull();
    expect(screen.queryByText('Is this person a public figure?')).toBeNull();
    // The adult tick and the statement are asked again.
    expect(button('Next').disabled).toBe(true);
    fireEvent.click(screen.getByLabelText('The person is 18 or older'));
    expect(button('Next').disabled).toBe(true);
    expect(screen.getByTestId('avatar-consent-statement').textContent).toBe(CONSENT_EN);
    fireEvent.click(screen.getByLabelText(ATTEST));
    expect(button('Next').disabled).toBe(false);
  });

  it('creates the variant with the original\'s person, a new consent, a keep-for period and the variant texts', async () => {
    renderTab();
    await addVariant('anna', 'From other files');
    await screen.findByRole('dialog', { name: 'New variant of Anna' });
    fireEvent.click(within(await screen.findByTestId('avatar-file-list')).getByLabelText('Use anna-hat.jpg'));
    fireEvent.click(button('Next'));
    fireEvent.click(await screen.findByLabelText('The person is 18 or older'));
    fireEvent.click(screen.getByLabelText(ATTEST));
    fireEvent.click(button('Next'));
    // The last step asks for the label and the description instead of a name, and for the keep-for period again.
    expect(await screen.findByLabelText(/Keep for \(days\)/)).toBeTruthy();
    expect(screen.queryByLabelText(/Avatar name/)).toBeNull();
    const submit = () => screen.getByTestId('avatar-submit') as HTMLButtonElement;
    expect(submit().disabled).toBe(true);
    fireEvent.change(screen.getByTestId('variant-label'), { target: { value: 'Glasses' } });
    fireEvent.change(screen.getByTestId('variant-description'), { target: { value: 'wearing round glasses' } });
    expect(submit().disabled).toBe(false);
    fireEvent.click(submit());
    await waitFor(() => expect(H.create).toHaveBeenCalledTimes(1));
    expect(H.create).toHaveBeenCalledWith('p1', {
      name: 'Anna · Glasses',
      consentStatement: CONSENT_EN,
      consentLocale: 'en',
      consentSubjectName: 'Anna Rossi',
      consentBasis: 'WrittenPermission',
      subjectIsAdult: true,
      consentDocumentFileId: null,
      references: [{ projectFileId: 'f21', role: 'Face' }],
      retentionDays: 365,
      projectVoiceId: null,
      disclosureLabel: 'Visible',
      disclosesOtherwise: false,
      variantOfAvatarId: 'anna',
      variantLabel: 'Glasses',
      variantDescription: 'wearing round glasses',
    });
    expect(H.notify).toHaveBeenCalledWith(expect.objectContaining({ message: 'Variant "Anna · Glasses" added.' }));
  });
});

describe('Avatars tab: "Make with AI"', () => {
  async function openAi() {
    renderTab();
    await addVariant('car', 'Make with AI');
    return screen.findByTestId('variant-ai-form');
  }
  const fill = (instruction = 'Paint it racing green.', label = 'Green') => {
    fireEvent.change(screen.getByTestId('variant-instruction'), { target: { value: instruction } });
    fireEvent.change(screen.getByTestId('variant-label'), { target: { value: label } });
    fireEvent.click(screen.getByLabelText(ATTEST));
  };
  const start = () => screen.getByTestId('variant-ai-start') as HTMLButtonElement;

  it('opens on the original with every picture ticked, the videos off and the object statement to confirm', async () => {
    const form = await openAi();
    expect(screen.getByRole('dialog', { name: 'Make a variant of Car with AI' })).toBeTruthy();
    expect(within(within(form).getByTestId('variant-original')).getByText('Car')).toBeTruthy();
    const pictures = within(screen.getByTestId('variant-pictures'));
    expect((pictures.getByLabelText('Change car-front.png') as HTMLInputElement).checked).toBe(true);
    expect((pictures.getByLabelText('Change car-side.png') as HTMLInputElement).checked).toBe(true);
    // Only pictures are offered to tick: the video has its own switch.
    expect(pictures.queryByLabelText('Change car-spin.mp4')).toBeNull();
    const videos = screen.getByTestId('variant-include-videos') as HTMLInputElement;
    expect(videos.checked).toBe(false);
    expect(screen.getByLabelText('Also remake its video')).toBe(videos);
    expect(screen.getByText(/made again from the changed picture, so the movement can differ from the original. This costs more/)).toBeTruthy();
    expect(screen.getByTestId('variant-object-statement').textContent).toBe(OBJ_EN);
    expect((screen.getByTestId('variant-instruction') as HTMLTextAreaElement).placeholder).toMatch(/^For example: .+ Or: .+/);
    expect(start().disabled).toBe(true);
  });

  it('shows no video switch for an object without videos', async () => {
    H.avatars = [{ ...CAR, references: CAR.references.filter((r) => r.kind === 'Image'), variantCount: 0 }];
    await openAi();
    expect(screen.queryByTestId('variant-include-videos')).toBeNull();
  });

  it('prefills the description from the instruction until the person writes their own', async () => {
    await openAi();
    fireEvent.change(screen.getByTestId('variant-instruction'), { target: { value: 'Paint it racing green.' } });
    const description = screen.getByTestId('variant-description') as HTMLTextAreaElement;
    expect(description.value).toBe('Paint it racing green.');
    fireEvent.change(description, { target: { value: 'racing green paint, tan seats' } });
    fireEvent.change(screen.getByTestId('variant-instruction'), { target: { value: 'Paint it racing green with tan seats.' } });
    expect((screen.getByTestId('variant-description') as HTMLTextAreaElement).value).toBe('racing green paint, tan seats');
  });

  it('asks the server for the price of the choice and shows it exactly as returned', async () => {
    await openAi();
    await waitFor(() => expect(H.estimate).toHaveBeenCalledWith('p1', 'car', { referenceIds: ['r1', 'r2'], includeVideos: false }), { timeout: 3000 });
    const estimate = await screen.findByText('About 12 credits');
    expect(within(screen.getByTestId('variant-estimate')).getByText('For 2 pictures.')).toBeTruthy();
    expect(estimate).toBeTruthy();
    expect(screen.queryByTestId('variant-estimate-short')).toBeNull();

    // A new choice is priced again: one picture less, and the video made again.
    H.estimate.mockResolvedValue({ billingEnabled: true, credits: 40.5, creditsHigh: 55, affordable: false, balanceCredits: 30, pictures: 1, videos: 1 });
    fireEvent.click(screen.getByLabelText('Change car-side.png'));
    fireEvent.click(screen.getByTestId('variant-include-videos'));
    await waitFor(() => expect(H.estimate).toHaveBeenLastCalledWith('p1', 'car', { referenceIds: ['r1', 'r3'], includeVideos: true }), { timeout: 3000 });
    expect(await screen.findByText('About 40.5 credits, 55 at most')).toBeTruthy();
    expect(screen.getByText('For 1 picture and 1 video.')).toBeTruthy();
    expect(screen.getByTestId('variant-estimate-short').textContent).toBe('You have 30 credits, which is not enough.');
  });

  it('shows no price line with billing off', async () => {
    H.estimate.mockResolvedValue({ billingEnabled: false, affordable: true, pictures: 2, videos: 0 });
    await openAi();
    await waitFor(() => expect(H.estimate).toHaveBeenCalled(), { timeout: 3000 });
    await waitFor(() => expect(screen.queryByText('Checking the price...')).toBeNull());
    expect(screen.queryByTestId('variant-estimate')).toBeNull();
  });

  it.each([
    ['VARIANT_PROVIDER_MISSING', 'This workspace has no AI model set up that can change pictures, so there is no price yet.'],
    ['VARIANT_VIDEO_MODEL_UNPRICED', 'The video model this workspace uses cannot be priced in credits, so the videos cannot be made again. Leave the videos out, or ask an admin to set up a priced video model.'],
    ['some_new_reason', 'The price cannot be worked out for this choice right now.'],
  ])('says in words why there is no price: %s', async (unavailableReason, sentence) => {
    H.estimate.mockResolvedValue({ billingEnabled: true, affordable: true, pictures: 2, videos: 0, unavailableReason });
    await openAi();
    await waitFor(() => expect(screen.getByTestId('variant-estimate').textContent).toBe(sentence), { timeout: 3000 });
  });

  it('says so when the price cannot be asked, without blocking the start', async () => {
    H.estimate.mockRejectedValue(new ApiError('boom', 500, []));
    await openAi();
    await waitFor(() => expect(screen.getByTestId('variant-estimate').textContent).toBe('The price could not be worked out right now.'), { timeout: 3000 });
    fill();
    expect(start().disabled).toBe(false);
  });

  it('needs an instruction, a label, a picture and the ticked statement', async () => {
    await openAi();
    fireEvent.change(screen.getByTestId('variant-instruction'), { target: { value: 'Paint it racing green.' } });
    expect(start().disabled).toBe(true);
    fireEvent.change(screen.getByTestId('variant-label'), { target: { value: 'Red' } });
    fireEvent.click(screen.getByLabelText(ATTEST));
    // "Red" is already a variant of this car.
    expect(screen.getByText('Another variant of this one already uses that label.')).toBeTruthy();
    expect(start().disabled).toBe(true);
    fireEvent.change(screen.getByTestId('variant-label'), { target: { value: 'Green' } });
    expect(start().disabled).toBe(false);
    fireEvent.click(screen.getByLabelText('Change car-front.png'));
    fireEvent.click(screen.getByLabelText('Change car-side.png'));
    expect(screen.getByTestId('variant-no-pictures').textContent).toBe('Choose at least one picture to change.');
    expect(start().disabled).toBe(true);
    fireEvent.click(screen.getByLabelText('Change car-side.png'));
    expect(start().disabled).toBe(false);
    // Changing the language of the statement clears the tick.
    fireEvent.click(screen.getByText('Italiano'));
    await waitFor(() => expect(screen.getByTestId('variant-object-statement').textContent).toBe(OBJ_IT));
    expect(start().disabled).toBe(true);
  });

  it('starts the build with exactly what was chosen, lists it under Being made and closes', async () => {
    await openAi();
    fill();
    fireEvent.click(screen.getByLabelText('Change car-side.png'));
    fireEvent.click(screen.getByTestId('variant-include-videos'));
    fireEvent.click(start());
    await waitFor(() => expect(H.start).toHaveBeenCalledTimes(1));
    expect(H.start).toHaveBeenCalledWith('p1', 'car', {
      instruction: 'Paint it racing green.',
      variantLabel: 'Green',
      variantDescription: 'Paint it racing green.',
      referenceIds: ['r1', 'r3'],
      includeVideos: true,
      consentStatement: OBJ_EN,
      consentLocale: 'en',
    });
    await waitFor(() => expect(H.refreshBuilds).toHaveBeenCalled());
    await waitFor(() => expect(screen.queryByTestId('variant-ai-form')).toBeNull());
    expect(H.notify).toHaveBeenCalledWith(expect.objectContaining({ message: 'Making "Car · Green". You can keep working: it is listed under Being made.' }));
    // The app never allows sending media per call: only the recorded permission counts.
    expect(H.start.mock.calls[0][2]).not.toHaveProperty('allowMediaEgress');
    expect(H.grant).not.toHaveBeenCalled();
  });

  it('asks once for the permission to send media when the project has none, records it and starts again', async () => {
    H.start
      .mockRejectedValueOnce(new ApiError('Consent required', 409, [], { code: 'CONSENT_REQUIRED' }))
      .mockResolvedValueOnce({ jobId: 'j2', state: 'queued' });
    await openAi();
    fill();
    fireEvent.click(start());
    const consent = within(await screen.findByRole('dialog', { name: 'Send avatar and object files to outside AI model providers (other companies)?' }));
    expect(consent.getByText(/the files you added for it are sent to the outside AI model providers this project uses/)).toBeTruthy();
    expect(consent.getByText(/withdraw this at any time in the project's Avatars tab/)).toBeTruthy();
    // Nothing is recorded until the person ticks the box and agrees.
    expect(H.grant).not.toHaveBeenCalled();
    expect(screen.queryByTestId('variant-ai-error')).toBeNull();
    expect((consent.getByTestId('media-consent-confirm') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(consent.getByTestId('media-consent-check'));
    fireEvent.click(consent.getByTestId('media-consent-confirm'));
    await waitFor(() => expect(H.grant).toHaveBeenCalledWith('p1'));
    await waitFor(() => expect(H.start).toHaveBeenCalledTimes(2));
    expect(H.start.mock.calls[1]).toEqual(H.start.mock.calls[0]);
    await waitFor(() => expect(screen.queryByTestId('variant-ai-form')).toBeNull());
  });

  it('starts nothing when the person does not give the permission', async () => {
    H.start.mockRejectedValue(new ApiError('Consent required', 409, [], { code: 'CONSENT_REQUIRED' }));
    await openAi();
    fill();
    fireEvent.click(start());
    fireEvent.click(await screen.findByTestId('media-consent-cancel'));
    await waitFor(() => expect(screen.queryByTestId('media-consent-dialog')).toBeNull());
    expect(H.grant).not.toHaveBeenCalled();
    expect(H.start).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId('variant-ai-form')).toBeTruthy();
  });

  it('says so when the permission could not be saved', async () => {
    H.start.mockRejectedValue(new ApiError('Consent required', 409, [], { code: 'CONSENT_REQUIRED' }));
    H.grant.mockRejectedValue(new ApiError('', 403, []));
    await openAi();
    fill();
    fireEvent.click(start());
    fireEvent.click(await screen.findByTestId('media-consent-check'));
    fireEvent.click(screen.getByTestId('media-consent-confirm'));
    expect(await screen.findByTestId('media-consent-error')).toBeTruthy();
    expect(H.start).toHaveBeenCalledTimes(1);
  });

  it.each([
    ['VARIANT_LIMIT_REACHED', 409, {}, 'This one already has the most variants it can have. Delete a variant first.'],
    ['VARIANT_PROVIDER_MISSING', 409, {}, /no AI model set up that can change pictures.+Make it from other files, or ask an admin/],
    ['VARIANT_MODEL_UNSUPPORTED', 409, {}, /cannot do this\. Make the variant from other files, or ask an admin to choose another model/],
    ['VARIANT_VIDEO_MODEL_MISSING', 409, {}, /no AI model set up that can turn a picture into a video, so the videos cannot be made again/],
    ['VARIANT_VIDEO_MODEL_UNPRICED', 409, {}, /cannot be priced in credits, so the videos cannot be made again/],
    ['PLAN_NOT_ENTITLED', 402, { feature: 'avatar_objects' }, 'Your current plan does not include objects. Upgrade your plan to make a variant.'],
    ['PLAN_NOT_ENTITLED', 402, { feature: 'video_generation' }, /does not include making videos with AI, so the videos cannot be made again/],
    ['INSUFFICIENT_CREDITS', 402, { message: 'You need about 40 credits and have 12.' }, 'You need about 40 credits and have 12.'],
    ['RUN_RATE_LIMITED', 429, {}, 'You have started a lot of AI work in the last hour. Try again in a few minutes.'],
    ['AVATAR_NOT_USABLE', 409, {}, 'The original cannot be used right now, so no variant can be made from it. Fix it first.'],
    ['object_statement_required', 400, {}, 'The statement did not match. Reload the page and confirm it again.'],
    ['EMAIL_NOT_VERIFIED', 403, {}, 'Verify your email address before making a variant.'],
    ['AVATAR_NAME_TAKEN', 409, {}, 'That name is already used in this project. Choose another label.'],
    ['VARIANT_LABEL_TAKEN', 409, {}, 'Another variant of this one already uses that label. Choose another label.'],
  ])('says a refused start in plain words: %s', async (code, status, extra, sentence) => {
    const body = { code, ...extra };
    H.start.mockRejectedValue(new ApiError((body as { message?: string }).message ?? 'server words', status, [], body));
    await openAi();
    fill();
    fireEvent.click(start());
    const error = await screen.findByTestId('variant-ai-error');
    if (typeof sentence === 'string') expect(error.textContent).toBe(sentence);
    else expect(error.textContent).toMatch(sentence);
    expect(H.refreshBuilds).not.toHaveBeenCalled();
    expect(screen.getByTestId('variant-ai-form')).toBeTruthy();
  });

  it('says a rate limit without a code, and a validation refusal with each of its points', async () => {
    H.start.mockRejectedValueOnce(new ApiError('Too Many Requests', 429, []));
    await openAi();
    fill();
    fireEvent.click(start());
    expect((await screen.findByTestId('variant-ai-error')).textContent).toBe('You have started a lot of AI work in the last hour. Try again in a few minutes.');
    H.start.mockRejectedValueOnce(new ApiError('The request is not valid.', 400, ['The label is too long.', 'Choose a picture.'], { message: 'The request is not valid.', errors: ['The label is too long.', 'Choose a picture.'] }));
    fireEvent.click(start());
    await waitFor(() => expect(screen.getByTestId('variant-ai-error').textContent).toBe('The request is not valid. The label is too long. Choose a picture.'));
  });
});

describe('Avatars tab: "Being made"', () => {
  it('shows nothing while nothing is being made', () => {
    renderTab();
    expect(screen.queryByTestId('avatar-builds')).toBeNull();
  });

  it('says where each build is in plain words', () => {
    H.builds = [
      build('q', 'queued', { name: 'Car · Queued' }),
      build('p', 'pictures', { progress: { done: 2, total: 5 } }),
      build('p0', 'pictures', { name: 'Car · No count' }),
      build('v', 'videos', { progress: { done: 0, total: 1 } }),
      build('c', 'creating'),
      build('x', 'creating', { kind: 'extraction', name: 'Bottle' }),
      build('f', 'finding', { kind: 'extraction', name: 'Bottle 2' }),
      build('u', 'cutting', { kind: 'extraction', name: 'Bottle 3' }),
      build('bad', 'failed', { message: 'The picture service did not answer.' }),
      build('bad2', 'failed', { failureCode: 'VARIANT_FAILED', message: 'raw server words' }),
      build('bad3', 'failed'),
    ];
    renderTab();
    const panel = screen.getByTestId('avatar-builds');
    expect(within(panel).getByText('Being made')).toBeTruthy();
    const stage = (id: string) => within(screen.getByTestId(`avatar-build-${id}`)).getByTestId('avatar-build-stage').textContent;
    expect(stage('q')).toBe('Waiting to start');
    expect(stage('p')).toBe('Changing pictures: 2 of 5');
    expect(stage('p0')).toBe('Changing the pictures');
    expect(stage('v')).toBe('Making the videos again: 0 of 1');
    expect(stage('c')).toBe('Creating the variant');
    expect(stage('x')).toBe('Creating the object');
    expect(stage('f')).toBe('Finding the object in the video');
    expect(stage('u')).toBe('Cutting out the pictures');
    expect(stage('bad')).toBe('Could not be made');
    const failure = (id: string) => within(screen.getByTestId(`avatar-build-${id}`)).getByTestId('avatar-build-failure').textContent;
    expect(failure('bad')).toBe('The picture service did not answer.');
    expect(failure('bad2')).toBe('None of the pictures could be changed, so no variant was made. Nothing was charged.');
    expect(failure('bad3')).toBe('Something went wrong while making it.');
    // Each row names what is being made and of which kind.
    expect(within(screen.getByTestId('avatar-build-p')).getByText('Car · Green')).toBeTruthy();
    expect(within(screen.getByTestId('avatar-build-p')).getByText('Variant')).toBeTruthy();
    expect(within(screen.getByTestId('avatar-build-x')).getByText('From a video')).toBeTruthy();
    // A counted step shows how far it is.
    expect(within(screen.getByTestId('avatar-build-p')).getByRole('progressbar')).toBeTruthy();
  });

  it('lists the running builds in the header tray too, and not the finished ones', () => {
    const running = build('p', 'pictures');
    H.builds = [running, build('bad', 'failed'), build('old', 'ready')];
    renderTab();
    expect(H.track).toHaveBeenCalledWith(running);
    expect(H.track.mock.calls.every(([b]) => (b as AvatarBuild).jobId === 'p')).toBe(true);
    // A build that was already finished when the page opened is not announced again.
    expect(screen.queryByTestId('avatar-build-old')).toBeNull();
    expect(H.mutate).not.toHaveBeenCalled();
  });

  it('stops a running build and reads the list again', async () => {
    const running = build('p', 'pictures');
    H.builds = [running, build('bad', 'failed')];
    renderTab();
    expect(within(screen.getByTestId('avatar-build-bad')).queryByRole('button', { name: /Cancel/ })).toBeNull();
    fireEvent.click(button('Cancel making Car · Green'));
    await waitFor(() => expect(H.trayCancel).toHaveBeenCalledWith(running));
    await waitFor(() => expect(H.refreshBuilds).toHaveBeenCalled());
  });

  it('says so when a build could not be stopped', async () => {
    H.trayCancel.mockRejectedValue(new ApiError('', 500, []));
    H.builds = [build('p', 'pictures')];
    renderTab();
    fireEvent.click(button('Cancel making Car · Green'));
    await waitFor(() => expect(H.notify).toHaveBeenCalledWith(expect.objectContaining({ color: 'red' })));
    expect(H.refreshBuilds).toHaveBeenCalled();
  });

  it('reads the avatars again and says the variant is ready when a build turns ready', async () => {
    H.builds = [build('p', 'creating')];
    const { rerender } = renderTab();
    expect(H.mutate).not.toHaveBeenCalled();
    H.builds = [build('p', 'ready', { avatarId: 'green' })];
    rerender(tab());
    await waitFor(() => expect(H.mutate).toHaveBeenCalledTimes(1));
    expect(H.notify).toHaveBeenCalledWith(expect.objectContaining({ color: 'green', message: 'The variant "Car · Green" is ready.' }));
    expect(within(screen.getByTestId('avatar-build-p')).getByTestId('avatar-build-stage').textContent).toBe('Ready');
    // The same reading again announces nothing more.
    rerender(tab());
    expect(H.mutate).toHaveBeenCalledTimes(1);
    expect(H.notify).toHaveBeenCalledTimes(1);
  });

  it('says an object pulled out of a video is ready in an object\'s words', async () => {
    H.builds = [build('x', 'creating', { kind: 'extraction', name: 'Bottle' })];
    const { rerender } = renderTab();
    H.builds = [build('x', 'ready', { kind: 'extraction', name: 'Bottle' })];
    rerender(tab());
    await waitFor(() => expect(H.notify).toHaveBeenCalledWith(expect.objectContaining({ message: 'The object "Bottle" is ready.' })));
  });

  it('keeps a failed build, with its reason, until it is dismissed, and remembers the dismissal', () => {
    H.builds = [build('bad', 'failed', { message: 'The picture service did not answer.' })];
    const first = renderTab();
    fireEvent.click(button('Dismiss Car · Green'));
    expect(screen.queryByTestId('avatar-builds')).toBeNull();
    first.unmount();
    renderTab();
    expect(screen.queryByTestId('avatar-builds')).toBeNull();
  });

  it('says what happened and what to do for each failure code that has no server words', () => {
    const en = JSON.parse(readFileSync(join(__dirname, '../../messages/en/avatars.json'), 'utf8')) as { builds: { failures: Record<string, string> } };
    const codes = ['PROJECT_ACCESS_LOST', 'VISION_PROVIDER_REQUIRED', 'CONSENT_REQUIRED', 'PROJECT_CONTENT_QUARANTINED'];
    H.builds = codes.map((code) => build(code, 'failed', { failureCode: code, message: 'raw server words' }));
    renderTab();
    for (const code of codes) {
      expect(en.builds.failures[code]).toBeTruthy();
      expect(within(screen.getByTestId(`avatar-build-${code}`)).getByText(en.builds.failures[code])).toBeTruthy();
    }
    expect(screen.queryByText('raw server words')).toBeNull();
  });

  it('shows a stopped build only when this page saw it running', () => {
    H.builds = [build('p', 'pictures'), build('old', 'cancelled', { name: 'Car · Old' })];
    const { rerender } = renderTab();
    expect(screen.queryByTestId('avatar-build-old')).toBeNull();
    H.builds = [build('p', 'cancelled'), build('old', 'cancelled', { name: 'Car · Old' })];
    rerender(tab());
    expect(within(screen.getByTestId('avatar-build-p')).getByTestId('avatar-build-stage').textContent).toBe('Stopped');
    expect(screen.queryByTestId('avatar-build-old')).toBeNull();
    expect(H.mutate).not.toHaveBeenCalled();
  });
});

describe('variant sources keep the 12 px floor', () => {
  it('has no font size under 12 px', () => {
    const files = [
      'MediaConsentDialog.tsx', 'avatar-names.tsx', 'AvatarCard.tsx',
      ...['AddVariantMenu.tsx', 'AvatarBuildsPanel.tsx', 'VariantAiModal.tsx', 'VariantChips.tsx', 'VariantFields.tsx'].map((n) => join('variants', n)),
    ];
    for (const name of files) expect(findSmallFonts(readFileSync(join(__dirname, name), 'utf8')), name).toEqual([]);
  });
});
