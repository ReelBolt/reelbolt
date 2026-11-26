// @vitest-environment jsdom

import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { PERSONAS, type Persona } from '@/lib/attribution/persona';
import { VALUE_COPY, valueAudience, type ValueAudience } from '@/lib/auth/signup-value';
import { loadFlat } from '@/i18n/catalog-utils';
import { ensureMatchMedia, renderWithI18n } from '@/test/i18n';
import { SignupValuePanel } from './SignupValuePanel';

const renderPanel = (persona?: Persona, firstVideoOffered?: boolean) =>
  render(
    <MantineProvider>
      <SignupValuePanel persona={persona} firstVideoOffered={firstVideoOffered} />
    </MantineProvider>,
  );

/** What a visitor reads: the panel's text without the inline <style> Mantine's grid renders. */
const panelText = () => {
  const copy = screen.getByTestId('signup-value-panel').cloneNode(true) as HTMLElement;
  copy.querySelectorAll('style').forEach((node) => node.remove());
  return copy.textContent ?? '';
};

const AUDIENCES = Object.keys(VALUE_COPY) as ValueAudience[];

describe('SignupValuePanel', () => {
  beforeEach(ensureMatchMedia);
  afterEach(cleanup);

  it('has copy for the general story and for each of the six audiences with a landing page', () => {
    expect(AUDIENCES.sort()).toEqual(['agency', 'business', 'creator', 'educator', 'general', 'nonprofit', 'software']);
    for (const persona of PERSONAS) {
      expect(valueAudience(persona)).toBe(persona === 'other' ? 'general' : persona);
    }
    expect(valueAudience(undefined)).toBe('general');
  });

  it('shows a different outcome, before and after for every audience', () => {
    const seen = new Set<string>();
    for (const audience of AUDIENCES) {
      renderPanel(audience === 'general' ? undefined : audience);
      const outcome = within(screen.getByTestId('signup-value-panel')).getByRole('heading', { level: 2 }).textContent!;
      const before = screen.getByTestId('signup-value-before').textContent!;
      const after = screen.getByTestId('signup-value-after').textContent!;
      expect(outcome.length).toBeGreaterThan(10);
      expect(before).toMatch(/^Before/);
      expect(after).toMatch(/^After/);
      for (const line of [outcome, before, after]) {
        expect(seen.has(line), `${audience}: "${line}" repeats another audience`).toBe(false);
        seen.add(line);
      }
      cleanup();
    }
  });

  it('labels the panel with its own heading for assistive technology', () => {
    renderPanel('creator');
    const aside = screen.getByRole('complementary');
    expect(aside).toBe(screen.getByTestId('signup-value-panel'));
    expect(aside.getAttribute('aria-labelledby')).toBe(within(aside).getByRole('heading', { level: 2 }).id);
  });

  it('states the two trust facts that hold on every deployment, and no first-video promise by default', () => {
    renderPanel();
    const facts = within(screen.getByTestId('signup-value-trust')).getAllByRole('listitem').map((li) => li.textContent);
    expect(facts).toEqual([
      'No card required. Starting is free.',
      'Every edited cut is scored before you see it. A low score sends it back for another pass.',
    ]);
  });

  it('adds the first-video line only when the grant is offered', () => {
    renderPanel(undefined, true);
    const facts = within(screen.getByTestId('signup-value-trust')).getAllByRole('listitem').map((li) => li.textContent);
    expect(facts).toEqual([
      'No card required. Starting is free.',
      'Your first video is on us: the credits for it are added when you verify your email. A few connections and temporary addresses are not eligible.',
      'Every edited cut is scored before you see it. A low score sends it back for another pass.',
    ]);
  });

  /**
   * The honesty rules of the messaging brief, applied to every audience in both languages. Nothing
   * is known about the visitor or the plan before sign-in, so the panel may only say what is true
   * for everyone: no number (a time saving would be invented, and the first-video credit grant
   * depends on billing settings and eligibility this page cannot see), no customer, no quote.
   */
  for (const locale of ['en', 'it']) {
    it(`makes no numeric, testimonial or plan-specific claim for any audience (${locale})`, () => {
      for (const audience of AUDIENCES) {
        renderWithI18n(<SignupValuePanel persona={audience === 'general' ? undefined : audience} firstVideoOffered />, { locale });
        const text = panelText();
        expect(text, `${locale}/${audience}`).not.toMatch(/\d/);
        expect(text, `${locale}/${audience}`).not.toMatch(/["“”«»]/);
        expect(text, `${locale}/${audience}`).not.toMatch(/watermark|filigrana|%/i);
        expect(text, `${locale}/${audience}`).not.toMatch(
          /customers?|clienti|users|utenti|creators trust|trusted by|rated|stars|stelle|reviews|recensioni|testimonial/i,
        );
        expect(text, `${locale}/${audience}`).not.toMatch(
          /faster|più veloce|hours? saved|ore risparmiate|revolutionary|seamless|unlock|effortless|supercharge|!/i,
        );
        cleanup();
      }
    });
  }

  it('renders in Italian with the same structure', () => {
    renderWithI18n(<SignupValuePanel persona="business" />, { locale: 'it' });
    expect(within(screen.getByTestId('signup-value-panel')).getByRole('heading', { level: 2 }).textContent).toBe(
      "Un video per il negozio, il lancio o l'offerta",
    );
    expect(screen.getByTestId('signup-value-before').textContent).toMatch(/^Prima/);
    expect(screen.getByTestId('signup-value-after').textContent).toMatch(/^Dopo/);
    expect(within(screen.getByTestId('signup-value-next')).getAllByRole('listitem')).toHaveLength(3);
  });

  it('every copy key exists in both catalogues', () => {
    for (const locale of ['en', 'it']) {
      const flat = loadFlat(locale);
      for (const keys of Object.values(VALUE_COPY)) {
        for (const key of Object.values(keys)) {
          expect(typeof flat[key], `${locale} ${key}`).toBe('string');
        }
      }
    }
  });

  /**
   * The panel and the signup page render on the server, where a Mantine component is a client
   * reference: `List.Item` (any `Parent.Child`) reads a property off that reference, gets
   * undefined, and the page answers 500. jsdom cannot see it (there the import is the real
   * component), so the source is checked: named exports only (`ListItem`), and no 'use client'
   * that would hide the problem by moving the panel to the client.
   */
  it('uses no compound Mantine member in the server-rendered files', () => {
    const web = join(__dirname, '..', '..');
    for (const file of ['components/auth/SignupValuePanel.tsx', 'app/(auth)/signup/page.tsx']) {
      const source = readFileSync(join(web, file), 'utf8');
      expect(source, file).not.toMatch(/^\s*['"]use client['"]/m);
      expect(source.match(/<\/?[A-Z]\w*\.[A-Z]\w*/g) ?? [], file).toEqual([]);
    }
  });
});
