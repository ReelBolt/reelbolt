'use client';

import { useState } from 'react';
import { Card, Group, Text, ActionIcon, TextInput, Stack, Textarea, Collapse, Button, Tooltip, SimpleGrid, Select } from '@mantine/core';
import { IconChevronUp, IconChevronDown, IconTrash, IconSettings, IconAdjustments } from '@tabler/icons-react';
import { AgentPicker } from './AgentPicker';
import { StepTypeSelector } from './StepTypeSelector';
import { StepTypeBadge } from './StepTypeBadge';
import { StepConfigSheet } from './StepConfigSheet';
import { createDefaultExtractFrameStepConfig, EXTRACT_FRAME_FIRST_STEP_SOURCE } from './ExtractFrameStepConfig';
import { createDefaultAudioGenerateStepConfig } from './AudioGenerateStepConfig';
import { createDefaultImageGenerateStepConfig } from './ImageGenerateStepConfig';
import { createDefaultAudioCleanupStepConfig, AUDIO_CLEANUP_FIRST_STEP_SOURCE } from './AudioCleanupStepConfig';
import { createDefaultVideoEnhanceStepConfig, VIDEO_ENHANCE_FIRST_STEP_SOURCE } from './VideoEnhanceStepConfig';
import { createDefaultPinInsertStepConfig } from './PinInsertStepConfig';
import {
  RoomStepConfigEditor,
  ROOM_STEP_KINDS,
  isRoomStepType,
  defaultRoomConfigPatch,
} from './RoomStepConfigEditor';
import type { StepData } from './WorkflowStepList';
import { isEditorialAgentType, type CacheMode, type StepType } from '@/lib/types/workflow';
import { useT } from '@/i18n';
import { useStepTypeText } from './step-type-i18n';
import { useAgents } from '@/lib/hooks/use-agents';
import restyle from './graph-restyle.module.css';

interface StepCardProps {
  step: StepData;
  stepNumber: number;
  allSteps: StepData[];
  currentStepIndex: number;
  projectId?: string;
  onChange: (updates: Partial<StepData>) => void;
  onRemove: () => void;
  onMoveUp: () => void;
  onMoveDown: () => void;
  canMoveUp: boolean;
  canMoveDown: boolean;
}

const INPUT_PLACEHOLDER = '{"input": "{{steps.1.output}}"}';

