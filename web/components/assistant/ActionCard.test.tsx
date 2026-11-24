// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import type { AssistantAction } from '@/lib/types/assistant';
import { ActionCard, type ActionCardProps } from './ActionCard';

const GUID = '12345678-1234-1234-1234-123456789012';

const action = (over: Partial<AssistantAction> = {}): AssistantAction => ({
  id: 'act-1',
  state: 'pending',
  mode: 'ask',
  kind: 'file',
  risk: 'normal',
  title: 'Rename the project “Launch Teaser”',
  summary: 'The project keeps its files and runs.',
  changes: [{ kind: 'change', text: 'Launch Teaser becomes Spring Reel' }],
  projectName: 'Launch Teaser',
  ...over,
});

function mount(props: Partial<ActionCardProps> & { action?: AssistantAction } = {}) {
  const respond = props.respond ?? vi.fn().mockResolvedValue(undefined);
  const ui = (next: Partial<ActionCardProps> & { action?: AssistantAction }) => (
    <MantineProvider env="test">
      <ActionCard action={action()} respond={respond} {...next} />
    </MantineProvider>
  );
  const view = render(ui(props));
  return { ...view, respond: respond as ReturnType<typeof vi.fn>, rerenderWith: (next: typeof props) => view.rerender(ui({ ...props, ...next })) };
}

const card = () => screen.getByTestId('assistant-action');
const button = (name: string | RegExp) => screen.getByRole('button', { name }) as HTMLButtonElement;
const user = () => userEvent.setup();

