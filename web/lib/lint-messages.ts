import { msg, type Msg, type MsgValue } from '@/i18n/msg';
import type { T } from '@/i18n/translator';
import type { LintFinding, LintFix } from '@/lib/types/workflow';

/**
 * The pre-run linter's sentences, built from the tokens T-109 puts on the wire (`messageKey`, `args`,
 * `fix.labelId`, `fix.args`). A finding never carries prose for a known rule: the sentence, its step
 * names and every closed word set come from `apps.lint.*` in the catalogue.
 *
 * Non-component code returns a `Msg`; the component resolves it with `useT().msg` (ReadinessStrip does
 * this through `resolveText`). Unknown keys fall back to the server's English `message`/`label`, so a
 * rule added server-side later still reads instead of disappearing.
 */

/** The part of a translator the builders need: `has` (scoped to the `apps` namespace). */
export type LintTranslator = Pick<T, 'has'>;

/** Catalogue paths (relative to the `apps.lint` namespace) as full literal keys, so the i18n tests see them used. */
const KEYS: Record<string, string> = {
  'step.named': 'apps.lint.step.named',
  'step.plain': 'apps.lint.step.plain',
  'step.none': 'apps.lint.step.none',
  'list.comma': 'apps.lint.list.comma',
  'list.and': 'apps.lint.list.and',
  'ref.previousFrom': 'apps.lint.ref.previousFrom',
  'ref.actor.named': 'apps.lint.ref.actor.named',
  'ref.actor.plain': 'apps.lint.ref.actor.plain',
  'ref.subject.named': 'apps.lint.ref.subject.named',
  'ref.subject.plain': 'apps.lint.ref.subject.plain',
  'ref.at.named': 'apps.lint.ref.at.named',
  'ref.at.plain': 'apps.lint.ref.at.plain',
  'ref.from.plain': 'apps.lint.ref.from.plain',
  'ref.from.none': 'apps.lint.ref.from.none',
  'ref.to.plain': 'apps.lint.ref.to.plain',
  'ref.to.none': 'apps.lint.ref.to.none',
  'ref.over.named': 'apps.lint.ref.over.named',
  'ref.over.plain': 'apps.lint.ref.over.plain',
  'ref.definite.named': 'apps.lint.ref.definite.named',
  'ref.definite.plain': 'apps.lint.ref.definite.plain',

  'problem.isNotSet': 'apps.lint.problem.isNotSet',
  'problem.notInWorkflow': 'apps.lint.problem.notInWorkflow',
  'problem.comesAfter': 'apps.lint.problem.comesAfter',
  'problem.wrongKind': 'apps.lint.problem.wrongKind',
  'problem.firstStep': 'apps.lint.problem.firstStep',
  'problem.previousWrongKind': 'apps.lint.problem.previousWrongKind',
  'problem.doesNotNameTheStep': 'apps.lint.problem.doesNotNameTheStep',
  'problem.noDefault': 'apps.lint.problem.noDefault',
  'problem.providerUnavailable': 'apps.lint.problem.providerUnavailable',

  'expected.scriptwriter': 'apps.lint.expected.scriptwriter',
  'expected.narrationWriter': 'apps.lint.expected.narrationWriter',
  'expected.pickupPlanner': 'apps.lint.expected.pickupPlanner',
  'expected.voiceover': 'apps.lint.expected.voiceover',
  'expected.videoGeneration': 'apps.lint.expected.videoGeneration',
  'expected.videoAnalysis': 'apps.lint.expected.videoAnalysis',
  'expected.editDecision': 'apps.lint.expected.editDecision',
  'expected.takeSelection': 'apps.lint.expected.takeSelection',
  'expected.transcriptTranslation': 'apps.lint.expected.transcriptTranslation',
  'expected.planWriter': 'apps.lint.expected.planWriter',
  'expected.overlayPlanner': 'apps.lint.expected.overlayPlanner',
  'expected.anyStep': 'apps.lint.expected.anyStep',

  'input.editDecision': 'apps.lint.input.editDecision',
  'input.analysis': 'apps.lint.input.analysis',
  'input.captionTranslation': 'apps.lint.input.captionTranslation',
  'input.takeSelection': 'apps.lint.input.takeSelection',
  'input.graphicsPlan': 'apps.lint.input.graphicsPlan',
  'input.musicPlan': 'apps.lint.input.musicPlan',
  'input.colorGradePlan': 'apps.lint.input.colorGradePlan',
  'input.sfxPlan': 'apps.lint.input.sfxPlan',
  'input.generatedClips': 'apps.lint.input.generatedClips',

  'role.video': 'apps.lint.role.video',
  'role.code': 'apps.lint.role.code',
  'role.music': 'apps.lint.role.music',
  'role.sfx': 'apps.lint.role.sfx',
  'role.image': 'apps.lint.role.image',
  'role.document': 'apps.lint.role.document',
  'role.insertContent': 'apps.lint.role.insertContent',
  'role.overlay': 'apps.lint.role.overlay',

  'fileRole.video': 'apps.lint.fileRole.video',
  'fileRole.code': 'apps.lint.fileRole.code',
  'fileRole.music': 'apps.lint.fileRole.music',
  'fileRole.sfx': 'apps.lint.fileRole.sfx',
  'fileRole.image': 'apps.lint.fileRole.image',
  'fileRole.document': 'apps.lint.fileRole.document',
  'fileRole.insertContent': 'apps.lint.fileRole.insertContent',
  'fileRole.overlay': 'apps.lint.fileRole.overlay',

  'capability.speechSynthesis': 'apps.lint.capability.speechSynthesis',
  'capability.videoGeneration': 'apps.lint.capability.videoGeneration',
  'capability.musicGeneration': 'apps.lint.capability.musicGeneration',
  'capability.soundEffectGeneration': 'apps.lint.capability.soundEffectGeneration',
  'capability.transcription': 'apps.lint.capability.transcription',
  'capability.vision': 'apps.lint.capability.vision',
  'capability.audioIsolation': 'apps.lint.capability.audioIsolation',
  'capability.videoUpscale': 'apps.lint.capability.videoUpscale',
  'capability.frameInterpolation': 'apps.lint.capability.frameInterpolation',

  'feature.music': 'apps.lint.feature.music',
  'feature.narration': 'apps.lint.feature.narration',
  'feature.reRecordedLines': 'apps.lint.feature.reRecordedLines',
  'feature.onScreenGraphics': 'apps.lint.feature.onScreenGraphics',
  'feature.soundEffects': 'apps.lint.feature.soundEffects',
  'feature.colorGrade': 'apps.lint.feature.colorGrade',
  'feature.insertedScreens': 'apps.lint.feature.insertedScreens',
  'feature.generatedClips': 'apps.lint.feature.generatedClips',
  'feature.captions': 'apps.lint.feature.captions',
  'feature.differentOutputFormat': 'apps.lint.feature.differentOutputFormat',
  'feature.duckedOrReplacedDialogue': 'apps.lint.feature.duckedOrReplacedDialogue',
  'feature.censoring': 'apps.lint.feature.censoring',
  'feature.fades': 'apps.lint.feature.fades',
  'feature.regionEffects': 'apps.lint.feature.regionEffects',
  'feature.transitions': 'apps.lint.feature.transitions',
  'feature.keyframesNotSnapped': 'apps.lint.feature.keyframesNotSnapped',
  'feature.rooms': 'apps.lint.feature.rooms',
  'feature.audioGeneration': 'apps.lint.feature.audioGeneration',
  'feature.videoGeneration': 'apps.lint.feature.videoGeneration',

  'field.language': 'apps.lint.field.language',
  'field.sourceLanguage': 'apps.lint.field.sourceLanguage',
  'field.lengthSec': 'apps.lint.field.lengthSec',
  'field.voice': 'apps.lint.field.voice',
  'field.style': 'apps.lint.field.style',
  'field.format': 'apps.lint.field.format',
  'field.resolution': 'apps.lint.field.resolution',
  'field.designSystem': 'apps.lint.field.designSystem',
  'field.sourceFiles': 'apps.lint.field.sourceFiles',
  'field.avatar': 'apps.lint.field.avatar',

  'reason.consentMissing': 'apps.lint.reason.consentMissing',
  'reason.consentRevoked': 'apps.lint.reason.consentRevoked',
  'reason.retentionExpired': 'apps.lint.reason.retentionExpired',
  'reason.notInProject': 'apps.lint.reason.notInProject',

  'voice.named': 'apps.lint.voice.named',
  'voice.youChose': 'apps.lint.voice.youChose',
  'avatar.named': 'apps.lint.avatar.named',
  'avatar.youChose': 'apps.lint.avatar.youChose',
  'voiceKind.narration': 'apps.lint.voiceKind.narration',
  'voiceKind.pickups': 'apps.lint.voiceKind.pickups',
  'later.narration': 'apps.lint.later.narration',
  'later.music': 'apps.lint.later.music',
  'later.finalCut': 'apps.lint.later.finalCut',
  'slot.videoFile': 'apps.lint.slot.videoFile',
  'slot.musicTrack': 'apps.lint.slot.musicTrack',
  'slot.insert': 'apps.lint.slot.insert',

  'WL001.isNotSet': 'apps.lint.WL001.isNotSet',
  'WL001.notInWorkflow': 'apps.lint.WL001.notInWorkflow',
  'WL001.comesAfter': 'apps.lint.WL001.comesAfter',
  'WL001.wrongKind': 'apps.lint.WL001.wrongKind',
  'WL002.doesNotNameTheStep': 'apps.lint.WL002.doesNotNameTheStep',
  'WL002.notInWorkflow': 'apps.lint.WL002.notInWorkflow',
  'WL002.comesAfter': 'apps.lint.WL002.comesAfter',
  'WL002.wrongKind': 'apps.lint.WL002.wrongKind',
  'WL002.problem': 'apps.lint.WL002.problem',
  'WL003.isNotSet': 'apps.lint.WL003.isNotSet',
  'WL003.notInWorkflow': 'apps.lint.WL003.notInWorkflow',
  'WL003.comesAfter': 'apps.lint.WL003.comesAfter',
  'WL003.noImprovement': 'apps.lint.WL003.noImprovement',
  'WL004.isNotSet': 'apps.lint.WL004.isNotSet',
  'WL004.notInWorkflow': 'apps.lint.WL004.notInWorkflow',
  'WL004.comesAfter': 'apps.lint.WL004.comesAfter',
  'WL004.wrongKind': 'apps.lint.WL004.wrongKind',
  'WL004.firstStep': 'apps.lint.WL004.firstStep',
  'WL004.previousWrongKind': 'apps.lint.WL004.previousWrongKind',
  WL005: 'apps.lint.WL005',
  'WL006': 'apps.lint.WL006.message',
  'WL006.providerOwnership': 'apps.lint.WL006.providerOwnership',
  'WL006.modelUnsupported': 'apps.lint.WL006.modelUnsupported',
  'WL006.lipSyncModelNotSet': 'apps.lint.WL006.lipSyncModelNotSet',
  'WL007.stepFeature': 'apps.lint.WL007.stepFeature',
  'WL007.resolution': 'apps.lint.WL007.resolution',
  WL008: 'apps.lint.WL008',
  WL009: 'apps.lint.WL009',
  'WL010.missingMedia': 'apps.lint.WL010.missingMedia',
  'WL010.archiveOnly': 'apps.lint.WL010.archiveOnly',
  'WL010.unpacking': 'apps.lint.WL010.unpacking',
  'WL010.missingFile': 'apps.lint.WL010.missingFile',
  WL011: 'apps.lint.WL011',
  'WL012.step': 'apps.lint.WL012.step',
  'WL012.brief': 'apps.lint.WL012.brief',
  WL013: 'apps.lint.WL013',
  WL014: 'apps.lint.WL014',
  'WL015.tracksAnObjectByName': 'apps.lint.WL015.tracksAnObjectByName',
  'WL015.startsFromFrame': 'apps.lint.WL015.startsFromFrame',
  'WL015.cleansAudioWithAi': 'apps.lint.WL015.cleansAudioWithAi',
  'WL015.editsPictureWithAi': 'apps.lint.WL015.editsPictureWithAi',
  'WL015.enhancesVideoWithAi': 'apps.lint.WL015.enhancesVideoWithAi',
  'WL015.showsAnAvatar': 'apps.lint.WL015.showsAnAvatar',
  WL016: 'apps.lint.WL016',
  WL017: 'apps.lint.WL017',
  'WL018.unreadField': 'apps.lint.WL018.unreadField',
  'WL018.unusedRole': 'apps.lint.WL018.unusedRole',
  WL019: 'apps.lint.WL019',
  WL020: 'apps.lint.WL020',
  WL021: 'apps.lint.WL021',
  WL022: 'apps.lint.WL022',
  WL026: 'apps.lint.WL026',
  WL027: 'apps.lint.WL027',
  WL028: 'apps.lint.WL028',
  WL029: 'apps.lint.WL029',
  WL030: 'apps.lint.WL030.message',
  'WL030.recommended': 'apps.lint.WL030.recommended',
  WL031: 'apps.lint.WL031',
  WL032: 'apps.lint.WL032',
  WL033: 'apps.lint.WL033',
  'WL034.useSibling': 'apps.lint.WL034.useSibling',
  'WL034.noSibling': 'apps.lint.WL034.noSibling',
  'inputValue.on': 'apps.lint.inputValue.on',
  'inputValue.off': 'apps.lint.inputValue.off',

  'fix.WL001.retargetSource': 'apps.lint.fix.WL001.retargetSource',
  'fix.WL002.retargetVoiceover': 'apps.lint.fix.WL002.retargetVoiceover',
  'fix.WL003.retargetTarget': 'apps.lint.fix.WL003.retargetTarget',
  'fix.WL003.retargetModelStep': 'apps.lint.fix.WL003.retargetModelStep',
  'fix.WL004.retargetInput': 'apps.lint.fix.WL004.retargetInput',
  'fix.WL005.reencode': 'apps.lint.fix.WL005.reencode',
  'fix.WL006.chooseProvider': 'apps.lint.fix.WL006.chooseProvider',
  'fix.WL006.chooseAvailableProvider': 'apps.lint.fix.WL006.chooseAvailableProvider',
  'fix.WL007.changePlan': 'apps.lint.fix.WL007.changePlan',
  'fix.WL007.changeResolution': 'apps.lint.fix.WL007.changeResolution',
  'fix.WL008.chooseVoice': 'apps.lint.fix.WL008.chooseVoice',
  'fix.WL009.chooseVoice': 'apps.lint.fix.WL009.chooseVoice',
  'fix.WL010.upload': 'apps.lint.fix.WL010.upload',
  'fix.WL010.pickFile': 'apps.lint.fix.WL010.pickFile',
  'fix.WL012.approveDesign': 'apps.lint.fix.WL012.approveDesign',
  'fix.WL013.startLoopAfter': 'apps.lint.fix.WL013.startLoopAfter',
  'fix.WL014.earlierStep': 'apps.lint.fix.WL014.earlierStep',
  'fix.WL015.allowEgress': 'apps.lint.fix.WL015.allowEgress',
  'fix.WL015.allowOnAppPage': 'apps.lint.fix.WL015.allowOnAppPage',
  'fix.WL016.addVoiceover': 'apps.lint.fix.WL016.addVoiceover',
  'fix.WL017.shorterFilm': 'apps.lint.fix.WL017.shorterFilm',
  'fix.WL018.removeReference': 'apps.lint.fix.WL018.removeReference',
  'fix.WL019.voiceOrCaptions': 'apps.lint.fix.WL019.voiceOrCaptions',
  'fix.WL020.moveReview': 'apps.lint.fix.WL020.moveReview',
  'fix.WL021.muteSourceAudio': 'apps.lint.fix.WL021.muteSourceAudio',
  'fix.WL026.useScript': 'apps.lint.fix.WL026.useScript',
  'fix.WL027.chooseAvatar': 'apps.lint.fix.WL027.chooseAvatar',
  'fix.WL028.fillInput': 'apps.lint.fix.WL028.fillInput',
  'fix.WL029.changeInput': 'apps.lint.fix.WL029.changeInput',
  'fix.WL030.chooseModel': 'apps.lint.fix.WL030.chooseModel',
  'fix.WL033.narrowRange': 'apps.lint.fix.WL033.narrowRange',
  'fix.WL034.chooseModel': 'apps.lint.fix.WL034.chooseModel',
};

