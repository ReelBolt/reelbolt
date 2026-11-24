// @vitest-environment jsdom
/** "Generate clip": the form, the price, the one-time consent, every refusal, and the job that outlives the dialog. */
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { SubjectClipFailedError, type SubjectClipEstimate, type SubjectClipStatus } from '@/lib/editing/subjects/subject-clip-api';
import { testAvatar, testObject } from '@/test/avatars';
import { registerEnhanceHost } from '../../enhance/enhance-host';
import { SubjectClipDialog } from '../SubjectClipDialog';
import { cancelSubjectClipJob, dismissSubjectClipJob, resetSubjectClipStore, startSubjectClipJob, subjectClipJobs, type SubjectClipApi } from '../subject-clip-store';
import { subjectClipActivityId } from '../useSubjectClipJob';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();
// Mantine's autosize textarea listens for font loads, which jsdom does not have.
Object.defineProperty(document, 'fonts', { configurable: true, value: { addEventListener: vi.fn(), removeEventListener: vi.fn() } });

const h = vi.hoisted(() => ({
  providers: [] as unknown[],
  files: [] as unknown[],
  role: 'Member' as string | null,
  trackJob: vi.fn(),
  cancelTracked: vi.fn(),
  allJobs: [] as unknown[],
  notify: vi.fn(),
  mutate: vi.fn(),
}));
vi.mock('@/lib/hooks/use-inference-providers', () => ({ useSelectableProviders: (_c: string, enabled: boolean) => ({ data: enabled ? h.providers : undefined }) }));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: h.files }) }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: h.role ? { orgRole: h.role } : null }) }));
vi.mock('@/lib/activity/ActivityProvider', () => ({ useActivity: () => ({ trackJob: h.trackJob, cancel: h.cancelTracked, allJobs: h.allJobs }) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: (...a: unknown[]) => h.notify(...a) } }));
vi.mock('swr', async (orig) => ({ ...(await orig<typeof import('swr')>()), mutate: (...a: unknown[]) => h.mutate(...a) }));

const anna = testAvatar();
const bea = testAvatar({ id: 'bea', name: 'Bea' });
const bottle = testObject();
const AVATARS = [anna, bea, bottle];
const H3 = { id: 'cap-h3', name: 'MiniMax H3', capability: 'VideoGeneration', model: 'MiniMax-H3', isPlatformManaged: true, kind: 'MiniMax', providerName: 'ReelBolt Inference', costIndex: null };
const TEXT_ONLY = { id: 'cap-t2v', name: 'Text only', capability: 'VideoGeneration', model: 'vendor/t2v', isPlatformManaged: true, kind: 'FalAi', providerName: 'ReelBolt Inference', costIndex: null };
const ESTIMATE: SubjectClipEstimate = { billingEnabled: true, credits: 12, creditsHigh: 12, balanceCredits: 100, affordable: true, path: 'References', stillIncluded: false, unavailableReason: null };
const refusal = (status: number, body: Record<string, unknown>, errors: string[] = []) => new ApiError(String(body.message ?? 'refused'), status, errors, body);

/** A fake API whose run stays "running" until the test settles it. */
function mockApi(estimate: SubjectClipEstimate = ESTIMATE) {
  let settle: { resolve: (s: SubjectClipStatus) => void; reject: (e: unknown) => void } | null = null;
  const api = {
    estimate: vi.fn().mockResolvedValue(estimate),
    start: vi.fn().mockResolvedValue({ executionId: 'e1', state: 'queued', credits: 12 }),
    wait: vi.fn<SubjectClipApi['wait']>(() => new Promise<SubjectClipStatus>((resolve, reject) => { settle = { resolve, reject }; })),
    cancel: vi.fn().mockResolvedValue({}),
    grantConsent: vi.fn().mockResolvedValue({ consented: true, consentedAt: '2026-11-16T10:00:00Z' }),
    getJob: vi.fn(),
    refreshPool: vi.fn(),
  } satisfies SubjectClipApi;
  return { api, finish: (s: SubjectClipStatus) => settle?.resolve(s), fail: (e: unknown) => settle?.reject(e) };
}

