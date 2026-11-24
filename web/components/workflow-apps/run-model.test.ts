import { describe, expect, it } from 'vitest';
import { createTranslator } from 'use-intl/core';
import { loadMessages } from '@/i18n/messages';
import { isMsg, type Msg } from '@/i18n/msg';
import { stableT, type BaseTranslator } from '@/i18n/translator';
import {
  acceptFor,
  failureMessage,
  insertFailureAdvice,
  insertGuideHref,
  runChargedNothing,
  runCreditsSummary,
  fileHasRole,
  fixActionFor,
  itemsFromFindings,
  newestFirst,
  paceValueOf,
  pageBrief,
  appRoleOf,
  priceLines,
  roundEstimate,
  roundUpEstimate,
  readinessStateOf,
  resolveText,
  roleNoun,
  wireRole,
} from './run-model';
import type { LintFinding, WorkflowExecution } from '@/lib/types/workflow';

const fmt = (n: number) => String(n);
const tEn = createTranslator({ locale: 'en', messages: loadMessages('en') }) as unknown as (k: string, v?: Record<string, unknown>) => string;
const en = (m: Msg | string | null): string | null => (m === null ? null : isMsg(m) ? tEn(m.$msg, m.values as Record<string, unknown>) : m);
const priceEn = (...a: Parameters<typeof priceLines>) => { const p = priceLines(...a); return { label: en(p.label), range: en(p.range) }; };

/** A full translator over the real catalogue, with `has` scoped to the `apps` namespace (as AppRunPage binds it). */
function translator(locale: 'en' | 'it') {
  const messages = loadMessages(locale);
  const root = createTranslator({ locale, messages }) as unknown as BaseTranslator;
  const scoped = createTranslator({ locale, messages, namespace: 'apps' }) as unknown as BaseTranslator;
  return stableT(scoped, root);
}
const lintEn = translator('en');
const lintIt = translator('it');

describe('fileHasRole', () => {
  it('counts only the own uploads of the project and treats GitHub files and the readiness code types as code', () => {
    expect(fileHasRole({ mimeType: 'video/mp4', originalFileName: 'a.mp4', category: 'outputFiles' }, 'video')).toBe(false);
    expect(fileHasRole({ mimeType: 'application/octet-stream', originalFileName: 'a.ts', sourceKind: 'github', category: 'userFiles' }, 'code')).toBe(true);
    expect(fileHasRole({ mimeType: 'application/octet-stream', originalFileName: 'a.ts', category: 'userFiles' }, 'code')).toBe(false);
    expect(fileHasRole({ mimeType: 'application/x-zip-compressed', originalFileName: 'a.zip', category: 'userFiles' }, 'code')).toBe(false);
  });

  it('works the role out from the file type, audio counting as both music and effects', () => {
    expect(fileHasRole({ mimeType: 'video/mp4', originalFileName: 'a.mp4' }, 'video')).toBe(true);
    expect(fileHasRole({ mimeType: 'audio/mpeg', originalFileName: 'a.mp3' }, 'music')).toBe(true);
    expect(fileHasRole({ mimeType: 'audio/mpeg', originalFileName: 'a.mp3' }, 'sfx')).toBe(true);
    expect(fileHasRole({ mimeType: 'application/zip', originalFileName: 'repo.zip' }, 'code')).toBe(false);
    expect(fileHasRole({ mimeType: 'video/mp4', originalFileName: 'a.mp4' }, 'code')).toBe(false);
    expect(fileHasRole({ mimeType: 'application/pdf', originalFileName: 'brief.pdf' }, 'document')).toBe(true);
  });
});

describe('the "what goes on the screen" role', () => {
  it('lists both pictures and videos, reads the camelCase wire name and sends the InsertContent role', () => {
    expect(fileHasRole({ mimeType: 'image/png', originalFileName: 'a.png' }, 'insertContent')).toBe(true);
    expect(fileHasRole({ mimeType: 'video/mp4', originalFileName: 'a.mp4' }, 'insertContent')).toBe(true);
    expect(fileHasRole({ mimeType: 'audio/mpeg', originalFileName: 'a.mp3' }, 'insertContent')).toBe(false);
    expect(appRoleOf('insertContent')).toBe('insertContent');
    expect(wireRole('insertContent')).toBe('InsertContent');
  });
});

