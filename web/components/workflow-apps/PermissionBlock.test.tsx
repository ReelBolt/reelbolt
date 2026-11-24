import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createTranslator } from 'use-intl/core';
import { loadMessages } from '@/i18n/messages';
import { stableT, type BaseTranslator, type T } from '@/i18n/translator';
import type { AppManifestInput, AppMediaEgress } from '@/lib/types/workflow';
import { PermissionBlock, permissionSentence } from './PermissionBlock';
import { runInputWords } from './run-app-inputs';

function translator(locale: 'en' | 'it', namespace?: string): T {
  const messages = loadMessages(locale);
  const root = createTranslator({ locale, messages }) as unknown as BaseTranslator;
  const scoped = (namespace ? createTranslator({ locale, messages, namespace }) : root) as unknown as BaseTranslator;
  return stableT(scoped, root);
}

const input = { kind: 'input', id: 'allowMediaEgress', type: 'toggle', label: 'Allow sending your media to AI services', required: true, permission: 'mediaEgress', help: 'Needed by: Clip (shows an avatar)' } as AppManifestInput & { id: string };
const egress: AppMediaEgress = {
  inputId: 'allowMediaEgress',
  required: true,
  alreadyAllowed: false,
  steps: [
    { stepOrder: 1, stepName: 'Analyze video', stepType: 'VideoAnalyze', reason: 'tracksAnObjectByName', need: 'required', allowedInStep: false },
    { stepOrder: 2, stepName: 'Generate Video', stepType: 'VideoGenerate', reason: 'showsAnAvatar', need: 'optional', allowedInStep: false },
    { stepOrder: 3, stepName: 'Clean audio', stepType: 'AudioCleanup', reason: 'cleansAudioWithAi', need: 'required', allowedInStep: true },
  ],
};

beforeAll(() => {
  window.matchMedia = vi.fn().mockImplementation((query: string) => ({
    matches: false, media: query, onchange: null, addListener: vi.fn(), removeListener: vi.fn(),
    addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
  })) as unknown as typeof window.matchMedia;
});
afterEach(cleanup);

describe('permissionSentence', () => {
  const en = translator('en', 'apps.permission.mediaEgress');
  const it_ = translator('it', 'apps.permission.mediaEgress');

  it('names the steps whose own switch is off, with why, in the reader\'s language', () => {
    expect(permissionSentence(en, egress)).toBe('Needed by: Analyze video (finds the object), Generate Video (shows an avatar, when you choose one).');
    expect(permissionSentence(it_, egress)).toBe("Serve a: Analyze video (trova l'oggetto), Generate Video (mostra un avatar, quando ne scegli uno).");
    expect(permissionSentence(en, { ...egress, required: false })).toMatch(/^Used by: /);
  });

  it('says so when every step already allows it, and falls back to the server sentence with no step list', () => {
    const all = { ...egress, required: false, alreadyAllowed: true, steps: egress.steps.map((s) => ({ ...s, allowedInStep: true })) };
    expect(permissionSentence(en, all)).toContain('Already allowed in the step settings for: Analyze video');
    expect(permissionSentence(en, null, 'Needed by: Clip (shows an avatar)')).toBe('Needed by: Clip (shows an avatar)');
    expect(permissionSentence(en, null, null)).toBeNull();
  });
});

describe('PermissionBlock', () => {
  const mount = (props: Partial<Parameters<typeof PermissionBlock>[0]> = {}) => {
    const onChange = vi.fn();
    render(
      <MantineProvider>
        <PermissionBlock input={input} egress={egress} checked={false} onChange={onChange} {...props} />
      </MantineProvider>,
    );
    return onChange;
  };

  it('is a labelled switch with its reason and its error tied to it', () => {
    const onChange = mount({ error: 'Allow sending your media to AI services to run this app.' });
    const toggle = screen.getByRole('switch', { name: /Allow sending your media to AI services/ });
    expect(toggle.getAttribute('aria-invalid')).toBe('true');
    const described = toggle.getAttribute('aria-describedby')!.split(' ').map((id) => document.getElementById(id)?.textContent ?? '');
    expect(described.join(' ')).toContain('Needed by: Analyze video');
    expect(described.join(' ')).toContain('to run this app');
    expect(screen.getByRole('alert').textContent).toBe('Allow sending your media to AI services to run this app.');
    fireEvent.click(toggle);
    expect(onChange).toHaveBeenCalledWith(true);
  });

  it('with no step list (the Run dialog of an older caller) still shows the server sentence', () => {
    mount({ egress: null });
    expect(screen.getByTestId('app-permission-block').textContent).toContain('Needed by: Clip (shows an avatar)');
  });
});

describe('run history', () => {
  it('shows who allowed sending media, in words', () => {
    const row = { id: 'allowMediaEgress', label: 'Allow sending your media to AI services', labelKey: 'permission.mediaEgress.label', type: 'toggle' as const, value: true, valueText: 'Allowed in the app' };
    expect(runInputWords(translator('en'), { ...row, allowedBy: 'app' }).value).toBe('Allowed in the app');
    expect(runInputWords(translator('it'), { ...row, allowedBy: 'mcp' })).toEqual({
      id: 'allowMediaEgress', label: "Consenti l'invio dei tuoi file ai servizi AI", value: 'Consentito da uno strumento collegato',
    });
    expect(runInputWords(translator('en'), { ...row, allowedBy: 'assistant_card' }).value).toBe('Approved in the assistant');
    expect(runInputWords(translator('en'), { ...row, allowedBy: 'api_key', valueText: 'Allowed by API key Nightly builds' }).value).toBe('Allowed by API key Nightly builds');
  });
});
