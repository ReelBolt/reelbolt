// @vitest-environment jsdom
/**
 * The welcome flow (docs/design/new-user-funnel.md section 3, step 4): two one-click questions, the
 * first project with the recipe the API names, and the place the person lands. Everything the flow
 * sends is asserted against the contract's shapes: `PUT /api/v1/onboarding/profile` with
 * `{persona, material}` or `{skipped: true}`, and `POST /api/v1/projects` with `starterTemplateKey`.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { PERSONAS, type Persona } from '@/lib/attribution/persona';
import { MATERIALS, type Material } from '@/lib/onboarding/state';
import { userKeyFromEmail } from '@/lib/telemetry';
import en from '../../messages/en/welcome.json';

const mocks = vi.hoisted(() => ({ emitFunnelEvent: vi.fn(), push: vi.fn(), replace: vi.fn() }));

// Only the emitter is replaced; `userKeyFromEmail` stays real, so the key asserted is the app's own.
vi.mock('@/lib/telemetry', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/telemetry')>()),
  emitFunnelEvent: mocks.emitFunnelEvent,
}));
vi.mock('next/navigation', () => ({ useRouter: () => ({ push: mocks.push, replace: mocks.replace }) }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { email: 'Ada@Example.com' } }) }));

import { WelcomeFlow } from './WelcomeFlow';

const PROJECT_ID = '2f0d5b7c-1a34-4c2e-9f10-8b6a1d2e3f40';
const SAMPLES = ['https://media.example.test/one.mp4', 'https://media.example.test/two.mp4'];

type Call = { url: string; method: string; body: unknown };
let calls: Call[];
let profile: Record<string, unknown> | null;
let profileStatus: number;
let starterTemplateKey: string | null;
let createStatus: number;
let canConnect: boolean;
/** Whether the project the flow made earlier can still be read (false: it was deleted since). */
let projectGone: boolean;
/** Per sample URL: the status the import answers with, and the body of a refusal. */
let importAnswers: Record<string, { status: number; body?: unknown }>;

const blank = { persona: null, material: null, suggestedPersona: null, answeredAt: null, skippedAt: null };
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

beforeEach(() => {
  window.localStorage.clear();
  window.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null,
    addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
  mocks.emitFunnelEvent.mockClear();
  mocks.push.mockClear();
  mocks.replace.mockClear();
  calls = [];
  profile = { ...blank };
  profileStatus = 200;
  starterTemplateKey = null;
  createStatus = 201;
  canConnect = false;
  projectGone = false;
  importAnswers = {};
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      const body = init?.body ? JSON.parse(String(init.body)) : undefined;
      calls.push({ url, method, body });
      if (url === '/api/v1/onboarding') {
        return json({ tour: { status: 'pending', version: null, updatedAt: null }, checklist: { dismissed: false, items: [] }, ...(profile ? { profile } : {}) });
      }
      if (url === '/api/v1/onboarding/profile') {
        if (profileStatus !== 200) return json({ message: 'nope' }, profileStatus);
        const answered = body.skipped
          ? { ...blank, skippedAt: '2026-11-11T10:00:00Z' }
          : { ...blank, persona: body.persona, material: body.material, answeredAt: '2026-11-11T10:00:00Z' };
        const key = body.skipped ? null : (starterTemplateKey ?? (body.material === 'code' ? 'quick-win-promo' : 'edit-my-video'));
        return json({ profile: answered, starterTemplateKey: key });
      }
      if (url === '/api/v1/projects' && method === 'POST') {
        if (createStatus !== 201) return json({ message: 'You do not have permission to create projects here.' }, createStatus);
        return json({ id: PROJECT_ID, name: body.name }, 201);
      }
      if (url === `/api/v1/projects/${PROJECT_ID}` && method === 'GET') {
        return projectGone ? json({ message: 'Not found' }, 404) : json({ id: PROJECT_ID, name: 'My first video' });
      }
      if (url === '/api/v1/github/account') return json({ connected: false, canConnect });
      if (url === `/api/v1/projects/${PROJECT_ID}/files/import-url`) {
        const answer = importAnswers[body.url] ?? { status: 201 };
        return json(answer.body ?? { id: `file-${body.url}` }, answer.status);
      }
      return new Response(null, { status: 204 });
    }),
  );
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const mount = (props: Parameters<typeof WelcomeFlow>[0] = {}) =>
  render(
    <MantineProvider>
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <WelcomeFlow {...props} />
      </SWRConfig>
    </MantineProvider>,
  );

