'use client';

import { useEffect } from 'react';
import { Center, Container } from '@mantine/core';
import { useT } from '@/i18n';
import { FailureCard } from './FailureCard';

/** The page-level failure used by the Next error boundaries (`error.tsx`). */
export function ErrorScreen({ error, reset, backHref = '/dashboard' }: { error: Error & { digest?: string }; reset: () => void; backHref?: string }) {
  const t = useT('errors.state');
  useEffect(() => {
    console.error('Page render failed', error);
  }, [error]);
  return (
    <Center mih="50vh" p="md">
      <Container size={560} w="100%">
        <FailureCard
          error={error}
          onRetry={reset}
          retryLabel={t('tryAgain')}
          backHref={backHref}
          fallbackSentence={t('pageFailed')}
        />
      </Container>
    </Center>
  );
}
