// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { StepChangeStepCapability } from '@/lib/types/change-requests';
import { RequestStepChangeModal } from './RequestStepChangeModal';
import { humanise, itemName as itemNameT, keyCopy as keyCopyT, valueLabel as valueLabelT } from './change-request-copy';
import { buildChangeRequest as buildCr, emptyForm, overrideKind } from './change-request-form';
import { capability, clipCapability, musicCapability, scriptCapability, thumbnailCapability } from './change-request-fixtures';
import { createTranslator } from 'use-intl/core';
import { loadMessages } from '@/i18n/messages';

const tCr = createTranslator({ locale: 'en', messages: loadMessages('en'), namespace: 'changeRequests' }) as unknown as (k: string, v?: Record<string, string | number>) => string;
const buildChangeRequest = (cap: Parameters<typeof buildCr>[0], form: Parameters<typeof buildCr>[1]) => buildCr(cap, form, tCr);
const itemName = (stepType: string, id: string) => itemNameT(tCr, stepType, id);
const keyCopy = (stepType: string, key: string) => keyCopyT(tCr, stepType, key);
const valueLabel = (key: string, value: string) => valueLabelT(tCr, key, value);


const push = vi.fn();
vi.mock('next/navigation', () => ({ useRouter: () => ({ push }) }));
vi.mock('next/link', () => ({ default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a> }));
const apiFetch = vi.fn();
vi.mock('@/lib/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/client')>();
  return { ...actual, apiFetch: (...a: unknown[]) => apiFetch(...a) };
});

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((q) => ({
      matches: false, media: q, onchange: null, addListener: vi.fn(), removeListener: vi.fn(),
      addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  Object.defineProperty(document, 'fonts', { configurable: true, value: { addEventListener: vi.fn(), removeEventListener: vi.fn() } });
});
beforeEach(() => { apiFetch.mockReset(); push.mockReset(); });
afterEach(cleanup);

const preview = {
  estimate: { billingEnabled: true, lowCredits: 1, highCredits: 3, requiredCredits: 3, balanceCredits: 10, affordable: true },
  replay: { steps: [{ stepOrder: 4, label: 'Clips', disposition: 'Run', reason: '' }] },
};

function open(cap: StepChangeStepCapability) {
  apiFetch.mockResolvedValue(preview);
  return render(
    <MantineProvider>
      <RequestStepChangeModal projectId="p1" executionId="ex1" capability={cap} opened onClose={vi.fn()} onCreated={vi.fn()} />
    </MantineProvider>,
  );
}

/** The body of the last estimate call. */
const lastBody = () => JSON.parse((apiFetch.mock.calls.at(-1)![1] as { body: string }).body);
const estimateShown = () => screen.findByTestId('change-request-estimate', undefined, { timeout: 3000 });

describe('the form is drawn from the capability by type', () => {
  it('Text: a text area limited to the catalogue length', async () => {
    open(clipCapability());
    fireEvent.click(await screen.findByLabelText(/Waves at sunset/));
    const field = (await screen.findByLabelText('New description of the clip')) as HTMLTextAreaElement;
    expect(field.tagName).toBe('TEXTAREA');
    expect(field.maxLength).toBe(2000);
    expect(field.value).toBe('Waves at sunset');
  });

  it('Number: a number input with the catalogue range in its help', async () => {
    open(musicCapability());
    fireEvent.click(await screen.findByLabelText(/Calm piano/));
    const field = (await screen.findByLabelText('Length')) as HTMLInputElement;
    expect(field.inputMode).toBe('decimal');
    expect(screen.getByText('Between 1 and 120 seconds.')).toBeTruthy();
  });

  it('Flag: a switch, off until chosen', async () => {
    open(clipCapability());
    fireEvent.click(await screen.findByLabelText(/Waves at sunset/));
    const sw = (await screen.findByRole('switch', { name: /Make a fresh take/ })) as HTMLInputElement;
    expect(sw.type).toBe('checkbox');
    expect(sw.checked).toBe(false);
    fireEvent.click(sw);
    expect(sw.checked).toBe(true);
  });

  it('Enum: a select of the listed values in plain words, starting on "keep"', async () => {
    open(thumbnailCapability());
    const sel = (await screen.findByLabelText('Where the text goes')) as HTMLSelectElement;
    expect(sel.tagName).toBe('SELECT');
    expect(Array.from(sel.options).map((o) => o.textContent)).toEqual(['Keep as it is', 'No text', 'Lower third', 'Upper third', 'Centre']);
    expect(sel.value).toBe('');
    const box = (await screen.findByLabelText('Box behind the text')) as HTMLSelectElement;
    expect(Array.from(box.options).map((o) => o.value)).toEqual(['', 'black@0.45', 'black@0.6', 'white@0.4', 'none']);
  });

  it('PronunciationList: the existing editor, still capped by the catalogue', async () => {
    open(capability());
    fireEvent.click(await screen.findByLabelText(/Hello there/));
    fireEvent.click(await screen.findByText('Add a word'));
    expect(await screen.findByLabelText('Word 1')).toBeTruthy();
    expect(screen.getByLabelText('How to say it 1')).toBeTruthy();
  });

  it('an unknown key is drawn nowhere and cannot break the build; an unknown type is left out', () => {
    const cap = capability({
      overrides: [
        ...capability().overrides,
        { key: 'mystery', type: 'Hologram', min: null, max: null, maxLength: null, maxEntries: null },
      ],
    });
    expect(overrideKind(cap.overrides[4])).toBeNull();
    const f = { ...emptyForm(), selected: ['vo1'], drafts: { vo1: { mystery: 'x', speed: 1.2 } } };
    expect(buildChangeRequest(cap, f).request?.overrides).toEqual({ vo1: { speed: 1.2 } });
  });

  it('an unknown key of a known type gets a humanised label', async () => {
    const cap = thumbnailCapability({
      overrides: [{ key: 'overlayTextSize', type: 'Number', min: 1, max: 9, maxLength: null, maxEntries: null }],
    });
    open(cap);
    expect(await screen.findByLabelText('Overlay text size')).toBeTruthy();
    expect(humanise('some_new-key')).toBe('Some new key');
    expect(keyCopy('X', 'fooBar').label).toBe('Foo bar');
    expect(valueLabel('overlayPosition', 'UpperThird')).toBe('Upper third');
    expect(valueLabel('somethingNew', 'BigBlue')).toBe('Big blue');
  });
});

describe('video clips', () => {
  it('groups takes under their shot and shows status and length', async () => {
    open(clipCapability());
    const shot = await screen.findByTestId('change-request-item-v1');
    expect(within(shot).getByTestId('change-request-item-v1t1')).toBeTruthy();
    expect(within(shot).getByTestId('change-request-item-v1t2')).toBeTruthy();
    expect(shot.textContent).toContain('Clip 1');
    expect(shot.textContent).toContain('Ready · 5s');
    expect(within(shot).getByTestId('change-request-item-v1t2').textContent).toContain('Take 2');
    expect(within(shot).getByTestId('change-request-item-v1t2').textContent).toContain('Failed');
    expect(screen.getByTestId('change-request-item-v2').textContent).toContain('Clip 2');
    // v2 is a shot of its own, not nested under v1
    expect(within(shot).queryByTestId('change-request-item-v2')).toBeNull();
  });

  it('sends a new description for a clip and prices it', async () => {
    open(clipCapability());
    fireEvent.click(await screen.findByLabelText(/Waves at sunset/));
    fireEvent.change(await screen.findByLabelText('New description of the clip'), { target: { value: 'Storm waves at night' } });
    await estimateShown();
    expect(lastBody()).toEqual({ stepResultId: 'sr4', itemIds: ['v2'], overrides: { v2: { prompt: 'Storm waves at night' } } });
    expect(apiFetch.mock.calls.at(-1)![0]).toContain('/estimate');
    expect(screen.getByTestId('change-request-estimate').textContent).toContain('About 1.00 to 3.00');
  });

  it('a fresh take with the same description sends only retake', async () => {
    open(clipCapability());
    fireEvent.click(await screen.findByLabelText(/Waves at sunset/));
    fireEvent.click(await screen.findByRole('switch', { name: /Make a fresh take/ }));
    await estimateShown();
    expect(lastBody()).toEqual({ stepResultId: 'sr4', itemIds: ['v2'], overrides: { v2: { retake: true } } });
  });

  it('a take can be changed on its own', async () => {
    open(clipCapability());
    const take = await screen.findByTestId('change-request-item-v1t1');
    fireEvent.click(within(take).getByRole('checkbox'));
    fireEvent.click(await within(take).findByRole('switch', { name: /Make a fresh take/ }));
    await estimateShown();
    expect(lastBody().overrides).toEqual({ v1t1: { retake: true } });
  });

  it('"All clips" offers the fresh take but not a description shared by every clip', async () => {
    open(clipCapability());
    fireEvent.click(await screen.findByText('All clips'));
    expect(await screen.findByRole('switch', { name: /Make a fresh take/ })).toBeTruthy();
    expect(screen.queryByLabelText('New description of the clip')).toBeNull();
    fireEvent.click(screen.getByRole('switch', { name: /Make a fresh take/ }));
    await estimateShown();
    expect(lastBody()).toEqual({ stepResultId: 'sr4', overrides: { '*': { retake: true } } });
  });

  it('does not offer a cut-short description as the starting text', async () => {
    const cap = clipCapability({ items: [{ id: 'v1', status: 'succeeded', durationSec: 5, text: 'A very long...' }] });
    open(cap);
    fireEvent.click(await screen.findByLabelText(/A very long/));
    expect(((await screen.findByLabelText('New description of the clip')) as HTMLTextAreaElement).value).toBe('');
  });

  it('refuses a description over the catalogue length and an unchanged description', () => {
    const cap = clipCapability();
    const tooLong = { ...emptyForm(), selected: ['v2'], drafts: { v2: { prompt: 'x'.repeat(2001) } } };
    const r = buildChangeRequest(cap, tooLong);
    expect(r.request).toBeNull();
    expect(r.errors[0]).toBe('Clip 2: new description of the clip is longer than 2000 characters.');
    const same = { ...emptyForm(), selected: ['v2'], drafts: { v2: { prompt: 'Waves at sunset' } } };
    expect(buildChangeRequest(cap, same)).toEqual({ request: null, errors: [] });
    const takeErr = buildChangeRequest(cap, { ...emptyForm(), selected: ['v1t2'], drafts: { v1t2: { prompt: 'y'.repeat(2001) } } });
    expect(takeErr.errors[0]).toContain('Clip 1, take 2');
  });
});

describe('music', () => {
  it('sends a new length for one take', async () => {
    open(musicCapability());
    fireEvent.click(await screen.findByLabelText(/Calm piano/));
    fireEvent.change(await screen.findByLabelText('Length'), { target: { value: '45' } });
    await estimateShown();
    expect(lastBody()).toEqual({ stepResultId: 'sr5', itemIds: ['a1'], overrides: { a1: { durationSec: 45 } } });
  });

  it('"All takes" changes the length of every take with *', async () => {
    open(musicCapability());
    fireEvent.click(await screen.findByText('All takes'));
    fireEvent.change(await screen.findByLabelText('Length'), { target: { value: '20' } });
    await estimateShown();
    expect(lastBody()).toEqual({ stepResultId: 'sr5', overrides: { '*': { durationSec: 20 } } });
  });

  it('sends a new description and refuses a length outside the catalogue', async () => {
    open(musicCapability());
    fireEvent.click(await screen.findByLabelText(/Calm piano/));
    fireEvent.change(await screen.findByLabelText('New description'), { target: { value: 'Slow jazz' } });
    await estimateShown();
    expect(lastBody().overrides).toEqual({ a1: { prompt: 'Slow jazz' } });

    const bad = buildChangeRequest(musicCapability(), { ...emptyForm(), selected: ['a1'], drafts: { a1: { durationSec: 500 } } });
    expect(bad.request).toBeNull();
    expect(bad.errors[0]).toBe('Take 1: length must be between 1 and 120.');
  });

  it('shows validation errors in the form and does not call the estimate', async () => {
    open(musicCapability());
    fireEvent.click(await screen.findByLabelText(/Calm piano/));
    fireEvent.change(await screen.findByLabelText('Length'), { target: { value: '500' } });
    // Mantine clamps on blur only; a typed 500 stays in the field and fails the catalogue range.
    expect((await screen.findByTestId('change-request-validation')).textContent).toContain('length must be between 1 and 120');
    expect(apiFetch).not.toHaveBeenCalled();
    expect((screen.getByText('Confirm and redo').closest('button') as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('thumbnail', () => {
  it('shows the form straight away, with no item picker', async () => {
    open(thumbnailCapability());
    expect(await screen.findByLabelText('Text on the picture')).toBeTruthy();
    expect(screen.queryByRole('checkbox')).toBeNull();
    expect(screen.queryByText('All items')).toBeNull();
  });

  it('builds the request for text, moment and the three choices under *', async () => {
    open(thumbnailCapability());
    fireEvent.change(await screen.findByLabelText('Text on the picture'), { target: { value: 'Launch day' } });
    fireEvent.change(screen.getByLabelText('Moment in the video'), { target: { value: '12.5' } });
    fireEvent.change(screen.getByLabelText('Where the text goes'), { target: { value: 'LowerThird' } });
    fireEvent.change(screen.getByLabelText('Text colour'), { target: { value: 'yellow' } });
    fireEvent.change(screen.getByLabelText('Box behind the text'), { target: { value: 'black@0.6' } });
    await waitFor(() => expect(apiFetch).toHaveBeenCalled(), { timeout: 3000 });
    await estimateShown();
    expect(lastBody()).toEqual({
      stepResultId: 'sr6',
      overrides: { '*': { overlayText: 'Launch day', atTimeSec: 12.5, overlayPosition: 'LowerThird', overlayFontColor: 'yellow', overlayBoxColor: 'black@0.6' } },
    });
    expect(screen.getByTestId('change-request-estimate').textContent).toContain('About');
  });

  it('needs at least one change; an instruction on its own is not one', async () => {
    const cap = thumbnailCapability();
    expect(buildChangeRequest(cap, emptyForm())).toEqual({ request: null, errors: [] });
    const talk = buildChangeRequest(cap, { ...emptyForm(), instruction: 'Make it pop' });
    expect(talk.request).toBeNull();
    expect(talk.errors).toEqual(['Choose at least one change.']);
    open(cap);
    expect((await screen.findByText('Confirm and redo')).closest('button')!.disabled).toBe(true);
    expect(apiFetch).not.toHaveBeenCalled();
  });

  it('refuses text that is too long, a moment out of range and a choice that is not listed', () => {
    const cap = thumbnailCapability();
    const r = buildChangeRequest(cap, {
      ...emptyForm(),
      all: { overlayText: 'x'.repeat(401), atTimeSec: -1, overlayPosition: 'Nowhere' },
    });
    expect(r.request).toBeNull();
    expect(r.errors).toEqual([
      'This change: text on the picture is longer than 400 characters.',
      'This change: moment in the video must be between 0 and 3600.',
      'This change: choose one of the listed options for where the text goes.',
    ]);
  });

  it('keeps the instruction alongside real changes', () => {
    const r = buildChangeRequest(thumbnailCapability(), { ...emptyForm(), instruction: ' bolder ', all: { overlayFontColor: 'black' } });
    expect(r.request).toEqual({ stepResultId: 'sr6', instruction: 'bolder', overrides: { '*': { overlayFontColor: 'black' } } });
  });
});

describe('music retake copy', () => {
  it('words the retake switch for music as a new version', () => {
    expect(keyCopy('AudioGenerate', 'retake').label).toBe('Make a new version');
    expect(keyCopy('VideoGenerate', 'retake').label).toBe('Make a fresh take');
  });
});

describe('names in plain words', () => {
  it('turns item ids into clip and take names and leaves other ids alone', () => {
    expect(itemName('VideoGenerate', 'v3')).toBe('Clip 3');
    expect(itemName('VideoGenerate', 'v3t2')).toBe('Take 2');
    expect(itemName('AudioGenerate', 'a4')).toBe('Take 4');
    expect(itemName('Voiceover', 'vo1')).toBe('vo1');
    expect(itemName('VideoGenerate', 'weird')).toBe('weird');
  });
});

describe('the script: scenes, and the standing cost note', () => {
  it('shows the most-expensive notice from the capability, and none for a step without one', async () => {
    open(scriptCapability());
    expect((await screen.findByTestId('change-request-cost-note')).textContent).toContain('the most expensive option: everything after it is made again');
    cleanup();
    open(capability());
    await screen.findByText(/Pick what to change/);
    expect(screen.queryByTestId('change-request-cost-note')).toBeNull();
  });

  it('ignores a cost note key it does not know', async () => {
    open(scriptCapability({ costNote: 'something-new' }));
    await screen.findByText(/Pick what to change/);
    expect(screen.queryByTestId('change-request-cost-note')).toBeNull();
  });

  it('names scene overrides in plain words', () => {
    expect(keyCopy('Agent', 'voiceover').label).toBe('New narration');
    expect(keyCopy('Agent', 'onScreenText').label).toBe('Text on screen');
    expect(keyCopy('Agent', 'visualDescription').label).toBe('What the scene shows');
  });

  it('a scene and a free instruction alone is a valid script request (instruction only)', () => {
    const r = buildChangeRequest(scriptCapability(), { ...emptyForm(), selected: ['sc2'], instruction: ' make scene 2 calmer ' });
    expect(r.request).toEqual({ stepResultId: 'sr2', instruction: 'make scene 2 calmer', itemIds: ['sc2'] });
  });

  it('still needs a change for a step that does not allow instruction only, and a scene for the script', () => {
    const noFlag = scriptCapability({ allowsInstructionOnly: false });
    expect(buildChangeRequest(noFlag, { ...emptyForm(), selected: ['sc2'], instruction: 'calmer' }).request).toBeNull();
    expect(buildChangeRequest(scriptCapability(), { ...emptyForm(), instruction: 'calmer' }).request).toBeNull();
  });

  it('sends the on-screen text with its line breaks as given and refuses too many lines', () => {
    const lines = 'Dawn\nHarbour';
    const ok = buildChangeRequest(scriptCapability(), { ...emptyForm(), selected: ['sc1'], drafts: { sc1: { onScreenText: lines } } });
    expect(ok.request?.overrides).toEqual({ sc1: { onScreenText: lines } });
    const many = buildChangeRequest(scriptCapability(), { ...emptyForm(), selected: ['sc1'], drafts: { sc1: { onScreenText: 'a\nb\nc\nd\ne\nf\ng' } } });
    expect(many.request).toBeNull();
    expect(many.errors[0]).toContain('6 lines');
  });

  it('draws the on-screen text as a text area and lets the instruction-only request be confirmed', async () => {
    open(scriptCapability());
    fireEvent.click(await screen.findByLabelText(/It writes, speaks and edits for you/));
    const field = (await screen.findByLabelText('Text on screen')) as HTMLTextAreaElement;
    expect(field.tagName).toBe('TEXTAREA');
    fireEvent.change(await screen.findByLabelText(/What should change in the scene/), { target: { value: 'make it calmer' } });
    await waitFor(() => expect((screen.getByRole('button', { name: /Confirm and redo/ }) as HTMLButtonElement).disabled).toBe(false));
  });
});