const personaCard = (persona: Persona) => document.querySelector<HTMLButtonElement>(`[data-persona="${persona}"]`)!;
const materialCard = (material: Material) => document.querySelector<HTMLButtonElement>(`[data-material="${material}"]`)!;
const step = () => screen.getByTestId('welcome-flow').getAttribute('data-step');
const of = (url: string, method: string) => calls.filter((c) => c.url === url && c.method === method);
const profilePuts = () => of('/api/v1/onboarding/profile', 'PUT');
const creates = () => of('/api/v1/projects', 'POST');
const imports = () => of(`/api/v1/projects/${PROJECT_ID}/files/import-url`, 'POST');

/** Answers both questions with one click each. */
async function answer(persona: Persona, material: Material) {
  fireEvent.click(personaCard(persona));
  expect(step()).toBe('material');
  fireEvent.click(materialCard(material));
  await waitFor(() => expect(creates()).toHaveLength(1));
}

describe('WelcomeFlow: the two questions', () => {
  it('asks what the video is for first, with the six audiences and "something else" as cards in their own words', () => {
    mount();
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('What are you here to make a video for?');
    expect(screen.getByText('Question 1 of 2')).toBeTruthy();
    const group = screen.getByRole('group', { name: 'What the video is for' });
    expect(within(group).getAllByRole('button').map((b) => b.getAttribute('data-persona'))).toEqual([...PERSONAS]);
    for (const persona of PERSONAS) {
      expect(personaCard(persona).textContent).toContain(en.personas[persona].title);
      expect(personaCard(persona).textContent).toContain(en.personas[persona].words);
    }
    expect(personaCard('creator').textContent).toContain('I film all week and edit all weekend.');
  });

  it('asks what the person starts from second, saying "code" in plain words', () => {
    mount();
    fireEvent.click(personaCard('business'));
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('What do you have to start from?');
    expect(screen.getByText('Question 2 of 2')).toBeTruthy();
    const group = screen.getByRole('group', { name: 'What you start from' });
    expect(within(group).getAllByRole('button').map((b) => b.getAttribute('data-material'))).toEqual([...MATERIALS]);
    expect(materialCard('footage').textContent).toContain('Clips I already shot');
    expect(materialCard('code').textContent).toContain('An app or website I built');
    expect(materialCard('idea').textContent).toContain('Just an idea so far');
  });

  it('preselects the audience of the landing page the person came from, one click from the next question', async () => {
    profile = { ...blank, suggestedPersona: 'educator' };
    mount();
    await waitFor(() => expect(personaCard('educator').getAttribute('aria-pressed')).toBe('true'));
    for (const persona of PERSONAS.filter((p) => p !== 'educator')) {
      expect(personaCard(persona).getAttribute('aria-pressed')).toBe('false');
    }
    fireEvent.click(screen.getByTestId('welcome-continue'));
    expect(step()).toBe('material');
    fireEvent.click(materialCard('footage'));
    await waitFor(() => expect(profilePuts()).toHaveLength(1));
    expect(profilePuts()[0].body).toEqual({ persona: 'educator', material: 'footage' });
  });

  it('offers no Continue while nothing is selected, and lets the person pick another audience than the suggested one', async () => {
    mount();
    await waitFor(() => expect(of('/api/v1/onboarding', 'GET')).toHaveLength(1));
    expect(screen.queryByTestId('welcome-continue')).toBeNull();
    cleanup();

    profile = { ...blank, suggestedPersona: 'software' };
    mount();
    await waitFor(() => expect(personaCard('software').getAttribute('aria-pressed')).toBe('true'));
    await answer('nonprofit', 'footage');
    expect(profilePuts()[0].body).toEqual({ persona: 'nonprofit', material: 'footage' });
  });

  it('goes back from the second question with the choice kept', () => {
    mount();
    fireEvent.click(personaCard('agency'));
    fireEvent.click(screen.getByRole('button', { name: 'Back' }));
    expect(step()).toBe('persona');
    expect(personaCard('agency').getAttribute('aria-pressed')).toBe('true');
    expect(profilePuts()).toHaveLength(0);
  });

  it('gives every control a touch target of at least 44px, on each screen', async () => {
    const targets = () =>
      within(screen.getByTestId('welcome-flow')).getAllByRole('button').map((b) => Number.parseInt((b as HTMLElement).style.minHeight, 10));
    mount({ sampleClipUrls: SAMPLES });
    expect(targets().length).toBe(PERSONAS.length + 1);
    expect(Math.min(...targets())).toBeGreaterThanOrEqual(44);
    fireEvent.click(personaCard('creator'));
    expect(targets().length).toBe(MATERIALS.length + 3);
    expect(Math.min(...targets())).toBeGreaterThanOrEqual(44);
    fireEvent.click(materialCard('idea'));
    await waitFor(() => expect(step()).toBe('shots'));
    expect(targets().length).toBe(4);
    expect(Math.min(...targets())).toBeGreaterThanOrEqual(44);
  });
});

