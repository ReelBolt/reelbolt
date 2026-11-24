// @vitest-environment jsdom
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { render, screen, cleanup, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { FieldDiffProvider, ProviderSelect, StepOrderSelect, AskAssistantButton, assistantPrompt, useFd } from './config-kit';
import type { StepData } from './WorkflowStepList';

const auth = vi.hoisted(() => ({ isAdmin: true, orgRole: 'Owner' as string | null }));
const providers = vi.hoisted(() => ({ data: [] as unknown[] | undefined, error: undefined as unknown }));
// A17-UI: ProviderSelect reads the member-facing `selectable` list, so it needs `useSelectableProviders`
// — not the admin-only hook — and the role from `useAuth` (every role but Billing may choose).
vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({
    isAdmin: auth.isAdmin,
    isPlatformAdmin: auth.isAdmin,
    isOrgAdmin: auth.orgRole === 'Owner' || auth.orgRole === 'Admin',
    user: { orgRole: auth.orgRole },
  }),
}));
vi.mock('@/lib/hooks/use-inference-providers', () => ({
  useSelectableProviders: () => ({ data: providers.data, isLoading: false, error: providers.error }),
}));

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', { writable: true, value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }) });
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
  Element.prototype.scrollIntoView = vi.fn();
});
afterEach(cleanup);

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);
/** One row of the member-facing `selectable` list: no isDefault, no endpoint, no health. */
const row = (over: Record<string, unknown>) => ({
  id: 'x', name: 'x', capability: 'Decision', model: 'm', isPlatformManaged: true,
  kind: 'TypeSafe', providerName: 'TypeSafe', ...over,
});

describe('ProviderSelect', () => {
  it('lists only models of the requested capability by display name and provider, under the field label Model', () => {
    auth.orgRole = 'Member';
    providers.data = [
      row({ id: 'd1', name: 'Jev', capability: 'Decision', providerName: 'TypeSafe' }),
      row({ id: 'c1', name: 'Chat One', capability: 'Chat', providerName: 'Azure' }),
    ];
    wrap(<ProviderSelect capability="Decision" value={null} onChange={vi.fn()} />);
    expect(screen.getByText('Model', { selector: 'label' })).toBeTruthy();
    fireEvent.click(screen.getByPlaceholderText('Use the default Decision model'));
    expect(screen.queryByText('Chat One · Azure')).toBeNull();
    expect(screen.getByText('Jev · TypeSafe')).toBeTruthy();
    expect(screen.getByText('ReelBolt')).toBeTruthy();
  });

  it('groups the workspace\u2019s own models apart from the platform-managed ones', () => {
    auth.orgRole = 'Owner';
    providers.data = [
      row({ id: 'p1', name: 'Jev', providerName: 'TypeSafe' }),
      row({ id: 'o1', name: 'Llama 3', providerName: 'Acme vLLM', isPlatformManaged: false }),
    ];
    wrap(<ProviderSelect capability="Decision" value={null} onChange={vi.fn()} />);
    fireEvent.click(screen.getByPlaceholderText('Use the default Decision model'));
    expect(screen.getByText('Llama 3 · Acme vLLM')).toBeTruthy();
    expect(screen.getByText('This workspace')).toBeTruthy();
  });

  it('stores the selected model capability id, unchanged', () => {
    auth.orgRole = 'Owner';
    providers.data = [row({ id: 'cap-42', name: 'Jev', providerName: 'TypeSafe' })];
    const onChange = vi.fn();
    wrap(<ProviderSelect capability="Decision" value={null} onChange={onChange} />);
    fireEvent.click(screen.getByPlaceholderText('Use the default Decision model'));
    fireEvent.click(screen.getByText('Jev · TypeSafe'));
    expect(onChange).toHaveBeenCalledWith('cap-42');
  });

  it('offers the workspace page to an org admin when nothing of that capability exists', () => {
    auth.isAdmin = false;
    auth.orgRole = 'Owner';
    providers.data = [row({ capability: 'Chat' })];
    wrap(<ProviderSelect capability="Transcription" value={null} onChange={vi.fn()} />);
    expect(screen.getByText(/No Transcription model configured/, { selector: 'p, div, span' })).toBeTruthy();
    expect(screen.getByRole('link', { name: /Organization/ }).getAttribute('href')).toContain('/org/inference-providers');
  });

  it('offers the platform page to a platform admin when nothing of that capability exists', () => {
    auth.isAdmin = true;
    auth.orgRole = 'Owner';
    providers.data = [row({ capability: 'Chat' })];
    wrap(<ProviderSelect capability="Transcription" value={null} onChange={vi.fn()} />);
    expect(screen.getByRole('link', { name: /Platform/ }).getAttribute('href')).toContain('/admin/inference-providers');
  });

  it('tells a plain member to ask an admin rather than offering a page they cannot open', () => {
    auth.isAdmin = false;
    auth.orgRole = 'Member';
    providers.data = [];
    wrap(<ProviderSelect capability="Transcription" value={null} onChange={vi.fn()} />);
    expect(screen.getByText(/Ask an Owner or Admin/)).toBeTruthy();
    expect(screen.queryByRole('link')).toBeNull();
  });

  it('keeps a stored id that no longer resolves visible', () => {
    auth.orgRole = 'Owner';
    providers.data = [row({ id: 'd1', name: 'Jev' })];
    wrap(<ProviderSelect capability="Decision" value="deadbeef-0000" onChange={vi.fn()} />);
    expect(screen.getByDisplayValue('Unknown model')).toBeTruthy();
  });

  it('is disabled for the Billing role, which the API refuses', () => {
    auth.isAdmin = false;
    auth.orgRole = 'Billing';
    providers.data = undefined;
    wrap(<ProviderSelect capability="Decision" value={null} onChange={vi.fn()} />);
    const field = screen.getByPlaceholderText(/not available to your role/) as HTMLInputElement;
    expect(field.disabled).toBe(true);
    expect(screen.getByText(/Your role cannot choose a model/)).toBeTruthy();
  });

  it('stays enabled for a plain Member of a workspace', () => {
    auth.isAdmin = false;
    auth.orgRole = 'Member';
    providers.data = [row({ id: 'd1', name: 'Jev' })];
    wrap(<ProviderSelect capability="Decision" value={null} onChange={vi.fn()} />);
    const field = screen.getByPlaceholderText('Use the default Decision model') as HTMLInputElement;
    expect(field.disabled).toBe(false);
  });

  it('is disabled when the API refuses the caller with 403 instead of trusting the role', () => {
    auth.isAdmin = false;
    auth.orgRole = 'Member';
    providers.data = undefined;
    providers.error = new ApiError('Forbidden', 403);
    wrap(<ProviderSelect capability="Decision" value={null} onChange={vi.fn()} />);
    expect(screen.getByPlaceholderText(/not available to your role/)).toBeTruthy();
    providers.error = undefined;
  });
});