describe('the picture-overlay role', () => {
  it('accepts both pictures and videos, maps the wire role and names the slot in words', () => {
    expect(fileHasRole({ mimeType: 'image/png', originalFileName: 'a.png' }, 'overlay')).toBe(true);
    expect(fileHasRole({ mimeType: 'video/mp4', originalFileName: 'a.mp4' }, 'overlay')).toBe(true);
    expect(fileHasRole({ mimeType: 'audio/mpeg', originalFileName: 'a.mp3' }, 'overlay')).toBe(false);
    expect(fileHasRole({ mimeType: 'image/png', originalFileName: 'a.png', category: 'outputFiles' }, 'overlay')).toBe(false);
    expect(acceptFor('overlay')).toBe('image/*,video/*');
    expect(appRoleOf('overlay')).toBe('overlay');
    expect(appRoleOf('Overlay')).toBe('overlay');
    expect(wireRole('overlay')).toBe('Overlay');
    expect(en(roleNoun('overlay'))).toBe('picture or video');
    expect(en(roleNoun('overlay', true))).toBe('pictures or videos');
  });

  it('writes the card-audio choice to the brief only when the person overrides the agent', () => {
    const base = { language: 'auto', lengthSec: null, narrated: null, voiceId: null, design: null, sourceFiles: [] };
    expect(pageBrief({ ...base, overlayCardAudio: 'agent' })).toEqual({});
    expect(pageBrief({ ...base, overlayCardAudio: 'keep' })).toEqual({ overlayCardAudio: 'keep' });
    expect(pageBrief({ ...base, overlayCardAudio: 'mute' })).toEqual({ overlayCardAudio: 'mute' });
  });

  it('sends the chosen avatar as brief.avatar, and nothing without one', () => {
    const base = { language: 'auto', lengthSec: null, narrated: null, voiceId: null, design: null, sourceFiles: [] };
    expect(pageBrief({ ...base, avatarId: 'a1' })).toEqual({ avatar: { avatarId: 'a1' } });
    expect(pageBrief({ ...base, avatarId: null })).toEqual({});
  });
});

describe('pace', () => {
  const base = { language: 'auto', lengthSec: null, narrated: null, voiceId: null, design: null, sourceFiles: [] };
  it('reads a pace word in any case and sends it only when set', () => {
    expect(paceValueOf('Fast')).toBe('fast');
    expect(paceValueOf('nope')).toBeNull();
    expect(paceValueOf(null)).toBeNull();
    expect(pageBrief(base)).toEqual({});
    expect(pageBrief({ ...base, pace: 'calm' })).toEqual({ pace: 'calm' });
  });
});

describe('readiness', () => {
  it('maps the linter severities to words and the worst item decides the state', () => {
    const items = itemsFromFindings(
      [
        { ruleId: 'WL016', severity: 'warning', message: 'silent' },
        { ruleId: 'WL010', severity: 'error', message: 'no footage', fix: { kind: 'uploadFiles', label: 'Add clips' } },
        { ruleId: 'WL019', severity: 'info', message: 'captions' },
      ],
      ['video'],
      lintEn,
    );
    expect(items.map((i) => i.level)).toEqual(['check', 'fix', 'note']);
    expect(items[1]?.fix).toEqual({ label: 'Add clips', action: 'upload', role: 'video' });
    expect(readinessStateOf(items)).toBe('fix');
    expect(readinessStateOf([])).toBe('ready');
    expect(readinessStateOf(items.filter((i) => i.level !== 'fix'))).toBe('check');
  });

  it('knows what each fix kind does on the page', () => {
    expect(fixActionFor({ kind: 'chooseVoice', label: 'x' })).toBe('voice');
    expect(fixActionFor({ kind: 'addStep', label: 'x' })).toBe('advanced');
    expect(fixActionFor({ kind: 'none', label: 'x' })).toBeNull();
    expect(fixActionFor(null)).toBeNull();
  });
});

