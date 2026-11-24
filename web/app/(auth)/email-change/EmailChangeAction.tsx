'use client';

import { useEffect, useRef, useState } from 'react';
import { Alert, Button, Loader, Stack, Text } from '@mantine/core';
import { IconAlertCircle, IconCircleCheck } from '@tabler/icons-react';
import { ApiError, apiFetch } from '@/lib/api/client';
import { useFormat, useT } from '@/i18n';

interface EmailChangePreview {
  currentEmailMasked: string;
  newEmail: string;
  expiresAt: string;
}

const BASE = '/api/v1/auth/email-change';

function post<T>(path: string, token: string): Promise<T> {
  return apiFetch<T>(`${BASE}/${path}`, { method: 'POST', body: JSON.stringify({ token }) });
}

/** Reads `token` from a `#token=...` fragment; empty when absent. */
export function tokenFromHash(hash: string): string {
  const raw = hash.startsWith('#') ? hash.slice(1) : hash;
  return new URLSearchParams(raw).get('token')?.trim() ?? '';
}

type State =
  | { kind: 'loading' }
  | { kind: 'invalid' }
  | { kind: 'ready'; preview: EmailChangePreview }
  | { kind: 'working'; preview: EmailChangePreview; action: 'confirm' | 'cancel' }
  | { kind: 'confirmed' }
  | { kind: 'cancelled' }
  | { kind: 'error'; preview: EmailChangePreview; message: string };

export function EmailChangeAction() {
  const t = useT('auth.emailChange');
  const format = useFormat();
  const tokenRef = useRef('');
  const [state, setState] = useState<State>({ kind: 'loading' });

  // The token lives only in the fragment; read it once, then drop it from the address bar and history.
  useEffect(() => {
    const found = tokenFromHash(window.location.hash);
    if (window.location.hash) {
      window.history.replaceState(null, '', window.location.pathname + window.location.search);
    }
    tokenRef.current = found;
    let alive = true;
    const previewing = found
      ? post<EmailChangePreview>('preview', found)
      : Promise.reject(new Error('missing token'));
    previewing
      .then((preview) => {
        if (alive) setState({ kind: 'ready', preview });
      })
      .catch(() => {
        if (alive) setState({ kind: 'invalid' });
      });
    return () => {
      alive = false;
    };
  }, []);

  if (state.kind === 'loading') {
    return (
      <Stack align="center">
        <Loader size="sm" />
      </Stack>
    );
  }
  if (state.kind === 'invalid') {
    return (
      <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light" data-testid="email-change-invalid">
        {t('invalid')}
      </Alert>
    );
  }
  if (state.kind === 'confirmed' || state.kind === 'cancelled') {
    return (
      <Alert
        icon={<IconCircleCheck size={16} />}
        color="green"
        variant="light"
        role="status"
        data-testid={`email-change-${state.kind}`}
      >
        {state.kind === 'confirmed' ? t('confirmed') : t('cancelled')}
      </Alert>
    );
  }

  const { preview } = state;
  const working = state.kind === 'working' ? state.action : null;

  async function run(action: 'confirm' | 'cancel') {
    setState({ kind: 'working', preview, action });
    try {
      await post<unknown>(action, tokenRef.current);
      setState({ kind: action === 'confirm' ? 'confirmed' : 'cancelled' });
    } catch (err) {
      if (err instanceof ApiError && err.code === 'EMAIL_CHANGE_TOKEN_INVALID') {
        setState({ kind: 'invalid' });
        return;
      }
      const message = err instanceof ApiError && err.code === 'EMAIL_IN_USE' ? t('inUse') : t('failed');
      setState({ kind: 'error', preview, message });
    }
  }

  return (
    <Stack>
      {state.kind === 'error' && (
        <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light" data-testid="email-change-error">
          {state.message}
        </Alert>
      )}
      <Stack gap={4}>
        <Text size="sm" c="dimmed">
          {t('current')}
        </Text>
        <Text size="sm" style={{ wordBreak: 'break-word' }} data-testid="email-change-current">
          {preview.currentEmailMasked}
        </Text>
        <Text size="sm" c="dimmed">
          {t('new')}
        </Text>
        <Text size="sm" fw={600} style={{ wordBreak: 'break-word' }} data-testid="email-change-new">
          {preview.newEmail}
        </Text>
        <Text size="xs" c="dimmed">
          {t('expires', { date: format.date(preview.expiresAt, 'dateTime') })}
        </Text>
      </Stack>
      <Text size="sm">{t('warning')}</Text>
      <Button
        fullWidth
        loading={working === 'confirm'}
        disabled={working === 'cancel'}
        onClick={() => void run('confirm')}
        data-testid="email-change-confirm"
      >
        {t('confirm')}
      </Button>
      <Button
        fullWidth
        variant="default"
        loading={working === 'cancel'}
        disabled={working === 'confirm'}
        onClick={() => void run('cancel')}
        data-testid="email-change-cancel"
      >
        {t('cancel')}
      </Button>
    </Stack>
  );
}
