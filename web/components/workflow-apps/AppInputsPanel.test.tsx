// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { AppManifest, WorkflowAppResponse } from '@/lib/types/workflow';

const app = vi.hoisted(() => ({ state: { data: undefined, error: undefined, isLoading: false } as {
  data: WorkflowAppResponse | undefined; error: unknown; isLoading: boolean } }));
vi.mock('@/lib/hooks/use-workflow-apps', () => ({ useWorkflowApp: () => app.state }));
const readable = vi.hoisted(() => vi.fn());
vi.mock('@/lib/workflow-apps/readable-fields', () => ({ readableBriefFields: readable }));

import { AppInputsPanel } from './AppInputsPanel';
import { renderWithI18n } from '@/test/i18n';

beforeAll(() => {
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: (query: string) => ({
      matches: false, media: query, onchange: null,
      addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => false,
    }),
  });
});

const MANIFEST: AppManifest = {
  version: 1,
  title: 'Explainer',
  inputs: [
    { kind: 'brief', field: 'language', label: 'Language', default: 'auto' },
    { kind: 'brief', field: 'lengthSec', label: 'Length', default: 60 },
    { kind: 'notes', label: 'Anything else?' },
  ],
};

function mount(over: Partial<React.ComponentProps<typeof AppInputsPanel>> = {}) {
  app.state = { data: { workflowId: 'w1', name: 'Explainer', manifest: MANIFEST, hasExplicitManifest: false,
    costClass: 'No credits', source: 'custom' }, error: undefined, isLoading: false };
  const onSave = vi.fn().mockResolvedValue(true);
  const onReset = vi.fn().mockResolvedValue(true);
  render(
    <MantineProvider env="test">
      <AppInputsPanel projectId="p1" workflowId="w1" draftSteps={[]} saving={false} errors={null} onSave={onSave} onReset={onReset} {...over} />
    </MantineProvider>,
  );
  return Object.assign(onSave, { onReset });
}

const addField = () => screen.getAllByLabelText('Add a field').find((e) => e.tagName === 'INPUT') as HTMLInputElement;

afterEach(() => { cleanup(); vi.clearAllMocks(); });