describe('WelcomeFlow: the answer creates the first project', () => {
  it.each(PERSONAS.flatMap((persona) => MATERIALS.map((material) => [persona, material] as const)))(
    '%s + %s: saves the answer, then creates the project with the recipe the API named',
    async (persona, material) => {
      mount();
      await answer(persona, material);
      expect(profilePuts().map((c) => c.body)).toEqual([{ persona, material }]);
      const expectedKey = material === 'code' ? 'quick-win-promo' : 'edit-my-video';
      expect(creates()[0].body).toEqual({ name: 'My first video', description: '', starterTemplateKey: expectedKey });
      // The profile is saved before the project is created.
      expect(calls.indexOf(profilePuts()[0])).toBeLessThan(calls.indexOf(creates()[0]));
      if (material === 'idea') {
        await waitFor(() => expect(step()).toBe('shots'));
        expect(mocks.push).not.toHaveBeenCalled();
      } else {
        await waitFor(() => expect(mocks.push).toHaveBeenCalledWith(`/projects/${PROJECT_ID}?tab=files`));
      }
    },
  );

  it('passes on whatever recipe key the API answers, not a key of its own', async () => {
    starterTemplateKey = 'video-derush-edit-graphics';
    mount();
    await answer('business', 'footage');
    expect(creates()[0].body).toMatchObject({ starterTemplateKey: 'video-derush-edit-graphics' });
  });

  it('creates the project with no folder and no tags', async () => {
    mount();
    await answer('creator', 'footage');
    expect(Object.keys(creates()[0].body as object).sort()).toEqual(['description', 'name', 'starterTemplateKey']);
  });

  it('sends code to the Repository tab when this server can connect a repository', async () => {
    canConnect = true;
    mount();
    fireEvent.click(personaCard('software'));
    await waitFor(() => expect(of('/api/v1/github/account', 'GET')).toHaveLength(1));
    // Let the answer land before the click, as a person reading the three cards would.
    await new Promise((r) => setTimeout(r, 20));
    fireEvent.click(materialCard('code'));
    await waitFor(() => expect(mocks.push).toHaveBeenCalledWith(`/projects/${PROJECT_ID}?tab=repository`));
  });

  it('sends code to the Files tab (zip upload) when no repository can be connected', async () => {
    canConnect = false;
    mount();
    await answer('software', 'code');
    await waitFor(() => expect(mocks.push).toHaveBeenCalledWith(`/projects/${PROJECT_ID}?tab=files`));
  });

  it('emits alpha.first_project on the transition, once, keyed to the user, and never on render', async () => {
    mount();
    fireEvent.click(personaCard('creator'));
    expect(mocks.emitFunnelEvent).not.toHaveBeenCalled();
    fireEvent.click(materialCard('footage'));
    await waitFor(() => expect(mocks.push).toHaveBeenCalled());
    expect(mocks.emitFunnelEvent).toHaveBeenCalledTimes(1);
    expect(mocks.emitFunnelEvent).toHaveBeenCalledWith('alpha.first_project', userKeyFromEmail('ada@example.com'));
  });

  it('never blocks the project on a failed profile save: the contract\'s own mapping names the recipe', async () => {
    profileStatus = 500;
    mount();
    await answer('educator', 'footage');
    expect(creates()[0].body).toMatchObject({ starterTemplateKey: 'edit-my-video' });
    await waitFor(() => expect(mocks.push).toHaveBeenCalledWith(`/projects/${PROJECT_ID}?tab=files`));
  });

  it('ignores a second click while the first is still working', async () => {
    mount();
    fireEvent.click(personaCard('creator'));
    fireEvent.click(materialCard('footage'));
    fireEvent.click(materialCard('code'));
    fireEvent.click(materialCard('idea'));
    await waitFor(() => expect(mocks.push).toHaveBeenCalled());
    expect(profilePuts()).toHaveLength(1);
    expect(creates()).toHaveLength(1);
  });

  it('says so when the project cannot be created, emits nothing, and lets the person try again', async () => {
    createStatus = 403;
    mount();
    await answer('creator', 'footage');
    const alert = await screen.findByTestId('welcome-error');
    expect(alert.textContent).toContain('Your project could not be set up');
    expect(alert.textContent).toContain('You do not have permission to create projects here.');
    expect(mocks.emitFunnelEvent).not.toHaveBeenCalled();
    expect(mocks.push).not.toHaveBeenCalled();

    createStatus = 201;
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(mocks.push).toHaveBeenCalledWith(`/projects/${PROJECT_ID}?tab=files`));
    expect(creates()).toHaveLength(2);
    expect(mocks.emitFunnelEvent).toHaveBeenCalledTimes(1);
  });
});

