import { describe, expect, it } from 'vitest';
import type { Project } from '@/lib/types/project';
import { pickDefaultProject } from '../default-project';

const p = (id: string, updatedAt: string): Project => ({ id, name: id, description: '', status: 'active', userId: 'u', createdAt: updatedAt, updatedAt });

describe('pickDefaultProject', () => {
  const list = [p('new-empty', '2026-10-01T00:00:00Z'), p('busy', '2026-10-20T00:00:00Z')];
  it('prefers the remembered project when it still exists', () => {
    expect(pickDefaultProject(list, 'new-empty')).toBe('new-empty');
  });
  it('falls back to the most recently updated project', () => {
    expect(pickDefaultProject(list, null)).toBe('busy');
    expect(pickDefaultProject(list, 'gone')).toBe('busy');
  });
  it('returns null with no projects', () => {
    expect(pickDefaultProject([], null)).toBeNull();
    expect(pickDefaultProject(undefined, null)).toBeNull();
  });
});