describe('linter sentences', () => {
  // Everybody sees the linter's English sentences today; these are the same findings T-109 puts on the wire.
  const wire = (key: string, args: Record<string, string>): Pick<LintFinding, 'messageKey' | 'args'> => ({ messageKey: key, args });
  const findings: LintFinding[] = [
    {
      ruleId: 'WL001',
      severity: 'error',
      stepOrder: 2,
      message: 'server English',
      ...wire('WL001.wrongKind', {
        stepOrder: '2',
        stepLabel: 'Draft narration',
        sourceOrder: '1',
        sourceLabel: 'Treatment',
        problem: 'wrongKind',
        expectedKind: 'scriptwriter',
      }),
    },
    {
      ruleId: 'WL018',
      severity: 'error',
      message: 'server English',
      ...wire('WL018.unreadField', { input: 'Narrator', inputIndex: '0' }),
    },
    {
      ruleId: 'WL020',
      severity: 'warning',
      message: 'server English',
      ...wire('WL020', {
        reviewOrder: '7',
        laterCount: '2',
        later1Kind: 'narration',
        later1Order: '8',
        later2Kind: 'finalCut',
        later2Order: '10',
        lastOrder: '10',
      }),
    },
    {
      ruleId: 'WL010',
      severity: 'error',
      stepOrder: 3,
      message: 'server English',
      ...wire('WL010.missingMedia', { stepOrder: '3', role: 'video' }),
      fix: { kind: 'uploadFiles', label: 'server English', labelId: 'WL010.upload', args: { role: 'music' } },
    },
    {
      ruleId: 'WL005',
      severity: 'error',
      stepOrder: 1,
      message: 'server English',
      ...wire('WL005', { stepOrder: '1', featureCount: '2', feature1: 'music', feature2: 'captions' }),
    },
    {
      ruleId: 'WL022',
      severity: 'warning',
      stepOrder: 4,
      message: 'server English',
      ...wire('WL022', { label: 'plate' }),
    },
  ];

  const rendered = (t: typeof lintIt) => itemsFromFindings(findings, [], t).map((i) => resolveText(t, i.message));

  it('builds each representative finding in Italian from the catalogue, with no placeholder left', () => {
    expect(rendered(lintIt)).toEqual([
      'Il passaggio 2 (Draft narration) legge le battute dal passaggio 1, che non è un passaggio Scriptwriter (è il passaggio 1 (Treatment)). La voce narrante non riuscirebbe a partire.',
      'L\'input "Narrator" non è letto da nessun passaggio di questo flusso di lavoro, quindi verrebbe mostrato e ignorato. Rimuovilo o aggiungi un passaggio che lo usi.',
      'La revisione al passaggio 7 gira prima di questi passaggi: la voce narrante (passaggio 8) e il montaggio finale (passaggio 10), quindi controlla un video muto. Sposta la revisione dopo il passaggio 10.',
      'Il passaggio 3 ha bisogno di file di tipo video, ma in questo progetto non ce ne sono. L\'esecuzione sarebbe vuota o fallirebbe.',
      'Il passaggio 1 copia il girato senza ricodificarlo, ma non può anche aggiungere la musica, i sottotitoli. Il passaggio si fermerebbe.',
      'La censura non si applicherà a plate: censurare richiede una traccia di feature, e questa etichetta è tracciata solo con la segmentazione. Usa invece un effetto di regione su quella traccia.',
    ]);
    for (const text of rendered(lintIt)) expect(text).not.toMatch(/\{[a-zA-Z]+\}/);
  });

  it('renders the same findings in English from the catalogue', () => {
    expect(rendered(lintEn)).toEqual([
      'The step 2 (Draft narration) speaks lines from step 1, which is not a Scriptwriter step (it is step 1 (Treatment)). The voiceover would fail when it runs.',
      'The input "Narrator" is not read by any step of this workflow, so it would be shown and ignored. Remove it or add a step that uses it.',
      'The review at step 7 runs before the narration (step 8) and the final cut (step 10), so it checks a silent picture. Move the review after step 10.',
      'The step 3 needs video footage, but there are none in this project. The run would be empty or fail.',
      'The step 1 copies the footage without re-encoding, which cannot also add music, captions. The step would stop.',
      'The censor won\'t apply to plate: censoring needs a feature track, and this label is tracked with segmentation only. Use a region effect on that track instead.',
    ]);
  });

  it('carries the Italian contraction and subject article for WL003, WL004, WL011 and WL012', () => {
    const extra: LintFinding[] = [
      { ruleId: 'WL003', severity: 'warning', message: 'server English', ...wire('WL003.noImprovement', { stepOrder: '5', stepLabel: 'Review draft', targetOrder: '2' }) },
      {
        ruleId: 'WL004',
        severity: 'error',
        message: 'server English',
        ...wire('WL004.wrongKind', {
          stepOrder: '3',
          stepLabel: 'Final cut',
          input: 'analysis',
          targetOrder: '1',
          targetLabel: 'Look',
          problem: 'wrongKind',
          expectedKind: 'videoAnalysis',
        }),
      },
      { ruleId: 'WL011', severity: 'info', message: 'server English', ...wire('WL011', { field: 'language' }) },
      { ruleId: 'WL012', severity: 'error', message: 'server English', ...wire('WL012.step', { stepOrder: '4', stepLabel: 'Design' }) },
    ];
    expect(itemsFromFindings(extra, [], lintIt).map((i) => resolveText(lintIt, i.message))).toEqual([
      'La revisione al passaggio 5 (Review draft) ripassa sul passaggio 2 e sui passaggi successivi, ma nessuno di loro crea qualcosa di nuovo a ogni ripetizione. Il ciclo non migliorerebbe il risultato.',
      'Il passaggio 3 (Final cut) legge l\'analisi dal passaggio 1, che non è un passaggio di analisi video (è il passaggio 1 (Look)). Il montaggio verrebbe sbagliato o il passaggio si fermerebbe.',
      'Il campo Lingua che hai impostato non serve a nessun passaggio di questo flusso di lavoro, quindi non cambierà il risultato.',
      'Il passaggio 4 (Design) ha bisogno del sistema di design approvato del progetto, ma non ce n\'è ancora uno approvato.',
    ]);
  });

  it('translates the fix button and leaves the server label when the rule carries no labelId', () => {    const done = itemsFromFindings(findings, [], lintIt);
    expect(resolveText(lintIt, done[3]!.fix!.label)).toBe('Carica i file di tipo musica nel progetto');
    expect(done[5]!.fix).toBeUndefined();
  });

  it('falls back to the server sentence and label when the key is unknown or absent', () => {
    const items = itemsFromFindings(
      [
        { ruleId: 'WL999', severity: 'error', message: 'Server says no.', ...wire('WL999', {}) },
        { ruleId: 'WL016', severity: 'warning', message: 'The film would be silent.' },
        {
          ruleId: 'WL010',
          severity: 'error',
          message: 'No footage.',
          ...wire('WL010.missingMedia', { stepOrder: '3', role: 'video' }),
          fix: { kind: 'uploadFiles', label: 'Server label', labelId: 'WL999.fix', args: {} },
        },
      ],
      [],
      lintIt,
    );
    expect(resolveText(lintIt, items[0]!.message)).toBe('Server says no.');
    expect(resolveText(lintIt, items[1]!.message)).toBe('The film would be silent.');
    expect(resolveText(lintIt, items[2]!.fix!.label)).toBe('Server label');
  });
});

