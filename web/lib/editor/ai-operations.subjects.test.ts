/** Avatars and objects in the picture editor: which tools take them, the request field, the capability answer and the refusals. */
import { describe, expect, it } from 'vitest';
import { ApiError } from '@/lib/api/client';
import { EDIT_IMAGE_OPERATIONS, type ImageOperationCapabilities } from '@/lib/types/image-document';
import { buildOperationRequest, subjectSupport, takesSubjects, type OperationInput } from './ai-operations';
import { describeSubjectRefusal } from './subject-refusal';

const input = (over: Partial<OperationInput> = {}): OperationInput => ({ layerId: 'l1', hasMask: true, prompt: 'make it red', params: { width: 1280, height: 720 }, ...over });
const refusal = (status: number, body: Record<string, unknown>) => new ApiError(String(body.message ?? 'refused'), status, [], body);

describe('subjectIds on a picture operation', () => {
  it('only "create a picture" and "edit with words" take avatars and objects', () => {
    expect(EDIT_IMAGE_OPERATIONS.filter(takesSubjects)).toEqual(['instruct', 'generate']);
  });

  it('create a picture: sends the chosen ids, in the order chosen', () => {
    expect(buildOperationRequest('generate', input({ layerId: null, prompt: 'Anna holds the bottle on a beach', subjectIds: ['anna', 'bottle'] }))).toEqual({
      operation: 'generate',
      prompt: 'Anna holds the bottle on a beach',
      subjectIds: ['anna', 'bottle'],
      params: { width: 1280, height: 720 },
    });
  });

  it('edit with words: sends them beside the layer and the reference pictures', () => {
    expect(buildOperationRequest('instruct', input({ subjectIds: ['bottle'], referenceProjectFileIds: ['f1'] }))).toEqual({
      operation: 'instruct',
      layerId: 'l1',
      prompt: 'make it red',
      referenceProjectFileIds: ['f1'],
      subjectIds: ['bottle'],
    });
  });

  it('leaves the key out when nobody is chosen', () => {
    for (const subjectIds of [undefined, null, [], ['', '  ']] as const) {
      expect('subjectIds' in buildOperationRequest('generate', input({ subjectIds }))).toBe(false);
      expect('subjectIds' in buildOperationRequest('instruct', input({ subjectIds }))).toBe(false);
    }
  });

  it('never sends the same one twice', () => {
    expect(buildOperationRequest('generate', input({ subjectIds: ['anna', 'bottle', 'anna'] })).subjectIds).toEqual(['anna', 'bottle']);
  });

  it('no other tool ever sends them, even when the form still holds some', () => {
    for (const op of EDIT_IMAGE_OPERATIONS.filter((o) => !takesSubjects(o))) {
      expect('subjectIds' in buildOperationRequest(op, input({ subjectIds: ['anna'] }), 'mask-key')).toBe(false);
    }
  });
});

describe('what the model can take', () => {
  const caps = (subjects: ImageOperationCapabilities['subjects']): Pick<ImageOperationCapabilities, 'subjects'> => ({ subjects });

  it('an older server says nothing: read as supported, up to three', () => {
    expect(subjectSupport(null)).toEqual({ supported: true, maxSubjects: 3, models: [] });
    expect(subjectSupport(undefined)).toEqual({ supported: true, maxSubjects: 3, models: [] });
    expect(subjectSupport({})).toEqual({ supported: true, maxSubjects: 3, models: [] });
    expect(subjectSupport(caps(null))).toEqual({ supported: true, maxSubjects: 3, models: [] });
  });

  it('reads a model that can, with its own limit, never above three', () => {
    expect(subjectSupport(caps({ supported: true, maxSubjects: 2, models: [] }))).toEqual({ supported: true, maxSubjects: 2, models: [] });
    expect(subjectSupport(caps({ supported: true, maxSubjects: 8 })).maxSubjects).toBe(3);
    expect(subjectSupport(caps({ supported: true })).maxSubjects).toBe(3);
  });

  it('reads a model that cannot, and the models that can', () => {
    expect(subjectSupport(caps({ supported: false, maxSubjects: 3, models: ['google/nano-banana-2.1/edit', '', 'fal-ai/qwen-image-edit-2511'] }))).toEqual({
      supported: false,
      maxSubjects: 3,
      models: ['google/nano-banana-2.1/edit', 'fal-ai/qwen-image-edit-2511'],
    });
    expect(subjectSupport(caps({ supported: false, models: null })).models).toEqual([]);
  });
});

describe('refusals about avatars and objects', () => {
  it('the one-time consent, whether or not the request named anyone', () => {
    expect(describeSubjectRefusal(refusal(409, { code: 'CONSENT_REQUIRED' }), true)).toEqual({ kind: 'consent' });
    expect(describeSubjectRefusal(refusal(409, { code: 'CONSENT_REQUIRED' }), false)).toEqual({ kind: 'consent' });
  });

  it('a person whose consent does not cover pictures, and an avatar that cannot be used', () => {
    expect(describeSubjectRefusal(refusal(409, { code: 'AVATAR_CONSENT_NO_PICTURES' }), true)).toEqual({ kind: 'noPictures' });
    expect(describeSubjectRefusal(refusal(409, { code: 'AVATAR_NOT_USABLE', reason: 'reference_held' }), true)).toEqual({ kind: 'notUsable', reason: 'reference_held' });
    expect(describeSubjectRefusal(refusal(409, { code: 'AVATAR_NOT_USABLE' }), true)).toEqual({ kind: 'notUsable' });
  });

  it('a model that cannot be shown them: only when the request carried some', () => {
    expect(describeSubjectRefusal(refusal(409, { code: 'OPERATION_UNSUPPORTED', models: ['google/nano-banana-2.1/edit'] }), true)).toEqual({ kind: 'unsupported', models: ['google/nano-banana-2.1/edit'] });
    expect(describeSubjectRefusal(refusal(409, { code: 'OPERATION_UNSUPPORTED' }), true)).toEqual({ kind: 'unsupported' });
    // without subjects the same code means the tool has no AI service at all: the editor handles that as it always did
    expect(describeSubjectRefusal(refusal(409, { code: 'OPERATION_UNSUPPORTED' }), false)).toBeNull();
  });

  it('a plan without avatars or objects', () => {
    expect(describeSubjectRefusal(refusal(402, { code: 'PLAN_NOT_ENTITLED', feature: 'avatars' }), true)).toEqual({ kind: 'plan', feature: 'avatars' });
    expect(describeSubjectRefusal(refusal(402, { code: 'PLAN_NOT_ENTITLED', feature: 'avatar_objects' }), true)).toEqual({ kind: 'plan', feature: 'avatar_objects' });
    expect(describeSubjectRefusal(refusal(402, { code: 'PLAN_NOT_ENTITLED', feature: 'resolution' }), true)).toEqual({ kind: 'plan' });
    expect(describeSubjectRefusal(refusal(402, { code: 'PLAN_NOT_ENTITLED', feature: 'avatars' }), false)).toBeNull();
  });

  it('anything else is not about avatars', () => {
    expect(describeSubjectRefusal(refusal(402, { code: 'INSUFFICIENT_CREDITS' }), true)).toBeNull();
    expect(describeSubjectRefusal(refusal(424, { code: 'OPERATION_FAILED' }), true)).toBeNull();
    expect(describeSubjectRefusal(new Error('offline'), true)).toBeNull();
    expect(describeSubjectRefusal(null, true)).toBeNull();
  });
});
