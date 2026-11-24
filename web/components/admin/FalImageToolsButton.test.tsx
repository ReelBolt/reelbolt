// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, cleanup, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { InferenceModel, InferenceProvider } from '@/lib/types/inference-provider';

const mocks = vi.hoisted(() => ({ create: vi.fn(), notify: vi.fn() }));

vi.mock('@mantine/notifications', () => ({ notifications: { show: mocks.notify } }));
vi.mock('@/lib/api/inference-providers', () => ({ createImageToolModels: mocks.create }));

import { FalImageToolsButton } from './FalImageToolsButton';

const row = (over: Partial<InferenceProvider> = {}): InferenceProvider => ({
  id: 'fal-1',
  organizationId: null,
  name: 'Fal.AI',
  kind: 'FalAi',
  endpoint: 'https://queue.fal.run',
  hasApiKey: true,
  apiKeyLastFour: 'ab12',
  isEnabled: true,
  isSystem: false,
  timeoutSeconds: null,
  personalOAuthMode: false,
  createdAt: '2026-10-10T00:00:00Z',
  updatedAt: '2026-10-10T00:00:00Z',
  models: [],
  ...over,
});

const model = (displayName: string): InferenceModel => ({
  id: `m-${displayName}`,
  modelCode: 'fal-ai/seedvr/upscale/image',
  displayName,
  isEnabled: true,
  capabilities: [],
});

describe('FalImageToolsButton', () => {
  beforeEach(() => {
    global.matchMedia = (query: string): MediaQueryList =>
      ({
        matches: false,
        media: query,
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => true,
      }) as MediaQueryList;
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('renders nothing for a provider that is not fal.ai', () => {
    render(
      <MantineProvider>
        <FalImageToolsButton provider={row({ kind: 'BlackForestLabs' })} />
      </MantineProvider>,
    );
    expect(screen.queryByRole('button', { name: /Set up all picture tools/ })).toBeNull();
  });

  it('adds the models through the scope it was given and names them', async () => {
    mocks.create.mockResolvedValue([model('Fal.AI - Upscale')]);
    const onDone = vi.fn();
    render(
      <MantineProvider>
        <FalImageToolsButton provider={row()} scope="org" onDone={onDone} />
      </MantineProvider>,
    );

    fireEvent.click(screen.getByRole('button', { name: /Set up all picture tools/ }));

    await waitFor(() => expect(onDone).toHaveBeenCalled());
    expect(mocks.create).toHaveBeenCalledWith('fal-1', 'org');
    expect(mocks.notify).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'Picture tools set up', message: expect.stringContaining('Fal.AI - Upscale') }),
    );
  });

  it('says so plainly when there was nothing to add, and shows a refusal as an error', async () => {
    mocks.create.mockResolvedValueOnce([]);
    render(
      <MantineProvider>
        <FalImageToolsButton provider={row()} />
      </MantineProvider>,
    );
    fireEvent.click(screen.getByRole('button', { name: /Set up all picture tools/ }));
    await waitFor(() => expect(mocks.notify).toHaveBeenCalledWith(expect.objectContaining({ title: 'Nothing to add' })));

    mocks.create.mockRejectedValueOnce(new Error('This provider has no API key to reuse. Save a key on it first.'));
    fireEvent.click(screen.getByRole('button', { name: /Set up all picture tools/ }));
    await waitFor(() =>
      expect(mocks.notify).toHaveBeenCalledWith(
        expect.objectContaining({ color: 'red', message: 'This provider has no API key to reuse. Save a key on it first.' }),
      ),
    );
  });
});
