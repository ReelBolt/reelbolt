import { describe, expect, it } from 'vitest';
import { causeLabel, groupVersions, retentionText } from '../versions';

const v = (revision: number, createdAt: string, extra: Record<string, unknown> = {}) => ({ revision, createdAt, cause: 'auto', ...extra });
const NOW = new Date(2026, 9, 24, 15, 0).getTime();
const at = (h: number, m: number, day = 24) => new Date(2026, 9, day, h, m).toISOString();

describe('R01.2 version labels', () => {
  it('says each cause in plain words', () => {
    expect(causeLabel({ cause: 'auto' })).toBe('Auto-saved checkpoint');
    expect(causeLabel({ cause: 'idle' })).toBe('Auto-saved checkpoint');
    expect(causeLabel({ cause: 'milestone' })).toBe('Auto-saved checkpoint');
    expect(causeLabel({ cause: 'milestone', revision: 1 })).toBe('Original version');
    expect(causeLabel({ cause: 'beforeRestore' })).toBe('Before you restored');
    expect(causeLabel({ cause: 'beforeRender' })).toBe('Before a render');
    expect(causeLabel({ cause: 'beforeProposal' })).toBe('Before an AI suggestion');
    expect(causeLabel({ cause: 'named', name: 'Client cut' })).toBe('Named version');
  });
  it('states retention', () => {
    expect(retentionText({})).toBe('kept forever');
    expect(retentionText({ expiresAt: new Date(NOW + 12 * 86_400_000 - 1000).toISOString() }, NOW)).toBe('deleted in 12 days');
    expect(retentionText({ expiresAt: new Date(NOW + 3_600_000).toISOString() }, NOW)).toBe('deleted in 1 day');
    expect(retentionText({ expiresAt: new Date(NOW - 1).toISOString() }, NOW)).toBe('deleted today');
  });
});

describe('R01.2 session grouping', () => {
  it('groups by sessions, breaking on a gap of 30 minutes or more, newest first', () => {
    const groups = groupVersions([v(1, at(10, 0)), v(2, at(14, 2)), v(3, at(14, 20)), v(4, at(14, 40)), v(5, at(14, 5, 23))], NOW);
    expect(groups.map((g) => g.versions.map((x) => x.revision))).toEqual([[4, 3, 2], [1], [5]]);
    expect(groups[0].label).toBe('Today 14:02-14:40');
    expect(groups[1].label).toBe('Today 10:00');
    expect(groups[2].label).toMatch(/^Yesterday 14:05$/);
  });
  it('a gap of exactly 30 minutes starts a new session', () => {
    const groups = groupVersions([v(1, at(9, 0)), v(2, at(9, 30))], NOW);
    expect(groups).toHaveLength(2);
  });
  it('is empty for no versions', () => {
    expect(groupVersions([], NOW)).toEqual([]);
  });
});