beforeEach(() => {
  global.matchMedia = ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => true,
  })) as unknown as typeof window.matchMedia;
  global.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe('a pending request', () => {
  it('says why it was shown in auto mode: the cost passed the limit', () => {
    mount({ action: action({ risk: 'paid', cost: 'needs up to 166 credits', autoNote: { limitCredits: 25, costCredits: 166 } }) });
    expect(screen.getByTestId('auto-note').textContent).toBe(
      'Shown because this costs about 166 credits (your auto-approve limit is 25).',
    );
  });

  it('says so when the cost could not be estimated', () => {
    mount({ action: action({ autoNote: { limitCredits: 25 } }) });
    expect(screen.getByTestId('auto-note').textContent).toBe(
      'Shown because the cost of this could not be estimated (your auto-approve limit is 25).',
    );
  });

  it('shows no such note on an ordinary card', () => {
    mount();
    expect(screen.queryByTestId('auto-note')).toBeNull();
  });

  it('asks the question in plain words: what, where, and what will change', () => {
    mount({ action: action({ kind: 'workflow', changes: [
      { kind: 'add', text: 'A review step' },
      { kind: 'remove', text: 'The old title step' },
      { kind: 'change', text: 'Launch Teaser becomes Spring Reel' },
      { kind: 'info', text: 'Runs already started are not touched' },
    ] }) });

    expect(screen.getByRole('heading', { name: 'Rename the project “Launch Teaser”' })).toBeTruthy();
    expect(screen.getByText('In project “Launch Teaser”')).toBeTruthy();
    expect(screen.getByText('The project keeps its files and runs.')).toBeTruthy();
    // Each change names its kind in WORDS, not only in colour.
    const list = screen.getByRole('list', { name: 'What will change' });
    const rows = within(list).getAllByRole('listitem').map((li) => li.textContent);
    expect(rows).toEqual([
      'AddA review step',
      'RemoveThe old title step',
      'ChangeLaunch Teaser becomes Spring Reel',
      'NoteRuns already started are not touched',
    ]);
  });

  it('is one named group, so a screen reader hears what is being asked', () => {
    mount();

    const group = screen.getByRole('group', { name: 'Rename the project “Launch Teaser”' });
    expect(group).toBe(card());
    expect(group.getAttribute('data-state')).toBe('pending');
  });

  it('offers Allow, Don\'t allow and Always allow, in that order, all reachable by keyboard', async () => {
    mount();

    const names = screen.getAllByRole('button').map((b) => b.textContent);
    expect(names).toEqual(['Allow', "Don't allow", 'Always allow']);

    await user().tab();
    expect(document.activeElement).toBe(button('Allow'));
    await user().tab();
    expect(document.activeElement).toBe(button("Don't allow"));
    await user().tab();
    expect(document.activeElement).toBe(button('Always allow'));
  });

  it('does not take focus when it appears', () => {
    mount();

    expect(document.activeElement).toBe(document.body);
  });

  it('announces itself politely, once, without moving anyone', async () => {
    mount();

    const region = card().querySelector('[aria-live="polite"]') as HTMLElement;
    expect(region).not.toBeNull();
    // Mounts empty — a live region only announces text that changes after it is in the page.
    expect(region.textContent).toBe('');
    await waitFor(() => expect(region.textContent).toBe('Permission needed: Rename the project “Launch Teaser”'));
    expect(document.activeElement).toBe(document.body);
  });

  it('shows what a paid run costs, and says it uses credits on the button', () => {
    mount({ action: action({ kind: 'run', risk: 'paid', cost: 'about 12 credits.', title: 'Run “Launch Teaser”' }) });

    expect(screen.getByText('This will use about 12 credits.')).toBeTruthy();
    expect(button('Allow and use credits')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Allow' })).toBeNull();
    expect(screen.queryByText(/can.t be undone/i)).toBeNull();
  });

  it('shows the server’s cost sentence as it is when it says what a run needs to start', () => {
    mount({
      action: action({
        kind: 'run',
        risk: 'paid',
        cost: 'usually 8–12 credits (needs 15 available to start; can cost more)',
        title: 'Run “Launch Teaser”',
      }),
    });

    expect(screen.getByText('Usually 8–12 credits (needs 15 available to start; can cost more).')).toBeTruthy();
    expect(screen.queryByText(/This will use/)).toBeNull();
  });

  it('keeps the generated-video part of the cost sentence', () => {
    mount({
      action: action({
        kind: 'run',
        risk: 'paid',
        cost: 'usually 8–12 credits (needs 15 available to start; can cost more), plus up to 30 for generated video.',
      }),
    });

    expect(
      screen.getByText('Usually 8–12 credits (needs 15 available to start; can cost more), plus up to 30 for generated video.'),
    ).toBeTruthy();
  });

  it('still reads a picture tool’s cost as "This will use …"', () => {
    mount({
      action: action({
        risk: 'paid',
        cost: 'about 5 credits per picture; larger pictures or several results can cost more',
      }),
    });

    expect(
      screen.getByText('This will use about 5 credits per picture; larger pictures or several results can cost more.'),
    ).toBeTruthy();
  });

  // QA (T-005): every branch of the server's cost sentence reads as one plain, capitalised sentence.
  it.each([
    [
      'up to 160 credits for generated video (needs 161 available to start)',
      'Up to 160 credits for generated video (needs 161 available to start).',
    ],
    ['needs 1 credit available to start; can cost more', 'Needs 1 credit available to start; can cost more.'],
    ['usually 12 credits (needs 13 available to start; can cost more)', 'Usually 12 credits (needs 13 available to start; can cost more).'],
    ['up to 160 credits for generated video', 'This will use up to 160 credits for generated video.'],
    ['about 8–12 credits', 'This will use about 8–12 credits.'],
    [
      'the credits this run uses could not be worked out in advance',
      'The credits this run uses could not be worked out in advance.',
    ],
    [
      'the credits this run uses could not be worked out in advance.',
      'The credits this run uses could not be worked out in advance.',
    ],
    ['the credits this uses could not be worked out in advance', 'The credits this uses could not be worked out in advance.'],
  ])('words the cost "%s" as "%s"', (cost, expected) => {
    mount({ action: action({ kind: 'run', risk: 'paid', cost }) });

    expect(screen.getByText(expected)).toBeTruthy();
  });

  it('still says credits are used when a paid request names no amount', () => {
    mount({ action: action({ risk: 'paid' }) });

    expect(screen.getByText('This will use credits.')).toBeTruthy();
  });

  it('says plainly that a destructive request cannot be undone, and styles Allow as dangerous', () => {
    mount({ action: action({ risk: 'destructive', title: 'Delete the file “notes.txt”' }) });

    expect(screen.getByText("This can't be undone.")).toBeTruthy();
    expect(card().getAttribute('data-risk')).toBe('destructive');
    // A red primary button — the style carries the colour, the line above carries the words.
    expect(button('Allow').getAttribute('style')).toMatch(/red/);
  });

  it('keeps an ordinary request calm: no cost line, no warning, the default button colour', () => {
    mount();

    expect(screen.queryByText(/This will use/)).toBeNull();
    expect(screen.queryByText(/can.t be undone/i)).toBeNull();
    expect(button('Allow').getAttribute('style') ?? '').not.toMatch(/red/);
  });

  it('never shows an id, even one that came with the request to be refreshed', () => {
    mount({ action: action({ refresh: { projectId: GUID, workflowId: GUID, documentId: GUID } }) });

    expect(card().textContent).not.toContain(GUID);
    expect(card().innerHTML).not.toContain(GUID);
  });
});

describe('answering', () => {
  it('Allow sends allow, shows work in flight on that button, and disables everything', async () => {
    let finish: () => void = () => {};
    const respond = vi.fn(() => new Promise<void>((resolve) => { finish = resolve; }));
    mount({ respond });

    await user().click(button('Allow'));

    expect(respond).toHaveBeenCalledWith('act-1', 'allow', undefined);
    await waitFor(() => expect(button('Allow').hasAttribute('data-loading')).toBe(true));
    expect(button('Allow').disabled).toBe(true);
    expect(button("Don't allow").disabled).toBe(true);
    expect(button('Always allow').disabled).toBe(true);

    await act(async () => finish());
    // Accepted, but the server has not said it ran yet: the card does not pretend, and does not re-open.
    expect(button('Allow').disabled).toBe(true);
    expect(button("Don't allow").disabled).toBe(true);
  });

  it("Don't allow sends deny", async () => {
    const { respond } = mount();

    await user().click(button("Don't allow"));

    expect(respond).toHaveBeenCalledWith('act-1', 'deny', undefined);
    await waitFor(() => expect(button("Don't allow").hasAttribute('data-loading')).toBe(true));
  });

  it('answers once however many times it is pressed in the same tick', async () => {
    const respond = vi.fn(() => new Promise<void>(() => {}));
    mount({ respond });

    const allow = button('Allow');
    act(() => {
      allow.click();
      allow.click();
      button("Don't allow").click();
    });

    expect(respond).toHaveBeenCalledTimes(1);
  });

  it.each([404, 409])('a %i says the request is gone, in place, and leaves nothing to press', async (status) => {
    const respond = vi.fn().mockRejectedValue(new ApiError('gone', status));
    mount({ respond });

    await user().click(button('Allow'));

    const problem = await screen.findByRole('alert');
    expect(problem.textContent).toBe('This request was already answered or has run out of time.');
    expect(button('Allow').disabled).toBe(true);
    expect(button("Don't allow").disabled).toBe(true);
    expect(respond).toHaveBeenCalledTimes(1);
  });

  it('any other failure says so calmly and lets the person try again', async () => {
    const respond = vi
      .fn()
      .mockRejectedValueOnce(new Error('network down'))
      .mockResolvedValueOnce(undefined);
    mount({ respond });

    await user().click(button('Allow'));

    const problem = await screen.findByRole('alert');
    expect(problem.textContent).toBe("Your answer didn't go through. Please try again.");
    expect(problem.textContent).not.toMatch(/network down/);
    await waitFor(() => expect(button('Allow').disabled).toBe(false));

    await user().click(button('Allow'));
    expect(respond).toHaveBeenCalledTimes(2);
    await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
  });

  it('moves focus to the card when its buttons go away after the person used them', async () => {
    const { rerenderWith } = mount();

    await user().click(button('Allow'));
    await waitFor(() => expect(button('Allow').hasAttribute('data-loading')).toBe(true));
    act(() => rerenderWith({ action: action({ state: 'allowed' }) }));

    expect(document.activeElement).toBe(card());
  });

  it('does not move focus for a card the person never touched', () => {
    const { rerenderWith } = mount();

    act(() => rerenderWith({ action: action({ state: 'done', outcome: 'Renamed.' }) }));

    expect(document.activeElement).toBe(document.body);
  });
});

describe('Always allow', () => {
  it('asks first, in plain words, and answers nothing until the person agrees', async () => {
    const { respond } = mount();

    await user().click(button('Always allow'));

    const dialog = await screen.findByRole('dialog', { name: 'Let the assistant act without asking?' });
    expect(dialog.textContent).toMatch(/deleting things — without asking you first. It still asks when an action is expected to cost more than the cost limit/);
    expect(dialog.textContent).toMatch(/switch back at any time/);
    expect(respond).not.toHaveBeenCalled();
  });

  it('"Keep asking" closes the dialog and sends nothing', async () => {
    const { respond } = mount();

    await user().click(button('Always allow'));
    await user().click(await screen.findByRole('button', { name: 'Keep asking' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(respond).not.toHaveBeenCalled();
    expect(button('Allow').disabled).toBe(false);
  });

  it('"Turn on auto mode" allows this request and switches to auto mode in the same answer', async () => {
    const onAlwaysAllowed = vi.fn();
    const { respond } = mount({ onAlwaysAllowed });

    await user().click(button('Always allow'));
    await user().click(await screen.findByRole('button', { name: 'Turn on auto mode' }));

    expect(respond).toHaveBeenCalledWith('act-1', 'allow', { alwaysAllow: true });
    await waitFor(() => expect(onAlwaysAllowed).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('does not claim auto mode is on when the answer did not go through', async () => {
    const onAlwaysAllowed = vi.fn();
    const respond = vi.fn().mockRejectedValue(new Error('offline'));
    mount({ respond, onAlwaysAllowed });

    await user().click(button('Always allow'));
    await user().click(await screen.findByRole('button', { name: 'Turn on auto mode' }));

    await screen.findByRole('alert');
    expect(onAlwaysAllowed).not.toHaveBeenCalled();
  });
});

describe('a request that has been dealt with', () => {
  it('while it works: "Working on it…" with a spinner, and nothing to press', () => {
    mount({ action: action({ state: 'allowed' }) });

    expect(card().textContent).toContain('Working on it…');
    expect(card().querySelector('.mantine-Loader-root')).not.toBeNull();
    expect(screen.queryByRole('button', { name: 'Allow' })).toBeNull();
    expect(card().getAttribute('data-state')).toBe('allowed');
  });

  it('when done: the outcome, with a check', () => {
    mount({ action: action({ state: 'done', outcome: 'Renamed the project to “Spring Reel”.' }) });

    expect(card().textContent).toContain('Renamed the project to “Spring Reel”.');
    expect(card().querySelector('.tabler-icon-check')).not.toBeNull();
    expect(card().textContent).not.toContain('Done automatically');
  });

  it('when done with a short outcome: says what it was done to, so "Saved." is never left dangling', () => {
    mount({ action: action({ state: 'done', outcome: 'Saved.' }) });

    expect(card().textContent).toContain('Saved.');
    expect(card().textContent).toContain('Rename the project “Launch Teaser”');
  });

  it('when done with no outcome: the title, marked done', () => {
    mount({ action: action({ state: 'done' }) });

    expect(card().textContent).toContain('Done: Rename the project “Launch Teaser”');
  });

  it('when done on its own in auto mode: says so quietly', () => {
    mount({ action: action({ state: 'done', mode: 'auto', outcome: 'Renamed.' }) });

    expect(screen.getByText('Done automatically')).toBeTruthy();
  });

  it('when declined: nothing was changed, and which request that was', () => {
    mount({ action: action({ state: 'declined' }) });

    expect(card().textContent).toContain('You chose not to allow this. Nothing was changed.');
    expect(card().textContent).toContain('Rename the project “Launch Teaser”');
  });

  it('when it ran out of time: nothing was changed', () => {
    mount({ action: action({ state: 'expired' }) });

    expect(card().textContent).toContain('No answer was given, so nothing was changed.');
  });

  it('when it failed: the reason, calmly, with no technical wording', () => {
    mount({ action: action({ state: 'failed', outcome: 'That file is being used by a run, so it was kept.' }) });

    expect(card().textContent).toContain('That file is being used by a run, so it was kept.');
    expect(card().textContent).not.toMatch(/exception|stack|error code|500/i);
  });

  it('when it failed with no reason: still calm, and says what to do', () => {
    mount({ action: action({ state: 'failed', summary: undefined, changes: [], projectName: undefined }) });

    expect(card().textContent).toMatch(/didn't work/i);
    expect(card().textContent).toMatch(/ask the assistant to try again/i);
  });

  it('collapses to one line that expands to what was asked, and back', async () => {
    mount({
      action: action({
        state: 'done',
        outcome: 'Removed the old step.',
        changes: [{ kind: 'remove', text: 'The old title step' }],
      }),
    });

    const toggle = screen.getByRole('button', { name: /Removed the old step\./ });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');

    await user().click(toggle);

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    const list = await screen.findByRole('list', { name: 'What was asked' });
    expect(within(list).getByText('The old title step')).toBeTruthy();
    expect(within(list).getByText('Remove')).toBeTruthy();

    await user().click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
  });

  it('has nothing to expand when it asked for nothing in particular', () => {
    mount({ action: action({ state: 'done', outcome: 'Done.', summary: undefined, changes: [], projectName: undefined }) });

    expect(screen.queryByRole('button')).toBeNull();
  });
});

describe('a request nobody can answer any more', () => {
  it('says the connection dropped, never that it timed out, with nothing to press', () => {
    mount({ action: action(), abandoned: true });

    expect(card().getAttribute('data-state')).toBe('expired');
    expect(card().textContent).toContain('The connection dropped before this was answered');
    expect(card().textContent).not.toMatch(/timed out|ran out of time|no answer was given/i);
    expect(screen.queryByRole('button', { name: 'Allow' })).toBeNull();
  });

  it('says the person stopped it, when they pressed Stop', () => {
    mount({ action: action(), abandoned: true, stopped: true });

    expect(card().textContent).toContain('You stopped this, so nothing was changed.');
    expect(card().textContent).not.toContain('connection dropped');
  });

  it('does not spin for ever on a request that was let through and then lost its connection', () => {
    mount({ action: action({ state: 'allowed' }), abandoned: true });

    expect(card().textContent).not.toContain('Working on it…');
    expect(card().textContent).toMatch(/stopped before it reported back/i);
    expect(card().querySelector('.mantine-Loader-root')).toBeNull();
  });

  it('leaves a settled request exactly as it was', () => {
    mount({ action: action({ state: 'done', outcome: 'Renamed.' }), abandoned: true });

    expect(card().textContent).toContain('Renamed.');
  });
});
