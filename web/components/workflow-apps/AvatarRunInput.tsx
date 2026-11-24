'use client';

import Link from 'next/link';
import { Anchor, Select, Stack, Text } from '@mantine/core';
import { useT } from '@/i18n';
import { useAvatarWords } from '@/components/avatars/avatar-words';
import { isObjectAvatar } from '@/lib/avatars/model';
import { pickerGroups, pickerName } from '@/lib/avatars/step-picker';
import type { ProjectAvatar } from '@/lib/types/avatars';

interface Props {
  projectId: string;
  label?: string | null;
  avatars: ProjectAvatar[] | undefined;
  value: string | null;
  onChange: (avatarId: string | null) => void;
  className?: string;
}

/**
 * The run page's "Avatar" input (brief field `avatar`, docs/avatars.md "Choosing an avatar in the dashboard"): the
 * project's avatars, people and objects, by name with their status (under a heading each when the project has
 * both); one that cannot be used is listed, disabled, with the reason in the same words as the Avatars tab. The
 * page renders it only while the project's avatars answer (404 = both switches off).
 */
export function AvatarRunInput({ projectId, label, avatars, value, onChange, className }: Props) {
  const t = useT('apps.run.page');
  const words = useAvatarWords();
  const groups = pickerGroups(avatars);
  const ordered = groups.flatMap((g) => g.avatars);
  const hasObjects = groups.some((g) => g.kind === 'Object');
  const reasonById = new Map(ordered.filter((a) => !a.selectable).map((a) => [a.id, words.reasonOf(a)]));
  const option = (a: ProjectAvatar) => ({
    // A variant reads "Car · Red", right after its original.
    value: a.id, label: t('avatarOption', { name: pickerName(a, avatars), status: words.statusLabel(a) }), disabled: !a.selectable,
  });
  type Option = { value: string; label: string; disabled?: boolean };
  // A heading per kind only when the project has both people and objects.
  const data: (Option | { group: string; items: Option[] })[] = [
    { value: '', label: t('avatarNone') },
    ...(groups.length > 1
      ? groups.map((g) => ({ group: g.kind === 'Object' ? t('avatarGroupObjects') : t('avatarGroupPeople'), items: g.avatars.map(option) }))
      : ordered.map(option)),
  ];
  const chosen = value ? ordered.find((a) => a.id === value) : undefined;

  return (
    <Stack gap={4} className={className}>
      <Select
        id="app-input-avatar"
        label={label || t('avatar')}
        description={hasObjects ? t('avatarDescriptionAny') : t('avatarDescription')}
        data={data}
        value={chosen ? value : ''}
        onChange={(v) => onChange(v ? v : null)}
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
      {avatars && ordered.length === 0 && (
        <Text size="sm" c="dimmed">
          {t('avatarNoAvatars')}{' '}
          <Anchor component={Link} href={`/projects/${projectId}?tab=avatars`} size="sm">{t('avatarOpenTab')}</Anchor>
        </Text>
      )}
      {chosen && (
        <Text size="sm" c="dimmed" data-testid="avatar-sends">
          {isObjectAvatar(chosen) ? t('avatarSendsObject', { name: pickerName(chosen, avatars) }) : t('avatarSends', { name: pickerName(chosen, avatars) })}
        </Text>
      )}
    </Stack>
  );
}
