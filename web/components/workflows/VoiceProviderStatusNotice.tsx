'use client';

import useSWR from 'swr';
import { Alert, Text } from '@mantine/core';
import { IconAlertTriangle, IconInfoCircle } from '@tabler/icons-react';
import { getProviderStatus } from '@/lib/api/inference-providers';
import { useT } from '@/i18n';
import type { InferenceProviderStatus } from '@/lib/types/inference-provider';
import { englishWorkflowsT } from './english-t';

type Translate = (key: string, values?: Record<string, string | number>) => string;

/** Why narration has something to say: no service, a failing one, or a backup. */
export type VoiceProviderNoticeReason = 'noService' | 'failing' | 'fallback';

export interface VoiceProviderNotice {
  level: 'error' | 'warning';
  reason: VoiceProviderNoticeReason;
  title: string;
  body: string;
}

export interface DescribeVoiceProviderStatusOptions {
  /**
   * How many of the project's voices can be used right now. A project voice is a service the run can
   * actually use, so the default capability not being configured is not a reason to warn.
   */
  projectVoiceCount?: number;
}

/**
 * The customer-facing sentence for a speech-provider status, or null when all is well. Exported so
 * the wording is pinned by a test without a network round trip. `reason` lets a caller tell "no
 * service at all" (which a project voice makes moot) from a failing or fallback service (which it
 * does not).
 */
export function describeVoiceProviderStatus(
  status: InferenceProviderStatus | undefined,
  translate?: Translate,
  { projectVoiceCount = 0 }: DescribeVoiceProviderStatusOptions = {},
): VoiceProviderNotice | null {
  if (!status) return null;
  const t = translate ?? englishWorkflowsT('voiceStatus');
  if (!status.providerName && !status.healthy) {
    // The project already has a voice it can use: the default capability being unconfigured is a
    // server-side detail, not something that stops this run.
    if (projectVoiceCount >= 1) return null;
    return {
      level: 'error',
      reason: 'noService',
      title: t('noService.title'),
      body: status.message ?? t('noService.body'),
    };
  }
  if (!status.healthy) {
    return {
      level: 'error',
      reason: 'failing',
      title: t('failing.title'),
      body: status.message
        ?? t('failing.body', { providerName: status.providerName ? status.providerName : 'none' }),
    };
  }
  if (status.usingFallback) {
    return {
      level: 'warning',
      reason: 'fallback',
      title: t('fallback.title'),
      body: status.message
        ?? t('fallback.body', { providerName: status.providerName ?? t('anotherOne') }),
    };
  }
  return null;
}

/**
 * Warns in the Voiceover step settings when the speech service a run would use right now is
 * unhealthy, or is a fallback for a broken default — otherwise a customer only finds out after an
 * hour-long run comes back without narration. Renders nothing while loading, on error (an older
 * server without the status endpoint), or when the provider is healthy.
 */
export function VoiceProviderStatusNotice() {
  const t = useT('workflows.voiceStatus');
  const { data } = useSWR<InferenceProviderStatus>(
    'inference-provider-status:SpeechSynthesis',
    () => getProviderStatus('SpeechSynthesis'),
    { revalidateOnFocus: false },
  );
  const notice = describeVoiceProviderStatus(data, t);
  if (!notice) return null;
  return (
    <Alert
      color={notice.level === 'error' ? 'red' : 'yellow'}
      variant="light"
      p="xs"
      icon={notice.level === 'error' ? <IconAlertTriangle size={16} /> : <IconInfoCircle size={16} />}
      title={notice.title}
      role="alert"
    >
      <Text size="xs">{notice.body}</Text>
    </Alert>
  );
}
