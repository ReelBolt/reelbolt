import { test, expect } from '../../../fixtures/editor-v2/lane-b-test';
import type { EditorPage } from '../../../fixtures/editor-v2/editor-page';
import { clipsTimeline, toastWith, type ClipSpec } from '../../../fixtures/editor-v2/lane-a';
import { type Timeline } from '../../../fixtures/editor-v2/api';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { selectOnTrack, trackIndex } from '../../../fixtures/editor-v2/lane-b';

/**
 * R07.1 / R07.3 / R07.4 (T-016): the clip-depth front door. Every assertion reads the STORED document.
 * The seeded timeline runs at 30 fps; V1 holds clipA [0,150) first.
 */

type Key = [number, number];
type Channel = { value?: number; keys?: Array<{ frame: number; value: number; interp: string }> | null };
type MediaItem = { id: string; type: string; name: string; recordIn: number; recordOut: number; sourceIn?: number; playbackRate?: number; fadeInFrames?: number; fadeOutFrames?: number; gainDb?: Channel | null; transform?: Record<string, Channel> | null; transitionType?: string };

const V1 = (tl: Timeline) => tl.document.tracks[trackIndex(tl.document, 'video')].items as unknown as MediaItem[];
const A1 = (tl: Timeline) => tl.document.tracks[trackIndex(tl.document, 'audio')].items as unknown as MediaItem[];
const keys = (item: MediaItem, param: string): Key[] => (item.transform?.[param]?.keys ?? []).map((k) => [k.frame, k.value]);

async function undoOnce(ed: EditorPage): Promise<void> {
  await ed.openPanelTab(/history/i);
  await ed.tap(ed.testId('history-undo'));
}

async function selectFirstClip(ed: EditorPage): Promise<void> {
  await selectOnTrack(ed, 'video', 30);
}

/** What each preset must write on a 150-frame clip at 30 fps with its default strength and length. */
const EXPECTED: Record<string, Record<string, Key[]>> = {
  zoomIn: { scale: [[0, 1], [30, 1.2]] },
  zoomOut: { scale: [[0, 1.2], [30, 1]] },
  pop: { scale: [[0, 0.6], [13, 1.1], [18, 1]], opacity: [[0, 0], [7, 1]] },
  slideLeft: { positionX: [[0, -1], [18, 0]] },
  slideRight: { positionX: [[0, 1], [18, 0]] },
  slideTop: { positionY: [[0, -1], [18, 0]] },
  slideBottom: { positionY: [[0, 1], [18, 0]] },
  fade: { opacity: [[0, 0], [18, 1]] },
  kenBurns: { scale: [[0, 1], [149, 1.15]], positionX: [[0, 0], [149, -0.0375]], positionY: [[0, 0], [149, -0.015]] },
};

