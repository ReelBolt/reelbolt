import { memo } from 'react';
import { IconPhotoAi } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { createDefaultImageGenerateStepConfig } from '../ImageGenerateStepConfig';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData, type SummaryChip } from './StepNodeShell';

export const ImageGenerateNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const config = { ...createDefaultImageGenerateStepConfig(), ...data.step.imageGenerateConfig };
  const isEdit = config.mode === 'Edit';
  const summary: SummaryChip[] = [{ text: isEdit ? tn('image.edit') : tn('image.generate') }];
  if ((config.variations ?? 1) > 1) summary.push({ text: tn('image.pictures', { count: config.variations ?? 1 }) });
  summary.push(
    (config.maxSpendCredits ?? 0) > 0
      ? { text: tn('image.maxCredits', { credits: config.maxSpendCredits }) }
      : { text: tn('image.noLimit') },
  );

  let warning: string | null = null;
  if (isEdit) {
    if (!config.instruction?.trim()) warning = tn('image.describeEdit');
    else if (!config.source) warning = tn('image.chooseSource');
    else if (!config.allowSourceMediaEgress) warning = tn('image.needsPermission');
  } else if (!config.prompt?.trim()) {
    warning = tn('image.describe');
  }

  return (
    <StepNodeShell
      data={data}
      accentHex="#1c7ed6"
      color="blue"
      icon={<IconPhotoAi size={13} />}
      kind={tn('image.kind')}
      role={stepRoleLabel('ImageGenerate')}
      labelPlaceholder={tn('image.namePlaceholder')}
      summary={summary}
      warning={warning}
    />
  );
});

ImageGenerateNode.displayName = 'ImageGenerateNode';