type Args = Record<string, string>;
type Item = string | number | Msg;

/**
 * A rule with one plain sentence and one variant cannot hold both under the same JSON name, so its plain
 * sentence sits at `<rule>.message`. The wire key stays the plain rule id (T-109's contract).
 */
const WIRE_PATHS: Record<string, string> = { WL006: 'WL006.message', WL030: 'WL030.message' };

function cataloguePath(key: string): string {
  return WIRE_PATHS[key] ?? key;
}

function build(path: string, values?: Record<string, Item | null>): Msg | null {
  const key = KEYS[path];
  if (!key) return null;
  if (!values) return msg(key);
  const out: Record<string, MsgValue> = {};
  for (const [name, value] of Object.entries(values)) {
    if (value === null) return null;
    out[name] = value;
  }
  return msg(key, out);
}

/** A closed word-set entry, e.g. `problem.wrongKind` or `feature.music`; unknown codes yield null. */
function word(prefix: string, code: string | undefined): Msg | null {
  return code ? build(`${prefix}.${code}`) : null;
}

/** The step as the subject of its own sentence ("The step 2 (Label)" / "Il passaggio 2 (Label)"). */
function actor(args: Args): Msg | null {
  const order = args.stepOrder;
  if (!order) return null;
  const label = args.stepLabel;
  return label
    ? build('ref.actor.named', { order, label })
    : build('ref.actor.plain', { order });
}

