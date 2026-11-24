// @vitest-environment jsdom
/**
 * Customer e2e findings 10/11/18: templates are picked by name + description (no "v1 · N steps"),
 * a run is confirmed before it starts, the brief is always offered, and the run history numbers
 * runs in plain words.
 */
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { TemplatePicker, pickableTemplates } from './TemplatePicker';
import { RunWithInputsModal } from '@/components/workflow-apps/RunWithInputsModal';
import { estimateRunDuration, formatEstimate } from '@/lib/utils/run-estimate';
import type { AppManifest, WorkflowExecution, WorkflowTemplateSummary } from '@/lib/types/workflow';

const { EXECUTIONS } = vi.hoisted(() => ({ EXECUTIONS: { current: [] as unknown[] } }));
vi.mock('@/lib/hooks/use-executions', () => ({
  useExecutions: () => ({ data: EXECUTIONS.current, isLoading: false }),
}));
vi.mock('@/lib/hooks/use-styles', () => ({
  useStylePresets: () => ({
    data: [
      { key: 'cinematic', name: 'Cinematic', description: 'Slow and atmospheric.', config: { pacing: 'Slow' }, suggestedFormat: 'Landscape' },
      { key: 'fast-social-reel', name: 'Fast social reel', description: 'Punchy and quick.', config: { pacing: 'Fast' }, suggestedFormat: 'Vertical' },
    ],
  }),
  useEditingStyles: () => ({
    data: [{ id: 'st1', name: 'House style', description: null, projectId: null, config: { tone: 'Calm' }, createdAt: '', updatedAt: '' }],
  }),
}));
// The plan's resolution cap; null = unlimited (or billing off). The real hook would reach the network.
const { CAP } = vi.hoisted(() => ({ CAP: { current: null as number | null } }));
vi.mock('@/lib/hooks/use-billing', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/hooks/use-billing')>()),
  useOutputHeightCap: () => CAP.current,
}));
import { ExecutionHistory } from './ExecutionHistory';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
  // The brief is an autosizing Textarea, which listens for font loading; jsdom has no FontFaceSet.
  Object.defineProperty(document, 'fonts', {
    configurable: true,
    value: { addEventListener: vi.fn(), removeEventListener: vi.fn(), ready: Promise.resolve() },
  });
});
afterEach(() => {
  cleanup();
  CAP.current = null;
});

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

const TEMPLATES: WorkflowTemplateSummary[] = [
  { key: 'auto', name: 'Promo from your code', description: 'Auto-created.', version: 1, autoCreateOnProject: true, requiresUserInput: false, stepCount: 11 },
  { key: 'reel', name: '15-second vertical reel', description: 'A punchy 9:16 reel from your clips.', version: 3, autoCreateOnProject: false, requiresUserInput: true, stepCount: 6 },
  { key: 'demo', name: '60-second product demo', description: 'A narrated walkthrough.', version: 1, autoCreateOnProject: false, requiresUserInput: true, stepCount: 9 },
];

function exec(id: string, status: WorkflowExecution['status'], startedAt: string | null, completedAt: string | null, userRequest: string | null = null): WorkflowExecution {
  return { id, workflowDefinitionId: 'w', status, startedAt, completedAt, iterationCount: 0, resultJson: null, stepResults: [], reviewScores: [], userRequest };
}

