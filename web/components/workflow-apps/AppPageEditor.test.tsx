// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { useEffect, useState } from 'react';
import { MantineProvider } from '@mantine/core';
import type { AppBindableField, AppManifest, AppManifestInput, AppMediaEgress, WorkflowAppResponse } from '@/lib/types/workflow';
import type { StepData } from '@/components/workflows/WorkflowStepList';

const app = vi.hoisted(() => ({ state: { data: undefined, error: undefined, isLoading: false } as {
  data: WorkflowAppResponse | undefined; error: unknown; isLoading: boolean } }));
vi.mock('@/lib/hooks/use-workflow-apps', () => ({ useWorkflowApp: () => app.state }));
const readable = vi.hoisted(() => vi.fn());
vi.mock('@/lib/workflow-apps/readable-fields', () => ({ readableBriefFields: readable }));
const describeChanges = vi.hoisted(() => vi.fn().mockResolvedValue({ lines: [] }));
vi.mock('@/lib/api/workflows', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/workflows')>();
  return { ...actual, describeAppChanges: describeChanges };
});
const swr = vi.hoisted(() => ({ keys: [] as (string | null)[], data: undefined as unknown }));
vi.mock('swr', () => ({
  default: (key: string | null) => {
    swr.keys.push(key);
    return { data: key ? swr.data : undefined, error: undefined, isLoading: false, mutate: vi.fn() };
  },
}));

import { AppInputsPanel } from './AppInputsPanel';
import { AppBuilderContext } from './AppBuilderContext';
import { StepAppFields } from '@/components/workflows/StepAppFields';
import { ConditionalStepConfig } from '@/components/workflows/ConditionalStepConfig';

beforeAll(() => {
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: (query: string) => ({
      matches: false, media: query, onchange: null,
      addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => false,
    }),
  });
  Element.prototype.scrollIntoView = () => {};
  // Mantine's autosize textarea listens for web fonts loading.
  Object.defineProperty(document, 'fonts', {
    configurable: true,
    value: { addEventListener: vi.fn(), removeEventListener: vi.fn(), ready: Promise.resolve() },
  });
});

const BINDABLE: Record<string, AppBindableField[]> = {
  VideoGenerate: [
    { path: 'inlinePrompt', mode: 'placeholder', types: ['shortText', 'longText'], label: 'Video prompt', maxLength: 7000 },
    { path: 'ratio', mode: 'value', types: ['shape', 'choice'], label: 'Shape', choices: ['9:16', '1:1', '16:9', '4:5'] },
    { path: '@model', mode: 'value', types: ['model'], label: 'Model', capabilities: ['VideoGeneration'] },
    { path: 'takes', mode: 'value', types: ['number', 'choice'], label: 'Takes', min: 1, max: 4, valueKind: 'number' },
  ],
};

function videoStep(config: Record<string, unknown>): StepData {
  return {
    id: 's1', label: 'Make the clip', agentDefinitionId: 'a', stepType: 'VideoGenerate', conditionExpression: null, loopSourceExpression: null,
    loopTargetStepOrder: null, maxIterations: 3, minScore: null, inputMappingJson: null, agentInputContextMode: null, cacheMode: null,
    selectedPriorStepOrders: [], trueBranchStepOrder: null, falseBranchStepOrder: null, parallelAgentIds: [], extractConfig: null,
    videoAnalyzeConfig: null, videoCompileConfig: null, videoGenerateConfig: config as never, voiceoverConfig: null, thumbnailGenerateConfig: null,
  };
}

const latest: { manifest?: AppManifest; steps?: StepData[] } = {};

function Harness({ manifest: initial, steps: initialSteps, panel = true, errors = null, onSave = vi.fn().mockResolvedValue(true), mediaEgress = null }: {
  manifest: AppManifest; steps: StepData[]; panel?: boolean; errors?: string[] | null; onSave?: (m: AppManifest) => Promise<boolean>;
  mediaEgress?: AppMediaEgress | null;
}) {
  const [manifest, setManifest] = useState(initial);
  const [steps, setSteps] = useState(initialSteps);
  useEffect(() => {
    latest.manifest = manifest;
    latest.steps = steps;
  });
  return (
    <MantineProvider env="test">
      <AppBuilderContext.Provider value={{ manifest, setManifest, steps, setSteps, bindableFields: BINDABLE, hasExplicitManifest: true, mediaEgress }}>
        {panel && (
          <AppInputsPanel projectId="p1" workflowId="w1" draftSteps={[]} saving={false} errors={errors} onSave={onSave} onReset={vi.fn()} />
        )}
        <StepAppFields step={steps[0]} onChange={(u) => setSteps((s) => [{ ...s[0], ...u }, ...s.slice(1)])} />
      </AppBuilderContext.Provider>
    </MantineProvider>
  );
}

