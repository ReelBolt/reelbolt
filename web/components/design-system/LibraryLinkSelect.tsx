'use client';

import { useState } from 'react';
import { Button, Group, Select, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { linkProjectLibrary } from '@/lib/api/design-system';
import { useDesignLibraries } from '@/lib/hooks/use-design-libraries';
import type { DesignSystemParentLibrary } from '@/lib/types/design-system';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

/**
 * SPEC §16.1/P4: link the project's design system to one of the active organization's brand
 * libraries, or clear the link. The list comes from the org route; the current link is the parent
 * library the project summary already carries. Writing needs Owner/Admin — a Member sees the value
 * read-only, and the API (`OrgLibraryGate.Write`) is the real guard.
 */
export function LibraryLinkSelect({
  projectId,
  current,
  canWrite,
  onChanged,
}: {
  projectId: string;
  current: DesignSystemParentLibrary | null;
  canWrite: boolean;
  onChanged: () => void;
}) {
  const { data: libraries, isLoading } = useDesignLibraries();
  const tr = useT('brandLibraries.profile.libraryLink');
  const tAll = useT();
  const [saving, setSaving] = useState(false);
  // What the user last saved, shown until the project summary catches up: the summary refetch is
  // asynchronous (and an older server never reports the link), so reading only `current` would snap
  // the control back to "Not linked" right after a successful link.
  // `base` is the summary's link at save time: once the summary reports something else, it has
  // caught up and the saved choice is no longer needed.
  const [saved, setSaved] = useState<{ id: string | null; base: string | null } | null>(null);
  const currentId = current?.id ?? null;
  const selectedId = saved && saved.base === currentId ? saved.id : currentId;

  const options = (libraries ?? []).map((library) => ({
    value: library.id,
    label: library.designSystem.currentVersion
      ? tr('optionLabel', { name: library.name, version: library.designSystem.currentVersion.versionNumber })
      : library.name,
  }));
  // A link whose library is no longer in the org list still has to render as the current value.
  if (current && !options.some((option) => option.value === current.id)) {
    options.push({ value: current.id, label: current.name });
  }

  async function update(next: string | null) {
    setSaving(true);
    try {
      await linkProjectLibrary(projectId, next);
      setSaved({ id: next, base: currentId });
      onChanged();
      notifications.show({
        title: tr('savedTitle'),
        message: next ? tr('linked') : tr('unlinked'),
        color: 'green',
      });
    } catch (err: unknown) {
      notifications.show({
        title: tr('errorTitle'),
        message:
          describeError(tAll, err, tr('updateFailed')),
        color: 'red',
      });
    } finally {
      setSaving(false);
    }
  }

  if (!canWrite) {
    return (
      <Text size="sm" c="dimmed" data-testid="library-link-readonly">
        {current ? tr('readOnlyCurrent', { name: current.name }) : tr('readOnlyNone')}
      </Text>
    );
  }

  return (
    <Group align="flex-end" gap="sm" wrap="nowrap">
      <Select
        style={{ flex: 1 }}
        label={tr('label')}
        description={tr('description')}
        placeholder={isLoading ? tr('loading') : tr('notLinked')}
        data={options}
        value={selectedId}
        onChange={(value) => void update(value)}
        disabled={saving || isLoading}
        searchable
        data-testid="library-link-select"
      />
      {selectedId && (
        <Button variant="default" onClick={() => void update(null)} loading={saving}>
          {tr('unlink')}
        </Button>
      )}
    </Group>
  );
}
