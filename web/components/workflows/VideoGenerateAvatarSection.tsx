'use client';

import { Stack, Select, Checkbox, Group, Text, Title, SegmentedControl, Anchor } from '@mantine/core';
import Link from 'next/link';
import { useT } from '@/i18n';
import { useAvatarWords } from '@/components/avatars/avatar-words';
import { useAvatarRequirements, useProjectAvatars } from '@/lib/hooks/use-avatars';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { enabledKinds, isObjectAvatar } from '@/lib/avatars/model';
import {
  audioFiles,
  avatarBlock,
  effectiveRoles,
  keepsPicture,
  pickerGroups,
  pickerName,
  rolesPresent,
  rolesToStore,
  speechDrive,
  speechSource,
  voiceoverStepOrders,
  type SpeechSource,
} from '@/lib/avatars/step-picker';
import type { ProjectAvatar } from '@/lib/types/avatars';
import type { VideoGenerateStepConfig } from '@/lib/types/workflow';
import { useFd, useFieldError } from './config-kit';
import type { StepData } from './WorkflowStepList';

interface Props {
  config: VideoGenerateStepConfig;
  patch: (update: Partial<VideoGenerateStepConfig>) => void;
  projectId?: string;
  allSteps: StepData[];
  currentStepIndex: number;
}

/** A server message for a field, shown right under it. */
function FieldError({ path }: { path: string }) {
  const message = useFieldError(path);
  return message ? <Text size="xs" c="red" role="alert">{message}</Text> : null;
}

/**
 * The "Avatar" section of a VideoGenerate step (docs/avatars.md "Choosing an avatar in the dashboard"). Shown only
 * when the project's avatars answer: with both operator switches off every avatar route is 404, and nothing here renders.
 * It lists people and objects (under a heading each when the project has both); the speech controls are a person's
 * only, since an object says nothing.
 * The step's permission to send media stays its own switch, just below this section; nothing here turns it on.
 */
