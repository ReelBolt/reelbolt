'use client';

import React, { useRef, useEffect } from 'react';
import {
  Drawer,
  Stack,
  Fieldset,
  Group,
  Text,
  Loader,
  Center,
  Accordion,
  TextInput,
  NumberInput,
  Textarea,
  Select,
  Button,
  Box,
  Alert,
} from '@mantine/core';
import { IconAdjustments } from '@tabler/icons-react';
import { useT, msg } from '@/i18n';
import { AgentPicker } from './AgentPicker';
import { ConditionalStepConfig } from './ConditionalStepConfig';
import { ParallelStepConfig } from './ParallelStepConfig';
import { ForEachStepConfig } from './ForEachStepConfig';
import { ReviewLoopStepConfig } from './ReviewLoopStepConfig';
import { ExtractStepConfig, createDefaultExtractStepConfig } from './ExtractStepConfig';
import { VideoAnalyzeStepConfig, createDefaultVideoAnalyzeStepConfig } from './VideoAnalyzeStepConfig';
import { VideoCompileStepConfig, createDefaultVideoCompileStepConfig } from './VideoCompileStepConfig';
import { VideoGenerateStepConfig, createDefaultVideoGenerateStepConfig } from './VideoGenerateStepConfig';
import { VoiceoverStepConfig, createDefaultVoiceoverStepConfig } from './VoiceoverStepConfig';
import { ThumbnailGenerateStepConfig, createDefaultThumbnailGenerateStepConfig } from './ThumbnailGenerateStepConfig';
import { TimelineRenderStepConfig, createDefaultTimelineRenderStepConfig } from './TimelineRenderStepConfig';
import { DesignSystemStepConfig, createDefaultDesignSystemStepConfig } from './DesignSystemStepConfig';
import { ExtractFrameStepConfig, createDefaultExtractFrameStepConfig } from './ExtractFrameStepConfig';
import { AudioGenerateStepConfig, createDefaultAudioGenerateStepConfig } from './AudioGenerateStepConfig';
import { ImageGenerateStepConfig, createDefaultImageGenerateStepConfig } from './ImageGenerateStepConfig';
import { AudioCleanupStepConfig, createDefaultAudioCleanupStepConfig } from './AudioCleanupStepConfig';
import { VideoEnhanceStepConfig, createDefaultVideoEnhanceStepConfig } from './VideoEnhanceStepConfig';
import { PinInsertStepConfig, createDefaultPinInsertStepConfig } from './PinInsertStepConfig';
import { AgentStepOptions } from './AgentStepOptions';
import {
  RoomStepConfigEditor,
  ROOM_STEP_KINDS,
  isRoomStepType,
} from './RoomStepConfigEditor';
import { SchemaDrivenStepConfigForm } from './SchemaDrivenStepConfigForm';
import { useAgents } from '@/lib/hooks/use-agents';
import { STEP_TYPE_LABELS } from '@/lib/utils/constants';
import type { StepData } from './WorkflowStepList';
import { ConfigCloseContext, FieldErrorProvider, FieldDiffProvider, AskAssistantButton, fdFrom, useFdNotes, assistantPrompt, AdvancedSection, NoAudioFilesNotice } from './config-kit';
import { stepRoleLabel, agentRoleLabel } from '@/lib/utils/agent-roles';
import type { StepFieldDiff } from './field-diff';
import { StepAppFields } from './StepAppFields';
import type { StepType, CacheMode } from '@/lib/types/workflow';

interface StepConfigPanelProps {
  step: StepData;
  opened: boolean;
  onClose: () => void;
  onChange: (updates: Partial<StepData>) => void;
  previousSteps?: StepData[];
  allSteps?: StepData[];
  currentStepIndex?: number;
  projectId?: string;
  /** Fields the assistant's pending proposal would change on this step (highlighted in place). */
  fieldDiff?: StepFieldDiff | null;
  /** Server validation messages for this step from a rejected save, and the fields they name. */
  stepErrors?: string[];
  fieldErrors?: Map<string, string> | null;
}

