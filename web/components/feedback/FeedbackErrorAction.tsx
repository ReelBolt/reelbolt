'use client';

import { useState } from 'react';
import { Button } from '@mantine/core';
import { candidatesFromPath, type FeedbackCandidates } from '@/lib/feedback/context';
import { FeedbackDialog } from './FeedbackDialog';
import { useT } from '@/i18n';

/**
 * The feedback affordance for an <b>error state</b> (T-019): dropped into a failure surface such as
 * `TabErrorBoundary`, so a user who hit an error can report it without leaving the page.
 *
 * It deliberately does <b>not</b> call `usePathname`: an error boundary can render outside a Next
 * router (and is rendered in tests with no router at all), where that hook throws. The page is read
 * from `window.location` at click time instead — the same path `resolvePageInfo` expects, with the
 * `/app` base path stripped — so the trigger is safe anywhere and the page is still offered.
 */
export function FeedbackErrorAction({ candidates }: { candidates?: FeedbackCandidates }) {
  const t = useT('feedback');
  const [opened, setOpened] = useState(false);
  const [resolved, setResolved] = useState<FeedbackCandidates>({});

  const open = () => {
    setResolved(
      candidates ?? (typeof window === 'undefined' ? {} : candidatesFromPath(window.location.pathname))
    );
    setOpened(true);
  };

  return (
    <>
      <Button variant="light" onClick={open}>
        {t('errorAction')}
      </Button>
      {opened ? (
        <FeedbackDialog candidates={resolved} onClose={() => setOpened(false)} />
      ) : null}
    </>
  );
}
