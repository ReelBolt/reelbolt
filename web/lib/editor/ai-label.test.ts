/** The "AI-generated" label: what a layer's content says, whether an export needs it, and where it goes. */
import { describe, expect, it } from 'vitest';
import { AI_LABEL_MIN_FONT_PX, aiLabelLayout, aiLabelStyle, aiMarkNames, aiMarkOf, needsAiLabel, type LayerAiMark } from './ai-label';
import { layerFromDto } from './model';
import type { EditImageLayer } from '@/lib/types/image-document';

const mark = (disclosure: LayerAiMark['disclosure']): LayerAiMark => ({ subjects: [{ avatarId: 'a', name: 'Anna', kind: 'Person' }], aiGenerated: true, disclosure });

describe('reading a layer\'s content', () => {
  it('reads who a picture shows and how it must be marked', () => {
    expect(aiMarkOf({ subjects: [{ avatarId: 'a1', name: 'Anna', kind: 'Person' }, { avatarId: 'o1', name: ' Bottle ', kind: 'Object' }], aiGenerated: true, disclosure: 'Visible' })).toEqual({
      subjects: [{ avatarId: 'a1', name: 'Anna', kind: 'Person' }, { avatarId: 'o1', name: 'Bottle', kind: 'Object' }],
      aiGenerated: true,
      disclosure: 'Visible',
    });
    expect(aiMarkNames(aiMarkOf({ subjects: [{ name: 'Anna' }, { name: 'Bottle', kind: 'object' }] }))).toEqual(['Anna', 'Bottle']);
  });

  it('reads an object-only picture as marked in its data only', () => {
    expect(aiMarkOf({ subjects: [{ avatarId: 'o1', name: 'Bottle', kind: 'Object' }], aiGenerated: true, disclosure: 'MetadataOnly' })?.disclosure).toBe('MetadataOnly');
  });

  it('is tolerant: no content, text content and anything unexpected say nothing', () => {
    expect(aiMarkOf(null)).toBeNull();
    expect(aiMarkOf(undefined)).toBeNull();
    expect(aiMarkOf('Visible')).toBeNull();
    expect(aiMarkOf([])).toBeNull();
    expect(aiMarkOf({})).toBeNull();
    expect(aiMarkOf({ subjects: 'Anna' })).toBeNull();
    expect(aiMarkOf({ text: 'SALE', fontFamily: 'Inter', disclosure: 'Visible' })).toBeNull();
    expect(aiMarkOf({ subjects: [{ avatarId: 'a' }, null, { name: '  ' }] })).toBeNull();
  });

  it('never shows a label on a word it does not know, and keeps one the content asks for', () => {
    expect(aiMarkOf({ subjects: [{ name: 'Anna' }], disclosure: 'SomethingNew' })?.disclosure).toBe('MetadataOnly');
    expect(aiMarkOf({ subjects: [{ name: 'Anna' }] })?.disclosure).toBe('MetadataOnly');
    expect(aiMarkOf({ aiGenerated: true, disclosure: 'visible' })).toEqual({ subjects: [], aiGenerated: true, disclosure: 'Visible' });
    expect(aiMarkOf({ aiGenerated: true })).toEqual({ subjects: [], aiGenerated: true, disclosure: 'MetadataOnly' });
  });

  it('the editor\'s layer carries it for a picture layer, and never for a text layer or an older server', () => {
    const dto = (over: Partial<EditImageLayer>): EditImageLayer => ({
      id: 'l', zOrder: 1, role: 'overlay', kind: 'Generated' as never, label: 'Picture', sourceProjectFileId: null, imageUrl: '/i', matteUrl: null, mimeType: 'image/png',
      width: 100, height: 100, opacity: 1, transform: { x: 0, y: 0, scale: 1, rotation: 0 }, segmentationModel: null, segmentationPrompt: null, createdAt: '', ...over,
    });
    expect(layerFromDto(dto({ content: { subjects: [{ avatarId: 'a', name: 'Anna', kind: 'Person' }], aiGenerated: true, disclosure: 'Visible' } })).aiMark).toMatchObject({ disclosure: 'Visible' });
    expect(layerFromDto(dto({ content: { subjects: [{ avatarId: 'a', name: 'Anna', kind: 'Person' }], aiGenerated: true, disclosure: 'Visible' } })).content).toBeNull();
    expect(layerFromDto(dto({ content: null })).aiMark).toBeNull();
    expect(layerFromDto(dto({})).aiMark).toBeNull();
    const text = layerFromDto(dto({ kind: 'Text' as never, content: { text: 'Hi', fontFamily: 'Inter', fontWeight: 700, fontSize: 40, lineHeight: 1.1, letterSpacing: 0, align: 'left', color: '#fff', stroke: null, shadow: null, maxWidth: null, uppercase: false } }));
    expect(text.aiMark).toBeNull();
    expect(text.content?.text).toBe('Hi');
  });
});

