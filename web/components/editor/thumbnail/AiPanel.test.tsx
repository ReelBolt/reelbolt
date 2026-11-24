// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { EditImageOperation } from '@/lib/types/image-document';
import type { EditorLayer } from '@/lib/editor/model';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
// Mantine's transitions schedule their completion through requestAnimationFrame; keep the chain inert
// in jsdom so nothing fires after cleanup() (the same reason DesignGraphicPanel.test.tsx does this).
vi.stubGlobal('requestAnimationFrame', () => 0);
vi.stubGlobal('cancelAnimationFrame', () => {});

import { AiPanel, type AiPanelProps } from './AiPanel';

const LAYER: EditorLayer = {
  id: 'src',
  kind: 'image',
  role: 'background',
  label: 'Original',
  zOrder: 1,
  visible: true,
  locked: false,
  opacity: 1,
  blendMode: 'normal',
  transform: { x: 0, y: 0, scale: 1, rotation: 0, flipX: false, flipY: false },
  width: 1280,
  height: 720,
  imageUrl: '/img/src',
  matteUrl: null,
  content: null,
  segmentationPrompt: null,
};

function mount(over: Partial<Omit<AiPanelProps, 'onRun'>> = {}) {
  const onRun = vi.fn();
  const utils = render(
    <MantineProvider>
      <AiPanel
        tool="instruct"
        onPickTool={() => {}}
        layer={LAYER}
        hasMask={false}
        canvas={{ width: 1280, height: 720 }}
        unavailable={{}}
        lastRun={{}}
        running={null}
        subjectControls={null}
        brushControls={null}
        {...over}
        onRun={onRun}
      />
    </MantineProvider>,
  );
  return { onRun, ...utils };
}

/** The whole control, which lists the chosen pictures in the order the AI will see them. */
const control = () => screen.getByTestId('reference-picture');
/** One line per chosen picture, e.g. "1st: Dana.png"; empty when none is chosen. */
const items = () => screen.queryAllByTestId('reference-picture-item').map((row) => row.textContent);
const uploadInput = () => screen.getByLabelText('Upload a reference picture') as HTMLInputElement;
const changePrompt = (text: string) =>
  fireEvent.change(screen.getByLabelText('What to change'), { target: { value: text } });
const run = () => fireEvent.click(screen.getByRole('button', { name: 'Make the change' }));
const removePicture = (name: string) => fireEvent.click(screen.getByRole('button', { name: `Remove ${name}` }));

/** Opens the picture picker and adds one of the project's pictures to the end of the list. */
function addPicture(name: string) {
  fireEvent.click(screen.getByRole('combobox', { name: 'Add a reference picture' }));
  fireEvent.click(screen.getByRole('option', { name }));
}

const pick = (name: string, type = 'image/jpeg') =>
  fireEvent.change(uploadInput(), { target: { files: [new File(['x'], name, { type })] } });

