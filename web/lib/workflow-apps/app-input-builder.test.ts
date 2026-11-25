import { describe, expect, it } from 'vitest';
import type { StepData } from '@/components/workflows/WorkflowStepList';
import type { AppBindableField, AppManifest } from '@/lib/types/workflow';
import {
  appInputsEntries, appInputsOf, bindValue, canMap, compatibleInputs, concretePaths, conditionSnippet, entryValues, errorsByInput,
  getPath, inputForSetting, inputUses, insertAt, mappableKeys, newInputId, normalizeInput, placeholderIds, removeInput, setPath,
  settingValue, settleVersion, unbindValue, compatibleTypes, changeType, manifestForSave, moveInputTo,
} from './app-input-builder';

function step(over: Partial<StepData>): StepData {
  return {
    id: 's', label: 'Step', agentDefinitionId: 'a', stepType: 'Agent', conditionExpression: null, loopSourceExpression: null,
    loopTargetStepOrder: null, maxIterations: 3, minScore: null, inputMappingJson: null, agentInputContextMode: null, cacheMode: null,
    selectedPriorStepOrders: [], trueBranchStepOrder: null, falseBranchStepOrder: null, parallelAgentIds: [], extractConfig: null,
    videoAnalyzeConfig: null, videoCompileConfig: null, videoGenerateConfig: null, voiceoverConfig: null, thumbnailGenerateConfig: null,
    ...over,
  } as StepData;
}

const PROMPT: AppBindableField = { path: 'inlinePrompt', mode: 'placeholder', types: ['shortText', 'longText'], label: 'Video prompt', maxLength: 7000 };
const RATIO: AppBindableField = { path: 'ratio', mode: 'value', types: ['shape', 'choice'], label: 'Shape', choices: ['9:16', '1:1', '16:9', '4:5'] };
const LENGTH: AppBindableField = { path: 'durationSec', mode: 'value', types: ['duration', 'number'], label: 'Length', min: 0.5, max: 300 };
const LOOK: AppBindableField = { path: 'captionStyle', mode: 'value', types: ['choice'], label: 'Caption look', choices: ['Subtitle', 'Karaoke'] };
const MODEL: AppBindableField = { path: '@model', mode: 'value', types: ['model'], label: 'Model', capabilities: ['MusicGeneration', 'SoundEffectGeneration'] };
const LABEL: AppBindableField = { path: 'trackObjects[].label', mode: 'placeholder', types: ['shortText'], label: 'What to follow', maxLength: 160 };

const video = step({ stepType: 'VideoGenerate', videoGenerateConfig: { inlinePrompt: 'A barista pours latte art', ratio: '16:9' } as never });

describe('paths and bindings', () => {
  it('expands an array path to each item the step has and reads and writes concrete paths', () => {
    const config = { trackObjects: [{ label: 'cat' }, { label: 'dog' }] };
    expect(concretePaths(config, 'trackObjects[].label')).toEqual(['trackObjects[0].label', 'trackObjects[1].label']);
    expect(concretePaths({}, 'trackObjects[].label')).toEqual([]);
    expect(getPath(config, 'trackObjects[1].label')).toBe('dog');
    const next = setPath(config, 'trackObjects[1].label', '{{input.what}}');
    expect(getPath(next, 'trackObjects[1].label')).toBe('{{input.what}}');
    expect(config.trackObjects[1].label).toBe('dog');
  });

  it('adds and removes appInputs entries, dropping the map when it empties', () => {
    const bound = bindValue({ ratio: '16:9' }, 'ratio', 'clipShape');
    expect(appInputsOf(bound)).toEqual({ ratio: 'clipShape' });
    expect(unbindValue(bound, 'ratio')).toEqual({ ratio: '16:9' });
  });

  it('lists where each input is used, in words the panel shows', () => {
    const manifest: AppManifest = {
      version: 2,
      requestTemplate: 'About {{input.topic}}',
      inputs: [{ kind: 'input', id: 'model', type: 'model', capability: 'Chat', agent: 'agent-1', label: 'AI model' }],
    };
    const steps = [
      step({ stepType: 'Conditional', conditionExpression: 'input.lite' }),
      step({ stepType: 'VideoGenerate', videoGenerateConfig: { inlinePrompt: '{{input.topic}} slow', appInputs: { ratio: 'clipShape' } } as never }),
      step({ stepType: 'Agent', agentDefinitionId: 'agent-1' }),
    ];
    const uses = inputUses(manifest, steps, { VideoGenerate: [PROMPT, RATIO] });
    expect(uses.get('lite')).toEqual([{ kind: 'condition', stepIndex: 0 }]);
    expect(uses.get('topic')).toEqual([{ kind: 'placeholder', stepIndex: 1, setting: 'Video prompt' }, { kind: 'request' }]);
    expect(uses.get('clipShape')).toEqual([{ kind: 'value', stepIndex: 1, setting: 'Shape' }]);
    expect(uses.get('model')).toEqual([{ kind: 'agent', stepIndex: 2 }]);
  });
});

