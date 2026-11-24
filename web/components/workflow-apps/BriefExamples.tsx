'use client';

import { useId } from 'react';
import type { Persona } from '@/lib/attribution/persona';
import { useT } from '@/i18n';
import { briefExamples, insertExample, type BriefFamily } from './brief-examples';
import styles from './AppRunPage.module.css';

interface BriefExamplesProps {
  family: BriefFamily;
  persona: Persona;
  /** What the box holds now. */
  value: string;
  /** Receives the box's new text; the caller moves focus to the box so it can be edited at once. */
  onInsert: (next: string) => void;
}

/**
 * Three example briefs under the "what should it show" box. Each is a button that puts its sentence
 * in the box (see `insertExample`: nothing the person typed is replaced).
 */
export function BriefExamples({ family, persona, value, onInsert }: BriefExamplesProps) {
  const t = useT('apps.run.briefHelp');
  const labelId = useId();
  const examples = briefExamples(family, persona).map((m) => t.msg(m));
  return (
    <div className={styles.examples} role="group" aria-labelledby={labelId} data-testid="brief-examples">
      <span className={styles.examplesLabel} id={labelId}>{t('label')}</span>
      <ul className={styles.exampleList}>
        {examples.map((example) => (
          <li key={example}>
            <button type="button" className={styles.exampleChip} onClick={() => onInsert(insertExample(value, example, examples))}>
              {example}
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}
