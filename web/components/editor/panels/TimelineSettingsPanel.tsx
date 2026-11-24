'use client';

import React, { useState } from 'react';
import { Button, Group, NumberInput, Select, Stack, Text } from '@mantine/core';
import type { AspectPolicy, EditTimelineSettings } from '@/lib/editing/types';
import { describeFrameRate, formatForSize, FRAME_RATE_CHOICES, TIMELINE_FORMATS } from '@/lib/editing/time';
import { TimecodeInput } from '../shell/TimecodeInput';
import { EditorPanel } from '../ui/EditorPanel';
import { useFormat, useT } from '@/i18n';

/** Why the frame-rate control is disabled once the timeline holds clips (their frames are not re-timed). */
export const FRAME_RATE_LOCKED_REASON = "The frame rate can't be changed once there are clips on the timeline. Start a new timeline at the frame rate you want.";

/** True when any track (including the tracks inside compound clips) holds an item. */
export function timelineHasClips(doc: { tracks: { items: unknown[] }[]; sequences?: { tracks: { items: unknown[] }[] }[] | null }): boolean {
  return doc.tracks.some((t) => t.items.length > 0) || (doc.sequences ?? []).some((q) => q.tracks.some((t) => t.items.length > 0));
}

const CUSTOM_FORMAT = 'custom';
const CUSTOM_RATE = 'custom';

export interface TimelineSettingsPanelProps {
  settings: EditTimelineSettings;
  /**
   * P4: apply a settings patch as one `setTimelineSettings` op. Absent (or `readOnly`) renders the
   * P3 read-only view, so a conflicted timeline still shows its settings without offering an edit
   * the server would refuse.
   */
  onChange?: (patch: Partial<EditTimelineSettings>) => void;
  readOnly?: boolean;
  /** Whether the timeline holds clips; when true the frame-rate control is disabled with a visible reason. */
  hasClips?: boolean;
}

const ASPECT_OPTIONS: { value: AspectPolicy }[] = [{ value: 'fit' }, { value: 'fill' }, { value: 'stretch' }];

/**
 * Timeline settings: resolution, rational rate, sample rate, pixel aspect, aspect policy, start
 * timecode. Read-only until P4; since P4 the fields edit a local draft and **Apply** dispatches
 * exactly the keys that changed as ONE `setTimelineSettings` op (one undo step), so a slip of the
 * mouse on a number input never becomes a document revision by itself.
 */