/** The step as a bare subject after its sentence has already supplied nothing ("step 2 (Label)" / "Il passaggio 2 (Label)"). */
function subject(args: Args): Msg | null {
  const order = args.stepOrder;
  if (!order) return null;
  const label = args.stepLabel;
  return label
    ? build('ref.subject.named', { order, label })
    : build('ref.subject.plain', { order });
}

/** "at step 2" / "al passaggio 2". */
function at(args: Args): Msg | null {
  const order = args.stepOrder;
  if (!order) return null;
  const label = args.stepLabel;
  return label ? build('ref.at.named', { order, label }) : build('ref.at.plain', { order });
}

/** "from step 2"/"from no step" / "dal passaggio 2"/"da nessun passaggio" (the C# sentence names only the order). */
function from(args: Args, orderKey: string): Msg | null {
  const order = args[orderKey];
  return order ? build('ref.from.plain', { order }) : build('ref.from.none');
}

/** "to step 2"/"to no step" / "al passaggio 2"/"a nessun passaggio" (the C# sentence names only the order). */
function to(args: Args, orderKey: string): Msg | null {
  const order = args[orderKey];
  return order ? build('ref.to.plain', { order }) : build('ref.to.none');
}

/** "over step 2 (Label)" / "sopra il passaggio 2 (Label)" (or "sul passaggio 2" without a label). */
function over(args: Args, orderKey: string, labelKey: string): Msg | null {
  const order = args[orderKey];
  if (!order) return null;
  const label = args[labelKey];
  return label ? build('ref.over.named', { order, label }) : build('ref.over.plain', { order });
}

