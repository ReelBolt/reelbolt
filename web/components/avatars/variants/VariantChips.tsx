'use client';

import { Button, Group, Menu, Stack, Text, Tooltip } from '@mantine/core';
import { IconChevronDown, IconFileDescription } from '@tabler/icons-react';
import { canSaveToLibrary, isLinkedAvatar } from '@/lib/avatars/library';
import { avatarStatus, isObjectAvatar, statusColor } from '@/lib/avatars/model';
import type { AvatarLibraryRequirements, ProjectAvatar } from '@/lib/types/avatars';
import { useT } from '@/i18n';
import { LinkedMenuItems, OwnMenuItems, type CardAction } from '../AvatarCard';
import { useAvatarWords } from '../avatar-words';

/**
 * The variants of one avatar, as labelled chips under its card (docs/design/avatar-objects.md "Avatar variants").
 * Each chip is a button: it shows the variant's label and a dot for whether it can be used, says what sets the
 * variant apart (a tooltip, and again at the top of its menu), and opens the variant's own actions, the same ones
 * any avatar of its kind has.
 */
export function VariantChips({ original, variants, library, onOpen }: {
  original: ProjectAvatar;
  variants: readonly ProjectAvatar[];
  /** The workspace library, when the server has one; null hides everything about it. */
  library: AvatarLibraryRequirements | null;
  onOpen: (variant: ProjectAvatar, action: CardAction) => void;
}) {
  const t = useT('avatars.variants.chips');
  if (variants.length === 0) return null;
  return (
    <Stack gap={6} mt="sm" data-testid={`variant-chips-${original.id}`}>
      <Text size="sm" c="dimmed" id={`variants-of-${original.id}`}>{t('title', { count: variants.length })}</Text>
      <Group gap={6} role="list" aria-labelledby={`variants-of-${original.id}`}>
        {variants.map((v) => (
          <div role="listitem" key={v.id}>
            <VariantChip original={original} variant={v} library={library} onOpen={(action) => onOpen(v, action)} />
          </div>
        ))}
      </Group>
    </Stack>
  );
}

function VariantChip({ original, variant, library, onOpen }: {
  original: ProjectAvatar;
  variant: ProjectAvatar;
  library: AvatarLibraryRequirements | null;
  onOpen: (action: CardAction) => void;
}) {
  const t = useT('avatars.variants.chips');
  const tCard = useT('avatars.card');
  const words = useAvatarWords();
  const status = avatarStatus(variant);
  const statusLabel = words.statusLabel(variant, status);
  const isObject = isObjectAvatar(variant);
  const linked = isLinkedAvatar(variant);
  const revoked = !isObject && Boolean(variant.consent?.revokedAt);
  const label = (variant.variantLabel ?? '').trim() || variant.name;
  const description = (variant.variantDescription ?? '').trim();

  return (
    <Menu position="bottom-start" withinPortal>
      <Tooltip label={description} disabled={!description} multiline maw={280} withinPortal events={{ hover: true, focus: true, touch: false }}>
        <Menu.Target>
          <Button
            variant="default" radius="xl" size="sm" style={{ minHeight: 36 }} data-testid={`variant-chip-${variant.id}`} data-status={status}
            aria-label={t('chipLabel', { label, status: statusLabel, original: original.name })}
            leftSection={<span aria-hidden style={{ width: 8, height: 8, borderRadius: '50%', background: `var(--mantine-color-${statusColor(status)}-6)` }} />}
            rightSection={<IconChevronDown size={14} aria-hidden />}
          >
            {label}
          </Button>
        </Menu.Target>
      </Tooltip>
      <Menu.Dropdown maw={320}>
        <Stack gap={2} px="sm" py={6} data-testid={`variant-summary-${variant.id}`}>
          <Text size="sm" fw={600}>{statusLabel}</Text>
          {!variant.selectable && variant.unselectableReason && <Text size="sm" c="dimmed">{words.reasonOf(variant)}</Text>}
          {description && <Text size="sm">{description}</Text>}
        </Stack>
        <Menu.Divider />
        <Menu.Item leftSection={<IconFileDescription size={15} />} onClick={() => onOpen('details')}>{tCard('details')}</Menu.Item>
        {linked
          ? <LinkedMenuItems isObject={isObject} onOpen={onOpen} />
          : <OwnMenuItems isObject={isObject} revoked={revoked} canPublish={canSaveToLibrary(variant, library)} onOpen={onOpen} />}
      </Menu.Dropdown>
    </Menu>
  );
}
