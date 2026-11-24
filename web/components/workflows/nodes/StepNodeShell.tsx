'use client';

import { Handle, Position } from '@xyflow/react';
import { Card, Group, Text, ActionIcon, Badge, Stack, TextInput, Tooltip, Button } from '@mantine/core';
import { IconTrash, IconSettings } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { useStepTypeText } from '../step-type-i18n';
import type { StepData } from '../WorkflowStepList';
import restyle from '../graph-restyle.module.css';

/**
 * Handlers and display state every canvas node receives. `expanded`/`pinned` and their callbacks
 * are kept on the type for compatibility with the graph derivation, but a node no longer grows an
 * inline settings form: settings live in the side drawer (`StepConfigPanel`), opened by clicking
 * the card or its Settings button. A tall in-node form used to overlap the following steps and
 * could pan the whole canvas off-screen.
 */
export interface BaseStepNodeData {
  step: StepData;
  stepNumber: number;
  allSteps?: StepData[];
  currentStepIndex?: number;
  projectId?: string;
  expanded?: boolean;
  pinned?: boolean;
  onExpandChange?: (stepId: string | null) => void;
  onTogglePin?: (stepId: string) => void;
  /** Opens this step's settings drawer. Injected by the FlowchartBuilder. */
  onOpenSettings?: (stepId: string) => void;
  onChange: (updates: Partial<StepData>) => void;
  onRemove: () => void;
}

export interface SummaryChip {
  text: string;
  /** Accessible name when the chip's visible text is terse. */
  ariaLabel?: string;
  color?: string;
}

interface StepNodeShellProps {
  data: BaseStepNodeData;
  /** Border / handle colour. */
  accentHex: string;
  /** Mantine colour of the kind badge. */
  color: string;
  icon: React.ReactNode;
  /** Short plain-language kind, e.g. "Watch footage". */
  kind: string;
  /** What this step does, in plain language — the card's main line. */
  role: string;
  /** Secondary line (e.g. the chosen agent's own name). */
  detail?: string | null;
  summary?: SummaryChip[];
  /** A problem the customer should fix before running. */
  warning?: string | null;
  labelPlaceholder?: string;
  children?: React.ReactNode;
}

export const STEP_NODE_WIDTH = 300;

export function StepNodeShell({
  data, accentHex, color, icon, kind, role, detail, summary = [], warning, labelPlaceholder, children,
}: StepNodeShellProps) {
  const t = useT('workflows.node.shell');
  const stepText = useStepTypeText();
  const { step, stepNumber, onOpenSettings, onChange, onRemove } = data;

  return (
    <>
      <Handle type="target" position={Position.Top} style={{ background: accentHex }} />
      <Card
        padding="sm"
        radius="xs"
        role="button"
        tabIndex={0}
        aria-label={t('ariaLabel', { number: stepNumber, type: stepText.label(step.stepType), label: step.label || t('untitled') })}
        data-step-kind={step.stepType}
        className={restyle.node}
        style={{
          width: STEP_NODE_WIDTH,
          border: `2px solid ${accentHex}`,
          cursor: 'var(--rb-cursor-pointer)',
        }}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            e.currentTarget.click();
          }
        }}
      >
        <Stack gap={6}>
          <Group justify="space-between" wrap="nowrap" gap="xs">
            <Group gap={8} wrap="nowrap" style={{ minWidth: 0 }}>
              <div
                aria-hidden
                style={{
                  width: 26,
                  height: 26,
                  flexShrink: 0,
                  borderRadius: '50%',
                  background: accentHex,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  color: 'white',
                  fontWeight: 700,
                  fontSize: 13,
                }}
              >
                {stepNumber}
              </div>
              <Badge size="md" variant="light" color={color} leftSection={icon} tt="none">
                {kind}
              </Badge>
            </Group>
            <Tooltip label={t('deleteStep')}>
              <ActionIcon
                color="red"
                variant="subtle"
                size="sm"
                className={restyle.touch}
                aria-label={t('deleteStepAria', { number: stepNumber })}
                onClick={(e) => {
                  e.stopPropagation();
                  onRemove();
                }}
              >
                <IconTrash size={15} />
              </ActionIcon>
            </Tooltip>
          </Group>

          <TextInput
            placeholder={labelPlaceholder ?? t('defaultNamePlaceholder')}
            aria-label={t('nameAria', { number: stepNumber })}
            value={step.label}
            onChange={(e) => onChange({ label: e.target.value })}
            size="sm"
            styles={{ input: { fontWeight: 600, fontSize: 15, border: 'none', background: 'transparent', padding: 0, minHeight: 0, height: 24 } }}
            onClick={(e) => e.stopPropagation()}
          />

          <Text size="sm" fw={500} className={restyle.muted} data-testid="step-role">{role}</Text>
          {detail && <Text size="xs" className={restyle.muted}>{detail}</Text>}

          {summary.length > 0 && (
            <Group gap={4}>
              {summary.map((chip) => (
                <Badge key={chip.text} size="sm" variant="outline" color={chip.color ?? 'gray'} tt="none" aria-label={chip.ariaLabel}>
                  {chip.text}
                </Badge>
              ))}
            </Group>
          )}

          {children}

          {warning && <Text size="xs" className={restyle.warn}>{warning}</Text>}

          <Button
            variant="light"
            color={color}
            size="compact-xs"
            className={restyle.touch}
            aria-label={t('openSettingsAria', { number: stepNumber })}
            leftSection={<IconSettings size={13} />}
            onClick={(e) => {
              e.stopPropagation();
              onOpenSettings?.(step.id);
            }}
            style={{ alignSelf: 'flex-start' }}
          >
            {t('settings')}
          </Button>
        </Stack>
      </Card>
      <Handle type="source" position={Position.Bottom} style={{ background: accentHex }} />
    </>
  );
}