describe('TemplatePicker', () => {
  it('is labelled plainly and shows each template name and description, without version or step count', () => {
    wrap(<TemplatePicker templates={TEMPLATES} selectedKey={null} onSelect={vi.fn()} onApply={vi.fn()} />);
    expect(screen.getByText('Start from a template')).toBeTruthy();
    expect(screen.queryByText(/WOW/)).toBeNull();
    expect(screen.getByText('15-second vertical reel')).toBeTruthy();
    expect(screen.getByText('A punchy 9:16 reel from your clips.')).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/v\d+ ·|\d+ steps/);
    // Without a project there is nothing to tell whether it already has the auto-created one: held back.
    expect(screen.queryByText('Promo from your code')).toBeNull();
  });

  it('selects a template and applies it', () => {
    const onSelect = vi.fn();
    const onApply = vi.fn();
    const { rerender } = wrap(<TemplatePicker templates={TEMPLATES} selectedKey={null} onSelect={onSelect} onApply={onApply} />);
    expect((screen.getByRole('button', { name: 'Use this template' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('radio', { name: /60-second product demo/ }));
    expect(onSelect).toHaveBeenCalledWith('demo');
    rerender(<MantineProvider><TemplatePicker templates={TEMPLATES} selectedKey="demo" onSelect={onSelect} onApply={onApply} /></MantineProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Use this template' }));
    expect(onApply).toHaveBeenCalled();
  });

  it('leaves the auto-created template out only for a project that has a workflow made from it', () => {
    expect(pickableTemplates(TEMPLATES).map((t) => t.key)).toEqual(['reel', 'demo']);
    expect(pickableTemplates(TEMPLATES, new Set(['auto'])).map((t) => t.key)).toEqual(['reel', 'demo']);
    expect(pickableTemplates(TEMPLATES, new Set(['video-derush-edit'])).map((t) => t.key)).toEqual(['auto', 'reel', 'demo']);
  });
});

describe('RunWithInputsModal (run confirmation)', () => {
  it('always offers an optional brief and lists what will run', () => {
    const onConfirm = vi.fn();
    wrap(
      <RunWithInputsModal projectId="p1" workflowId="w1" opened onClose={vi.fn()} onConfirm={onConfirm} stepSummaries={['Watches your footage', 'Picks the best clips']} estimate="Usually takes about 12 minutes, based on your last run." workflowName="Reel" />,
    );
    expect(screen.getByLabelText(/Your brief/)).toBeTruthy();
    expect(screen.getByText('Watches your footage')).toBeTruthy();
    expect(screen.getByText(/about 12 minutes/)).toBeTruthy();
    fireEvent.change(screen.getByLabelText(/Your brief/), { target: { value: '  A 20 s vertical teaser  ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));
    expect(onConfirm).toHaveBeenCalledWith({ notes: 'A 20 s vertical teaser' });
  });

  it('starts with no brief when the workflow does not require one', () => {
    const onConfirm = vi.fn();
    wrap(<RunWithInputsModal projectId="p1" workflowId="w1" opened onClose={vi.fn()} onConfirm={onConfirm} />);
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));
    expect(onConfirm).toHaveBeenCalledWith({});
  });

  it('will not start without a brief when one is required', () => {
    const onConfirm = vi.fn();
    wrap(<RunWithInputsModal projectId="p1" workflowId="w1" opened onClose={vi.fn()} onConfirm={onConfirm} requiresBrief />);
    const start = screen.getByRole('button', { name: 'Start run' }) as HTMLButtonElement;
    expect(start.disabled).toBe(true);
    fireEvent.change(screen.getByLabelText(/Your brief/), { target: { value: 'Upbeat product reel' } });
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));
    expect(onConfirm).toHaveBeenCalledWith({ notes: 'Upbeat product reel' });
  });

  it('a preset suggests its shape, and a shape the user picked wins', () => {
    const onConfirm = vi.fn();
    wrap(<RunWithInputsModal projectId="p1" workflowId="w1" opened onClose={vi.fn()} onConfirm={onConfirm} />);
    fireEvent.click(screen.getByRole('combobox', { name: /Editing style/ }));
    fireEvent.click(screen.getByRole('option', { name: 'Fast social reel' }));
    expect(screen.getByText('Punchy and quick.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));
    expect(onConfirm).toHaveBeenLastCalledWith({ stylePreset: 'fast-social-reel', format: 'Vertical' });
  });

  it('runs a saved style in the shape the user picked', () => {
    const onConfirm = vi.fn();
    wrap(<RunWithInputsModal projectId="p1" workflowId="w1" opened onClose={vi.fn()} onConfirm={onConfirm} />);
    fireEvent.click(screen.getByText('4:5'));
    fireEvent.click(screen.getByRole('combobox', { name: /Editing style/ }));
    fireEvent.click(screen.getByRole('option', { name: 'House style' }));
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));
    expect(onConfirm).toHaveBeenLastCalledWith({ styleId: 'st1', format: 'Portrait' });
  });

  it('runs at the resolution the user picked, next to the shape', () => {
    const onConfirm = vi.fn();
    wrap(<RunWithInputsModal projectId="p1" workflowId="w1" opened onClose={vi.fn()} onConfirm={onConfirm} />);
    fireEvent.click(screen.getByText('1:1'));
    fireEvent.click(screen.getByText('4K'));
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));
    expect(onConfirm).toHaveBeenLastCalledWith({ format: 'Square', resolution: '2160p' });
  });

  it('a resolution above the plan cannot be picked, and the run stays on Auto', () => {
    CAP.current = 720;
    const onConfirm = vi.fn();
    wrap(<RunWithInputsModal projectId="p1" workflowId="w1" opened onClose={vi.fn()} onConfirm={onConfirm} />);
    expect((screen.getByRole('radio', { name: '1080p' }) as HTMLInputElement).disabled).toBe(true);
    fireEvent.click(screen.getByText('4K'));
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));
    expect(onConfirm).toHaveBeenLastCalledWith({});
    expect(screen.getByText(/Your plan renders up to 720p\. Upgrade to render at 1080p or 4K\./)).toBeTruthy();
  });
});

