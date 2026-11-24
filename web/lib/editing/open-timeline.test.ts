import { beforeEach, describe, expect, it, vi } from 'vitest';

const api = vi.hoisted(() => ({ listEditTimelines: vi.fn(), createEditTimeline: vi.fn() }));
vi.mock('./sync/api', () => api);

const notif = vi.hoisted(() => ({ show: vi.fn() }));
vi.mock('@mantine/notifications', () => ({ notifications: notif }));

import { editorHubHref, openTimelineForRender, relativeTime, timelineEditorHref } from './open-timeline';
import { seedRuleSentence } from './humanize-error';
import { ApiError } from '@/lib/api/client';

beforeEach(() => {
  api.listEditTimelines.mockReset();
  api.createEditTimeline.mockReset();
  notif.show.mockReset();
});

describe('unplaced items note', () => {
  it('shows a yellow, sticky note in words for a non-empty list', async () => {
    api.listEditTimelines.mockResolvedValue([]);
    api.createEditTimeline.mockResolvedValue({
      id: 'made',
      unplacedItems: [
        { track: 'V1', itemId: 'x1', label: null, reason: 'source_unresolved' },
        { track: 'V2', itemId: 'x2', label: 'Logo', reason: 'asset_unresolved' },
        { track: 'V3', itemId: 'x3', label: null, reason: 'asset_unresolved' },
      ],
    });
    await openTimelineForRender('p1', 'sr1');
    const arg = notif.show.mock.calls[0][0];
    expect(arg.color).toBe('yellow');
    expect(arg.autoClose).toBe(false);
    expect(arg.message).toContain('3 items from this run could not be placed');
    expect(arg.message).toContain('1 video clip, 1 screen insert, 1 graphic');
    expect(arg.message).not.toMatch(/x1|source_unresolved|asset_unresolved|V1/);
  });

  it('names a censored stretch in words, not by its track or reason code', async () => {
    api.listEditTimelines.mockResolvedValue([]);
    api.createEditTimeline.mockResolvedValue({
      id: 'made',
      unplacedItems: [{ track: 'censor', itemId: 'censor-r-0', label: 'face', reason: 'censored_stretch_withheld' }],
    });
    await openTimelineForRender('p1', 'sr1');
    const arg = notif.show.mock.calls[0][0];
    expect(arg.message).toContain('1 censored stretch');
    expect(arg.message).not.toMatch(/censor-r-0|censored_stretch_withheld/);
  });

  it('shows nothing for an empty or absent list', async () => {
    api.listEditTimelines.mockResolvedValue([]);
    api.createEditTimeline.mockResolvedValue({ id: 'a', unplacedItems: [] });
    await openTimelineForRender('p1', 'sr1');
    api.createEditTimeline.mockResolvedValue({ id: 'b' });
    await openTimelineForRender('p1', 'sr1');
    expect(notif.show).not.toHaveBeenCalled();
  });
});

describe('openTimelineForRender', () => {
  it('returns to the most recently edited timeline already seeded from the render', async () => {
    api.listEditTimelines.mockResolvedValue([
      { id: 'old', seedStepResultId: 'sr1', updatedAt: '2026-10-01T00:00:00Z' },
      { id: 'other', seedStepResultId: 'sr2', updatedAt: '2026-10-09T00:00:00Z' },
      { id: 'new', seedStepResultId: 'sr1', updatedAt: '2026-10-05T00:00:00Z' },
    ]);
    await expect(openTimelineForRender('p1', 'sr1')).resolves.toBe('new');
    expect(api.createEditTimeline).not.toHaveBeenCalled();
  });

  it('seeds a new timeline, named after the render, when none exists', async () => {
    api.listEditTimelines.mockResolvedValue([]);
    api.createEditTimeline.mockResolvedValue({ id: 'made' });
    await expect(openTimelineForRender('p1', 'sr1', 'Teaser, Run 2')).resolves.toBe('made');
    expect(api.createEditTimeline).toHaveBeenCalledWith('p1', { name: 'Teaser, Run 2', seedStepResultId: 'sr1' });
  });

  it('passes the seed-invalid body through so the failure names the rule', async () => {
    api.listEditTimelines.mockResolvedValue([]);
    api.createEditTimeline.mockRejectedValue(
      new ApiError('seed failed', 500, [], {
        message: 'A clip on the timeline overlaps the next clip, so the seed was refused.',
        violations: [{ path: 'tracks[1].clips[0].sourceIn', message: 'must not overlap the previous clip' }],
      }),
    );
    const caught = await openTimelineForRender('p1', 'sr9').catch((error: unknown) => error);
    expect(caught).toBeInstanceOf(ApiError);
    expect(seedRuleSentence(caught)).toContain('overlaps the next clip');
    expect(seedRuleSentence(caught)).toContain('tracks[1].clips[0].sourceIn');
  });
});

describe('editor links', () => {
  it('leaves the default view out of the hub URL', () => {
    expect(editorHubHref()).toBe('/editor');
    expect(editorHubHref('p1')).toBe('/editor?project=p1');
    expect(editorHubHref('p1', 'videos')).toBe('/editor?project=p1');
    expect(editorHubHref('p1', 'media')).toBe('/editor?project=p1');
    expect(editorHubHref('p1', 'timelines')).toBe('/editor?project=p1');
    expect(editorHubHref(null, 'pictures')).toBe('/editor?view=pictures');
    expect(timelineEditorHref('p 1', 't1')).toBe('/editor/p%201/timeline/t1');
  });

  it('says how long ago in a few words', () => {
    const now = Date.parse('2026-10-11T12:00:00Z');
    expect(relativeTime('2026-10-11T11:59:50Z', now)).toBe('Just now');
    expect(relativeTime('2026-10-11T11:30:00Z', now)).toBe('30 min ago');
    expect(relativeTime('2026-10-11T09:00:00Z', now)).toBe('3 h ago');
    expect(relativeTime('2026-10-10T10:00:00Z', now)).toBe('Yesterday');
    expect(relativeTime('2026-10-01T10:00:00Z', now)).toBe('Oct 1');
  });
});