export function StepCard({
  step,
  stepNumber,
  allSteps,
  currentStepIndex,
  projectId,
  onChange,
  onRemove,
  onMoveUp,
  onMoveDown,
  canMoveUp,
  canMoveDown,
}: StepCardProps) {
  const t = useT('workflows.stepCard');
  const stepText = useStepTypeText();
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [configSheetOpen, setConfigSheetOpen] = useState(false);

  // Narrowed once here rather than at each use: the narrowing a type-guard gives an inline JSX
  // condition is lost inside the editor's onChange callback, which TypeScript cannot prove runs
  // before `step` changes.
  const roomType = isRoomStepType(step.stepType) ? step.stepType : null;

  // Rooms are always an editorial multi-agent deliberation; a solo Agent step's editorial-ness
  // depends on which agent is picked.
  const { data: agents } = useAgents();
  const selectedAgent = agents?.find((a) => a.id === step.agentDefinitionId);
  const isEditorial = roomType !== null || isEditorialAgentType(selectedAgent?.agentType);
  const effectiveCacheMode: CacheMode = step.cacheMode ?? 'Default';

  // Which step types have a type-specific configuration form below. Agent steps have none — the
  // agent picker beside the type select is their whole configuration.
  const hasTypeConfig = step.stepType !== 'Agent';

  const showAgentPicker =
    step.stepType !== 'Conditional' &&
    step.stepType !== 'Extract' &&
    step.stepType !== 'VideoAnalyze' &&
    step.stepType !== 'VideoCompile' &&
    step.stepType !== 'VideoGenerate' &&
    step.stepType !== 'Voiceover' &&
    step.stepType !== 'ThumbnailGenerate' &&
    step.stepType !== 'TimelineRender' &&
    step.stepType !== 'DesignSystem' &&
    step.stepType !== 'ExtractFrame' &&
    step.stepType !== 'AudioGenerate' &&
    step.stepType !== 'ImageGenerate' &&
    step.stepType !== 'AudioCleanup' &&
    step.stepType !== 'VideoEnhance' &&
    step.stepType !== 'PinInsert' &&
    roomType === null;

  // Switching a step's TYPE to a room must seed the runnable default config immediately — the
  // executor hard-fails a room step whose config JSON is null/empty, and unlike the typed
  // extract/video configs (which the submit mapper serializes from their `?? default` fallback),
  // these configs are carried as raw strings that would otherwise stay null until the user opened
  // the editor.
  //
  // Switching to Conditional/Extract/VideoAnalyze/VideoCompile/VideoGenerate must also assign the appropriate
  // placeholder agent id if currently empty, matching the pattern from handleAddStep.
  const handleStepTypeChange = (stepType: StepType) => {
    const updates: Partial<StepData> = { stepType };

    // Assign placeholder agent ids for non-agent step types that need them.
    if (step.agentDefinitionId === '' && agents) {
      const extractTransformAgentId = agents.find((a) => a.agentType === 'ExtractTransform')?.id;
      const videoTransformAgentId = agents.find((a) => a.agentType === 'VideoTransform')?.id;

      if (stepType === 'Conditional' || stepType === 'Extract' || stepType === 'Parallel' || stepType === 'DesignSystem') {
        if (extractTransformAgentId) {
          updates.agentDefinitionId = extractTransformAgentId;
        }
      } else if (stepType === 'VideoAnalyze' || stepType === 'VideoCompile' || stepType === 'VideoGenerate' || stepType === 'Voiceover' || stepType === 'ThumbnailGenerate' || stepType === 'TimelineRender' || stepType === 'ExtractFrame' || stepType === 'AudioGenerate' || stepType === 'ImageGenerate' || stepType === 'AudioCleanup' || stepType === 'VideoEnhance' || stepType === 'PinInsert') {
        if (videoTransformAgentId) {
          updates.agentDefinitionId = videoTransformAgentId;
        }
      }
    }

    // These two have no runnable shape without a config (a named video; a description), so seed the
    // default now rather than leaving the column null until the sheet is opened.
    if (stepType === 'ExtractFrame' && !step.extractFrameConfig) {
      updates.extractFrameConfig = {
        ...createDefaultExtractFrameStepConfig(),
        ...(currentStepIndex === 0 ? { source: EXTRACT_FRAME_FIRST_STEP_SOURCE } : {}),
      };
    }
    if (stepType === 'AudioGenerate' && !step.audioGenerateConfig) updates.audioGenerateConfig = createDefaultAudioGenerateStepConfig();
    if (stepType === 'ImageGenerate' && !step.imageGenerateConfig) updates.imageGenerateConfig = createDefaultImageGenerateStepConfig();
    if (stepType === 'AudioCleanup' && !step.audioCleanupConfig) {
      updates.audioCleanupConfig = {
        ...createDefaultAudioCleanupStepConfig(),
        ...(currentStepIndex === 0 ? { source: AUDIO_CLEANUP_FIRST_STEP_SOURCE } : {}),
      };
    }
    if (stepType === 'VideoEnhance' && !step.videoEnhanceConfig) {
      updates.videoEnhanceConfig = {
        ...createDefaultVideoEnhanceStepConfig(),
        ...(currentStepIndex === 0 ? { source: VIDEO_ENHANCE_FIRST_STEP_SOURCE } : {}),
      };
    }
    if (stepType === 'PinInsert' && !step.pinInsertConfig) updates.pinInsertConfig = createDefaultPinInsertStepConfig();

    if (isRoomStepType(stepType) && !step[ROOM_STEP_KINDS[stepType].configField]) {
      onChange({ ...updates, ...defaultRoomConfigPatch(stepType) });
      return;
    }

    onChange(updates);
  };

  return (
    <Card withBorder padding="sm" radius="md" className={restyle.stepCard}>
      <Stack gap="sm">
        <Group gap="sm" wrap="nowrap">
          <Stack gap={2}>
            <Tooltip label={t('moveUp')}>
              <ActionIcon
                variant="subtle"
                size="lg"
                disabled={!canMoveUp}
                onClick={onMoveUp}
                aria-label={t('moveStepUp')}
              >
                <IconChevronUp size={14} />
              </ActionIcon>
            </Tooltip>
            <Tooltip label={t('moveDown')}>
              <ActionIcon
                variant="subtle"
                size="lg"
                disabled={!canMoveDown}
                onClick={onMoveDown}
                aria-label={t('moveStepDown')}
              >
                <IconChevronDown size={14} />
              </ActionIcon>
            </Tooltip>
          </Stack>
          <Text size="sm" fw={700} c="dimmed" w={24} ta="center">{stepNumber}</Text>
          <StepTypeBadge stepType={step.stepType} />
          <div style={{ flex: 1 }} />
          <ActionIcon color="red" variant="subtle" size="lg" onClick={onRemove} aria-label={t('deleteStep')}>
            <IconTrash size={16} />
          </ActionIcon>
        </Group>

        {/* Own row rather than inline with the badge: a long step-type badge ("Color Grade Room")
            plus four controls leaves no usable width for a text input at phone widths. */}
        <TextInput
          aria-label={t('stepLabel')}
          placeholder={t('stepLabel')}
          value={step.label}
          onChange={(e) => onChange({ label: e.target.value })}
          size="sm"
        />

        {/* Two side-by-side selects are unusable at phone widths — stack them below `xs`. */}
        <SimpleGrid cols={{ base: 1, xs: showAgentPicker ? 2 : 1 }} spacing="sm">
          <StepTypeSelector value={step.stepType} onChange={handleStepTypeChange} />
          {showAgentPicker && (
            <AgentPicker value={step.agentDefinitionId} onChange={(agentDefinitionId) => onChange({ agentDefinitionId })} />
          )}
        </SimpleGrid>

        {hasTypeConfig && (
          <>
            <Button
              variant="light"
              size="compact-sm"
              leftSection={<IconAdjustments size={14} />}
              onClick={() => setConfigSheetOpen(true)}
              aria-label={t('configureAria', { number: stepNumber, type: stepText.label(step.stepType), label: step.label || t('untitled') })}
              fullWidth
            >
              {t('configure', { type: stepText.label(step.stepType) })}
            </Button>
            <StepConfigSheet
              step={step}
              opened={configSheetOpen}
              onClose={() => setConfigSheetOpen(false)}
              onChange={onChange}
              previousSteps={allSteps}
              allSteps={allSteps}
              currentStepIndex={currentStepIndex}
              projectId={projectId}
            />
          </>
        )}

        <Button
          variant="subtle"
          size="compact-xs"
          leftSection={<IconSettings size={14} />}
          onClick={() => setAdvancedOpen(!advancedOpen)}
        >
          {t('advanced')}
        </Button>
        <Collapse expanded={advancedOpen}>
          <Stack gap="xs">
            <Select
              label={t('cacheModeLabel')}
              description={t('cacheModeDescription')}
              size="sm"
              value={effectiveCacheMode}
              data={[
                { value: 'Default', label: t('cacheDefault') },
                { value: 'Always', label: t('cacheAlways') },
                { value: 'Never', label: t('cacheNever') },
              ]}
              onChange={(value) => {
                if (!value) return;
                const nextMode = value as CacheMode;
                onChange({ cacheMode: nextMode === 'Default' ? null : nextMode });
              }}
            />
            {isEditorial && effectiveCacheMode !== 'Never' && (
              <Text size="xs" style={{ color: 'light-dark(var(--mantine-color-orange-9), var(--mantine-color-orange-4))' }}>
                {t('editorialNote')}
              </Text>
            )}
            <Textarea
              label={t('inputMappingLabel')}
              description={t('inputMappingDescription')}
              placeholder={INPUT_PLACEHOLDER}
              value={step.inputMappingJson || ''}
              onChange={(e) => onChange({ inputMappingJson: e.target.value || null })}
              size="sm"
              rows={3}
            />
          </Stack>
        </Collapse>
      </Stack>
    </Card>
  );
}
