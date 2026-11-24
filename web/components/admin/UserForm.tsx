'use client';

import { useState } from 'react';
import { Alert, Modal, TextInput, Switch, Button, Stack, Group, Text } from '@mantine/core';
import { useForm } from '@mantine/form';
import { notifications } from '@mantine/notifications';
import { useT, type T } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { cancelEmailChange, createUser, requestEmailChange, updateUser } from '@/lib/api/admin';
import { ApiError } from '@/lib/api/client';
import type { AdminUser, CreateUserResponse, PendingEmailChange } from '@/lib/types/admin';

const EMAIL_CHANGE_ERRORS: Record<string, string> = {
  EMAIL_CHANGE_CURRENT_UNVERIFIED:
    "The user's current address is not verified, so it cannot confirm a change.",
  EMAIL_INVALID: 'That is not a valid email address.',
  EMAIL_UNCHANGED: 'That is already the current address.',
  EMAIL_IN_USE: 'Another account already uses that address.',
  EMAIL_CHANGE_RATE_LIMITED: 'Too many email change requests. Try again later.',
};

function emailChangeError(t: T, err: unknown): string {
  if (err instanceof ApiError && err.code && EMAIL_CHANGE_ERRORS[err.code]) {
    return EMAIL_CHANGE_ERRORS[err.code];
  }
  return describeError(t, err, 'Request failed');
}

