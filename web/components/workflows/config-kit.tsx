'use client';

import React, { createContext, useContext, useMemo, useState } from 'react';
import Link from 'next/link';
import { Select, Button, Text, Alert, Group, Collapse, Stack, Anchor } from '@mantine/core';
import { IconWand, IconInfoCircle, IconAlertTriangle, IconChevronDown, IconChevronUp, IconSettings, IconMusic } from '@tabler/icons-react';
import { useT, msg, type Msg } from '@/i18n';
import { useSelectableProviders } from '@/lib/hooks/use-inference-providers';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { useAuth } from '@/lib/hooks/use-auth';
import { useAssistantShell } from '@/components/assistant/AssistantShellProvider';
import { ApiError } from '@/lib/api/client';
import { selectableLabel, type InferenceProviderCapability } from '@/lib/types/inference-provider';
import { formatDiffValue, type StepFieldDiff } from './field-diff';
import diffClasses from './field-diff.module.css';
import type { StepData } from './WorkflowStepList';

/* ------------------------------------------------------------------ field-level diff */

export type WrapProps = Record<string, string | number | boolean | undefined>;

/** The two tooltip notes a highlighted field carries; built from the catalogue by `useFdNotes`. */
export interface FdNotes {
  rejected: (message: string) => string;
  proposes: (before: string, after: string) => string;
}

export function useFdNotes(): FdNotes {
  const t = useT('wfSteps.kit');
  return {
    rejected: (message) => t('fieldNote.rejected', { message }),
    proposes: (before, after) => t('fieldNote.proposes', { before, after }),
  };
}

const FieldDiffContext = createContext<StepFieldDiff | null>(null);

export function FieldDiffProvider({ diff, children }: { diff: StepFieldDiff | null; children: React.ReactNode }) {
  return <FieldDiffContext.Provider value={diff && diff.size > 0 ? diff : null}>{children}</FieldDiffContext.Provider>;
}

/** path -> server validation message for the open step (from a rejected save). */
export type FieldErrors = Map<string, string>;
const FieldErrorContext = createContext<FieldErrors | null>(null);

export function FieldErrorProvider({ errors, children }: { errors: FieldErrors | null; children: React.ReactNode }) {
  return <FieldErrorContext.Provider value={errors && errors.size > 0 ? errors : null}>{children}</FieldErrorContext.Provider>;
}

/** The server's message for one field of the open step (a rejected save), to show next to the field; else null. */
export function useFieldError(path: string): string | null {
  return useContext(FieldErrorContext)?.get(path) ?? null;
}

/** Pure form of `useFd` for components that render the provider themselves. */
export function fdFrom(
  diff: StepFieldDiff | null | undefined,
  errors: FieldErrors | null | undefined,
  notes: FdNotes,
): (path: string) => { wrapperProps?: WrapProps } {
  return (path: string) => {
    const message = errors?.get(path);
    if (message) {
      return {
        wrapperProps: {
          className: diffClasses.invalid,
          'data-field-error': 'true',
          'data-field-path': path,
          'data-diff-note': notes.rejected(message),
        },
      };
    }
    const change = diff?.get(path);
    if (!change) return {};
    return {
      wrapperProps: {
        className: diffClasses.changed,
        'data-diff': 'changed',
        'data-field-path': path,
        'data-diff-note': notes.proposes(formatDiffValue(change.before), formatDiffValue(change.after)),
      },
    };
  };
}

/** Props to spread on a Mantine input (`wrapperProps`) so a proposed change or rejected value is highlighted. */
export function useFd(): (path: string) => { wrapperProps?: WrapProps } {
  return fdFrom(useContext(FieldDiffContext), useContext(FieldErrorContext), useFdNotes());
}

/** Same, for components without `wrapperProps` (SegmentedControl, plain Boxes). */
export function useFdRaw(): (path: string) => WrapProps {
  const diff = useContext(FieldDiffContext);
  const notes = useFdNotes();
  return (path: string) => {
    const change = diff?.get(path);
    if (!change) return {};
    return {
      className: diffClasses.changed,
      'data-diff': 'changed',
      'data-field-path': path,
      'data-diff-note': notes.proposes(formatDiffValue(change.before), formatDiffValue(change.after)),
    };
  };
}

/** Number of fields in the open step's pending proposal, for a banner. */
export function useFieldDiffCount(): number {
  return useContext(FieldDiffContext)?.size ?? 0;
}

/* ------------------------------------------------------------------ ask assistant */

/**
 * The config drawer provides its own close here so "Ask assistant" can dismiss it first: both are
 * modal drawers and the assistant's would otherwise open underneath the one the user is in.
 */
