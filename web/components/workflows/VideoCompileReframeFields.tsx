'use client';

import { useEffect, useRef, useState } from 'react';
import { Stack, Group, Select, TextInput, NumberInput, Text, Textarea, Button, Fieldset, SimpleGrid } from '@mantine/core';
import { useT } from '@/i18n';
import { msg, type Msg } from '@/i18n/msg';
import { useProjectFiles } from '@/lib/hooks/use-files';
import type {
  InsertContentRef,
  VideoCompileStepConfig as VideoCompileStepConfigValue,
  VideoOutputFit,
  VideoReframe,
} from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';
import { useFd, stepOrderOptions } from './config-kit';

// Labels: wfSteps.reframe.fit.<value>
const FIT_VALUES: VideoOutputFit[] = ['Crop', 'Pad', 'Subject'];

// Labels: wfSteps.reframe.refFrom.<value>
const REF_FROM_VALUES: InsertContentRef['from'][] = ['ProjectFile', 'Step', 'StepResult'];

const REFRAME_PLACEHOLDER = `[
  {
    "sourceIndex": 0,
    "fromSec": 0,
    "toSec": 6,
    "keyframes": [
      { "atSec": 0, "zoom": 1, "centerX": 0.5, "centerY": 0.5 },
      { "atSec": 6, "zoom": 1.6, "centerX": 0.62, "centerY": 0.4, "easing": "EaseInOut" }
    ]
  }
]`;

/**
 * Parses the reframes text. Empty means "no reframes" (null); anything else must be a JSON list. The shape of each
 * reframe is deliberately NOT re-checked here: the server owns the rules (ordering, overlap, zoom and centre ranges,
 * per-compile caps) and answers a bad list with a 400 naming the problem, which is shown on this field.
 */
export function parseReframesText(text: string): { value: VideoReframe[] | null; error: string | null } {
  if (!text.trim()) return { value: null, error: null };
  try {
    const parsed: unknown = JSON.parse(text);
    if (!Array.isArray(parsed)) return { value: null, error: 'Reframes must be a JSON list: [ { ... }, { ... } ].' };
    if (parsed.some((r) => r === null || typeof r !== 'object' || Array.isArray(r))) {
      return { value: null, error: 'Each reframe must be an object with a sourceIndex and its keyframes.' };
    }
    return { value: parsed.length > 0 ? (parsed as VideoReframe[]) : null, error: null };
  } catch (e) {
    // eslint-disable-next-line no-restricted-syntax -- JSON.parse's own detail for the person's pasted text, not an API error
    return { value: null, error: `Not valid JSON: ${e instanceof Error ? e.message : 'parse error'}` };
  }
}

/**
 * Same parse as `parseReframesText`, with the error as a catalogue message for the editor to resolve.
 * `parseReframesText` stays for callers (and tests) that read the plain English text.
 */
export function parseReframesMsg(text: string): { value: VideoReframe[] | null; error: Msg | null } {
  if (!text.trim()) return { value: null, error: null };
  try {
    const parsed: unknown = JSON.parse(text);
    if (!Array.isArray(parsed)) return { value: null, error: msg('wfSteps.reframe.errors.notList') };
    if (parsed.some((r) => r === null || typeof r !== 'object' || Array.isArray(r))) {
      return { value: null, error: msg('wfSteps.reframe.errors.notObject') };
    }
    return { value: parsed.length > 0 ? (parsed as VideoReframe[]) : null, error: null };
  } catch (e) {
    // eslint-disable-next-line no-restricted-syntax -- JSON.parse's own detail for the person's pasted text, not an API error
    return { value: null, error: msg('wfSteps.reframe.errors.invalidJson', { detail: e instanceof Error ? e.message : '' }) };
  }
}

const prettyReframes = (reframes: VideoReframe[] | null | undefined) =>
  reframes && reframes.length > 0 ? JSON.stringify(reframes, null, 2) : '';

