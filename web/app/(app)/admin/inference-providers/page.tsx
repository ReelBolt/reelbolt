'use client';

import { useState } from 'react';
import { Button, Loader, Center } from '@mantine/core';
import { IconPlus } from '@tabler/icons-react';
import { notifications } from '@mantine/notifications';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { useProviders } from '@/lib/hooks/use-inference-providers';
import { deleteProvider } from '@/lib/api/inference-providers';
import { PageHeader } from '@/components/shared/PageHeader';
import { InferenceProviderTable } from '@/components/admin/InferenceProviderTable';
import { InferenceProviderForm } from '@/components/admin/InferenceProviderForm';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { EmptyState } from '@/components/shared/EmptyState';
import type { InferenceProvider } from '@/lib/types/inference-provider';

export default function InferenceProvidersPage() {
  const tAll = useT();
  const { data: providers, isLoading, mutate } = useProviders('platform');
  const [formOpened, setFormOpened] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<InferenceProvider | null>(null);
  const [deleteLoading, setDeleteLoading] = useState(false);

  const handleCreateSuccess = () => {
    mutate();
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    setDeleteLoading(true);
    try {
      await deleteProvider(deleteTarget.id);
      mutate();
      notifications.show({ title: 'Deleted', message: 'Provider deleted', color: 'green' });
    } catch (err: unknown) {
      notifications.show({
        title: 'Error',
        message: describeError(tAll, err, 'Failed to delete provider'),
        color: 'red',
      });
    } finally {
      setDeleteLoading(false);
      setDeleteTarget(null);
    }
  };

  if (isLoading) {
    return <Center h={300}><Loader /></Center>;
  }

  return (
    <>
      <PageHeader title="Inference Providers">
        <Button w={{ base: '100%', sm: 'auto' }} h={{ base: 40, sm: 36 }} leftSection={<IconPlus size={16} />} onClick={() => setFormOpened(true)}>
          New Provider
        </Button>
      </PageHeader>

      {providers && providers.length > 0 ? (
        <InferenceProviderTable providers={providers} onDelete={setDeleteTarget} />
      ) : (
        <EmptyState
          title="No inference providers"
          description="Add a provider account (its endpoint and key), then the models it serves and what each model can do."
        />
      )}

      <InferenceProviderForm opened={formOpened} onClose={() => setFormOpened(false)} onSuccess={handleCreateSuccess} />

      <ConfirmModal
        opened={!!deleteTarget}
        onClose={() => setDeleteTarget(null)}
        onConfirm={handleDelete}
        title="Delete Provider"
        message={`Delete ${deleteTarget?.name} and every model under it? Agents and steps pointing at one of its models fall back to the default. A provider that still holds a default cannot be deleted.`}
        loading={deleteLoading}
      />
    </>
  );
}
