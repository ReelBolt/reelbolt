// @vitest-environment jsdom
/**
 * The shared upgrade callout: what a plan refusal does not include, the lowest plan that does (read from the billing
 * summary or the refusal, never typed in the UI), and the way to it: a button for someone who can manage billing, a
 * sentence for everyone else. Nothing at all while the summary is unread or billing is off.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const env = vi.hoisted(() => ({ summary: { data: undefined as unknown, error: undefined as unknown } }));
vi.mock('@/lib/hooks/use-billing', () => ({ useBillingSummary: () => env.summary }));
vi.mock('next/link', () => ({
  default: ({ children, href, ...rest }: { children: React.ReactNode; href: string }) => <a href={href} {...rest}>{children}</a>,
}));

import { UpgradeCallout } from './UpgradeCallout';

const summary = (canManageBilling: boolean, unlocks: { feature: string; planKey: string; planName: string }[] = []) =>
  ({ canManageBilling, entitlements: { maxOutputHeight: 720, unlocks } });
const mount = (props: React.ComponentProps<typeof UpgradeCallout>) =>
  render(
    <MantineProvider>
      <UpgradeCallout {...props} />
    </MantineProvider>,
  );

beforeEach(() => {
  window.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null,
    addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
  env.summary = { data: summary(true, [{ feature: 'video_generation', planKey: 'pro', planName: 'Pro' }]), error: undefined };
});
afterEach(cleanup);

describe('UpgradeCallout', () => {
  it('an owner is told what is blocked, the plan from the catalogue, and gets a button to the plans', () => {
    mount({ feature: 'video_generation' });
    const callout = screen.getByTestId('upgrade-callout');
    expect(callout.textContent).toContain('Not included in your plan');
    expect(callout.textContent).toContain('Available on Pro.');
    const button = screen.getByRole('link', { name: 'Upgrade to Pro' });
    expect(button.getAttribute('href')).toBe('/settings/billing?plan=pro#plans');
  });

  it('a member who cannot manage billing is told to ask the owner and gets no button that would be refused', () => {
    env.summary = { data: summary(false, [{ feature: 'video_generation', planKey: 'pro', planName: 'Pro' }]), error: undefined };
    mount({ feature: 'video_generation' });
    expect(screen.getByTestId('upgrade-callout').textContent).toContain('Ask the workspace owner to upgrade.');
    expect(screen.queryByRole('link')).toBeNull();
    expect(screen.queryByRole('button')).toBeNull();
  });

  it('the plan the refusal itself named wins over the summary', () => {
    mount({ feature: 'video_generation', plan: { key: 'creator', name: 'Creator' } });
    expect(screen.getByTestId('upgrade-callout').textContent).toContain('Available on Creator.');
    expect(screen.getByRole('link', { name: 'Upgrade to Creator' }).getAttribute('href')).toBe('/settings/billing?plan=creator#plans');
  });

  it('with no plan known it still says to upgrade and offers the plans page, naming no plan', () => {
    env.summary = { data: summary(true), error: undefined };
    mount({ feature: 'rooms' });
    const callout = screen.getByTestId('upgrade-callout');
    expect(callout.textContent).toContain('Using multi-agent editing rooms');
    expect(callout.textContent).toContain('Upgrade your plan to unlock it.');
    expect(screen.getByRole('link', { name: 'See plans' }).getAttribute('href')).toBe('/settings/billing#plans');
  });

  it('an unknown feature reads as "This feature"', () => {
    mount({ feature: 'something_new' });
    expect(screen.getByTestId('upgrade-callout').textContent).toContain('This feature');
  });

  it('compact is one line: where it is available and the way to it, no title', () => {
    mount({ feature: 'video_generation', compact: true });
    const callout = screen.getByTestId('upgrade-callout');
    expect(callout.textContent).not.toContain('Not included');
    expect(callout.textContent).toContain('Available on Pro.');
  });

  it('shows no price and no number of its own', () => {
    mount({ feature: 'resolution' });
    expect(screen.getByTestId('upgrade-callout').textContent).not.toMatch(/[$€£]|\d/);
  });

  it('renders nothing while the summary is unread, or when billing is off (the summary does not exist)', () => {
    env.summary = { data: undefined, error: undefined };
    mount({ feature: 'video_generation' });
    expect(screen.queryByTestId('upgrade-callout')).toBeNull();
    cleanup();
    env.summary = { data: undefined, error: new Error('404') };
    mount({ feature: 'video_generation' });
    expect(screen.queryByTestId('upgrade-callout')).toBeNull();
  });
});
