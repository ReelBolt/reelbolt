// @vitest-environment jsdom
import { useRef } from 'react';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import type { StepChangeRequest, StepChangeStepCapability } from '@/lib/types/change-requests';
import type { RevisionHistory } from '@/lib/types/revisions';
import { ChangeVideoPanel } from './ChangeVideoPanel';
import { emptyForm } from './change-request-form';
import { capability, changeRequest } from './change-request-fixtures';
import type { ChangeEntry } from './change-set';
import { history, revision, version } from './revision-fixtures';
import { useChangeSet } from './use-change-set';

const push = vi.fn();
vi.mock('next/navigation', () => ({ useRouter: () => ({ push }) }));
vi.mock('next/link', () => ({ default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a> }));

let revisionsResult: { data: RevisionHistory | undefined; error: unknown; mutate: ReturnType<typeof vi.fn> };
vi.mock('@/lib/hooks/use-revisions', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/hooks/use-revisions')>();
  return { ...actual, useRevisions: () => revisionsResult };
});

const apiFetch = vi.fn();
vi.mock('@/lib/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/client')>();
  return { ...actual, apiFetch: (...args: unknown[]) => apiFetch(...args) };
});

let phone = false;
beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((q: string) => ({
      matches: phone && q.includes('max-width'), media: q, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  // jsdom has no FontFaceSet; Mantine's autosize Textarea listens on it.
  Object.defineProperty(document, 'fonts', { configurable: true, value: { addEventListener: vi.fn(), removeEventListener: vi.fn() } });
});
beforeEach(() => {
  apiFetch.mockReset();
  push.mockReset();
  phone = false;
  revisionsResult = { data: history(), error: undefined, mutate: vi.fn() };
});
afterEach(cleanup);

const film = (over: Partial<StepChangeStepCapability> = {}): StepChangeStepCapability => ({
  stepOrder: 7, stepResultId: 'sr7', stepType: 'Composition', label: 'Video', supported: true, notSupportedReason: null,
  route: 'composition-revision', itemKind: 'None', items: [], overrides: [], combinable: true, ...over,
});
const stepEntry: ChangeEntry = {
  stepOrder: 3, label: 'Narration', stepType: 'Voiceover', form: emptyForm(), summary: ['Hello there.', 'Changing: Speed: 1.5'],
  request: { stepResultId: 'sr3', itemIds: ['vo1'], overrides: { vo1: { speed: 1.5 } } },
};
const preview = {
  estimate: { billingEnabled: true, lowCredits: 2, highCredits: 5, requiredCredits: 5, balanceCredits: 50, affordable: true },
  replay: { steps: [{ stepOrder: 1, label: 'Script', disposition: 'Reuse', reason: '' }, { stepOrder: 3, label: 'Narration', disposition: 'Revise', reason: '' }] },
};

const callsTo = (suffix: string) => apiFetch.mock.calls.filter((c) => String(c[0]).endsWith(suffix));
const bodiesTo = (suffix: string) => callsTo(suffix).map((c) => JSON.parse((c[1] as { body: string }).body));

/** Answers the revision route, the estimate and the change-request route. */
const answerAll = () => apiFetch.mockImplementation((url: string) => {
  if (url.endsWith('/revisions')) return Promise.resolve({ id: 'rev9', executionId: 'ex-new', workflowId: 'wf-rev' });
  if (url.endsWith('/estimate')) return Promise.resolve(preview);
  return Promise.resolve(changeRequest({ executionId: 'ex9', workflowDefinitionId: 'wf9' }));
});

interface HarnessProps {
  steps?: StepChangeStepCapability[];
  requests?: StepChangeRequest[];
  runFinished?: boolean;
  runStatus?: string;
  max?: number;
}
/** The run page's wiring in miniature: the change set, the film step's shortcut and the unified panel. */
function Harness({ steps = [capability(), film()], requests = [], runFinished = true, runStatus = 'Passed', max = 6 }: HarnessProps) {
  const set = useChangeSet();
  const ref = useRef<HTMLTextAreaElement>(null);
  return (
    <>
      <button onClick={() => set.upsert(stepEntry)}>add-step</button>
      <button onClick={() => { ref.current?.focus(); }}>film-shortcut</button>
      <ChangeVideoPanel
        projectId="p1" executionId="ex1" runStatus={runStatus} runFinished={runFinished} changeSet={set} requests={requests}
        capabilities={{ executionId: 'ex1', runFinished: true, maxTargets: max, steps }}
        onEditEntry={() => undefined} onCreated={set.clear} textareaRef={ref}
      />
    </>
  );
}
const mount = (props: HarnessProps = {}) => render(<MantineProvider><Harness {...props} /></MantineProvider>);

const textarea = () => screen.getByLabelText('What should change?') as HTMLTextAreaElement;
const submitButton = () => screen.getByTestId('change-set-submit') as HTMLButtonElement;
const write = (text: string) => fireEvent.change(textarea(), { target: { value: text } });
async function submit() {
  await act(async () => { fireEvent.click(submitButton()); });
}

const replayHistory = () => history({
  replay: {
    available: true,
    reason: null,
    hasEditDecisions: true,
    steps: [
      { stepOrder: 5, label: 'Analyze the film', stepType: 'VideoAnalyze', keepCut: 'Run', reEdit: 'Run' },
      { stepOrder: 6, label: 'Edit room deliberation', stepType: 'EditRoom', keepCut: 'KeepIfCompatible', reEdit: 'Run' },
      { stepOrder: 7, label: 'Compile the final cut', stepType: 'VideoCompile', keepCut: 'Run', reEdit: 'Run' },
    ],
    keepCutEstimate: { billingEnabled: true, lowCredits: 5, highCredits: 12, requiredCredits: 12, balanceCredits: 100, affordable: true },
    reEditEstimate: { billingEnabled: true, lowCredits: 9, highCredits: 12, requiredCredits: 12, balanceCredits: 100, affordable: true },
  },
});

describe('visibility', () => {
  it('renders nothing for a run with no video to change, no supported steps and no history', () => {
    revisionsResult = { data: history({ availability: 'Unavailable', canRevise: false, versions: [], currentVersionId: null }), error: undefined, mutate: vi.fn() };
    mount({ steps: [] });
    expect(screen.queryByTestId('change-video-panel')).toBeNull();
  });

  it('renders nothing until the video history has loaded, or when it failed, with nothing else to show', () => {
    revisionsResult = { data: undefined, error: undefined, mutate: vi.fn() };
    mount({ steps: [] });
    expect(screen.queryByTestId('change-video-panel')).toBeNull();
    cleanup();
    revisionsResult = { data: undefined, error: new Error('boom'), mutate: vi.fn() };
    mount({ steps: [] });
    expect(screen.queryByTestId('change-video-panel')).toBeNull();
  });

  it('shows the video form, the hint and the versions for an animated video', () => {
    mount();
    expect(screen.getByRole('region', { name: 'Change this video' })).toBeTruthy();
    expect(screen.getByTestId('request-changes')).toBeTruthy();
    expect(screen.getByTestId('change-set-hint').textContent).toContain('pick a step in the list');
    expect(screen.getByTestId('revision-history')).toBeTruthy();
    // Nothing to send yet: no estimate and no submit.
    expect(screen.queryByTestId('change-set-submit')).toBeNull();
  });

  it('shows only the other-changes part for a run with supported steps but no video surface', () => {
    revisionsResult = { data: undefined, error: new Error('no video'), mutate: vi.fn() };
    mount({ steps: [capability()] });
    expect(screen.getByTestId('change-video-panel')).toBeTruthy();
    expect(screen.queryByTestId('request-changes')).toBeNull();
    expect(screen.getByTestId('change-set-hint')).toBeTruthy();
  });

  it('shows past requests even when nothing else can be changed', () => {
    revisionsResult = { data: undefined, error: new Error('no video'), mutate: vi.fn() };
    mount({ steps: [], requests: [changeRequest()] });
    expect(screen.getByTestId('change-request-row')).toBeTruthy();
  });

  it('re-reads the video history when the run status changes, not on first render', () => {
    const view = mount({ runStatus: 'Running', runFinished: false });
    expect(revisionsResult.mutate).not.toHaveBeenCalled();
    view.rerender(<MantineProvider><Harness runStatus="Passed" /></MantineProvider>);
    expect(revisionsResult.mutate).toHaveBeenCalledTimes(1);
  });
});

describe('the video on its own', () => {
  it('has no submit until something is written, and counts a trimmed note as one change', () => {
    mount();
    expect(screen.queryByTestId('change-set-submit')).toBeNull();
    write('   ');
    expect(screen.queryByTestId('change-set-submit')).toBeNull();
    write('Brighter intro');
    expect(submitButton().disabled).toBe(false);
    expect(submitButton().textContent).toBe('Request 1 change');
    expect(screen.getByTestId('change-set-count').textContent).toBe('1 change');
  });

  it('routes to the video change with the scenes, keeps the start version and goes to the new run', async () => {
    answerAll();
    mount();
    expect(screen.getByText('Only these scenes (optional)')).toBeTruthy();
    expect(screen.getByText('0–4 s')).toBeTruthy();
    expect(screen.getByText('4–8.5 s')).toBeTruthy();
    fireEvent.click(screen.getByRole('checkbox', { name: /Stats/ }));
    write('  Add better graphics in the intro and the stats section ');
    await submit();

    expect(callsTo('/estimate')).toHaveLength(0);
    expect(callsTo('/change-requests')).toHaveLength(0);
    const [url, init] = callsTo('/revisions')[0] as [string, RequestInit];
    expect(url).toBe('/api/v1/projects/p1/executions/ex1/revisions');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual({
      instruction: 'Add better graphics in the intro and the stats section',
      targets: { scenes: ['Stats'] },
      fromVersionId: 'v1',
      mode: 'Render',
    });
    expect(revisionsResult.mutate).toHaveBeenCalled();
    expect(push).toHaveBeenCalledWith('/projects/p1/workflows/wf-rev/executions/ex-new');
  });

  it('sends no targets when no scene is picked', async () => {
    answerAll();
    mount();
    write('Slower');
    await submit();
    expect(bodiesTo('/revisions')[0]).toEqual({ instruction: 'Slower', targets: null, fromVersionId: 'v1', mode: 'Render' });
  });

  it('"Change from this version" sets the start version and focuses the note', async () => {
    revisionsResult = {
      data: history({ versions: [version(), version({ id: 'v2', versionNumber: 2, instruction: 'Bigger logo', renderStepResultId: 'sr2' })] }),
      error: undefined, mutate: vi.fn(),
    };
    answerAll();
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Change Version 1' }));
    expect(document.activeElement).toBe(textarea());
    write('Slower');
    await submit();
    expect(bodiesTo('/revisions')[0].fromVersionId).toBe('v1');
  });

  it('offers "Start from" only with more than one playable version', () => {
    mount();
    expect(screen.queryByLabelText('Start from')).toBeNull();
    cleanup();
    revisionsResult = { data: history({ versions: [version(), version({ id: 'v2', versionNumber: 2, instruction: 'Bigger logo' })] }), error: undefined, mutate: vi.fn() };
    mount();
    expect((screen.getAllByLabelText('Start from', { selector: 'input' })[0] as HTMLInputElement).value).toBe('Version 1 — Original');
  });

  it('keeps the same cut by default, lists what runs again and sends the choice', async () => {
    revisionsResult = { data: replayHistory(), error: undefined, mutate: vi.fn() };
    answerAll();
    mount();
    expect((screen.getByRole('radio', { name: /Keep the same cut/ }) as HTMLInputElement).checked).toBe(true);
    write('More graphics');
    const replay = screen.getByTestId('request-changes-replay');
    expect(replay.textContent).toContain('Analyze the film');
    expect(replay.textContent).toContain('Edit room deliberation (kept as it is, unless the scenes changed)');
    expect(replay.textContent).toContain('Compile the final cut');
    expect(screen.getByTestId('change-set-estimate').textContent).toBe('About 5.00–12.00 credits.');
    expect(screen.getByTestId('change-set-needs').textContent).toBe('Needs 12 credits available to start; it can cost more.');
    await submit();
    expect(bodiesTo('/revisions')[0].cutPolicy).toBe('KeepCut');
    expect(push).toHaveBeenCalledWith('/projects/p1/workflows/wf-rev/executions/ex-new');
  });

  it('re-editing changes the list and the price and is sent as ReEdit', async () => {
    revisionsResult = { data: replayHistory(), error: undefined, mutate: vi.fn() };
    answerAll();
    mount();
    write('More graphics');
    fireEvent.click(screen.getByRole('radio', { name: /Re-edit the video too/ }));
    expect(screen.getByTestId('request-changes-replay').textContent).not.toContain('unless the scenes changed');
    expect(screen.getByTestId('change-set-estimate').textContent).toBe('About 9.00–12.00 credits.');
    await submit();
    expect(bodiesTo('/revisions')[0].cutPolicy).toBe('ReEdit');
  });

  it('shows no cut choice when the animation is the finished video', () => {
    revisionsResult = { data: history({ replay: { available: false, reason: null, hasEditDecisions: false, steps: [], keepCutEstimate: null, reEditEstimate: null } }), error: undefined, mutate: vi.fn() };
    mount();
    write('x');
    expect(screen.queryByTestId('request-changes-replay')).toBeNull();
    expect(screen.queryByRole('radio')).toBeNull();
  });

  it('shows the cost range and the plain note, and warns when credits may not be enough', () => {
    revisionsResult = { data: history({ estimate: { billingEnabled: true, lowCredits: 4, highCredits: 9, requiredCredits: 9, balanceCredits: 2, affordable: false } }), error: undefined, mutate: vi.fn() };
    mount();
    write('Brighter intro');
    expect(screen.getByTestId('change-set-estimate').textContent).toBe('About 4.00–9.00 credits.');
    expect(screen.getByTestId('change-set-needs').textContent).toBe('Needs 9 credits available to start; it can cost more.');
    expect(screen.getByText(/Usually takes a few minutes/)).toBeTruthy();
    expect(screen.getByTestId('change-set-shortfall').textContent).toContain('You may not have enough credits');
  });

  it('says what a change needs to start, rounded up, and leaves it out when nothing is held', () => {
    revisionsResult = { data: history({ estimate: { billingEnabled: true, lowCredits: 4, highCredits: 9, requiredCredits: 12.4, balanceCredits: 100, affordable: true } }), error: undefined, mutate: vi.fn() };
    mount();
    write('x');
    expect(screen.getByTestId('change-set-needs').textContent).toBe('Needs 13 credits available to start; it can cost more.');
    cleanup();
    revisionsResult = { data: history({ estimate: { billingEnabled: true, lowCredits: 0, highCredits: 0, requiredCredits: 0, balanceCredits: 100, affordable: true } }), error: undefined, mutate: vi.fn() };
    mount();
    write('x');
    expect(screen.queryByTestId('change-set-needs')).toBeNull();
  });

  it('hides the cost line when billing is off', () => {
    revisionsResult = { data: history({ estimate: { billingEnabled: false, lowCredits: 0, highCredits: 0, requiredCredits: 0, balanceCredits: null, affordable: true } }), error: undefined, mutate: vi.fn() };
    mount();
    write('x');
    expect(screen.queryByTestId('change-set-estimate')).toBeNull();
    expect(screen.queryByTestId('change-set-needs')).toBeNull();
  });

  it('never mentions implementation names or internal material', () => {
    revisionsResult = { data: history({ availability: 'Recoverable', currentVersionId: null, versions: [] }), error: undefined, mutate: vi.fn() };
    mount();
    write('x');
    expect(document.body.textContent ?? '').not.toMatch(/remotion|sandbox|agent|workspace|token|reviser|workflow engine|ceo/i);
  });

  it.each([
    [402, { code: 'INSUFFICIENT_CREDITS', message: 'This change needs 9 credits and you have 2.', required: 9, balance: 2 }, 'Not enough credits', 'This change needs 9 credits and you have 2.'],
    [409, { code: 'WORKSPACE_UNAVAILABLE', message: 'This video can no longer be changed.' }, 'This video cannot be changed', 'This video can no longer be changed.'],
    [409, { code: 'RUN_NOT_FINISHED', message: 'Wait for the run to finish.' }, 'This run has not finished yet', 'Wait for the run to finish.'],
    [429, { code: 'REVISION_RATE_LIMITED', message: 'Too many changes this hour.', retryAfterSeconds: 150 }, 'Too many change requests', 'Too many changes this hour. You can try again in about 3 minutes.'],
    [403, { code: 'EMAIL_NOT_VERIFIED', message: 'Confirm your email first.' }, 'Confirm your email address', 'Confirm your email first.'],
  ])('maps a %i refusal of the video route to a friendly alert', async (status, body, title, message) => {
    apiFetch.mockRejectedValueOnce(new ApiError(body.message, status, [], body));
    mount();
    write('Brighter intro');
    await submit();
    const alert = screen.getByTestId('change-set-error');
    expect(alert.textContent).toContain(title);
    expect(alert.textContent).toContain(message);
    expect(push).not.toHaveBeenCalled();
    // The form stays usable for another try.
    expect(submitButton().disabled).toBe(false);
  });

  it('explains a run made before versions were saved', () => {
    revisionsResult = { data: history({ availability: 'Recoverable', currentVersionId: null, versions: [] }), error: undefined, mutate: vi.fn() };
    mount();
    expect(screen.getByText(/made before changes were saved automatically/)).toBeTruthy();
    expect(textarea().disabled).toBe(false);
  });

  it('disables the form and shows the reason when the video cannot be changed', () => {
    revisionsResult = { data: history({ availability: 'Unavailable', canRevise: false, unavailableReason: 'This video is too old to change.' }), error: undefined, mutate: vi.fn() };
    mount();
    expect(screen.getByTestId('request-changes-disabled').textContent).toBe('This video is too old to change.');
    expect(textarea().disabled).toBe(true);
  });

  it('asks to wait while the run is still going', () => {
    revisionsResult = { data: history({ canRevise: false }), error: undefined, mutate: vi.fn() };
    mount({ runFinished: false });
    expect(screen.getByTestId('request-changes-disabled').textContent).toBe('You can request changes once this run has finished.');
    expect(textarea().disabled).toBe(true);
  });

  it('shows a counter near the length limit', () => {
    mount();
    expect(screen.queryByText(/\/ 4,000 characters/)).toBeNull();
    write('a'.repeat(3700));
    expect(screen.getByText('3,700 / 4,000 characters')).toBeTruthy();
    expect(textarea().maxLength).toBe(4000);
  });

  it('the film step shortcut reaches the note field', () => {
    mount();
    fireEvent.click(screen.getByText('film-shortcut'));
    expect(document.activeElement).toBe(textarea());
  });
});

describe('other changes only', () => {
  it('lists the pending step with edit and remove, and routes to one change request without a video target', async () => {
    answerAll();
    mount();
    fireEvent.click(screen.getByText('add-step'));
    const item = screen.getByTestId('change-set-entry-3');
    expect(item.textContent).toContain('Narration');
    expect(within(screen.getByRole('list', { name: 'Other changes' })).getAllByRole('listitem')).toHaveLength(1);
    expect(screen.getByTestId('change-set-edit-3')).toBeTruthy();
    expect(screen.getByTestId('change-set-remove-3')).toBeTruthy();
    expect(submitButton().textContent).toBe('Request 1 change');

    await screen.findByTestId('change-set-estimate', undefined, { timeout: 3000 });
    expect(bodiesTo('/estimate').at(-1)).toEqual({ stepResultId: 'sr3', itemIds: ['vo1'], overrides: { vo1: { speed: 1.5 } } });
    await submit();
    await waitFor(() => expect(push).toHaveBeenCalledWith('/projects/p1/workflows/wf9/executions/ex9'));
    expect(bodiesTo('/change-requests')).toEqual([{ stepResultId: 'sr3', itemIds: ['vo1'], overrides: { vo1: { speed: 1.5 } } }]);
    expect(callsTo('/revisions')).toHaveLength(0);
    // The set is cleared once the request was created.
    expect(screen.queryByTestId('change-set-submit')).toBeNull();
  });

  it('removes the entry', () => {
    mount();
    fireEvent.click(screen.getByText('add-step'));
    fireEvent.click(screen.getByTestId('change-set-remove-3'));
    expect(screen.queryByTestId('change-set-entry-3')).toBeNull();
    expect(screen.queryByTestId('change-set-submit')).toBeNull();
  });
});

describe('the video together with other changes', () => {
  it('sends one change request with the video as a target, and not the scenes, cut or start version', async () => {
    revisionsResult = { data: replayHistory(), error: undefined, mutate: vi.fn() };
    answerAll();
    mount();
    fireEvent.click(screen.getByRole('checkbox', { name: /Stats/ }));
    fireEvent.click(screen.getByRole('radio', { name: /Re-edit the video too/ }));
    fireEvent.click(screen.getByText('add-step'));
    write('Make the title bigger');

    expect(screen.getByTestId('request-changes-alone-note').textContent).toBe('These options apply when the video is changed on its own.');
    expect((screen.getByRole('checkbox', { name: /Stats/ }) as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByRole('radio', { name: /Re-edit the video too/ }) as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByRole('radio', { name: /Keep the same cut/ }) as HTMLInputElement).disabled).toBe(true);
    expect(submitButton().textContent).toBe('Request 2 changes');

    await screen.findByTestId('change-set-estimate', undefined, { timeout: 3000 });
    const body = {
      targets: [
        { stepResultId: 'sr3', itemIds: ['vo1'], overrides: { vo1: { speed: 1.5 } } },
        { stepOrder: 7, instruction: 'Make the title bigger' },
      ],
    };
    expect(bodiesTo('/estimate').at(-1)).toEqual(body);
    await submit();
    await waitFor(() => expect(push).toHaveBeenCalledWith('/projects/p1/workflows/wf9/executions/ex9'));
    expect(bodiesTo('/change-requests')).toEqual([body]);
    expect(callsTo('/revisions')).toHaveLength(0);
  });

  it('enables the options again when the other changes are removed', () => {
    mount();
    fireEvent.click(screen.getByText('add-step'));
    write('Bigger title');
    expect((screen.getByRole('checkbox', { name: /Stats/ }) as HTMLInputElement).disabled).toBe(true);
    fireEvent.click(screen.getByTestId('change-set-remove-3'));
    expect((screen.getByRole('checkbox', { name: /Stats/ }) as HTMLInputElement).disabled).toBe(false);
    expect(screen.queryByTestId('request-changes-alone-note')).toBeNull();
  });

  it('refuses to send when the video cannot be combined', async () => {
    answerAll();
    mount({ steps: [capability(), film({ combinable: false })] });
    fireEvent.click(screen.getByText('add-step'));
    write('Bigger title');
    expect(screen.getByTestId('change-set-film-alone')).toBeTruthy();
    expect(submitButton().disabled).toBe(true);
    await new Promise((r) => setTimeout(r, 700));
    expect(callsTo('/estimate')).toHaveLength(0);
  });

  it('maps a refusal of the change-request route, and a 400 path to the video note', async () => {
    apiFetch.mockImplementation((url: string) => {
      if (url.endsWith('/estimate')) return Promise.resolve(preview);
      return Promise.reject(new ApiError('Only a step that finished can be changed.', 409, [], { code: 'STEP_NOT_COMPLETED', message: 'Only a step that finished can be changed.' }));
    });
    mount();
    fireEvent.click(screen.getByText('add-step'));
    write('Bigger title');
    await screen.findByTestId('change-set-estimate', undefined, { timeout: 3000 });
    await submit();
    const alert = await screen.findByTestId('change-set-error');
    expect(alert.textContent).toContain('This step did not finish');
    expect(push).not.toHaveBeenCalled();

    cleanup();
    apiFetch.mockReset();
    apiFetch.mockImplementation((url: string) => (url.endsWith('/estimate')
      ? Promise.reject(new ApiError('Bad', 400, ['Too vague'], { message: 'Bad', errors: [{ path: 'targets[1].instruction', message: 'Too vague' }] }))
      : Promise.resolve(preview)));
    mount();
    fireEvent.click(screen.getByText('add-step'));
    write('Bigger');
    await waitFor(() => expect(textarea().getAttribute('aria-invalid')).toBe('true'), { timeout: 3000 });
    expect(screen.getByText('Too vague')).toBeTruthy();
    expect(screen.queryByTestId('change-set-error')).toBeNull();
  });
});

describe('limits and announcements', () => {
  it('locks a new video note when the steps already fill the request, and says why', () => {
    mount({ max: 1 });
    fireEvent.click(screen.getByText('add-step'));
    expect(textarea().disabled).toBe(true);
    expect(screen.getByTestId('request-changes-locked').textContent).toContain('up to 1 changes');
  });

  it('does not send the video note with other changes when the video cannot be changed', async () => {
    revisionsResult = { data: history({ canRevise: false }), error: undefined, mutate: vi.fn() };
    mount();
    fireEvent.click(screen.getByText('add-step'));
    fireEvent.change(textarea(), { target: { value: 'x' } });
    expect(submitButton().disabled).toBe(true);
    expect(screen.getByTestId('change-set-film-alone').textContent).toContain('cannot be changed right now');
  });

  it('announces the price in a polite status region', async () => {
    answerAll();
    mount();
    fireEvent.click(screen.getByText('add-step'));
    const estimate = await screen.findByTestId('change-set-estimate', undefined, { timeout: 3000 });
    expect(estimate.closest('[role="status"]')?.getAttribute('aria-live')).toBe('polite');
  });
});

describe('on a phone', () => {
  it('keeps the video form in the page and opens the review in a sheet from a bottom bar', async () => {
    phone = true;
    answerAll();
    mount();
    expect(screen.queryByTestId('change-set-bar')).toBeNull();
    write('Bigger title');
    expect(screen.getByTestId('change-set-bar').textContent).toContain('1 change');
    expect(screen.queryByTestId('change-set-submit')).toBeNull();
    fireEvent.click(screen.getByTestId('change-set-sheet-open'));
    const sheet = await screen.findByRole('dialog', { name: 'Your changes' });
    expect(within(sheet).getByTestId('change-set-estimate')).toBeTruthy();
    await act(async () => { fireEvent.click(within(sheet).getByTestId('change-set-submit')); });
    expect(bodiesTo('/revisions')).toHaveLength(1);
  });
});

describe('the merged history', () => {
  it('lists a combined run once', () => {
    revisionsResult = {
      data: history({ revisions: [revision({ id: 'rev7', executionId: 'ex-both', status: 'Running', instruction: 'Bigger title' })] }),
      error: undefined, mutate: vi.fn(),
    };
    mount({ requests: [changeRequest({ executionId: 'ex-both', workflowDefinitionId: 'wf-both', status: 'Running' })] });
    expect(screen.getAllByTestId('change-request-row')).toHaveLength(1);
    expect(screen.queryByTestId('revision-request')).toBeNull();
  });
});
