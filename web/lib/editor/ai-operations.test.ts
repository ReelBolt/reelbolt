import { describe, expect, it } from 'vitest';
import { EDIT_IMAGE_OPERATIONS } from '@/lib/types/image-document';
import type { ImageOperationCapabilities } from '@/lib/types/image-document';
import {
  MAX_REFERENCE_PICTURES,
  OPERATION_INFO,
  OPERATION_ORDER,
  aspectOf,
  buildOperationRequest,
  defaultExpandAspect,
  defaultParams,
  expandMarginMax,
  expandShapeSummary,
  hasShape,
  operationBlocker,
  promptRuleFor,
  shapeRatio,
  takesReferencePicture,
} from './ai-operations';

const input = (over: Partial<Parameters<typeof operationBlocker>[1]> = {}) => ({
  layerId: 'L1',
  hasMask: false,
  prompt: '',
  params: {},
  ...over,
});

describe('the operation table', () => {
  it('has one entry per contract operation, each listed once in the panel order', () => {
    expect(Object.keys(OPERATION_INFO).sort()).toEqual([...EDIT_IMAGE_OPERATIONS].sort());
    expect([...OPERATION_ORDER].sort()).toEqual([...EDIT_IMAGE_OPERATIONS].sort());
    expect(new Set(OPERATION_ORDER).size).toBe(OPERATION_ORDER.length);
  });

  it('matches the contract\'s "needs" column: masks for erase and fill, a layer for all but generate', () => {
    expect(OPERATION_INFO.erase.needsMask && OPERATION_INFO.fill.needsMask).toBe(true);
    const maskless = EDIT_IMAGE_OPERATIONS.filter((o) => o !== 'erase' && o !== 'fill');
    expect(maskless.some((o) => OPERATION_INFO[o].needsMask)).toBe(false);
    expect(OPERATION_INFO.generate.needsLayer).toBe(false);
    expect(EDIT_IMAGE_OPERATIONS.filter((o) => o !== 'generate').every((o) => OPERATION_INFO[o].needsLayer)).toBe(true);
  });

  it('speaks plainly: no vendor, model or endpoint names in any title or summary', () => {
    for (const info of Object.values(OPERATION_INFO)) {
      expect(`${info.title} ${info.summary} ${info.action}`).not.toMatch(/\b(fal|flux|kontext|sam|birefnet|lama|api|endpoint|model)\b/i);
    }
  });
});

describe('operationBlocker', () => {
  it('asks for a layer, and refuses a text layer for pixel operations', () => {
    expect(operationBlocker('upscale', input({ layerId: null }), true)).toBe('Select a layer first.');
    expect(operationBlocker('upscale', input(), false)).toContain('Text layers have no pixels');
    expect(operationBlocker('upscale', input(), true)).toBeNull();
  });

  it('needs a painted mask for erase and fill', () => {
    expect(operationBlocker('erase', input(), true)).toContain('Paint over');
    expect(operationBlocker('erase', input({ hasMask: true }), true)).toBeNull();
    expect(operationBlocker('fill', input({ hasMask: true }), true)).not.toBeNull(); // still needs the prompt
    expect(operationBlocker('fill', input({ hasMask: true, prompt: 'sky' }), true)).toBeNull();
  });

  it('needs a prompt where one is required, and not where it is optional', () => {
    expect(operationBlocker('instruct', input(), true)).not.toBeNull();
    expect(operationBlocker('instruct', input({ prompt: '  ' }), true)).not.toBeNull();
    expect(operationBlocker('relight', input(), true)).toBeNull();
  });

  it('needs at least one expansion side', () => {
    expect(operationBlocker('expand', input({ params: { left: 0 } }), true)).toContain('at least one side');
    expect(operationBlocker('expand', input({ params: { left: 128 } }), true)).toBeNull();
  });

  it('generate needs no layer, even when the selected layer is text', () => {
    expect(operationBlocker('generate', input({ layerId: null, prompt: 'a mountain' }), false)).toBeNull();
  });
});