describe('new fields from a setting', () => {
  const empty: AppManifest = { version: 1, inputs: [] };

  it('gives a fresh, valid id that is never a run-brief field', () => {
    expect(newInputId('What happens in the video', empty)).toBe('whatHappensInTheVideo');
    expect(newInputId('Language', empty)).toBe('language2');
    expect(newInputId('Shape', { version: 2, inputs: [{ kind: 'input', id: 'shape', type: 'shape' }] })).toBe('shape2');
    expect(newInputId('Caffè ☕', empty)).toBe('caffe');
    expect(newInputId('3 shots', empty)).toBe('field3Shots');
  });

  it('turns a text setting into a long text field whose example is the old words', () => {
    const input = inputForSetting(video, PROMPT, 'inlinePrompt', empty, 'Video prompt');
    expect(input).toMatchObject({ kind: 'input', id: 'videoPrompt', type: 'longText', maxLength: 7000, example: 'A barista pours latte art' });
  });

  it('turns choices, shapes and lengths into fields that fit the setting', () => {
    expect(inputForSetting(video, RATIO, 'ratio', empty, 'Shape')).toMatchObject({ type: 'shape', choices: ['9:16', '1:1', '16:9', '4:5'], default: '16:9' });
    const compile = step({ stepType: 'VideoCompile', videoCompileConfig: { captionStyle: 'Karaoke' } as never });
    expect(inputForSetting(compile, LOOK, 'captionStyle', empty, 'Caption look')).toMatchObject({
      type: 'choice', default: 'Karaoke', options: [{ value: 'Subtitle', label: 'Subtitle' }, { value: 'Karaoke', label: 'Karaoke' }],
    });
    const music = step({ stepType: 'AudioGenerate', audioGenerateConfig: { kind: 'SoundEffect', durationSec: 0.5 } as never });
    expect(inputForSetting(music, LENGTH, 'durationSec', empty, 'Length')).toEqual({ kind: 'input', id: 'length', label: 'Length', type: 'duration', min: 1, max: 300 });
    expect(inputForSetting(music, MODEL, '@model', empty, 'Model')).toMatchObject({ type: 'model', capability: 'SoundEffectGeneration' });
  });

  it('offers only fields of a type the setting takes, and a model of the same kind', () => {
    const manifest: AppManifest = {
      version: 2,
      inputs: [
        { kind: 'input', id: 'shape', type: 'shape' },
        { kind: 'input', id: 'words', type: 'shortText' },
        { kind: 'input', id: 'music', type: 'model', capability: 'MusicGeneration' },
        { kind: 'input', id: 'chat', type: 'model', capability: 'Chat', agent: 'a' },
      ],
    };
    expect(compatibleInputs(manifest, RATIO, null).map((i) => i.id)).toEqual(['shape']);
    expect(compatibleInputs(manifest, MODEL, 'MusicGeneration').map((i) => i.id)).toEqual(['music']);
    expect(compatibleInputs(manifest, LABEL, null).map((i) => i.id)).toEqual(['words']);
  });
});

