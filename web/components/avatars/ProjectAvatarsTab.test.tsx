// @vitest-environment jsdom
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { findSmallFonts } from '@/components/editor/__tests__/no-small-fonts';
import type { AvatarRequirements, ProjectAvatar } from '@/lib/types/avatars';

const H = vi.hoisted(() => ({
  avatars: [] as unknown[],
  usage: { avatarId: 'a1', takeCount: 0, runs: [], picturesUsingReferences: 2 } as unknown,
  files: [] as unknown[],
  voices: [] as unknown[],
  mutate: vi.fn(),
  create: vi.fn(),
  replace: vi.fn(),
  update: vi.fn(),
  revoke: vi.fn(),
  del: vi.fn(),
  notify: vi.fn(),
}));

vi.mock('@/lib/hooks/use-avatars', () => ({
  useProjectAvatars: () => ({ data: H.avatars, error: undefined, isLoading: false, mutate: H.mutate }),
  useAvatarUsage: () => ({ data: H.usage, error: undefined, isLoading: false }),
  useAvatarRequirements: () => ({ data: undefined, error: undefined }),
  // The permission row has its own tests (library/ProjectAvatarsLibrary.test.tsx); here it only reads the requirements.
  useAvatarMediaConsent: () => ({ data: undefined, mutate: vi.fn() }),
}));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: H.files, mutate: vi.fn() }) }));
// Builds in progress have their own tests (ProjectAvatarsVariants.test.tsx); here the project has none.
vi.mock('@/lib/hooks/use-avatar-builds', () => ({ useAvatarBuilds: () => ({ builds: [], off: false, refresh: async () => undefined }) }));
vi.mock('@/lib/hooks/use-voices', () => ({ useProjectVoices: () => ({ data: H.voices }) }));
vi.mock('@/lib/api/avatars', () => ({
  createAvatar: (...a: unknown[]) => H.create(...a),
  replaceAvatarReferences: (...a: unknown[]) => H.replace(...a),
  updateAvatar: (...a: unknown[]) => H.update(...a),
  revokeAvatarConsent: (...a: unknown[]) => H.revoke(...a),
  deleteAvatar: (...a: unknown[]) => H.del(...a),
}));
vi.mock('@mantine/notifications', () => ({ notifications: { show: (...a: unknown[]) => H.notify(...a) } }));
vi.mock('@/components/outputs/CheckedVideo', () => ({ CheckedImage: ({ alt }: { alt: string }) => <span role="img" aria-label={alt} /> }));

import { ProjectAvatarsTab } from './ProjectAvatarsTab';

const EN = 'I confirm that I am the person shown and heard in these files, or that I have that person\'s written permission.';
const IT = 'Confermo di essere la persona che si vede e si sente in questi file.';

const REQUIREMENTS: AvatarRequirements = {
  statementVersion: 'avatar-consent-2026-11',
  statements: { en: EN, it: IT },
  locales: ['en', 'it'],
  limits: {
    maxAvatarsPerProject: 20, maxImages: 8, maxVideos: 3, maxAudio: 3, maxImageBytes: 30 * 1024 * 1024,
    minImageSidePx: 256, maxImageSidePx: 5760, minImageAspect: 0.4, maxImageAspect: 2.5, maxVideoBytes: 50 * 1024 * 1024,
    minVideoMs: 2000, maxVideoMs: 15000, minVideoFps: 23.976, maxVideoFps: 60, videoCodecs: ['h264'],
    maxAudioBytes: 15 * 1024 * 1024, minAudioMs: 2000, maxAudioMs: 15000, minRetentionDays: 1, maxRetentionDays: 730,
    defaultRetentionDays: 365, revokedGraceDays: 30, maxNameLength: 200, maxDescriptionLength: 2000, maxSubjectNameLength: 200,
  },
  mimeTypes: { image: ['image/jpeg', 'image/png', 'image/webp'], video: ['video/mp4', 'video/quicktime'], audio: ['audio/wav', 'audio/mpeg'] },
  roles: ['Face', 'HalfBody', 'FullBody', 'Motion', 'Speech'],
  bases: ['Self', 'WrittenPermission'],
  disclosureLabels: ['Visible', 'MetadataOnly'],
  entitled: true,
  moderationEnabled: true,
  csamScanningEnabled: false,
};

const file = (id: string, name: string, mimeType: string, extra: Record<string, unknown> = {}) => ({
  id, projectId: 'p1', originalFileName: name, mimeType, sizeBytes: 1000, storageKey: `k/${id}`, category: 'userFiles',
  agentSummary: null, summaryStatus: 'Done', indexingStatus: 'Indexed', uploadedAt: '2026-11-01T00:00:00Z', ...extra,
});