function formatExpiry(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

/**
 * Admin-requested address change: the admin only PROPOSES the new address. The user confirms it from
 * the current, verified mailbox; until then the stored email is untouched.
 */
function ChangeEmailModal({
  user,
  opened,
  onClose,
  onRequested,
}: {
  user: AdminUser;
  opened: boolean;
  onClose: () => void;
  onRequested: (pending: PendingEmailChange) => void;
}) {
  const tAll = useT();
  const [newEmail, setNewEmail] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [sending, setSending] = useState(false);

  const submit = async () => {
    if (!newEmail.trim()) {
      setError('Email is required');
      return;
    }
    setSending(true);
    setError(null);
    try {
      const res = await requestEmailChange(user.id, newEmail.trim());
      onRequested({
        newEmail: res.pendingEmail,
        requestedAt: new Date().toISOString(),
        expiresAt: res.expiresAt,
      });
      notifications.show({
        title: 'Request sent',
        message: 'The user must confirm the change from their current address.',
        color: 'green',
      });
      setNewEmail('');
      onClose();
    } catch (err: unknown) {
      setError(emailChangeError(tAll, err));
    } finally {
      setSending(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title="Change email" centered>
      <Stack>
        <Text size="sm" c="dimmed">
          We email a confirmation link to the current address. The change happens only when the user
          confirms it there.
        </Text>
        <TextInput
          label="New email"
          placeholder="user@example.com"
          type="email"
          value={newEmail}
          onChange={(e) => setNewEmail(e.currentTarget.value)}
          error={error}
          data-testid="change-email-input"
        />
        <Button onClick={() => void submit()} loading={sending} data-testid="change-email-submit">
          Send confirmation request
        </Button>
      </Stack>
    </Modal>
  );
}

interface UserFormProps {
  opened: boolean;
  onClose: () => void;
  onSuccess: (result?: CreateUserResponse) => void;
  user?: AdminUser;
}

export function UserForm({ opened, onClose, onSuccess, user }: UserFormProps) {
  const tAll = useT();
  const [loading, setLoading] = useState(false);
  const isEdit = !!user;
  const [pending, setPending] = useState<PendingEmailChange | null>(user?.pendingEmailChange ?? null);
  // Follow the server when the prop changes (SWR refresh, expiry, confirm). Keyed on the prop's
  // content, so a local optimistic update after request/cancel stands until the server says otherwise.
  const propPending = user?.pendingEmailChange ?? null;
  const propKey = propPending ? `${propPending.newEmail}|${propPending.expiresAt}` : '';
  const [seenKey, setSeenKey] = useState(propKey);
  if (propKey !== seenKey) {
    setSeenKey(propKey);
    setPending(propPending);
  }
  const [changeOpen, setChangeOpen] = useState(false);
  const [cancelling, setCancelling] = useState(false);

  const handleCancelChange = async () => {
    if (!user) return;
    setCancelling(true);
    try {
      await cancelEmailChange(user.id);
      setPending(null);
      notifications.show({ title: 'Cancelled', message: 'Email change request cancelled', color: 'green' });
    } catch (err: unknown) {
      notifications.show({
        title: 'Error',
        message: describeError(tAll, err, 'Operation failed'),
        color: 'red',
      });
    } finally {
      setCancelling(false);
    }
  };

  const form = useForm({
    initialValues: {
      email: user?.email || '',
      displayName: user?.displayName || '',
      isAdmin: user?.isAdmin || false,
      resetPassword: false,
    },
    validate: {
      email: (v) => (!v.trim() ? 'Email is required' : null),
      displayName: (v) => (!v.trim() ? 'Display name is required' : null),
    },
  });

  const handleSubmit = form.onSubmit(async (values) => {
    setLoading(true);
    try {
      if (isEdit) {
        // The email is read-only here: the stored value goes back unchanged (a changed one is refused
        // with EMAIL_CHANGE_REQUIRES_USER); a change goes through the confirmation flow instead.
        await updateUser(user.id, {
          email: user.email,
          displayName: values.displayName,
          isAdmin: values.isAdmin,
          resetPassword: values.resetPassword || undefined,
        });
        notifications.show({ title: 'Updated', message: 'User updated', color: 'green' });
        onSuccess();
      } else {
        const result = await createUser({
          email: values.email,
          displayName: values.displayName,
          isAdmin: values.isAdmin,
        });
        onSuccess(result);
      }
      form.reset();
      onClose();
    } catch (err: unknown) {
      notifications.show({
        title: 'Error',
        message: describeError(tAll, err, 'Operation failed'),
        color: 'red',
      });
    } finally {
      setLoading(false);
    }
  });

  return (
    <Modal opened={opened} onClose={onClose} title={isEdit ? 'Edit User' : 'Create User'} centered>
      <form onSubmit={handleSubmit}>
        <Stack>
          {isEdit ? (
            <Stack gap="xs">
              <TextInput
                label="Email"
                value={user.email}
                readOnly
                description="Only the user can confirm an email change"
                data-testid="user-email-readonly"
              />
              {pending ? (
                <Alert color="yellow" variant="light" title="Email change pending" data-testid="pending-email-alert">
                  <Stack gap="xs">
                    <Text size="sm">
                      Waiting for the user to confirm {pending.newEmail}. The request expires{' '}
                      {formatExpiry(pending.expiresAt)}.
                    </Text>
                    <Group>
                      <Button
                        size="xs"
                        variant="default"
                        loading={cancelling}
                        onClick={() => void handleCancelChange()}
                        data-testid="cancel-email-change"
                      >
                        Cancel request
                      </Button>
                    </Group>
                  </Stack>
                </Alert>
              ) : (
                <Group>
                  <Button
                    size="xs"
                    variant="light"
                    onClick={() => setChangeOpen(true)}
                    data-testid="open-change-email"
                  >
                    Change email…
                  </Button>
                </Group>
              )}
            </Stack>
          ) : (
            <TextInput label="Email" placeholder="user@example.com" {...form.getInputProps('email')} />
          )}
          <TextInput label="Display Name" placeholder="John Doe" {...form.getInputProps('displayName')} />
          <Switch label="Administrator" {...form.getInputProps('isAdmin', { type: 'checkbox' })} />
          {isEdit && (
            <Switch
              label="Reset password (generates new temporary password)"
              {...form.getInputProps('resetPassword', { type: 'checkbox' })}
            />
          )}
          <Button type="submit" loading={loading} fullWidth>
            {isEdit ? 'Update' : 'Create'}
          </Button>
        </Stack>
      </form>
      {isEdit && (
        <ChangeEmailModal
          user={user}
          opened={changeOpen}
          onClose={() => setChangeOpen(false)}
          onRequested={setPending}
        />
      )}
    </Modal>
  );
}