describe('buildOperationRequest', () => {
  it('builds erase with the layer and the uploaded mask key and nothing else', () => {
    expect(buildOperationRequest('erase', input({ hasMask: true, prompt: 'ignored' }), 'mask/key.png')).toEqual({
      operation: 'erase',
      layerId: 'L1',
      maskKey: 'mask/key.png',
    });
  });

  it('builds fill with its prompt trimmed', () => {
    expect(buildOperationRequest('fill', input({ prompt: '  blue sky ' }), 'm')).toEqual({
      operation: 'fill',
      layerId: 'L1',
      maskKey: 'm',
      prompt: 'blue sky',
    });
  });

  it('sends only the sides that grow for expand', () => {
    expect(
      buildOperationRequest('expand', input({ params: { top: 0, right: 128, bottom: 0, left: 64 } })),
    ).toEqual({ operation: 'expand', layerId: 'L1', params: { right: 128, left: 64 } });
  });

  it('sends the upscale factor, defaulting to 2', () => {
    expect(buildOperationRequest('upscale', input({ params: { factor: 2 } })).params).toEqual({ factor: 2 });
    expect(buildOperationRequest('upscale', input()).params).toEqual({ factor: 2 });
  });

  it('offers the upscale factor at most x2, the most the upscalers allow', () => {
    const factor = OPERATION_INFO.upscale.params.find((f) => f.key === 'factor');
    expect(factor?.kind).toBe('choice');
    if (factor?.kind !== 'choice') return;
    expect(Math.max(...factor.options)).toBeLessThanOrEqual(2);
    expect(factor.defaultValue).toBeLessThanOrEqual(2);
  });

  it('builds generate with no layer and the chosen size', () => {
    expect(buildOperationRequest('generate', input({ layerId: null, prompt: 'mountain', params: { width: 1280, height: 720 } }))).toEqual({
      operation: 'generate',
      prompt: 'mountain',
      params: { width: 1280, height: 720 },
    });
  });

  it('leaves out a prompt for operations that take none, and an empty optional one', () => {
    expect(buildOperationRequest('removeBackground', input({ prompt: 'x' }))).toEqual({ operation: 'removeBackground', layerId: 'L1' });
    expect(buildOperationRequest('relight', input())).toEqual({ operation: 'relight', layerId: 'L1' });
  });

  it('seeds generate with the canvas size, clamped', () => {
    expect(defaultParams('generate', { width: 1280, height: 720 })).toEqual({ width: 1280, height: 720 });
    expect(defaultParams('generate', { width: 64, height: 9000 })).toEqual({ width: 256, height: 4096 });
    expect(defaultParams('upscale')).toEqual({ factor: 2 });
  });
});

describe('the reference pictures', () => {
  it('is taken by "edit with words" and by no other tool', () => {
    expect(EDIT_IMAGE_OPERATIONS.filter((op) => takesReferencePicture(op))).toEqual(['instruct']);
  });

  it('is capped at four, the number the server refuses a fifth against', () => {
    expect(MAX_REFERENCE_PICTURES).toBe(4);
  });

  it('goes into the request for "edit with words", in the order it was given', () => {
    expect(
      buildOperationRequest(
        'instruct',
        input({ prompt: 'put the badge from the second picture on her jacket', referenceProjectFileIds: ['ref-2', 'ref-1'] }),
      ),
    ).toEqual({
      operation: 'instruct',
      layerId: 'L1',
      prompt: 'put the badge from the second picture on her jacket',
      referenceProjectFileIds: ['ref-2', 'ref-1'],
    });
  });

  it('leaves the key out entirely when no picture is chosen', () => {
    const bodies = [
      buildOperationRequest('instruct', input({ prompt: 'make the shirt red' })),
      buildOperationRequest('instruct', input({ prompt: 'make the shirt red', referenceProjectFileIds: [] })),
      buildOperationRequest('instruct', input({ prompt: 'make the shirt red', referenceProjectFileIds: null })),
      buildOperationRequest('instruct', input({ prompt: 'make the shirt red', referenceProjectFileIds: ['', '  '] })),
    ];
    for (const body of bodies) expect('referenceProjectFileIds' in body).toBe(false);
  });

  it('drops the empty entries but keeps the order of the rest', () => {
    expect(
      buildOperationRequest('instruct', input({ prompt: 'x', referenceProjectFileIds: ['', 'ref-2', 'ref-3'] })),
    ).toMatchObject({ referenceProjectFileIds: ['ref-2', 'ref-3'] });
  });

  it('never sends them to a tool that does not take a reference', () => {
    for (const op of EDIT_IMAGE_OPERATIONS) {
      if (op === 'instruct') continue;
      const request = buildOperationRequest(
        op,
        input({ hasMask: true, prompt: 'a description', referenceProjectFileIds: ['ref-1', 'ref-2'] }),
        'mask/key.png',
      );
      expect('referenceProjectFileIds' in request).toBe(false);
    }
  });
});

