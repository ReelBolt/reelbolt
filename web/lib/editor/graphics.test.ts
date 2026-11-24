import { describe, expect, it } from 'vitest';
import {
  GRAPHIC_PLACEMENT_OPTIONS,
  activeGraphicJob,
  buildGraphicRequest,
  defaultGraphicForm,
  graphicCostLine,
  graphicFormBlocker,
  graphicTargetRegion,
  jobsToAdopt,
  latestProgressStage,
  revisableGraphic,
} from './graphics';
import { GRAPHIC_PLACEMENTS, type EditImageLayer, type ImageGraphicJob } from '@/lib/types/image-document';

const job = (over: Partial<ImageGraphicJob> & { id: string }): ImageGraphicJob => ({
  documentId: 'd',
  executionId: `e-${over.id}`,
  workflowId: 'w',
  status: 'Running',
  description: 'A badge',
  frameWidth: 400,
  frameHeight: 200,
  placement: 'topRight',
  target: { x: 800, y: 40, width: 400, height: 200 },
  revisesLayerId: null,
  layerId: null,
  layer: null,
  summary: null,
  error: null,
  createdAt: '2026-10-10T00:00:00Z',
  completedAt: null,
  ...over,
});

const layer = (id: string): EditImageLayer => ({ id } as EditImageLayer);

describe('which layers can be changed', () => {
  it('is a designed layer that knows its design version', () => {
    expect(
      revisableGraphic({
        id: 'l1',
        provenance: { operation: 'designGraphic', workspaceVersionId: 'v1', prompt: 'A red badge', graphicId: 'g1' },
      }),
    ).toEqual({ layerId: 'l1', prompt: 'A red badge', graphicId: 'g1', workspaceVersionId: 'v1' });
  });

  it('is nothing else', () => {
    expect(revisableGraphic(null)).toBeNull();
    expect(revisableGraphic({ id: 'l', provenance: null })).toBeNull();
    expect(revisableGraphic({ id: 'l', provenance: { operation: 'fill', prompt: 'grass' } })).toBeNull();
    expect(revisableGraphic({ id: 'l', provenance: { operation: 'designGraphic', workspaceVersionId: null } })).toBeNull();
    expect(revisableGraphic({ id: 'l', provenance: { operation: 'designGraphic', workspaceVersionId: '' } })).toBeNull();
  });
});

describe('the request', () => {
  const canvas = { width: 1280, height: 720 };

  it('designs in the whole picture unless a size is chosen, and always states the canvas', () => {
    const form = { ...defaultGraphicForm(canvas), description: '  A sale badge  ', placement: 'topRight' as const };
    expect(buildGraphicRequest(form, canvas)).toEqual({
      description: 'A sale badge',
      placement: 'topRight',
      canvasWidth: 1280,
      canvasHeight: 720,
    });
    expect(buildGraphicRequest({ ...form, size: 'custom', width: 500.4, height: 200 }, canvas)).toEqual({
      description: 'A sale badge',
      placement: 'topRight',
      width: 500,
      height: 200,
      canvasWidth: 1280,
      canvasHeight: 720,
    });
  });

  it('names the layer to change and sends the change request as the description', () => {
    const form = { ...defaultGraphicForm(canvas), description: 'Make it blue', size: 'custom' as const };
    expect(buildGraphicRequest(form, canvas, 'l1')).toEqual({
      description: 'Make it blue',
      canvasWidth: 1280,
      canvasHeight: 720,
      revisesLayerId: 'l1',
    });
  });

  it('says why it cannot be sent yet', () => {
    const form = defaultGraphicForm(canvas);
    expect(graphicFormBlocker(form, false)).toBe('Describe the graphic you want.');
    expect(graphicFormBlocker(form, true)).toBe('Say what should change.');
    expect(graphicFormBlocker({ ...form, description: 'x'.repeat(2001) }, false)).toMatch(/2000/);
    expect(graphicFormBlocker({ ...form, description: 'A badge', size: 'custom', width: 10 }, false)).toMatch(/64 and 4096/);
    expect(graphicFormBlocker({ ...form, description: 'A badge', size: 'custom', width: 64, height: 4096 }, false)).toBeNull();
    // A size outside the limits does not matter while the picture's own frame is used.
    expect(graphicFormBlocker({ ...form, description: 'A badge', width: 1 }, false)).toBeNull();
  });

  it('starts at a sensible custom size inside the limits', () => {
    expect(defaultGraphicForm({ width: 100, height: 100 })).toMatchObject({ width: 64, height: 64, size: 'fit' });
    expect(defaultGraphicForm({ width: 1280, height: 720 })).toMatchObject({ width: 640, height: 240 });
  });

  it('offers every placement the server knows, once, with a plain name', () => {
    expect(GRAPHIC_PLACEMENT_OPTIONS.map((o) => o.value)).toEqual([...GRAPHIC_PLACEMENTS]);
    expect(GRAPHIC_PLACEMENT_OPTIONS.find((o) => o.value === 'center')?.label).toBe('Centre');
  });
});

