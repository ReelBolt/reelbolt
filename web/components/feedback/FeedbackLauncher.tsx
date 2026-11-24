'use client';

import { useState } from 'react';
import { Button, Text } from '@mantine/core';
import { IconFlag } from '@tabler/icons-react';
import { usePathname } from 'next/navigation';
import { useAssistant } from '@/components/assistant/AssistantShellProvider';
import { getThread } from '@/lib/api/assistant';
import {
  candidatesFromPath,
  lastAssistantTurns,
  type FeedbackCandidates,
  type FeedbackTurn
} from '@/lib/feedback/context';
import { FeedbackDialog } from './FeedbackDialog';
import { useT } from '@/i18n';

/**
 * The shell's "Flag a bug / Send feedback" action (T-019), mounted in `AppShell`'s header beside the
 * assistant trigger and the account menu.
 *
 * The page and its ids come from the route. The last assistant turns come from the thread the shell
 * has bound, fetched on open: they are only offered when there is a thread, and a fetch failure just
 * means they are not offered. Nothing is gathered until the user opens the dialog, and nothing is
 * sent until they press Send — and then only what they left switched on.
 */
export function FeedbackLauncher() {
  const t = useT('feedback');
  const pathname = usePathname();
  const assistant = useAssistant();
  const [opened, setOpened] = useState(false);
  const [turns, setTurns] = useState<FeedbackTurn[]>([]);

  const open = async () => {
    const threadId = assistant.threadId;
    if (threadId) {
      try {
        setTurns(lastAssistantTurns(await getThread(threadId)));
      } catch {
        setTurns([]);
      }
    } else {
      setTurns([]);
    }
    setOpened(true);
  };

  const candidates: FeedbackCandidates = { ...candidatesFromPath(pathname) };
  if (turns.length > 0) candidates.assistantTurns = turns;

  return (
    <>
      <Button
        variant="subtle"
        size="compact-sm"
        leftSection={<IconFlag size={16} />}
        onClick={open}
        aria-label={t('trigger')}
        px={{ base: 6, sm: 'md' }}
      >
        {/* Phone width: this label is the only wide item left in the header's right group - the
            install prompt hides itself below `sm`, the assistant trigger and the account menu are
            icons - so it is what pushed the row past a ~400px viewport. The flag icon and the
            aria-label keep the action reachable and named. */}
        <Text component="span" inherit visibleFrom="sm">
          {t('triggerShort')}
        </Text>
      </Button>
      {opened ? (
        <FeedbackDialog candidates={candidates} onClose={() => setOpened(false)} />
      ) : null}
    </>
  );
}