test.describe('R07.1 Animate presets', () => {
  for (const [preset, channels] of Object.entries(EXPECTED)) {
    test(`R07.1 the ${preset} preset writes its keyframes and one Undo takes them all back`, async ({ freshTimeline, openEditor }) => {
      runOn('desktop');
      const ed = await openEditor(await freshTimeline('seeded'));
      await selectFirstClip(ed);
      await ed.tap(ed.testId(`animate-${preset}`));
      const done = await ed.expectServer((tl) => Object.keys(channels).every((p) => keys(V1(tl)[0], p).length === channels[p].length), `${preset} keys are stored`);
      for (const [param, expected] of Object.entries(channels)) expect(keys(V1(done)[0], param), `${preset} ${param}`).toEqual(expected);
      await expect(ed.testId('animate-state')).toContainText('Animated');
      await undoOnce(ed);
      await ed.expectServer((tl) => ['positionX', 'positionY', 'scale', 'opacity'].every((p) => keys(V1(tl)[0], p).length === 0), 'one Undo removes every key the preset wrote');
    });
  }

  test('R07.1 a second preset replaces the first; Remove animation clears; Fine-tune keeps the diamonds', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    await selectFirstClip(ed);
    await ed.tap(ed.testId('animate-zoomIn'));
    await ed.expectServer((tl) => keys(V1(tl)[0], 'scale').length === 2, 'zoom keys');
    await ed.tap(ed.testId('animate-slideLeft'));
    const tl = await ed.expectServer((t) => keys(V1(t)[0], 'positionX').length === 2 && keys(V1(t)[0], 'scale').length === 0, 'the slide replaced the zoom');
    expect(keys(V1(tl)[0], 'positionX')).toEqual(EXPECTED.slideLeft.positionX);
    await expect(ed.testId('inspector-key-opacity')).toBeHidden(); // the diamonds are collapsed under Fine-tune
    await ed.tap(ed.testId('inspector-finetune-toggle'));
    await expect(ed.testId('inspector-key-opacity')).toBeVisible();
    await expect(ed.testId('inspector-lane-positionX')).toContainText('2');
    await ed.tap(ed.testId('animate-remove'));
    await ed.expectServer((t) => ['positionX', 'positionY', 'scale', 'opacity'].every((p) => keys(V1(t)[0], p).length === 0), 'Remove animation clears every key');
  });

  test('R07.1 slide timing "Goes out" and a typed length are stored', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    await selectFirstClip(ed);
    await ed.tap(ed.testId('animate-slideRight'));
    await ed.expectServer((tl) => keys(V1(tl)[0], 'positionX').length === 2, 'slide keys');
    await ed.testId('animate-seconds').fill('1');
    await ed.testId('animate-seconds').press('Enter');
    await ed.expectServer((tl) => JSON.stringify(keys(V1(tl)[0], 'positionX')) === JSON.stringify([[0, 1], [30, 0]]), 'the slide now takes one second (30 frames)');
    await ed.tap(ed.testId('animate-timing').getByText('Goes out'));
    const tl = await ed.expectServer((t) => JSON.stringify(keys(V1(t)[0], 'positionX')) === JSON.stringify([[119, 0], [149, 1]]), 'the slide leaves over the last second');
    expect(keys(V1(tl)[0], 'positionX')[1]).toEqual([149, 1]);
  });
});