function serve(manifest: AppManifest) {
  app.state = {
    data: { workflowId: 'w1', name: 'Clip', manifest, hasExplicitManifest: true, costClass: 'Uses credits', source: 'custom', bindableFields: BINDABLE },
    error: undefined,
    isLoading: false,
  };
}

const BOUND: AppManifest = {
  version: 2,
  inputs: [
    { kind: 'brief', field: 'language', label: 'Language' },
    { kind: 'input', id: 'clipShape', type: 'shape', label: 'Shape', choices: ['9:16', '16:9'], default: '9:16' },
  ],
};

afterEach(() => { cleanup(); vi.clearAllMocks(); swr.keys = []; swr.data = undefined; describeChanges.mockReset(); describeChanges.mockResolvedValue({ lines: [] }); });

describe('App page editor (app inputs on)', () => {
  it('lists each field with where it is used, beside a preview of the page', async () => {
    readable.mockResolvedValue([]);
    serve(BOUND);
    render(<Harness manifest={BOUND} steps={[videoStep({ ratio: '16:9', appInputs: { ratio: 'clipShape' } })]} />);
    const row = screen.getByTestId('app-field-in:clipShape');
    expect(within(row).getByText('Step 1 · Shape')).toBeTruthy();
    expect(within(row).getAllByText('Shape').length).toBeGreaterThan(0);
    expect(screen.getByTestId('app-page-preview')).toBeTruthy();
    // The old panel's "Shown in the app" switches are gone: the creator removes a field instead.
    expect(screen.queryByRole('switch', { name: /Shown in the app/ })).toBeNull();
    await act(async () => {});
  });

  it('adds a new field, edits it and saves the page as version 2', async () => {
    readable.mockResolvedValue([]);
    serve(BOUND);
    const onSave = vi.fn().mockResolvedValue(true);
    render(<Harness manifest={BOUND} steps={[videoStep({ ratio: '16:9' })]} onSave={onSave} />);
    fireEvent.click(screen.getByRole('button', { name: 'Add a field' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Short text' }));
    fireEvent.change(screen.getByLabelText('Name on the page for Short answer'), { target: { value: 'Call to action' } });
    fireEvent.click(screen.getByRole('switch', { name: 'Must be filled' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save the app page' }));

    const saved = onSave.mock.calls[0][0] as AppManifest;
    expect(saved.version).toBe(2);
    expect(saved.inputs?.[2]).toEqual({ kind: 'input', id: 'shortAnswer', type: 'shortText', label: 'Call to action', maxLength: 120, required: true });
    await act(async () => {});
  });

  it('removes a field and unbinds it from the step that used it', async () => {
    readable.mockResolvedValue([]);
    serve(BOUND);
    render(<Harness manifest={BOUND} steps={[videoStep({ ratio: '16:9', appInputs: { ratio: 'clipShape' } })]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Remove Shape' }));
    expect(latest.manifest?.inputs?.map((i) => i.field ?? i.id)).toEqual(['language']);
    expect(latest.steps?.[0].videoGenerateConfig).toEqual({ ratio: '16:9' });
    await act(async () => {});
  });

  it('shows the server\'s refusal under the field it names', async () => {
    readable.mockResolvedValue([]);
    serve(BOUND);
    render(<Harness manifest={BOUND} steps={[videoStep({})]} errors={['The input "Shape" is not used by any step, the request template or a condition. Use it or remove it.']} />);
    const row = screen.getByTestId('app-field-in:clipShape');
    expect(within(row).getByRole('alert').textContent).toContain('is not used by any step');
    expect(screen.queryByTestId('app-inputs-error')).toBeNull();
    await act(async () => {});
  });

  it('lets the creator narrow the models and pick the recommended one', async () => {
    readable.mockResolvedValue([]);
    const manifest: AppManifest = { version: 2, inputs: [{ kind: 'input', id: 'videoModel', type: 'model', label: 'Video model', capability: 'VideoGeneration' }] };
    serve(manifest);
    swr.data = {
      inputId: 'videoModel', capability: 'VideoGeneration',
      options: [
        { id: 'm1', label: 'Hailuo', recommended: true, available: true, costIndex: 1 },
        { id: 'm2', label: 'Kling', recommended: false, available: true, costIndex: 2 },
        { id: 'm3', label: 'Lip sync', recommended: false, available: false, unavailableCode: 'wrongJob' },
      ],
    };
    render(<Harness manifest={manifest} steps={[videoStep({ appInputs: { '@model': 'videoModel' } })]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
    expect(swr.keys).toContain('/api/v1/projects/p1/workflows/w1/app/inputs/videoModel/options?all=true');
    fireEvent.click(screen.getByRole('switch', { name: /^People may pick any model/ }));
    expect(latest.manifest?.inputs?.[0].allowed).toEqual(['m1', 'm2']);
    fireEvent.click(screen.getByRole('checkbox', { name: /Kling/ }));
    expect(latest.manifest?.inputs?.[0].allowed).toEqual(['m1']);
    expect((screen.getByRole('checkbox', { name: /Lip sync/ }) as HTMLInputElement).disabled).toBe(true);
    await act(async () => {});
  });
});

describe('App page editor: what changes, drag to reorder, the kind of a field (F3)', () => {
  it('shows what saving the draft changes, as the server words it', async () => {
    readable.mockResolvedValue([]);
    describeChanges.mockResolvedValue({ lines: ['Adds the field “Who it is for” (short text) to the app page.'] });
    serve(BOUND);
    render(<Harness manifest={BOUND} steps={[videoStep({ ratio: '16:9' })]} />);
    await waitFor(() => expect(screen.getByTestId('app-page-changes')).toBeTruthy(), { timeout: 2000 });
    expect(screen.getByText('What changes when you save')).toBeTruthy();
    expect(screen.getByText('Adds the field “Who it is for” (short text) to the app page.')).toBeTruthy();
    expect(describeChanges.mock.calls[0][0]).toBe('p1');
    expect(describeChanges.mock.calls[0][2].appManifest.inputs).toHaveLength(2);
  });

  it('drags a field to another place, and keeps the arrows', async () => {
    readable.mockResolvedValue([]);
    describeChanges.mockResolvedValue({ lines: [] });
    serve(BOUND);
    render(<Harness manifest={BOUND} steps={[videoStep({ ratio: '16:9' })]} />);
    expect(screen.getByRole('button', { name: /Move Shape up/ })).toBeTruthy();
    fireEvent.dragStart(screen.getByTestId('app-field-drag-1'));
    fireEvent.dragOver(screen.getByTestId('app-field-item-0'));
    fireEvent.drop(screen.getByTestId('app-field-item-0'));
    await waitFor(() => expect(latest.manifest?.inputs?.map((i) => i.id ?? i.field)).toEqual(['clipShape', 'language']));
  });

  it('offers another kind only where the answer keeps its shape', async () => {
    readable.mockResolvedValue([]);
    describeChanges.mockResolvedValue({ lines: [] });
    const page: AppManifest = { version: 2, inputs: [{ kind: 'input', id: 'who', type: 'shortText', label: 'Who' }, ...BOUND.inputs!.slice(1)] };
    serve(page);
    render(<Harness manifest={page} steps={[videoStep({ ratio: '16:9' })]} />);
    fireEvent.click(within(screen.getByTestId('app-field-in:who')).getByRole('button', { name: 'Edit' }));
    expect(screen.getByTestId('app-input-type')).toBeTruthy();
    fireEvent.click(within(screen.getByTestId('app-field-in:who')).getByRole('button', { name: 'Done' }));
    fireEvent.click(within(screen.getByTestId('app-field-in:clipShape')).getByRole('button', { name: 'Edit' }));
    expect(screen.getByTestId('app-input-type-fixed').textContent).toContain('Kind: Shape');
    await act(async () => {});
  });
});

describe('App fields in a step: what each answer sets (F1)', () => {
  it('lets a switch fill a number setting with a value per position and keeps the other entries', async () => {
    readable.mockResolvedValue([]);
    const manifest: AppManifest = { version: 2, inputs: [{ kind: 'input', id: 'twoTakes', type: 'toggle', label: 'Two takes' }] };
    serve(manifest);
    render(<Harness manifest={manifest} panel={false} steps={[videoStep({ takes: 1, appInputs: { ratio: 'clipShape' } })]} />);
    const fields = screen.getByTestId('step-app-fields');
    const buttons = within(fields).getAllByRole('button', { name: 'Use a field for this setting' });
    fireEvent.click(buttons[buttons.length - 1]);
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Use “Two takes”' }));

    // The switch starts by writing the step's own value when on, and leaves it when off: the run is unchanged.
    expect(latest.steps?.[0].videoGenerateConfig).toEqual({ takes: 1, appInputs: { ratio: 'clipShape', takes: { input: 'twoTakes', values: { true: 1 } } } });
    const answers = screen.getByTestId('answer-values');
    fireEvent.change(within(answers).getByLabelText('When “Two takes” is on'), { target: { value: '2' } });
    fireEvent.change(within(answers).getByLabelText('When “Two takes” is off'), { target: { value: '1' } });
    expect(latest.steps?.[0].videoGenerateConfig).toEqual({ takes: 1, appInputs: { ratio: 'clipShape', takes: { input: 'twoTakes', values: { true: 2, false: 1 } } } });

    fireEvent.click(screen.getByRole('button', { name: 'Stop using the field' }));
    expect(latest.steps?.[0].videoGenerateConfig).toEqual({ takes: 1, appInputs: { ratio: 'clipShape' } });
    await act(async () => {});
  });
});

describe('App fields in a step', () => {
  it('makes a new field from a setting and binds it, then stops using it', async () => {
    readable.mockResolvedValue([]);
    const manifest: AppManifest = { version: 1, inputs: [] };
    serve(manifest);
    render(<Harness manifest={manifest} panel={false} steps={[videoStep({ ratio: '16:9' })]} />);
    const fields = screen.getByTestId('step-app-fields');
    const buttons = within(fields).getAllByRole('button', { name: 'Use a field for this setting' });
    fireEvent.click(buttons[1]);
    fireEvent.click(await screen.findByRole('menuitem', { name: 'A new field' }));

    expect(latest.manifest?.version).toBe(2);
    expect(latest.manifest?.inputs).toEqual([{ kind: 'input', id: 'shape', label: 'Shape', type: 'shape', choices: ['9:16', '1:1', '16:9', '4:5'], default: '16:9' }]);
    expect(latest.steps?.[0].videoGenerateConfig).toEqual({ ratio: '16:9', appInputs: { ratio: 'shape' } });
    expect(screen.getByText('From the app: Shape')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Stop using the field' }));
    expect(latest.steps?.[0].videoGenerateConfig).toEqual({ ratio: '16:9' });
    await act(async () => {});
  });

  it('puts a field inside a text setting, keeping the old words as its example', async () => {
    readable.mockResolvedValue([]);
    const manifest: AppManifest = { version: 1, inputs: [] };
    serve(manifest);
    render(<Harness manifest={manifest} panel={false} steps={[videoStep({ inlinePrompt: 'A barista pours latte art' })]} />);
    fireEvent.click(within(screen.getByTestId('step-app-fields')).getAllByRole('button', { name: 'Use a field for this setting' })[0]);
    fireEvent.click(await screen.findByRole('menuitem', { name: 'A new field' }));
    expect(latest.steps?.[0].videoGenerateConfig).toEqual({ inlinePrompt: '{{input.videoPrompt}}' });
    expect(latest.manifest?.inputs?.[0]).toMatchObject({ id: 'videoPrompt', type: 'longText', example: 'A barista pours latte art' });
    // Now the text is edited with its fields; "Stop using fields" puts the example back.
    fireEvent.click(screen.getByRole('button', { name: 'Stop using fields' }));
    expect(latest.steps?.[0].videoGenerateConfig).toEqual({ inlinePrompt: 'A barista pours latte art' });
    await act(async () => {});
  });

  it('renders nothing with app inputs off', () => {
    render(
      <MantineProvider env="test">
        <StepAppFields step={videoStep({ ratio: '16:9' })} onChange={vi.fn()} />
      </MantineProvider>,
    );
    expect(screen.queryByTestId('step-app-fields')).toBeNull();
  });
});

describe('A condition with app inputs', () => {
  const LITE: AppManifest = { version: 2, inputs: [{ kind: 'input', id: 'liteMode', type: 'toggle', label: 'Lite mode' }] };

  function mountCondition(withBuilder: boolean) {
    const onChange = vi.fn();
    const body = (
      <ConditionalStepConfig conditionExpression={null} trueBranchStepOrder={null} falseBranchStepOrder="end" onChange={onChange} />
    );
    render(
      <MantineProvider env="test">
        {withBuilder ? (
          <AppBuilderContext.Provider value={{ manifest: LITE, setManifest: vi.fn(), steps: [], setSteps: vi.fn(), bindableFields: {}, hasExplicitManifest: true }}>
            {body}
          </AppBuilderContext.Provider>
        ) : body}
      </MantineProvider>,
    );
    return onChange;
  }

  it('reads a field and can finish the run on a branch', async () => {
    const onChange = mountCondition(true);
    fireEvent.click(screen.getByRole('button', { name: 'Read an app field' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Lite mode' }));
    expect(onChange).toHaveBeenCalledWith({ conditionExpression: 'input.liteMode' });
    const finish = screen.getAllByRole('checkbox', { name: 'Finish the run here' });
    expect((finish[1] as HTMLInputElement).checked).toBe(true);
    fireEvent.click(finish[0]);
    expect(onChange).toHaveBeenCalledWith({ trueBranchStepOrder: 'end' });
  });

  it('is as before with app inputs off', () => {
    mountCondition(false);
    expect(screen.queryByRole('button', { name: 'Read an app field' })).toBeNull();
    expect(screen.queryByRole('checkbox', { name: 'Finish the run here' })).toBeNull();
  });
});

describe('App fields in a step: the app-only settings are named from the catalogue (F2)', () => {
  it('names "Words to speak" and "Picture model" from catalogue keys, not the step card', () => {
    const bindable: Record<string, AppBindableField[]> = {
      VideoGenerate: [{ path: '@stillModel', mode: 'value', types: ['model'], label: 'server words', capabilities: ['ImageEdit'] }],
    };
    render(
      <MantineProvider env="test">
        <AppBuilderContext.Provider value={{ manifest: { version: 2, inputs: [] }, setManifest: vi.fn(), steps: [], setSteps: vi.fn(), bindableFields: bindable, hasExplicitManifest: true }}>
          <StepAppFields step={videoStep({ avatarAudio: { from: 'Step', stepOrder: 1 } })} onChange={vi.fn()} />
        </AppBuilderContext.Provider>
      </MantineProvider>,
    );
    const fields = screen.getByTestId('step-app-fields');
    expect(within(fields).getByText('Picture model')).toBeTruthy();
    expect(within(fields).queryByText('server words')).toBeNull();
  });
});

describe('The permission to send media in the builder (docs/design/app-inputs.md "Permission to send media as an app field")', () => {
  const PERMISSION: AppManifestInput = {
    kind: 'input', id: 'allowMediaEgress', type: 'toggle', label: 'Allow sending your media to AI services', required: true,
    more: false, permission: 'mediaEgress', labelKey: 'permission.mediaEgress.label',
  };
  const EGRESS: AppMediaEgress = {
    inputId: 'allowMediaEgress', required: true, alreadyAllowed: false,
    steps: [{ stepOrder: 1, stepName: 'Make the clip', stepType: 'VideoGenerate', reason: 'showsAnAvatar', need: 'required', allowedInStep: false }],
  };

  it('lists it locked, with one line saying why, and offers nothing to edit, remove or bind it', async () => {
    readable.mockResolvedValue([]);
    // The server adds the field on every read; the builder's draft never holds it.
    serve({ ...BOUND, inputs: [...(BOUND.inputs ?? []), PERMISSION] });
    render(<Harness manifest={BOUND} steps={[videoStep({ ratio: '16:9', appInputs: { ratio: 'clipShape' } })]} mediaEgress={EGRESS} />);

    const locked = screen.getByTestId('app-builder-permission');
    expect(locked.textContent).toContain('Allow sending your media to AI services');
    expect(locked.textContent).toContain('Added by the app');
    expect(locked.textContent).toContain('Make the clip (shows an avatar)');
    expect(within(locked).queryByRole('button')).toBeNull();
    expect(within(locked).queryByRole('textbox')).toBeNull();
    expect(screen.queryByTestId('app-field-in:allowMediaEgress')).toBeNull();
    expect(screen.getByTestId('app-field-in:clipShape')).toBeTruthy();
    expect(latest.manifest?.inputs?.some((i) => i.id === 'allowMediaEgress')).toBe(false);
    await act(async () => {});
  });

  it('is not there when no step sends media', async () => {
    readable.mockResolvedValue([]);
    serve(BOUND);
    render(<Harness manifest={BOUND} steps={[videoStep({ ratio: '16:9' })]} />);
    expect(screen.queryByTestId('app-builder-permission')).toBeNull();
    await act(async () => {});
  });
});