describe('whether the export carries the label', () => {
  it('needs it when a visible layer asks for a visible label', () => {
    expect(needsAiLabel([{ visible: true, aiMark: mark('Visible') }])).toBe(true);
    expect(needsAiLabel([{ visible: true, aiMark: null }, { visible: true, aiMark: mark('Visible') }])).toBe(true);
  });

  it('does not need it for a hidden layer, a data-only mark, or pictures made without avatars', () => {
    expect(needsAiLabel([{ visible: false, aiMark: mark('Visible') }])).toBe(false);
    expect(needsAiLabel([{ visible: true, aiMark: mark('MetadataOnly') }])).toBe(false);
    expect(needsAiLabel([{ visible: true }, { visible: true, aiMark: null }])).toBe(false);
    expect(needsAiLabel([])).toBe(false);
  });
});

describe('where the label goes', () => {
  it('is 14 px on a 1280 px wide picture and never under 12 px', () => {
    expect(aiLabelStyle(1280, 720).fontSize).toBe(14);
    expect(aiLabelStyle(1280, 720).fontSize).toBeGreaterThanOrEqual(AI_LABEL_MIN_FONT_PX);
    expect(aiLabelStyle(640, 360).fontSize).toBe(12);
    expect(aiLabelStyle(720, 1280).fontSize).toBe(12);
    expect(aiLabelStyle(256, 256).fontSize).toBe(12);
  });

  it('grows with the picture, and a wide banner is measured by its height', () => {
    expect(aiLabelStyle(3840, 2160).fontSize).toBe(42);
    expect(aiLabelStyle(1920, 1080).fontSize).toBe(21);
    expect(aiLabelStyle(4096, 512).fontSize).toBe(aiLabelStyle(1024, 512).fontSize);
  });

  it('sits in the bottom-right corner, inside the picture, with the text inside the box', () => {
    const style = aiLabelStyle(1280, 720);
    const box = aiLabelLayout(1280, 720, 80, style);
    expect(box.scale).toBe(1);
    expect(box.width).toBe(80 + style.paddingX * 2);
    expect(box.height).toBe(style.fontSize + style.paddingY * 2);
    expect(box.x + box.width).toBe(1280 - style.margin);
    expect(box.y + box.height).toBe(720 - style.margin);
    expect(box.x).toBeGreaterThan(640);
    expect(box.y).toBeGreaterThan(360);
    expect(box.textX).toBe(box.x + style.paddingX);
    expect(box.textY).toBe(box.y + box.height / 2);
    expect(box.fontSize).toBe(14);
  });

  it('shrinks to fit a picture too small to hold it, and still stays inside', () => {
    for (const [w, h] of [[64, 64], [16, 16], [200, 20], [20, 200]] as const) {
      const box = aiLabelLayout(w, h, 80);
      expect(box.scale).toBeLessThanOrEqual(1);
      expect(box.scale).toBeGreaterThan(0);
      expect(box.x).toBeGreaterThanOrEqual(0);
      expect(box.y).toBeGreaterThanOrEqual(0);
      expect(box.x + box.width).toBeLessThanOrEqual(w + 1e-9);
      expect(box.y + box.height).toBeLessThanOrEqual(h + 1e-9);
      expect(box.fontSize).toBeLessThanOrEqual(12);
    }
    expect(aiLabelLayout(64, 64, 80).scale).toBeLessThan(1);
    expect(aiLabelLayout(16, 16, 80).fontSize).toBeLessThan(12);
    // a strip exactly as tall as the box: full size, flush with the edges
    expect(aiLabelLayout(200, 20, 80)).toMatchObject({ scale: 1, y: 0, height: 20 });
  });
});