export function TimelineSettingsPanel({ settings, onChange, readOnly = false, hasClips }: TimelineSettingsPanelProps) {
  const tx = useT('editor.panels.timelineSettingsPanel');
  const nf = useFormat();
  const fps = settings.rate.den === 0 ? 0 : settings.rate.num / settings.rate.den;
  const editable = Boolean(onChange) && !readOnly;
  // The draft is an overlay of edited keys over the live settings, so an undo or a remote change
  // shows through immediately and no effect has to copy props into state.
  const [overrides, setOverrides] = useState<Partial<EditTimelineSettings>>({});
  const draft: EditTimelineSettings = { ...settings, ...overrides };
  const setDraft = (next: EditTimelineSettings) => setOverrides(next);

  const row = (label: string, value: string, testId: string) => (
    <Group justify="space-between" key={label}>
      <Text size="xs" c="dimmed">
        {label}
      </Text>
      <Text size="xs" ff="monospace" data-testid={testId}>
        {value}
      </Text>
    </Group>
  );

  if (!editable) {
    return (
      <EditorPanel title={tx('timelineSettings')} testId="timeline-settings-panel">
      <Stack gap={4}>
        {row(tx('resolution'), `${settings.width}×${settings.height}`, 'settings-resolution') /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
        {row(tx('frameRate2'), `${settings.rate.num}/${settings.rate.den} (${nf.number(fps, { minimumFractionDigits: 3, maximumFractionDigits: 3 })} fps)`, 'settings-rate') /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
        {row(tx('sampleRate'), `${settings.sampleRate} Hz`, 'settings-sample-rate') /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
        {row(tx('pixelAspect'), `${settings.pixelAspect.num}:${settings.pixelAspect.den}`, 'settings-par') /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
        {row(tx('aspectPolicy2'), settings.aspectPolicy, 'settings-aspect-policy') /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
        {row(tx('startTimecode2'), `${settings.startTimecodeFrames} f`, 'settings-start-tc') /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
        <Text size="xs" c="dimmed">
          {readOnly ? tx('readOnlyWhileThis') : tx('readOnly')}
        </Text>
      </Stack>
      </EditorPanel>
    );
  }

  const patch: Partial<EditTimelineSettings> = {};
  if (draft.width !== settings.width) patch.width = draft.width;
  if (draft.height !== settings.height) patch.height = draft.height;
  if (draft.rate.num !== settings.rate.num || draft.rate.den !== settings.rate.den) patch.rate = { ...draft.rate };
  if (draft.sampleRate !== settings.sampleRate) patch.sampleRate = draft.sampleRate;
  if (draft.pixelAspect.num !== settings.pixelAspect.num || draft.pixelAspect.den !== settings.pixelAspect.den) patch.pixelAspect = { ...draft.pixelAspect };
  if (draft.aspectPolicy !== settings.aspectPolicy) patch.aspectPolicy = draft.aspectPolicy;
  if (draft.startTimecodeFrames !== settings.startTimecodeFrames) patch.startTimecodeFrames = draft.startTimecodeFrames;
  const dirty = Object.keys(patch).length > 0;
  const rateLocked = hasClips === true;
  const rateChoice = draft.rate.den === 1 && FRAME_RATE_CHOICES.includes(draft.rate.num) ? String(draft.rate.num) : CUSTOM_RATE;
  const int = (v: string | number, fallback: number) => (typeof v === 'number' && Number.isInteger(v) ? v : fallback);

  return (
    <EditorPanel title={tx('timelineSettings')} testId="timeline-settings-panel">
    <Stack gap={6}>
      <Select
        label={tx('format')}
        size="xs"
        value={formatForSize(draft.width, draft.height)?.key ?? CUSTOM_FORMAT}
        allowDeselect={false}
        data={[...TIMELINE_FORMATS.map((f) => ({ value: f.key, label: f.label })), { value: CUSTOM_FORMAT, label: tx('customSize') }]}
        onChange={(v) => {
          const f = TIMELINE_FORMATS.find((x) => x.key === v);
          if (f) setDraft({ ...draft, width: f.width, height: f.height });
        }}
        comboboxProps={{ withinPortal: true }}
        data-testid="settings-format"
      />
      <Select
        label={tx('frameRate')}
        size="xs"
        value={rateChoice}
        allowDeselect={false}
        disabled={rateLocked}
        data={[
          ...FRAME_RATE_CHOICES.map((n) => ({ value: String(n), label: tx('framesPerSecond', { n }) })),
          ...(rateChoice === CUSTOM_RATE ? [{ value: CUSTOM_RATE, label: tx('framesPerSecond2', { v1: describeFrameRate(draft.rate) }) }] : []),
        ]}
        onChange={(v) => {
          const n = Number(v);
          if (Number.isFinite(n) && n > 0) setDraft({ ...draft, rate: { num: n, den: 1 } });
        }}
        comboboxProps={{ withinPortal: true }}
        data-testid="settings-fps"
      />
      {rateLocked ? (
        <Text size="xs" c="dimmed" data-testid="settings-rate-locked">
          {FRAME_RATE_LOCKED_REASON}
        </Text>
      ) : null}
      <Group grow>
        <NumberInput label={tx('width')} size="xs" min={16} value={draft.width} onChange={(v) => setDraft({ ...draft, width: int(v, draft.width) })} data-testid="settings-width" />
        <NumberInput label={tx('height')} size="xs" min={16} value={draft.height} onChange={(v) => setDraft({ ...draft, height: int(v, draft.height) })} data-testid="settings-height" />
      </Group>
      <Group grow>
        <NumberInput label={tx('rateNum')} size="xs" min={1} disabled={rateLocked} value={draft.rate.num} onChange={(v) => setDraft({ ...draft, rate: { ...draft.rate, num: int(v, draft.rate.num) } })} data-testid="settings-rate-num" />
        <NumberInput label={tx('rateDen')} size="xs" min={1} disabled={rateLocked} value={draft.rate.den} onChange={(v) => setDraft({ ...draft, rate: { ...draft.rate, den: int(v, draft.rate.den) } })} data-testid="settings-rate-den" />
      </Group>
      <Text size="xs" c="dimmed" data-testid="settings-rate">
        {`${draft.rate.num}/${draft.rate.den} = ${nf.number(draft.rate.den === 0 ? 0 : draft.rate.num / draft.rate.den, { minimumFractionDigits: 3, maximumFractionDigits: 3 })} fps`}
      </Text>
      <NumberInput label={tx('sampleRateHz')} size="xs" min={8000} value={draft.sampleRate} onChange={(v) => setDraft({ ...draft, sampleRate: int(v, draft.sampleRate) })} data-testid="settings-sample-rate" />
      <Group grow>
        <NumberInput label={tx('pixelAspectNum')} size="xs" min={1} value={draft.pixelAspect.num} onChange={(v) => setDraft({ ...draft, pixelAspect: { ...draft.pixelAspect, num: int(v, draft.pixelAspect.num) } })} />
        <NumberInput label={tx('pixelAspectDen')} size="xs" min={1} value={draft.pixelAspect.den} onChange={(v) => setDraft({ ...draft, pixelAspect: { ...draft.pixelAspect, den: int(v, draft.pixelAspect.den) } })} />
      </Group>
      <Select label={tx('aspectPolicy')} size="xs" data={ASPECT_OPTIONS.map((o) => ({ value: o.value, label: tx(`aspectOptions.${o.value}`) }))} value={draft.aspectPolicy} allowDeselect={false} onChange={(v) => v && setDraft({ ...draft, aspectPolicy: v as AspectPolicy })} data-testid="settings-aspect-policy" />
      <TimecodeInput label={tx('startTimecode')} size="xs" frames={draft.startTimecodeFrames} rate={draft.rate} onCommit={(frames) => setDraft({ ...draft, startTimecodeFrames: frames })} data-testid="settings-start-tc" />
      <Group justify="flex-end" gap={4}>
        <Button size="compact-xs" fz="xs" variant="subtle" disabled={!dirty} onClick={() => setOverrides({})}>
          {tx('revert')}
        </Button>
        <Button size="compact-xs" fz="xs" disabled={!dirty} onClick={() => { onChange?.(patch); setOverrides({}); }} data-testid="settings-apply">
          {tx('apply')}
        </Button>
      </Group>
      <Text size="xs" c="dimmed">
        {tx('applyWritesOneUndoable')}
      </Text>
    </Stack>
    </EditorPanel>
  );
}