describe('StepOrderSelect', () => {
  const steps = ['A', 'B', 'C'].map((label, i) => ({ id: `s${i}`, label, stepType: 'Agent' }) as StepData);

  it('offers only steps in scope and reports a numeric order', () => {
    const onChange = vi.fn();
    wrap(<StepOrderSelect steps={steps} currentIndex={1} scope="before" value={null} onChange={onChange} label="Target" />);
    fireEvent.click(screen.getAllByLabelText('Target')[0]);
    expect(screen.queryByText('3. C')).toBeNull();
    fireEvent.click(screen.getByText('1. A'));
    expect(onChange).toHaveBeenCalledWith(1);
  });
});

describe('field diff + assistant button', () => {
  function Probe() {
    const fd = useFd();
    return <div data-testid="p" {...(fd('config.mode').wrapperProps ?? {})} />;
  }

  it('marks only the fields the proposal changes', () => {
    wrap(
      <FieldDiffProvider diff={new Map([['config.mode', { before: 'Expression', after: 'Decision' }]])}>
        <Probe />
      </FieldDiffProvider>,
    );
    const el = screen.getByTestId('p');
    expect(el.getAttribute('data-diff')).toBe('changed');
    expect(el.getAttribute('data-diff-note')).toContain('Expression');
    expect(el.getAttribute('data-diff-note')).toContain('Decision');
  });

  it('is a no-op outside a diff', () => {
    wrap(<Probe />);
    expect(screen.getByTestId('p').getAttribute('data-diff')).toBeNull();
  });

  it('renders nothing when no assistant shell is mounted', () => {
    const { container } = wrap(<AskAssistantButton prompt={assistantPrompt(0, 'Agent')} />);
    expect(container.querySelector('button')).toBeNull();
  });
});