/** "step 2 (Label)" / "il passaggio 2 (Label)" — a referenced step inside a problem clause. */
function definite(args: Args, orderKey: string, labelKey: string): Msg | null {
  const order = args[orderKey];
  if (!order) return null;
  const label = args[labelKey];
  return label ? build('ref.definite.named', { order, label }) : build('ref.definite.plain', { order });
}

/** A step named only inside parentheses, with no article in either language ("(step 3 (Label))"). */
function bareStep(args: Args): Msg | null {
  const order = args.stepOrder;
  if (!order) return build('step.none');
  const label = args.stepLabel;
  return label ? build('step.named', { order, label }) : build('step.plain', { order });
}

function problem(args: Args, prefix: 'source' | 'target'): Msg | null {
  const code = args.problem;
  if (!code) return null;
  if (code === 'wrongKind' || code === 'previousWrongKind') {
    const expected = word('expected', args.expectedKind);
    const found = definite(args, `${prefix}Order`, `${prefix}Label`);
    return build(`problem.${code}`, { expected, found });
  }
  return build(`problem.${code}`);
}

function indexed(args: Args, prefix: string): string[] {
  const count = Number(args[`${prefix}Count`] ?? 0);
  const out: string[] = [];
  for (let i = 1; i <= count; i++) {
    const value = args[`${prefix}${i}`];
    if (value === undefined) break;
    out.push(value);
  }
  return out;
}