test.describe('R07.3 plain-unit clip controls', () => {
  test('R07.3 opacity in percent stores the exact fraction; an out-of-range value is refused with a sentence and the field reverts', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    await selectFirstClip(ed);
    const opacity = ed.testId('inspector-opacity');
    await opacity.fill('35');
    await opacity.press('Enter');
    await ed.expectServer((tl) => V1(tl)[0].transform?.opacity?.value === 0.35, 'opacity 35% is stored as 0.35');
    await opacity.fill('150');
    await opacity.press('Enter');
    await expect(toastWith(ed, /opacity is between 0% and 100%/i)).toBeVisible();
    await opacity.blur();
    await expect(opacity, 'the field shows the stored 35 again').toHaveValue('35');
    await ed.tap(ed.testId('inspector-reset-transform'));
    await ed.expectServer((tl) => !V1(tl)[0].transform, 'Reset clears the transform section');
  });

  test('R07.3 fades in seconds store exact frames; a fade that does not fit is refused with a sentence; Auto fade and Reset', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    await selectFirstClip(ed);
    await ed.testId('inspector-fade-in').fill('0.5');
    await ed.testId('inspector-fade-in').press('Enter');
    await ed.expectServer((tl) => V1(tl)[0].fadeInFrames === 15, 'a 0.5 s fade in is 15 frames');
    await ed.testId('inspector-fade-out').fill('99');
    await ed.testId('inspector-fade-out').press('Enter');
    await expect(toastWith(ed, /longer than the room left/i)).toBeVisible();
    await ed.testId('inspector-fade-out').blur();
    await expect(ed.testId('inspector-fade-out')).toHaveValue('0');
    await ed.tap(ed.testId('inspector-auto-fade'));
    await ed.expectServer((tl) => V1(tl)[0].fadeInFrames === 15 && V1(tl)[0].fadeOutFrames === 15, 'Auto fade sets a half-second fade at both ends');
    await ed.tap(ed.testId('inspector-reset-fades'));
    await ed.expectServer((tl) => !V1(tl)[0].fadeInFrames && !V1(tl)[0].fadeOutFrames, 'Reset clears both fades');
  });

  test('R07.3 the on-clip fade corner is draggable and moves by a frame with the arrow keys', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    await selectFirstClip(ed);
    const handle = ed.testId('fade-handle-in');
    await expect(handle).toBeVisible();
    const box = (await handle.boundingBox())!;
    const cx = box.x + box.width / 2;
    const cy = box.y + 8;
    await ed.mouseDrag(cx, cy, cx + 60, cy);
    const dragged = await ed.expectServer((tl) => (V1(tl)[0].fadeInFrames ?? 0) > 4, 'dragging the corner stores a fade in');
    const frames = V1(dragged)[0].fadeInFrames!;
    await ed.testId('fade-handle-in').focus();
    await ed.page.keyboard.press('ArrowRight');
    await ed.expectServer((tl) => V1(tl)[0].fadeInFrames === frames + 1, 'an arrow key moves the fade by one frame');
    await ed.testId('fade-handle-in').press('ArrowLeft');
    await ed.expectServer((tl) => V1(tl)[0].fadeInFrames === frames, 'and back');
  });

  test('R07.3 speed chips store the exact speed, update the clip length and say how long the clip is now', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    await selectFirstClip(ed);
    await ed.tap(ed.testId('inspector-speed-chip-2'));
    const tl = await ed.expectServer((t) => V1(t)[0].playbackRate === 2, 'speed 2 is stored');
    expect(V1(tl)[0].recordOut - V1(tl)[0].recordIn, 'half as long').toBe(75);
    expect(V1(tl)[1].recordIn, 'the next clip rippled left').toBe(75);
    await expect(ed.testId('inspector-speed-readout')).toContainText('Clip is now 2.5 s');
    await ed.tap(ed.testId('inspector-speed-chip-0.5'));
    await ed.expectServer((t) => V1(t)[0].playbackRate === 0.5 && V1(t)[0].recordOut === 300, 'half speed doubles the length');
    await expect(ed.testId('inspector-speed-readout')).toContainText('Clip is now 10 s');
    await ed.testId('inspector-speed').fill('40');
    await ed.testId('inspector-speed').press('Enter');
    await expect(toastWith(ed, /speed is between/i)).toBeVisible();
    await ed.tap(ed.testId('inspector-reset-speed'));
    await ed.expectServer((t) => V1(t)[0].playbackRate === 1 && V1(t)[0].recordOut === 150, 'Reset puts the speed back');
  });

  test('R07.3 volume in percent with Mute: exact dB stored, unmute restores, Reset is 100%', async ({ freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('seeded'));
    await selectOnTrack(ed, 'audio', 30);
    await ed.testId('inspector-volume').fill('50');
    await ed.testId('inspector-volume').press('Enter');
    await ed.expectServer((tl) => A1(tl)[0].gainDb?.value === -6.02, '50% volume is -6.02 dB');
    // A person presses the switch's label (Mantine's track label sits over the input, so a click on the input is intercepted): press the label that belongs to the input.
    const mute = ed.page.getByRole('switch', { name: 'Mute', exact: true });
    const muteLabel = ed.page.locator(`label[for="${await mute.getAttribute('id')}"]`);
    await muteLabel.click();
    await expect(mute).toBeChecked();
    await ed.expectServer((tl) => A1(tl)[0].gainDb?.value === -60, 'Mute stores the quietest gain');
    await muteLabel.click();
    await expect(mute).not.toBeChecked();
    await ed.expectServer((tl) => A1(tl)[0].gainDb?.value === -6.02, 'Unmute brings the volume back');
    await ed.testId('inspector-volume').fill('500');
    await ed.testId('inspector-volume').press('Enter');
    await expect(toastWith(ed, /volume is between 0% and 200%/i)).toBeVisible();
    await ed.tap(ed.testId('inspector-reset-volume'));
    await ed.expectServer((tl) => (A1(tl)[0].gainDb?.value ?? 0) === 0, 'Reset is 100% (0 dB)');
    await ed.tap(ed.testId('inspector-advanced-toggle'));
    await expect(ed.testId('inspector-gainDb'), 'the dB box is still there under Advanced').toBeVisible();
  });
});

const handles = (sourceIn: number): ClipSpec[] => [
  { name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 90 },
  { name: 'clipB.mp4', file: 'clipB', recordIn: 90, recordOut: 180, extra: { sourceIn } },
];
const three = (secondHandle: number, thirdHandle: number): ClipSpec[] => [
  { name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 90 },
  { name: 'clipB.mp4', file: 'clipB', recordIn: 90, recordOut: 180, extra: { sourceIn: secondHandle } },
  { name: 'vertical.mp4', file: 'vertical', recordIn: 180, recordOut: 270, extra: { sourceIn: thirdHandle } },
];
const transitions = (tl: Timeline) => V1(tl).filter((i) => i.type === 'transition');

