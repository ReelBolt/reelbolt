'use client';

import { Button, Menu, Text } from '@mantine/core';
import { IconFiles, IconPlus, IconWand } from '@tabler/icons-react';
import { isObjectAvatar } from '@/lib/avatars/model';
import { MAX_VARIANTS, aiVariantBlock, type AddVariantBlock } from '@/lib/avatars/variants';
import type { ProjectAvatar } from '@/lib/types/avatars';
import { useT } from '@/i18n';
import { useAvatarName } from '../avatar-names';

/** How a variant is made: from other files of the project, or by the picture model changing the original's own pictures. */
export type AddVariantMode = 'files' | 'ai';

/**
 * "Add a variant" on an original's card (docs/design/avatar-objects.md "Avatar variants"), with the two ways to
 * make one. The picture model changes OBJECTS only: for a person the choice is not offered, and one quiet line
 * says a person's variant is made from other files. At the limit the button gives way to a plain line; where a
 * variant cannot be added at all (a variant, an import from the library, a kind not in the plan) nothing shows.
 */
export function AddVariantMenu({ avatar, block, onAdd }: {
  avatar: ProjectAvatar;
  /** `addVariantBlock(avatar, …)`: null offers the menu. */
  block: AddVariantBlock | null;
  onAdd: (mode: AddVariantMode) => void;
}) {
  const t = useT('avatars.variants.add');
  const nameOf = useAvatarName();
  if (block === 'limit') {
    return (
      <Text size="sm" c="dimmed" mt="sm" data-testid="variant-limit">
        {isObjectAvatar(avatar) ? t('limitObject', { max: MAX_VARIANTS }) : t('limitPerson', { max: MAX_VARIANTS })}
      </Text>
    );
  }
  if (block !== null) return null;
  const ai = aiVariantBlock(avatar);

  return (
    <Menu position="bottom-start" withinPortal>
      <Menu.Target>
        <Button
          mt="sm" variant="subtle" leftSection={<IconPlus size={16} />} style={{ minHeight: 44, alignSelf: 'flex-start' }}
          aria-label={t('buttonFor', { name: nameOf(avatar) })} data-testid={`add-variant-${avatar.id}`}
        >
          {t('button')}
        </Button>
      </Menu.Target>
      <Menu.Dropdown maw={320}>
        <Menu.Item leftSection={<IconFiles size={15} />} onClick={() => onAdd('files')}>{t('fromFiles')}</Menu.Item>
        {ai !== 'person' && (
          <Menu.Item leftSection={<IconWand size={15} />} onClick={() => onAdd('ai')} disabled={ai === 'unusable'}>{t('withAi')}</Menu.Item>
        )}
        {ai !== null && (
          <Text size="sm" c="dimmed" px="sm" py={6} data-testid="variant-ai-note">
            {ai === 'person' ? t('personNote') : t('unusableNote')}
          </Text>
        )}
      </Menu.Dropdown>
    </Menu>
  );
}
