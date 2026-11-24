import { describe, expect, it } from 'vitest';
import { createTranslator } from 'use-intl/core';
import { isMsg, type Msg } from '@/i18n/msg';
import { loadMessages } from '@/i18n/messages';
import { stableT, type BaseTranslator, type T } from '@/i18n/translator';
import { findingMessage, fixLabel } from '@/lib/lint-messages';
import type { LintFinding } from '@/lib/types/workflow';

/** A full translator over the real catalogue, with `has` scoped to the `apps` namespace (as AppRunPage binds it). */
function translator(locale: 'en' | 'it'): T {
  const messages = loadMessages(locale);
  const root = createTranslator({ locale, messages }) as unknown as BaseTranslator;
  const scoped = createTranslator({ locale, messages, namespace: 'apps' }) as unknown as BaseTranslator;
  return stableT(scoped, root);
}

function read(t: T, value: string | Msg): string {
  return isMsg(value) ? t.msg(value) : value;
}

const SERVER_MESSAGE =
  'The narration at step 2 is typed inline even though step 1 writes the lines, so that script is never spoken. Point the voiceover at the script.';
const SERVER_FIX_LABEL = 'Point the voiceover at the script';

/** The WL026 finding as T-006 puts it on the wire. */
function voiceoverFinding(): LintFinding {
  return {
    ruleId: 'WL026',
    severity: 'warning',
    stepOrder: 2,
    messageKey: 'WL026',
    args: { stepOrder: '2', scriptOrder: '1', scriptKind: 'scriptwriter' },
    message: SERVER_MESSAGE,
    fix: { kind: 'changeSetting', label: SERVER_FIX_LABEL, labelId: 'WL026.useScript' },
  };
}

describe('WL026 voiceover-ignores-script', () => {
  it('builds the sentence and the fix label from the wire finding in English', () => {
    const t = translator('en');
    const finding = voiceoverFinding();
    expect(read(t, findingMessage(finding, t))).toBe(SERVER_MESSAGE);
    expect(read(t, fixLabel(finding.fix!, t))).toBe('Point the voiceover at the script');
  });

  it('builds the same sentence and fix label in Italian instead of falling back to English', () => {
    const t = translator('it');
    const finding = voiceoverFinding();
    expect(read(t, findingMessage(finding, t))).toBe(
      'La narrazione al passaggio 2 è scritta direttamente, anche se il passaggio 1 scrive le battute, quindi quello script non viene mai letto. Collega la voce allo script.',
    );
    expect(read(t, fixLabel(finding.fix!, t))).toBe('Collega la voce allo script');
  });

  it('falls back to the server sentence for a key this catalogue version does not know', () => {
    const t = translator('it');
    const finding: LintFinding = {
      ruleId: 'WL999',
      severity: 'warning',
      messageKey: 'WL999',
      message: 'A rule this web build has not heard of yet.',
    };
    expect(read(t, findingMessage(finding, t))).toBe('A rule this web build has not heard of yet.');
  });
});
describe('app-input rules (WL028-WL033)', () => {
  const branch: LintFinding = {
    ruleId: 'WL031',
    severity: 'info',
    stepOrder: 1,
    messageKey: 'WL031',
    args: { input: 'Lite mode', inputId: 'liteMode', value: 'on', count: '3', skippedCount: '3', skipped1: '4', skipped2: '5', skipped3: '6' },
    message: 'When Lite mode is on, 3 step(s) do not run.',
  };

  it('renders the branch note with the switch word and a plural in both languages', () => {
    expect(read(translator('en'), findingMessage(branch, translator('en')))).toBe('When “Lite mode” is on, 3 steps do not run.');
    expect(read(translator('it'), findingMessage(branch, translator('it')))).toBe('Quando “Lite mode” è attivo, 3 passaggi non vengono eseguiti.');
  });

  it('renders the missing-input sentence and its fix with the author label', () => {
    const t = translator('it');
    const missing: LintFinding = {
      ruleId: 'WL028',
      severity: 'error',
      messageKey: 'WL028',
      args: { input: 'Cosa succede', inputId: 'videoPrompt' },
      message: 'The app field Cosa succede is required and has no value. The run would be refused.',
      fix: { kind: 'fillInput', label: 'Fill in “Cosa succede”', labelId: 'WL028.fillInput', args: { input: 'Cosa succede' } },
    };
    expect(read(t, findingMessage(missing, t))).toBe("Il campo dell'app “Cosa succede” è obbligatorio e non ha un valore. L'esecuzione verrebbe rifiutata.");
    expect(read(t, fixLabel(missing.fix!, t))).toBe('Compila “Cosa succede”');
  });
});

