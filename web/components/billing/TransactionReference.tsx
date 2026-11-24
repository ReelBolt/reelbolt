'use client';

import { Button, Code, CopyButton, Stack } from '@mantine/core';
import { IconCheck, IconCopy } from '@tabler/icons-react';
import { useT } from '@/i18n';

/**
 * The transaction id the payment provider put on its redirect, shown verbatim so the buyer can
 * quote it to support.
 *
 * A client component of its own because Mantine's `CopyButton` takes a render-prop child, and a
 * function cannot cross the server/client boundary from the server-rendered checkout page.
 */
export function TransactionReference({ value }: { value: string }) {
  const t = useT('checkout.reference');
  return (
    <Stack gap="xs" align="flex-start">
      <Code block p="sm" w="100%" style={{ wordBreak: 'break-all' }} data-testid="checkout-transaction-id">
        {value}
      </Code>
      <CopyButton value={value} timeout={2000}>
        {({ copied, copy }) => (
          <Button
            variant="light"
            size="xs"
            color={copied ? 'green' : 'violet'}
            leftSection={copied ? <IconCheck size={16} /> : <IconCopy size={16} />}
            onClick={copy}
          >
            {copied ? t('copied') : t('copy')}
          </Button>
        )}
      </CopyButton>
    </Stack>
  );
}
