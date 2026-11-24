// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { ReactFlowProvider } from '@xyflow/react';
import type { StepData } from '../WorkflowStepList';
import { AgentNode } from './AgentNode';
import { VideoAnalyzeNode } from './VideoAnalyzeNode';
import { VideoCompileNode } from './VideoCompileNode';
import { createDefaultVideoAnalyzeStepConfig } from '../VideoAnalyzeStepConfig';
import { createDefaultVideoCompileStepConfig } from '../VideoCompileStepConfig';
import { ThumbnailGenerateNode } from './ThumbnailGenerateNode';
import { createDefaultThumbnailGenerateStepConfig } from '../ThumbnailGenerateStepConfig';
import { TimelineRenderNode } from './TimelineRenderNode';
import { createDefaultTimelineRenderStepConfig } from '../TimelineRenderStepConfig';
import { DesignSystemNode } from './DesignSystemNode';
import { createDefaultDesignSystemStepConfig } from '../DesignSystemStepConfig';
import { ExtractFrameNode } from './ExtractFrameNode';
import { PinInsertNode } from './PinInsertNode';
import { createDefaultPinInsertStepConfig } from '../PinInsertStepConfig';
import { createDefaultExtractFrameStepConfig } from '../ExtractFrameStepConfig';
import { AudioGenerateNode } from './AudioGenerateNode';
import { createDefaultAudioGenerateStepConfig } from '../AudioGenerateStepConfig';
import { ImageGenerateNode } from './ImageGenerateNode';
import { createDefaultImageGenerateStepConfig } from '../ImageGenerateStepConfig';

/**
 * Customer e2e findings 19/23/29: canvas cards say what a step DOES in plain language, carry no
 * inline settings form (settings open in the side drawer), and show no internal jargon.
 */

const { MOCK_AGENTS, MOCK_FILES } = vi.hoisted(() => ({
  MOCK_AGENTS: [
    { id: 'nw', agentType: 'NarrationWriter', name: 'NarrationWriter' },
    { id: 'ms', agentType: 'MusicSupervisor', name: 'MusicSupervisor' },
  ],
  MOCK_FILES: { current: [] as { id: string; mimeType: string; originalFileName: string }[] },
}));

vi.mock('@/lib/hooks/use-agents', () => ({ useAgents: () => ({ data: MOCK_AGENTS, isLoading: false }) }));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: MOCK_FILES.current }) }));

afterEach(() => cleanup());

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
});

function makeStep(overrides: Partial<StepData>): StepData {
  return {
    id: 's1', label: '', agentDefinitionId: '', stepType: 'Agent',
    conditionExpression: null, loopSourceExpression: null, loopTargetStepOrder: null, maxIterations: 3, minScore: null,
    inputMappingJson: null, agentInputContextMode: null, cacheMode: null, selectedPriorStepOrders: [],
    trueBranchStepOrder: null, falseBranchStepOrder: null, parallelAgentIds: [],
    extractConfig: null, videoAnalyzeConfig: null, videoCompileConfig: null, videoGenerateConfig: null, voiceoverConfig: null,
    thumbnailGenerateConfig: null,
    ...overrides,
  } as StepData;
}

function renderNode(Node: React.ComponentType<{ data: never }>, step: StepData, extra: Record<string, unknown> = {}) {
  const onOpenSettings = vi.fn();
  render(
    <MantineProvider>
      <ReactFlowProvider>
        <Node data={{ step, stepNumber: 2, allSteps: [step], currentStepIndex: 0, projectId: 'p1', onOpenSettings, onChange: vi.fn(), onRemove: vi.fn(), ...extra } as never} />
      </ReactFlowProvider>
    </MantineProvider>,
  );
  return { onOpenSettings };
}

