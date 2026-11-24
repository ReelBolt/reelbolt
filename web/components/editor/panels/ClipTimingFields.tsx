'use client';

import React, { useState } from 'react';
import { Group, SegmentedControl, Stack, Text, TextInput } from '@mantine/core';
import { framesToTimecode, parseTimecodeInput } from '@/lib/editing/time';
import type { Item, Rational } from '@/lib/editing/types';
import { useT } from '@/i18n';

type Field = 'sourceIn' | 'recordOut' | 'duration';

export interface ClipTimingFieldsProps {
  item: Item;
  rate: Rational;
  readOnly: boolean;
  /** Phone trim sheet: finger-sized fields (44 px). */
  touch?: boolean;
  /** Plain sentence when refused, else null (the document changed). */
  onApply: (field: Field, frames: number) => string | null;
}

/**
 * R03.3: the selected clip's In (its point in the source), Out and Duration as typed values, in frames
 * or timecode. Enter or leaving the field commits it; a value that cannot be made says why in a
 * sentence and the field goes back to what the clip really has.
 */
export function ClipTimingFields({ item, rate, readOnly, touch = false, onApply }: ClipTimingFieldsProps) {
  const t = useT('editor.panels.clipTimingFields');
  const [unit, setUnit] = useState<'frames' | 'timecode'>('frames');
  const [drafts, setDrafts] = useState<Partial<Record<Field, string>>>({});
  const [problem, setProblem] = useState<string | null>(null);
  const clip = item.type === 'mediaClip' ? item : null;
  const current: Record<Field, number> = { sourceIn: clip?.sourceIn ?? 0, recordOut: item.recordOut, duration: item.recordOut - item.recordIn };
  const show = (field: Field): string => {
    const draft = drafts[field];
    if (draft !== undefined) return draft;
    return unit === 'frames' ? String(current[field]) : framesToTimecode(current[field], rate);
  };
  const commit = (field: Field) => {
    const draft = drafts[field];
    if (draft === undefined) return;
    setDrafts((d) => {
      const next = { ...d };
      delete next[field];
      return next;
    });
    let frames: number;
    if (unit === 'frames') {
      if (!/^\d+$/.test(draft.trim())) return setProblem(t('typeAWholeNumber'));
      frames = Number.parseInt(draft.trim(), 10);
    } else {
      const parsed = parseTimecodeInput(draft, rate);
      if (!parsed.ok) return setProblem(parsed.reason);
      frames = parsed.frames;
    }
    setProblem(frames === current[field] ? null : onApply(field, frames));
  };
  const input = (field: Field, label: string, testId: string) => (
    <TextInput
      key={field}
      size={touch ? 'md' : 'xs'}
      styles={touch ? { input: { minHeight: 44, fontSize: 16 } } : undefined}
      label={label}
      value={show(field)}
      disabled={readOnly}
      ff="monospace"
      onChange={(e) => {
        const value = e.currentTarget.value;
        setDrafts((d) => ({ ...d, [field]: value }));
        setProblem(null);
      }}
      onBlur={() => commit(field)}
      onKeyDown={(e) => {
        if (e.key === 'Enter') {
          e.preventDefault();
          commit(field);
        } else if (e.key === 'Escape') {
          setDrafts((d) => {
            const next = { ...d };
            delete next[field];
            return next;
          });
        }
      }}
      aria-invalid={problem !== null}
      data-testid={testId}
    />
  );
  return (
    <Stack gap={6} data-testid="clip-timing">
      <Group justify="space-between" align="center">
        <Text size="12px" c="dimmed">
          {t('startsAt')} {framesToTimecode(item.recordIn, rate)}
        </Text>
        <SegmentedControl size={touch ? 'md' : 'xs'} value={unit} onChange={(v) => setUnit(v as 'frames' | 'timecode')} data={[{ label: t('frames'), value: 'frames' }, { label: t('timecode'), value: 'timecode' }]} data-testid="timing-unit" />
      </Group>
      <Group grow gap="xs" align="flex-start">
        {clip && input('sourceIn', t('inInTheSource'), 'inspector-in') /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
        {input('recordOut', t('outOnTheTimeline'), 'inspector-out') /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
        {input('duration', t('duration'), 'inspector-duration') /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
      </Group>
      {problem && (
        <Text size="xs" c="red" role="alert" data-testid="timing-problem">
          {problem}
        </Text>
      )}
      <Text size="12px" c="dimmed">
        {t('pressEnterToApply')}
      </Text>
    </Stack>
  );
}
