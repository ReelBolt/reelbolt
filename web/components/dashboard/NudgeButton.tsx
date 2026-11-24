'use client';

import { IconWand } from '@tabler/icons-react';
import { useAssistantShell } from '@/components/assistant/AssistantShellProvider';
import classes from './dashboard.module.css';

export interface Nudge {
  key: string;
  label: string;
  prompt: string;
  context?: Record<string, unknown>;
  /** Send straight away. Only for read-only questions; anything that could change data is prefilled. */
  send?: boolean;
}

/** One-tap handoff to the platform assistant with the prompt prefilled (or sent, for questions). */
export function NudgeButton({ nudge }: { nudge: Nudge }) {
  const { openWithPrompt } = useAssistantShell();
  return (
    <button
      type="button"
      className={classes.nudge}
      onClick={() => openWithPrompt(nudge.prompt, nudge.context, { send: nudge.send })}
    >
      <IconWand size={18} className={classes.nudgeIcon} aria-hidden />
      <span>{nudge.label}</span>
    </button>
  );
}

export function NudgeList({ nudges }: { nudges: Nudge[] }) {
  if (nudges.length === 0) return null;
  return (
    <div style={{ display: 'grid', gap: 8 }}>
      {nudges.map((n) => <NudgeButton key={n.key} nudge={n} />)}
    </div>
  );
}
