/** The avatar clip in the header's activity tray: its own type, where "Open" goes, and where a finished one stays quiet. */
import { afterEach, describe, expect, it, vi } from 'vitest';
import en from '../../../messages/en/activity.json';
import itMessages from '../../../messages/it/activity.json';
import '../builtins';
import { isOnJobScreen } from '../notifier';
import { GENERIC_JOB_TYPE, getJobType, jobHref } from '../registry';
import { item } from './helpers';
import type { ActivityJob } from '../types';

const job = (over: Partial<ActivityJob> = {}): ActivityJob => ({ ...item(), origin: 'client', type: 'subjectClip', projectId: 'p1', ...over }) as ActivityJob;

afterEach(() => vi.unstubAllGlobals());

describe('the subjectClip job type', () => {
  it('is registered with its own label, in both languages', () => {
    const def = getJobType('subjectClip');
    expect(def).not.toBe(GENERIC_JOB_TYPE);
    expect(def.labelKey).toBe('types.subjectClip');
    expect(en.types.subjectClip).toBe('Making a clip with your avatars');
    expect(itMessages.types.subjectClip).toBe('Creazione di una clip con i tuoi avatar');
  });

  it('opens the editor of its project, where the new clip is in the media pool', () => {
    expect(jobHref(job())).toBe('/editor?project=p1');
  });

  it('a server row is stopped through the subject-clips route, and "it ended first" is just as good', async () => {
    const calls: string[] = [];
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      calls.push(`${init?.method ?? 'GET'} ${url}`);
      return calls.length === 1
        ? ({ ok: true, status: 202, json: async () => ({ executionId: 'e1', state: 'stopping' }) } as Response)
        : ({ ok: false, status: 409, statusText: 'Conflict', headers: new Headers(), json: async () => ({ code: 'SUBJECT_CLIP_ALREADY_FINISHED' }) } as unknown as Response);
    }));
    const cancel = getJobType('subjectClip').cancel;
    expect(cancel).toBeTypeOf('function');
    const row = job({ origin: 'server', executionId: 'e1' });
    await cancel!(row);
    await expect(cancel!(row)).resolves.toBeUndefined();
    expect(calls).toEqual(['POST /api/v1/projects/p1/subject-clips/e1/cancel', 'POST /api/v1/projects/p1/subject-clips/e1/cancel']);
    await cancel!(job({ executionId: null }));
    expect(calls).toHaveLength(2);
  });

  it('a finished clip is not announced twice: the tray is quiet in that project\'s editor, where the pool says it', () => {
    const j = job();
    expect(isOnJobScreen('/editor/p1/timeline/t1', j, jobHref(j))).toBe(true);
    expect(isOnJobScreen('/editor', j, jobHref(j))).toBe(true);
    expect(isOnJobScreen('/editor/p2/timeline/t9', j, jobHref(j))).toBe(false);
    expect(isOnJobScreen('/dashboard', j, jobHref(j))).toBe(false);
  });
});