describe('AgentNode', () => {
  it("is labelled with the agent's plain-language role, not 'AGENT'", () => {
    renderNode(AgentNode, makeStep({ agentDefinitionId: 'nw' }));
    expect(screen.getByTestId('step-role').textContent).toBe('Writes the narration');
    expect(screen.queryByText('AGENT')).toBeNull();
  });

  it('opens the settings drawer from its Settings button instead of growing an inline form', () => {
    const { onOpenSettings } = renderNode(AgentNode, makeStep({ agentDefinitionId: 'nw' }));
    // The old inline form's fields are gone from the card.
    expect(screen.queryByText('Agent Context')).toBeNull();
    expect(screen.queryByText('Advanced Settings')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Open settings for step 2' }));
    expect(onOpenSettings).toHaveBeenCalledWith('s1');
  });

  it('warns on a music step when the project has no audio files', () => {
    MOCK_FILES.current = [{ id: 'v', mimeType: 'video/mp4', originalFileName: 'a.mp4' }];
    renderNode(AgentNode, makeStep({ agentDefinitionId: 'ms' }));
    expect(screen.getByText(/Your project has no music files/)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Files' }).getAttribute('href')).toBe('/projects/p1?tab=files');
  });

  it('shows no audio notice once the project has an audio file', () => {
    MOCK_FILES.current = [{ id: 'a', mimeType: 'audio/mpeg', originalFileName: 'bed.mp3' }];
    renderNode(AgentNode, makeStep({ agentDefinitionId: 'ms' }));
    expect(screen.queryByText(/Your project has no music files/)).toBeNull();
  });
});

describe('VideoAnalyzeNode / VideoCompileNode', () => {
  it('summarises the analyze step in plain words, without engineer jargon', () => {
    renderNode(VideoAnalyzeNode, makeStep({
      stepType: 'VideoAnalyze',
      videoAnalyzeConfig: {
        ...createDefaultVideoAnalyzeStepConfig(),
        source: undefined,
        sources: [
          { kind: 'ProjectFile', projectFileId: 'f1', stepOrder: null },
          { kind: 'ProjectFile', projectFileId: 'f2', stepOrder: null },
        ],
      },
    }));
    expect(screen.getByTestId('step-role').textContent).toBe('Watches your footage');
    expect(screen.getByText('2 clips')).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/ffmpeg|Deterministic|ASR|PROJECTFILE/i);
  });

  it('flags a first-step analyze that still points at a "previous step" as having no video', () => {
    renderNode(VideoAnalyzeNode, makeStep({ stepType: 'VideoAnalyze', videoAnalyzeConfig: createDefaultVideoAnalyzeStepConfig() }), { stepNumber: 1 });
    expect(screen.getByText('No video chosen')).toBeTruthy();
    expect(screen.getByText(/Choose which video to use/)).toBeTruthy();
  });

  it('shows the chosen output format and target length instead of codec settings', () => {
    renderNode(VideoCompileNode, makeStep({
      stepType: 'VideoCompile',
      videoCompileConfig: { ...createDefaultVideoCompileStepConfig(), outputFormat: 'Vertical', targetDurationSec: 15 },
    }));
    expect(screen.getByText('Vertical 9:16')).toBeTruthy();
    expect(screen.getByText('About 15 s')).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/crf|Reencode/i);
  });
});

describe('ThumbnailGenerateNode', () => {
  it('summarises the thumbnail in plain words and flags the missing footage step', () => {
    renderNode(ThumbnailGenerateNode, makeStep({
      stepType: 'ThumbnailGenerate',
      thumbnailGenerateConfig: createDefaultThumbnailGenerateStepConfig(),
    }), { allSteps: [], currentStepIndex: 0 });

    expect(screen.getByTestId('step-role').textContent).toBe('Makes a thumbnail image');
    expect(screen.getByText('1280×720')).toBeTruthy();
    expect(screen.getByText('Best moment')).toBeTruthy();
    // Nothing to take a frame from: said on the card, not only when the run reaches this step.
    expect(screen.getByText(/Add a Watch footage step above this one/)).toBeTruthy();
    // The chips are the customer's words — never the ffmpeg/encoder vocabulary behind them.
    expect(document.body.textContent).not.toMatch(/jpeg|q:v|drawtext|ffmpeg/i);
  });

  it('shows the chosen moment and overlay instead of the Auto summary, with no warning once footage precedes it', () => {
    const analyzeStep = makeStep({ stepType: 'VideoAnalyze' });
    const thumbnailStep = makeStep({
      stepType: 'ThumbnailGenerate',
      thumbnailGenerateConfig: {
        ...createDefaultThumbnailGenerateStepConfig(),
        selection: 'AtTime',
        atTimeSec: 12.5,
        overlayPosition: 'LowerThird',
        overlayText: 'Watch the launch',
      },
    });

    renderNode(ThumbnailGenerateNode, thumbnailStep, {
      allSteps: [analyzeStep, thumbnailStep],
      currentStepIndex: 1,
    });

    expect(screen.getByText('At 12.5 s')).toBeTruthy();
    expect(screen.getByText('Text')).toBeTruthy();
    expect(screen.queryByText('Best moment')).toBeNull();
    expect(screen.queryByText(/Add a Watch footage step above this one/)).toBeNull();
  });
});

