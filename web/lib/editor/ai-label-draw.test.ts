// @vitest-environment jsdom
/** The label as it is drawn on an exported picture: a dark box in the bottom-right corner with white text. */
import { describe, expect, it } from 'vitest';
import { RecordingContext } from './__tests__/recording-context';
import { aiLabelLayout, aiLabelStyle } from './ai-label';
import { drawAiLabel } from './canvas-dom';

function canvasOf(width: number, height: number, ctx: RecordingContext | null) {
  return { width, height, getContext: () => (ctx ? ctx.as() : null) } as unknown as HTMLCanvasElement;
}

describe('drawAiLabel', () => {
  it('draws the box, then the words in white, where the layout says', () => {
    const ctx = new RecordingContext();
    const box = drawAiLabel(canvasOf(1280, 720, ctx), 'AI-generated');
    // the recording context measures 10 px per character
    expect(box).toEqual(aiLabelLayout(1280, 720, 120, aiLabelStyle(1280, 720)));
    const [rect] = ctx.of('rect');
    expect(rect.args).toEqual([box!.x, box!.y, box!.width, box!.height]);
    const [fill] = ctx.of('fill');
    expect(fill.fillStyle).toBe('rgba(0, 0, 0, 0.62)');
    const [text] = ctx.of('fillText');
    expect(text.args).toEqual(['AI-generated', box!.textX, box!.textY]);
    expect(text.fillStyle).toBe('#ffffff');
    expect(text.font).toContain('14px');
    expect(text.alpha).toBe(1);
    expect(text.composite).toBe('source-over');
    // the box is under the text, and the whole label is inside the picture's bottom-right quarter
    expect(ctx.ops().indexOf('fill')).toBeLessThan(ctx.ops().indexOf('fillText'));
    expect(box!.x).toBeGreaterThan(640);
    expect(box!.y).toBeGreaterThan(360);
    expect(box!.x + box!.width).toBeLessThanOrEqual(1280);
    expect(box!.y + box!.height).toBeLessThanOrEqual(720);
  });

  it('leaves the picture\'s own drawing state as it found it', () => {
    const ctx = new RecordingContext();
    ctx.globalAlpha = 0.4;
    ctx.fillStyle = '#123456';
    drawAiLabel(canvasOf(1920, 1080, ctx), 'AI-generated');
    expect(ctx.ops()[0]).toBe('save');
    expect(ctx.ops().at(-1)).toBe('restore');
    expect(ctx.globalAlpha).toBe(0.4);
    expect(ctx.fillStyle).toBe('#123456');
  });

  it('answers null where the browser cannot draw', () => {
    expect(drawAiLabel(canvasOf(1280, 720, null), 'AI-generated')).toBeNull();
  });
});