describe('WelcomeFlow: skip', () => {
  it('shows "Skip for now" on both questions', () => {
    mount();
    expect(screen.getByRole('button', { name: 'Skip for now' })).toBeTruthy();
    fireEvent.click(personaCard('creator'));
    expect(screen.getByRole('button', { name: 'Skip for now' })).toBeTruthy();
  });

  it('records the skip and goes to the dashboard without creating anything', async () => {
    mount();
    fireEvent.click(screen.getByTestId('welcome-skip'));
    expect(mocks.replace).toHaveBeenCalledWith('/dashboard');
    await waitFor(() => expect(profilePuts()).toHaveLength(1));
    expect(profilePuts()[0].body).toEqual({ skipped: true });
    expect(creates()).toHaveLength(0);
    expect(mocks.emitFunnelEvent).not.toHaveBeenCalled();
  });

  it('records one skip however many times the button is pressed', async () => {
    mount();
    fireEvent.click(screen.getByTestId('welcome-skip'));
    fireEvent.click(screen.getByTestId('welcome-skip'));
    await waitFor(() => expect(profilePuts()).toHaveLength(1));
    await new Promise((r) => setTimeout(r, 50));
    expect(profilePuts()).toHaveLength(1);
    expect(mocks.replace).toHaveBeenCalledTimes(1);
  });
});