describe('pageBrief', () => {
  it('sends only what the person decided', () => {
    expect(pageBrief({ language: 'auto', lengthSec: null, narrated: null, voiceId: null, design: null, sourceFiles: [] })).toEqual({});
    expect(
      pageBrief({
        language: 'it',
        lengthSec: 45,
        narrated: true,
        voiceId: 'v1',
        design: 'none',
        sourceFiles: [{ role: 'video', fileIds: ['a'] }, { role: 'music', fileIds: [] }],
      }),
    ).toEqual({
      language: 'it',
      lengthSec: 45,
      voice: { mode: 'Narrated', projectVoiceId: 'v1' },
      designSystem: 'none',
      sourceFiles: [{ role: 'Video', fileIds: ['a'] }],
    });
    expect(pageBrief({ language: 'auto', lengthSec: null, narrated: false, voiceId: 'v1', design: null, sourceFiles: [] }).voice).toEqual({ mode: 'None' });
    expect(wireRole('sfx')).toBe('Sfx');
  });
});

describe('priceLines', () => {
  const base = { billingEnabled: true, lowCredits: 30, highCredits: 55, typicalCredits: 40 };
  it('puts the number on the label and the range under it', () => {
    expect(priceEn(base, fmt, 'Uses credits')).toEqual({ label: 'Run, about 40 credits', range: 'usually 30 to 60' });
  });
  it('rounds to 10 and gives one range, never a figure to the cent', () => {
    expect(priceEn({ billingEnabled: true, typicalCredits: 571.59, lowCredits: 472.56, highCredits: 742.15 }, fmt, 'Uses credits'))
      .toEqual({ label: 'Run, about 570 credits', range: 'usually 470 to 750' });
    expect(priceEn({ billingEnabled: true, typicalCredits: 1, lowCredits: 1, highCredits: 1 }, fmt, 'Uses credits')).toEqual({ label: 'Run, about 1 credit', range: null });
    expect(roundEstimate(9.5)).toBe(9.5);
    expect(roundEstimate(14)).toBe(10);
    expect(roundUpEstimate(471.2)).toBe(480);
  });
  it('names the AI shot ceiling and says "No credits used" for a free app', () => {
    expect(priceEn({ ...base, fixedMaxCredits: 20 }, fmt, 'Uses credits and AI shots').range).toBe('usually 30 to 80');
    expect(priceEn({ ...base, typicalCredits: 0, lowCredits: 0, highCredits: 0 }, fmt, 'No credits')).toEqual({ label: 'Run', range: 'No credits used' });
  });
  it('shows no price at all when nothing is metered', () => {
    expect(priceEn({ ...base, billingEnabled: false }, fmt, 'Uses credits')).toEqual({ label: 'Run', range: null });
  });
});

