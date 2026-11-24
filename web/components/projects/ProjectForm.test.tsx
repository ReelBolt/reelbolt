// @vitest-environment jsdom
/**
 * The plain "New project" form decides the project's first app. It used to send no recipe at all,
 * so every project got the promo-from-code app, whoever made it: someone told to "add your clips"
 * was then led to an app that needs source code. The form now says what the project starts from,
 * preselected from what the person told the welcome questions, and sends the matching recipe.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { renderWithI18n } from '@/test/i18n';
import type { OnboardingProfile } from '@/lib/onboarding/state';

const env = vi.hoisted(() => ({
  createProject: vi.fn(),
  updateProject: vi.fn(),
  profile: null as OnboardingProfile | null,
}));
vi.mock('@/lib/api/projects', () => ({ createProject: env.createProject, updateProject: env.updateProject }));
vi.mock('@/lib/hooks/use-onboarding', () => ({ useOnboarding: () => ({ profile: env.profile }) }));
vi.mock('@/lib/hooks/use-project-folders', () => ({ useProjectFolders: () => ({ data: [] }) }));
vi.mock('@/lib/hooks/use-project-tags', () => ({ useProjectTags: () => ({ data: [] }) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));

import { ProjectForm } from './ProjectForm';

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

const blank: OnboardingProfile = { persona: null, material: null, suggestedPersona: null, pickedPlan: null, answeredAt: null, skippedAt: null };
const onSuccess = vi.fn();
const mount = (props: Partial<React.ComponentProps<typeof ProjectForm>> = {}) =>
  renderWithI18n(<ProjectForm opened onClose={() => {}} onSuccess={onSuccess} {...props} />, { locale: 'en' });

const chosen = () =>
  (screen.getAllByRole('radio') as HTMLInputElement[]).find((radio) => radio.checked)?.value ?? null;
const create = async (name = 'Autumn menu') => {
  fireEvent.change(screen.getByLabelText(/^Name/), { target: { value: name } });
  fireEvent.click(screen.getByRole('button', { name: 'Create project' }));
  await waitFor(() => expect(env.createProject).toHaveBeenCalledTimes(1));
  return env.createProject.mock.calls[0][0] as Record<string, unknown>;
};

beforeEach(() => {
  env.createProject.mockReset();
  env.createProject.mockResolvedValue({ id: 'p1', name: 'Autumn menu' });
  env.updateProject.mockReset();
  env.profile = null;
  onSuccess.mockReset();
});
afterEach(cleanup);

describe('ProjectForm: what the project starts from', () => {
  it('asks, in plain words, with clips chosen for someone nothing is known about', () => {
    mount();
    expect(screen.getByText('What is this video made from?')).toBeTruthy();
    expect(screen.getByRole('radio', { name: 'Clips I already shot' })).toBeTruthy();
    expect(screen.getByRole('radio', { name: 'An app or website I built' })).toBeTruthy();
    expect(chosen()).toBe('footage');
  });

  it('creates the project with the footage recipe, not the automatic promo-from-code app', async () => {
    mount();
    const body = await create();
    expect(body).toMatchObject({ name: 'Autumn menu', starterTemplateKey: 'edit-my-video' });
  });

  it('creates a code project with the promo recipe when the person says they start from an app', async () => {
    mount();
    fireEvent.click(screen.getByRole('radio', { name: 'An app or website I built' }));
    const body = await create('Release 4.2');
    expect(body).toMatchObject({ starterTemplateKey: 'quick-win-promo' });
  });

  it.each([
    [{ ...blank, material: 'code' as const }, 'code'],
    [{ ...blank, material: 'footage' as const, persona: 'software' as const }, 'footage'],
    // An idea is filmed: it starts from clips.
    [{ ...blank, material: 'idea' as const }, 'footage'],
    // No answer: the audience the person named, or arrived as.
    [{ ...blank, persona: 'software' as const }, 'code'],
    [{ ...blank, suggestedPersona: 'software' as const }, 'code'],
    [{ ...blank, suggestedPersona: 'nonprofit' as const }, 'footage'],
    [blank, 'footage'],
  ])('starts from what the person told the welcome questions (%j -> %s)', (profile, expected) => {
    env.profile = profile;
    mount();
    expect(chosen()).toBe(expected);
  });

  it('keeps the person\'s own choice over the suggestion', async () => {
    env.profile = { ...blank, material: 'code' };
    mount();
    fireEvent.click(screen.getByRole('radio', { name: 'Clips I already shot' }));
    const body = await create();
    expect(body).toMatchObject({ starterTemplateKey: 'edit-my-video' });
  });

  it('asks in Italian too', () => {
    renderWithI18n(<ProjectForm opened onClose={() => {}} onSuccess={onSuccess} />, { locale: 'it' });
    expect(screen.getByText('Da cosa parte questo video?')).toBeTruthy();
    expect(screen.getByRole('radio', { name: 'Clip che ho già girato' })).toBeTruthy();
    expect(screen.getByRole('radio', { name: "Un'app o un sito che ho creato" })).toBeTruthy();
  });

  it('does not ask when an existing project is edited, and sends no recipe', async () => {
    env.updateProject.mockResolvedValue({ id: 'p1', name: 'Renamed' });
    mount({ project: { id: 'p1', name: 'Old', description: '' } as never });
    expect(screen.queryByText('What is this video made from?')).toBeNull();
    expect(screen.queryAllByRole('radio')).toHaveLength(0);
    fireEvent.change(screen.getByLabelText(/^Name/), { target: { value: 'Renamed' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(env.updateProject).toHaveBeenCalledTimes(1));
    expect(env.updateProject.mock.calls[0][1]).not.toHaveProperty('starterTemplateKey');
    expect(env.createProject).not.toHaveBeenCalled();
  });
});