export const ConfigCloseContext = createContext<(() => void) | null>(null);

/**
 * Opens the platform assistant with `prompt` prefilled (not sent), so a user stuck on a config
 * section can ask about it in one click. Renders nothing outside an AssistantShellProvider.
 */
export function AskAssistantButton({ prompt: promptMsg, label, size = 'compact-xs' }: {
  prompt: Msg;
  label?: string;
  size?: string;
}) {
  const t = useT('wfSteps.kit');
  const buttonLabel = label ?? t('askAssistant');
  // The prefilled text is the user's own message to the assistant, so it is in the user's language.
  const prompt = t.msg(promptMsg);
  let shell: ReturnType<typeof useAssistantShell> | null = null;
  try {
    // eslint-disable-next-line react-hooks/rules-of-hooks
    shell = useAssistantShell();
  } catch {
    shell = null; // rendered outside the dashboard shell (tests, storybook)
  }
  const closeConfig = useContext(ConfigCloseContext);
  if (!shell) return null;
  const open = shell.openWithPrompt;
  return (
    <Button
      variant="light"
      color="violet"
      size={size}
      leftSection={<IconWand size={13} />}
      onClick={(e) => {
        e.stopPropagation();
        closeConfig?.();
        open(prompt);
      }}
      aria-label={`${buttonLabel}: ${prompt}`}
    >
      {buttonLabel}
    </Button>
  );
}

/* ------------------------------------------------------------------ provider select */

interface ProviderSelectProps {
  capability: InferenceProviderCapability;
  value: string | null | undefined;
  onChange: (id: string | null) => void;
  label?: string;
  description?: React.ReactNode;
  size?: string;
  error?: React.ReactNode;
  /** Prepended to the "use default" placeholder, e.g. what null resolves to for this field. */
  nullMeans?: string;
  wrapperProps?: WrapProps;
}

// Display nouns live in the catalogue under wfSteps.kit.capability.<InferenceProviderCapability>.
const CAPABILITY_KEYS = new Set([
  'Chat', 'Transcription', 'Vision', 'Decision', 'VideoGeneration', 'SpeechSynthesis', 'Embedding',
  'ImageGeneration', 'ImageEdit', 'Segmentation', 'Depth', 'ImageErase', 'ImageFill', 'ImageExpand',
  'ImageBackgroundReplace', 'ImageRelight', 'ImageUpscale', 'VideoSegmentation', 'Compute',
  'MusicGeneration', 'SoundEffectGeneration', 'AudioIsolation', 'VideoUpscale', 'FrameInterpolation', 'LipSync',
]);

/**
 * Dropdown of the models this workspace may use for `capability` — never free text. Each option is
 * one model capability, labelled `{model} · {provider}` and grouped by who manages it; the stored
 * value is the model capability id.
 *
 * A17-UI: this reads `GET /api/v1/inference-providers/selectable`, not the admin list, so EVERY
 * member of a workspace can pick a provider (the Billing role is the one exception, and the API
 * refuses it). It used to read the admin-only list and hand a non-admin a disabled field, which
 * meant only a platform admin could choose a provider at all — wrong once a workspace owns its own.
 *
 * A blank value means "use the default row for this capability". The selectable list deliberately
 * carries no `isDefault`/endpoint/health — a member is not shown where a workspace's keys point —
 * so the placeholder names the capability rather than the default row.
 */
