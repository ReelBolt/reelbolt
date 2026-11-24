'use client';

import { useState } from 'react';
import { Paper, Group, Text, Badge, Button } from '@mantine/core';
import { IconPlayerPlay, IconCheck, IconX, IconClock, IconActivity, IconTool, IconBrain, IconMessage2 } from '@tabler/icons-react';
import { JsonViewer } from '@/components/workflows/JsonViewer';
import type { ExecutionStreamEvent } from '@/lib/hooks/use-execution-stream';
import { useT, useFormat, useLocale, type T, type Format } from '@/i18n';
import { agentRoleLabel } from '@/lib/utils/agent-labels';
import { formatDurationLong } from '@/lib/utils/format';

export function getPayloadString(payload: Record<string, unknown>, ...keys: string[]): string {
  for (const key of keys) {
    const value = payload[key];
    if (typeof value === 'string' && value.length > 0) {
      return value;
    }
  }

  return '';
}

export function getPayloadNumber(payload: Record<string, unknown>, ...keys: string[]): number {
  for (const key of keys) {
    const value = payload[key];
    if (typeof value === 'number' && Number.isFinite(value)) {
      return value;
    }
  }

  return 0;
}

export function getEventBadgeColor(eventType: string): string {
  if (eventType === 'execution.completed') {
    return 'green';
  }
  if (eventType === 'execution.failed') {
    return 'red';
  }
  if (eventType === 'execution.running') {
    return 'blue';
  }
  if (eventType === 'step.started') {
    return 'cyan';
  }
  if (eventType === 'step.progress') {
    return 'blue';
  }
  if (eventType === 'step.tool-called') {
    return 'indigo';
  }
  if (eventType === 'step.reasoning') {
    return 'grape';
  }
  if (eventType === 'step.chat-turn') {
    return 'teal';
  }

  return 'violet';
}

/** Event types that only matter to someone debugging an agent; hidden from the default feed. */
export const TECHNICAL_EVENT_TYPES: ReadonlySet<string> = new Set(['step.tool-called', 'step.reasoning']);

export function isTechnicalEvent(event: Pick<ExecutionStreamEvent, 'type'>): boolean {
  return TECHNICAL_EVENT_TYPES.has(event.type);
}

/** Plain-language name for an event type, used where the raw `step.completed` badge used to be. */
export function getEventTypeLabel(t: T, eventType: string): string {
  switch (eventType) {
    case 'execution.running': return t('type.running');
    case 'execution.completed': return t('type.completed');
    case 'execution.failed': return t('type.failed');
    case 'step.started': return t('type.stepStarted');
    case 'step.progress': return t('type.progress');
    case 'step.tool-called': return t('type.toolCalled');
    case 'step.reasoning': return t('type.reasoning');
    case 'step.chat-turn': return t('type.chatTurn');
    case 'step.completed': return t('type.stepCompleted');
    default: return t('type.other');
  }
}

function getEventTitle(t: T, event: ExecutionStreamEvent): string {
  if (event.type === 'execution.running') return t('title.running');
  if (event.type === 'execution.completed') return t('title.completed');
  if (event.type === 'execution.failed') return t('title.failed');
  if (event.type === 'step.started') return t('title.stepStarted');
  if (event.type === 'step.progress') return getPayloadString(event.payload, 'stage', 'Stage') || t('title.progress');
  if (event.type === 'step.tool-called') return t('title.toolCalled');
  if (event.type === 'step.reasoning') return t('title.reasoning');
  if (event.type === 'step.chat-turn') {
    const speaker = getPayloadString(event.payload, 'speaker', 'Speaker');
    return speaker ? t('title.speakerSpoke', { speaker: agentRoleLabel(speaker, speaker) }) : t('title.discussionTurn');
  }
  const status = getPayloadString(event.payload, 'stepStatus', 'StepStatus');
  if (status === 'Failed') return t('title.stepFailed');
  if (status === 'Skipped') return t('title.stepSkipped');
  return t('title.stepCompleted');
}

export function getEventIcon(eventType: ExecutionStreamEvent['type']) {
  if (eventType === 'execution.completed') return <IconCheck size={12} />;
  if (eventType === 'execution.failed') return <IconX size={12} />;
  if (eventType === 'execution.running') return <IconActivity size={12} />;
  if (eventType === 'step.started') return <IconClock size={12} />;
  if (eventType === 'step.progress') return <IconActivity size={12} />;
  if (eventType === 'step.tool-called') return <IconTool size={12} />;
  if (eventType === 'step.reasoning') return <IconBrain size={12} />;
  if (eventType === 'step.chat-turn') return <IconMessage2 size={12} />;
  return <IconPlayerPlay size={12} />;
}

