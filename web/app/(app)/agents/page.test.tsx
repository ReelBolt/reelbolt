// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { AgentDefinition } from '@/lib/types/agent';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.mock('next/navigation', () => ({ useRouter: () => ({ push: vi.fn() }) }));
vi.mock('@/components/agents/AgentForm', () => ({ AgentForm: () => null }));

const agent = (over: Partial<AgentDefinition>): AgentDefinition => ({
  id: 'a1', name: 'CodeStructureAnalyzer', description: 'Produces a schema-validated CodeStructureOutput.', agentType: 'CodeStructureAnalyzer',
  systemPrompt: '', isBuiltIn: true, ownerId: null, createdAt: '', color: null, outputSchemaJson: null,
  availableTools: ['ReadProjectFile', 'ListProjectFiles'], generatesOutput: true, outputSchemaName: 'CodeStructureOutput',
  inferenceProviderId: null, inferenceProviderName: null, assignedSkills: null, effectiveSkills: [], ...over,
});
const agentsResult = {
  data: [
    agent({}),
    agent({ id: 'a2', name: 'My hype writer', description: 'Writes hype lines.', agentType: 'Custom', isBuiltIn: false, availableTools: [], generatesOutput: false, outputSchemaName: null }),
  ],
  isLoading: false,
  mutate: vi.fn(),
};
vi.mock('@/lib/hooks/use-agents', () => ({ useAgents: () => agentsResult }));

import AgentsPage from './page';

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

describe('Agents page', () => {
  it('shows customer-facing names and one-line descriptions, with internals behind Details', () => {
    render(<MantineProvider><AgentsPage /></MantineProvider>);
    expect(screen.getByText('Understanding your app')).toBeTruthy();
    expect(screen.getByText('Reads how your app is built')).toBeTruthy();
    expect(screen.getByText("Learns your app's screens and how they fit together.")).toBeTruthy();
    // No enum badges, schema names or tool counts up front.
    const body = document.body.textContent ?? '';
    expect(body).not.toContain('CodeStructureAnalyzer');
    expect(body).not.toContain('CodeStructureOutput');
    expect(body).not.toMatch(/\d+ tools?/);
    expect(body).not.toContain('schema-validated');
    // A custom agent keeps the name its owner chose.
    expect(screen.getByText('My hype writer')).toBeTruthy();
    expect(screen.getByText('Writes hype lines.')).toBeTruthy();

    const card = screen.getByText('Reads how your app is built').closest('.mantine-Card-root') as HTMLElement;
    fireEvent.click(within(card).getByRole('button', { name: 'Details' }));
    const details = within(card).getByTestId('agent-details');
    expect(details.textContent).toContain('CodeStructureAnalyzer');
    expect(details.textContent).toContain('CodeStructureOutput');
    expect(details.textContent).toContain('2 tools');
  });
});
