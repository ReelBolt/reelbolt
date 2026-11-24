'use client';

import { useState } from 'react';
import { Alert, Button, Stack, Text } from '@mantine/core';
import { IconAlertCircle, IconCircleCheck } from '@tabler/icons-react';
import { ApiError } from '@/lib/api/client';
import { confirmMarketingSubscription, unsubscribeMarketing } from '@/lib/api/marketing';
import { useT, type T } from '@/i18n';

type State = 'idle' | 'working' | 'done' | 'error';

function failureMessage(err: unknown, expiredText: string, t: T): string {
  if (err instanceof ApiError) {
    if (err.code === 'token_expired' || err.status === 410) return expiredText;
    if (err.code === 'invalid_token' || err.status === 400) {
      return t('invalidLink');
    }
  }
  return t('failed');
}

/**
 * The single button on a page opened from an email link. Opening the page changes nothing: only the
 * click POSTs the token, so a mail scanner that follows links can neither unsubscribe nor confirm
 * anyone (compliance: "GET never unsubscribes", "GET shows a button and POST confirms").
 */
function TokenAction({
  token,
  action,
  buttonLabel,
  buttonTestId,
  doneText,
  doneTestId,
  expiredText,
}: {
  token: string;
  action: (token: string) => Promise<void>;
  buttonLabel: string;
  buttonTestId: string;
  doneText: string;
  doneTestId: string;
  expiredText: string;
}) {
  const t = useT('auth.marketing');
  const [state, setState] = useState<State>('idle');
  const [error, setError] = useState('');

  if (!token) {
    return (
      <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light">
        {t('incomplete')}
      </Alert>
    );
  }

  if (state === 'done') {
    return (
      <Alert icon={<IconCircleCheck size={16} />} color="green" variant="light" role="status" data-testid={doneTestId}>
        {doneText}
      </Alert>
    );
  }

  async function run() {
    setState('working');
    setError('');
    try {
      await action(token);
      setState('done');
    } catch (err) {
      setError(failureMessage(err, expiredText, t));
      setState('error');
    }
  }

  return (
    <Stack>
      {state === 'error' && (
        <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light">
          {error}
        </Alert>
      )}
      <Button fullWidth loading={state === 'working'} onClick={() => void run()} data-testid={buttonTestId}>
        {buttonLabel}
      </Button>
    </Stack>
  );
}

export function UnsubscribeAction({ token }: { token: string }) {
  const t = useT('auth.marketing');
  return (
    <TokenAction
      token={token}
      action={unsubscribeMarketing}
      buttonLabel={t('unsubscribe')}
      buttonTestId="unsubscribe-button"
      doneText={t('unsubscribed')}
      doneTestId="unsubscribe-done"
      expiredText={t('invalidLink')}
    />
  );
}

export function ConfirmSubscriptionAction({ token }: { token: string }) {
  const t = useT('auth.marketing');
  return (
    <>
      <TokenAction
        token={token}
        action={confirmMarketingSubscription}
        buttonLabel={t('confirm')}
        buttonTestId="confirm-subscription-button"
        doneText={t('confirmed')}
        doneTestId="confirm-subscription-done"
        expiredText={t('confirmExpired')}
      />
      <Text size="xs" c="dimmed" ta="center" mt="md">
        {t('ignore')}
      </Text>
    </>
  );
}
