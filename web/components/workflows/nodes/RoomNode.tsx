'use client';

import { memo } from 'react';
import { IconUsersGroup } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { ROOM_STEP_KINDS, isRoomStepType } from '../RoomStepConfigEditor';
import { StepNodeShell, type BaseStepNodeData } from './StepNodeShell';

/**
 * Canvas node shared by all three room step types (EditRoom / GraphicsRoom / ColorGradeRoom) — a
 * team of agents that discusses and agrees on one result. They differ only in accent colour and
 * copy, which live in `ROOM_STEP_KINDS`. Settings live in the side drawer.
 */
export const RoomNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const { step } = data;
  // Defensive: React Flow hands this node whatever `STEP_TYPE_TO_NODE_TYPE` routed here.
  if (!isRoomStepType(step.stepType)) return null;
  const meta = ROOM_STEP_KINDS[step.stepType];
  return (
    <StepNodeShell
      data={data}
      accentHex={meta.accentHex}
      color={meta.accent}
      icon={<IconUsersGroup size={13} />}
      kind={meta.badge}
      role={stepRoleLabel(step.stepType)}
      labelPlaceholder={meta.labelPlaceholder}
    />
  );
});

RoomNode.displayName = 'RoomNode';
