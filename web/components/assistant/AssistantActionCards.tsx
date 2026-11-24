'use client';

import { useMemo } from 'react';
import { useAuiState } from '@assistant-ui/react';
import { isAbandoned, reduceAssistantActions } from '@/lib/assistant/actions';
import { useAdoptAssistantMode } from '@/lib/hooks/use-assistant-preferences';
import { ActionCard } from './ActionCard';
import classes from './ActionCard.module.css';

/**
 * The permission cards of the assistant reply this renders under: one per request, however many
 * frames the stream sent for it. Reads them from the reply's own data, so a card is part of the
 * message — it scrolls with it, and it is still there when the drawer is closed and opened again.
 */
export function AssistantActionCards() {
  // The runtime's own array, a stable reference until a frame arrives — see `AssistantActionSync`.
  const unstableData = useAuiState((s) => (s.message.role === 'assistant' ? (s.message.metadata.unstable_data ?? null) : null));
  const running = useAuiState((s) => s.message.role === 'assistant' && s.message.status?.type === 'running');
  // The person pressed Stop: a card left open says so, rather than that the connection dropped.
  const stopped = useAuiState(
    (s) =>
      s.message.role === 'assistant' &&
      s.message.status?.type === 'incomplete' &&
      s.message.status.reason === 'cancelled',
  );
  const adoptMode = useAdoptAssistantMode();

  const actions = useMemo(() => reduceAssistantActions(unstableData), [unstableData]);
  if (actions.length === 0) return null;

  return (
    <div className={classes.list} data-testid="assistant-actions">
      {actions.map((action) => (
        <ActionCard
          key={action.id}
          action={action}
          abandoned={isAbandoned(action, running)}
          stopped={stopped}
          onAlwaysAllowed={() => adoptMode('auto')}
        />
      ))}
    </div>
  );
}
