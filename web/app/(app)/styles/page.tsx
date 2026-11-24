'use client';

import { useState } from 'react';
import { Badge, Button, Card, Center, Group, Loader, Modal, SimpleGrid, Stack, Text } from '@mantine/core';
import { IconCopy, IconPencil, IconPlus, IconTrash } from '@tabler/icons-react';
import { notifications } from '@mantine/notifications';
import { PageHeader } from '@/components/shared/PageHeader';
import { EmptyState } from '@/components/shared/EmptyState';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { StyleForm } from '@/components/styles/StyleForm';
import { RUN_FORMAT_CHOICES, describeStyle } from '@/components/styles/style-options';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { useEditingStyles, useStylePresets } from '@/lib/hooks/use-styles';
import { createEditingStyle, deleteEditingStyle, updateEditingStyle } from '@/lib/api/styles';
import type { EditingStyle, SaveEditingStyleRequest, StyleConfig } from '@/lib/types/styles';

type FormState =
  | { mode: 'create'; initial?: { name?: string; config?: StyleConfig } }
  | { mode: 'edit'; style: EditingStyle };

/**
 * Editing styles (docs/user-guide: "Editing styles"): the built-in looks everyone can pick when
 * running a workflow, and the user's own saved styles, which they can create, edit and delete here.
 */
export default function StylesPage() {
  const t = useT('styles.page');
  const tAll = useT();
  const { data: presets, isLoading: presetsLoading } = useStylePresets();
  const { data: styles, isLoading: stylesLoading, mutate } = useEditingStyles();
  const [form, setForm] = useState<FormState | null>(null);
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState<EditingStyle | null>(null);
  const [deleteBusy, setDeleteBusy] = useState(false);

  const save = async (request: SaveEditingStyleRequest) => {
    if (!form) return;
    setSaving(true);
    try {
      if (form.mode === 'edit') await updateEditingStyle(form.style.id, request);
      else await createEditingStyle(request);
      await mutate();
      notifications.show({ title: t('savedTitle'), message: t('savedBody', { name: request.name }), color: 'green' });
      setForm(null);
    } catch (err: unknown) {
      notifications.show({ title: t('notSavedTitle'), message: describeError(tAll, err, t('saveFailed')), color: 'red' });
    } finally {
      setSaving(false);
    }
  };

  const remove = async () => {
    if (!deleting) return;
    setDeleteBusy(true);
    try {
      await deleteEditingStyle(deleting.id);
      await mutate();
      setDeleting(null);
    } catch (err: unknown) {
      notifications.show({ title: t('notDeletedTitle'), message: describeError(tAll, err, t('deleteFailed')), color: 'red' });
    } finally {
      setDeleteBusy(false);
    }
  };

  if (presetsLoading || stylesLoading) {
    return <Center h={300}><Loader /></Center>;
  }

  return (
    <>
      <PageHeader title={t('title')}>
        <Button leftSection={<IconPlus size={16} />} onClick={() => setForm({ mode: 'create' })}>
          {t('new')}
        </Button>
      </PageHeader>
      <Text size="sm" c="dimmed" maw={680} mt={-8} mb="lg">
        {t('intro')}
      </Text>

      <Text fw={600} size="lg" mb="sm">{t('yours')}</Text>
      {styles && styles.length > 0 ? (
        <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }} mb="xl">
          {styles.map((style) => (
            <Card key={style.id} withBorder padding="md">
              <Stack gap={6}>
                <Group justify="space-between" wrap="nowrap">
                  <Text fw={600} truncate>{style.name}</Text>
                  {style.projectId && <Badge variant="light" size="sm">{t('oneProject')}</Badge>}
                </Group>
                {style.description && <Text size="sm">{style.description}</Text>}
                <Text size="xs" c="dimmed">{describeStyle(style.config, tAll)}</Text>
                <Group gap="xs" mt="xs">
                  <Button size="xs" variant="light" leftSection={<IconPencil size={14} />} onClick={() => setForm({ mode: 'edit', style })}>
                    {t('edit')}
                  </Button>
                  <Button size="xs" variant="subtle" color="red" leftSection={<IconTrash size={14} />} onClick={() => setDeleting(style)}>
                    {t('delete')}
                  </Button>
                </Group>
              </Stack>
            </Card>
          ))}
        </SimpleGrid>
      ) : (
        <EmptyState
          title={t('emptyTitle')}
          description={t('emptyBody')}
        />
      )}

      <Text fw={600} size="lg" mb="sm">{t('builtIn')}</Text>
      <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }}>
        {(presets ?? []).map((preset) => (
          <Card key={preset.key} withBorder padding="md">
            <Stack gap={6}>
              <Group justify="space-between" wrap="nowrap">
                <Text fw={600}>{preset.name}</Text>
                {preset.suggestedFormat !== 'Original' && (
                  <Badge variant="outline" size="sm">
                    {(() => { const f = RUN_FORMAT_CHOICES.find((o) => o.value === preset.suggestedFormat); return f ? tAll.msg(f.label) : null; })()}
                  </Badge>
                )}
              </Group>
              <Text size="sm">{preset.description}</Text>
              <Text size="xs" c="dimmed">{describeStyle(preset.config, tAll)}</Text>
              <Group gap="xs" mt="xs">
                <Button
                  size="xs"
                  variant="light"
                  leftSection={<IconCopy size={14} />}
                  onClick={() => setForm({ mode: 'create', initial: { name: t('copyName', { name: preset.name.toLowerCase() }), config: preset.config } })}
                >
                  {t('copy')}
                </Button>
              </Group>
            </Stack>
          </Card>
        ))}
      </SimpleGrid>

      <Modal
        opened={form !== null}
        onClose={() => setForm(null)}
        title={form?.mode === 'edit' ? t('editTitle', { name: form.style.name }) : t('newTitle')}
        size="lg"
        centered
      >
        {form && (
          <StyleForm
            initial={form.mode === 'edit' ? form.style : form.initial}
            busy={saving}
            onSubmit={save}
            onCancel={() => setForm(null)}
          />
        )}
      </Modal>

      <ConfirmModal
        opened={deleting !== null}
        onClose={() => setDeleting(null)}
        onConfirm={remove}
        title={t('deleteTitle')}
        message={t('deleteBody', { name: deleting?.name ?? '' })}
        loading={deleteBusy}
      />
    </>
  );
}