describe('editing the page', () => {
  it('drops members the type cannot carry and empty words', () => {
    expect(normalizeInput({ kind: 'input', id: 'x', type: 'toggle', label: 'Lite', help: '', maxLength: 10, min: 1, required: false, default: true }))
      .toEqual({ kind: 'input', id: 'x', type: 'toggle', label: 'Lite', default: true });
  });

  it('is version 2 only while it uses something of version 2', () => {
    expect(settleVersion({ version: 1, inputs: [{ kind: 'brief', field: 'language' }] }).version).toBe(1);
    expect(settleVersion({ version: 1, inputs: [{ kind: 'brief', field: 'language', more: true }] }).version).toBe(2);
    expect(settleVersion({ version: 1, inputs: [], requestTemplate: 'x' }).version).toBe(2);
  });

  it('removes a field and every use of it, and refuses while a condition reads it', () => {
    const manifest: AppManifest = {
      version: 2,
      requestTemplate: 'Make it {{input.mood}}.\nFor {{input.who}}.',
      inputs: [
        { kind: 'input', id: 'mood', type: 'shortText', label: 'Mood', default: 'calm' },
        { kind: 'input', id: 'who', type: 'shortText', label: 'Who', showWhen: { input: 'mood', equals: 'x' } },
      ],
    };
    const steps = [step({ stepType: 'VideoGenerate', videoGenerateConfig: { inlinePrompt: 'A {{input.mood}} shot', appInputs: { ratio: 'mood' } } as never })];
    const result = removeInput(manifest, steps, 'mood');
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.manifest.requestTemplate).toBe('For {{input.who}}.');
    expect(result.manifest.inputs).toEqual([{ kind: 'input', id: 'who', type: 'shortText', label: 'Who', showWhen: null }]);
    expect(result.steps[0].videoGenerateConfig).toEqual({ inlinePrompt: 'A calm shot' });

    const refused = removeInput(manifest, [step({ stepType: 'Conditional', conditionExpression: 'input.mood == "x"' })], 'mood');
    expect(refused).toEqual({ ok: false, reason: 'condition', stepIndex: 0 });
  });

  it('inserts a field at the caret with a space before it', () => {
    expect(insertAt('Make it', 7, '{{input.mood}}')).toEqual({ text: 'Make it {{input.mood}}', caret: 22 });
    expect(insertAt('', null, '{{input.mood}}').text).toBe('{{input.mood}}');
    expect(placeholderIds('{{input.a}} and {{input.b}} {{input.a}}')).toEqual(['a', 'b']);
  });

  it('writes a condition for a switch and a choice', () => {
    expect(conditionSnippet({ kind: 'input', id: 'lite', type: 'toggle' })).toBe('input.lite');
    expect(conditionSnippet({ kind: 'input', id: 'q', type: 'choice', options: [{ value: 'high' }] })).toBe("input.q == 'high'");
    expect(conditionSnippet({ kind: 'input', id: 'q', type: 'choice', options: [{ value: "a\\'b" }] })).toBe("input.q == 'a\\\\\\'b'");
  });

  it('places each server refusal under the field it names', () => {
    const manifest: AppManifest = { version: 2, inputs: [{ kind: 'brief', field: 'language', label: 'Language' }, { kind: 'input', id: 'mood', type: 'shortText', label: 'Mood' }] };
    const placed = errorsByInput(manifest, [
      'The input "Mood" is not used by any step, the request template or a condition. Use it or remove it.',
      '"ghost" is used (step 1) but the app declares no input with that id.',
    ]);
    expect(placed.byIndex.get(1)).toHaveLength(1);
    expect(placed.general).toEqual(['"ghost" is used (step 1) but the app declares no input with that id.']);
  });
});