/** The reframes JSON editor: shows the client-side JSON error immediately, and the server's 400 through the field marker. */
function ReframesEditor({
  value, onChange,
}: { value: VideoReframe[] | null | undefined; onChange: (value: VideoReframe[] | null) => void }) {
  const t = useT('wfSteps.reframe');
  const fd = useFd();
  const [text, setText] = useState(() => prettyReframes(value));
  const [error, setError] = useState<Msg | null>(null);
  // What this editor last handed to the parent, so a change that came from elsewhere (the assistant) replaces the
  // text but the parent echoing the author's own typing does not reformat it under their cursor.
  const lastEmitted = useRef(JSON.stringify(value ?? null));

  useEffect(() => {
    const incoming = JSON.stringify(value ?? null);
    if (incoming !== lastEmitted.current) {
      lastEmitted.current = incoming;
      setText(prettyReframes(value));
      setError(null);
    }
  }, [value]);

  return (
    <Textarea
      // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
      {...fd('config.reframes')}
      label={t('reframes.label')}
      description={t('reframes.description')}
      placeholder={REFRAME_PLACEHOLDER}
      size="xs"
      rows={8}
      styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }}
      value={text}
      error={error ? t.msg(error) : null}
      onChange={(e) => {
        const next = e.target.value;
        setText(next);
        const result = parseReframesMsg(next);
        setError(result.error);
        if (!result.error) {
          lastEmitted.current = JSON.stringify(result.value ?? null);
          onChange(result.value);
        }
      }}
    />
  );
}

/** Per-source fit overrides: one row per source clip that is fitted differently from the rest. */
function SourceFitsEditor({
  value, onChange,
}: { value: Record<string, VideoOutputFit> | null | undefined; onChange: (value: Record<string, VideoOutputFit> | null) => void }) {
  const t2 = useT('wfSteps.reframe.fits');
  const fd = useFd();
  const rows = Object.entries(value ?? {});
  const emit = (next: [string, VideoOutputFit][]) => onChange(next.length > 0 ? Object.fromEntries(next) : null);
  return (
    <Stack
      gap="xs"
      // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
      {...fd('config.sourceFits').wrapperProps}>
      <Text size="xs" fw={500}>{t2('title')}</Text>
      <Text size="xs" c="dimmed">
        {t2('description')}
      </Text>
      {rows.map(([key, fit], index) => (
        <Group key={key} align="flex-end" gap="xs" wrap="wrap">
          <NumberInput
            label={t2('clip.label')}
            description={t2('clip.description')}
            size="xs"
            min={1}
            w={170}
            value={Number(key) + 1}
            onChange={(v) => {
              const nextKey = typeof v === 'number' ? String(Math.max(0, Math.round(v) - 1)) : key;
              emit(rows.map((r, i) => (i === index ? [nextKey, r[1]] : r)));
            }}
          />
          <Select
            label={t2('fit')}
            size="xs"
            data={FIT_VALUES.map((v) => ({ value: v, label: t2(`option.${v}`) }))}
            value={fit}
            onChange={(v) => v && emit(rows.map((r, i) => (i === index ? [r[0], v as VideoOutputFit] : r)))}
            allowDeselect={false}
            style={{ flex: 1, minWidth: 200 }}
          />
          <Button size="compact-xs" variant="subtle" color="red" onClick={() => emit(rows.filter((_, i) => i !== index))}>
            {t2('remove')}
          </Button>
        </Group>
      ))}
      <Button
        size="compact-xs"
        variant="light"
        style={{ alignSelf: 'flex-start' }}
        onClick={() => {
          const used = new Set(rows.map((r) => r[0]));
          let next = 0;
          while (used.has(String(next))) next += 1;
          emit([...rows, [String(next), 'Pad']]);
        }}
      >
        {t2('add')}
      </Button>
    </Stack>
  );
}

