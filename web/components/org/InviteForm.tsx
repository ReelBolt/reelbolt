'use client';

import { useState } from 'react';
import { Alert, Button, Group, Select, Stack, TextInput } from '@mantine/core';
import { useForm } from '@mantine/form';
import { notifications } from '@mantine/notifications';
import { IconInfoCircle } from '@tabler/icons-react';
import { createInvite } from '@/lib/api/orgs';
import { friendlyMessage } from '@/lib/api/error-messages';
import { assignableRoles } from '@/lib/org-permissions';
import type { OrgRole } from '@/lib/types/auth';
import { useT } from '@/i18n';
import { useRoleLabel } from './useRoleLabel';

interface InviteFormProps {
  orgId: string;
  /** The caller's role — it decides which roles may be handed out. */
  callerRole: string | null;
  onCreated: () => void;
}

/**
 * Invite someone to a Team workspace by email (A16-UI).
 *
 * The API never returns the invite token — it is only ever emailed — so the only feedback the
 * form can give is `emailSent`. When that is false the invitation still exists and still works
 * once mail is configured, so the form says exactly that rather than claiming it was sent.
 */
export function InviteForm({ orgId, callerRole, onCreated }: InviteFormProps) {
  const t = useT('org.inviteForm');
  const tr = useT();
  const roleLabel = useRoleLabel();
  const [loading, setLoading] = useState(false);
  const [undelivered, setUndelivered] = useState<string | null>(null);
  const options = assignableRoles(callerRole);

  const form = useForm({
    initialValues: { email: '', role: 'Member' as OrgRole },
    validate: {
      email: (v) => (!v.trim() ? t('emailRequired') : null),
    },
  });

  const handleSubmit = form.onSubmit(async (values) => {
    setLoading(true);
    setUndelivered(null);
    try {
      const invite = await createInvite(orgId, { email: values.email.trim(), role: values.role });
      // One message per outcome: an undelivered invite is explained by the banner below, so it gets
      // no toast saying the same thing.
      if (invite.emailSent === false) setUndelivered(invite.email);
      else notifications.show({ title: t('createdTitle'), message: t('createdMessage', { email: invite.email }), color: 'green' });
      form.reset();
      onCreated();
    } catch (err: unknown) {
      // Shown once, next to the field it is about, in a plain sentence; cleared as soon as it is edited.
      form.setFieldError('email', friendlyMessage(err, t('createFailed'), tr));
    } finally {
      setLoading(false);
    }
  });

  if (options.length === 0) return null;

  return (
    <form onSubmit={handleSubmit}>
      <Stack gap="sm">
        {undelivered && (
          <Alert icon={<IconInfoCircle size={16} />} color="yellow" variant="light">
            {t('undelivered', { email: undelivered })}
          </Alert>
        )}
        <Group align="flex-end" gap="sm" wrap="wrap">
          <TextInput
            label={t('emailLabel')}
            placeholder={t('emailPlaceholder')}
            style={{ flex: '1 1 240px', minWidth: 220 }}
            {...form.getInputProps('email')}
          />
          <Select
            label={t('roleLabel')}
            data={options.map((role) => ({ value: role, label: roleLabel(role) }))}
            allowDeselect={false}
            style={{ flex: '0 1 160px', minWidth: 140 }}
            {...form.getInputProps('role')}
          />
          <Button type="submit" loading={loading}>
            {t('submit')}
          </Button>
        </Group>
      </Stack>
    </form>
  );
}
