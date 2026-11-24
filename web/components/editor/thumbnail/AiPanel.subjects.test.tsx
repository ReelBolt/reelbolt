// @vitest-environment jsdom
/** The AI panel's subject picker: offered by the two tools that take avatars and objects, and only while the feature answers. */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { EditorLayer } from '@/lib/editor/model';
import type { ImageOperationCapabilities } from '@/lib/types/image-document';
import { testAvatar, testObject } from '@/test/avatars';
import { renderWithI18n } from '@/test/i18n';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('requestAnimationFrame', () => 0);
vi.stubGlobal('cancelAnimationFrame', () => {});

import { AiPanel, type AiPanelProps } from './AiPanel';

const LAYER: EditorLayer = {
  id: 'src', kind: 'image', role: 'background', label: 'Original', zOrder: 1, visible: true, locked: false, opacity: 1, blendMode: 'normal',
  transform: { x: 0, y: 0, scale: 1, rotation: 0, flipX: false, flipY: false }, width: 1280, height: 720, imageUrl: '/img/src', matteUrl: null, content: null, segmentationPrompt: null,
};
const anna = testAvatar();
const bea = testAvatar({ id: 'bea', name: 'Bea' });
const bottle = testObject();
const videoOnly = testAvatar({ id: 'vid', name: 'Vid', coversPictures: false });
const SUBJECTS = { projectId: 'p1', avatars: [anna, bea, bottle, videoOnly] };
const caps = (over: Partial<ImageOperationCapabilities> = {}): ImageOperationCapabilities => ({
  operation: 'generate', provider: { kind: 'FalAi', model: 'google/nano-banana-2.1/edit' }, takesPrompt: true, maxPromptLength: 2000, expand: null, ...over,
});

const props = (over: Partial<AiPanelProps> = {}): AiPanelProps => ({
  tool: 'generate', onPickTool: () => {}, layer: LAYER, hasMask: false, canvas: { width: 1280, height: 720 }, unavailable: {}, lastRun: {}, running: null,
  subjectControls: null, brushControls: null, subjects: SUBJECTS, onRun: vi.fn(), ...over,
});
function mount(over: Partial<AiPanelProps> = {}) {
  const onRun = vi.fn<AiPanelProps['onRun']>();
  const utils = render(<MantineProvider><AiPanel {...props({ onRun, ...over })} /></MantineProvider>);
  return { onRun, ...utils };
}
const tick = (id: string) => fireEvent.click(screen.getByTestId(`subject-option-${id}`));
const box = (id: string) => screen.getByTestId(`subject-option-${id}`) as HTMLInputElement;

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

