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
  ThemeIcon,
  Flex,
  Alert,
} from '@mantine/core';
import { IconGripVertical } from '@tabler/icons-react';
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
import { ConfigCloseContext, FieldErrorProvider, FieldDiffProvider, AskAssistantButton, fdFrom, useFdNotes, assistantPrompt } from './config-kit';
import type { StepFieldDiff } from './field-diff';
import type { StepType, CacheMode } from '@/lib/types/workflow';
import restyle from './graph-restyle.module.css';

interface StepConfigSheetProps {
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

// Internal component: shared body content for both desktop Drawer and mobile bottom sheet
function StepConfigBody({
  step,
  onChange,
  onClose,
  firstFieldRef,
  previousSteps = [],
  allSteps = [],
  currentStepIndex = 0,
  projectId,
  fieldDiff = null,
}: {
  step: StepData;
  onChange: (updates: Partial<StepData>) => void;
  onClose?: () => void;
  firstFieldRef: React.RefObject<HTMLInputElement | HTMLTextAreaElement | null>;
  previousSteps?: StepData[];
  allSteps?: StepData[];
  currentStepIndex?: number;
  projectId?: string;
  fieldDiff?: StepFieldDiff | null;
}): React.ReactElement | null {
  const { data: agents, isLoading: agentsLoading } = useAgents();
  const t = useT('wfSteps.sheet');
  const fd = fdFrom(fieldDiff, undefined, useFdNotes());
  const roomType = isRoomStepType(step.stepType) ? step.stepType : null;
  const effectiveCacheMode: CacheMode = step.cacheMode ?? 'Default';

  // Show loading state while agents are loading (for Agent steps and steps that need agent data)
  const isLoadingAgentData = agentsLoading && (
    step.stepType === 'Agent' ||
    step.stepType === 'Parallel' ||
    step.stepType === 'Extract' ||
    step.stepType === 'VideoAnalyze' ||
    step.stepType === 'VideoCompile' ||
    step.stepType === 'VideoGenerate' ||
    step.stepType === 'Voiceover' ||
    step.stepType === 'ThumbnailGenerate' ||
    step.stepType === 'TimelineRender' ||
    step.stepType === 'DesignSystem'
  );

  if (isLoadingAgentData) {
    return (
      <Center py={40}>
        <Loader size="sm" />
      </Center>
    );
  }

  // Helper to render field grouping for different step types
  const renderLabelField = () => (
    <TextInput
      // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
      {...fd('col.label')}
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

  const renderStepTypeConfig = () => {
    // Agent step: single fieldset with label, agent picker, input mapping, cache mode
    if (step.stepType === 'Agent') {
      return (
        <Fieldset legend={t('legend.agent')}>
          <Stack gap="sm">
            <TextInput
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
            <AskAssistantButton prompt={assistantPrompt(currentStepIndex, 'Agent', msg('wfSteps.prompts.agent'))} />
            {renderInputMappingField()}
            {renderCacheModeField()}
            <AgentStepOptions step={step} onChange={onChange} projectId={projectId} />
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
          <ForEachStepConfig
            loopSourceExpression={step.loopSourceExpression}
            maxIterations={step.maxIterations}
            onChange={onChange}
            previousSteps={previousSteps}
            currentStepIndex={currentStepIndex}
          />
        </Fieldset>
      );
    }

    // ReviewLoop: single fieldset "Review loop"
    if (step.stepType === 'ReviewLoop') {
      return (
        <Fieldset legend={t('legend.reviewLoop')}>
          <ReviewLoopStepConfig
            minScore={step.minScore}
            maxIterations={step.maxIterations}
            loopTargetStepOrder={step.loopTargetStepOrder}
            reviewLoopConfig={step.reviewLoopConfig}
            onChange={onChange}
            previousSteps={previousSteps}
            currentStepIndex={currentStepIndex}
          />
        </Fieldset>
      );
    }

    // Parallel: single fieldset "Parallel agents"
    if (step.stepType === 'Parallel') {
      return (
        <Fieldset legend={t('legend.parallel')}>
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
        <ExtractStepConfig
          config={{ ...createDefaultExtractStepConfig(), ...step.extractConfig }}
          onChange={(extractConfig) => onChange({ extractConfig })}
          allSteps={allSteps}
          currentStepIndex={currentStepIndex}
        />
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

    // Voiceover: mirroring VideoAnalyze/VideoCompile pattern
    if (step.stepType === 'Voiceover') {
      return (
        <VoiceoverStepConfig
          config={{ ...createDefaultVoiceoverStepConfig(), ...step.voiceoverConfig }}
          onChange={(voiceoverConfig) => onChange({ voiceoverConfig })}
          allSteps={allSteps}
          currentStepIndex={currentStepIndex}
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

  return (
    <ConfigCloseContext.Provider value={onClose ?? null}>
    <FieldDiffProvider diff={fieldDiff}>
    <Stack gap="md">
      <>
        {fieldDiff && fieldDiff.size > 0 && (
          <Alert color="orange" variant="light" p="xs" title={t('proposes', { count: fieldDiff.size })}>
            <Text size="xs">{t('proposesNote')}</Text>
          </Alert>
        )}
        {renderStepTypeConfig()}

        {/* Show common fields (label, input mapping, cache mode) for non-Agent steps */}
        {step.stepType !== 'Agent' && (
          <Fieldset legend={t('legend.stepSettings')}>
            <Stack gap="sm">
              <TextInput
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
    </Stack>
    </FieldDiffProvider>
    </ConfigCloseContext.Provider>
  );
}

export function StepConfigSheet({
  step,
  opened,
  onClose,
  onChange,
  previousSteps = [],
  allSteps = [],
  currentStepIndex = 0,
  projectId,
  fieldDiff,
}: StepConfigSheetProps): React.ReactElement {
  const t = useT('wfSteps.sheet');
  const triggerButtonRef = useRef<HTMLButtonElement>(null);
  const firstFieldRef = useRef<HTMLInputElement | HTMLTextAreaElement | null>(null);

  const prefersReducedMotion = typeof window !== 'undefined'
    ? window.matchMedia('(prefers-reduced-motion: reduce)').matches
    : false;

  // Focus management: when sheet opens, focus first field; when closes, return to trigger button
  useEffect(() => {
    if (opened) {
      // Focus the first field when sheet opens
      setTimeout(() => {
        if (firstFieldRef.current) {
          firstFieldRef.current.focus();
        }
      }, prefersReducedMotion ? 0 : 250);
    } else {
      // Return focus to trigger button when sheet closes
      triggerButtonRef.current?.focus();
    }
  }, [opened, prefersReducedMotion]);

  // Prevent body scroll when sheet is open by using Drawer's own scroll management
  const duration = prefersReducedMotion ? 0 : 250;

  return (
    <Drawer
      opened={opened}
      onClose={onClose}
      position="bottom"
      size="90%"
      transitionProps={{
        duration: duration,
        transition: 'slide-up',
      }}
      withCloseButton={false}
      classNames={{ content: restyle.sheetContent, body: restyle.sheetBody }}
    >
      <Stack gap="md" className={restyle.sheetStack}>
        {/* Custom header with drag handle and Done button */}
        <Flex justify="space-between" align="center" gap="md" pb="md" className={restyle.sheetHeader} mih={44}>
          <Group gap="xs">
            <ThemeIcon variant="subtle" size="lg" radius="md" aria-label={t('dragHandle')} w={44} h={44}>
              <IconGripVertical size={20} />
            </ThemeIcon>
            <Text fw={500} size="sm">
              {t('configure', { type: STEP_TYPE_LABELS[step.stepType] })}
            </Text>
          </Group>
          <Button
            ref={triggerButtonRef}
            variant="subtle"
            size="sm"
            onClick={onClose}
            miw={44}
            mih={44}
            px="md"
          >
            {t('done')}
          </Button>
        </Flex>

        {/* Config body content */}
        <Box style={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
          <StepConfigBody
            step={step}
            onChange={onChange}
            onClose={onClose}
            firstFieldRef={firstFieldRef}
            previousSteps={previousSteps}
            allSteps={allSteps}
            currentStepIndex={currentStepIndex}
            projectId={projectId}
            fieldDiff={fieldDiff}
          />
        </Box>
      </Stack>
    </Drawer>
  );
}
