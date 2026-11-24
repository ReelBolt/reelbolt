import type { ReactNode } from 'react';
import { Section } from '@/components/layout/Section';
import { Panel } from '@/components/ui/Panel';
import { GridOverlay } from '@/components/ui/GridOverlay';

export interface HowStepCopy {
  title: string;
  description: string;
}

/**
 * The three-step "how it works" band the home page and every audience page share: drop in what you
 * have, say what it is for, get a reviewed cut you can change. The callers supply the words (the
 * audience pages put their own examples in the first two steps); the layout and the numbering live
 * here so the two can never drift apart.
 */
export function HowItWorks({
  id = 'how-it-works',
  title,
  intro,
  steps,
  children,
}: {
  id?: string;
  title: string;
  intro?: string;
  steps: readonly HowStepCopy[];
  /** An optional closing line under the steps (the home page links the feature breakdown). */
  children?: ReactNode;
}) {
  return (
    <Section id={id} className="relative overflow-hidden">
      <GridOverlay columns={4} />
      <div className="relative text-center">
        <h2 className="mt-3 font-display text-3xl font-bold text-ink sm:text-4xl">{title}</h2>
        {intro && <p className="mx-auto mt-4 max-w-2xl text-ink-muted">{intro}</p>}
      </div>
      <ol className="relative mt-10 grid grid-cols-1 gap-6 md:grid-cols-3">
        {steps.map((step) => (
          <li key={step.title}>
            <Panel tone="paper-2" className="h-full p-6">
              <h3 className="font-display text-base font-bold text-ink">{step.title}</h3>
              <p className="mt-2 text-sm text-ink-muted">{step.description}</p>
            </Panel>
          </li>
        ))}
      </ol>
      {children && <div className="relative mt-8 text-center text-sm text-ink-muted">{children}</div>}
    </Section>
  );
}
