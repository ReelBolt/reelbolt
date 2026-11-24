'use client';

import { Anchor, Button, Group, Stack, Text } from '@mantine/core';
import { IconMessageChatbot, IconRefresh } from '@tabler/icons-react';
import { useLocale, useT } from '@/i18n';
import { insertFailureAdvice, insertGuideHref } from './run-model';

/**
 * "What you can do" under a failed screen insert on the classic run page: the guide, the assistant (with a
 * prefilled ask to put the content in a corner) and Run again, which leads when only a re-run can help.
 */
export function InsertFailureNext({
  code,
  onRunAgain,
  onAskAssistant,
}: {
  code: string | null | undefined;
  onRunAgain: () => void;
  onAskAssistant?: (prompt: string) => void;
}) {
  const t = useT('apps.run.result.insertNext');
  const locale = useLocale();
  const advice = insertFailureAdvice(code);
  if (!advice) return null;
  const lead = !advice.chooseClip;
  const runAgain = (
    <Button key="run" size="xs" variant={lead ? 'filled' : 'default'} leftSection={<IconRefresh size={14} />} onClick={onRunAgain}>
      {t('runAgain')}
    </Button>
  );
  const ask = onAskAssistant && (code?.toUpperCase() === 'INSERT_NOT_APPLIED' || code?.toUpperCase() === 'INSERT_TRACKING_FAILED') ? (
    <Button key="ask" size="xs" variant="default" leftSection={<IconMessageChatbot size={14} />} onClick={() => onAskAssistant(t('cornerPrompt'))}>
      {t('askCorner')}
    </Button>
  ) : null;
  return (
    <Stack gap={4} mt={8} data-testid="insert-next-step" role="group" aria-label={t('heading')}>
      <Text size="sm" fw={600}>{t('heading')}</Text>
      <Anchor href={insertGuideHref(locale)} target="_blank" rel="noopener noreferrer" size="sm">{t('whatWorks')}</Anchor>
      <Group gap="xs">{lead ? [runAgain, ask] : [ask, runAgain]}</Group>
    </Stack>
  );
}
