import { describe, expect, it } from 'vitest';
import { TOUR_STEPS, buildTourSteps } from '@/components/onboarding/tour-steps';
import { CHECKLIST_ITEM_GUIDES, GUIDE_SLUGS, TOUR_STEP_GUIDES, docsHomeHref, guideHref } from './guide-links';

// The slug contract with the docs lane (plan.md): /docs/basics/<slug>.
const CONTRACT = [
  'index',
  'sign-up-and-first-look',
  'create-a-project-and-upload',
  'pick-an-app-and-run',
  'follow-progress-and-share',
  'ask-for-changes',
  'editor-basics',
  'assistant-basics',
  'pictures-and-thumbnails',
  'plan-credits-and-billing',
  'connect-a-runner',
];

describe('tour steps by feature flag', () => {
  it('drops the editor and pictures steps when the editor is gated', () => {
    const ids = buildTourSteps({ editorEnabled: false, appsEnabled: true }).map((s) => s.id);
    expect(ids).toEqual(['welcome', 'new-project', 'apps', 'progress', 'assistant', 'credits']);
  });

  it('says Workflows instead of the Apps tab when apps are off', () => {
    const apps = buildTourSteps({ editorEnabled: true, appsEnabled: false }).find((s) => s.id === 'apps')!;
    expect(apps.body).toMatch(/Workflows tab/);
    expect(apps.body).not.toMatch(/Apps tab/);
    expect(buildTourSteps({ editorEnabled: true, appsEnabled: true })).toHaveLength(8);
  });
});

describe('guide links', () => {
  it('lists exactly the guides the docs publish', () => {
    expect([...GUIDE_SLUGS]).toEqual(CONTRACT);
  });

  it('points every tour step and checklist item at a slug in the contract', () => {
    for (const slug of Object.values(TOUR_STEP_GUIDES)) expect(CONTRACT).toContain(slug);
    for (const slug of Object.values(CHECKLIST_ITEM_GUIDES)) expect(CONTRACT).toContain(slug);
    for (const step of TOUR_STEPS) expect(CONTRACT).toContain(step.guide);
  });

  it('builds absolute /docs/basics hrefs', () => {
    expect(guideHref('editor-basics')).toBe('/docs/basics/editor-basics');
    expect(guideHref('index')).toBe('/docs/basics/');
  });

  it('has eight steps in the planned order, in plain words', () => {
    expect(TOUR_STEPS.map((s) => s.id)).toEqual(['welcome', 'new-project', 'apps', 'progress', 'assistant', 'editor', 'pictures', 'credits']);
    const text = TOUR_STEPS.map((s) => `${s.title} ${s.body}`).join(' ');
    expect(text).not.toMatch(/workflow engine|remotion|ffmpeg|inference|runner/i);
    expect(text).toMatch(/Early preview/);
  });
});

describe('docsHomeHref', () => {
  it('points at the documentation root in the active language', () => {
    expect(docsHomeHref('en')).toBe('/docs/');
    expect(docsHomeHref('it')).toBe('/docs/it/');
  });
});