function mount(api: SubjectClipApi, props: Partial<React.ComponentProps<typeof SubjectClipDialog>> = {}) {
  const onClose = vi.fn();
  render(
    <MantineProvider>
      <SubjectClipDialog projectId="p1" opened onClose={onClose} avatars={AVATARS} api={api} canvas={{ width: 1920, height: 1080 }} {...props} />
    </MantineProvider>,
  );
  return { onClose };
}

const tick = (id: string) => fireEvent.click(screen.getByTestId(`subject-option-${id}`));
const type = (text: string) => fireEvent.change(screen.getByTestId('subject-clip-prompt'), { target: { value: text } });
/** A Mantine Select's visible input, whatever role this Mantine version gives it. */
const select = (name: string) => screen.queryByRole('combobox', { name }) ?? screen.getByRole('textbox', { name });
const startButton = () => screen.getByTestId('subject-clip-start') as HTMLButtonElement;
const fill = () => {
  tick('anna');
  tick('bottle');
  type('She opens the bottle');
};
const BODY = { subjects: [{ avatarId: 'anna' }, { avatarId: 'bottle' }], prompt: 'She opens the bottle', durationSeconds: 6, ratio: '16:9', resolution: '768P', takes: 1 };
const priced = () => screen.findByTestId('subject-clip-price', undefined, { timeout: 3000 });

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  h.providers = [H3, TEXT_ONLY];
  h.files = [{ id: 'audio-1', originalFileName: 'line.mp3', mimeType: 'audio/mpeg' }, { id: 'pic-1', originalFileName: 'photo.jpg', mimeType: 'image/jpeg' }];
  h.role = 'Member';
  h.allJobs = [];
  h.trackJob.mockReset().mockImplementation((_meta: unknown, work: Promise<unknown>) => work);
  h.cancelTracked.mockReset();
  h.notify.mockReset();
  h.mutate.mockReset();
  window.history.replaceState(null, '', '/app/editor/p1/timeline/t1');
});
afterEach(() => {
  cleanup();
  resetSubjectClipStore();
  registerEnhanceHost(null);
});

describe('the form', () => {
  it('cannot be sent until someone is chosen and the clip is described, and says which is missing', () => {
    const { api } = mockApi();
    mount(api);
    expect(startButton().disabled).toBe(true);
    expect(screen.getByTestId('subject-clip-blocker').textContent).toBe('Choose at least one person or object.');
    tick('bottle');
    expect(screen.getByTestId('subject-clip-blocker').textContent).toBe('Say what happens in the clip.');
    type('It spins slowly');
    expect(screen.queryByTestId('subject-clip-blocker')).toBeNull();
    expect(startButton().disabled).toBe(false);
    expect(api.estimate).not.toHaveBeenCalled(); // debounced: nothing was asked while typing
  });

  it('starts on the shape nearest to the timeline: the given size, else the open timeline, else 16:9', () => {
    const shape = () => (within(screen.getByTestId('subject-clip-shape')).getByRole('radio', { checked: true }) as HTMLInputElement).value;
    const { api } = mockApi();
    mount(api, { canvas: { width: 1080, height: 1920 } });
    expect(shape()).toBe('9:16');
    cleanup();
    registerEnhanceHost({ projectId: 'p1', getDoc: () => ({ settings: { width: 1080, height: 1350 } }) as never, dispatchBatch: () => undefined });
    mount(api, { canvas: undefined });
    expect(shape()).toBe('4:5');
    cleanup();
    registerEnhanceHost(null);
    mount(api, { canvas: undefined });
    expect(shape()).toBe('16:9');
  });

  it('offers the speech file only for one person alone, and sends it only then', async () => {
    const { api } = mockApi();
    mount(api);
    expect(screen.queryByTestId('subject-clip-speech')).toBeNull();
    tick('anna');
    const speech = select('What they say (audio file)');
    fireEvent.click(speech);
    // only the project's audio files are offered
    expect(screen.getByRole('option', { name: 'line.mp3' })).toBeTruthy();
    expect(screen.queryByRole('option', { name: 'photo.jpg' })).toBeNull();
    fireEvent.click(screen.getByRole('option', { name: 'line.mp3' }));
    type('She says hello');
    await priced();
    expect(api.estimate).toHaveBeenLastCalledWith('p1', expect.objectContaining({ subjects: [{ avatarId: 'anna' }], speechFileId: 'audio-1' }));
    // an object joins: nobody talks any more, and the file is not sent
    tick('bottle');
    expect(screen.queryByTestId('subject-clip-speech')).toBeNull();
    await waitFor(() => expect(api.estimate.mock.calls.at(-1)?.[1]).toEqual(expect.objectContaining({ subjects: [{ avatarId: 'anna' }, { avatarId: 'bottle' }] })), { timeout: 3000 });
    expect('speechFileId' in (api.estimate.mock.calls.at(-1)?.[1] as object)).toBe(false);
  });

  it('lists the models a member may pick and none for a role that may not read them', () => {
    const { api } = mockApi();
    mount(api);
    fireEvent.click(select('Model'));
    expect(screen.getByRole('option', { name: 'MiniMax H3 · ReelBolt Inference' })).toBeTruthy();
    cleanup();
    h.role = 'Billing';
    mount(api);
    fireEvent.click(select('Model'));
    expect(screen.queryByRole('option', { name: 'MiniMax H3 · ReelBolt Inference' })).toBeNull();
  });
});

