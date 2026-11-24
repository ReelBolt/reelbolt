import { describe, it, expect } from 'vitest';
import { camelizeKeys, parseConfigJson, camelizeConfigJsonString } from './config-case';

describe('config-case', () => {
  it('camelizes nested PascalCase config', () => {
    expect(camelizeKeys({ Source: { Kind: 'ProjectFile', ProjectFileId: 'x' }, Sources: [{ Kind: 'StepOutput' }], Vision: 'Optional' }))
      .toEqual({ source: { kind: 'ProjectFile', projectFileId: 'x' }, sources: [{ kind: 'StepOutput' }], vision: 'Optional' });
  });
  it('leaves already-camelCase config untouched', () => {
    const c = { version: 1, view: { from: 'Previous' } };
    expect(camelizeKeys(c)).toEqual(c);
  });
  it('keeps author-chosen Extract input names verbatim', () => {
    expect(camelizeKeys({ Operation: 'Project', Inputs: { Items: { From: 'Step', StepOrder: 2 } } }))
      .toEqual({ operation: 'Project', inputs: { Items: { from: 'Step', stepOrder: 2 } } });
  });
  it('tolerates empty and invalid JSON', () => {
    expect(parseConfigJson(null)).toBeNull();
    expect(parseConfigJson('{nope')).toBeNull();
    expect(camelizeConfigJsonString('{nope')).toBe('{nope');
    expect(camelizeConfigJsonString('{"View":{"From":"Step"}}')).toBe('{"view":{"from":"Step"}}');
  });
});