/** A reference to insert content that is not a fixed file: an uploaded file, an earlier step, or an earlier run's step result. */
function InsertContentRefEditor({
  value, onChange, allSteps, currentStepIndex, projectId,
}: {
  value: InsertContentRef | null | undefined;
  onChange: (value: InsertContentRef | null) => void;
  allSteps: StepData[];
  currentStepIndex: number;
  projectId?: string;
}) {
  const t3 = useT('wfSteps.reframe.insertRef');
  const fd = useFd();
  const { data: files } = useProjectFiles(projectId ?? '');
  const insertable = (files ?? []).filter((f) => f.mimeType?.startsWith('video/') || f.mimeType?.startsWith('image/'));
  const stepOptions = stepOrderOptions(allSteps, currentStepIndex, 'before', t3.msg);

  return (
    <Stack gap="xs">
      <Select
        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
        {...fd('config.insertContentRef.from')}
        label={t3('label')}
        description={t3('description')}
        size="xs"
        data={REF_FROM_VALUES.map((v) => ({ value: v, label: t3(`from.${v}`) }))}
        value={value?.from ?? null}
        onChange={(v) => {
          if (!v) return onChange(null);
          onChange({ from: v as InsertContentRef['from'], projectFileId: null, stepOrder: null, stepResultId: null });
        }}
        placeholder={t3('none')}
        clearable
      />
      {value?.from === 'ProjectFile' && (
        <Select
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.insertContentRef.projectFileId')}
          label={t3('file.label')}
          size="xs"
          data={insertable.map((f) => ({ value: f.id, label: f.originalFileName }))}
          value={value.projectFileId ?? null}
          onChange={(v) => onChange({ from: 'ProjectFile', projectFileId: v, stepOrder: null, stepResultId: null })}
          placeholder={insertable.length > 0 ? t3('file.select') : t3('file.empty')}
          disabled={insertable.length === 0}
          searchable
        />
      )}
      {value?.from === 'Step' && (
        <Select
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.insertContentRef.stepOrder')}
          label={t3('step.label')}
          size="xs"
          data={stepOptions}
          value={value.stepOrder != null ? String(value.stepOrder) : null}
          onChange={(v) => onChange({ from: 'Step', projectFileId: null, stepOrder: v ? Number(v) : null, stepResultId: null })}
          placeholder={stepOptions.length > 0 ? t3('step.select') : t3('step.none')}
          disabled={stepOptions.length === 0}
        />
      )}
      {value?.from === 'StepResult' && (
        <TextInput
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.insertContentRef.stepResultId')}
          label={t3('stepResult.label')}
          description={t3('stepResult.description')}
          size="xs"
          value={value.stepResultId ?? ''}
          onChange={(e) => onChange({ from: 'StepResult', projectFileId: null, stepOrder: null, stepResultId: e.target.value.trim() || null })}
        />
      )}
    </Stack>
  );
}

interface VideoCompileReframeFieldsProps {
  config: VideoCompileStepConfigValue;
  patch: (updates: Partial<VideoCompileStepConfigValue>) => void;
  allSteps: StepData[];
  currentStepIndex: number;
  projectId?: string;
}

/**
 * The compile step's reframing and by-reference options, shown under Advanced: keyframed reframes (a JSON editor, no
 * keyframe UI), per-clip fits, the subject safe area, and insert content named by reference. Every one is empty by
 * default and left out of the saved config while empty, so an untouched step saves exactly as before.
 */
export function VideoCompileReframeFields({ config, patch, allSteps, currentStepIndex, projectId }: VideoCompileReframeFieldsProps) {
  const t = useT('wfSteps.reframe');
  const fd = useFd();
  const hasReframes = (config.reframes?.length ?? 0) > 0;
  return (
    <Fieldset legend={t('legend')}>
      <Stack gap="md">
        <ReframesEditor
          value={config.reframes}
          onChange={(reframes) => patch({
            reframes,
            // A zoom or pan has no stream-copy equivalent, so entering one switches to Re-encode, as the other
            // pixel-changing options on this form do.
            ...(reframes && config.mode === 'StreamCopy' ? { mode: 'Reencode' as const } : {}),
          })}
        />
        {hasReframes && config.mode === 'StreamCopy' && (
          <Text size="xs" c="orange">{t('needsReencode')}</Text>
        )}
        <SourceFitsEditor value={config.sourceFits} onChange={(sourceFits) => patch({ sourceFits })} />
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.subjectSafeArea')}
            label={t('safeArea.label')}
            description={t('safeArea.description')}
            size="xs"
            min={0}
            max={0.25}
            step={0.01}
            decimalScale={3}
            value={config.subjectSafeArea ?? ''}
            onChange={(v) => patch({ subjectSafeArea: typeof v === 'number' ? Math.min(0.25, Math.max(0, v)) : null })}
          />
        </SimpleGrid>
        <InsertContentRefEditor
          value={config.insertContentRef}
          onChange={(insertContentRef) => patch({ insertContentRef })}
          allSteps={allSteps}
          currentStepIndex={currentStepIndex}
          projectId={projectId}
        />
      </Stack>
    </Fieldset>
  );
}