describe('WelcomeFlow: an idea gets a shot list first', () => {
  it.each(PERSONAS)('%s: five concrete clips to film, then the drop zone', async (persona) => {
    mount();
    await answer(persona, 'idea');
    await waitFor(() => expect(step()).toBe('shots'));
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Film these five clips on your phone');
    expect(screen.getByText(/About ten seconds each is plenty/)).toBeTruthy();
    const items = within(screen.getByRole('list', { name: 'Your five clips' })).getAllByRole('listitem');
    expect(items.map((i) => i.textContent)).toEqual(Object.values(en.shots.lists[persona]));
    expect(items).toHaveLength(5);

    fireEvent.click(screen.getByRole('button', { name: 'I have my clips. Add them' }));
    expect(mocks.push).toHaveBeenCalledWith(`/projects/${PROJECT_ID}?tab=files`);
  });

  it('lets the person leave to film: the project is kept and the dashboard is next', async () => {
    mount();
    await answer('creator', 'idea');
    await waitFor(() => expect(step()).toBe('shots'));
    fireEvent.click(screen.getByRole('button', { name: 'I will film them first' }));
    expect(mocks.push).toHaveBeenCalledWith('/dashboard');
    expect(screen.getByText(/This list is on your dashboard when you come back/)).toBeTruthy();
  });

  /**
   * The shot list is the screen a person leaves to go and film, which is when a phone puts the tab
   * aside and reloads it later. The step used to live only in memory: the reload showed Question 1
   * again, and answering it made a second "My first video".
   */
  it('puts the step and the project in the address, so a reload reopens this list', async () => {
    mount();
    await answer('creator', 'idea');
    await waitFor(() => expect(step()).toBe('shots'));
    expect(mocks.replace).toHaveBeenCalledWith(`/welcome?step=shots&project=${PROJECT_ID}`);
    // A replace, not a push: Back does not return to a question that would create another project.
    expect(mocks.push).not.toHaveBeenCalled();
  });

  it('does not touch the address on the paths that leave the flow at once', async () => {
    mount();
    await answer('creator', 'footage');
    await waitFor(() => expect(mocks.push).toHaveBeenCalledWith(`/projects/${PROJECT_ID}?tab=files`));
    expect(mocks.replace).not.toHaveBeenCalled();
  });

  it('answering again after a reload opens the project the first answer made, not a second one', async () => {
    mount();
    await answer('creator', 'idea');
    await waitFor(() => expect(step()).toBe('shots'));
    expect(creates()).toHaveLength(1);

    // The tab is reloaded at the bare address: the questions start over, the browser remembers.
    cleanup();
    profile = { ...blank, persona: 'creator', material: 'idea', answeredAt: '2026-11-11T10:00:00Z' };
    mocks.emitFunnelEvent.mockClear();
    mount();
    expect(step()).toBe('persona');
    await answer('creator', 'idea');
    await waitFor(() => expect(step()).toBe('shots'));
    expect(creates()).toHaveLength(1);
    expect(of(`/api/v1/projects/${PROJECT_ID}`, 'GET')).toHaveLength(1);
    // No second project means no second "first project" event either.
    expect(mocks.emitFunnelEvent).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'I have my clips. Add them' }));
    expect(mocks.push).toHaveBeenCalledWith(`/projects/${PROJECT_ID}?tab=files`);
  });

  it('makes a new project when the earlier one was deleted, or when the new answer needs another recipe', async () => {
    mount();
    await answer('creator', 'idea');
    await waitFor(() => expect(step()).toBe('shots'));
    cleanup();

    // Deleted since: there is nothing to reopen.
    projectGone = true;
    mount();
    fireEvent.click(personaCard('creator'));
    fireEvent.click(materialCard('idea'));
    await waitFor(() => expect(creates()).toHaveLength(2));
    await waitFor(() => expect(step()).toBe('shots'));
    cleanup();

    // Still there, but the person now starts from code: that is another app, so another project.
    projectGone = false;
    mount();
    fireEvent.click(personaCard('software'));
    fireEvent.click(materialCard('code'));
    await waitFor(() => expect(creates()).toHaveLength(3));
    expect(creates()[2].body).toMatchObject({ starterTemplateKey: 'quick-win-promo' });
  });

  it('reopens the shot list of an existing project with the audience the person answered, creating nothing', async () => {
    profile = { ...blank, persona: 'nonprofit', material: 'idea', answeredAt: '2026-11-11T10:00:00Z' };
    mount({ shotsForProjectId: PROJECT_ID });
    expect(step()).toBe('shots');
    // Nothing of another audience's list flashes while the person's own answer loads.
    expect(screen.queryByRole('list', { name: 'Your five clips' })).toBeNull();
    await waitFor(() =>
      expect(within(screen.getByRole('list', { name: 'Your five clips' })).getAllByRole('listitem')[0].textContent).toBe(en.shots.lists.nonprofit.one),
    );
    fireEvent.click(screen.getByRole('button', { name: 'I have my clips. Add them' }));
    expect(mocks.push).toHaveBeenCalledWith(`/projects/${PROJECT_ID}?tab=files`);
    expect(profilePuts()).toHaveLength(0);
    expect(creates()).toHaveLength(0);
  });

  it('shows the general shot list when the person never said who they are', async () => {
    profile = { ...blank };
    mount({ shotsForProjectId: PROJECT_ID });
    await waitFor(() =>
      expect(within(screen.getByRole('list', { name: 'Your five clips' })).getAllByRole('listitem')[0].textContent).toBe(en.shots.lists.other.one),
    );
  });

  it('has a shot list of five non-empty, distinct prompts for every audience', () => {
    for (const persona of PERSONAS) {
      const prompts = Object.values(en.shots.lists[persona]);
      expect(prompts).toHaveLength(5);
      expect(new Set(prompts).size).toBe(5);
    }
  });
});

