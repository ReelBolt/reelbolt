import { describe, it, expect } from 'vitest';
import { parseContinueComposition, continueCompositionToJson, stepOptionsToRequest } from './step-options';

describe('step options', () => {
  it('sends a PinInsert config through the shared spread, and nothing for any other step', () => {
    const pinInsertConfig = { analysis: { from: 'Previous' }, pins: [{ regionId: 'r0', content: { from: 'Step', stepOrder: 2 } }] };
    expect(stepOptionsToRequest({ stepType: 'PinInsert', pinInsertConfig })).toEqual({ pinInsertConfigJson: JSON.stringify(pinInsertConfig) });
    expect(stepOptionsToRequest({ stepType: 'Agent', pinInsertConfig })).toEqual({});
    expect(stepOptionsToRequest({ stepType: 'PinInsert', pinInsertConfig: null })).toEqual({});
  });

  it('parses an execution or a version reference, case-insensitively', () => {
    expect(parseContinueComposition('{"executionId":"e1"}')).toEqual({ executionId: 'e1', versionId: null });
    expect(parseContinueComposition('{"VersionId":"v1"}')).toEqual({ executionId: null, versionId: 'v1' });
  });

  it('returns null for empty, invalid or non-object input', () => {
    expect(parseContinueComposition(null)).toBeNull();
    expect(parseContinueComposition('')).toBeNull();
    expect(parseContinueComposition('not json')).toBeNull();
    expect(parseContinueComposition('[1]')).toBeNull();
  });

  it('serialises exactly one id and nothing for a half-made choice', () => {
    expect(continueCompositionToJson({ executionId: 'e1' })).toBe('{"executionId":"e1"}');
    expect(continueCompositionToJson({ versionId: 'v1' })).toBe('{"versionId":"v1"}');
    expect(continueCompositionToJson({ executionId: null, versionId: null })).toBeNull();
    expect(continueCompositionToJson(null)).toBeNull();
  });

  it('omits unset options from the request entirely', () => {
    expect(stepOptionsToRequest({})).toEqual({});
    expect(stepOptionsToRequest({ continueComposition: { executionId: null }, registerOutputAsProjectFile: false })).toEqual({});
    expect(stepOptionsToRequest({ continueComposition: { executionId: 'e1' }, registerOutputAsProjectFile: true })).toEqual({
      continueCompositionJson: '{"executionId":"e1"}',
      registerOutputAsProjectFile: true,
    });
  });
});