describe('expand to a shape', () => {
  const flux: ImageOperationCapabilities = {
    operation: 'expand',
    provider: { kind: 'FalAi', model: 'fal-ai/flux-2-pro/outpaint' },
    takesPrompt: false,
    maxPromptLength: 0,
    expand: { geometry: 'margins', maxMarginPx: 2048, maxOutputPixels: 4_000_000, maxInputPixels: 4_000_000, basis: null },
  };
  const bria: ImageOperationCapabilities = {
    operation: 'expand',
    provider: { kind: 'FalAi', model: 'fal-ai/bria/expand' },
    takesPrompt: true,
    maxPromptLength: 4000,
    expand: { geometry: 'canvas', maxMarginPx: null, maxOutputPixels: 25_000_000, maxInputPixels: null, basis: null },
  };

  it('names sizes as reduced shapes', () => {
    expect(aspectOf(720, 1280)).toBe('9:16');
    expect(aspectOf(1080, 1350)).toBe('4:5');
    expect(aspectOf(1000, 667)).toBe('1000:667');
    expect(shapeRatio('9:16')).toBeCloseTo(0.5625);
    expect(shapeRatio('wide')).toBeNull();
  });

  it("starts on the canvas's shape when the picture does not have it - the reel cover case", () => {
    expect(defaultExpandAspect({ width: 720, height: 1280 }, { width: 5931, height: 3954 })).toBe('9:16');
    expect(defaultExpandAspect({ width: 1280, height: 720 }, { width: 1920, height: 1080 })).toBe('9:16');
    expect(defaultExpandAspect({ width: 1080, height: 1080 }, null)).toBe('1:1');
  });

  it('says where the picture grows, in its own pixels', () => {
    expect(expandShapeSummary('9:16', { width: 5931, height: 3954 })).toBe(
      'Adds picture above and below: 5931 × 3954 becomes about 5931 × 10544.',
    );
    expect(expandShapeSummary('16:9', { width: 1000, height: 1000 })).toBe(
      'Adds picture on the left and right: 1000 × 1000 becomes about 1778 × 1000.',
    );
    expect(hasShape({ width: 1920, height: 1080 }, '16:9')).toBe(true);
  });

  it('sends only the shape, and the prompt only to a service that takes one', () => {
    const shaped = input({ prompt: 'a sunset sky', params: { top: 300 }, aspect: '9:16' });
    expect(buildOperationRequest('expand', shaped, undefined, bria)).toEqual({
      operation: 'expand',
      layerId: 'L1',
      prompt: 'a sunset sky',
      params: { aspect: '9:16' },
    });
    expect(buildOperationRequest('expand', shaped, undefined, flux)).toEqual({
      operation: 'expand',
      layerId: 'L1',
      params: { aspect: '9:16' },
    });
    expect(promptRuleFor('expand', flux)).toBe('none');
    expect(promptRuleFor('expand', bria)).toBe('optional');
    expect(promptRuleFor('fill', { ...flux, operation: 'fill' })).toBe('required');
    expect(operationBlocker('expand', input({ aspect: '9:16' }), true)).toBeNull();
  });

  it("keeps pixels per side within the service's per-side cap", () => {
    expect(expandMarginMax(flux)).toBe(2048);
    expect(expandMarginMax(bria)).toBe(10000);
    expect(expandMarginMax(null)).toBe(2048);
    expect(buildOperationRequest('expand', input({ params: { top: 3295, bottom: 3295 } }), undefined, flux).params).toEqual({
      top: 2048,
      bottom: 2048,
    });
  });
});