describe('mapped and nested bindings, the words and the picture model (F1)', () => {
  const FADE: AppBindableField = { path: 'programFadeOutMs', mode: 'value', types: ['toggle', 'number'], label: 'Fade', min: 0, max: 10000, valueKind: 'number' };
  const SOURCE: AppBindableField = { path: 'captionSource', mode: 'value', types: ['choice'], label: 'Caption text from', choices: ['Voiceover', 'Transcript'], valueKind: 'text' };
  const EFFECT: AppBindableField = { path: 'regionEffects[].effect', mode: 'value', types: ['choice'], label: 'Effect', choices: ['Blur', 'Pixelate'], valueKind: 'text' };
  const WORDS: AppBindableField = { path: '@spokenText', mode: 'value', types: ['shortText', 'longText'], label: 'Words to speak', maxLength: 7000 };
  const STILL: AppBindableField = { path: '@stillModel', mode: 'value', types: ['model'], label: 'Picture model', capabilities: ['ImageEdit'] };

  it('writes, reads and removes a mapped entry next to plain ones', () => {
    let config: Record<string, unknown> = bindValue({ programFadeOutMs: 800 }, 'enableCaptions', 'captions');
    config = bindValue(config, 'captionSource', 'captions', { true: 'Transcript' });
    expect(config.appInputs).toEqual({ enableCaptions: 'captions', captionSource: { input: 'captions', values: { true: 'Transcript' } } });
    expect(appInputsOf(config)).toEqual({ enableCaptions: 'captions', captionSource: 'captions' });
    expect(entryValues(config, 'captionSource')).toEqual({ true: 'Transcript' });
    expect(entryValues(config, 'enableCaptions')).toBeNull();
    config = unbindValue(config, 'enableCaptions');
    expect(appInputsEntries(config)).toEqual({ captionSource: { input: 'captions', values: { true: 'Transcript' } } });
    expect(bindValue(config, 'captionSource', 'captions', {}).appInputs).toEqual({ captionSource: 'captions' });
  });

  it('lists the answers of a switch or a choice and types each value for the setting', () => {
    expect(mappableKeys({ kind: 'input', id: 'a', type: 'toggle' }).map((k) => k.key)).toEqual(['true', 'false']);
    expect(mappableKeys({ kind: 'input', id: 'b', type: 'choice', options: [{ value: 'calm', label: 'Calm' }, { value: -16, label: 'Mid' }] }).map((k) => k.key))
      .toEqual(['calm', '-16']);
    expect(settingValue(FADE, '800')).toBe(800);
    expect(settingValue(FADE, '99999')).toBeNull();
    expect(settingValue(SOURCE, 'transcript')).toBe('Transcript');
    expect(settingValue(SOURCE, 'Melt')).toBeNull();
    expect(settingValue({ ...FADE, valueKind: 'boolean' }, 'true')).toBe(true);
    expect(settingValue(FADE, '')).toBeNull();
    expect(canMap(FADE)).toBe(true);
    expect(canMap(WORDS)).toBe(false);
    expect(canMap(STILL)).toBe(false);
  });

  it('offers a switch or a choice for a setting of another type, never for a model or the words', () => {
    const manifest: AppManifest = {
      version: 2,
      inputs: [
        { kind: 'input', id: 'ending', type: 'toggle', label: 'Ending' },
        { kind: 'input', id: 'pace', type: 'choice', label: 'Pace', options: [{ value: 'a', label: 'A' }] },
        { kind: 'input', id: 'words', type: 'longText', label: 'Words' },
        { kind: 'input', id: 'picture', type: 'model', label: 'Picture', capability: 'ImageEdit' },
      ],
    };
    expect(compatibleInputs(manifest, SOURCE, null).map((i) => i.id)).toEqual(['ending', 'pace']);
    expect(compatibleInputs(manifest, WORDS, null).map((i) => i.id)).toEqual(['words']);
    expect(compatibleInputs(manifest, STILL, 'ImageEdit').map((i) => i.id)).toEqual(['picture']);
  });

  it('expands an item setting to each item and makes the right new field for the words and the picture model', () => {
    const compile = step({ stepType: 'VideoCompile', videoCompileConfig: { regionEffects: [{ target: 'plate', effect: 'Blur' }] } as never });
    expect(concretePaths({ regionEffects: [{ effect: 'Blur' }] }, EFFECT.path)).toEqual(['regionEffects[0].effect']);
    expect(inputForSetting(compile, EFFECT, 'regionEffects[0].effect', { version: 2, inputs: [] }, 'Effect'))
      .toMatchObject({ type: 'choice', default: 'Blur', options: [{ value: 'Blur' }, { value: 'Pixelate' }] });
    const voice = step({ stepType: 'Voiceover', voiceoverConfig: { source: { kind: 'Inline', lines: [] } } as never });
    expect(inputForSetting(voice, WORDS, '@spokenText', { version: 2, inputs: [] }, 'What they say')).toMatchObject({ type: 'longText', maxLength: 7000 });
    expect(inputForSetting(video, STILL, '@stillModel', { version: 2, inputs: [] }, 'Picture model')).toMatchObject({ type: 'model', capability: 'ImageEdit' });
  });

  it('removing a field removes its mapped entries too', () => {
    const manifest: AppManifest = { version: 2, inputs: [{ kind: 'input', id: 'captions', type: 'toggle', label: 'Captions' }] };
    const compile = step({
      stepType: 'VideoCompile',
      videoCompileConfig: { appInputs: { enableCaptions: 'captions', captionSource: { input: 'captions', values: { true: 'Transcript' } }, ratio: 'x' } } as never,
    });
    const result = removeInput(manifest, [compile], 'captions');
    expect(result.ok && result.steps[0].videoCompileConfig).toEqual({ appInputs: { ratio: 'x' } });
    expect(inputUses(manifest, [compile], null).get('captions')).toHaveLength(2);
  });
});

