'use client';

import React, { useId, useState } from 'react';
import { Checkbox, Group, Stack, Text } from '@mantine/core';
import { IconBox, IconUserCircle } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { projectFileUrl } from '@/lib/api/files';
import { isObjectAvatar, primaryPictureFileId } from '@/lib/avatars/model';
import { subjectBlock, subjectGroups, subjectLimit, subjectUnusable, toggleSubject, type SubjectRules } from '@/lib/avatars/subjects';
import type { ProjectAvatar } from '@/lib/types/avatars';
import { useAvatarWords } from './avatar-words';

export interface SubjectPickerProps {
  projectId: string;
  /** The project's people and objects (`useProjectAvatars`). */
  avatars: readonly ProjectAvatar[] | null | undefined;
  /** The chosen avatar ids, in the order they were chosen. */
  value: readonly string[];
  onChange: (ids: string[]) => void;
  /** A picture, not a video: a person whose consent does not cover pictures is listed with the reason, not offered. */
  forPictures?: boolean;
  /** The most subjects allowed here (defaults to 3). */
  max?: number;
  /** The heading above the list. */
  label?: string;
  /** One line under the heading. */
  hint?: string;
  /** Nothing can be changed (the tool around it is unavailable, or a run is in flight). */
  disabled?: boolean;
  /** Touch screen: 44 px rows. */
  touch?: boolean;
}

/** The avatar's first picture, small; an icon by kind when it has none to show or the picture does not load. */
function SubjectThumb({ projectId, avatar }: { projectId: string; avatar: ProjectAvatar }) {
  const fileId = primaryPictureFileId(avatar);
  const [failedFor, setFailedFor] = useState<string | null>(null);
  const box: React.CSSProperties = { width: 32, height: 32, flex: '0 0 32px', borderRadius: 6, overflow: 'hidden', display: 'grid', placeItems: 'center', background: 'var(--mantine-color-default-hover)' };
  return (
    <span style={box} aria-hidden data-testid={`subject-thumb-${avatar.id}`}>
      {fileId && failedFor !== fileId ? (
        // eslint-disable-next-line @next/next/no-img-element -- authenticated same-origin stream, not an optimizable static asset
        <img src={projectFileUrl(projectId, fileId)} alt="" loading="lazy" width={32} height={32} style={{ width: '100%', height: '100%', objectFit: 'cover' }} onError={() => setFailedFor(fileId)} />
      ) : isObjectAvatar(avatar) ? (
        <IconBox size={20} stroke={1.4} />
      ) : (
        <IconUserCircle size={20} stroke={1.4} />
      )}
    </span>
  );
}

/**
 * Choose which of the project's people and objects to show: up to three, at most one of them a person
 * (docs/design/avatar-objects.md). People and objects are listed under a heading each. One that cannot be used
 * stays listed, greyed, with the reason in plain words, so nothing is hidden silently. A ticked one can always be
 * unticked. Shared by the timeline's "Generate clip" dialog and the picture editor's AI tools.
 */
export function SubjectPicker({ projectId, avatars, value, onChange, forPictures = false, max, label, hint, disabled = false, touch = false }: SubjectPickerProps) {
  const t = useT('avatars.subjects');
  const words = useAvatarWords();
  const headingId = useId();
  const rules: SubjectRules = { forPictures, max };
  const limit = subjectLimit(max);
  const groups = subjectGroups(avatars);
  const list = avatars ?? [];

  const reasonFor = (avatar: ProjectAvatar): string | null => {
    // A ticked one that stopped being usable stays ticked, with the reason, so the person sees why and can untick it.
    const never = subjectUnusable(avatar, rules);
    if (never === 'unusable') return words.reasonOf(avatar);
    if (never === 'noPictureConsent') return t('blocked.noPictureConsent');
    const block = subjectBlock(avatar, value, list, rules);
    if (block === 'tooMany') return t('blocked.tooMany', { max: limit });
    if (block === 'secondPerson') return t('blocked.secondPerson');
    return null;
  };

  return (
    <Stack gap={6} role="group" aria-labelledby={headingId} data-testid="subject-picker">
      <div>
        <Text size="sm" fw={600} id={headingId}>{label ?? t('label')}</Text>
        <Text size="xs" c="dimmed">{hint ?? t('rule', { max: limit })}</Text>
      </div>
      {groups.length === 0 && (
        <Text size="sm" c="dimmed" data-testid="subject-picker-empty">{t('empty')}</Text>
      )}
      {groups.map((group) => (
        <Stack key={group.kind} gap={4} role="group" aria-label={group.kind === 'Object' ? t('objects') : t('people')} data-testid={`subject-group-${group.kind === 'Object' ? 'objects' : 'people'}`}>
          <Text size="xs" fw={600} c="dimmed" aria-hidden>{group.kind === 'Object' ? t('objects') : t('people')}</Text>
          {group.avatars.map((avatar) => {
            const checked = value.includes(avatar.id);
            const blocked = !checked && subjectBlock(avatar, value, list, rules) !== null;
            const reason = reasonFor(avatar);
            return (
              <Checkbox
                key={avatar.id}
                size="sm"
                checked={checked}
                disabled={disabled || blocked}
                onChange={() => onChange(toggleSubject(value, avatar, list, rules))}
                data-testid={`subject-option-${avatar.id}`}
                styles={{ root: { minHeight: touch ? 44 : 32, display: 'flex', alignItems: 'center' }, body: { alignItems: 'center', width: '100%' }, labelWrapper: { flex: 1, minWidth: 0 } }}
                label={
                  <Group gap={8} wrap="nowrap" align="center">
                    <SubjectThumb projectId={projectId} avatar={avatar} />
                    <span style={{ minWidth: 0 }}>
                      <Text size="sm" truncate component="span" display="block">{avatar.name}</Text>
                      {reason && <Text size="xs" c="dimmed" component="span" display="block" data-testid={`subject-reason-${avatar.id}`}>{reason}</Text>}
                    </span>
                  </Group>
                }
              />
            );
          })}
        </Stack>
      ))}
      {value.length > 0 && (
        <Text size="xs" c="dimmed" role="status" data-testid="subject-picker-count">{t('chosen', { count: value.length, max: limit })}</Text>
      )}
    </Stack>
  );
}