export function ProviderSelect({
  capability, value, onChange, label, description, size = 'xs', error, nullMeans, wrapperProps,
}: ProviderSelectProps) {
  const t2 = useT('wfSteps.kit.provider');
  const { isPlatformAdmin, isOrgAdmin, user } = useAuth();
  const role = user?.orgRole ?? null;
  // Mirrors the endpoint's gate (InferenceProvidersController.Selectable): every role but Billing.
  // Asking anyway would only spend a request on a guaranteed 403.
  const maySelect = !!role && role !== 'Billing';
  const { data: providers, isLoading, error: fetchError } = useSelectableProviders(capability, maySelect);
  const denied = !maySelect || (fetchError instanceof ApiError && fetchError.status === 403);
  const noun = CAPABILITY_KEYS.has(capability) ? t2(`capability.${capability}`) : capability;

  // The server already returns only enabled rows of this capability; the client-side filter is a
  // guard against a stale cache entry, not a second policy.
  const eligible = (providers ?? []).filter((p) => p.capability === capability);
  const toOption = (p: (typeof eligible)[number]) => ({ value: p.id, label: selectableLabel(p) });
  const platform = eligible.filter((p) => p.isPlatformManaged).map(toOption);
  const own = eligible.filter((p) => !p.isPlatformManaged).map(toOption);
  const extra: { value: string; label: string }[] = [];
  // A stored id that no longer resolves must stay visible, otherwise the select would silently show "empty".
  if (value && providers && !eligible.some((p) => p.id === value)) {
    extra.push({ value, label: t2('unknownModel') });
  }
  const options = [
    ...(platform.length > 0 ? [{ group: 'ReelBolt', items: platform }] : []),
    ...(own.length > 0 ? [{ group: t2('groupWorkspace'), items: own }] : []),
    ...(extra.length > 0 ? [{ group: t2('groupUnavailable'), items: extra }] : []),
  ];

  let placeholder: string;
  if (denied) placeholder = t2('placeholder.denied', { noun });
  else if (isLoading) placeholder = t2('placeholder.loading');
  else if (eligible.length === 0) placeholder = t2('placeholder.none', { noun });
  else placeholder = nullMeans ?? t2('placeholder.default', { noun });

  const empty = !denied && !isLoading && providers && eligible.length === 0;

  return (
    <div>
      <Select
        label={label ?? t2('model')}
        description={description}
        size={size}
        data={options}
        value={value ?? null}
        onChange={(v) => onChange(v)}
        placeholder={placeholder}
        clearable
        searchable
        disabled={denied || eligible.length === 0}
        error={error}
        wrapperProps={wrapperProps}
        onClick={(e) => e.stopPropagation()}
      />
      {denied && (
        <Text size="xs" c="dimmed" mt={4}>
          <IconInfoCircle size={12} style={{ verticalAlign: -1 }} /> {t2('deniedNote', { noun: noun.toLowerCase() })}
        </Text>
      )}
      {empty && (
        <Text size="xs" c="dimmed" mt={4}>
          <IconInfoCircle size={12} style={{ verticalAlign: -1 }} /> {t2('emptyNote', { noun })}{' '}
          {isPlatformAdmin ? (
            t2.rich('addPlatform', { link: (chunks) => <Link href="/admin/inference-providers">{chunks}</Link> })
          ) : isOrgAdmin ? (
            t2.rich('addOrg', { link: (chunks) => <Link href="/org/inference-providers">{chunks}</Link> })
          ) : (
            t2('askAdmin')
          )}
          {t2('sentenceEnd')}{nullMeans ? ` ${nullMeans}` : ''}
        </Text>
      )}
    </div>
  );
}

/* ------------------------------------------------------------------ step order select */

interface StepOrderSelectProps {
  steps: StepData[];
  currentIndex: number;
  /** Which steps may be picked relative to this one. */
  scope: 'before' | 'after' | 'other';
  value: number | string | null | undefined;
  onChange: (order: number | null) => void;
  label?: string;
  description?: React.ReactNode;
  size?: string;
  placeholder?: string;
  error?: React.ReactNode;
  wrapperProps?: WrapProps;
}

export function stepOrderOptions(
  steps: StepData[],
  currentIndex: number,
  scope: 'before' | 'after' | 'other',
  resolve: (m: Msg) => string,
  unlabelled: (stepType: string) => Msg = (stepType) => msg('wfSteps.kit.stepOrder.unlabelled', { stepType }),
) {
  return steps
    .map((s, i) => ({ s, i }))
    .filter(({ i }) => (scope === 'before' ? i < currentIndex : scope === 'after' ? i > currentIndex : i !== currentIndex))
    .map(({ s, i }) => ({
      value: String(i + 1),
      label: `${i + 1}. ${s.label?.trim() || resolve(unlabelled(s.stepType))}`,
    }));
}

/** A step picker (by 1-based order) instead of a typed number. */
export function StepOrderSelect({
  steps, currentIndex, scope, value, onChange, label, description, size = 'sm', placeholder, error, wrapperProps,
}: StepOrderSelectProps) {
  const t3 = useT('wfSteps.kit.stepOrder');
  const options = useMemo(
    () => stepOrderOptions(steps, currentIndex, scope, t3.msg),
    [steps, currentIndex, scope, t3],
  );
  const v = value === null || value === undefined || value === '' ? null : String(value);
  const data = v && !options.some((o) => o.value === v) ? [...options, { value: v, label: t3('unavailable', { order: v }) }] : options;
  return (
    <Select
      label={label}
      description={description}
      size={size}
      data={data}
      value={v}
      onChange={(x) => onChange(x ? Number(x) : null)}
      placeholder={placeholder ?? (options.length > 0 ? t3('select') : t3('none'))}
      disabled={options.length === 0}
      clearable
      error={error}
      wrapperProps={wrapperProps}
      onClick={(e) => e.stopPropagation()}
    />
  );
}