export function VideoGenerateAvatarSection({ config, patch, projectId, allSteps, currentStepIndex }: Props) {
  const t = useT('wfSteps.videoGenerate.avatar');
  const ta = useT('avatars');
  const words = useAvatarWords();
  const fd = useFd();
  const { data: requirements, error } = useAvatarRequirements(projectId);
  const enabled = Boolean(projectId && requirements && !error);
  const { data: avatars } = useProjectAvatars(projectId, enabled);
  const { data: files } = useProjectFiles(enabled ? (projectId ?? '') : '');
  if (!enabled) return null;

  const block = avatarBlock(config);
  const groups = pickerGroups(avatars);
  const ordered = groups.flatMap((g) => g.avatars);
  const chosen: ProjectAvatar | undefined = config.avatarId ? ordered.find((a) => a.id === config.avatarId) : undefined;
  const chosenIsObject = isObjectAvatar(chosen);
  const { reasonOf } = words;
  const option = (a: ProjectAvatar) => ({
    value: a.id,
    // A variant reads "Car · Red", right after its original.
    label: t('option', { name: pickerName(a, avatars), status: words.statusLabel(a) }),
    // A chosen avatar that became unusable stays visible (and selected) so the person sees why.
    disabled: !a.selectable && a.id !== config.avatarId,
  });
  type Option = { value: string; label: string; disabled?: boolean };
  // A heading per kind only when the project has both people and objects.
  const data: (Option | { group: string; items: Option[] })[] = [
    { value: '', label: t('none') },
    ...(groups.length > 1
      ? groups.map((g) => ({ group: g.kind === 'Object' ? t('groupObjects') : t('groupPeople'), items: g.avatars.map(option) }))
      : ordered.map(option)),
  ];
  if (config.avatarId && !chosen && avatars) data.push({ value: config.avatarId, label: t('missing'), disabled: true });
  const reasonById = new Map(ordered.filter((a) => !a.selectable).map((a) => [a.id, reasonOf(a)]));

  const present = rolesPresent(chosen);
  const roles = effectiveRoles(config.avatarReferenceRoles, present);
  const rolesOk = keepsPicture(roles);
  // What the intro promises follows what this server offers: people, objects, or both.
  const offered = enabledKinds(requirements);
  const intro = offered.length > 1 ? t('introAny') : offered[0] === 'Object' ? t('introObjects') : t('intro');

  const drive = speechDrive(config.model);
  const source = speechSource(config);
  const voiceoverOrders = voiceoverStepOrders(allSteps, currentStepIndex);
  const audio = audioFiles(files);
  const stepLabel = (order: number) => {
    const label = allSteps[order - 1]?.label?.trim();
    return label ? `${order}. ${label}` : t('speech.stepFallback', { n: order });
  };
  const changeSource = (next: string) => {
    const value = next as SpeechSource;
    if (value === 'none') patch({ avatarAudio: null, avatarAudioProjectFileId: null });
    else if (value === 'step') {
      const first = voiceoverOrders[voiceoverOrders.length - 1];
      patch({ avatarAudioProjectFileId: null, avatarAudio: first ? { from: 'Step', stepOrder: first } : null });
    } else patch({ avatarAudio: null, avatarAudioProjectFileId: audio[0]?.id ?? null });
  };

  const usesPersonMedia = Boolean(config.avatarId || config.avatarAudio || config.avatarAudioProjectFileId);
  // With no avatar on the step, the one chosen when the run starts is shown (inline steps only).
  const noneDescription = block ? t(`blocked.${block}`) : config.avatarId ? undefined : t('noneMeansRun');

  return (
    <Stack gap="xs" data-testid="video-generate-avatar-section">
      <Title order={4} size="h6">{t('title')}</Title>
      <Text size="sm" c="dimmed">{intro}</Text>
      <Select
        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
        {...fd('config.avatarId')}
        label={t('label')}
        description={noneDescription}
        size="xs"
        data={data}
        value={config.avatarId ?? ''}
        disabled={Boolean(block) && !config.avatarId}
        onChange={(v) => {
          if (!v) patch({ avatarId: null, avatarReferenceRoles: null });
          // An object says nothing: choosing one drops a speech track set for a person.
          else if (isObjectAvatar(ordered.find((a) => a.id === v))) patch({ avatarId: v, avatarReferenceRoles: null, avatarAudio: null, avatarAudioProjectFileId: null });
          else patch({ avatarId: v, avatarReferenceRoles: null });
        }}
        allowDeselect={false}
        comboboxProps={{ withinPortal: true }}
        renderOption={({ option }) => {
          const reason = reasonById.get(option.value);
          return (
            <Stack gap={0}>
              <Text size="sm">{option.label}</Text>
              {reason && <Text size="xs" c="dimmed">{reason}</Text>}
            </Stack>
          );
        }}
      />
      <FieldError path="config.avatarId" />
      {block && config.avatarId && <Text size="xs" c="red">{t(`blocked.${block}`)}</Text>}
      {chosen && !chosen.selectable && (
        <Text size="xs" c="red" data-testid="avatar-unusable">{t('unusable', { name: pickerName(chosen, avatars), reason: reasonOf(chosen) })}</Text>
      )}
      {config.avatarId && !chosen && avatars && <Text size="xs" c="red">{t('missingHint')}</Text>}
      {ordered.length === 0 && avatars && (
        <Text size="xs" c="dimmed">
          {t('noAvatars')}{' '}
          <Anchor component={Link} href={`/projects/${projectId}?tab=avatars`} size="xs">{t('openAvatars')}</Anchor>
        </Text>
      )}

      {chosen && present.length > 0 && (
        <Checkbox.Group
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.avatarReferenceRoles')}
          label={t('roles.label')}
          description={t('roles.description')}
          size="xs"
          value={roles}
          onChange={(ticked) => patch({ avatarReferenceRoles: rolesToStore(ticked, present) })}
          error={rolesOk ? undefined : chosenIsObject ? t('roles.needPictureObject') : t('roles.needPicture')}
        >
          <Group gap="md" mt={4}>
            {present.map((r) => <Checkbox key={r} value={r} label={ta(`roles.${r}`)} size="xs" />)}
          </Group>
        </Checkbox.Group>
      )}
      <FieldError path="config.avatarReferenceRoles" />

      {!block && chosenIsObject && (
        <Text size="xs" c="dimmed" data-testid="avatar-speech-not-for-objects">{t('speech.notForObjects')}</Text>
      )}
      {!block && !chosenIsObject && (
        <Stack gap={4}>
          <Text size="xs" fw={500} id="avatar-speech-label">{t('speech.label')}</Text>
          <Text size="xs" c={drive === 'no' && source !== 'none' ? 'orange' : 'dimmed'} data-testid="avatar-speech-hint">
            {t(`speech.hint.${drive}`)}
          </Text>
          <SegmentedControl
            aria-labelledby="avatar-speech-label"
            size="xs"
            fullWidth
            value={source}
            onChange={changeSource}
            data={[
              { value: 'none', label: t('speech.none') },
              { value: 'step', label: t('speech.fromStep') },
              { value: 'file', label: t('speech.fromFile') },
            ]}
          />
          {source === 'step' && (
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.avatarAudio')}
              aria-label={t('speech.stepLabel')}
              size="xs"
              placeholder={t('speech.pickStep')}
              data={voiceoverOrders.map((o) => ({ value: String(o), label: stepLabel(o) }))}
              value={config.avatarAudio?.from === 'Step' && config.avatarAudio.stepOrder != null ? String(config.avatarAudio.stepOrder) : null}
              onChange={(v) => patch({ avatarAudio: v ? { from: 'Step', stepOrder: Number(v) } : null })}
              description={voiceoverOrders.length === 0 ? t('speech.noVoiceoverStep') : undefined}
              comboboxProps={{ withinPortal: true }}
            />
          )}
          {source === 'file' && (
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.avatarAudioProjectFileId')}
              aria-label={t('speech.fileLabel')}
              size="xs"
              searchable
              placeholder={t('speech.pickFile')}
              data={audio.map((file) => ({ value: file.id, label: file.originalFileName }))}
              value={config.avatarAudioProjectFileId ?? null}
              onChange={(v) => patch({ avatarAudioProjectFileId: v ?? null })}
              description={audio.length === 0 ? t('speech.noAudioFiles') : undefined}
              comboboxProps={{ withinPortal: true }}
            />
          )}
          <FieldError path="config.avatarAudio" />
          <FieldError path="config.avatarAudioProjectFileId" />
        </Stack>
      )}

      {usesPersonMedia && !config.allowSourceMediaEgress && (
        <Text size="xs" c="orange" data-testid="avatar-egress-hint">{t('egressNeeded')}</Text>
      )}
    </Stack>
  );
}