describe('reorder by drag, kind of a field, the page as saved (F3)', () => {
  const page: AppManifest = { version: 2, title: 'T', inputs: [
    { kind: 'input', id: 'a', type: 'shortText', label: 'A' },
    { kind: 'input', id: 'b', type: 'number', label: 'B', min: 1, max: 4, step: 1, unit: 'takes' },
    { kind: 'input', id: 'c', type: 'toggle', label: 'C' },
  ] };

  it('moves a field to any place and ignores a drop out of range', () => {
    expect(moveInputTo(page, 2, 0).inputs!.map((i) => i.id)).toEqual(['c', 'a', 'b']);
    expect(moveInputTo(page, 0, 9)).toBe(page);
  });

  it('changes a field between kinds with the same answer only, keeping its id', () => {
    expect(compatibleTypes('shortText')).toEqual(['shortText', 'longText']);
    expect(compatibleTypes('toggle')).toEqual([]);
    const long = changeType({ kind: 'input', id: 'a', type: 'longText', label: 'A', maxLength: 2000, default: 'x'.repeat(300) }, 'shortText');
    expect(long).toMatchObject({ id: 'a', type: 'shortText', maxLength: 200 });
    expect((long.default as string).length).toBe(200);
    expect(changeType(page.inputs![1], 'duration')).toEqual({ kind: 'input', id: 'b', type: 'duration', label: 'B', min: 1, max: 4 });
    expect(changeType(page.inputs![2], 'number')).toBe(page.inputs![2]);
  });

  it('saves the whole page when one is stored, else only the fields and the request text', () => {
    expect(manifestForSave(page, true)).toBe(page);
    expect(manifestForSave({ ...page, requestTemplate: 'R' }, false)).toEqual({ version: 2, inputs: page.inputs, requestTemplate: 'R' });
  });
});
