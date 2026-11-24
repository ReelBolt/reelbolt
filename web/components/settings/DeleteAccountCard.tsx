'use client';

import { useState } from 'react';
import Link from 'next/link';
import {
  Alert,
  Anchor,
  Button,
  Group,
  Modal,
  PasswordInput,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { IconAlertTriangle, IconTrash } from '@tabler/icons-react';
import { SettingsCard } from './SettingsCard';
import { requestAccountDeletion } from '@/lib/api/account';
import { DELETION_GRACE_DAYS, providerDisplayName } from '@/lib/account/format';
import { useT } from '@/i18n';

/** What has to be typed to arm the delete button. */
export const DELETE_CONFIRMATION_WORD = 'DELETE';

type Mode = 'password' | 'provider';

type Outcome =
  | { kind: 'wrong_password' }
  | { kind: 'password_required' }
  | { kind: 'sole_owner'; organization: string }
  | { kind: 'provider_confirmation'; provider: string; confirmUrl: string }
  | { kind: 'confirmation_unavailable' }
  | { kind: 'signed_out' }
  | { kind: 'failed' }
  | null;

/**
 * "Delete account": schedules the account for deletion after re-proving control of it.
 *
 * Proof is the password, re-entered here on purpose (a session left open on someone else's laptop
 * must not be enough). An account created with Google or GitHub has no password, so it confirms by
 * signing in with that service instead: the server answers with a link, and the deletion is
 * scheduled when that sign-in comes back. The card cannot know in advance which kind of account it
 * is looking at (the session does not say), so the person picks — and a wrong pick is answered in
 * words, not with a dead end.
 *
 * On success the server has already signed this browser out, so the only place left to go is the
 * sign-in page, by a full page load. That page names the date and how to change your mind.
 */
export function DeleteAccountCard() {
  const t = useT('settings.deleteAccount');
  const [open, setOpen] = useState(false);
  const [mode, setMode] = useState<Mode>('password');
  const [password, setPassword] = useState('');
  const [typed, setTyped] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [outcome, setOutcome] = useState<Outcome>(null);

  const reset = () => {
    setMode('password');
    setPassword('');
    setTyped('');
    setOutcome(null);
    setSubmitting(false);
  };

  const close = () => {
    if (submitting) return;
    setOpen(false);
    reset();
  };

  const confirmed = typed.trim() === DELETE_CONFIRMATION_WORD;
  const armed = confirmed && (mode === 'provider' || password.length > 0) && !submitting;

  const switchMode = (next: Mode) => {
    setMode(next);
    setOutcome(null);
    if (next === 'provider') setPassword('');
  };

  const onSubmit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!armed) return;
    setSubmitting(true);
    setOutcome(null);
    try {
      const res = await requestAccountDeletion(mode === 'password' ? password : '');
      if (res.kind === 'scheduled') {
        // The session is gone already; keep the button busy until the page changes. A full page
        // load, not the client router: it drops everything this signed-out tab still holds in
        // memory, and `replace` keeps the signed-in page out of the back button. Raw navigation
        // does not get the '/app' basePath, so the path carries it.
        const query = new URLSearchParams({ deletion: 'scheduled', date: res.deletionDate });
        window.location.replace(`/app/login?${query.toString()}`);
        return;
      }
      if (res.kind === 'password_required') setMode('password');
      setOutcome(res);
    } catch {
      setOutcome({ kind: 'failed' });
    }
    setSubmitting(false);
  };

  const continueWithProvider = (confirmUrl: string) => {
    // The link comes from our own server; still, only ever leave for a web address.
    if (/^https?:\/\//i.test(confirmUrl)) window.location.assign(confirmUrl);
    else setOutcome({ kind: 'failed' });
  };

  return (
    <SettingsCard
      title={t('title')}
      description={t('description')}
      testId="delete-account-card"
    >
      <Stack gap="xs">
        <Text size="sm">
          {t('summary', { days: DELETION_GRACE_DAYS })}
        </Text>
        <Text size="sm" c="dimmed">
          {t.rich('notes', {
            link: (chunks) => (
              <Anchor component={Link} href="/settings/billing" size="sm">
                {chunks}
              </Anchor>
            ),
          })}
        </Text>
      </Stack>
      <Group>
        <Button
          color="red"
          variant="outline"
          leftSection={<IconTrash size={16} />}
          onClick={() => setOpen(true)}
        >
          {t('openButton')}
        </Button>
      </Group>

      <Modal opened={open} onClose={close} title={t('modalTitle')} centered size="md">
        <form onSubmit={onSubmit} noValidate>
          <Stack gap="md">
            <Text size="sm">
              {t('modalBody', { days: DELETION_GRACE_DAYS })}
            </Text>

            {mode === 'password' ? (
              <Stack gap={4}>
                <PasswordInput
                  label={t('passwordLabel')}
                  description={t('passwordHint')}
                  autoComplete="current-password"
                  value={password}
                  onChange={(e) => setPassword(e.currentTarget.value)}
                  data-autofocus
                  required
                />
                <Anchor component="button" type="button" size="xs" onClick={() => switchMode('provider')}>
                  {t('useProvider')}
                </Anchor>
              </Stack>
            ) : (
              <Stack gap={4}>
                <Text size="sm" c="dimmed">
                  {t('providerNote')}
                </Text>
                <Anchor component="button" type="button" size="xs" onClick={() => switchMode('password')}>
                  {t('usePassword')}
                </Anchor>
              </Stack>
            )}

            <TextInput
              label={t('typeToConfirm', { word: DELETE_CONFIRMATION_WORD })}
              placeholder={DELETE_CONFIRMATION_WORD}
              autoComplete="off"
              spellCheck={false}
              value={typed}
              onChange={(e) => setTyped(e.currentTarget.value)}
            />

            <OutcomeMessage outcome={outcome} onContinue={continueWithProvider} />

            <Group justify="flex-end" gap="sm">
              <Button variant="default" onClick={close} disabled={submitting}>
                {t('cancel')}
              </Button>
              <Button type="submit" color="red" disabled={!armed} loading={submitting}>
                {mode === 'password' ? t('confirmPassword') : t('confirmProvider')}
              </Button>
            </Group>
          </Stack>
        </form>
      </Modal>
    </SettingsCard>
  );
}

