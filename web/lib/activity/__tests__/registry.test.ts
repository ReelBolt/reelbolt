import { describe, expect, it } from 'vitest';
import '../builtins';
import { GENERIC_JOB_TYPE, getJobType, jobHref, registerJobType } from '../registry';
import { item } from './helpers';
import type { ActivityJob } from '../types';

const job = (over: Partial<ActivityJob> = {}): ActivityJob => ({ ...item(), origin: 'server', ...over }) as ActivityJob;

describe('job type registry', () => {
  it('falls back to the generic definition for an unknown type', () => {
    expect(getJobType('somethingNew')).toBe(GENERIC_JOB_TYPE);
    expect(jobHref(job({ type: 'somethingNew' }))).toBeNull();
  });

  it('builds hrefs for the built-in types (no basePath)', () => {
    expect(jobHref(job({ type: 'render' }))).toBe('/editor/p1/timeline/t1');
    expect(jobHref(job({ type: 'workflowRun' }))).toBe('/projects/p1/workflows/w1/executions/e1');
    expect(jobHref(job({ type: 'graphic', subject: { documentId: 'd1' } }))).toBe('/editor/thumbnails/d1');
    expect(jobHref(job({ type: 'mediaPrep' }))).toBe('/editor?project=p1');
  });

  it('lets a feature register its own type and a job override its href', () => {
    registerJobType({ ...GENERIC_JOB_TYPE, type: 'audioCleanup', href: () => '/x' });
    expect(jobHref(job({ type: 'audioCleanup' }))).toBe('/x');
    expect(jobHref(job({ type: 'audioCleanup', href: '/y' }))).toBe('/y');
  });
});