describe('the price', () => {
  it('asks once the form settles, with exactly the body that will be sent, and shows what came back', async () => {
    const { api } = mockApi({ ...ESTIMATE, credits: 12.5, creditsHigh: 12.5, stillIncluded: true });
    mount(api);
    fill();
    expect(screen.getByTestId('subject-clip-estimate-checking')).toBeTruthy();
    expect((await priced()).textContent).toBe('About 12.5 credits.');
    expect(api.estimate).toHaveBeenCalledTimes(1);
    expect(api.estimate).toHaveBeenCalledWith('p1', BODY);
    expect(screen.getByTestId('subject-clip-still').textContent).toBe('Includes one still picture made first.');
  });

  it('shows the high figure too when the server gives a range, and no still line when none is made', async () => {
    const { api } = mockApi({ ...ESTIMATE, credits: 8, creditsHigh: 20 });
    mount(api);
    fill();
    expect((await priced()).textContent).toBe('About 8 credits, up to 20.');
    expect(screen.queryByTestId('subject-clip-still')).toBeNull();
  });

  it('shows no price at all when billing is off', async () => {
    const { api } = mockApi({ billingEnabled: false, affordable: true });
    mount(api);
    fill();
    await waitFor(() => expect(api.estimate).toHaveBeenCalled(), { timeout: 3000 });
    await waitFor(() => expect(screen.queryByTestId('subject-clip-estimate-checking')).toBeNull());
    expect(screen.queryByTestId('subject-clip-price')).toBeNull();
    expect(startButton().disabled).toBe(false);
  });

  it('says why in the server\'s words when it cannot run, and does not offer to start', async () => {
    const { api } = mockApi({ ...ESTIMATE, unavailableReason: 'No video model is set up for this workspace.' });
    mount(api);
    fill();
    expect((await screen.findByTestId('subject-clip-unavailable', undefined, { timeout: 3000 })).textContent).toBe('No video model is set up for this workspace.');
    expect(screen.queryByTestId('subject-clip-price')).toBeNull();
    expect(startButton().disabled).toBe(true);
  });

  it('words a clip of a person in a workspace that makes none yet itself, and does not offer to start', async () => {
    const { api } = mockApi({
      ...ESTIMATE,
      credits: null,
      creditsHigh: null,
      affordable: false,
      unavailableReason: 'Clips that show a person are not available in this workspace yet. A clip of objects only can be made.',
      unavailableCode: 'AVATAR_GENERATION_DISABLED',
    });
    mount(api);
    fill();
    expect((await screen.findByTestId('subject-clip-unavailable', undefined, { timeout: 3000 })).textContent)
      .toBe('Clips that show a person are not available in this workspace yet. A clip with objects only can be made.');
    expect(screen.queryByTestId('subject-clip-price')).toBeNull();
    expect(startButton().disabled).toBe(true);
  });

  it('says so when the balance does not cover it, with the way to add credits', async () => {
    const { api } = mockApi({ ...ESTIMATE, credits: 500, creditsHigh: 500, affordable: false });
    mount(api);
    fill();
    await priced();
    expect(screen.getByTestId('subject-clip-not-enough').textContent).toContain('You do not have enough credits for this.');
    expect(within(screen.getByTestId('subject-clip-not-enough')).getByRole('link', { name: 'Add credits' }).getAttribute('href')).toBe('/settings/billing');
    expect(startButton().disabled).toBe(true);
  });

  it('asks again when the form changes, and never shows the price of the form before', async () => {
    const { api } = mockApi();
    mount(api);
    fill();
    await priced();
    api.estimate.mockResolvedValue({ ...ESTIMATE, credits: 24, creditsHigh: 24 });
    fireEvent.click(within(screen.getByTestId('subject-clip-takes')).getByLabelText('2 clips'));
    expect(screen.queryByTestId('subject-clip-price')).toBeNull();
    await waitFor(() => expect(screen.queryByTestId('subject-clip-price')?.textContent).toBe('About 24 credits.'), { timeout: 3000 });
    expect(api.estimate).toHaveBeenLastCalledWith('p1', { ...BODY, takes: 2 });
  });

  it('a refused estimate is worded like a refused start; a price that cannot be checked does not block', async () => {
    const { api } = mockApi();
    api.estimate.mockRejectedValue(refusal(402, { code: 'PLAN_NOT_ENTITLED', feature: 'avatar_objects' }));
    mount(api);
    fill();
    const box = await screen.findByTestId('subject-clip-estimate-refusal', undefined, { timeout: 3000 });
    expect(box.textContent).toContain('Your plan does not include objects.');
    cleanup();
    api.estimate.mockRejectedValue(new Error('offline'));
    mount(api);
    fill();
    expect((await screen.findByTestId('subject-clip-estimate-failed', undefined, { timeout: 3000 })).textContent).toBe('The price could not be checked right now.');
    expect(startButton().disabled).toBe(false);
  });
});

