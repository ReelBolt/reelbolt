// @vitest-environment jsdom
import { useRef, useState } from 'react';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import type { StepChangeRequest, StepChangeStepCapability } from '@/lib/types/change-requests';
import { RequestStepChangeModal } from './RequestStepChangeModal';
import { ChangeVideoPanel } from './ChangeVideoPanel';
import { buildSetBody, targetErrors, type ChangeEntry } from './change-set';
import { emptyForm } from './change-request-form';
import { useChangeSet } from './use-change-set';
import { capability, changeRequest, clipCapability, musicCapability } from './change-request-fixtures';

const push = vi.fn();
// No video of its own here: the panel shows the steps only.
vi.mock('@/lib/hooks/use-revisions', () => ({ useRevisions: () => ({ data: undefined, error: undefined, mutate: vi.fn() }) }));
vi.mock('next/navigation', () => ({ useRouter: () => ({ push }) }));
vi.mock('next/link', () => ({ default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a> }));
const apiFetch = vi.fn();
vi.mock('@/lib/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/client')>();
  return { ...actual, apiFetch: (...a: unknown[]) => apiFetch(...a) };
});

let phone = false;
beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((q: string) => ({
      matches: phone && q.includes('max-width'), media: q, onchange: null, addListener: vi.fn(), removeListener: vi.fn(),
      addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  Object.defineProperty(document, 'fonts', { configurable: true, value: { addEventListener: vi.fn(), removeEventListener: vi.fn() } });
});
beforeEach(() => { apiFetch.mockReset(); push.mockReset(); phone = false; });
afterEach(cleanup);

const caps = [capability(), clipCapability(), musicCapability()];

const preview = {
  estimate: { billingEnabled: true, lowCredits: 2, highCredits: 5, requiredCredits: 5, balanceCredits: 50, affordable: true },
  replay: { steps: [{ stepOrder: 1, label: 'Script', disposition: 'Reuse', reason: '' }, { stepOrder: 3, label: 'Narration', disposition: 'Revise', reason: '' }] },
};
const posts = (suffix: string) => apiFetch.mock.calls.filter((c) => String(c[0]).endsWith(suffix)).map((c) => JSON.parse((c[1] as { body: string }).body));
const answer = (created: Partial<StepChangeRequest> = {}) =>
  apiFetch.mockImplementation((url: string) => Promise.resolve(url.endsWith('/estimate') ? preview : changeRequest({ executionId: 'ex9', workflowDefinitionId: 'wf9', ...created })));

/** The run page's wiring in miniature: a button per step, the step modal and the unified panel. */
function Harness({ max = 6 }: { max?: number }) {
  const set = useChangeSet();
  const [target, setTarget] = useState<StepChangeStepCapability | null>(null);
  const ref = useRef<HTMLTextAreaElement>(null);
  return (
    <>
      {caps.map((c) => <button key={c.stepOrder} onClick={() => setTarget(c)}>{`open-${c.stepOrder}`}</button>)}
      <ChangeVideoPanel
        projectId="p1" executionId="ex1" runFinished changeSet={set} requests={[]} textareaRef={ref}
        capabilities={{ executionId: 'ex1', runFinished: true, maxTargets: max, steps: caps }}
        onEditEntry={(e) => setTarget(caps.find((c) => c.stepOrder === e.stepOrder)!)} onCreated={set.clear}
      />
      {target && (
        <RequestStepChangeModal
          key={target.stepOrder} projectId="p1" executionId="ex1" capability={target} opened onClose={() => setTarget(null)}
          onAdd={set.upsert} existing={set.entries.find((e) => e.stepOrder === target.stepOrder) ?? null} setSize={set.entries.length} maxTargets={max}
        />
      )}
    </>
  );
}
const mount = (max?: number) => render(<MantineProvider><Harness max={max} /></MantineProvider>);

async function addSpeed(step: number, line: string, speed: string) {
  fireEvent.click(screen.getByText(`open-${step}`));
  fireEvent.click(await screen.findByLabelText(new RegExp(line)));
  fireEvent.change(await screen.findByLabelText('Speed'), { target: { value: speed } });
  fireEvent.click(screen.getByTestId('change-request-add'));
  await waitFor(() => expect(screen.queryByTestId('change-request-modal')).toBeNull());
}
async function addMusicPrompt(text = 'Calmer') {
  fireEvent.click(screen.getByText('open-5'));
  fireEvent.click(await screen.findByLabelText(/Calm piano/));
  const box = (await screen.findAllByRole('textbox')).find((el) => el.tagName === 'TEXTAREA' && (el as HTMLTextAreaElement).value === 'Calm piano');
  fireEvent.change(box!, { target: { value: text } });
  fireEvent.click(screen.getByTestId('change-request-add'));
  await waitFor(() => expect(screen.queryByTestId('change-request-modal')).toBeNull());
}

describe('pending set', () => {
  it('offers "Request this change now" only while the set is empty and adds without posting', async () => {
    answer();
    mount();
    fireEvent.click(screen.getByText('open-3'));
    expect(await screen.findByTestId('change-request-now')).toBeTruthy();
    cleanup();
    mount();
    await addSpeed(3, 'Hello there', '1.5');
    expect(screen.getByTestId('change-set-entry-3').textContent).toContain('Narration');
    expect(screen.getByTestId('change-set-entry-3').textContent).toContain('Speed: 1.5');
    expect(posts('/change-requests')).toHaveLength(0);
    fireEvent.click(screen.getByText('open-5'));
    await screen.findByTestId('change-request-modal');
    expect(screen.queryByTestId('change-request-now')).toBeNull();
    expect(screen.getByTestId('change-request-add')).toBeTruthy();
  });

  it('reopens a step prefilled to edit it, and removes an entry', async () => {
    answer();
    mount();
    await addSpeed(3, 'Hello there', '1.5');
    fireEvent.click(screen.getByTestId('change-set-edit-3'));
    const speed = (await screen.findByLabelText('Speed')) as HTMLInputElement;
    expect(speed.value).toBe('1.5');
    fireEvent.change(speed, { target: { value: '1.2' } });
    fireEvent.click(screen.getByText('Update this change'));
    await waitFor(() => expect(screen.queryByTestId('change-request-modal')).toBeNull());
    expect(screen.getByTestId('change-set-entry-3').textContent).toContain('Speed: 1.2');
    expect(screen.getAllByTestId(/^change-set-entry-\d+$/)).toHaveLength(1);
    fireEvent.click(screen.getByTestId('change-set-remove-3'));
    expect(screen.queryByTestId('change-set-entries')).toBeNull();
    expect(screen.queryByTestId('change-set-submit')).toBeNull();
  });

  it('asks for one combined estimate with targets[] and submits one multi body', async () => {
    answer();
    mount();
    await addSpeed(3, 'Hello there', '1.5');
    await addMusicPrompt();
    expect((await screen.findByTestId('change-set-estimate', undefined, { timeout: 3000 })).textContent).toContain('About 2.00 to 5.00');
    expect(screen.getByText('Will be redone')).toBeTruthy();
    expect(screen.getByText('Reused (free)')).toBeTruthy();
    const body = {
      targets: [
        { stepResultId: 'sr3', itemIds: ['vo1'], overrides: { vo1: { speed: 1.5 } } },
        { stepResultId: 'sr5', itemIds: ['a1'], overrides: { a1: { prompt: 'Calmer' } } },
      ],
    };
    expect(posts('/estimate').at(-1)).toEqual(body);
    fireEvent.click(screen.getByTestId('change-set-submit'));
    await waitFor(() => expect(push).toHaveBeenCalledWith('/projects/p1/workflows/wf9/executions/ex9'));
    expect(posts('/change-requests')).toEqual([body]);
    expect(screen.queryByTestId('change-set-submit')).toBeNull();
  });

  it("sends a single non-film entry as today's single body", async () => {
    answer();
    mount();
    await addSpeed(3, 'Hello there', '1.5');
    expect(screen.getByTestId('change-set-submit').textContent).toBe('Request 1 change');
    await screen.findByTestId('change-set-estimate', undefined, { timeout: 3000 });
    fireEvent.click(screen.getByTestId('change-set-submit'));
    await waitFor(() => expect(push).toHaveBeenCalled());
    expect(posts('/change-requests')).toEqual([{ stepResultId: 'sr3', itemIds: ['vo1'], overrides: { vo1: { speed: 1.5 } } }]);
  });

  it('disables adding at the maximum with a reason', async () => {
    answer();
    mount(2);
    await addSpeed(3, 'Hello there', '1.5');
    await addMusicPrompt();
    fireEvent.click(screen.getByText('open-4'));
    await screen.findByTestId('change-request-modal');
    expect(screen.getByTestId('change-request-max').textContent).toContain('up to 2 changes');
    expect((screen.getByTestId('change-request-add') as HTMLButtonElement).disabled).toBe(true);
  });

  it('shows a 400 message beside the entry its path names', async () => {
    apiFetch.mockImplementation((url: string) => (url.endsWith('/estimate')
      ? Promise.reject(new ApiError('Bad', 400, ['Prompt is too long'], { message: 'Bad', errors: [{ path: 'targets[1].overrides', message: 'Prompt is too long' }] }))
      : Promise.resolve(preview)));
    mount();
    await addSpeed(3, 'Hello there', '1.5');
    await addMusicPrompt();
    const err = await screen.findByTestId('change-set-entry-error-5', undefined, { timeout: 3000 });
    expect(err.textContent).toBe('Prompt is too long');
    expect(within(screen.getByTestId('change-set-entry-3')).queryByRole('alert')).toBeNull();
    expect(screen.queryByTestId('change-set-error')).toBeNull();
  });

  it('on a phone shows a bottom bar that opens a sheet, closes on Escape', async () => {
    phone = true;
    answer();
    mount();
    await addSpeed(3, 'Hello there', '1.5');
    expect(screen.queryByTestId('change-set-submit')).toBeNull();
    expect(screen.getByTestId('change-set-bar').textContent).toContain('1 change');
    fireEvent.click(screen.getByTestId('change-set-sheet-open'));
    const sheet = await screen.findByRole('dialog', { name: 'Your changes' });
    expect(within(sheet).getByTestId('change-set-submit')).toBeTruthy();
    fireEvent.keyDown(sheet, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Your changes' })).toBeNull());
  });
});

describe('body and error helpers', () => {
  const entry = (stepOrder: number, instruction: string): ChangeEntry =>
    ({ stepOrder, label: 'Narration', stepType: 'Voiceover', request: { stepOrder, instruction }, form: emptyForm(), summary: [] });
  const filmRequest = { stepOrder: 7, instruction: 'x' };

  it('buildSetBody: nothing, and the video alone (that is the video route, not this one)', () => {
    expect(buildSetBody([])).toBeNull();
    expect(buildSetBody([], null, filmRequest)).toBeNull();
  });
  it('buildSetBody: one step is the single body; a step with the video is targets[], the video last', () => {
    expect(buildSetBody([entry(3, 'y')])).toEqual({ stepOrder: 3, instruction: 'y' });
    expect(buildSetBody([entry(3, 'y')], null, filmRequest)).toEqual({ targets: [{ stepOrder: 3, instruction: 'y' }, { stepOrder: 7, instruction: 'x' }] });
  });
  it('F3 buildSetBody: the app fields and settings ride with the steps (and the video) in one body', () => {
    const fields = { inputs: { audience: 'Tourists' }, brief: { language: 'it' }, lines: ['Who: Baristas → Tourists'] };
    expect(buildSetBody([entry(3, 'y')], fields)).toEqual({ inputs: { audience: 'Tourists' }, brief: { language: 'it' }, targets: [{ stepOrder: 3, instruction: 'y' }] });
    expect(buildSetBody([], fields)).toEqual({ inputs: { audience: 'Tourists' }, brief: { language: 'it' } });
    expect(buildSetBody([], fields, filmRequest)).toEqual({ inputs: { audience: 'Tourists' }, brief: { language: 'it' }, targets: [{ stepOrder: 7, instruction: 'x' }] });
  });
  it('targetErrors splits indexed and general messages and ignores non-400', () => {
    const e = new ApiError('Bad', 400, [], { errors: [{ path: 'targets[0].itemIds', message: 'a' }, { path: 'targets', message: 'b' }] });
    expect(targetErrors(e)).toEqual({ byIndex: { 0: ['a'] }, general: ['b'] });
    expect(targetErrors(new ApiError('x', 402, [], {}))).toBeNull();
    expect(targetErrors(new ApiError('x', 409, [], { errors: [{ path: 'targets[2]', message: 'c' }] }))?.byIndex).toEqual({ 2: ['c'] });
  });
});
