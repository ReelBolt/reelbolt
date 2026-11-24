// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import type { StepChangeAppInputField, StepChangeAppInputs, StepChangeBriefField } from '@/lib/types/change-requests';
import { AppInputChangeModal } from './AppInputChangeModal';
import { canChangeFields, changedBrief, changedInputs, picksAnotherBranch, runSettingValues, runValues } from './app-input-change';

const push = vi.fn();
vi.mock('next/navigation', () => ({ useRouter: () => ({ push }) }));
const apiFetch = vi.fn();
vi.mock('@/lib/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/client')>();
  return { ...actual, apiFetch: (...a: unknown[]) => apiFetch(...a) };
});

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((q: string) => ({
      matches: false, media: q, onchange: null, addListener: vi.fn(), removeListener: vi.fn(),
      addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  Object.defineProperty(document, 'fonts', { configurable: true, value: { addEventListener: vi.fn(), removeEventListener: vi.fn() } });
});
beforeEach(() => { apiFetch.mockReset(); push.mockReset(); });
afterEach(cleanup);

const prompt: StepChangeAppInputField = {
  inputId: 'whatTheyDo', label: 'What they do', type: 'shortText', value: 'Pours latte art', valueText: 'Pours latte art',
  supported: true, notSupportedReason: null, reasonCode: null, usedBy: ['Make the spokesperson clip'], decidesBranch: false,
  branchChangingValues: [], input: { kind: 'input', id: 'whatTheyDo', type: 'shortText', label: 'What they do' },
};
const shape: StepChangeAppInputField = {
  inputId: 'shape', label: 'Shape', type: 'choice', value: '9:16', valueText: 'Vertical', supported: true, notSupportedReason: null,
  reasonCode: null, usedBy: ['Make the spokesperson clip'], decidesBranch: true, branchChangingValues: ['1:1'],
  input: { kind: 'input', id: 'shape', type: 'choice', label: 'Shape', options: [{ value: '9:16', label: 'Vertical' }, { value: '16:9', label: 'Wide' }, { value: '1:1', label: 'Square' }] },
};
const lite: StepChangeAppInputField = {
  inputId: 'liteMode', label: 'Lite mode', type: 'toggle', value: false, valueText: 'no', supported: false,
  notSupportedReason: 'it picks another path through the app, so run it again instead', reasonCode: 'INPUT_CHANGES_BRANCH',
  usedBy: [], decidesBranch: true, branchChangingValues: [true], input: { kind: 'input', id: 'liteMode', type: 'toggle', label: 'Lite mode' },
};
const app: StepChangeAppInputs = { workflowId: 'wf1', fields: [prompt, shape, lite] };

const language: StepChangeBriefField = {
  key: 'brief:language', kind: 'brief', field: 'language', role: null, label: 'Language', value: null, valueText: '(automatic)',
  supported: true, notSupportedReason: null, reasonCode: null, usedBy: ['Write the script'], input: { kind: 'brief', field: 'language' },
};
const notes: StepChangeBriefField = {
  key: 'brief:notes', kind: 'notes', field: null, role: null, label: 'Anything else', value: 'Keep it warm.', valueText: 'Keep it warm.',
  supported: true, notSupportedReason: null, reasonCode: null, usedBy: ['Write the script'], input: { kind: 'notes' },
};
const clips: StepChangeBriefField = {
  key: 'brief:files:Video', kind: 'files', field: null, role: 'Video', label: 'Your clips', value: ['f1'], valueText: 'a.mp4',
  supported: true, notSupportedReason: null, reasonCode: null, usedBy: ['Analyse the footage'], input: { kind: 'files', role: 'video' },
};
const format: StepChangeBriefField = {
  key: 'brief:format', kind: 'brief', field: 'format', role: null, label: 'Shape', value: null, valueText: '',
  supported: false, notSupportedReason: 'it changes the whole video, so run it again instead', reasonCode: 'BRIEF_FIELD_NOT_CHANGEABLE', usedBy: [],
  input: { kind: 'brief', field: 'format' },
};

const preview = {
  estimate: { billingEnabled: true, lowCredits: 2, highCredits: 5, requiredCredits: 5, balanceCredits: 50, affordable: true },
  replay: { steps: [{ stepOrder: 1, label: 'Lite mode?', disposition: 'Reuse', reason: '' }, { stepOrder: 6, label: 'Make the spokesperson clip', disposition: 'Revise', reason: '' }] },
  inputChanges: [{ inputId: 'whatTheyDo', label: 'What they do', type: 'shortText', before: 'Pours latte art', after: 'Waves hello', value: 'Waves hello' }],
};
const bodies = (suffix: string) => apiFetch.mock.calls.filter((c) => String(c[0]).endsWith(suffix)).map((c) => JSON.parse((c[1] as { body: string }).body));

function renderModal(onRunAgain = vi.fn()) {
  render(
    <MantineProvider>
      <AppInputChangeModal opened onClose={vi.fn()} projectId="p1" workflowId="wf1" executionId="ex1" appInputs={app} onRunAgain={onRunAgain} phone={false} />
    </MantineProvider>,
  );
  return onRunAgain;
}

describe('app-input change helpers', () => {
  it('sends only the changed supported fields, trimmed, an emptied one as null', () => {
    const values = { ...runValues(app.fields), whatTheyDo: '  Waves hello ', liteMode: true };
    expect(changedInputs(app.fields, values)).toEqual({ whatTheyDo: 'Waves hello' });
    expect(changedInputs(app.fields, { ...runValues(app.fields), whatTheyDo: '' })).toEqual({ whatTheyDo: null });
    expect(changedInputs(app.fields, runValues(app.fields))).toEqual({});
  });

  it('knows the values that pick another branch and whether any field can change', () => {
    expect(picksAnotherBranch(shape, '1:1')).toBe(true);
    expect(picksAnotherBranch(shape, '16:9')).toBe(false);
    expect(canChangeFields(app)).toBe(true);
    expect(canChangeFields({ workflowId: null, fields: [lite] })).toBe(false);
    expect(canChangeFields(null)).toBe(false);
    expect(canChangeFields({ workflowId: null, fields: [lite], briefFields: [language] })).toBe(true);
  });

  it('F3: sends only the changed run settings in the brief shape', () => {
    const fields = [language, notes, clips, format];
    const own = runSettingValues(fields);
    expect(changedBrief(fields, own)).toEqual({});
    expect(changedBrief(fields, { ...own, 'brief:language': 'it', 'brief:notes': ' Make it cosy ', 'brief:files:Video': ['f2'], 'brief:format': '9:16' }))
      .toEqual({ language: 'it', notes: 'Make it cosy', sourceFiles: [{ role: 'Video', fileIds: ['f2'] }] });
  });
});

describe('AppInputChangeModal', () => {
  it('prices the changed field, says what is made again and starts the linked run', async () => {
    apiFetch.mockImplementation((url: string) => Promise.resolve(url.endsWith('/estimate') ? preview : { executionId: 'ex9', workflowDefinitionId: 'wf9' }));
    renderModal();

    expect(screen.getByText(/Lite mode: it picks another path/)).toBeTruthy();
    fireEvent.change(screen.getByLabelText(/What they do/), { target: { value: 'Waves hello' } });

    await waitFor(() => expect(screen.getByTestId('app-input-change-preview')).toBeTruthy(), { timeout: 2000 });
    expect(bodies('/change-requests/estimate')[0]).toEqual({ inputs: { whatTheyDo: 'Waves hello' } });
    expect(screen.getByText('What they do: Pours latte art → Waves hello')).toBeTruthy();
    expect(screen.getByText('Made again: Make the spokesperson clip')).toBeTruthy();

    fireEvent.click(screen.getByTestId('app-input-change-submit'));
    await waitFor(() => expect(push).toHaveBeenCalledWith('/projects/p1/workflows/wf9/executions/ex9'));
    expect(bodies('/change-requests').at(-1)).toEqual({ inputs: { whatTheyDo: 'Waves hello' } });
  });

  it('F3: run settings are edited beside the fields and, in set mode, join the change set instead of starting a run', async () => {
    apiFetch.mockImplementation((url: string) => Promise.resolve(url.endsWith('/estimate')
      ? { ...preview, inputChanges: [{ inputId: 'brief:notes', label: 'Anything else', type: 'brief', before: 'Keep it warm.', after: 'Make it cosy', value: null }] }
      : []));
    const onAddToSet = vi.fn();
    render(
      <MantineProvider>
        <AppInputChangeModal opened onClose={vi.fn()} projectId="p1" workflowId="wf1" executionId="ex1"
          appInputs={{ ...app, briefFields: [notes, format] }} phone={false} onAddToSet={onAddToSet} />
      </MantineProvider>,
    );

    expect(screen.getByTestId('app-input-change-setting-brief:notes')).toBeTruthy();
    expect(screen.getByText(/Shape: it changes the whole video/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Change other things too' })).toBeNull();
    fireEvent.change(screen.getByLabelText(/Anything else/), { target: { value: 'Make it cosy' } });

    await waitFor(() => expect(screen.getByTestId('app-input-change-preview')).toBeTruthy(), { timeout: 2000 });
    expect(bodies('/change-requests/estimate')[0]).toEqual({ brief: { notes: 'Make it cosy' } });
    fireEvent.click(screen.getByTestId('app-input-change-submit'));
    expect(onAddToSet).toHaveBeenCalledWith({ brief: { notes: 'Make it cosy' }, lines: ['Anything else: Keep it warm. → Make it cosy'] });
    expect(push).not.toHaveBeenCalled();
  });

  it('offers running the app again when the server says the change picks another branch', async () => {
    apiFetch.mockRejectedValue(new ApiError('This change picks another path through the app.', 409, [], { code: 'INPUT_CHANGES_BRANCH' }));
    const onRunAgain = renderModal();

    fireEvent.change(screen.getByLabelText(/What they do/), { target: { value: 'Waves hello' } });
    await waitFor(() => expect(screen.getByTestId('app-input-change-refusal')).toBeTruthy(), { timeout: 2000 });
    fireEvent.click(screen.getByRole('button', { name: 'Run it again instead' }));
    expect(onRunAgain).toHaveBeenCalled();
  });
});