describe('WelcomeFlow: sample clips', () => {
  it('does not render the offer at all when no sample clip is configured (the default)', async () => {
    mount();
    expect(screen.queryByTestId('welcome-samples')).toBeNull();
    fireEvent.click(personaCard('creator'));
    expect(screen.queryByTestId('welcome-samples')).toBeNull();
    fireEvent.click(materialCard('idea'));
    await waitFor(() => expect(step()).toBe('shots'));
    expect(screen.queryByTestId('welcome-samples')).toBeNull();
    expect(document.body.textContent).not.toContain('sample clips');
  });

  it('offers them on the second question and imports each into the new project with the import-from-link API', async () => {
    mount({ sampleClipUrls: SAMPLES });
    expect(screen.queryByTestId('welcome-samples')).toBeNull();
    fireEvent.click(personaCard('creator'));
    const offer = screen.getByTestId('welcome-samples');
    expect(offer.textContent).toBe('No clips handy? Try it with our sample clips');
    fireEvent.click(offer);
    await waitFor(() => expect(mocks.push).toHaveBeenCalledWith(`/projects/${PROJECT_ID}?tab=files`));
    // Sample clips are footage: the answer and the recipe say so.
    expect(profilePuts()[0].body).toEqual({ persona: 'creator', material: 'footage' });
    expect(creates()[0].body).toMatchObject({ starterTemplateKey: 'edit-my-video' });
    expect(imports().map((c) => c.body)).toEqual(SAMPLES.map((url) => ({ url })));
  });

  it('offers them on the shot list too, into the project that already exists', async () => {
    mount({ sampleClipUrls: SAMPLES });
    await answer('educator', 'idea');
    await waitFor(() => expect(step()).toBe('shots'));
    fireEvent.click(screen.getByTestId('welcome-samples'));
    await waitFor(() => expect(mocks.push).toHaveBeenCalledWith(`/projects/${PROJECT_ID}?tab=files`));
    expect(creates()).toHaveLength(1);
    expect(profilePuts()).toHaveLength(1);
    expect(imports()).toHaveLength(2);
  });

  it('handles an unconfirmed email gracefully: one refusal, a plain explanation, the project still opens', async () => {
    const refusal = { status: 403, body: { code: 'EMAIL_NOT_VERIFIED', message: 'Confirm your email address first.' } };
    importAnswers = { [SAMPLES[0]]: refusal, [SAMPLES[1]]: refusal };
    mount({ sampleClipUrls: SAMPLES });
    fireEvent.click(personaCard('creator'));
    fireEvent.click(screen.getByTestId('welcome-samples'));
    const note = await screen.findByTestId('welcome-samples-unverified');
    expect(note.textContent).toContain('Confirm your email to use the sample clips');
    expect(note.textContent).toContain('Your project is ready');
    // Every clip would be refused the same way: the first refusal ends the attempt.
    expect(imports()).toHaveLength(1);
    expect(mocks.push).not.toHaveBeenCalled();
    // The project exists: the funnel event fired and the person can open it.
    expect(mocks.emitFunnelEvent).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: 'Open my project' }));
    expect(mocks.push).toHaveBeenCalledWith(`/projects/${PROJECT_ID}?tab=files`);
  });

  it('retries only what is still missing, into the same project', async () => {
    importAnswers = { [SAMPLES[1]]: { status: 422, body: { code: 'IMPORT_FAILED', message: 'The link could not be read.' } } };
    mount({ sampleClipUrls: SAMPLES });
    fireEvent.click(personaCard('creator'));
    fireEvent.click(screen.getByTestId('welcome-samples'));
    const note = await screen.findByTestId('welcome-samples-partial');
    expect(note.textContent).toContain('Some sample clips could not be added');
    expect(note.textContent).toContain('1 sample clip is in your project');
    expect(imports()).toHaveLength(2);

    importAnswers = {};
    fireEvent.click(screen.getByRole('button', { name: 'Try the sample clips again' }));
    // The retry stays on this screen (it does not fall back to a question) and says it is working.
    expect(step()).toBe('samples');
    expect(screen.getByRole('status').textContent).toContain('Adding the sample clips...');
    await waitFor(() => expect(mocks.push).toHaveBeenCalledWith(`/projects/${PROJECT_ID}?tab=files`));
    expect(imports().map((c) => c.body)).toEqual([{ url: SAMPLES[0] }, { url: SAMPLES[1] }, { url: SAMPLES[1] }]);
    expect(creates()).toHaveLength(1);
  });

  it('says so when no sample clip could be added, and still opens the project', async () => {
    const failure = { status: 422, body: { code: 'IMPORT_FAILED', message: 'The link could not be read.' } };
    importAnswers = { [SAMPLES[0]]: failure, [SAMPLES[1]]: failure };
    mount({ sampleClipUrls: SAMPLES });
    fireEvent.click(personaCard('business'));
    fireEvent.click(screen.getByTestId('welcome-samples'));
    const note = await screen.findByTestId('welcome-samples-failed');
    expect(note.textContent).toContain('The sample clips could not be added');
    fireEvent.click(screen.getByRole('button', { name: 'Open my project' }));
    expect(mocks.push).toHaveBeenCalledWith(`/projects/${PROJECT_ID}?tab=files`);
  });
});
