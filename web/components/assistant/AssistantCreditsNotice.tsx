'use client';

import Link from 'next/link';
import { useMemo } from 'react';
import { Alert, Button, Text } from '@mantine/core';
import { IconCoins } from '@tabler/icons-react';
import { useAuiState } from '@assistant-ui/react';
import { INSUFFICIENT_CREDITS_CODE } from '@/lib/api/billing';
import { useT } from '@/i18n';
import { useCreditAmount } from '@/components/billing/useCreditAmount';

/** The name the chat runtime files a `data-credits` frame under in `unstable_data`. */
export const ASSISTANT_CREDITS_DATA_NAME = 'credits';

/**
 * The machine codes the assistant's credit checks answer with. `INSUFFICIENT_CREDITS` and `TURN_LIMIT`
 * arrive as a `data-credits` part on an ordinary reply; `TURN_CONCURRENCY_LIMIT` arrives as the 429
 * body of a turn that was never started (see `assistantTurnRefusal`).
 */
export type AssistantCreditsCode = 'INSUFFICIENT_CREDITS' | 'TURN_LIMIT' | 'TURN_CONCURRENCY_LIMIT';

/** A reply stopped at the most one turn may spend. */
export const TURN_LIMIT_CODE = 'TURN_LIMIT' satisfies AssistantCreditsCode;
/** A turn refused because the workspace already has too many assistant turns in flight. */
export const TURN_CONCURRENCY_LIMIT_CODE = 'TURN_CONCURRENCY_LIMIT' satisfies AssistantCreditsCode;

/** What a reply's `data-credits` part asks the notice to say. */
export type AssistantCreditsRefusal =
  | { code: 'INSUFFICIENT_CREDITS'; balance: number | null }
  | { code: 'TURN_LIMIT' };

/**
 * The credits notice a reply's `unstable_data` asks for, or null. The server answers a turn it will
 * not run (or stops) for lack of credits with a normal message plus one `data-credits` part
 * `{ code: 'INSUFFICIENT_CREDITS', balance? }`, and a turn stopped at the per-turn spend ceiling with
 * `{ code: 'TURN_LIMIT' }`; any other code, or a malformed frame, is ignored. The last such part wins.
 */
export function creditsRefusalOf(unstableData: unknown): AssistantCreditsRefusal | null {
  if (!Array.isArray(unstableData)) return null;
  for (let i = unstableData.length - 1; i >= 0; i--) {
    const item: unknown = unstableData[i];
    if (typeof item !== 'object' || item === null) continue;
    const { name, data } = item as { name?: unknown; data?: unknown };
    if (name !== ASSISTANT_CREDITS_DATA_NAME || typeof data !== 'object' || data === null) continue;
    const { code, balance } = data as { code?: unknown; balance?: unknown };
    if (code === TURN_LIMIT_CODE) return { code: TURN_LIMIT_CODE };
    if (code !== INSUFFICIENT_CREDITS_CODE) continue;
    return {
      code: INSUFFICIENT_CREDITS_CODE,
      balance: typeof balance === 'number' && Number.isFinite(balance) ? balance : null,
    };
  }
  return null;
}

/** The notice itself, separate from the runtime so it renders the same wherever it is placed. */
export function CreditsNotice({ refusal }: { refusal: AssistantCreditsRefusal }) {
  const t = useT('billing.assistantNotice');
  const formatCreditAmount = useCreditAmount();
  if (refusal.code === TURN_LIMIT_CODE) {
    return (
      <Alert
        color="gray"
        variant="light"
        icon={<IconCoins size={16} />}
        title={t('limitTitle')}
        mt="xs"
        data-testid="assistant-turn-limit-notice"
      >
        <Text size="sm">{t('limitBody')}</Text>
      </Alert>
    );
  }
  return (
    <Alert
      color="orange"
      variant="light"
      icon={<IconCoins size={16} />}
      title={t('creditsTitle')}
      mt="xs"
      data-testid="assistant-credits-notice"
    >
      <Text size="sm">
        {refusal.balance !== null
          ? t('creditsBodyBalance', { balance: formatCreditAmount(refusal.balance) })
          : t('creditsBody')}
      </Text>
      <Button component={Link} href="/settings/billing" size="compact-sm" variant="light" mt="xs">
        {t('add')}
      </Button>
    </Alert>
  );
}

/** Reads the current assistant message's `data-credits` part and shows the notice under the reply. */
export function AssistantCreditsNotice() {
  // The runtime's own array — a stable reference until a frame arrives — never a filtered copy.
  const unstableData = useAuiState((s) => (s.message.role === 'assistant' ? (s.message.metadata.unstable_data ?? null) : null));
  const refusal = useMemo(() => creditsRefusalOf(unstableData), [unstableData]);
  return refusal ? <CreditsNotice refusal={refusal} /> : null;
}
