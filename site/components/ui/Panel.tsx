import type { ReactNode } from 'react';
import { CornerBrackets } from './CornerBrackets';

type Tone = 'paper' | 'paper-2' | 'ink' | 'accent-deep';

interface PanelProps {
  tone?: Tone;
  brackets?: boolean;
  /** Opt-in hairline border. Cards are borderless by default. */
  bordered?: boolean;
  className?: string;
  children: ReactNode;
}

const TONE_CLASS: Record<Tone, string> = {
  paper: 'bg-paper',
  'paper-2': 'bg-paper-2',
  ink: 'bg-ink text-paper border-ink',
  'accent-deep': 'bg-accent-deep text-white border-accent-deep',
};

export function Panel({ tone = 'paper-2', brackets, bordered, className, children }: PanelProps) {
  return (
    <div className={`relative ${bordered ? 'border border-line' : ''} ${TONE_CLASS[tone]} ${className ?? ''}`}>
      {brackets && (
        <CornerBrackets tone={tone === 'accent-deep' ? 'white' : tone === 'ink' ? 'paper' : 'ink'} />
      )}
      {children}
    </div>
  );
}