function getEventMetadata(t: T, f: Format, event: ExecutionStreamEvent, technical: boolean): string[] {
  const metadata: string[] = [];

  const stepOrder = getPayloadNumber(event.payload, 'stepOrder', 'StepOrder');
  if (stepOrder > 0) {
    metadata.push(t('meta.stepNumber', { number: stepOrder }));
  }

  const stepLabel = getPayloadString(event.payload, 'stepLabel', 'StepLabel');
  if (stepLabel) {
    metadata.push(stepLabel);
  }

  // Engine identifiers (VIDEOTRANSFORM, NarrationWriter) become the customer-facing role; the raw
  // value is only shown in technical mode.
  const agentName = getPayloadString(event.payload, 'agentName', 'AgentName');
  if (agentName) {
    const role = agentRoleLabel(agentName, agentName);
    metadata.push(role);
    if (technical && role !== agentName) metadata.push(agentName);
  }

  if (event.type === 'step.completed') {
    const durationMs = getPayloadNumber(event.payload, 'durationMs', 'DurationMs');
    const tokens = getPayloadNumber(event.payload, 'tokensUsed', 'TokensUsed');

    if (durationMs > 0) metadata.push(t('meta.took', { duration: formatDurationLong(Math.round(durationMs)) }));
    if (technical && tokens > 0) metadata.push(t('meta.tokens', { count: f.number(tokens) }));
  }

  if (event.type === 'step.tool-called') {
    const toolName = getPayloadString(event.payload, 'toolName', 'ToolName');
    const sequence = getPayloadNumber(event.payload, 'sequence', 'Sequence');
    if (toolName) metadata.push(t('meta.tool', { name: toolName }));
    if (sequence > 0) metadata.push(t('meta.call', { number: sequence }));
  }

  if (event.type === 'step.reasoning') {
    const sequence = getPayloadNumber(event.payload, 'sequence', 'Sequence');
    if (sequence > 0) metadata.push(t('meta.reasoning', { number: sequence }));
  }

  if (event.type === 'step.progress') {
    const percent = getPayloadNumber(event.payload, 'percentComplete', 'PercentComplete');
    if (percent > 0) metadata.push(t('meta.percent', { percent: Math.round(percent) }));
  }

  if (event.type === 'step.chat-turn') {
    const turnIndex = getPayloadNumber(event.payload, 'turnIndex', 'TurnIndex');
    const totalTurns = getPayloadNumber(event.payload, 'totalTurns', 'TotalTurns');
    metadata.push(totalTurns > 0 ? t('meta.turnOf', { turn: turnIndex + 1, total: totalTurns }) : t('meta.turn', { turn: turnIndex + 1 }));
    const speakerRole = getPayloadString(event.payload, 'speakerRole', 'SpeakerRole');
    if (speakerRole) metadata.push(technical ? speakerRole : agentRoleLabel(speakerRole, speakerRole));
  }

  return metadata;
}

function getEventBody(event: ExecutionStreamEvent): string | null {
  if (event.type === 'execution.failed') {
    return getPayloadString(event.payload, 'errorMessage', 'ErrorMessage') || null;
  }

  if (event.type === 'step.tool-called') {
    return getPayloadString(event.payload, 'argumentsPreview', 'ArgumentsPreview')
      || getPayloadString(event.payload, 'resultPreview', 'ResultPreview')
      || null;
  }

  if (event.type === 'step.reasoning') {
    return getPayloadString(event.payload, 'content', 'Content') || null;
  }

  if (event.type === 'step.completed') {
    return getPayloadString(event.payload, 'errorDetails', 'ErrorDetails') || null;
  }

  if (event.type === 'step.chat-turn') {
    return getPayloadString(event.payload, 'text', 'Text') || null;
  }

  return null;
}

/**
 * One execution event. By default it is written for the customer: friendly titles and role names,
 * no token counts, no raw payload. `technical` (the page's "Details" disclosure) restores the
 * engine identifiers, token counts and the raw-payload viewer.
 */
export function ExecutionEventCard({ event, technical = false }: { event: ExecutionStreamEvent; technical?: boolean }) {
  const t = useT('execution.event');
  const f = useFormat();
  const locale = useLocale();
  const [showRaw, setShowRaw] = useState(false);
  const metadata = getEventMetadata(t, f, event, technical);
  const body = getEventBody(event);

  return (
    <Paper withBorder p="sm" radius="md">
      <Group justify="space-between" align="flex-start" mb={6}>
        <Group gap="xs" wrap="wrap">
          <Badge size="xs" color={getEventBadgeColor(event.type)}>{technical ? event.type : getEventTypeLabel(t, event.type)}</Badge>
          <Text size="sm" fw={600}>{getEventTitle(t, event)}</Text>
        </Group>
        <Text size="xs" c="dimmed">{new Date(event.timestamp).toLocaleTimeString(locale)}</Text>
      </Group>

      {metadata.length > 0 && (
        <Group gap={6} wrap="wrap" mb={body ? 6 : 0}>
          {metadata.map((item) => (
            <Badge key={item} size="xs" variant="light" color="gray">{item}</Badge>
          ))}
        </Group>
      )}

      {body && (
        <Text size="xs" c="dimmed" style={{ whiteSpace: 'pre-wrap' }} mb={8}>
          {body}
        </Text>
      )}

      {technical && (
        <>
          <Button variant="subtle" size="compact-xs" onClick={() => setShowRaw((current) => !current)}>
            {showRaw ? t('hideRaw') : t('showRaw')}
          </Button>
          {showRaw && <JsonViewer label={t('payloadLabel')} value={JSON.stringify(event.payload)} />}
        </>
      )}
    </Paper>
  );
}
