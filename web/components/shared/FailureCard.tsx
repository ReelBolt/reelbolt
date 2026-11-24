'use client';

import type { ReactNode } from 'react';
import Link from 'next/link';
import { Button, Group, Paper, Stack, Text } from '@mantine/core';
import { FeedbackErrorAction } from '@/components/feedback/FeedbackErrorAction';
import { classifyFailure, type FailureKind } from '@/lib/api/failure-kind';
import { describeError } from '@/lib/api/describe-error';
import { useT } from '@/i18n';

export interface FailureCardProps {
  error: unknown;
  /** Retry / Try again. Hidden for 403 and 404 (retrying cannot change the answer). */
  onRetry?: () => void;
  retryLabel?: string;
  backHref?: string;
  backLabel?: string;
  resource?: string;
  /** Use the generic page sentence for a render crash (no HTTP status to describe). */
  fallbackSentence?: string;
  extra?: ReactNode;
}

/** The one failure card: plain title, plain sentence, and a way out. Never shows a raw `err.message`. */
export function FailureCard({ error, onRetry, retryLabel, backHref, backLabel, resource, fallbackSentence, extra }: FailureCardProps) {
  const t = useT();
  const kind: FailureKind = classifyFailure(error);
  const titles: Record<FailureKind, string> = {
    notFound: resource ? t('errors.state.notFoundWith', { resource }) : t('errors.state.title.notFound'),
    forbidden: t('errors.state.title.forbidden'),
    network: t('errors.state.title.network'),
    server: t('errors.state.title.server'),
    unauthorized: t('errors.state.title.unauthorized'),
    paymentRequired: t('errors.state.title.paymentRequired'),
    rateLimited: t('errors.state.title.rateLimited'),
    other: t('errors.state.title.other'),
  };
  const sentence = kind === 'other' && fallbackSentence ? fallbackSentence : describeError(t, error);
  const canRetry = onRetry && kind !== 'notFound' && kind !== 'forbidden';
  // Never a dead end: with no Retry and no own way back, the card leads to the dashboard.
  const backTo = backHref ?? (canRetry ? undefined : '/dashboard');
  return (
    <Paper withBorder radius="lg" p="lg" role="alert" data-testid="failure-card" data-kind={kind}>
      <Stack gap="sm" align="flex-start">
        <Text fw={600}>{titles[kind]}</Text>
        <Text size="sm">{sentence}</Text>
        <Group gap="sm">
          {canRetry ? (
            <Button variant="light" onClick={onRetry}>
              {retryLabel ?? t('errors.state.retry')}
            </Button>
          ) : null}
          {kind === 'paymentRequired' ? (
            <Button component={Link} href="/settings/billing" variant="light">
              {t('errors.state.billing')}
            </Button>
          ) : null}
          {kind === 'unauthorized' ? (
            <Button component={Link} href="/login" variant="light">
              {t('errors.state.signIn')}
            </Button>
          ) : null}
          {backTo ? (
            <Button component={Link} href={backTo} variant="subtle">
              {backLabel ?? t('errors.state.backDashboard')}
            </Button>
          ) : null}
          {kind === 'server' || kind === 'other' ? <FeedbackErrorAction /> : null}
          {extra}
        </Group>
      </Stack>
    </Paper>
  );
}
