'use client';

import { useMemo } from 'react';
import { Text } from '@mantine/core';
import { useAuiState } from '@assistant-ui/react';
import { useT } from '@/i18n';

/**
 * The name the chat runtime files a `data-turnTruncated` frame under in `unstable_data` — the frame
 * T-022's hub puts FIRST on an attach replay whose frame log was capped, so the client is told it is
 * seeing a partial turn rather than silently shown a shorter answer.
 */
export const ASSISTANT_TURN_TRUNCATED_DATA_NAME = 'turnTruncated';

/**
 * Whether a reply's `unstable_data` carries the truncation marker. The frame is
 * `{ type: 'data-turnTruncated', data: { truncated: true, note } }`; only the boolean is read — the
 * user-facing sentence is the client's own copy, the way the credits and quota notices word their
 * own states rather than echoing the backend. Anything malformed, or any other part, is ignored.
 */
export function turnWasTruncated(unstableData: unknown): boolean {
  if (!Array.isArray(unstableData)) return false;
  for (const item of unstableData) {
    if (typeof item !== 'object' || item === null) continue;
    const { name, data } = item as { name?: unknown; data?: unknown };
    if (name !== ASSISTANT_TURN_TRUNCATED_DATA_NAME || typeof data !== 'object' || data === null) continue;
    if ((data as { truncated?: unknown }).truncated === true) return true;
  }
  return false;
}

/**
 * A one-line note under a reply that is a PARTIAL replay of a long turn. A re-attach to a turn whose
 * frame log was capped replays only its tail, so without this the panel would show a shorter answer
 * as if it were the whole one. It is a sentence, not a control: the turn is still the server's, and
 * the whole reply is in the thread's stored history.
 */
export function AssistantTurnTruncatedNotice() {
  const t = useT();
  // The runtime's own array — a stable reference until a frame arrives — never a filtered copy.
  const unstableData = useAuiState((s) => (s.message.role === 'assistant' ? (s.message.metadata.unstable_data ?? null) : null));
  const truncated = useMemo(() => turnWasTruncated(unstableData), [unstableData]);
  if (!truncated) return null;
  return (
    <Text size="xs" c="dimmed" mt="xs" data-testid="assistant-turn-truncated">
      {t('assistant.turnTruncated')}
    </Text>
  );
}