test.describe('R07.4 transitions at the seam', () => {
  test('R07.4 the + on a cut adds the chosen word at the chosen length; one Undo removes it', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, handles(60)));
    await ed.tap(ed.page.locator('[data-testid="seam-add"][data-cut-frame="90"]'));
    await expect(ed.testId('seam-picker')).toBeVisible();
    await ed.tap(ed.testId('transition-word-wipeleft'));
    await expect(ed.testId('transition-parity')).toHaveAttribute('data-parity', 'approximate');
    await ed.tap(ed.testId('transition-seconds-1'));
    await ed.tap(ed.testId('transition-add'));
    const tl = await ed.expectServer((t) => transitions(t).length === 1, 'a transition item is stored');
    const tr = transitions(tl)[0];
    expect(tr.transitionType).toBe('wipeleft');
    expect(tr.name, 'the item name follows the word').toBe('Wipe left');
    expect([tr.recordIn, tr.recordOut], 'one second, centred on the cut').toEqual([75, 105]);
    await expect(ed.page.locator('[data-testid="seam-edit"]')).toBeVisible();
    await undoOnce(ed);
    await ed.expectServer((t) => transitions(t).length === 0 && V1(t)[0].recordOut === 90, 'one Undo restores the hard cut');
  });

  test('R07.4 the parity line says faithful for a dissolve and approximate for the rest, beside the picker', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, handles(60)));
    await ed.tap(ed.page.locator('[data-testid="seam-add"][data-cut-frame="90"]'));
    await expect(ed.testId('transition-parity')).toHaveAttribute('data-parity', 'faithful');
    await ed.tap(ed.testId('transition-word-slideleft'));
    await expect(ed.testId('transition-parity')).toContainText(/previews as a dissolve|shows a dissolve/i);
    await expect(ed.testId('transition-parity')).toContainText(/slide left/i);
  });

  test('R07.4 Apply to all cuts adds one transition per cut as one undo step', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, three(60, 60)));
    await ed.tap(ed.page.locator('[data-testid="seam-add"][data-cut-frame="90"]'));
    await ed.tap(ed.testId('transition-apply-all'));
    const tl = await ed.expectServer((t) => transitions(t).length === 2, 'both cuts have a transition');
    expect(transitions(tl).map((t) => t.transitionType)).toEqual(['dissolve', 'dissolve']);
    await undoOnce(ed);
    await ed.expectServer((t) => transitions(t).length === 0, 'one Undo removes both');
  });

  test('R07.4 Apply to all cuts skips a cut with no spare footage and says so', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, three(60, 0)));
    await ed.tap(ed.page.locator('[data-testid="seam-add"][data-cut-frame="90"]'));
    await ed.tap(ed.testId('transition-apply-all'));
    await ed.expectServer((t) => transitions(t).length === 1, 'only the cut that has room got one');
    await expect(toastWith(ed, /Added to 1 of 2 cuts/i)).toBeVisible();
  });

  test('R07.4 Make room trims the next clip head so the transition fits', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, handles(0)));
    await ed.tap(ed.page.locator('[data-testid="seam-add"][data-cut-frame="90"]'));
    await expect(ed.testId('transition-add')).toBeDisabled();
    await expect(ed.testId('transition-note')).toContainText(/no spare footage/i);
    await ed.tap(ed.testId('transition-make-room'));
    const trimmed = await ed.expectServer((t) => (V1(t)[1].sourceIn ?? 0) >= 7, 'the next clip start is trimmed (its source offset grew)');
    expect(V1(trimmed)[1].sourceIn).toBe(7);
    await expect(ed.testId('transition-add')).toBeEnabled();
    await ed.tap(ed.testId('transition-add'));
    const tl = await ed.expectServer((t) => transitions(t).length === 1, 'the transition is stored once there is room');
    expect(transitions(tl)[0].transitionType).toBe('dissolve');
  });

  test('R07.4 Make room says why not when the next clip is too short', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(
      await clipsTimeline(freshTimeline, api, seed, [
        { name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 90 },
        { name: 'clipB.mp4', file: 'clipB', recordIn: 90, recordOut: 96 },
      ]),
    );
    await ed.tap(ed.page.locator('[data-testid="seam-add"][data-cut-frame="90"]'));
    await expect(ed.testId('transition-note')).toContainText(/only 6 frames long/i);
    await expect(ed.testId('transition-make-room')).toBeDisabled();
  });

  test('R07.4 the glyph on an existing transition changes the word and removes it', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, handles(60)));
    await ed.tap(ed.page.locator('[data-testid="seam-add"][data-cut-frame="90"]'));
    await ed.tap(ed.testId('transition-add'));
    await ed.expectServer((t) => transitions(t).length === 1, 'dissolve added');
    await ed.tap(ed.testId('seam-edit'));
    await ed.tap(ed.testId('transition-word-circleopen'));
    await ed.expectServer((t) => transitions(t)[0]?.transitionType === 'circleopen' && transitions(t)[0]?.name === 'Circle open', 'the word and the item name changed together');
    await ed.tap(ed.testId('transition-remove'));
    await ed.expectServer((t) => transitions(t).length === 0, 'the transition is gone');
  });
});