describe('TimelineRenderNode', () => {
  it('summarises the delivery in plain words and flags a missing timeline', () => {
    renderNode(TimelineRenderNode, makeStep({
      stepType: 'TimelineRender',
      timelineRenderConfig: createDefaultTimelineRenderStepConfig(),
    }), { allSteps: [], currentStepIndex: 0 });

    expect(screen.getByTestId('step-role').textContent).toBe('Renders your edited timeline');
    expect(screen.getByText('HD 1080p (16:9)')).toBeTruthy();
    expect(screen.getByText('timeline-render.mp4')).toBeTruthy();
    expect(screen.getByText(/Choose the editor timeline to render/)).toBeTruthy();
    // Customer words only — never the encoder vocabulary behind them.
    expect(document.body.textContent).not.toMatch(/crf|libx264|loudnorm|ffmpeg/i);
  });

  it('shows the pinned revision and loudness chips with no warning once a timeline is chosen', () => {
    renderNode(TimelineRenderNode, makeStep({
      stepType: 'TimelineRender',
      timelineRenderConfig: {
        ...createDefaultTimelineRenderStepConfig(),
        editTimelineId: '1b2c3d4e-5f60-4711-8a92-3c4d5e6f7001',
        revision: 12,
        preset: 'Vertical9x16',
        loudness: { targetLufs: -16, truePeakDbtp: -1.5, loudnessRange: 11 },
      },
    }), { allSteps: [], currentStepIndex: 0 });

    expect(screen.getByText('Vertical (9:16, phones)')).toBeTruthy();
    expect(screen.getByText('Revision 12')).toBeTruthy();
    expect(screen.getByText('Loudness normalised')).toBeTruthy();
    expect(screen.queryByText(/Choose the editor timeline to render/)).toBeNull();
  });
});

describe('DesignSystemNode', () => {
  it('summarises the read in plain words and carries no internal jargon', () => {
    renderNode(DesignSystemNode, makeStep({
      stepType: 'DesignSystem',
      designSystemConfig: createDefaultDesignSystemStepConfig(),
    }), { allSteps: [], currentStepIndex: 0 });

    expect(screen.getByTestId('step-role').textContent).toBe('Builds your brand and product profile');
    expect(screen.getByText('Reads design files')).toBeTruthy();
    // Customer words only — never the engine vocabulary behind them.
    expect(document.body.textContent).not.toMatch(/design system|workflow engine|ffmpeg/i);
  });

  it('flags a Compose with no read step, and stays quiet once one is chosen', () => {
    renderNode(DesignSystemNode, makeStep({
      stepType: 'DesignSystem',
      designSystemConfig: { op: 'Compose', version: 1, compose: { parseStepOrder: 0, ingest: true } },
    }), { allSteps: [], currentStepIndex: 0 });
    expect(screen.getByText(/Choose which earlier step read your files/)).toBeTruthy();

    cleanup();
    renderNode(DesignSystemNode, makeStep({
      stepType: 'DesignSystem',
      designSystemConfig: { op: 'Compose', version: 1, compose: { parseStepOrder: 1, ingest: true } },
    }), { allSteps: [], currentStepIndex: 0 });
    expect(screen.queryByText(/Choose which earlier step read your files/)).toBeNull();
    expect(screen.getByText('Read step 1')).toBeTruthy();
  });
});