describe('RunWithInputsModal (typed app inputs)', () => {
  const manifest: AppManifest = {
    version: 2,
    inputs: [
      { kind: 'input', id: 'prompt', type: 'longText', label: 'What happens', required: true },
      { kind: 'input', id: 'lite', type: 'toggle', label: 'Lite mode' },
    ],
  };

  it('shows the typed inputs and no free brief; Run waits for a required value and sends brief.inputs', () => {
    const onConfirm = vi.fn();
    wrap(<RunWithInputsModal projectId="p1" workflowId="w1" opened onClose={vi.fn()} onConfirm={onConfirm} manifest={manifest} requiresBrief />);
    expect(screen.queryByLabelText(/Your brief/)).toBeNull();
    const start = screen.getByRole('button', { name: 'Start run' }) as HTMLButtonElement;
    expect(start.disabled).toBe(true);
    fireEvent.change(screen.getByRole('textbox', { name: /What happens/ }), { target: { value: '  A barista  ' } });
    fireEvent.click(screen.getByRole('switch', { name: 'Lite mode' }));
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));
    const brief = onConfirm.mock.calls[0]![0] as Record<string, unknown>;
    expect(brief.inputs).toEqual({ prompt: 'A barista', lite: true });
    expect(brief).not.toHaveProperty('notes');
    expect(JSON.stringify(brief)).not.toMatch(/gress/);
  });

  it('a derived page (no typed inputs) keeps the one notes box', () => {
    const onConfirm = vi.fn();
    wrap(<RunWithInputsModal projectId="p1" workflowId="w1" opened onClose={vi.fn()} onConfirm={onConfirm} manifest={{ version: 1, inputs: [{ kind: 'notes' }] }} />);
    fireEvent.change(screen.getByLabelText(/Your brief/), { target: { value: 'Teaser' } });
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));
    expect(onConfirm).toHaveBeenCalledWith({ notes: 'Teaser' });
  });
});

describe('estimateRunDuration', () => {
  it('uses the median of finished runs', () => {
    const runs = [
      exec('a', 'Passed', '2026-10-01T10:00:00Z', '2026-10-01T10:10:00Z'),
      exec('b', 'Passed', '2026-10-01T11:00:00Z', '2026-10-01T11:20:00Z'),
      exec('c', 'Passed', '2026-10-01T12:00:00Z', '2026-10-01T14:00:00Z'),
      exec('d', 'Failed', '2026-10-01T15:00:00Z', '2026-10-01T15:01:00Z'),
    ];
    expect(estimateRunDuration(runs, 3)).toBe('Usually takes about 20 minutes, based on your last 3 runs.');
  });

  it('warns about long workflows without history and stays silent for short ones', () => {
    expect(estimateRunDuration([], 11)).toMatch(/up to an hour/);
    expect(estimateRunDuration([], 3)).toBeNull();
  });

  it('formats hours', () => {
    expect(formatEstimate(70 * 60000)).toBe('about 1 hour 10 minutes');
    expect(formatEstimate(30000)).toBe('about 1 minute');
  });
});

describe('ExecutionHistory', () => {
  it('says "No runs yet" when empty', () => {
    EXECUTIONS.current = [];
    wrap(<ExecutionHistory projectId="p" workflowId="w" />);
    expect(screen.getByText('No runs yet')).toBeTruthy();
  });

  it('numbers runs oldest-first, treats a not-yet-started run as the newest, and shows the brief', () => {
    EXECUTIONS.current = [
      exec('new', 'Queued', null, null, 'A 15 s vertical reel\nwith captions'),
      exec('old', 'Passed', '2026-10-01T10:00:00Z', '2026-10-01T10:10:00Z'),
    ];
    wrap(<ExecutionHistory projectId="p" workflowId="w" />);
    expect(screen.getByText('Run 2')).toBeTruthy();
    expect(screen.getByText('Run 1')).toBeTruthy();
    expect(screen.getByText('A 15 s vertical reel')).toBeTruthy();
    expect(screen.getByText('Starting…')).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/\bnew\b|\bold\b/); // no raw ids
  });
});