test.describe('R07 tablet (1024x768, coarse pointer)', () => {
  test.use({ viewport: { width: 1024, height: 768 }, hasTouch: true });

  test('R07 tablet: preset chips, the seam +, the fade corner and the transition buttons are >= 44 px and work by touch', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, handles(60)));
    expect(await ed.page.evaluate(() => window.matchMedia('(pointer: coarse)').matches), 'the page sees a coarse pointer').toBe(true);
    const plus = ed.page.locator('[data-testid="seam-add"][data-cut-frame="90"]');
    const plusBox = (await plus.boundingBox())!;
    expect(plusBox.width).toBeGreaterThanOrEqual(44);
    expect(plusBox.height).toBeGreaterThanOrEqual(44);
    await plus.tap();
    await expect(ed.testId('seam-picker')).toBeVisible();
    for (const id of ['transition-seconds-0.5', 'transition-add']) {
      const box = (await ed.testId(id).boundingBox())!;
      expect(box.height, `${id} is a touch target`).toBeGreaterThanOrEqual(44);
    }
    await ed.testId('transition-word-slideright').tap();
    await ed.testId('transition-add').tap();
    const tl = await ed.expectServer((t) => transitions(t).length === 1, 'the transition was added by touch');
    expect(transitions(tl)[0].transitionType).toBe('slideright');

    // The selected clip: its fade corner is a touch target and an Animate chip writes keys.
    await ed.selectClipAt(20);
    const fade = (await ed.testId('fade-handle-in').boundingBox())!;
    expect(fade.width).toBeGreaterThanOrEqual(44);
    expect(fade.height).toBeGreaterThanOrEqual(44);
    await ed.openInspector();
    const chip = (await ed.testId('animate-zoomIn').boundingBox())!;
    expect(chip.height).toBeGreaterThanOrEqual(44);
    await ed.testId('animate-zoomIn').tap();
    await ed.expectServer((t) => keys(V1(t)[0], 'scale').length === 2, 'the zoom keys are stored');
    const speed = (await ed.testId('inspector-speed-chip-2').boundingBox())!;
    expect(speed.height).toBeGreaterThanOrEqual(44);
  });
});

test.describe('R07 phone: the Inspector sheet', () => {
  test('R07.3 phone: opacity percent, fade seconds and a speed chip are stored; an Animate preset is one Undo', async ({ freshTimeline, openEditor }) => {
    runOn('pixel7', 'iphone14webkit');
    const ed = await openEditor(await freshTimeline('seeded'));
    await selectFirstClip(ed);
    await ed.testId('inspector-opacity').fill('60');
    await ed.testId('inspector-opacity').press('Enter');
    await ed.expectServer((tl) => V1(tl)[0].transform?.opacity?.value === 0.6, 'opacity 60% is stored as 0.6');
    await ed.testId('inspector-fade-in').fill('0.5');
    await ed.testId('inspector-fade-in').press('Enter');
    await ed.expectServer((tl) => V1(tl)[0].fadeInFrames === 15, 'a 0.5 s fade in is 15 frames');
    await ed.tap(ed.testId('inspector-speed-chip-2'));
    await ed.expectServer((tl) => V1(tl)[0].playbackRate === 2 && V1(tl)[0].recordOut === 75, 'speed 2 halves the clip');
    await ed.tap(ed.testId('animate-fade'));
    await ed.expectServer((tl) => keys(V1(tl)[0], 'opacity').length === 2, 'the fade preset wrote two opacity keys');
    await undoOnce(ed);
    await ed.expectServer((tl) => keys(V1(tl)[0], 'opacity').length === 0, 'one Undo removes the preset');
  });
});