describe('the model of a step that shows an avatar (WL034)', () => {
  const finding = (messageKey: string, args: Record<string, string>): LintFinding => ({
    ruleId: 'WL034',
    severity: 'error',
    stepOrder: 2,
    messageKey,
    args,
    message: "The step 2 shows an avatar or an object, but its model 'fal-ai/veo3.1' takes no pictures.",
    fix: { kind: 'chooseModel', label: 'Choose a model that takes pictures', labelId: 'WL034.chooseModel' },
  });

  it('names the sibling model in both languages, and the fix', () => {
    const useSibling = finding('WL034.useSibling', { stepOrder: '2', stepLabel: 'Clip', model: 'fal-ai/veo3.1', sibling: 'fal-ai/veo3.1/reference-to-video' });
    expect(read(translator('en'), findingMessage(useSibling, translator('en')))).toBe(
      'The step 2 (Clip) shows an avatar or an object, but its model “fal-ai/veo3.1” takes no pictures. Use “fal-ai/veo3.1/reference-to-video” instead. The step would be refused before anything is charged.');
    expect(read(translator('it'), findingMessage(useSibling, translator('it')))).toBe(
      'Il passaggio 2 (Clip) mostra un avatar o un oggetto, ma il suo modello “fal-ai/veo3.1” non accetta immagini. Usa invece “fal-ai/veo3.1/reference-to-video”. Il passaggio verrebbe rifiutato prima di addebitare qualcosa.');
    expect(read(translator('en'), fixLabel(useSibling.fix!, translator('en')))).toBe('Choose a model that takes pictures');
    expect(read(translator('it'), fixLabel(useSibling.fix!, translator('it')))).toBe('Scegli un modello che accetta immagini');
  });

  it('asks for any model that takes pictures when there is no sibling', () => {
    const noSibling = finding('WL034.noSibling', { stepOrder: '2', model: 'text-only' });
    expect(read(translator('en'), findingMessage(noSibling, translator('en')))).toBe(
      'The step 2 shows an avatar or an object, but its model “text-only” takes no pictures. Choose a model that takes pictures. The step would be refused before anything is charged.');
  });
});

describe('WL006 model not supported by its provider', () => {
  const finding: LintFinding = {
    ruleId: 'WL006',
    severity: 'error',
    stepOrder: 2,
    messageKey: 'WL006.modelUnsupported',
    args: { stepOrder: '2', model: 'kling-9' },
    message: 'The server sentence in English.',
    fix: { kind: 'chooseProvider', label: 'Connect or choose a provider', labelId: 'WL006.chooseProvider' },
  };

  it('is built from the wire finding in English and Italian, never the server sentence', () => {
    const en = read(translator('en'), findingMessage(finding, translator('en')));
    const it = read(translator('it'), findingMessage(finding, translator('it')));
    expect(en).toContain('kling-9');
    expect(en).not.toBe(finding.message);
    expect(it).toContain('kling-9');
    expect(it).toContain('modello video');
  });
});

describe('WL006 no lip-sync model set up', () => {
  const finding: LintFinding = {
    ruleId: 'WL006',
    severity: 'error',
    stepOrder: 2,
    messageKey: 'WL006.lipSyncModelNotSet',
    args: { stepOrder: '2' },
    message: 'The server sentence in English.',
    fix: { kind: 'chooseProvider', label: 'Connect or choose a provider', labelId: 'WL006.chooseProvider' },
  };

  it('is built from the wire finding in English and Italian, never the server sentence', () => {
    const en = read(translator('en'), findingMessage(finding, translator('en')));
    const it = read(translator('it'), findingMessage(finding, translator('it')));
    expect(en).toContain('lip-sync');
    expect(en).not.toBe(finding.message);
    expect(it).toContain('lip-sync');
    expect(it).toContain('avatar');
  });
});
