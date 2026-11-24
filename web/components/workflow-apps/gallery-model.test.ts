import { describe, expect, it } from 'vitest';
import type { WorkflowRecipeCard } from '@/lib/types/workflow';
import { createTranslator } from 'use-intl/core';
import { loadMessages } from '@/i18n/messages';
import type { Msg } from '@/i18n/msg';
import { fileKindsOf, costBadgeFor, filterCards, needsHref, rankRecipes, rankingKinds, runStatusFor, suggestedRecipes } from './gallery-model';

const recipe = (key: string, missing: number): WorkflowRecipeCard => ({
  key,
  title: key,
  tagline: null,
  category: 'promo',
  costClass: 'Uses credits',
  inputsCount: 1,
  fit: missing === 0 ? 'ready' : 'needs',
  missing: Array.from({ length: missing }, (_, i) => ({ need: `n${i}`, message: `m${i}`, action: 'add-files' as const })),
});

const tEn = createTranslator({ locale: 'en', messages: loadMessages('en') }) as unknown as (k: string, v?: Record<string, unknown>) => string;
const en = (m: Msg): string => tEn(m.$msg, m.values as Record<string, unknown>);

describe('gallery model', () => {
  it('words every cost class and marks AI shots amber', () => {
    expect(en(costBadgeFor('No credits').hint!)).toContain('processing time');
    expect(costBadgeFor('Uses credits and AI shots').tone).toBe('amber');
    expect(costBadgeFor('Uses credits and a voice').tone).toBe('plain');
  });

  it('turns the last run into a word', () => {
    expect(en(runStatusFor(null).word)).toBe('Not run yet');
    expect(en(runStatusFor({ status: 'Passed', finishedAt: null }).word)).toBe('Finished');
    expect(runStatusFor({ status: 'Failed', finishedAt: null }).kind).toBe('failed');
    expect(en(runStatusFor({ status: 'Queued', finishedAt: null }).word)).toBe('Running');
    expect(en(runStatusFor({ status: 'Cancelled', finishedAt: null }).word)).toBe('Stopped');
  });

  it('filters by category and by title or tagline, case-insensitively', () => {
    const cards = [
      { title: 'Highlight edit', tagline: 'Tight cut from clips', category: 'footage' as const },
      { title: 'Launch promo', tagline: null, category: 'promo' as const },
    ];
    expect(filterCards(cards, '', 'all')).toHaveLength(2);
    expect(filterCards(cards, 'TIGHT', 'all').map((c) => c.title)).toEqual(['Highlight edit']);
    expect(filterCards(cards, '', 'promo').map((c) => c.title)).toEqual(['Launch promo']);
    expect(filterCards(cards, 'launch', 'footage')).toEqual([]);
  });

  it('suggests ready recipes first, then the closest to ready, in catalog order', () => {
    const picks = suggestedRecipes([recipe('a', 2), recipe('b', 0), recipe('c', 1), recipe('d', 0), recipe('e', 0)]);
    expect(picks.map((r) => r.key)).toEqual(['b', 'd', 'e']);
    expect(suggestedRecipes([recipe('a', 2), recipe('b', 1)]).map((r) => r.key)).toEqual(['b', 'a']);
  });

  it('links each need to the place that fixes it', () => {
    expect(needsHref('p1', 'add-files')).toBe('/projects/p1?tab=files');
    expect(needsHref('p1', 'build-profile')).toBe('/projects/p1?tab=profile');
    expect(needsHref('p1', 'connect-provider')).toBe('/org/inference-providers');
    // "X is not included in your plan" goes to the plans, so it is never dead text.
    expect(needsHref('p1', 'upgrade-plan')).toBe('/settings/billing#plans');
    expect(needsHref('p1', 'something-new' as never)).toBeNull();
  });

  it('lets the welcome answer stand in for the files only while the project has no code', () => {
    const none = { code: false, video: false };
    expect(rankingKinds(none, 'footage')).toEqual({ code: false, video: true });
    expect(rankingKinds(none, 'idea')).toEqual({ code: false, video: true });
    // Starting from code, or no answer at all, changes nothing.
    expect(rankingKinds(none, 'code')).toEqual(none);
    expect(rankingKinds(none, null)).toEqual(none);
    expect(rankingKinds(none, undefined)).toEqual(none);
    expect(rankingKinds(none, 'something-new')).toEqual(none);
    // The files win: a project with code keeps its promo-first order whatever was answered.
    expect(rankingKinds({ code: true, video: false }, 'footage')).toEqual({ code: true, video: false });
    expect(rankingKinds({ code: false, video: true }, 'code')).toEqual({ code: false, video: true });
  });

  it('leads a footage or idea person\'s empty project with footage recipes, never the code promos', () => {
    // Catalogue order: the code promos come first, as on the API.
    const catalogue = [
      { ...recipe('quick-win-promo', 1), category: 'promo' as const },
      { ...recipe('lean-context-promo', 1), category: 'promo' as const },
      { ...recipe('video-derush-edit', 1), category: 'footage' as const },
      { ...recipe('testimonial-reel', 2), category: 'footage' as const },
      { ...recipe('thumbnail', 1), category: 'utility' as const },
      { ...recipe('video-subtitles', 1), category: 'footage' as const },
    ];
    const noFiles = fileKindsOf([]);
    for (const material of ['footage', 'idea']) {
      const lead = suggestedRecipes(catalogue, 3, rankingKinds(noFiles, material));
      expect(lead.map((r) => r.key)).toEqual(['video-derush-edit', 'video-subtitles', 'testimonial-reel']);
      expect(lead.some((r) => r.category === 'promo')).toBe(false);
      // The rest of the list keeps footage ahead of promo too.
      const rest = rankRecipes(catalogue, rankingKinds(noFiles, material)).map((r) => r.category);
      expect(rest.lastIndexOf('footage')).toBeLessThan(rest.indexOf('promo'));
    }
    // Unchanged for a person who starts from code and for one who was never asked.
    for (const material of ['code', null]) {
      expect(suggestedRecipes(catalogue, 3, rankingKinds(noFiles, material)).map((r) => r.key))
        .toEqual(['quick-win-promo', 'lean-context-promo', 'video-derush-edit']);
    }
  });

  it('ranks suggestions by the file kinds present before readiness', () => {
    const promo = { ...recipe('promo', 1), category: 'promo' as const };
    const footage = { ...recipe('footage', 2), category: 'footage' as const };
    const free = { ...recipe('free', 0), category: 'utility' as const };
    expect(suggestedRecipes([free, promo, footage], 3, { code: true, video: false }).map((r) => r.key)).toEqual(['promo', 'free', 'footage']);
    expect(suggestedRecipes([free, promo, footage], 3, { code: false, video: true }).map((r) => r.key)).toEqual(['footage', 'free', 'promo']);
  });

  it('reads file kinds from names and mime types', () => {
    expect(fileKindsOf(undefined)).toEqual({ code: false, video: false });
    expect(fileKindsOf([{ originalFileName: 'app.zip', mimeType: 'application/zip' }, { originalFileName: 'a.mov', mimeType: 'video/quicktime' }]))
      .toEqual({ code: false, video: true });
  });
});