/* ------------------------------------------------------------------ small blocks */

export function CostWarning({ children }: { children: React.ReactNode }) {
  return (
    <Alert color="yellow" variant="light" p="xs" icon={<IconAlertTriangle size={14} />}>
      <Text size="xs">{children}</Text>
    </Alert>
  );
}

export function EgressWarning({ children }: { children: React.ReactNode }) {
  const t = useT('wfSteps.kit');
  return (
    <Alert color="orange" variant="light" p="xs" icon={<IconAlertTriangle size={14} />} title={t('egressTitle')}>
      <Text size="xs">{children}</Text>
    </Alert>
  );
}

/** A mode-by-mode explanation line under a Select. */
export function ModeHint({ children }: { children: React.ReactNode }) {
  return (
    <Group gap={6} wrap="nowrap" align="flex-start">
      <IconInfoCircle size={14} style={{ flexShrink: 0, marginTop: 2 }} />
      <Text size="xs" c="dimmed">{children}</Text>
    </Group>
  );
}

/** Prompt builder shared by every "Ask assistant" button. */
export function assistantPrompt(stepIndex: number, stepType: string, topic?: Msg): Msg {
  const values = { order: stepIndex + 1, stepType };
  return topic
    ? msg('wfSteps.kit.assistantPrompt.withTopic', { ...values, topic })
    : msg('wfSteps.kit.assistantPrompt.generic', values);
}

/* ------------------------------------------------------------------ advanced disclosure */

/**
 * "Advanced" disclosure for the internals a customer rarely needs (codecs, budgets, thresholds,
 * output formats, decision-model knobs). Collapsed by default, so the step settings lead with the
 * choices that change the video. It opens on its own when the assistant proposes a change or a
 * rejected save names a field — a highlight hidden behind a closed toggle would be invisible.
 *
 * Children stay mounted while collapsed (Mantine `Collapse`), so field-level highlight markers and
 * existing values are always in the DOM.
 */
export function AdvancedSection({
  children, label, description, defaultOpen = false,
}: {
  children: React.ReactNode;
  label?: string;
  description?: React.ReactNode;
  defaultOpen?: boolean;
}) {
  const t = useT('wfSteps.kit');
  const diff = useContext(FieldDiffContext);
  const errors = useContext(FieldErrorContext);
  const hasHighlights = diff !== null || errors !== null;
  const [open, setOpen] = useState(defaultOpen || hasHighlights);
  return (
    <Stack gap={4}>
      <Button
        variant="subtle"
        size="compact-xs"
        color="gray"
        leftSection={<IconSettings size={14} />}
        rightSection={open ? <IconChevronUp size={14} /> : <IconChevronDown size={14} />}
        onClick={(e) => {
          e.stopPropagation();
          setOpen(!open);
        }}
        aria-expanded={open}
        style={{ alignSelf: 'flex-start' }}
      >
        {label ?? t('advanced')}
      </Button>
      {description && !open && <Text size="xs" c="dimmed">{description}</Text>}
      <Collapse expanded={open}>
        <Stack gap="md" pt="xs">{children}</Stack>
      </Collapse>
    </Stack>
  );
}

/* ------------------------------------------------------------------ no-audio notice */

/** True once the project's file list has loaded and holds no audio file. `undefined` while loading. */
export function useProjectHasNoAudio(projectId: string | undefined): boolean | undefined {
  const { data: files } = useProjectFiles(projectId ?? '');
  if (!projectId || !files) return undefined;
  return !files.some((f) => f.mimeType?.startsWith('audio/'));
}

/**
 * Shown wherever music or sound effects are switched on: both pick ONLY from audio files the
 * customer uploaded, and with none the video silently comes out without them. Renders nothing
 * while the file list loads, when the project has audio, or without a project.
 */
export function NoAudioFilesNotice({ projectId, kind }: { projectId: string | undefined; kind: 'music' | 'sfx' | 'both' }) {
  const t4 = useT('wfSteps.kit.noAudio');
  const noAudio = useProjectHasNoAudio(projectId);
  if (!noAudio || !projectId) return null;
  const what = t4(`what.${kind}`);
  return (
    <Alert color="yellow" variant="light" p="xs" icon={<IconMusic size={14} />} title={t4('title', { what })}>
      <Text size="xs">
        {t4.rich('body', {
          what,
          without: t4(`without.${kind}`),
          link: (chunks) => <Anchor component={Link} href={`/projects/${projectId}?tab=files`} size="xs">{chunks}</Anchor>,
        })}
      </Text>
    </Alert>
  );
}