describe('ExtractFrameNode', () => {
  it('summarises which frame and format, and carries no internal jargon', () => {
    renderNode(ExtractFrameNode, makeStep({
      stepType: 'ExtractFrame',
      extractFrameConfig: createDefaultExtractFrameStepConfig(),
    }), { allSteps: [], currentStepIndex: 1 });
    expect(screen.getByTestId('step-role').textContent).toBe('Grabs a frame from a video');
    expect(screen.getByText('Last frame')).toBeTruthy();
    expect(screen.getByText('PNG')).toBeTruthy();
    expect(screen.getByText('Saved to Files')).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/ffmpeg|ExtractFrame/);
  });

  it('shows a chosen moment', () => {
    renderNode(ExtractFrameNode, makeStep({
      stepType: 'ExtractFrame',
      extractFrameConfig: { ...createDefaultExtractFrameStepConfig(), at: 'Time', atSec: 2.5, format: 'jpeg' },
    }), { allSteps: [], currentStepIndex: 1 });
    expect(screen.getByText('At 2.5 s')).toBeTruthy();
    expect(screen.getByText('JPEG')).toBeTruthy();
  });

  it('flags a source that names no video, and "previous step" on a first step', () => {
    renderNode(ExtractFrameNode, makeStep({
      stepType: 'ExtractFrame',
      extractFrameConfig: { ...createDefaultExtractFrameStepConfig(), source: { kind: 'ProjectFile', projectFileId: null, stepOrder: null } },
    }), { allSteps: [], currentStepIndex: 0 });
    expect(screen.getByText('Choose which video the frame is taken from.')).toBeTruthy();

    cleanup();
    renderNode(ExtractFrameNode, makeStep({
      stepType: 'ExtractFrame',
      extractFrameConfig: createDefaultExtractFrameStepConfig(),
    }), { allSteps: [], currentStepIndex: 0 });
    expect(screen.getByText(/Nothing comes before this step/)).toBeTruthy();

    cleanup();
    renderNode(ExtractFrameNode, makeStep({
      stepType: 'ExtractFrame',
      extractFrameConfig: createDefaultExtractFrameStepConfig(),
    }), { allSteps: [], currentStepIndex: 1 });
    expect(screen.queryByText(/Nothing comes before this step/)).toBeNull();
  });
});

describe('PinInsertNode', () => {
  it('summarises the pins in plain words and flags content that is not chosen yet', () => {
    renderNode(PinInsertNode, makeStep({
      stepType: 'PinInsert',
      pinInsertConfig: createDefaultPinInsertStepConfig(),
    }), { allSteps: [], currentStepIndex: 2 });
    expect(screen.getByTestId('step-role').textContent).toBe('Puts content on the tracked screens');
    expect(screen.getByText('1 pin')).toBeTruthy();
    expect(screen.getByText('Every screen')).toBeTruthy();
    expect(screen.getByText('Choose what goes on each screen.')).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/PinInsert|regionId/);

    cleanup();
    renderNode(PinInsertNode, makeStep({
      stepType: 'PinInsert',
      pinInsertConfig: { ...createDefaultPinInsertStepConfig(), pins: [{ regionId: 'r0', content: { from: 'Step', stepOrder: 2 } }] },
    }), { allSteps: [], currentStepIndex: 2 });
    expect(screen.queryByText('Choose what goes on each screen.')).toBeNull();
    expect(screen.queryByText('Every screen')).toBeNull();
  });
});