describe('the cost line', () => {
  const estimate = { billingEnabled: true, lowCredits: 12, highCredits: 30.4, requiredCredits: 30, balanceCredits: 500, affordable: true };

  it('says the range, or that billing is off, or nothing when unknown', () => {
    expect(graphicCostLine(estimate)).toEqual({
      text: 'About 12–30 credits.',
      needs: 'Needs 30 credits available to start; it can cost more.',
      affordable: true,
    });
    expect(graphicCostLine({ ...estimate, highCredits: 12 })?.text).toBe('About 12 credits.');
    expect(graphicCostLine({ ...estimate, lowCredits: 0.25, highCredits: 1.26 })?.text).toBe('About 0.3–1.3 credits.');
    expect(graphicCostLine({ ...estimate, billingEnabled: false })).toEqual({
      text: 'Billing is off on this installation.',
      needs: null,
      affordable: true,
    });
    expect(graphicCostLine(null)).toBeNull();
  });

  it('says plainly when the balance is too low', () => {
    const line = graphicCostLine({ ...estimate, balanceCredits: 4, affordable: false });
    expect(line?.affordable).toBe(false);
    expect(line?.text).toBe('About 12–30 credits. You have 4 credits.');
    expect(line?.needs).toBe('Needs 30 credits available to start; it can cost more.');
  });

  it('rounds what a design needs up, never down, and leaves it out when nothing is held', () => {
    expect(graphicCostLine({ ...estimate, requiredCredits: 12.4 })?.needs).toBe(
      'Needs 13 credits available to start; it can cost more.',
    );
    expect(graphicCostLine({ ...estimate, requiredCredits: 0 })?.needs).toBeNull();
  });
});

describe('jobs', () => {
  it('finds the newest working job and where it will land', () => {
    const jobs = [job({ id: 'new', status: 'Queued' }), job({ id: 'old', status: 'Running' }), job({ id: 'done', status: 'Passed' })];
    expect(activeGraphicJob(jobs)?.id).toBe('new');
    expect(activeGraphicJob([job({ id: 'x', status: 'Failed' })])).toBeNull();
    expect(activeGraphicJob(undefined)).toBeNull();
    expect(graphicTargetRegion(jobs[0])).toEqual({ x: 800, y: 40, w: 400, h: 200 });
    expect(graphicTargetRegion(job({ id: 'z', target: { x: 0, y: 0, width: 0, height: 10 } }))).toBeNull();
  });

  it('brings in finished layers the document lacks, oldest first, once', () => {
    const jobs = [
      job({ id: 'c', status: 'Passed', layer: layer('lc') }),
      job({ id: 'b', status: 'Passed', layer: layer('lb') }),
      job({ id: 'a', status: 'Passed', layer: layer('la') }),
      job({ id: 'running', status: 'Running' }),
      job({ id: 'failed', status: 'Failed', layer: null }),
      job({ id: 'unsettled', status: 'Passed', layer: null }),
    ];
    expect(jobsToAdopt(jobs, new Set(['lb']), new Set(['c'])).map((j) => j.id)).toEqual(['a']);
    expect(jobsToAdopt(jobs, new Set(), new Set()).map((j) => j.id)).toEqual(['a', 'b', 'c']);
    expect(jobsToAdopt(undefined, new Set(), new Set())).toEqual([]);
  });

  it('reads the latest stage of the live progress feed', () => {
    expect(
      latestProgressStage([
        { type: 'step.completed', payload: {} },
        { type: 'step.progress', payload: { stage: 'Rendering the picture' } },
        { type: 'step.progress', payload: { stage: 'Designing your graphic' } },
      ]),
    ).toBe('Rendering the picture');
    expect(latestProgressStage([{ type: 'step.progress', payload: { Stage: 'Designing' } }])).toBe('Designing');
    expect(latestProgressStage([{ type: 'step.progress', payload: { stage: '  ' } }])).toBeNull();
    expect(latestProgressStage([])).toBeNull();
  });
});

describe('plainGraphicError', () => {
  it('keeps a sentence and turns an infrastructure trace into plain words', async () => {
    const { plainGraphicError } = await import('./graphics');
    expect(plainGraphicError('The designer could not fit the text.')).toBe('The designer could not fit the text.');
    expect(plainGraphicError('Resource temporarily unavailable (sandbox-executor:8080)')).toMatch(/could not start on this installation/);
    expect(plainGraphicError('Connection refused')).toMatch(/could not start/);
    expect(plainGraphicError(null)).toBe('The graphic could not be designed.');
  });
});
