import { describe, expect, it } from 'vitest';
import { AI_TOOL_GROUPS, AI_TOOL_ORDER, aiToolInfo, isOperationTool, usesBrush } from './ai-tools';
import { EXPORT_FORMATS, MAX_EXPORT_BYTES, exportFormat, formatBytes } from './export-formats';
import { exportBaseName, exportFileName } from './export-name';
import { MaskPainter, type MaskCanvas } from './mask';
import { containFitTransform } from './transform-tools';
import { RecordingContext } from './__tests__/recording-context';
import { EDIT_IMAGE_OPERATIONS } from '@/lib/types/image-document';

describe('containFitTransform (a picture added to the canvas)', () => {
  const canvas = { width: 1280, height: 720 };

  it('fits a picture larger than the canvas inside it, centred, without cropping', () => {
    const t = containFitTransform({ width: 4000, height: 3000 }, canvas);
    expect(t.scale).toBeCloseTo(0.24);
    expect(4000 * t.scale).toBeLessThanOrEqual(1280);
    expect(3000 * t.scale).toBeCloseTo(720);
    expect(t.x).toBe(Math.round((1280 - 4000 * 0.24) / 2));
    expect(t.y).toBe(0);
    expect(t).toMatchObject({ rotation: 0, flipX: false, flipY: false });
  });

  it('fits by the width when the picture is wider than the canvas shape', () => {
    const t = containFitTransform({ width: 2560, height: 400 }, canvas);
    expect(t.scale).toBe(0.5);
    expect(t).toMatchObject({ x: 0, y: 260 });
  });

  it('keeps a picture that already fits at its natural size, centred', () => {
    expect(containFitTransform({ width: 200, height: 100 }, canvas)).toMatchObject({ scale: 1, x: 540, y: 310 });
  });

  it('fits a picture larger on one side only', () => {
    expect(containFitTransform({ width: 640, height: 1440 }, canvas).scale).toBe(0.5);
  });
});

describe('export names', () => {
  it('uses the chosen format\'s extension and replaces any picture extension the user typed', () => {
    expect(exportFileName('cover', '.psd')).toBe('cover.psd');
    expect(exportFileName('cover.png', '.jpg')).toBe('cover.jpg');
    expect(exportFileName('cover.JPEG', '.ora')).toBe('cover.ora');
    expect(exportFileName('', '.webp')).toBe('thumbnail.webp');
    expect(exportBaseName('../my/cover.psd')).toBe('my cover');
  });
});

describe('export formats', () => {
  it('offers the flat formats and the two layered ones, each with its own type and extension', () => {
    expect(EXPORT_FORMATS.map((f) => f.id)).toEqual(['png', 'jpeg', 'webp', 'avif', 'psd', 'ora']);
    expect(EXPORT_FORMATS.filter((f) => f.layered).map((f) => f.extension)).toEqual(['.psd', '.ora']);
    expect(new Set(EXPORT_FORMATS.map((f) => f.mime)).size).toBe(EXPORT_FORMATS.length);
    expect(exportFormat('jpeg').transparency).toBe(false);
  });

  it('matches the server\'s 200 MiB export limit and says sizes plainly', () => {
    expect(MAX_EXPORT_BYTES).toBe(200 * 1024 * 1024);
    expect(formatBytes(512)).toBe('512 bytes');
    expect(formatBytes(12_400_000)).toBe('12.4 MB');
  });
});

describe('AI tools', () => {
  it('lists every operation once, plus cutting out a subject and designing a graphic', () => {
    expect([...AI_TOOL_ORDER].sort()).toEqual([...EDIT_IMAGE_OPERATIONS, 'subject', 'designGraphic'].sort());
    expect(new Set(AI_TOOL_ORDER).size).toBe(AI_TOOL_ORDER.length);
    expect(AI_TOOL_GROUPS.length).toBeGreaterThan(1);
  });

  it('gives each a title and one plain sentence, and arms the right canvas tool', () => {
    for (const id of AI_TOOL_ORDER) {
      const info = aiToolInfo(id);
      expect(info.title.length).toBeGreaterThan(0);
      expect(info.description).toMatch(/\.$/);
    }
    expect(usesBrush('erase')).toBe(true);
    expect(usesBrush('fill')).toBe(true);
    expect(usesBrush('upscale')).toBe(false);
    expect(usesBrush(null)).toBe(false);
    expect(aiToolInfo('subject').canvasTool).toBe('subject');
  });

  it('offers "Design a graphic" with the new-picture tools, needing no layer and no canvas tool', () => {
    const info = aiToolInfo('designGraphic');
    expect(info.title).toBe('Design a graphic');
    expect(info.needsLayer).toBe(false);
    expect(info.canvasTool).toBeNull();
    expect(usesBrush('designGraphic')).toBe(false);
    expect(AI_TOOL_GROUPS.find((g) => g.title === 'Make something new')?.tools).toContain('designGraphic');
    expect(isOperationTool('designGraphic')).toBe(false);
    expect(isOperationTool('subject')).toBe(false);
    expect(isOperationTool('generate')).toBe(true);
  });
});

describe('the painted area of a brush mask', () => {
  const painter = () =>
    new MaskPainter(
      (width, height) => {
        const ctx = new RecordingContext();
        return { width, height, getContext: () => ctx.as() } as MaskCanvas;
      },
      400,
      300,
    );

  it('is nothing before a stroke, then the box around every stroke, clamped to the layer', () => {
    const p = painter();
    expect(p.paintedBounds).toBeNull();
    p.stroke({ x: 100, y: 100 }, { x: 150, y: 120 }, { size: 20, erase: false });
    expect(p.paintedBounds).toEqual({ x: 90, y: 90, w: 70, h: 40 });
    p.stroke({ x: 395, y: 5 }, { x: 395, y: 5 }, { size: 40, erase: false });
    expect(p.paintedBounds).toEqual({ x: 90, y: 0, w: 310, h: 130 });
    p.clear();
    expect(p.paintedBounds).toBeNull();
  });

  it('is not grown by rubbing out', () => {
    const p = painter();
    p.stroke({ x: 10, y: 10 }, { x: 10, y: 10 }, { size: 4, erase: true });
    expect(p.paintedBounds).toBeNull();
  });
});