export function StepConfigPanel({
  step,
  opened,
  onClose,
  onChange,
  previousSteps = [],
  allSteps = [],
  currentStepIndex = 0,
  projectId,
  fieldDiff = null,
  stepErrors = [],
  fieldErrors = null,
}: StepConfigPanelProps): React.ReactElement {
  const triggerButtonRef = useRef<HTMLButtonElement>(null);
  const firstFieldRef = useRef<HTMLInputElement | HTMLTextAreaElement | null>(null);
  const { data: agents, isLoading: agentsLoading } = useAgents();
  const selectedAgent = agents?.find((a) => a.id === step.agentDefinitionId);

  const t = useT('wfSteps.panel');
  const fd = fdFrom(fieldDiff, fieldErrors, useFdNotes());
  const roomType = isRoomStepType(step.stepType) ? step.stepType : null;
  const effectiveCacheMode: CacheMode = step.cacheMode ?? 'Default';

  // Mantine's Drawer supports transitions via transitionProps
  const prefersReducedMotion = typeof window !== 'undefined'
    ? window.matchMedia('(prefers-reduced-motion: reduce)').matches
    : false;

  // Focus management: when drawer opens, focus first field; when closes, return to trigger button
  useEffect(() => {
    if (opened) {
      // Focus the first field when drawer opens
      setTimeout(() => {
        if (firstFieldRef.current) {
          firstFieldRef.current.focus();
        }
      }, prefersReducedMotion ? 0 : 200);
    } else {
      // Return focus to trigger button when drawer closes
      triggerButtonRef.current?.focus();
    }
  }, [opened, prefersReducedMotion]);

  // Helper to render field grouping for different step types
  const renderStepTypeConfig = () => {
    // Define a helper for common text input field
    const renderLabelField = () => (
      <TextInput
        label={t('stepLabel.label')}
        placeholder={t('stepLabel.placeholder')}
        value={step.label}
        onChange={(e) => onChange({ label: e.target.value })}
        size="sm"
      />
    );

    const renderInputMappingField = () => (
      <Textarea
        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
        {...fd('col.inputMappingJson')}
        label={t('inputMapping.label')}
        description={t('inputMapping.description')}
        // eslint-disable-next-line i18next/no-literal-string -- syntax example, not copy
        placeholder='{"input": "{{steps.1.output}}"}'
        value={step.inputMappingJson || ''}
        onChange={(e) => onChange({ inputMappingJson: e.target.value || null })}
        size="sm"
        rows={3}
      />
    );

    const renderCacheModeField = () => (
      <Select
        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
        {...fd('col.cacheMode')}
        label={t('cache.label')}
        description={t('cache.description')}
        size="sm"
        value={effectiveCacheMode}
        data={[
          { value: 'Default', label: t('cache.Default') },
          { value: 'Always', label: t('cache.Always') },
          { value: 'Never', label: t('cache.Never') },
        ]}
        onChange={(value) => {
          if (!value) return;
          const nextMode = value as CacheMode;
          onChange({ cacheMode: nextMode === 'Default' ? null : nextMode });
        }}
      />
    );

    // Agent step: label + agent picker up front; input mapping and caching behind "Advanced".
    if (step.stepType === 'Agent') {
      return (
        <Fieldset legend={t('legend.agent')}>
          <Stack gap="sm">
            <TextInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('col.label')}
              ref={firstFieldRef as React.Ref<HTMLInputElement>}
              label={t('stepLabel.label')}
              placeholder={t('stepLabel.placeholder')}
              value={step.label}
              onChange={(e) => onChange({ label: e.target.value })}
              size="sm"
            />
            <AgentPicker
              value={step.agentDefinitionId}
              onChange={(agentDefinitionId) => onChange({ agentDefinitionId })}
            />
            {selectedAgent && (
              <Text size="sm" c="dimmed">{agentRoleLabel(selectedAgent.agentType)}</Text>
            )}
            {selectedAgent?.agentType === 'MusicSupervisor' && <NoAudioFilesNotice projectId={projectId} kind="music" />}
            {selectedAgent?.agentType === 'SoundDesigner' && <NoAudioFilesNotice projectId={projectId} kind="sfx" />}
            <AskAssistantButton
              prompt={assistantPrompt(currentStepIndex, 'Agent', msg('wfSteps.prompts.agent'))}
            />
            <AdvancedSection description={t('agentAdvanced')}>
              {renderInputMappingField()}
              {renderCacheModeField()}
              <AgentStepOptions step={step} onChange={onChange} projectId={projectId} />
            </AdvancedSection>
          </Stack>
        </Fieldset>
      );
    }

    // Conditional: single fieldset "Condition"
    if (step.stepType === 'Conditional') {
      return (
        <Fieldset legend={t('legend.condition')}>
          <ConditionalStepConfig
            conditionExpression={step.conditionExpression}
            trueBranchStepOrder={step.trueBranchStepOrder}
            falseBranchStepOrder={step.falseBranchStepOrder}
            conditionalConfig={step.conditionalConfig}
            onChange={onChange}
            previousSteps={previousSteps}
            currentStepIndex={currentStepIndex}
          />
        </Fieldset>
      );
    }

    // ForEach: single fieldset "Loop source"
    if (step.stepType === 'ForEach') {
      return (
        <Fieldset legend={t('legend.loopSource')}>
          <Stack gap="sm">
            <AskAssistantButton prompt={assistantPrompt(currentStepIndex, 'ForEach', msg('wfSteps.prompts.forEach'))} />
            <AgentPicker
              value={step.agentDefinitionId}
              onChange={(agentDefinitionId) => onChange({ agentDefinitionId })}
            />
            <ForEachStepConfig
              loopSourceExpression={step.loopSourceExpression}
              maxIterations={step.maxIterations}
              onChange={onChange}
              previousSteps={previousSteps}
              currentStepIndex={currentStepIndex}
            />
          </Stack>
        </Fieldset>
      );
    }

    // ReviewLoop: single fieldset "Review loop"
    if (step.stepType === 'ReviewLoop') {
      return (
        <Fieldset legend={t('legend.reviewLoop')}>
          <Stack gap="sm">
            <AgentPicker
              value={step.agentDefinitionId}
              onChange={(agentDefinitionId) => onChange({ agentDefinitionId })}
            />
            <ReviewLoopStepConfig
              minScore={step.minScore}
              maxIterations={step.maxIterations}
              loopTargetStepOrder={step.loopTargetStepOrder}
              reviewLoopConfig={step.reviewLoopConfig}
              onChange={onChange}
              previousSteps={previousSteps}
              currentStepIndex={currentStepIndex}
            />
          </Stack>
        </Fieldset>
      );
    }

    // Parallel: single fieldset "Parallel agents"
    if (step.stepType === 'Parallel') {
      return (
        <Fieldset legend={t('legend.parallel')}>
          <AskAssistantButton prompt={assistantPrompt(currentStepIndex, 'Parallel', msg('wfSteps.prompts.parallel'))} />
          <ParallelStepConfig
            parallelAgentIds={step.parallelAgentIds}
            onChange={onChange}
          />
        </Fieldset>
      );
    }

    // Extract: 3 named groups via ExtractStepConfig component
    if (step.stepType === 'Extract') {
      return (
        <>
        <AskAssistantButton prompt={assistantPrompt(currentStepIndex, 'Extract', msg('wfSteps.prompts.extract'))} />
        <ExtractStepConfig
          config={{ ...createDefaultExtractStepConfig(), ...step.extractConfig }}
          onChange={(extractConfig) => onChange({ extractConfig })}
          allSteps={allSteps}
          currentStepIndex={currentStepIndex}
        />
        </>
      );
    }

    // VideoAnalyze: 8 fieldsets with advanced sections collapsed
    if (step.stepType === 'VideoAnalyze') {
      return (
        <VideoAnalyzeStepConfig
          config={{ ...createDefaultVideoAnalyzeStepConfig(), ...step.videoAnalyzeConfig }}
          onChange={(videoAnalyzeConfig) => onChange({ videoAnalyzeConfig })}
          allSteps={allSteps}
          currentStepIndex={currentStepIndex}
          projectId={projectId}
        />
      );
    }

    // VideoCompile: fieldsets similar to VideoAnalyze
    if (step.stepType === 'VideoCompile') {
      return (
        <VideoCompileStepConfig
          config={{ ...createDefaultVideoCompileStepConfig(), ...step.videoCompileConfig }}
          onChange={(videoCompileConfig) => onChange({ videoCompileConfig })}
          allSteps={allSteps}
          currentStepIndex={currentStepIndex}
          projectId={projectId}
        />
      );
    }

    // VideoGenerate: hand-written editor (MiniMax model/resolution guidance), same as VideoCompile
    if (step.stepType === 'VideoGenerate') {
      return (
        <VideoGenerateStepConfig
          config={{ ...createDefaultVideoGenerateStepConfig(), ...step.videoGenerateConfig }}
          onChange={(videoGenerateConfig) => onChange({ videoGenerateConfig })}
          allSteps={allSteps}
          currentStepIndex={currentStepIndex}
          projectId={projectId}
        />
      );
    }

    // Voiceover: mirroring the VideoAnalyze/VideoCompile pattern
    if (step.stepType === 'Voiceover') {
      return (
        <VoiceoverStepConfig
          config={{ ...createDefaultVoiceoverStepConfig(), ...step.voiceoverConfig }}
          onChange={(voiceoverConfig) => onChange({ voiceoverConfig })}
          allSteps={allSteps}
          currentStepIndex={currentStepIndex}
          projectId={projectId}
        />
      );
    }

    // ThumbnailGenerate: deterministic frame extraction, same hand-written editor family as VideoCompile
    if (step.stepType === 'ThumbnailGenerate') {
      return (
        <ThumbnailGenerateStepConfig
          config={{ ...createDefaultThumbnailGenerateStepConfig(), ...step.thumbnailGenerateConfig }}
          onChange={(thumbnailGenerateConfig) => onChange({ thumbnailGenerateConfig })}
          allSteps={allSteps}
          currentStepIndex={currentStepIndex}
        />
      );
    }

    // TimelineRender (editor v2, P4): deterministic render of an editor timeline, hand-written like ThumbnailGenerate
    if (step.stepType === 'TimelineRender') {
      return (
        <TimelineRenderStepConfig
          config={{ ...createDefaultTimelineRenderStepConfig(), ...step.timelineRenderConfig }}
          onChange={(timelineRenderConfig) => onChange({ timelineRenderConfig })}
          projectId={projectId}
          currentStepIndex={currentStepIndex}
        />
      );
    }

    // DesignSystem: deterministic read/compose/require of the brand and product profile, same hand-written family.
    if (step.stepType === 'DesignSystem') {
      return (
        <DesignSystemStepConfig
          config={{ ...createDefaultDesignSystemStepConfig(), ...step.designSystemConfig }}
          onChange={(designSystemConfig) => onChange({ designSystemConfig })}
          allSteps={allSteps}
          currentStepIndex={currentStepIndex}
        />
      );
    }

    // ExtractFrame: deterministic single-frame grab, hand-written like ThumbnailGenerate
    if (step.stepType === 'ExtractFrame') {
      return (
        <ExtractFrameStepConfig
          config={{ ...createDefaultExtractFrameStepConfig(), ...step.extractFrameConfig }}
          onChange={(extractFrameConfig) => onChange({ extractFrameConfig })}
          allSteps={allSteps}
          currentStepIndex={currentStepIndex}
          projectId={projectId}
        />
      );
    }

    // PinInsert: deterministic pinning onto tracked screens, hand-written like ExtractFrame
    if (step.stepType === 'PinInsert') {
      return (
        <PinInsertStepConfig
          config={{ ...createDefaultPinInsertStepConfig(), ...step.pinInsertConfig }}
          onChange={(pinInsertConfig) => onChange({ pinInsertConfig })}
          allSteps={allSteps}
          currentStepIndex={currentStepIndex}
          projectId={projectId}
        />
      );
    }

    // AudioGenerate: paid music / sound-effect generation, hand-written like VideoGenerate
    if (step.stepType === 'AudioGenerate') {
      return (
        <AudioGenerateStepConfig
          config={{ ...createDefaultAudioGenerateStepConfig(), ...step.audioGenerateConfig }}
          onChange={(audioGenerateConfig) => onChange({ audioGenerateConfig })}
          currentStepIndex={currentStepIndex}
        />
      );
    }

    // ImageGenerate: paid text-to-image / instruction edit, hand-written like AudioGenerate
    if (step.stepType === 'ImageGenerate') {
      return (
        <ImageGenerateStepConfig
          config={{ ...createDefaultImageGenerateStepConfig(), ...step.imageGenerateConfig }}
          onChange={(imageGenerateConfig) => onChange({ imageGenerateConfig })}
          allSteps={allSteps}
          currentStepIndex={currentStepIndex}
          projectId={projectId}
        />
      );
    }

    // AudioCleanup: free local fixes or a hosted voice/music split, hand-written like ExtractFrame
    if (step.stepType === 'AudioCleanup') {
      return (
        <AudioCleanupStepConfig
          config={{ ...createDefaultAudioCleanupStepConfig(), ...step.audioCleanupConfig }}
          onChange={(audioCleanupConfig) => onChange({ audioCleanupConfig })}
          allSteps={allSteps}
          currentStepIndex={currentStepIndex}
          projectId={projectId}
        />
      );
    }

    // VideoEnhance: free local fixes or a hosted upscale / frame interpolation, hand-written like ExtractFrame
    if (step.stepType === 'VideoEnhance') {
      return (
        <VideoEnhanceStepConfig
          config={{ ...createDefaultVideoEnhanceStepConfig(), ...step.videoEnhanceConfig }}
          onChange={(videoEnhanceConfig) => onChange({ videoEnhanceConfig })}
          allSteps={allSteps}
          currentStepIndex={currentStepIndex}
          projectId={projectId}
        />
      );
    }

    // Room types: single group per room type
    if (roomType) {
      return (
        <Fieldset legend={t('legend.room', { type: STEP_TYPE_LABELS[roomType] })}>
          <RoomStepConfigEditor
            stepType={roomType}
            configJson={step[ROOM_STEP_KINDS[roomType].configField]}
            onChange={(json) => onChange({ [ROOM_STEP_KINDS[roomType].configField]: json })}
            allSteps={allSteps}
            currentStepIndex={currentStepIndex}
          />
        </Fieldset>
      );
    }

    return null;
  };

  // Show loading state while agents are loading (for Agent steps and steps that need agent data)
  const isLoadingAgentData = agentsLoading && (
    step.stepType === 'Agent' ||
    step.stepType === 'Parallel' ||
    step.stepType === 'Extract' ||
    step.stepType === 'VideoAnalyze' ||
    step.stepType === 'VideoCompile' ||
    step.stepType === 'VideoGenerate' ||
    step.stepType === 'ThumbnailGenerate' ||
    step.stepType === 'TimelineRender' ||
    step.stepType === 'DesignSystem'
  );

  return (
    <Drawer
      opened={opened}
      onClose={onClose}
      title={t('title', { n: currentStepIndex + 1, role: stepRoleLabel(step.stepType, selectedAgent?.agentType) })}
      position="right"
      size="lg"
      transitionProps={{
        duration: prefersReducedMotion ? 0 : 250,
        transition: 'slide-left',
      }}
    >
      <ConfigCloseContext.Provider value={onClose}>
      <FieldErrorProvider errors={fieldErrors}>
      <FieldDiffProvider diff={fieldDiff}>
      <Stack gap="md">
        {stepErrors.length > 0 && (
          <Alert color="red" variant="light" p="xs" title={t('rejected')}>
            <Stack gap={2}>
              {stepErrors.map((m) => <Text key={m} size="xs">{m}</Text>)}
            </Stack>
          </Alert>
        )}
        {fieldDiff && fieldDiff.size > 0 && (
          <Alert color="orange" variant="light" p="xs" title={t('proposes', { count: fieldDiff.size })}>
            <Text size="xs">{t('proposesNote')}</Text>
          </Alert>
        )}
        {isLoadingAgentData ? (
          <Center py={40}>
            <Loader size="sm" />
          </Center>
        ) : (
          <>
            {renderStepTypeConfig()}

            {/* App inputs on: the settings of this step the app page may fill. Renders nothing otherwise. */}
            <StepAppFields step={step} onChange={onChange} />

            {/* Show common fields (label, input mapping, cache mode) for non-Agent steps */}
            {step.stepType !== 'Agent' && (
              <Fieldset legend={t('legend.stepSettings')}>
                <Stack gap="sm">
                  <TextInput
                    // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                    {...fd('col.label')}
                    ref={firstFieldRef as React.Ref<HTMLInputElement>}
                    label={t('stepLabel.label')}
                    placeholder={t('stepLabel.placeholder')}
                    value={step.label}
                    onChange={(e) => onChange({ label: e.target.value })}
                    size="sm"
                  />
                </Stack>
              </Fieldset>
            )}
          </>
        )}
      </Stack>
      </FieldDiffProvider>
      </FieldErrorProvider>
      </ConfigCloseContext.Provider>
    </Drawer>
  );
}