describe('ImageGenerateNode', () => {
  it('summarises the mode, the picture count and the spend limit', () => {
    renderNode(ImageGenerateNode, makeStep({
      stepType: 'ImageGenerate',
      imageGenerateConfig: { ...createDefaultImageGenerateStepConfig(), prompt: 'a lighthouse', variations: 3, maxSpendCredits: 2 },
    }), { allSteps: [], currentStepIndex: 0 });
    expect(screen.getByTestId('step-role').textContent).toBe('Generates or edits pictures');
    expect(screen.getByText('Make pictures')).toBeTruthy();
    expect(screen.getByText('3 pictures')).toBeTruthy();
    expect(screen.getByText('Max 2 credits')).toBeTruthy();
    expect(screen.queryByText('Describe the picture to make.')).toBeNull();
  });

  it('asks for a description, and for Edit the instruction, the source and the egress consent in turn', () => {
    renderNode(ImageGenerateNode, makeStep({
      stepType: 'ImageGenerate',
      imageGenerateConfig: createDefaultImageGenerateStepConfig(),
    }), { allSteps: [], currentStepIndex: 0 });
    expect(screen.getByText('Describe the picture to make.')).toBeTruthy();

    const edit = { ...createDefaultImageGenerateStepConfig(), mode: 'Edit' as const, prompt: null };
    cleanup();
    renderNode(ImageGenerateNode, makeStep({ stepType: 'ImageGenerate', imageGenerateConfig: edit }), { allSteps: [], currentStepIndex: 0 });
    expect(screen.getByText('Say what to change in the picture.')).toBeTruthy();

    cleanup();
    renderNode(ImageGenerateNode, makeStep({ stepType: 'ImageGenerate', imageGenerateConfig: { ...edit, instruction: 'night' } }), { allSteps: [], currentStepIndex: 0 });
    expect(screen.getByText('Choose the picture to edit.')).toBeTruthy();

    cleanup();
    const src = { kind: 'ProjectFile' as const, projectFileId: 'f1' };
    renderNode(ImageGenerateNode, makeStep({ stepType: 'ImageGenerate', imageGenerateConfig: { ...edit, instruction: 'night', source: src } }), { allSteps: [], currentStepIndex: 0 });
    expect(screen.getByText('Allow sending the picture away to edit it.')).toBeTruthy();

    cleanup();
    renderNode(ImageGenerateNode, makeStep({ stepType: 'ImageGenerate', imageGenerateConfig: { ...edit, instruction: 'night', source: src, allowSourceMediaEgress: true } }), { allSteps: [], currentStepIndex: 0 });
    expect(screen.queryByText('Allow sending the picture away to edit it.')).toBeNull();
  });
});

describe('AudioGenerateNode', () => {
  it('summarises kind, length and the spend limit; a missing limit reads as one', () => {
    renderNode(AudioGenerateNode, makeStep({
      stepType: 'AudioGenerate',
      audioGenerateConfig: { ...createDefaultAudioGenerateStepConfig(), prompt: 'warm guitar', takes: 2 },
    }), { allSteps: [], currentStepIndex: 0 });
    expect(screen.getByTestId('step-role').textContent).toBe('Generates music or sound effects');
    expect(screen.getByText('Music')).toBeTruthy();
    expect(screen.getByText('30 s')).toBeTruthy();
    expect(screen.getByText('2 takes')).toBeTruthy();
    expect(screen.getByText('No spending limit')).toBeTruthy();

    cleanup();
    renderNode(AudioGenerateNode, makeStep({
      stepType: 'AudioGenerate',
      audioGenerateConfig: { ...createDefaultAudioGenerateStepConfig(), kind: 'SoundEffect', durationSec: 4, prompt: 'creak', maxSpendCredits: 1.5 },
    }), { allSteps: [], currentStepIndex: 0 });
    expect(screen.getByText('Sound effect')).toBeTruthy();
    expect(screen.getByText('Max 1.5 credits')).toBeTruthy();
  });

  it('asks for a description unless a composition plan stands in for it', () => {
    renderNode(AudioGenerateNode, makeStep({
      stepType: 'AudioGenerate',
      audioGenerateConfig: createDefaultAudioGenerateStepConfig(),
    }), { allSteps: [], currentStepIndex: 0 });
    expect(screen.getByText('Describe the audio to generate.')).toBeTruthy();

    cleanup();
    renderNode(AudioGenerateNode, makeStep({
      stepType: 'AudioGenerate',
      audioGenerateConfig: { ...createDefaultAudioGenerateStepConfig(), compositionPlanJson: '{"sections":[]}', durationSec: 0 },
    }), { allSteps: [], currentStepIndex: 0 });
    expect(screen.queryByText('Describe the audio to generate.')).toBeNull();
    expect(screen.queryByText('0 s')).toBeNull();
  });
});