const AVATAR: ProjectAvatar = {
  id: 'a1', projectId: 'p1', name: 'Anna', description: null, selectable: true, unselectableReason: null,
  projectVoiceId: null, projectVoiceName: null, projectVoiceSelectable: null,
  consent: {
    attestedByUserId: 'u1', attestedAt: '2026-11-01T00:00:00Z', statement: EN, statementVersion: 'avatar-consent-2026-11',
    locale: 'en', subjectName: 'Anna Rossi', basis: 'Self', subjectIsAdult: true, documentFileId: null, revokedAt: null, revokedByUserId: null,
  },
  retentionUntil: '2099-01-01T00:00:00Z', disclosureLabel: 'Visible', primaryReferenceId: 'r1',
  references: [
    { id: 'r1', projectFileId: 'f1', fileName: 'anna.jpg', kind: 'Image', role: 'Face', ordinal: 0, width: 800, height: 800, durationMs: null, attachedAt: '2026-11-01T00:00:00Z', state: 'ok' },
  ],
  createdAt: '2026-11-01T00:00:00Z', createdByUserId: 'u1', updatedAt: '2026-11-01T00:00:00Z',
};

function renderTab(requirements: AvatarRequirements = REQUIREMENTS) {
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
  H.files = [
    file('f1', 'anna.jpg', 'image/jpeg'),
    file('f2', 'anna-talking.mp4', 'video/mp4'),
    file('f3', 'notes.txt', 'text/plain'),
    file('f4', 'synced.png', 'image/png', { sourceKind: 'github' }),
  ];
  H.voices = [];
  H.mutate = vi.fn().mockResolvedValue(undefined);
  H.create.mockReset().mockResolvedValue(AVATAR);
  H.replace.mockReset().mockResolvedValue(AVATAR);
  H.update.mockReset().mockResolvedValue(AVATAR);
  H.revoke.mockReset().mockResolvedValue({ ...AVATAR, selectable: false });
  H.del.mockReset();
  H.notify.mockReset();
});
afterEach(cleanup);

const button = (name: string | RegExp) => screen.getByRole('button', { name }) as HTMLButtonElement;

/** Walks the create flow to the consent step with one picture chosen. */
async function openConsentStep() {
  fireEvent.click(button('New avatar'));
  const list = await screen.findByTestId('avatar-file-list');
  fireEvent.click(within(list).getByLabelText('Use anna.jpg'));
  fireEvent.click(button('Next'));
  return screen.findByTestId('avatar-consent-statement');
}

describe('ProjectAvatarsTab: list', () => {
  it('shows an upgrade hint instead of the create button when the plan has no avatars', () => {
    renderTab({ ...REQUIREMENTS, entitled: false });
    expect(screen.getByTestId('avatars-upgrade').textContent).toContain('does not include avatars');
    expect(screen.getByTestId('avatars-upgrade').textContent).toContain('Pro, Team and Enterprise');
    expect(screen.getByTestId('avatars-pro-badge').textContent).toBe('Pro');
    expect(screen.queryByRole('button', { name: 'New avatar' })).toBeNull();
  });

  it('shows the empty state', () => {
    renderTab();
    expect(screen.getByText('No avatars yet')).toBeTruthy();
  });

  it('shows the status badge and the plain reason when an avatar cannot be used', () => {
    H.avatars = [AVATAR, { ...AVATAR, id: 'a2', name: 'Marco', selectable: false, unselectableReason: 'reference_missing' }];
    renderTab();
    const ready = screen.getByTestId('avatar-card-a1');
    expect(within(ready).getByTestId('avatar-status').textContent).toBe('Ready');
    const broken = screen.getByTestId('avatar-card-a2');
    expect(within(broken).getByTestId('avatar-status').textContent).toBe('File missing');
    expect(within(broken).getByText(/deleted, replaced or changed after you added it/)).toBeTruthy();
    expect(within(broken).getByRole('button', { name: 'Choose the files again' })).toBeTruthy();
  });
});

