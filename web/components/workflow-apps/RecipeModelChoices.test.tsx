// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import type { ModelOptionsResponse } from './app-input-model';

const fetched = vi.hoisted(() => ({ urls: [] as string[], body: [] as unknown }));
vi.mock('@/lib/api/client', () => ({
  fetcher: async (url: string) => {
    fetched.urls.push(url);
    return fetched.body;
  },
}));

import { RecipeModelChoices } from './RecipeModelChoices';

function renderChoices() {
  return render(
    <MantineProvider env="test">
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <RecipeModelChoices projectId="p1" recipeKey="avatar-clip" />
      </SWRConfig>
    </MantineProvider>,
  );
}

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});

afterEach(() => {
  cleanup();
  fetched.urls = [];
});

describe('RecipeModelChoices (docs/design/app-inputs.md 28)', () => {
  it('reads nothing until opened, then lists each model field with words, never a figure', async () => {
    const fields: ModelOptionsResponse[] = [
      {
        inputId: 'videoModel',
        capability: 'VideoGeneration',
        label: 'Video model',
        options: [
          { id: 'a', label: 'MiniMax H3', recommended: true, available: true, costIndex: 1 },
          { id: 'b', label: 'Kling', recommended: false, available: true, costIndex: 2 },
          { id: 'c', label: 'Lip sync', recommended: false, available: false, unavailableCode: 'notInPlan', planFeature: 'avatars' },
        ],
      },
    ];
    fetched.body = fields;
    renderChoices();
    expect(fetched.urls).toEqual([]);

    fireEvent.click(screen.getByRole('button', { name: 'Model choices' }));
    expect(await screen.findByText('Video model')).toBeTruthy();
    expect(fetched.urls).toEqual(['/api/v1/projects/p1/workflow-apps/recipes/avatar-clip/model-options']);
    const items = screen.getAllByRole('listitem').map((li) => li.textContent);
    expect(items).toEqual([
      'MiniMax H3 · Recommended · About the same price',
      'Kling · About 2× the price',
      'Lip sync · Your plan does not include avatars',
    ]);
    expect(screen.getByRole('button', { name: 'Hide model choices' }).getAttribute('aria-expanded')).toBe('true');
  });

  it('says so when the recipe offers no model', async () => {
    fetched.body = [];
    renderChoices();
    fireEvent.click(screen.getByRole('button', { name: 'Model choices' }));
    expect(await screen.findByText('This recipe has no model to choose.')).toBeTruthy();
  });
});