describe('insert failures', () => {
  it('has a plain sentence for each insert code', () => {
    expect(en(failureMessage('INSERT_NOT_APPLIED'))).toContain('large, steady');
    expect(en(failureMessage('INSERT_CONTENT_UNUSABLE'))).toContain('PNG, JPG or MP4');
    expect(en(failureMessage('INSERT_CONTENT_MISSING'))).toContain('Choose a picture or video');
    expect(en(failureMessage('INSERT_FILTER_UNAVAILABLE'))).toContain('our side');
  });
  it('says what to change; our own fault offers no other clip', () => {
    expect(insertFailureAdvice('INSERT_NOT_APPLIED')).toEqual({ chooseClip: true, slot: 'video' });
    expect(insertFailureAdvice('insert_content_unusable')).toEqual({ chooseClip: true, slot: 'insertContent' });
    expect(insertFailureAdvice('INSERT_CONTENT_MISSING')).toEqual({ chooseClip: true, slot: 'insertContent' });
    expect(insertFailureAdvice('INSERT_FILTER_UNAVAILABLE')).toEqual({ chooseClip: false, slot: null });
    expect(insertFailureAdvice('INSERT_TRACKING_FAILED')).toEqual({ chooseClip: false, slot: null });
    expect(insertFailureAdvice('VOICE_NOT_FOUND')).toBeNull();
    expect(insertFailureAdvice(null)).toBeNull();
  });
  it('splits recorded charges by finished and failed steps; null with no figure', () => {
    const run = (rs: Array<{ status: string; creditsCharged?: number | null }>) => ({ stepResults: rs }) as never;
    expect(runCreditsSummary(run([{ status: 'Completed', creditsCharged: 0.326 }, { status: 'Failed', creditsCharged: 0 }]))).toEqual({ finished: 0.326, finishedSteps: 1, failed: 0, failedAllZero: true });
    expect(runCreditsSummary(run([{ status: 'Completed', creditsCharged: 1 }, { status: 'Failed', creditsCharged: 2 }]))).toEqual({ finished: 1, finishedSteps: 1, failed: 2, failedAllZero: false });
    expect(runCreditsSummary(run([{ status: 'Failed' }]))).toBeNull();
  });
  it('links the guide in the active language', () => {
    expect(insertGuideHref('en')).toBe('/docs/recipes/tracked-screen-insert#which-clip-works');
    expect(insertGuideHref('it')).toBe('/docs/it/recipes/tracked-screen-insert#which-clip-works');
  });
  it('claims no charge only when every step recorded zero', () => {
    const run = (rs: Array<{ creditsCharged?: number | null }>) => ({ stepResults: rs }) as never;
    expect(runChargedNothing(run([{ creditsCharged: 0 }, { creditsCharged: 0 }]))).toBe(true);
    expect(runChargedNothing(run([{ creditsCharged: 0 }, { creditsCharged: 1.5 }]))).toBe(false);
    expect(runChargedNothing(run([{ creditsCharged: 0 }, { creditsCharged: null }]))).toBe(false);
    expect(runChargedNothing(run([]))).toBe(false);
  });
});