describe('App inputs panel', () => {
  it('lists the effective inputs with a switch each', async () => {
    readable.mockResolvedValue([]);
    mount();
    expect(screen.getByRole('switch', { name: 'Shown in the app: Language' })).toBeTruthy();
    expect(screen.getByRole('switch', { name: 'Shown in the app: Length' })).toBeTruthy();
    expect(screen.getByRole('switch', { name: 'Shown in the app: Anything else?' })).toBeTruthy();
    await waitFor(() => expect(readable).toHaveBeenCalled());
  });

  it('saves hidden, relabelled, defaulted and reordered inputs', async () => {
    readable.mockResolvedValue([]);
    const onSave = mount();
    fireEvent.click(screen.getByRole('switch', { name: 'Shown in the app: Anything else?' }));
    fireEvent.change(screen.getByLabelText('Name in the app for Length'), { target: { value: 'How long' } });
    fireEvent.change(screen.getByLabelText('Starts as, for How long'), { target: { value: '90' } });
    fireEvent.click(screen.getByRole('button', { name: 'Move How long up' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save app inputs' }));

    expect(onSave).toHaveBeenCalledTimes(1);
    const saved = onSave.mock.calls[0][0] as AppManifest;
    expect(saved.inputs).toEqual([
      { kind: 'brief', field: 'lengthSec', label: 'How long', default: 90 },
      { kind: 'brief', field: 'language', label: 'Language', default: 'auto' },
    ]);
    expect(saved.title).toBeUndefined();
    await act(async () => {});
  });

  it('keeps focus on the arrow at an end (aria-disabled), announces the move and does nothing there', async () => {
    readable.mockResolvedValue([]);
    const onSave = mount();
    const up = screen.getByRole('button', { name: 'Move Length up' });
    up.focus();
    fireEvent.click(up);
    expect(screen.getByText(/Moved Length to position 1 of 3/).textContent).toBeTruthy();
    const first = screen.getByRole('button', { name: 'Move Length up' });
    expect(first.getAttribute('aria-disabled')).toBe('true');
    expect((first as HTMLButtonElement).disabled).toBe(false);
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Move Length down' })));
    fireEvent.click(first);
    fireEvent.click(screen.getByRole('button', { name: 'Save app inputs' }));
    expect((onSave.mock.calls[0][0] as AppManifest).inputs?.[0].field).toBe('lengthSec');
  });

  it('offers only the fields the server says a step can read, and adds one', async () => {
    readable.mockResolvedValue(['format', 'voice']);
    const onSave = mount();
    await waitFor(() => expect(addField().hasAttribute('disabled')).toBe(false));
    // The candidates asked about exclude what is already exposed.
    expect(readable.mock.calls[0][3]).not.toContain('language');
    fireEvent.click(addField());
    const options = screen.getAllByRole('option').map((o) => o.textContent);
    expect(options).toEqual(['Shape', 'Narration']);
    fireEvent.click(screen.getByRole('option', { name: 'Narration' }));
    fireEvent.click(screen.getByRole('button', { name: 'Add' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save app inputs' }));
    const inputs = (onSave.mock.calls[0][0] as AppManifest).inputs ?? [];
    expect(inputs[inputs.length - 1]).toEqual({ kind: 'brief', field: 'voice', label: 'Narration' });
  });

  it('resets to the automatic inputs only after a confirm', async () => {
    readable.mockResolvedValue([]);
    const onSave = mount();
    fireEvent.click(screen.getByRole('button', { name: 'Reset to automatic inputs' }));
    expect(onSave.onReset).not.toHaveBeenCalled();
    fireEvent.click(await screen.findByRole('button', { name: 'Reset' }));
    expect(onSave.onReset).toHaveBeenCalledTimes(1);
    await act(async () => {});
  });

  it('stores only the inputs when the manifest is derived, and uses a neutral heading for other refusals', async () => {
    readable.mockResolvedValue([]);
    const onSave = mount({ errors: ['The label of input 1 is too long.'] });
    expect(screen.getByRole('alert').textContent).toContain('fix the app inputs below');
    fireEvent.click(screen.getByRole('button', { name: 'Save app inputs' }));
    const saved = onSave.mock.calls[0][0] as AppManifest;
    expect(saved.title).toBeUndefined();
    expect(saved.version).toBe(1);
    await act(async () => {});
  });

  it('names the orphaned input from a refused save', async () => {
    readable.mockResolvedValue([]);
    mount({ errors: ['The input "Narration" is not read by any step of this workflow, so it would be shown and ignored.'] });
    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('"Narration"');
    await act(async () => {});
  });

  it('shows a stored English default label in the active language and keeps an author\'s own wording', async () => {
    readable.mockResolvedValue([]);
    app.state = { data: { workflowId: 'w1', name: 'Explainer', hasExplicitManifest: false, costClass: 'No credits', source: 'custom',
      manifest: { version: 1, title: 'Explainer', inputs: [
        { kind: 'brief', field: 'resolution', label: 'Resolution' },
        { kind: 'brief', field: 'format', label: 'Shape' },
        { kind: 'brief', field: 'lengthSec', label: 'How long' },
      ] } }, error: undefined, isLoading: false };
    renderWithI18n(
      <AppInputsPanel projectId="p1" workflowId="w1" draftSteps={[]} saving={false} errors={null} onSave={vi.fn()} onReset={vi.fn()} />,
    );
    expect(screen.getByDisplayValue('Risoluzione')).toBeTruthy();
    expect(screen.getByDisplayValue('Formato')).toBeTruthy();
    expect(screen.getByDisplayValue('How long')).toBeTruthy();
    expect(screen.queryByDisplayValue('Resolution')).toBeNull();
    await waitFor(() => expect(readable).toHaveBeenCalled());
  });
});