beforeEach(() => {
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

const PICTURES = [
  { value: 'ref-1', label: 'Dana.png' },
  { value: 'ref-2', label: 'Badge.png' },
  { value: 'ref-3', label: 'Logo.png' },
  { value: 'ref-4', label: 'Badge-2.png' },
  { value: 'ref-5', label: 'Logo-2.png' },
];

describe('the reference picture control', () => {
  it('is offered for "edit with words" and for no other tool', () => {
    const first = mount();
    expect(control()).toBeTruthy();
    first.unmount();

    const others: EditImageOperation[] = [
      'erase', 'fill', 'expand', 'replaceBackground', 'relight', 'upscale', 'generate', 'removeBackground',
    ];
    for (const tool of others) {
      const utils = mount({ tool });
      expect(screen.queryByTestId('reference-picture')).toBeNull();
      utils.unmount();
    }
  });

  it('names the picture chosen from the project, by position, and carries it into the run', () => {
    const { onRun } = mount({ referencePictures: PICTURES });
    changePrompt('replace the face with this person');
    addPicture('Dana.png');
    expect(items()).toEqual(['1st: Dana.png']);

    run();
    expect(onRun).toHaveBeenCalledWith('instruct', expect.objectContaining({ referenceProjectFileIds: ['ref-1'] }));
  });

  it('adds a second and a third picture in that order, and removes one without disturbing the rest', () => {
    const { onRun } = mount({ referencePictures: PICTURES });
    changePrompt('put the badge from the second picture on her jacket');
    addPicture('Dana.png');
    addPicture('Badge.png');
    addPicture('Logo.png');
    expect(items()).toEqual(['1st: Dana.png', '2nd: Badge.png', '3rd: Logo.png']);

    removePicture('Dana.png');
    expect(items()).toEqual(['1st: Badge.png', '2nd: Logo.png']);

    run();
    expect(onRun).toHaveBeenCalledWith('instruct', expect.objectContaining({ referenceProjectFileIds: ['ref-2', 'ref-3'] }));
  });

  it('stops at four pictures, says why, and still runs with the four', () => {
    const { onRun } = mount({ referencePictures: PICTURES, onUploadReference: vi.fn() });
    changePrompt('make the shirt match the first picture');
    for (const name of ['Dana.png', 'Badge.png', 'Logo.png', 'Badge-2.png']) addPicture(name);
    expect(items()).toEqual(['1st: Dana.png', '2nd: Badge.png', '3rd: Logo.png', '4th: Badge-2.png']);

    // Nothing offers a fifth: no picker and no upload button, and the reason is on screen.
    expect(screen.queryByRole('combobox', { name: 'Add a reference picture' })).toBeNull();
    expect(screen.queryByLabelText('Upload a reference picture')).toBeNull();
    expect(screen.getByTestId('reference-picture-full').textContent).toContain('4');
    expect(screen.getByTestId('reference-picture-full').textContent).toContain('Remove one to add another.');

    run();
    expect(onRun).toHaveBeenCalledWith('instruct', expect.objectContaining({ referenceProjectFileIds: ['ref-1', 'ref-2', 'ref-3', 'ref-4'] }));

    // Removing one makes room again.
    removePicture('Badge-2.png');
    expect(screen.getByRole('combobox', { name: 'Add a reference picture' })).toBeTruthy();
  });

  it('uses pictures uploaded from the device, in order, and forgets them when removed', async () => {
    const onUploadReference = vi
      .fn()
      .mockResolvedValueOnce({ value: 'ref-9', label: 'me.jpg' })
      .mockResolvedValueOnce({ value: 'ref-10', label: 'badge.png' });
    const { onRun } = mount({ onUploadReference });
    changePrompt('replace the face with this person and put the badge from the second picture on her jacket');
    pick('me.jpg');

    await waitFor(() => expect(onUploadReference).toHaveBeenCalledTimes(1));
    expect(onUploadReference.mock.calls[0][0]).toBeInstanceOf(File);
    pick('badge.png');
    await waitFor(() => expect(onUploadReference).toHaveBeenCalledTimes(2));
    expect(items()).toEqual(['1st: me.jpg', '2nd: badge.png']);

    run();
    expect(onRun.mock.calls[0][1]).toMatchObject({ referenceProjectFileIds: ['ref-9', 'ref-10'] });

    removePicture('me.jpg');
    expect(items()).toEqual(['1st: badge.png']);
    run();
    expect(onRun.mock.calls[1][1]).toMatchObject({ referenceProjectFileIds: ['ref-10'] });

    removePicture('badge.png');
    expect(items()).toEqual([]);
    run();
    expect(onRun.mock.calls[2][1]).toMatchObject({ referenceProjectFileIds: [] });
  });

  it('runs without a reference when none is chosen', () => {
    const { onRun } = mount({ referencePictures: PICTURES });
    changePrompt('make the shirt red');
    run();
    expect(onRun).toHaveBeenCalledWith('instruct', expect.objectContaining({ referenceProjectFileIds: [] }));
    expect(items()).toEqual([]);
  });

  it('says so when the upload fails, in place of a silent no-op', async () => {
    const onUploadReference = vi.fn().mockRejectedValue(new Error('That file is too large.'));
    mount({ onUploadReference });
    pick('big.jpg');
    expect((await screen.findByRole('alert')).textContent).toBe("Couldn’t upload big.jpg. That file is too large.");
  });

  it('stays inert, with the server\'s own reason, where this workspace cannot run the tool', () => {
    mount({
      unavailable: { instruct: 'No image editing provider is set up for this workspace.' },
      onUploadReference: vi.fn().mockResolvedValue({ value: 'ref-9', label: 'me.jpg' }),
    });
    expect(screen.getByText('No image editing provider is set up for this workspace.')).toBeTruthy();
    expect((screen.getByRole('combobox', { name: 'Add a reference picture' }) as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Upload a picture' }) as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('the upscale factor control', () => {
  it('offers x2 and no higher factor', () => {
    mount({ tool: 'upscale' });
    expect(screen.getByRole('radio', { name: '2×' })).toBeTruthy();
    expect(screen.queryByRole('radio', { name: '4×' })).toBeNull();
    expect(screen.queryAllByRole('radio').length).toBe(1);
  });
});