function OutcomeMessage({
  outcome,
  onContinue,
}: {
  outcome: Outcome;
  onContinue: (confirmUrl: string) => void;
}) {
  const t2 = useT('settings.deleteAccount.outcome');
  const tr = useT();
  if (!outcome) return null;
  const danger = { color: 'red', variant: 'light', icon: <IconAlertTriangle size={16} />, role: 'alert' } as const;

  switch (outcome.kind) {
    case 'wrong_password':
      return (
        <Alert {...danger} data-testid="delete-account-error">
          {t2('wrongPassword')}
        </Alert>
      );
    case 'password_required':
      return (
        <Alert {...danger} data-testid="delete-account-error">
          {t2('passwordRequired')}
        </Alert>
      );
    case 'sole_owner':
      return (
        <Alert {...danger} data-testid="delete-account-error">
          {t2.rich('soleOwner', {
            team: outcome.organization || t2('aTeam'),
            link: (chunks) => (
              <Anchor component={Link} href="/org/members" size="sm">
                {chunks}
              </Anchor>
            ),
          })}
        </Alert>
      );
    case 'provider_confirmation': {
      const name = providerDisplayName(outcome.provider, tr);
      return (
        <Alert color="violet" variant="light" title={t2('providerTitle', { name })} data-testid="delete-account-provider">
          <Stack gap="xs">
            <Text size="sm">
              {t2('providerBody', { name })}
            </Text>
            <Group>
              <Button color="red" size="xs" onClick={() => onContinue(outcome.confirmUrl)}>
                {t2('providerContinue', { name })}
              </Button>
            </Group>
          </Stack>
        </Alert>
      );
    }
    case 'confirmation_unavailable':
      return (
        <Alert {...danger} data-testid="delete-account-error">
          {t2('unavailable')}
        </Alert>
      );
    case 'signed_out':
      return (
        <Alert {...danger} data-testid="delete-account-error">
          {t2('signedOut')}
        </Alert>
      );
    case 'failed':
    default:
      return (
        <Alert {...danger} data-testid="delete-account-error">
          {t2('failed')}
        </Alert>
      );
  }
}