describe('ProjectAvatarsTab: create flow', () => {
  it('keeps the three steps on one line on a desktop: short labels in the header, the hint in the body', async () => {
    renderTab();
    fireEvent.click(button('New avatar'));
    await screen.findByTestId('avatar-file-list');
    const steps = document.querySelector('.mantine-Stepper-steps');
    expect(steps?.hasAttribute('data-wrap')).toBe(false);
    expect(document.querySelectorAll('.mantine-Stepper-stepDescription')).toHaveLength(0);
    expect(screen.getByTestId('avatar-step-hint').textContent).toBe('Pictures, videos, recordings');
    // The modal is 'xl' (about 780 px of steps): three labels of at most 14 characters fit beside their icons in en and it.
    for (const locale of ['en', 'it']) {
      const catalogue = JSON.parse(readFileSync(join(__dirname, '..', '..', 'messages', locale, 'avatars.json'), 'utf8'));
      const { files, person, settings } = catalogue.form.steps as Record<string, string>;
      for (const label of [files, person, settings]) expect(label.length, `${locale}: ${label}`).toBeLessThanOrEqual(14);
    }
  });

  it('offers only accepted files that no connected source keeps in step', async () => {
    renderTab();
    fireEvent.click(button('New avatar'));
    const list = await screen.findByTestId('avatar-file-list');
    expect(within(list).getByLabelText('Use anna.jpg')).toBeTruthy();
    expect(within(list).getByLabelText('Use anna-talking.mp4')).toBeTruthy();
    expect(within(list).queryByLabelText('Use notes.txt')).toBeNull();
    expect(within(list).queryByLabelText('Use synced.png')).toBeNull();
  });

  it('needs a picture before the next step', async () => {
    renderTab();
    fireEvent.click(button('New avatar'));
    const list = await screen.findByTestId('avatar-file-list');
    expect(button('Next').disabled).toBe(true);
    fireEvent.click(within(list).getByLabelText('Use anna-talking.mp4'));
    expect(screen.getByText('Choose at least one picture of the person.')).toBeTruthy();
    expect(button('Next').disabled).toBe(true);
    fireEvent.click(within(list).getByLabelText('Use anna.jpg'));
    expect(button('Next').disabled).toBe(false);
  });

  it('shows the server statement verbatim, in the chosen language', async () => {
    renderTab();
    const statement = await openConsentStep();
    expect(statement.textContent).toBe(EN);
    fireEvent.click(screen.getByText('Italiano'));
    await waitFor(() => expect(screen.getByTestId('avatar-consent-statement').textContent).toBe(IT));
  });

  it('keeps Next disabled until every consent field is complete', async () => {
    renderTab();
    await openConsentStep();
    const next = () => button('Next');
    expect(next().disabled).toBe(true);
    fireEvent.change(screen.getByLabelText(/The person's name/), { target: { value: 'Anna Rossi' } });
    fireEvent.click(screen.getByLabelText('This is me'));
    fireEvent.click(screen.getByLabelText('No'));
    expect(next().disabled).toBe(true);
    fireEvent.click(screen.getByLabelText('The person is 18 or older'));
    expect(next().disabled).toBe(true);
    fireEvent.click(screen.getByLabelText('I have read this statement and I confirm it'));
    expect(next().disabled).toBe(false);
    // Unticking the adult checkbox closes the way again.
    fireEvent.click(screen.getByLabelText('The person is 18 or older'));
    expect(next().disabled).toBe(true);
  });

  it('refuses a public figure unless the basis is Self', async () => {
    renderTab();
    await openConsentStep();
    fireEvent.change(screen.getByLabelText(/The person's name/), { target: { value: 'A Star' } });
    fireEvent.click(screen.getByLabelText('Someone else, and I have their written permission'));
    fireEvent.click(screen.getByLabelText('Yes'));
    fireEvent.click(screen.getByLabelText('The person is 18 or older'));
    fireEvent.click(screen.getByLabelText('I have read this statement and I confirm it'));
    expect(screen.getByText('An avatar of a public figure can only be made by that person, for themselves.')).toBeTruthy();
    expect(button('Next').disabled).toBe(true);
  });

  it('sends the exact statement of the chosen language and the consent fields', async () => {
    renderTab();
    await openConsentStep();
    fireEvent.click(screen.getByText('Italiano'));
    fireEvent.change(screen.getByLabelText(/The person's name/), { target: { value: 'Anna Rossi' } });
    fireEvent.click(screen.getByLabelText('This is me'));
    fireEvent.click(screen.getByLabelText('No'));
    fireEvent.click(screen.getByLabelText('The person is 18 or older'));
    fireEvent.click(screen.getByLabelText('I have read this statement and I confirm it'));
    fireEvent.click(button('Next'));

    const submit = await screen.findByTestId('avatar-submit');
    expect((submit as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByLabelText(/Avatar name/), { target: { value: 'Anna' } });
    expect((submit as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(submit);

    await waitFor(() => expect(H.create).toHaveBeenCalledTimes(1));
    const [projectId, body] = H.create.mock.calls[0];
    expect(projectId).toBe('p1');
    expect(body).toMatchObject({
      name: 'Anna', consentStatement: IT, consentLocale: 'it', consentSubjectName: 'Anna Rossi', consentBasis: 'Self',
      subjectIsAdult: true, subjectIsPublicFigure: false, retentionDays: 365, disclosureLabel: 'Visible', disclosesOtherwise: false,
      references: [{ projectFileId: 'f1', role: 'Face' }],
    });
  });

  it('names the file a refusal is about, in plain words', async () => {
    H.create.mockRejectedValue(new ApiError('"anna.jpg" is still being checked.', 409, [], {
      code: 'AVATAR_REFERENCE_PENDING', message: '"anna.jpg" is still being checked.', fileId: 'f1',
    }));
    renderTab();
    await openConsentStep();
    fireEvent.change(screen.getByLabelText(/The person's name/), { target: { value: 'Anna Rossi' } });
    fireEvent.click(screen.getByLabelText('This is me'));
    fireEvent.click(screen.getByLabelText('No'));
    fireEvent.click(screen.getByLabelText('The person is 18 or older'));
    fireEvent.click(screen.getByLabelText('I have read this statement and I confirm it'));
    fireEvent.click(button('Next'));
    fireEvent.change(await screen.findByLabelText(/Avatar name/), { target: { value: 'Anna' } });
    fireEvent.click(screen.getByTestId('avatar-submit'));
    expect((await screen.findByTestId('avatar-form-error')).textContent).toBe('"anna.jpg" is still being checked. Try again in a minute.');
  });
});

describe('ProjectAvatarsTab: revoke and delete', () => {
  const openAction = async (label: string) => {
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Anna' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: label }));
  };

  it('withdraws consent only after the confirmation', async () => {
    H.avatars = [AVATAR];
    renderTab();
    await openAction('Withdraw consent');
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/deleted automatically after 30 days/)).toBeTruthy();
    expect(H.revoke).not.toHaveBeenCalled();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Withdraw consent' }));
    await waitFor(() => expect(H.revoke).toHaveBeenCalledWith('p1', 'a1'));
    expect(H.mutate).toHaveBeenCalled();
  });

  it('cancelling the withdrawal changes nothing', async () => {
    H.avatars = [AVATAR];
    renderTab();
    await openAction('Withdraw consent');
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Cancel' }));
    expect(H.revoke).not.toHaveBeenCalled();
  });

  it('lists what will be removed, then shows the report of what was removed', async () => {
    H.avatars = [AVATAR];
    H.del.mockResolvedValue({
      id: 'a1', rowDeleted: true, references: { deleted: 1, failed: 0, kept: 0 },
      vendorAssets: [{ provider: 'MiniMax', outcome: 'vendor_refused' }], consentDocumentDeleted: false,
    });
    renderTab();
    await openAction('Delete');
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByTestId('avatar-delete-list').textContent).toContain('anna.jpg');
    expect(within(dialog).getByText(/2 pictures in the editor use one of its photos/)).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete avatar' }));
    const report = await screen.findByTestId('avatar-delete-report');
    expect(H.del).toHaveBeenCalledWith('p1', 'a1');
    expect(report.textContent).toContain('1 file deleted.');
    expect(report.textContent).toContain('Copy at MiniMax: the service refused; we have recorded it and will follow up.');
  });
});

const OBJ_EN = 'I confirm that these files show an object, a product or a place and no identifiable person.';
const OBJ_IT = 'Confermo che questi file mostrano un oggetto, un prodotto o un luogo e nessuna persona identificabile.';

/** A server with objects: both kinds switched on and in the plan. */
const BOTH: AvatarRequirements = {
  ...REQUIREMENTS,
  statementVersion: 'avatar-consent-2026-12',
  kinds: { person: { enabled: true, entitled: true }, object: { enabled: true, entitled: true } },
  objectStatementVersion: 'avatar-object-2026-11',
  objectStatements: { en: OBJ_EN, it: OBJ_IT },
  objectRoles: ['Front', 'Angle', 'Detail', 'Motion'],
  objectLimits: { maxImages: 8, maxVideos: 3 },
  personCheck: true,
  mediaConsent: { consented: false, consentedAt: null },
};
const kinds = (person: [boolean, boolean], object: [boolean, boolean]): AvatarRequirements => ({
  ...BOTH,
  entitled: person[1],
  kinds: { person: { enabled: person[0], entitled: person[1] }, object: { enabled: object[0], entitled: object[1] } },
});

const PERSON: ProjectAvatar = { ...AVATAR, subjectKind: 'Person', coversPictures: true };
const OBJECT: ProjectAvatar = {
  ...AVATAR, id: 'o1', name: 'Red shoe', subjectKind: 'Object', coversPictures: true, retentionUntil: null,
  disclosureLabel: 'MetadataOnly', primaryReferenceId: 'or1',
  consent: { ...AVATAR.consent, statement: OBJ_EN, statementVersion: 'avatar-object-2026-11', subjectName: '', subjectIsAdult: false },
  references: [
    { id: 'or1', projectFileId: 'f5', fileName: 'shoe.png', kind: 'Image', role: 'Front', ordinal: 0, width: 800, height: 800, durationMs: null, attachedAt: '2026-11-01T00:00:00Z', state: 'ok' },
  ],
};

const objectFiles = () => [
  ...H.files,
  file('f5', 'shoe.png', 'image/png'),
  file('f6', 'shoe-side.png', 'image/png'),
  // A platform-made file (an extracted frame) may be an object's file; a recording never is.
  file('f7', 'shoe-frame.png', 'image/png', { category: 'outputFiles', sourceKind: 'output_copy' }),
  file('f8', 'voice.wav', 'audio/wav'),
];

const radio = (name: string | RegExp) => screen.getByRole('radio', { name }) as HTMLInputElement;
const roleOf = (fileName: string) => (screen.getByRole('combobox', { name: `What ${fileName} shows` }) as HTMLInputElement).value;

/** Walks the object flow to its second step with one picture chosen. */
async function openObjectStatement(pick = 'shoe.png') {
  fireEvent.click(button('New object'));
  const list = await screen.findByTestId('avatar-file-list');
  fireEvent.click(within(list).getByLabelText(`Use ${pick}`));
  fireEvent.click(button('Next'));
  return screen.findByTestId('avatar-object-statement');
}

describe('ProjectAvatarsTab: people and objects', () => {
  it('shows a People / Objects switch, both actions and one kind at a time when both kinds are on', () => {
    H.avatars = [PERSON, OBJECT];
    renderTab(BOTH);
    expect(screen.getByTestId('avatars-intro').textContent).toContain('people and objects');
    expect(button('New avatar').disabled).toBe(false);
    expect(button('New object').disabled).toBe(false);
    expect(radio('People (1)').checked).toBe(true);
    expect(screen.getByTestId('avatar-card-a1')).toBeTruthy();
    expect(screen.queryByTestId('avatar-card-o1')).toBeNull();

    fireEvent.click(radio('Objects (1)'));
    expect(screen.queryByTestId('avatar-card-a1')).toBeNull();
    const card = screen.getByTestId('avatar-card-o1');
    expect(within(card).getByTestId('avatar-kind').textContent).toBe('Object');
    expect(within(card).getByTestId('avatar-status').textContent).toBe('Ready');
    expect(within(card).getByText('1 picture, 0 videos')).toBeTruthy();
    // Nothing a person's card says: no person, no consent basis, no voice, no keep-until date.
    expect(card.textContent).not.toMatch(/Person:|this is me|voice|Kept until|recording/i);
  });

  it('badges a person card as a person', () => {
    H.avatars = [PERSON];
    renderTab(BOTH);
    expect(within(screen.getByTestId('avatar-card-a1')).getByTestId('avatar-kind').textContent).toBe('Person');
  });

  it('opens on the objects when the project has only objects', () => {
    H.avatars = [OBJECT];
    renderTab(BOTH);
    expect(radio('Objects (1)').checked).toBe(true);
    expect(screen.getByTestId('avatar-card-o1')).toBeTruthy();
  });

  it('offers an object Edit, Change files, Details and Delete, and never Withdraw consent', async () => {
    H.avatars = [OBJECT];
    renderTab(BOTH);
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Red shoe' }));
    const items = (await screen.findAllByRole('menuitem')).map((m) => m.textContent);
    expect(items).toEqual(['Details and usage', 'Edit', 'Change files', 'Delete']);
  });

  it('shows only objects, with no switch and no person action, when only objects are switched on', () => {
    H.avatars = [OBJECT];
    renderTab(kinds([false, false], [true, true]));
    expect(screen.queryByTestId('avatars-kind-filter')).toBeNull();
    expect(screen.queryByRole('button', { name: 'New avatar' })).toBeNull();
    expect(button('New object').disabled).toBe(false);
    expect(screen.getByTestId('avatars-intro').textContent).toMatch(/^An object is a product/);
    expect(screen.getByTestId('avatar-card-o1')).toBeTruthy();
  });

  it('shows only people when objects are switched off', () => {
    H.avatars = [PERSON];
    renderTab(kinds([true, true], [false, false]));
    expect(screen.queryByTestId('avatars-kind-filter')).toBeNull();
    expect(screen.queryByRole('button', { name: 'New object' })).toBeNull();
    expect(button('New avatar')).toBeTruthy();
    expect(screen.getByTestId('avatars-intro').textContent).toMatch(/^An avatar is a likeness of a real person/);
  });

  it('reads a server from before objects as people only: no kinds, no subject kind', () => {
    H.avatars = [AVATAR];
    renderTab(REQUIREMENTS);
    expect(screen.queryByTestId('avatars-kind-filter')).toBeNull();
    expect(screen.queryByRole('button', { name: 'New object' })).toBeNull();
    const card = screen.getByTestId('avatar-card-a1');
    expect(within(card).getByTestId('avatar-kind').textContent).toBe('Person');
    expect(within(card).getByText(/Person: Anna Rossi/)).toBeTruthy();
    // It says nothing about pictures: the old server does not tell.
    expect(within(card).queryByTestId('avatar-not-for-pictures')).toBeNull();
  });

  it('says objects need Creator and up when the plan has people but no objects', () => {
    renderTab(kinds([true, true], [true, false]));
    expect(button('New avatar')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'New object' })).toBeNull();
    expect(screen.queryByTestId('avatars-objects-upgrade')).toBeNull();
    fireEvent.click(radio('Objects (0)'));
    expect(screen.getByTestId('avatars-objects-upgrade').textContent).toContain('Creator, Pro, Team and Enterprise');
    expect(screen.getByTestId('avatars-objects-plan-badge').textContent).toBe('Creator');
    expect(screen.queryByTestId('avatars-upgrade')).toBeNull();
  });

  it('says people need Pro and up while objects can still be added (a Creator plan)', () => {
    renderTab(kinds([true, false], [true, true]));
    expect(screen.getByTestId('avatars-upgrade').textContent).toContain('Pro, Team and Enterprise');
    expect(screen.getByTestId('avatars-pro-badge').textContent).toBe('Pro');
    expect(screen.queryByRole('button', { name: 'New avatar' })).toBeNull();
    expect(button('New object').disabled).toBe(false);
    fireEvent.click(radio('Objects (0)'));
    expect(screen.queryByTestId('avatars-upgrade')).toBeNull();
    expect(screen.queryByTestId('avatars-objects-upgrade')).toBeNull();
    expect(screen.getByText('No objects yet')).toBeTruthy();
  });

  it('counts the limit of each kind apart', () => {
    H.avatars = Array.from({ length: 20 }, (_, i) => ({ ...OBJECT, id: `o${i}`, name: `Thing ${i}` }));
    renderTab(BOTH);
    expect(button('New object').disabled).toBe(true);
    expect(button('New avatar').disabled).toBe(false);
    expect(screen.getByTestId('avatars-limit').textContent).toContain('already has 20 objects');
  });

  it('hints quietly that a person is not confirmed for pictures yet, and confirming again is the way to fix it', async () => {
    H.avatars = [{ ...PERSON, coversPictures: false }];
    renderTab(BOTH);
    const card = screen.getByTestId('avatar-card-a1');
    expect(within(card).getByTestId('avatar-not-for-pictures').textContent).toMatch(/^Not yet confirmed for pictures/);
    expect(within(card).getByTestId('avatar-status').textContent).toBe('Ready');
    fireEvent.click(within(card).getByRole('button', { name: 'Confirm consent again' }));
    expect(await screen.findByText('Change the files of Anna')).toBeTruthy();
    // The form shows the CURRENT statement of the server, whatever the avatar was confirmed under.
    await screen.findByTestId('avatar-file-list');
  });

  it('says nothing about pictures for a person who is confirmed for them', () => {
    H.avatars = [PERSON];
    renderTab(BOTH);
    expect(screen.queryByTestId('avatar-not-for-pictures')).toBeNull();
  });

  it('says why an object cannot be used in an object\'s words', () => {
    H.avatars = [{ ...OBJECT, selectable: false, unselectableReason: 'consent_missing' }];
    renderTab(BOTH);
    const card = screen.getByTestId('avatar-card-o1');
    expect(within(card).getByTestId('avatar-status').textContent).toBe('Not confirmed');
    expect(within(card).getByText(/The statement for this object is not on record/)).toBeTruthy();
    expect(within(card).getByRole('button', { name: 'Confirm again' })).toBeTruthy();
  });
});

describe('ProjectAvatarsTab: object form', () => {
  beforeEach(() => { H.files = objectFiles(); });

  it('offers pictures and videos, a platform-made file included, and never a recording or a synced file', async () => {
    renderTab(BOTH);
    fireEvent.click(button('New object'));
    const list = await screen.findByTestId('avatar-file-list');
    for (const name of ['shoe.png', 'shoe-side.png', 'shoe-frame.png', 'anna-talking.mp4']) {
      expect(within(list).getByLabelText(`Use ${name}`)).toBeTruthy();
    }
    for (const name of ['voice.wav', 'notes.txt', 'synced.png']) {
      expect(within(list).queryByLabelText(`Use ${name}`)).toBeNull();
    }
    expect(screen.getByText('The object on its own: no one as the main subject.')).toBeTruthy();
    expect(screen.getByTestId('avatar-counts').textContent).toBe('Chosen: 0 of 8 pictures, 0 of 3 videos.');
  });

  it('gives each file an object role: front first, then another angle, a video moving', async () => {
    renderTab(BOTH);
    fireEvent.click(button('New object'));
    const list = await screen.findByTestId('avatar-file-list');
    fireEvent.click(within(list).getByLabelText('Use shoe.png'));
    fireEvent.click(within(list).getByLabelText('Use shoe-side.png'));
    fireEvent.click(within(list).getByLabelText('Use anna-talking.mp4'));
    expect(roleOf('shoe.png')).toBe('Front view');
    expect(roleOf('shoe-side.png')).toBe('Another angle');
    expect(roleOf('anna-talking.mp4')).toBe('Moving');
    fireEvent.click(screen.getByRole('combobox', { name: 'What shoe-side.png shows' }));
    const options = (await screen.findAllByRole('option')).map((o) => o.textContent);
    expect(options).toEqual(['Front view', 'Another angle', 'Close-up detail']);
  });

  it('needs a picture of the object before the next step', async () => {
    renderTab(BOTH);
    fireEvent.click(button('New object'));
    const list = await screen.findByTestId('avatar-file-list');
    fireEvent.click(within(list).getByLabelText('Use anna-talking.mp4'));
    expect(screen.getByText('Choose at least one picture of the object.')).toBeTruthy();
    expect(button('Next').disabled).toBe(true);
    fireEvent.click(within(list).getByLabelText('Use shoe.png'));
    expect(button('Next').disabled).toBe(false);
  });

  it('shows the object statement verbatim in the chosen language, and the note about the automatic check', async () => {
    renderTab(BOTH);
    const statement = await openObjectStatement();
    expect(statement.textContent).toBe(OBJ_EN);
    expect(screen.getByTestId('object-person-check-note').textContent).toBe('Each picture is checked automatically. A picture whose main subject is a person with a visible face is refused; people in the background are fine.');
    fireEvent.click(screen.getByLabelText('I have read this statement and I confirm it'));
    fireEvent.click(screen.getByText('Italiano'));
    await waitFor(() => expect(screen.getByTestId('avatar-object-statement').textContent).toBe(OBJ_IT));
    // Changing the language clears the tick: the person confirms the text they read.
    expect((screen.getByLabelText('I have read this statement and I confirm it') as HTMLInputElement).checked).toBe(false);
    // None of the person's questions.
    expect(screen.queryByLabelText(/The person's name/)).toBeNull();
    expect(screen.queryByLabelText('The person is 18 or older')).toBeNull();
    expect(screen.queryByLabelText(/Keep for/)).toBeNull();
  });

  it('leaves the note out when pictures are not checked automatically', async () => {
    renderTab({ ...BOTH, personCheck: false });
    await openObjectStatement();
    expect(screen.queryByTestId('object-person-check-note')).toBeNull();
  });

  it('sends the object statement unchanged and none of a person\'s fields', async () => {
    H.create.mockResolvedValue(OBJECT);
    renderTab(BOTH);
    await openObjectStatement();
    const submit = screen.getByTestId('avatar-submit') as HTMLButtonElement;
    expect(submit.disabled).toBe(true);
    fireEvent.change(screen.getByLabelText(/Object name/), { target: { value: ' Red shoe ' } });
    expect(submit.disabled).toBe(true);
    fireEvent.click(screen.getByText('Italiano'));
    fireEvent.click(screen.getByLabelText('I have read this statement and I confirm it'));
    expect(submit.disabled).toBe(false);
    fireEvent.click(submit);

    await waitFor(() => expect(H.create).toHaveBeenCalledTimes(1));
    const [projectId, body] = H.create.mock.calls[0];
    expect(projectId).toBe('p1');
    // Exactly these: a person's field sent with an object is refused (`field_not_for_objects`).
    expect(body).toEqual({
      subjectKind: 'Object', name: 'Red shoe', consentStatement: OBJ_IT, consentLocale: 'it',
      references: [{ projectFileId: 'f5', role: 'Front' }],
    });
    await waitFor(() => expect(H.notify).toHaveBeenCalledWith(expect.objectContaining({ message: 'Object "Red shoe" added.' })));
    expect(H.mutate).toHaveBeenCalled();
  });

  it('sends the description only when one was typed', async () => {
    renderTab(BOTH);
    await openObjectStatement();
    fireEvent.change(screen.getByLabelText(/Object name/), { target: { value: 'Red shoe' } });
    fireEvent.change(screen.getByLabelText(/Description/), { target: { value: 'A red running shoe' } });
    fireEvent.click(screen.getByLabelText('I have read this statement and I confirm it'));
    fireEvent.click(screen.getByTestId('avatar-submit'));
    await waitFor(() => expect(H.create).toHaveBeenCalledTimes(1));
    expect(H.create.mock.calls[0][1]).toMatchObject({ subjectKind: 'Object', description: 'A red running shoe' });
  });

  const refusals: [string, number, Record<string, unknown>, string][] = [
    ['object_statement_required', 400, {}, 'The statement did not match. Reload the page and confirm it again.'],
    ['field_not_for_objects', 400, {}, 'Something in this request applies only to people, so nothing was saved. Reload the page and try again.'],
    ['object_reference_shows_person', 400, { fileId: 'f5' }, '"shoe.png" seems to show a person as its main subject, so it cannot be used for an object. Use a picture that shows the object on its own, or one where people are only in the background.'],
    ['OBJECT_PERSON_CHECK_UNAVAILABLE', 503, {}, 'The pictures could not be checked right now, so nothing was saved. Try again in a few minutes.'],
    ['PLAN_NOT_ENTITLED', 402, { feature: 'avatar_objects' }, 'Your current plan does not include objects. Upgrade your plan to add one.'],
    ['AVATAR_LIMIT_REACHED', 409, {}, 'This project already holds the most objects it can. Delete one first.'],
    ['AVATAR_REFERENCE_PENDING', 409, { fileId: 'f5' }, '"shoe.png" is still being checked. Try again in a minute.'],
  ];
  it.each(refusals)('says a refused object in plain words: %s', async (code, status, extra, sentence) => {
    H.create.mockRejectedValue(new ApiError('server words', status, [], { code, message: 'server words', ...extra }));
    renderTab(BOTH);
    await openObjectStatement();
    fireEvent.change(screen.getByLabelText(/Object name/), { target: { value: 'Red shoe' } });
    fireEvent.click(screen.getByLabelText('I have read this statement and I confirm it'));
    fireEvent.click(screen.getByTestId('avatar-submit'));
    expect((await screen.findByTestId('avatar-form-error')).textContent).toBe(sentence);
    expect(H.mutate).not.toHaveBeenCalled();
  });

  it('changing an object\'s files confirms the statement again and sends no keep-for period', async () => {
    H.avatars = [OBJECT];
    renderTab(BOTH);
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Red shoe' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Change files' }));
    const list = await screen.findByTestId('avatar-file-list');
    // The form starts from the object's current files.
    await waitFor(() => expect((within(list).getByLabelText('Use shoe.png') as HTMLInputElement).checked).toBe(true));
    fireEvent.click(within(list).getByLabelText('Use shoe-side.png'));
    fireEvent.click(button('Next'));
    expect((await screen.findByTestId('avatar-object-statement')).textContent).toBe(OBJ_EN);
    expect(screen.queryByLabelText(/Object name/)).toBeNull();
    expect(screen.queryByLabelText(/Keep for/)).toBeNull();
    const submit = screen.getByTestId('avatar-submit') as HTMLButtonElement;
    expect(submit.disabled).toBe(true);
    fireEvent.click(screen.getByLabelText('I have read this statement and I confirm it'));
    fireEvent.click(submit);
    await waitFor(() => expect(H.replace).toHaveBeenCalledTimes(1));
    expect(H.replace.mock.calls[0]).toEqual(['p1', 'o1', {
      consentStatement: OBJ_EN, consentLocale: 'en',
      references: [{ projectFileId: 'f5', role: 'Front' }, { projectFileId: 'f6', role: 'Angle' }],
    }]);
    expect(H.create).not.toHaveBeenCalled();
  });
});

describe('ProjectAvatarsTab: object dialogs', () => {
  const openObjectAction = async (label: string) => {
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Red shoe' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: label }));
    return screen.findByRole('dialog');
  };

  it('edits an object\'s name and description only', async () => {
    H.avatars = [OBJECT];
    H.voices = [{ id: 'v1', name: 'Warm voice', selectable: true }];
    renderTab(BOTH);
    const dialog = await openObjectAction('Edit');
    expect(within(dialog).queryByText(/Voice/)).toBeNull();
    expect(within(dialog).queryByText(/AI label/)).toBeNull();
    fireEvent.change(within(dialog).getByLabelText(/Object name/), { target: { value: 'Blue shoe' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(H.update).toHaveBeenCalledWith('p1', 'o1', { name: 'Blue shoe' }));
  });

  it('says an object\'s files are kept, then shows the kept files in the report', async () => {
    H.avatars = [OBJECT];
    H.del.mockResolvedValue({
      id: 'o1', rowDeleted: true, references: { deleted: 0, failed: 0, kept: 1 },
      vendorAssets: [], consentDocumentDeleted: false, stills: { deleted: 2, failed: 0 },
    });
    renderTab(BOTH);
    const dialog = await openObjectAction('Delete');
    expect(within(dialog).getByTestId('object-files-kept').textContent).toBe('Its file stays in the project\'s files: shoe.png');
    // Nothing about deleting its files, a signed permission or editor pictures losing a photo.
    expect(within(dialog).getByTestId('avatar-delete-list').textContent).not.toMatch(/shoe\.png|signed permission/);
    expect(dialog.textContent).not.toMatch(/placeholder/);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete object' }));
    const report = await screen.findByTestId('avatar-delete-report');
    expect(H.del).toHaveBeenCalledWith('p1', 'o1');
    expect(report.textContent).toContain('1 file was kept in the project\'s files.');
    expect(report.textContent).toContain('2 pictures made for clips were deleted.');
    expect(report.textContent).not.toMatch(/0 files deleted|another avatar/);
  });

  it('shows an object\'s confirmation, with no person and no keep-until date', async () => {
    H.avatars = [OBJECT];
    renderTab(BOTH);
    const dialog = await openObjectAction('Details and usage');
    const confirmation = within(dialog).getByTestId('object-confirmation');
    expect(confirmation.textContent).toContain('Statement avatar-object-2026-11, in English');
    expect(dialog.textContent).not.toMatch(/Person:|Kept until|AI label/);
    expect(within(dialog).getByText('Front view')).toBeTruthy();
  });

  it('says there is no consent to withdraw when the server answers that the avatar is not a person', async () => {
    H.avatars = [AVATAR];
    H.revoke.mockRejectedValue(new ApiError('not a person', 409, [], { code: 'AVATAR_NOT_A_PERSON', message: 'not a person' }));
    renderTab(REQUIREMENTS);
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Anna' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Withdraw consent' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Withdraw consent' }));
    expect(await within(dialog).findByText('This is an object, so there is no consent to withdraw. Delete it if you no longer want it.')).toBeTruthy();
  });
});

describe('avatar object words in both languages', () => {
  const catalogue = (locale: string) => JSON.parse(readFileSync(join(__dirname, '..', '..', 'messages', locale, 'avatars.json'), 'utf8'));

  it('has a sentence for every refusal that came with objects', () => {
    for (const locale of ['en', 'it']) {
      const { errors } = catalogue(locale) as { errors: Record<string, string> };
      for (const code of [
        'object_statement_required', 'field_not_for_objects', 'object_reference_shows_person',
        'OBJECT_PERSON_CHECK_UNAVAILABLE', 'AVATAR_NOT_A_PERSON', 'AVATAR_CONSENT_NO_PICTURES',
      ]) {
        expect(errors[code], `${locale}: ${code}`).toBeTruthy();
      }
      expect(errors.object_reference_shows_person).toContain('{file}');
    }
  });

  it('keeps the two object steps short enough for one line, and uses no em dash', () => {
    for (const locale of ['en', 'it']) {
      const c = catalogue(locale);
      for (const label of [c.form.steps.files, c.form.steps.objectName, c.form.steps.objectConfirm]) {
        expect(label.length, `${locale}: ${label}`).toBeLessThanOrEqual(14);
      }
      expect(JSON.stringify(c)).not.toContain('—');
    }
  });
});

describe('avatar sources keep the 12 px floor', () => {
  it('has no font size under 12 px', () => {
    for (const name of ['ProjectAvatarsTab.tsx', 'AvatarFormModal.tsx', 'ObjectFormModal.tsx', 'ReferenceParts.tsx', 'AvatarDialogs.tsx', 'avatar-words.ts']) {
      expect(findSmallFonts(readFileSync(join(__dirname, name), 'utf8'))).toEqual([]);
    }
  });
});
