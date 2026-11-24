import { describe, expect, it, vi } from 'vitest';
import type { DashboardOverview } from '@/lib/types/dashboard';
import { ApiError } from '@/lib/api/client';
import { FIRST_FILM_PROJECT_NAME, firstFilmErrorKind, sampleFootageChoice, shouldOfferFirstFilm, startFirstFilm } from './first-film';

const overview = (recent: number, active: number) =>
  ({ recentExecutions: new Array(recent).fill({}), activeExecutions: new Array(active).fill({}) }) as unknown as DashboardOverview;

describe('shouldOfferFirstFilm', () => {
  it('offers it to someone with no run', () => expect(shouldOfferFirstFilm(overview(0, 0), false)).toBe(true));
  it('hides it once there is a finished run', () => expect(shouldOfferFirstFilm(overview(1, 0), false)).toBe(false));
  it('hides it while a run is active', () => expect(shouldOfferFirstFilm(overview(0, 1), false)).toBe(false));
  it('hides it when dismissed or not loaded', () => {
    expect(shouldOfferFirstFilm(overview(0, 0), true)).toBe(false);
    expect(shouldOfferFirstFilm(undefined, false)).toBe(false);
  });
});

describe('startFirstFilm', () => {
  const mk = () => ({
    createProject: vi.fn(async () => ({ id: 'new' })),
    addSample: vi.fn(async () => ({ id: 'f1' })),
    applyTemplate: vi.fn(async () => ({ id: 'wf' })),
  });

  it('creates the project, adds the sample, sets up the app and returns its page', async () => {
    const d = mk();
    await expect(startFirstFilm(d, { projects: [] })).resolves.toBe('/projects/new/workflows/wf?view=app&sample=f1');
    expect(d.createProject).toHaveBeenCalledWith({ name: FIRST_FILM_PROJECT_NAME, description: '' });
    expect(d.addSample).toHaveBeenCalledWith('new');
    expect(d.applyTemplate).toHaveBeenCalledWith('new', 'video-derush-edit', true);
  });

  it('reuses the first-film project', async () => {
    const d = mk();
    await startFirstFilm(d, { projects: [{ id: 'old', name: FIRST_FILM_PROJECT_NAME }] });
    expect(d.createProject).not.toHaveBeenCalled();
    expect(d.addSample).toHaveBeenCalledWith('old');
  });

  it('uses a given project (gallery empty state)', async () => {
    const d = mk();
    await expect(startFirstFilm(d, { projectId: 'p9' })).resolves.toBe('/projects/p9/workflows/wf?view=app&sample=f1');
    expect(d.createProject).not.toHaveBeenCalled();
  });

  it('does not set up the app when the sample fails', async () => {
    const d = mk();
    d.addSample.mockRejectedValueOnce(new Error('x'));
    await expect(startFirstFilm(d, { projectId: 'p' })).rejects.toThrow('x');
    expect(d.applyTemplate).not.toHaveBeenCalled();
  });
});

describe('sampleFootageChoice', () => {
  const files = [{ id: 'a' }, { id: 'f1' }];
  it('chooses only the sample among several videos', () => expect(sampleFootageChoice('f1', files)).toEqual(['f1']));
  it('changes nothing without the parameter or when the file is unknown', () => {
    expect(sampleFootageChoice(null, files)).toBeNull();
    expect(sampleFootageChoice('zz', files)).toBeNull();
    expect(sampleFootageChoice('f1', undefined)).toBeNull();
  });
});

describe('retry after the sample step failed', () => {
  it('reuses the project the first try created', async () => {
    const d = {
      createProject: vi.fn(async () => ({ id: 'new' })),
      addSample: vi.fn().mockRejectedValueOnce(new Error('boom')).mockResolvedValue({ id: 'f1' }),
      applyTemplate: vi.fn(async () => ({ id: 'wf' })),
    };
    const created: { id?: string } = {};
    await expect(startFirstFilm(d, { projects: [], created })).rejects.toThrow('boom');
    await expect(startFirstFilm(d, { projects: [], created })).resolves.toContain('/projects/new/');
    expect(d.createProject).toHaveBeenCalledTimes(1);
  });
});

describe('firstFilmErrorKind', () => {
  it('maps the known failures', () => {
    expect(firstFilmErrorKind(new ApiError('x', 403, [], { code: 'EMAIL_NOT_VERIFIED' }))).toBe('unverified');
    expect(firstFilmErrorKind(new ApiError('x', 413))).toBe('tooLarge');
    expect(firstFilmErrorKind(new TypeError('Failed to fetch'))).toBe('network');
    expect(firstFilmErrorKind(new Error('raw'))).toBe('generic');
  });
});
