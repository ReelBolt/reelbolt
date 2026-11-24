'use client';

import { Modal, Stack, Button, Text, ScrollArea } from '@mantine/core';
import { IconRobot, IconGitBranch, IconRepeat, IconStarFilled, IconLayoutColumns, IconFilterCog, IconWaveSine, IconScissors, IconWand, IconMicrophone, IconUsersGroup, IconTypography, IconPalette, IconPhoto, IconPhotoAi, IconPencil, IconMovie, IconColorSwatch, IconCamera, IconMusic, IconVolume2, IconZoomIn, IconDeviceTv } from '@tabler/icons-react';
import { useT } from '@/i18n';
import type { StepType } from '@/lib/types/workflow';
import { STEP_TYPE_COLORS, isAuthorableStepType } from '@/lib/utils/constants';
import { useStepTypeText } from './step-type-i18n';

interface AddStepModalProps {
  opened: boolean;
  onClose: () => void;
  onAdd: (stepType: StepType) => void;
  /**
   * True while the builder is still fetching the agent list. Extract/VideoAnalyze/VideoCompile and
   * the three room step types auto-assign a built-in placeholder agent id from that list at the
   * moment they're added — adding one before the fetch resolves would bake in a blank id that the
   * create-workflow submit guard would then reject with a confusing "requires an agent" (found by
   * Copilot review). Disable just those step types until the list is in, rather than the whole
   * modal.
   */
  nonLlmStepsDisabled?: boolean;
}

const NON_LLM_STEP_TYPES: ReadonlySet<StepType> = new Set([
  'Extract', 'VideoAnalyze', 'VideoCompile', 'VideoGenerate', 'Voiceover', 'EditRoom', 'GraphicsRoom', 'ColorGradeRoom',
  'ThumbnailGenerate', 'DailiesRoom', 'TimelineRender', 'DesignSystem', 'ExtractFrame', 'AudioGenerate', 'AudioCleanup',
  'VideoEnhance', 'PinInsert', 'ImageGenerate',
]);

/**
 * Picker copy per step type, keyed as a `Record<StepType, …>` rather than a hand-ordered array so
 * TypeScript refuses to compile a new `StepType` that nobody added a picker entry for — the
 * "recently added step types have no UI" failure mode this whole map exists to prevent. Labels and
 * colours come from `STEP_TYPE_LABELS`/`STEP_TYPE_COLORS`, the same source the step badge and the
 * step-type Select read, so a step never changes name or colour between picking it and seeing it.
 */
const STEP_TYPE_PICKER: Record<StepType, { icon: React.ReactNode }> = {
  Agent: {
    icon: <IconRobot size={24} />,
  },
  Conditional: {
    icon: <IconGitBranch size={24} />,
  },
  ForEach: {
    icon: <IconRepeat size={24} />,
  },
  ReviewLoop: {
    icon: <IconStarFilled size={24} />,
  },
  Parallel: {
    icon: <IconLayoutColumns size={24} />,
  },
  Extract: {
    icon: <IconFilterCog size={24} />,
  },
  VideoAnalyze: {
    icon: <IconWaveSine size={24} />,
  },
  VideoCompile: {
    icon: <IconScissors size={24} />,
  },
  VideoGenerate: {
    icon: <IconWand size={24} />,
  },
  Voiceover: {
    icon: <IconMicrophone size={24} />,
  },
  EditRoom: {
    icon: <IconUsersGroup size={24} />,
  },
  GraphicsRoom: {
    icon: <IconTypography size={24} />,
  },
  ColorGradeRoom: {
    icon: <IconPalette size={24} />,
  },
  ThumbnailGenerate: {
    icon: <IconPhoto size={24} />,
  },
  // A system step ("Request changes" on a finished video). The entry exists only to keep this map
  // exhaustive; `isAuthorableStepType` filters it out of the picker below.
  Revise: {
    icon: <IconPencil size={24} />,
  },
  // A system step too ("Design a graphic" in the picture editor); filtered out like `Revise`.
  DesignGraphic: {
    icon: <IconPhoto size={24} />,
  },
  DailiesRoom: {
    icon: <IconMovie size={24} />,
  },
  TimelineRender: {
    icon: <IconMovie size={24} />,
  },
  DesignSystem: {
    icon: <IconColorSwatch size={24} />,
  },
  ExtractFrame: {
    icon: <IconCamera size={24} />,
  },
  AudioGenerate: {
    icon: <IconMusic size={24} />,
  },
  VideoEnhance: {
    icon: <IconZoomIn size={24} />,
  },
  PinInsert: {
    icon: <IconDeviceTv size={24} />,
  },
  AudioCleanup: {
    icon: <IconVolume2 size={24} />,
  },
  ImageGenerate: {
    icon: <IconPhotoAi size={24} />,
  },
};

/** The picker's options, in map order, without the system step types an author can never add. */
const STEP_TYPE_ORDER = (Object.keys(STEP_TYPE_PICKER) as StepType[]).filter(isAuthorableStepType);

export function AddStepModal({ opened, onClose, onAdd, nonLlmStepsDisabled = false }: AddStepModalProps) {
  const t = useT('workflows.addStep');
  const stepText = useStepTypeText();
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={t('title')}
      size="lg"
      centered
      // Fourteen step types at ~72px each are taller than a short viewport: let the modal body
      // scroll rather than clipping the last options off-screen.
      scrollAreaComponent={ScrollArea.Autosize}
    >
      <Stack gap="md">
        <Text size="sm" c="dimmed">
          {t('intro')}
        </Text>

        <Stack gap="sm">
          {STEP_TYPE_ORDER.map((stepType) => {
            const entry = STEP_TYPE_PICKER[stepType];
            const disabled = nonLlmStepsDisabled && NON_LLM_STEP_TYPES.has(stepType);
            return (
              <Button
                key={stepType}
                variant="light"
                color={STEP_TYPE_COLORS[stepType]}
                size="lg"
                leftSection={entry.icon}
                onClick={() => onAdd(stepType)}
                disabled={disabled}
                styles={{
                  root: { height: 'auto', padding: '16px' },
                  inner: { justifyContent: 'flex-start' },
                  // Without this the label block is laid out as a flex item that refuses to shrink,
                  // so a long description pushes the button wider than a phone viewport.
                  label: { minWidth: 0, whiteSpace: 'normal', textAlign: 'left' },
                }}
              >
                <div>
                  <Text fw={600}>{stepText.label(stepType)}</Text>
                  <Text size="xs" c="dimmed" fw={400}>
                    {disabled ? t('loadingAgent') : stepType === 'DailiesRoom' ? t('dailiesRoomDescription') : stepText.description(stepType)}
                  </Text>
                </div>
              </Button>
            );
          })}
        </Stack>
      </Stack>
    </Modal>
  );
}