describe('AiPanel: avatars and objects', () => {
  it('"Create a picture" offers the picker under the description, with a one-line hint', () => {
    mount();
    const area = within(screen.getByTestId('ai-subjects-generate'));
    expect(area.getByRole('group', { name: 'People and objects (optional)' })).toBeTruthy();
    expect(area.getByText("Show a person or an object from this project's Avatars tab. Up to 3, one person at most.")).toBeTruthy();
    expect(area.getByRole('group', { name: 'People' })).toBeTruthy();
    expect(area.getByRole('group', { name: 'Objects' })).toBeTruthy();
    // under the prompt
    const prompt = screen.getByLabelText('Describe it');
    expect(prompt.compareDocumentPosition(screen.getByTestId('ai-subjects-generate')) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('"Create a picture" runs with the chosen ids, in the order chosen', () => {
    const { onRun } = mount();
    fireEvent.change(screen.getByLabelText('Describe it'), { target: { value: 'Anna holds the bottle' } });
    tick('bottle');
    tick('anna');
    fireEvent.click(screen.getByRole('button', { name: 'Create the picture' }));
    expect(onRun).toHaveBeenCalledWith('generate', expect.objectContaining({ prompt: 'Anna holds the bottle', subjectIds: ['bottle', 'anna'] }));
  });

  it('"Edit with words" offers it too and runs with the layer, the prompt and the chosen ids', () => {
    const { onRun } = mount({ tool: 'instruct' });
    expect(screen.getByTestId('ai-subjects-instruct')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('What to change'), { target: { value: 'put the bottle on the table' } });
    tick('bottle');
    fireEvent.click(screen.getByRole('button', { name: 'Make the change' }));
    expect(onRun).toHaveBeenCalledWith('instruct', expect.objectContaining({ layerId: 'src', subjectIds: ['bottle'] }));
  });

  it('runs with no ids when nobody is chosen', () => {
    const { onRun } = mount();
    fireEvent.change(screen.getByLabelText('Describe it'), { target: { value: 'a mountain' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create the picture' }));
    expect(onRun.mock.calls[0][1].subjectIds).toEqual([]);
  });

  it.each(['upscale', 'relight', 'expand', 'replaceBackground', 'removeBackground', 'erase', 'fill'] as const)('%s never offers it', (tool) => {
    mount({ tool });
    expect(screen.queryByTestId('subject-picker')).toBeNull();
  });

  it('shows nothing at all while the project\'s avatars do not answer (the feature is off)', () => {
    mount({ subjects: null });
    expect(screen.queryByTestId('subject-picker')).toBeNull();
    expect(screen.queryByTestId('ai-subjects-generate')).toBeNull();
    cleanup();
    mount({ subjects: undefined, tool: 'instruct' });
    expect(screen.queryByTestId('subject-picker')).toBeNull();
  });

  it('lists a person without picture consent greyed, with the reason, and keeps the one-person rule', () => {
    mount();
    expect(box('vid').disabled).toBe(true);
    expect(screen.getByTestId('subject-reason-vid').textContent).toBe('Confirm consent again in the Avatars tab to use this person in pictures');
    tick('anna');
    expect(box('bea').disabled).toBe(true);
    expect(box('bottle').disabled).toBe(false);
  });

  it('older server: capabilities say nothing about subjects, so the picker works and no hint is shown', () => {
    const { onRun } = mount({ capabilities: { generate: caps() } });
    expect(screen.queryByTestId('ai-subjects-unsupported-generate')).toBeNull();
    expect(box('anna').disabled).toBe(false);
    fireEvent.change(screen.getByLabelText('Describe it'), { target: { value: 'Anna on a beach' } });
    tick('anna');
    fireEvent.click(screen.getByRole('button', { name: 'Create the picture' }));
    expect(onRun.mock.calls[0][1].subjectIds).toEqual(['anna']);
  });

  it('a model that cannot be shown them: the picker stays, greyed, and the hint names the models that can', () => {
    const { onRun } = mount({ capabilities: { generate: caps({ subjects: { supported: false, maxSubjects: 3, models: ['google/nano-banana-2.1/edit', 'fal-ai/qwen-image-edit-2511'] } }) } });
    expect(screen.getByTestId('subject-picker')).toBeTruthy();
    expect(box('anna').disabled).toBe(true);
    expect(screen.getByTestId('ai-subjects-unsupported-generate').textContent).toBe(
      'The AI model set up for this tool cannot be shown people or objects, so none are used. These models can: google/nano-banana-2.1/edit and fal-ai/qwen-image-edit-2511.',
    );
    fireEvent.change(screen.getByLabelText('Describe it'), { target: { value: 'a mountain' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create the picture' }));
    expect(onRun.mock.calls[0][1].subjectIds).toEqual([]);
  });

  it('a model that cannot, with no other named: the plain hint', () => {
    mount({ capabilities: { generate: caps({ subjects: { supported: false } }) } });
    expect(screen.getByTestId('ai-subjects-unsupported-generate').textContent).toBe('The AI model set up for this tool cannot be shown people or objects, so none are used.');
  });

  it('takes the model\'s own limit', () => {
    mount({ capabilities: { generate: caps({ subjects: { supported: true, maxSubjects: 1 } }) } });
    expect(screen.getByText("Show a person or an object from this project's Avatars tab. Up to 1, one person at most.")).toBeTruthy();
    tick('bottle');
    expect(box('anna').disabled).toBe(true);
    expect(screen.getByTestId('subject-reason-anna').textContent).toBe('You can choose up to 1. Untick one to choose this.');
  });

  it('will not run with a chosen one that stopped being usable, and says what to do', () => {
    const { onRun, rerender } = mount();
    fireEvent.change(screen.getByLabelText('Describe it'), { target: { value: 'Anna on a beach' } });
    tick('anna');
    const gone = { projectId: 'p1', avatars: [testAvatar({ selectable: false, unselectableReason: 'consent_revoked' }), bottle] };
    rerender(<MantineProvider><AiPanel {...props({ subjects: gone, onRun })} /></MantineProvider>);
    const run = screen.getByRole('button', { name: 'Create the picture' }) as HTMLButtonElement;
    expect(run.disabled).toBe(true);
    expect(screen.getByText('Untick the person or object that cannot be used.')).toBeTruthy();
    tick('anna');
    expect((screen.getByRole('button', { name: 'Create the picture' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('keeps the choice per tool', () => {
    const { rerender, onRun } = mount();
    tick('bottle');
    rerender(<MantineProvider><AiPanel {...props({ tool: 'instruct', onRun })} /></MantineProvider>);
    expect(box('bottle').checked).toBe(false);
    rerender(<MantineProvider><AiPanel {...props({ tool: 'generate', onRun })} /></MantineProvider>);
    expect(box('bottle').checked).toBe(true);
  });

  it('reads in Italian', () => {
    renderWithI18n(<AiPanel {...props({ capabilities: { generate: caps({ subjects: { supported: false } }) } })} />, { locale: 'it' });
    expect(screen.getByRole('group', { name: 'Persone e oggetti (facoltativo)' })).toBeTruthy();
    expect(screen.getByTestId('ai-subjects-unsupported-generate').textContent).toBe('Il modello AI configurato per questo strumento non può ricevere persone o oggetti, quindi non ne viene usato nessuno.');
  });
});
