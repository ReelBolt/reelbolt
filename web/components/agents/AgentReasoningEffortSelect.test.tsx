// @vitest-environment jsdom
/**
 * Who may change an agent's thinking effort, and what is sent. The API mirrors the provider override's
 * authority: a built-in agent is a platform-admin action, a custom agent its workspace's Owner/Admin.
 * The select must offer exactly the API's vocabulary, with no `high`.
 */
import { beforeAll, afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const H = vi.hoisted(() => ({
  isPlatformAdmin: false,
  isOrgAdmin: false,
  setEffort: vi.fn(),
}));

vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({ isPlatformAdmin: H.isPlatformAdmin, isOrgAdmin: H.isOrgAdmin }),
}));
vi.mock('@/lib/api/agents', () => ({
  setAgentReasoningEffort: (...a: unknown[]) => H.setEffort(...a),
}));

import { AgentReasoningEffortSelect } from './AgentReasoningEffortSelect';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
  Element.prototype.scrollIntoView = vi.fn();
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  H.isPlatformAdmin = false;
  H.isOrgAdmin = false;
});

function renderSelect(over: Partial<React.ComponentProps<typeof AgentReasoningEffortSelect>> = {}) {
  const onUpdated = vi.fn();
  render(
    <MantineProvider>
      <AgentReasoningEffortSelect agentId="a-1" reasoningEffort={null} isBuiltIn onUpdated={onUpdated} {...over} />
    </MantineProvider>,
  );
  return { onUpdated };
}

describe('AgentReasoningEffortSelect', () => {
  it('lets a platform admin set a built-in agent, sending the vocabulary value', async () => {
    H.isPlatformAdmin = true;
    H.setEffort.mockResolvedValue({});
    const { onUpdated } = renderSelect();

    fireEvent.click(screen.getByRole('combobox', { name: 'Thinking effort' }));
    fireEvent.click(await screen.findByText('Extra high'));

    await waitFor(() => expect(H.setEffort).toHaveBeenCalledWith('a-1', 'xhigh'));
    await waitFor(() => expect(onUpdated).toHaveBeenCalled());
  });

  it('offers Default, Off, Low, Medium and Extra high, and no High', async () => {
    H.isPlatformAdmin = true;
    renderSelect();

    fireEvent.click(screen.getByRole('combobox', { name: 'Thinking effort' }));
    await screen.findByText('Extra high');

    for (const label of ['Default', 'Off', 'Low', 'Medium', 'Extra high']) {
      expect(screen.getAllByText(label).length).toBeGreaterThan(0);
    }
    expect(screen.queryByText('High')).toBeNull();
  });

  it('clears the stored value when Default is chosen', async () => {
    H.isPlatformAdmin = true;
    H.setEffort.mockResolvedValue({});
    renderSelect({ reasoningEffort: 'low' });

    fireEvent.click(screen.getByRole('combobox', { name: 'Thinking effort' }));
    fireEvent.click(await screen.findByText('Default'));

    await waitFor(() => expect(H.setEffort).toHaveBeenCalledWith('a-1', null));
  });

  it('shows the current value', () => {
    H.isPlatformAdmin = true;
    renderSelect({ reasoningEffort: 'none' });
    expect(screen.getByDisplayValue('Off')).toBeTruthy();
  });

  it('is read-only for a workspace Owner on a built-in agent: that row is the platform default', () => {
    H.isOrgAdmin = true;
    renderSelect({ reasoningEffort: 'low' });
    expect(screen.queryByRole('combobox')).toBeNull();
    expect(screen.getByText('Low')).toBeTruthy();
  });

  it('lets a workspace Owner or Admin set their own custom agent', async () => {
    H.isOrgAdmin = true;
    H.setEffort.mockResolvedValue({});
    renderSelect({ isBuiltIn: false });

    fireEvent.click(screen.getByRole('combobox', { name: 'Thinking effort' }));
    fireEvent.click(await screen.findByText('Medium'));

    await waitFor(() => expect(H.setEffort).toHaveBeenCalledWith('a-1', 'medium'));
  });

  it('is read-only for a plain member on a custom agent, even with platform authority', () => {
    H.isPlatformAdmin = true;
    renderSelect({ isBuiltIn: false });
    expect(screen.queryByRole('combobox')).toBeNull();
    expect(screen.getByText('Default')).toBeTruthy();
  });

  it('reports an API failure instead of claiming success', async () => {
    H.isPlatformAdmin = true;
    H.setEffort.mockRejectedValue(new Error('boom'));
    const { onUpdated } = renderSelect();

    fireEvent.click(screen.getByRole('combobox', { name: 'Thinking effort' }));
    fireEvent.click(await screen.findByText('Low'));

    await waitFor(() => expect(H.setEffort).toHaveBeenCalled());
    expect(onUpdated).not.toHaveBeenCalled();
  });
});
