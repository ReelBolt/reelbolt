'use client';

import React, { useState } from 'react';
import { Alert, Badge, Button, Group, SegmentedControl, Stack, Switch, Text, TextInput } from '@mantine/core';
import { IconScissors, IconX } from '@tabler/icons-react';
import type { SubjectBoxPrompt, SubjectPointPrompt } from '@/lib/types/image-document';
import { isUnaffordable, type PictureEstimate } from '@/lib/editor/credit-estimate';
import { useT } from '@/i18n';
import { useRunLabelA } from './run-label-a';
import { CreditNote, NoProviderCard } from './AiPanel';
import { PinnedRun } from './PinnedRun';
import { EditorIcon } from '../ui';

/** How many cut-outs "several options" asks for. */
export const MAX_SUBJECT_MASKS = 4;

export interface SubjectPanelProps {
  points: readonly SubjectPointPrompt[];
  boxes: readonly SubjectBoxPrompt[];
  /** Why the tool cannot run right now (no picture layer to cut from, a run in flight), or null. */
  blocker: string | null;
  /** The name of the layer the subject will be cut from, or null when there is none. */
  targetLabel?: string | null;
  busy: boolean;
  onRemovePoint(index: number): void;
  onRemoveBox(index: number): void;
  onClear(): void;
  onRun(input: { prompt: string; returnMultipleMasks: boolean }): void;
  /** What a plain click on the picture marks: the thing to cut out, or something to leave out. */
  markMode?: 'include' | 'leaveOut';
  onMarkMode?(mode: 'include' | 'leaveOut'): void;
  /** The server's reason no cut-out service is set up in this workspace, or undefined when one is. */
  unavailable?: string;
  /** True when the caller may set up the workspace's AI services. */
  canConfigure?: boolean;
  /** One run's price and whether the balance covers it. */
  estimate?: PictureEstimate | null;
  /** What the last cut-out cost. */
  chargedCredits?: number | null;
}

/** True when there is anything to send: a word, a click or a box. */
export function hasSubjectPrompt(prompt: string, points: readonly unknown[], boxes: readonly unknown[]): boolean {
  return prompt.trim().length > 0 || points.length > 0 || boxes.length > 0;
}

/**
 * Cuts a subject out of the picture. Three ways to say which one, usable together: click it (a
 * point; Shift-click marks something to leave out), drag a box around it, or type its name. Each
 * result becomes its own cut-out layer. This is a paid AI call every time it runs.
 */
export function SubjectPanel({
  points,
  boxes,
  blocker,
  targetLabel = null,
  busy,
  onRemovePoint,
  onRemoveBox,
  onClear,
  onRun,
  markMode = 'include',
  onMarkMode,
  unavailable,
  canConfigure = false,
  estimate = null,
  chargedCredits = null,
}: SubjectPanelProps) {
  const t = useT('editor-thumbnail-a.subject');
  const { runLabel } = useRunLabelA();
  const [prompt, setPrompt] = useState('');
  const [multiple, setMultiple] = useState(false);
  const ready = hasSubjectPrompt(prompt, points, boxes);

  return (
    <Stack gap="sm">
      <Text size="xs" c="dimmed">
        {t('intro')}
      </Text>
      {onMarkMode && (
        <SegmentedControl
          size="xs"
          fullWidth
          aria-label={t('markLabel')}
          data={[
            { value: 'include', label: t('cutThis') },
            { value: 'leaveOut', label: t('leaveThisOut') },
          ]}
          value={markMode}
          onChange={(v) => onMarkMode(v as 'include' | 'leaveOut')}
          styles={{ label: { minHeight: 36, display: 'flex', alignItems: 'center', justifyContent: 'center' } }}
        />
      )}
      {targetLabel && (
        <Text size="xs">
          {t.rich('cuttingFrom', { name: targetLabel, b: (c) => <b>{c}</b> })}
        </Text>
      )}

      <TextInput
        size="xs"
        label={t('describe')}
        placeholder={t('describePlaceholder')}
        value={prompt}
        onChange={(e) => setPrompt(e.currentTarget.value)}
      />

      {(points.length > 0 || boxes.length > 0) && (
        <Stack gap={4}>
          <Group justify="space-between">
            <Text size="xs" fw={600}>
              {t('marksTitle')}
            </Text>
            <Button size="compact-xs" fz="xs" variant="subtle" color="gray" onClick={onClear}>
              {t('clearMarks')}
            </Button>
          </Group>
          <Group gap={6}>
            {points.map((p, i) => (
              <Badge
                key={`p-${i}`}
                size="sm" fz="xs"
                variant="light"
                color={p.label === 1 ? 'green' : 'red'}
                rightSection={
                  <EditorIcon icon={IconX} size="sm"
                    variant="transparent"
                    label={p.label === 1 ? t('removeInclude', { n: i + 1 }) : t('removeLeaveOut', { n: i + 1 })}
                    onClick={() => onRemovePoint(i)} />
                }
              >
                {p.label === 1 ? t('include', { n: i + 1 }) : t('leaveOut', { n: i + 1 })}
              </Badge>
            ))}
            {boxes.map((_, i) => (
              <Badge
                key={`b-${i}`}
                size="sm" fz="xs"
                variant="light"
                color="blue"
                rightSection={
                  <EditorIcon icon={IconX} size="sm"
                    variant="transparent"
                    label={t('removeBox', { n: i + 1 })}
                    onClick={() => onRemoveBox(i)} />
                }
              >
                {t('box', { n: i + 1 })}
              </Badge>
            ))}
          </Group>
        </Stack>
      )}

      <Switch
        size="xs"
        label={t('options', { count: MAX_SUBJECT_MASKS })}
        description={t('optionsHint')}
        checked={multiple}
        onChange={(e) => setMultiple(e.currentTarget.checked)}
      />

      {unavailable ? (
        <NoProviderCard reason={unavailable} canConfigure={canConfigure} />
      ) : (
        <PinnedRun>
          <Button
            size="xs" fz="xs"
            leftSection={<IconScissors size={14} />}
            loading={busy}
            disabled={!ready || blocker !== null || isUnaffordable(estimate)}
            onClick={() => onRun({ prompt: prompt.trim(), returnMultipleMasks: multiple })}
          >
            {runLabel(t('run'), estimate)}
          </Button>
          <CreditNote estimate={estimate} charged={chargedCredits} testId="credits-subject" />
          {blocker ? (
            <Text size="xs" c="dimmed">
              {blocker}
            </Text>
          ) : !ready ? (
            <Text size="xs" c="dimmed">
              {t('needInput')}
            </Text>
          ) : null}
        </PinnedRun>
      )}
      <Text size="xs" c="dimmed">
        {t('paidNote')}
      </Text>
    </Stack>
  );
}