function commaList(items: Item[]): Item | null {
  if (items.length === 0) return null;
  if (items.length === 1) return items[0]!;
  const tail = commaList(items.slice(1));
  return tail === null ? null : build('list.comma', { head: items[0]!, tail });
}

function andList(items: Item[]): Item | null {
  if (items.length === 0) return null;
  if (items.length === 1) return items[0]!;
  if (items.length === 2) return build('list.and', { head: items[0]!, tail: items[1]! });
  const tail = andList(items.slice(1));
  return tail === null ? null : build('list.comma', { head: items[0]!, tail });
}

function featureList(args: Args): Item | null {
  const items = indexed(args, 'feature').map((code) => word('feature', code));
  return items.some((item) => item === null) ? null : commaList(items as Msg[]);
}

function languageList(args: Args): Item | null {
  return commaList(indexed(args, 'language'));
}

function laterList(args: Args): Item | null {
  const count = Number(args.laterCount ?? 0);
  const items: (Msg | null)[] = [];
  for (let i = 1; i <= count; i++) {
    const kind = args[`later${i}Kind`];
    const order = args[`later${i}Order`];
    items.push(kind && order ? build(`later.${kind}`, { order }) : null);
  }
  return items.length === 0 || items.some((item) => item === null) ? null : andList(items as Msg[]);
}