describe('starting', () => {
  it('posts the form, closes, lists the run, tracks it in the activity tray and adds the clip when it is ready', async () => {
    const { api, finish } = mockApi();
    const { onClose } = mount(api);
    fill();
    await priced();
    fireEvent.click(startButton());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(api.start).toHaveBeenCalledWith('p1', BODY);
    expect(subjectClipJobs('p1')).toMatchObject([{ executionId: 'e1', state: 'queued', names: ['Anna', 'Bottle'], credits: 12 }]);
    expect(h.trackJob).toHaveBeenCalledWith(
      expect.objectContaining({ type: 'subjectClip', title: 'Anna, Bottle', projectId: 'p1', id: subjectClipActivityId('e1'), executionId: 'e1', href: '/editor?project=p1', creditsEstimate: 12 }),
      expect.any(Promise),
    );
    expect(api.wait).toHaveBeenCalledWith('p1', 'e1', expect.any(AbortSignal), expect.any(Function));
    // progress moves the listed state
    act(() => api.wait.mock.calls[0][3]({ executionId: 'e1', state: 'running', outputFileIds: [] }));
    expect(subjectClipJobs('p1')[0].state).toBe('running');
    await act(async () => finish({ executionId: 'e1', state: 'ready', outputFileIds: ['f1'] }));
    expect(subjectClipJobs('p1')[0]).toMatchObject({ state: 'ready', outputFileIds: ['f1'] });
    expect(api.refreshPool).toHaveBeenCalledWith('p1');
    expect(h.notify).toHaveBeenCalledWith(expect.objectContaining({ message: 'Clip added to your media' }));
  });

  it('leaves the "added" note to the activity tray when the person is no longer in the editor', async () => {
    const { api, finish } = mockApi();
    mount(api);
    fill();
    fireEvent.click(startButton());
    await waitFor(() => expect(api.start).toHaveBeenCalled());
    window.history.replaceState(null, '', '/app/dashboard');
    await act(async () => finish({ executionId: 'e1', state: 'ready', outputFileIds: ['f1'] }));
    expect(api.refreshPool).toHaveBeenCalledWith('p1');
    expect(h.notify).not.toHaveBeenCalled();
  });

  it('keeps the dialog open and lists nothing when the start is refused', async () => {
    const { api } = mockApi();
    api.start.mockRejectedValue(refusal(500, {}));
    const { onClose } = mount(api);
    fill();
    fireEvent.click(startButton());
    expect((await screen.findByTestId('subject-clip-refusal')).textContent).toContain('That did not work. Try again.');
    expect(onClose).not.toHaveBeenCalled();
    expect(subjectClipJobs('p1')).toEqual([]);
    expect(h.trackJob).not.toHaveBeenCalled();
  });
});

