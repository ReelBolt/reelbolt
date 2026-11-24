import { describe, expect, it, vi } from 'vitest';
import { IconSparkles } from '@tabler/icons-react';
import { listEditorAiTools, registerEditorAiTool, toolNeedMet, unregisterEditorAiTool, type EditorAiToolContext } from '../ai-tools';
import '../ai-tools.builtin';

const ctx = (over: Partial<EditorAiToolContext> = {}): EditorAiToolContext => ({
  selectedClipId: null, selectedClipKind: null, hasRange: false,
  openPanel: vi.fn(), openWorkspace: vi.fn(), openRemoveSilence: vi.fn(), openCutToBeat: vi.fn(), openInspectorSection: vi.fn(), openAssist: vi.fn(), openDeliver: vi.fn(), ...over,
});

describe('editor AI tools registry', () => {
  it('lists the built-ins and accepts a new tool, replacing by id', () => {
    const ids = listEditorAiTools().map((t) => t.id);
    expect(ids).toEqual(expect.arrayContaining(['magicBlur', 'cleanVoice', 'removeSilence', 'cutToMusic', 'analyseFootage', 'editByTranscript', 'suggestRecut', 'suggestReview', 'checkLoudness', 'listenToMix', 'preview3s']));
    const def = { id: 'x', icon: IconSparkles, group: 'sound' as const, labelKey: 'a', descriptionKey: 'b', cost: 'free' as const, needs: 'none' as const, open: vi.fn() };
    registerEditorAiTool(def);
    registerEditorAiTool({ ...def, cost: 'credits' });
    expect(listEditorAiTools().filter((t) => t.id === 'x')).toHaveLength(1);
    expect(listEditorAiTools().find((t) => t.id === 'x')?.cost).toBe('credits');
    unregisterEditorAiTool('x');
  });
  it('checks needs against the selection', () => {
    expect(toolNeedMet('none', ctx())).toBe(true);
    expect(toolNeedMet('videoClip', ctx({ selectedClipId: 'a', selectedClipKind: 'audio' }))).toBe(false);
    expect(toolNeedMet('videoClip', ctx({ selectedClipId: 'a', selectedClipKind: 'video' }))).toBe(true);
    expect(toolNeedMet('range', ctx({ hasRange: true }))).toBe(true);
  });
  it('opens the right entries', () => {
    const find = (id: string) => listEditorAiTools().find((t) => t.id === id)!;
    const c = ctx();
    find('removeSilence').open(c);
    expect(c.openRemoveSilence).toHaveBeenCalled();
    find('cutToMusic').open(c);
    expect(c.openCutToBeat).toHaveBeenCalled();
    find('suggestGrade').open(c);
    expect(c.openAssist).toHaveBeenCalledWith('grade');
    find('cleanVoice').open(c);
    expect(c.openWorkspace).toHaveBeenCalledWith('audio');
    find('magicBlur').open(c);
    expect(c.openInspectorSection).toHaveBeenCalledWith('object-effects-section');
    const phone = ctx({ openSheet: vi.fn() });
    find('editByTranscript').open(phone);
    expect(phone.openSheet).toHaveBeenCalledWith('captions');
    expect(phone.openPanel).toHaveBeenCalledWith('transcript');
  });
});
