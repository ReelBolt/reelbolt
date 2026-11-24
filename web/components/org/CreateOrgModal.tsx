'use client';

import { useState } from 'react';
import { Button, Modal, Stack, TextInput } from '@mantine/core';
import { useForm } from '@mantine/form';
import { notifications } from '@mantine/notifications';
import { createOrg, type Org } from '@/lib/api/orgs';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

interface CreateOrgModalProps {
  opened: boolean;
  onClose: () => void;
  /** Called with the new workspace. The caller switches into it and routes. */
  onCreated: (org: Org) => void;
}

/**
 * Create a Team workspace (A16-UI). Personal workspaces are created with the account and can
 * never be created, renamed or deleted from here — `POST /api/v1/orgs` always makes a Team org
 * with the caller as its Owner.
 */
export function CreateOrgModal({ opened, onClose, onCreated }: CreateOrgModalProps) {
  const t = useT('org.createModal');
  const tAll = useT();
  const [loading, setLoading] = useState(false);

  const form = useForm({
    initialValues: { name: '' },
    validate: {
      name: (v) => (!v.trim() ? t('nameRequired') : v.trim().length > 100 ? t('nameTooLong') : null),
    },
  });

  const handleSubmit = form.onSubmit(async (values) => {
    setLoading(true);
    try {
      const org = await createOrg({ name: values.name.trim() });
      form.reset();
      onClose();
      onCreated(org);
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: describeError(tAll, err, t('createFailed')),
        color: 'red',
      });
    } finally {
      setLoading(false);
    }
  });

  return (
    <Modal opened={opened} onClose={onClose} title={t('title')} centered>
      <form onSubmit={handleSubmit}>
        <Stack>
          <TextInput
            label={t('nameLabel')}
            placeholder={t('namePlaceholder')}
            data-autofocus
            {...form.getInputProps('name')}
          />
          <Button type="submit" loading={loading} fullWidth>
            {t('submit')}
          </Button>
        </Stack>
      </form>
    </Modal>
  );
}