const CHECK_PROBLEMS = new Set(['isNotSet', 'notInWorkflow', 'comesAfter', 'wrongKind']);

/** The sentence for a wire `messageKey`; null when this catalogue version cannot build it (the caller falls back). */
function message(key: string, args: Args): Msg | null {
  const rule = key.slice(0, 5);
  const variant = key.slice(6);

  if (rule === 'WL001' && CHECK_PROBLEMS.has(variant)) {
    return build(key, {
      actor: actor(args),
      from: from(args, 'sourceOrder'),
      problem: problem(args, 'source'),
    });
  }

  if (rule === 'WL002' && [...CHECK_PROBLEMS, 'doesNotNameTheStep'].includes(variant)) {
    const detail = problem(args, 'target');
    const voiceKind = word('voiceKind', args.voiceKind);
    const wrapped = detail && voiceKind ? build('WL002.problem', { voiceKind, problem: detail }) : null;
    return build(key, { actor: actor(args), problem: wrapped });
  }

  if (rule === 'WL003') {
    if (variant === 'noImprovement') {
      return build(key, { at: at(args), over: over(args, 'targetOrder', 'targetLabel') });
    }
    if (CHECK_PROBLEMS.has(variant)) {
      return build(key, {
        at: at(args),
        to: to(args, 'targetOrder'),
        problem: problem(args, 'target'),
      });
    }
    return null;
  }

  if (rule === 'WL004') {
    if (variant === 'firstStep') {
      return build(key, {
        actor: actor(args),
        input: word('input', args.input),
        from: build('ref.previousFrom'),
        problem: problem(args, 'target'),
      });
    }
    if (variant === 'previousWrongKind' || CHECK_PROBLEMS.has(variant)) {
      return build(key, {
        actor: actor(args),
        input: word('input', args.input),
        from: from(args, 'targetOrder'),
        problem: problem(args, 'target'),
      });
    }
    return null;
  }

  switch (key) {
    case 'WL005':
      return build(key, { actor: actor(args), features: featureList(args) });
    case 'WL006':
      return build(key, {
        actor: actor(args),
        capability: word('capability', args.capability),
        problem: word('problem', args.problem),
      });
    case 'WL006.providerOwnership':
      return build(key, { detail: args.detail ?? null });
    case 'WL006.modelUnsupported':
      return build(key, { actor: actor(args), model: args.model ?? null });
    case 'WL006.lipSyncModelNotSet':
      return build(key, { actor: actor(args) });
    case 'WL007.stepFeature':
      return build(key, { feature: word('feature', args.feature), step: bareStep(args) });
    case 'WL007.resolution':
      return build(key, { resolution: args.resolution ?? null });
    case 'WL008': {
      const voice = args.voice === 'youChose'
        ? build('voice.youChose')
        : args.voice ? build('voice.named', { name: args.voice }) : null;
      return build(key, { voice, reason: word('reason', args.reason) });
    }
    case 'WL009':
      return build(key, { language: args.language ?? null, languages: languageList(args) });
    case 'WL010.missingMedia':
    case 'WL010.archiveOnly':
    case 'WL010.unpacking':
      return build(key, { actor: actor(args), role: word('role', args.role) });
    case 'WL010.missingFile':
      return build(key, { stepOrder: args.stepOrder ?? null, slot: word('slot', args.slot) });
    case 'WL011':
      return build(key, { field: word('field', args.field) });
    case 'WL012.step':
      return build(key, { step: subject(args) });
    case 'WL012.brief':
      return build(key);
    case 'WL013':
      return build(key, { actor: actor(args), loop: args.loopOrder ?? null });
    case 'WL014':
      return build(key, { actor: actor(args), target: args.targetOrder ?? null });
    case 'WL015.tracksAnObjectByName':
    case 'WL015.startsFromFrame':
    case 'WL015.cleansAudioWithAi':
    case 'WL015.editsPictureWithAi':
    case 'WL015.enhancesVideoWithAi':
    case 'WL015.showsAnAvatar':
      return build(key, { actor: actor(args) });
    case 'WL016':
      return build(key);
    case 'WL017':
      return build(key, { length: args.length ?? null, available: args.available ?? null });
    case 'WL018.unreadField':
      return build(key, { input: args.input ?? null });
    case 'WL018.unusedRole':
      return build(key, { input: args.input ?? null, role: word('fileRole', args.role) });
    case 'WL019':
      return build(key, { actor: actor(args) });
    case 'WL020':
      return build(key, {
        reviewOrder: args.reviewOrder ?? null,
        later: laterList(args),
        lastOrder: args.lastOrder ?? null,
      });
    case 'WL021':
      return build(key, {
        actor: actor(args),
        over: over(args, 'sourceOrder', 'sourceLabel'),
      });
    case 'WL022':
      return build(key, { label: args.label ?? null });
    case 'WL026':
      // {step} is the "at step N" form ("al passaggio N"), {script} the subject form ("even though step N writes").
      return args.stepOrder
        ? build(key, { step: bareStep(args), script: definite(args, 'scriptOrder', 'scriptLabel') })
        : null;
    case 'WL027': {
      // {avatar} is the avatar's own display name (never the consent subject's), or the code youChose.
      const avatar = args.avatar === 'youChose'
        ? build('avatar.youChose')
        : args.avatar ? build('avatar.named', { name: args.avatar }) : null;
      return build(key, { actor: actor(args), avatar, reason: word('reason', args.reason) });
    }
    // App inputs (docs/design/app-inputs.md 11.2): {input} is the author's own label, shown as written.
    case 'WL028':
    case 'WL029':
    case 'WL030':
    case 'WL030.recommended':
      return build(key, { input: args.input ?? null });
    case 'WL031': {
      // A switch's value is the code on/off; a choice's value is its own label; a number as written.
      const value = args.value === 'on' || args.value === 'off' ? build(`inputValue.${args.value}`) : args.value ?? null;
      return build(key, { input: args.input ?? null, value, count: count(args.count) });
    }
    case 'WL032':
      return build(key, { input: args.input ?? null, count: count(args.count) });
    case 'WL033':
      return build(key, { input: args.input ?? null, actor: actor(args), credits: args.credits ?? null, cap: args.cap ?? null });
    // A step that shows an avatar or an object on a model that takes no pictures: {model} and {sibling} are model names,
    // shown as written; useSibling names the image-capable model of the same family, noSibling has none to name.
    case 'WL034.useSibling':
      return build(key, { actor: actor(args), model: args.model ?? null, sibling: args.sibling ?? null });
    case 'WL034.noSibling':
      return build(key, { actor: actor(args), model: args.model ?? null });
    default:
      return null;
  }
}

