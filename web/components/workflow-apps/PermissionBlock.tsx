'use client';

import { Switch, Text } from '@mantine/core';
import { useT } from '@/i18n';
import type { T } from '@/i18n/translator';
import type { AppManifestInput, AppMediaEgress, AppMediaEgressStep } from '@/lib/types/workflow';
import { inputFieldId } from './app-input-model';
import styles from './AppRunPage.module.css';

const REASONS = new Set(['tracksAnObjectByName', 'startsFromFrame', 'showsAnAvatar', 'plannedFrames', 'plannedFramesAndSubjects', 'enhancesVideoWithAi', 'cleansAudioWithAi']);

/** Why one step sends media, in the reader's words ("shows an avatar"). `t` is bound to `apps.permission.mediaEgress`. */
function reasonWords(t: T, step: AppMediaEgressStep): string {
  if (step.reason === 'showsAnAvatar' && step.need === 'optional') return t('reason.showsAnAvatarOptional');
  return t(`reason.${REASONS.has(step.reason) ? step.reason : 'other'}`);
}

/**
 * The steps the permission is for, as one sentence: "Needed by: Make the clip (shows an avatar)." Steps whose own
 * switch is already on are left out unless every step is. Falls back to the server's English sentence when the page
 * was given no step list.
 */
export function permissionSentence(
  t: T, egress: AppMediaEgress | null | undefined, fallback?: string | null, required?: boolean,
): string | null {
  if (!egress || egress.steps.length === 0) return fallback?.trim() || null;
  const pending = egress.steps.filter((s) => !s.allowedInStep);
  const named = egress.alreadyAllowed || pending.length === 0 ? egress.steps : pending;
  const steps = named.map((s) => t('step', { name: s.stepName, reason: reasonWords(t, s) })).join(', ');
  if (egress.alreadyAllowed) return t('alreadyAllowed', { steps });
  return (required ?? egress.required) ? t('neededBy', { steps }) : t('usedBy', { steps });
}

interface PermissionBlockProps {
  /** The app's own permission field (`permission: "mediaEgress"`). */
  input: AppManifestInput & { id: string };
  egress?: AppMediaEgress | null | undefined;
  checked: boolean;
  onChange: (checked: boolean) => void;
  /** Shown under the switch: the run gate's refusal, or "allow it to run" after a blocked press. */
  error?: string | undefined;
  /** Whether the run being prepared needs it (the page knows the current choices); default: what the app said. */
  required?: boolean | undefined;
}

/**
 * The permission to send the person's media to AI services, in its own bordered block above Run
 * (docs/design/app-inputs.md "Permission to send media as an app field"): one switch, which steps need it and why,
 * and that it applies to this run only. When every step already allows it in its own settings the switch is shown on
 * and cannot be changed here (it would change nothing).
 */
export function PermissionBlock({ input, egress, checked, onChange, error, required: neededNow }: PermissionBlockProps) {
  const t = useT('apps.permission.mediaEgress');
  const id = inputFieldId(input.id);
  const locked = egress?.alreadyAllowed === true;
  const required = neededNow ?? (egress ? egress.required : input.required === true);
  const sentence = permissionSentence(t, egress, input.help, required);
  return (
    <section className={styles.permission} aria-labelledby={`${id}-title`} data-testid="app-permission-block">
      <Text size="sm" fw={600} id={`${id}-title`} className={styles.permissionTitle}>
        {t('title')}
      </Text>
      <Switch
        id={id}
        size="md"
        label={t('label')}
        checked={locked || checked}
        disabled={locked}
        required={required && !locked}
        aria-describedby={`${id}-why${error ? ` ${id}-error` : ''}`}
        aria-invalid={error ? true : undefined}
        onChange={(e) => onChange(e.currentTarget.checked)}
        className={styles.field}
        data-testid="app-permission-switch"
      />
      <Text size="sm" className={styles.muted} id={`${id}-why`}>
        {sentence ? `${sentence} ` : ''}
        {locked ? '' : t('thisRunOnly')}
      </Text>
      {error && (
        <Text size="sm" className={styles.fieldError} role="alert" id={`${id}-error`} data-testid="app-permission-error">
          {error}
        </Text>
      )}
    </section>
  );
}
