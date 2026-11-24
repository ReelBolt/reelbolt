import { IconEye, IconFileText, IconFocus2, IconHeadphones, IconMicrophone, IconMusic, IconScissors, IconWand, IconVolume, IconWaveSine } from '@tabler/icons-react';
import { registerEditorAiTool, type EditorAiToolContext, type EditorAiToolSheet } from './ai-tools';
import type { ProposalAgent } from '@/lib/editing/sync/api';

/** Phone opens the sheet that hosts the panel; desktop just switches the panel. Both run, the last tab choice wins. */
function go(ctx: EditorAiToolContext, sheet: EditorAiToolSheet, panel?: string): void {
  ctx.openSheet?.(sheet);
  if (panel) ctx.openPanel(panel);
}

/** The audio page (mixer): desktop switches page, phone opens the Audio sheet. */
function openAudio(ctx: EditorAiToolContext): void {
  if (ctx.openSheet) ctx.openSheet('audio');
  else ctx.openWorkspace('audio');
}

registerEditorAiTool({
  id: 'magicBlur', icon: IconFocus2, group: 'picture', labelKey: 'tools.magicBlur.label', descriptionKey: 'tools.magicBlur.description',
  cost: 'credits', needs: 'videoClip', activityTypes: ['objectAnalysis'],
  open: (ctx) => {
    ctx.openSheet?.('edit');
    ctx.openInspectorSection('object-effects-section');
  },
});
registerEditorAiTool({
  id: 'cleanVoice', icon: IconMicrophone, group: 'sound', labelKey: 'tools.cleanVoice.label', descriptionKey: 'tools.cleanVoice.description',
  cost: 'free', needs: 'none', open: openAudio,
});
registerEditorAiTool({
  id: 'cleanupAudio', icon: IconWaveSine, group: 'sound', labelKey: 'tools.cleanupAudio.label', descriptionKey: 'tools.cleanupAudio.description',
  cost: 'credits', needs: 'none', activityTypes: ['audioCleanup'], open: openAudio,
});
registerEditorAiTool({
  id: 'removeSilence', icon: IconScissors, group: 'edit', labelKey: 'tools.removeSilence.label', descriptionKey: 'tools.removeSilence.description',
  cost: 'free', needs: 'none', open: (ctx) => ctx.openRemoveSilence(),
});
registerEditorAiTool({
  id: 'cutToMusic', icon: IconMusic, group: 'edit', labelKey: 'tools.cutToMusic.label', descriptionKey: 'tools.cutToMusic.description',
  cost: 'free', needs: 'none', activityTypes: ['mediaAnalysis'], open: (ctx) => ctx.openCutToBeat(),
});
registerEditorAiTool({
  id: 'analyseFootage', icon: IconWaveSine, group: 'check', labelKey: 'tools.analyseFootage.label', descriptionKey: 'tools.analyseFootage.description',
  cost: 'credits', needs: 'none', activityTypes: ['mediaAnalysis'], open: (ctx) => go(ctx, 'captions', 'analyse'),
});
registerEditorAiTool({
  id: 'editByTranscript', icon: IconFileText, group: 'edit', labelKey: 'tools.editByTranscript.label', descriptionKey: 'tools.editByTranscript.description',
  cost: 'credits', needs: 'none', open: (ctx) => go(ctx, 'captions', 'transcript'),
});

const HELPERS: readonly { agent: ProposalAgent; id: string; group: 'edit' | 'picture' | 'sound' | 'check' }[] = [
  { agent: 'recut', id: 'suggestRecut', group: 'edit' },
  { agent: 'graphics', id: 'suggestGraphics', group: 'picture' },
  { agent: 'grade', id: 'suggestGrade', group: 'picture' },
  { agent: 'sfx', id: 'suggestSoundEffects', group: 'sound' },
  { agent: 'review', id: 'suggestReview', group: 'check' },
];
for (const h of HELPERS) {
  registerEditorAiTool({
    id: h.id, icon: IconWand, group: h.group, labelKey: `tools.${h.id}.label`, descriptionKey: `tools.${h.id}.description`,
    cost: 'credits', needs: 'none', activityTypes: ['proposal'],
    open: (ctx) => {
      ctx.openSheet?.('assist');
      ctx.openAssist(h.agent);
    },
  });
}

registerEditorAiTool({
  id: 'checkLoudness', icon: IconVolume, group: 'check', labelKey: 'tools.checkLoudness.label', descriptionKey: 'tools.checkLoudness.description',
  cost: 'credits', needs: 'none', activityTypes: ['loudness'], open: openAudio,
});
registerEditorAiTool({
  id: 'listenToMix', icon: IconHeadphones, group: 'check', labelKey: 'tools.listenToMix.label', descriptionKey: 'tools.listenToMix.description',
  cost: 'free', needs: 'none', activityTypes: ['audition'], open: openAudio,
});
registerEditorAiTool({
  id: 'preview3s', icon: IconEye, group: 'check', labelKey: 'tools.preview3s.label', descriptionKey: 'tools.preview3s.description',
  cost: 'free', needs: 'none', activityTypes: ['preview'],
  open: (ctx) => {
    ctx.openWorkspace('edit');
    setTimeout(() => document.querySelector<HTMLElement>('[data-testid="preview3s-button"]')?.focus(), 150);
  },
});