/** A count token as the number an ICU plural needs; null when absent or not a number. */
function count(value: string | undefined): number | null {
  const n = value === undefined ? NaN : Number(value);
  return Number.isFinite(n) ? n : null;
}

function buildFix(fixLabelId: string, args: Args): Msg | null {
  switch (fixLabelId) {
    case 'WL028.fillInput':
    case 'WL029.changeInput':
      return build(`fix.${fixLabelId}`, { input: args.input ?? null });
    case 'WL001.retargetSource':
      return build('fix.WL001.retargetSource', { expected: word('expected', args.expectedKind) });
    case 'WL004.retargetInput':
      return build('fix.WL004.retargetInput', { input: word('input', args.input) });
    case 'WL010.upload':
      return build('fix.WL010.upload', { role: word('role', args.role) });
    default:
      return build(`fix.${fixLabelId}`);
  }
}

/** The finding's sentence as a catalogue message, or the server's English sentence when it cannot be built. */
export function findingMessage(finding: LintFinding, t: LintTranslator): string | Msg {
  const key = finding.messageKey;
  if (!key || !t.has(`lint.${cataloguePath(key)}`)) return finding.message;
  return message(key, finding.args ?? {}) ?? finding.message;
}

/** The fix button's label as a catalogue message, or the server's English label when it cannot be built. */
export function fixLabel(f: LintFix, t: LintTranslator): string | Msg {
  const key = f.labelId;
  if (!key || !t.has(`lint.fix.${key}`)) return f.label;
  return buildFix(key, f.args ?? {}) ?? f.label;
}