describe('failures and runs', () => {
  it('turns a failure code into a plain sentence, never the code', () => {
    expect(en(failureMessage('VOICE_NOT_FOUND'))).toBe('The chosen voice could not be found.');
    expect(en(failureMessage('SOURCE_UNRESOLVED'))).toContain('could not be found');
    expect(en(failureMessage('RUN_RATE_LIMITED'))).toContain('Try again');
    expect(en(failureMessage('SOMETHING_NEW', null))).toBe('Something went wrong while making your video.');
    expect(en(failureMessage(null, 'The footage had no audio track.'))).toBe('The footage had no audio track.');
    const stored = 'La frase della scena 3 è troppo lunga. '.repeat(8).trim();
    expect(stored.length).toBeGreaterThan(250);
    expect(en(failureMessage('NARRATION_DOES_NOT_FIT', stored))).toBe(stored);
    expect(en(failureMessage('VOICE_NOT_FOUND', stored))).toBe('The chosen voice could not be found.');
  });
  it('orders runs newest first', () => {
    const a = { id: 'a', startedAt: '2026-10-25T09:00:00Z' } as WorkflowExecution;
    const b = { id: 'b', startedAt: '2026-10-25T10:00:00Z' } as WorkflowExecution;
    expect(newestFirst([a, b]).map((e) => e.id)).toEqual(['b', 'a']);
  });
});

describe('needsConfirm', () => {
  const base = { billingEnabled: true, highCredits: 80 };
  it('asks only for a costly run with billing on', async () => {
    const { needsConfirm } = await import('./run-model');
    expect(needsConfirm(base, null)).toBe(true);
    expect(needsConfirm({ ...base, highCredits: 10 }, null)).toBe(false);
    expect(needsConfirm({ ...base, highCredits: 10, fixedMaxCredits: 60 }, null)).toBe(true);
    expect(needsConfirm(base, 'No credits')).toBe(false);
    expect(needsConfirm({ ...base, billingEnabled: false }, null)).toBe(false);
    expect(needsConfirm(undefined, null)).toBe(false);
  });
});

describe('confirmCopy and acceptTotal', () => {
  it('shows one accepted total and rounds up to 2 decimals', async () => {
    const { confirmCopy, acceptTotal } = await import('./run-model');
    const e = { typicalCredits: 40, lowCredits: 30, highCredits: 45.123, fixedMaxCredits: 10 };
    expect(acceptTotal(e)).toBe(55.13);
    expect(acceptTotal({ highCredits: 80 })).toBe(80);
    const text = en(confirmCopy(e, (n) => String(n))) ?? '';
    expect(text).toContain('about 40 credits');
    expect(text).toContain('Usually 30 to 45.123');
    expect(text).toContain('Most it can use: 55.13 credits, including AI shots');
    expect(en(confirmCopy({ lowCredits: 5, highCredits: 5 }, String))).not.toContain('Usually');
  });
});