describe('the one-time consent', () => {
  const needsConsent = () => refusal(409, { code: 'CONSENT_REQUIRED', message: 'Consent is required.' });

  it('asks when the server says it is needed, records the yes, then starts the same clip', async () => {
    const { api } = mockApi();
    api.start.mockRejectedValueOnce(needsConsent());
    const { onClose } = mount(api);
    fill();
    fireEvent.click(startButton());
    const dialog = within(await screen.findByRole('dialog', { name: 'Send avatar and object files to outside AI model providers (other companies)?' }));
    expect(dialog.getByText(/the files you added for it are sent to the outside AI model providers this project uses/)).toBeTruthy();
    expect(dialog.getByText('its pictures')).toBeTruthy();
    expect(dialog.getByText('its short videos')).toBeTruthy();
    expect(dialog.getByText("a person's voice samples")).toBeTruthy();
    expect(dialog.getByText(/asked once for this project.*withdraw this at any time in the project's Avatars tab/)).toBeTruthy();
    const confirm = dialog.getByTestId('media-consent-confirm') as HTMLButtonElement;
    expect(confirm.disabled).toBe(true); // an explicit yes: the box starts unticked
    expect(api.grantConsent).not.toHaveBeenCalled();
    fireEvent.click(dialog.getByTestId('media-consent-check'));
    fireEvent.click(confirm);
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(api.grantConsent).toHaveBeenCalledWith('p1');
    expect(api.start).toHaveBeenCalledTimes(2);
    expect(api.start).toHaveBeenLastCalledWith('p1', BODY);
    // the Avatars tab and the picture editor read the same answer
    expect(h.mutate).toHaveBeenCalledWith('/api/v1/projects/p1/avatars/media-consent');
    expect(screen.queryByTestId('subject-clip-refusal')).toBeNull();
  });

  it('starts nothing when the person says not now', async () => {
    const { api } = mockApi();
    api.start.mockRejectedValueOnce(needsConsent());
    const { onClose } = mount(api);
    fill();
    fireEvent.click(startButton());
    fireEvent.click(await screen.findByTestId('media-consent-cancel'));
    await waitFor(() => expect(screen.queryByTestId('media-consent-dialog')).toBeNull());
    expect(api.grantConsent).not.toHaveBeenCalled();
    expect(api.start).toHaveBeenCalledTimes(1);
    expect(onClose).not.toHaveBeenCalled();
  });

  it('says so when the yes could not be saved, and starts nothing', async () => {
    const { api } = mockApi();
    api.start.mockRejectedValueOnce(needsConsent());
    api.grantConsent.mockRejectedValue(new Error('offline'));
    mount(api);
    fill();
    fireEvent.click(startButton());
    fireEvent.click(await screen.findByTestId('media-consent-check'));
    fireEvent.click(screen.getByTestId('media-consent-confirm'));
    expect((await screen.findByTestId('media-consent-error')).textContent).toBe('We could not save your choice. Try again.');
    expect(api.start).toHaveBeenCalledTimes(1);
  });

  it('does not block the form when the estimate itself asks for the consent first', async () => {
    const { api } = mockApi();
    api.estimate.mockRejectedValue(needsConsent());
    mount(api);
    fill();
    expect(await screen.findByTestId('subject-clip-estimate-consent', undefined, { timeout: 3000 })).toBeTruthy();
    expect(startButton().disabled).toBe(false);
  });
});

describe('refusals, in plain words', () => {
  const refuse = async (error: unknown) => {
    const { api } = mockApi();
    api.start.mockRejectedValue(error);
    mount(api);
    fill();
    fireEvent.click(startButton());
    return { api, box: await screen.findByTestId('subject-clip-refusal') };
  };

  it.each([
    ['video_generation', 'Your plan does not include making videos with AI. See the plans to unlock it.'],
    ['avatars', 'Your plan does not include avatars of people. See the plans to unlock them.'],
    ['avatar_objects', 'Your plan does not include objects. See the plans to unlock them.'],
    ['something_new', 'Your plan does not include this. See the plans to unlock it.'],
  ])('a plan without %s', async (feature, sentence) => {
    const { box } = await refuse(refusal(402, { code: 'PLAN_NOT_ENTITLED', feature }));
    expect(box.getAttribute('data-kind')).toBe('plan');
    expect(box.textContent).toContain(sentence);
    expect(within(box).getByRole('link', { name: 'See plans' }).getAttribute('href')).toBe('/settings/billing#plans');
  });

  it('too few credits: the server\'s own sentence, and the way to add some', async () => {
    const { box } = await refuse(refusal(402, { code: 'INSUFFICIENT_CREDITS', message: 'You need 30 more credits to make this clip.', missing: 30 }));
    expect(box.textContent).toContain('You need 30 more credits to make this clip.');
    expect(within(box).getByRole('link', { name: 'Add credits' })).toBeTruthy();
  });

  it('too many runs in a short time', async () => {
    const { box } = await refuse(refusal(429, { code: 'RUN_RATE_LIMITED' }));
    expect(box.textContent).toContain('You have started a lot of clips in a short time. Wait a while and try again.');
  });

  it('a model that cannot show pictures: names the one that can and switches to it in one press', async () => {
    const { api, box } = await refuse(refusal(409, { code: 'AVATAR_NOT_SUPPORTED_BY_MODEL', sibling: 'MiniMax-H3' }));
    expect(box.textContent).toContain('This model cannot show pictures. Use MiniMax H3 instead.');
    api.start.mockResolvedValue({ executionId: 'e2', state: 'queued' });
    fireEvent.click(within(box).getByRole('button', { name: 'Use MiniMax H3' }));
    expect(screen.queryByTestId('subject-clip-refusal')).toBeNull();
    fireEvent.click(startButton());
    await waitFor(() => expect(api.start).toHaveBeenLastCalledWith('p1', { ...BODY, capabilityId: 'cap-h3' }));
  });

  it('a model that cannot show pictures, when the one that can is not in the list: no switch is offered', async () => {
    const { box } = await refuse(refusal(409, { code: 'AVATAR_NOT_SUPPORTED_BY_MODEL', sibling: 'Some other model' }));
    expect(box.textContent).toContain('Use Some other model instead.');
    expect(within(box).queryByRole('button')).toBeNull();
  });

  it('an avatar that stopped being usable, with why when the server says', async () => {
    const { box } = await refuse(refusal(409, { code: 'AVATAR_NOT_USABLE', reason: 'reference_missing' }));
    expect(box.textContent).toContain('One of the people or objects you chose cannot be used right now. One of its files was deleted, replaced or changed after you added it.');
    cleanup();
    const plain = await refuse(refusal(409, { code: 'AVATAR_NOT_USABLE' }));
    expect(plain.box.textContent).toContain('Check it in the Avatars tab.');
  });

  it('an unverified email address', async () => {
    const { box } = await refuse(refusal(403, { code: 'EMAIL_NOT_VERIFIED' }));
    expect(box.textContent).toContain('Verify your email address first, then try again.');
  });

  it('choices the server does not allow: its sentence and each thing to fix', async () => {
    const { box } = await refuse(refusal(400, { message: 'Some settings are not allowed.', errors: ['This model makes clips of 5 to 10 seconds.'] }, ['This model makes clips of 5 to 10 seconds.']));
    expect(box.textContent).toContain('Some settings are not allowed.');
    expect(box.textContent).toContain('This model makes clips of 5 to 10 seconds.');
  });

  it('the feature switched off', async () => {
    const { box } = await refuse(refusal(404, {}));
    expect(box.textContent).toContain('Clips with avatars are not available here');
  });

  it('the estimate can already say the model cannot show pictures, and offers the same switch', async () => {
    const { api } = mockApi();
    api.estimate.mockRejectedValueOnce(refusal(409, { code: 'AVATAR_NOT_SUPPORTED_BY_MODEL', sibling: 'MiniMax-H3' }));
    mount(api);
    fill();
    const box = await screen.findByTestId('subject-clip-estimate-refusal', undefined, { timeout: 3000 });
    expect(box.textContent).toContain('Use MiniMax H3 instead.');
    fireEvent.click(within(box).getByRole('button', { name: 'Use MiniMax H3' }));
    await waitFor(() => expect(api.estimate).toHaveBeenLastCalledWith('p1', { ...BODY, capabilityId: 'cap-h3' }), { timeout: 3000 });
    expect((await priced()).textContent).toBe('About 12 credits.');
  });

  it('clears the refusal as soon as the form changes', async () => {
    await refuse(refusal(429, {}));
    type('Something else happens');
    expect(screen.queryByTestId('subject-clip-refusal')).toBeNull();
  });
});

describe('the job store', () => {
  const REQUEST = { subjects: [{ avatarId: 'anna' }], prompt: 'x', durationSeconds: 6, ratio: '16:9', resolution: '768P', takes: 1 };

  it('shows a failed run with the server\'s sentence and rejects what the tray tracks', async () => {
    const { api, fail } = mockApi();
    let finished: Promise<unknown> | null = null;
    await startSubjectClipJob('p1', REQUEST, { names: ['Anna'], api, onStarted: (_job, f) => { finished = f; } });
    fail(new SubjectClipFailedError({ executionId: 'e1', state: 'failed', outputFileIds: [], message: 'The model refused this description.', failureCode: 'CONTENT_BLOCKED' }));
    await expect(finished).rejects.toBeInstanceOf(SubjectClipFailedError);
    expect(subjectClipJobs('p1')[0]).toMatchObject({ state: 'failed', message: 'The model refused this description.', failureCode: 'CONTENT_BLOCKED' });
    expect(api.refreshPool).not.toHaveBeenCalled();
  });

  it('cancel shows the run as stopped at once, tells the server, and stops polling', async () => {
    const { api } = mockApi();
    let finished: Promise<unknown> | null = null;
    await startSubjectClipJob('p1', REQUEST, { names: ['Anna'], api, onStarted: (_job, f) => { finished = f; } });
    const signal = api.wait.mock.calls[0][2];
    const cancelling = cancelSubjectClipJob('e1');
    expect(subjectClipJobs('p1')[0].state).toBe('cancelled');
    expect(signal.aborted).toBe(true);
    await cancelling;
    expect(api.cancel).toHaveBeenCalledWith('p1', 'e1');
    await expect(finished).rejects.toMatchObject({ name: 'AbortError' });
    // cancelling again, or a finished run, does nothing
    await cancelSubjectClipJob('e1');
    expect(api.cancel).toHaveBeenCalledTimes(1);
  });

  it('a cancel that came too late shows the clip the run made', async () => {
    const { api } = mockApi();
    api.cancel.mockResolvedValue(null);
    api.getJob.mockResolvedValue({ executionId: 'e1', state: 'ready', outputFileIds: ['f9'] });
    await startSubjectClipJob('p1', REQUEST, { names: ['Anna'], api });
    await cancelSubjectClipJob('e1');
    expect(subjectClipJobs('p1')[0]).toMatchObject({ state: 'ready', outputFileIds: ['f9'] });
    expect(api.refreshPool).toHaveBeenCalledWith('p1');
  });

  it('keeps jobs per project, running ones first, and clears only finished ones', async () => {
    const first = mockApi();
    await startSubjectClipJob('p1', REQUEST, { names: ['Anna'], api: first.api });
    const second = mockApi();
    second.api.start.mockResolvedValue({ executionId: 'e2', state: 'running' });
    await startSubjectClipJob('p1', REQUEST, { names: ['Bottle'], api: second.api });
    const other = mockApi();
    other.api.start.mockResolvedValue({ executionId: 'e3', state: 'queued' });
    await startSubjectClipJob('p2', REQUEST, { names: ['Car'], api: other.api });
    expect(subjectClipJobs('p2').map((j) => j.executionId)).toEqual(['e3']);
    await act(async () => first.finish({ executionId: 'e1', state: 'ready', outputFileIds: ['f1'] }));
    expect(subjectClipJobs('p1').map((j) => `${j.executionId}:${j.state}`)).toEqual(['e2:running', 'e1:ready']);
    dismissSubjectClipJob('e2'); // still running: stays
    dismissSubjectClipJob('e1');
    expect(subjectClipJobs('p1').map((j) => j.executionId)).toEqual(['e2']);
  });
});
