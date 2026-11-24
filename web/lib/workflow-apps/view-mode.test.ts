import { describe, expect, it } from 'vitest';
import { parseWorkflowView, resolveWorkflowView, withWorkflowView, workflowViewStorageKey } from './view-mode';

describe('workflow view mode', () => {
  it('only accepts the two views', () => {
    expect(parseWorkflowView('advanced')).toBe('advanced');
    expect(parseWorkflowView('app')).toBe('app');
    expect(parseWorkflowView('graph')).toBeNull();
    expect(parseWorkflowView(null)).toBeNull();
  });

  it('prefers the URL, then the remembered choice, then the fallback', () => {
    expect(resolveWorkflowView('?view=app', 'advanced')).toBe('app');
    expect(resolveWorkflowView('', 'app')).toBe('app');
    expect(resolveWorkflowView('?view=nonsense', 'app')).toBe('app');
    expect(resolveWorkflowView('', null)).toBe('app');
    expect(resolveWorkflowView('', null, 'advanced')).toBe('advanced');
  });

  it('spells the view in the URL, keeping other params', () => {
    expect(withWorkflowView('', 'advanced')).toBe('?view=advanced');
    expect(withWorkflowView('?a=1&view=advanced', 'app')).toBe('?a=1&view=app');
  });

  it('keys the memory per user', () => {
    expect(workflowViewStorageKey('U1')).not.toBe(workflowViewStorageKey('u2'));
    expect(workflowViewStorageKey('U1')).toBe(workflowViewStorageKey('u1'));
  });
});
