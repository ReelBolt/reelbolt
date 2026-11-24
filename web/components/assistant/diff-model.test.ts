import { describe, it, expect } from 'vitest';
import { countByKind, describeDiff } from './diff-model';
import type { WorkflowEditDiff } from '@/lib/types/workflow-assistant';

const base: WorkflowEditDiff = { confirmationToken: 't', additions: [], removals: [], modifications: [] };

describe('describeDiff', () => {
  it('names removals and changes from the current steps and additions from the proposal', () => {
    const rows = describeDiff(
      {
        ...base,
        additions: [{ stepOrder: 3, agentDefinitionId: 'a3', stepType: 'ReviewLoop', summary: 'Review' }],
        removals: [{ stepOrder: 1, agentDefinitionId: 'a1', stepType: 'Agent', summary: 'x' }],
        modifications: [{ stepOrder: 2, agentDefinitionId: 'a2', stepType: 'Agent', summary: 'Script v2' }],
        proposedSteps: [
          { agentDefinitionId: 'a2', stepOrder: 1, label: 'Script', stepType: 'Agent' },
          { agentDefinitionId: 'a2', stepOrder: 2, label: 'Script v2', stepType: 'Agent' },
          { agentDefinitionId: 'a3', stepOrder: 3, label: 'Quality gate', stepType: 'ReviewLoop' },
        ],
      },
      [
        { label: 'Analyze', stepType: 'Agent' },
        { label: 'Script', stepType: 'Agent' },
      ],
    );

    expect(rows.map((r) => [r.kind, r.name, r.previousName])).toEqual([
      ['added', 'Quality gate', undefined],
      ['changed', 'Script v2', 'Script'],
      ['removed', 'Analyze', undefined],
    ]);
    // The summary repeats the new name, so it is not shown again as detail.
    expect(rows[1].detail).toBeUndefined();
    // The addition's summary "Review" says something the name doesn't.
    expect(rows[0].detail).toBe('Review');
    expect(rows[0].stepTypeLabel).toBe('Review Loop');
  });

  it('falls back to the summary, then the step type, when a lookup misses (stale or legacy proposal)', () => {
    const rows = describeDiff({
      ...base,
      removals: [{ stepOrder: 9, agentDefinitionId: 'a', stepType: 'Extract', summary: '' }],
      modifications: [{ stepOrder: 8, agentDefinitionId: 'b', stepType: 'Agent', summary: 'Tweak prompt' }],
    });
    expect(rows.map((r) => r.name)).toEqual(['Tweak prompt', 'Extract step']);
  });

  it('does not report a rename when a changed step keeps its name', () => {
    const [row] = describeDiff(
      {
        ...base,
        modifications: [{ stepOrder: 1, agentDefinitionId: 'a', stepType: 'Agent', summary: 'New prompt' }],
        proposedSteps: [{ agentDefinitionId: 'a', stepOrder: 1, label: 'Script', stepType: 'Agent' }],
      },
      [{ label: 'Script', stepType: 'Agent' }],
    );
    expect(row.previousName).toBeUndefined();
    expect(row.detail).toBe('New prompt');
  });

  it('names an unlabelled step by its type', () => {
    const [row] = describeDiff({ ...base, removals: [{ stepOrder: 1, agentDefinitionId: 'a', stepType: 'Agent', summary: '' }] }, [
      { label: '', stepType: 'VideoCompile' },
    ]);
    expect(row.name).toBe('Compile Video step');
  });

  it('counts rows by kind', () => {
    const rows = describeDiff({
      ...base,
      additions: [
        { stepOrder: 1, agentDefinitionId: 'a', stepType: 'Agent', summary: 'a' },
        { stepOrder: 2, agentDefinitionId: 'b', stepType: 'Agent', summary: 'b' },
      ],
      removals: [{ stepOrder: 1, agentDefinitionId: 'c', stepType: 'Agent', summary: 'c' }],
    });
    expect(countByKind(rows)).toEqual({ added: 2, removed: 1, changed: 0 });
  });
});
