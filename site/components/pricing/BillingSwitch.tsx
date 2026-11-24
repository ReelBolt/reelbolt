'use client';

import { useRef, useState, type KeyboardEvent } from 'react';
import { useT } from '@/i18n';

type Billing = 'monthly' | 'annual';
const OPTIONS: readonly Billing[] = ['monthly', 'annual'];

/**
 * The Monthly/Annual choice above the plan cards. The cards are server-rendered with both prices;
 * this island only flips `data-billing` on the wrapper that carries it, so nothing re-renders and
 * nothing moves. Default is Monthly. It is a radio group: arrows move and select, Home and End jump.
 */
export function BillingSwitch() {
  const t = useT('plans.billing');
  const [value, setValue] = useState<Billing>('monthly');
  const refs = useRef<Record<Billing, HTMLButtonElement | null>>({ monthly: null, annual: null });

  function choose(next: Billing) {
    setValue(next);
    refs.current[next]?.closest('[data-billing]')?.setAttribute('data-billing', next);
    refs.current[next]?.focus();
  }

  function onKeyDown(e: KeyboardEvent) {
    const i = OPTIONS.indexOf(value);
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') choose(OPTIONS[(i + 1) % OPTIONS.length]);
    else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') choose(OPTIONS[(i + OPTIONS.length - 1) % OPTIONS.length]);
    else if (e.key === 'Home') choose(OPTIONS[0]);
    else if (e.key === 'End') choose(OPTIONS[OPTIONS.length - 1]);
    else return;
    e.preventDefault();
  }

  return (
    <div role="radiogroup" aria-label={t('label')} onKeyDown={onKeyDown} className="mb-6 inline-flex border border-line">
      {OPTIONS.map((option) => {
        const on = value === option;
        return (
          <button
            key={option}
            ref={(el) => {
              refs.current[option] = el;
            }}
            type="button"
            role="radio"
            aria-checked={on}
            tabIndex={on ? 0 : -1}
            onClick={() => choose(option)}
            className={`px-4 py-2 font-sans text-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong ${
              on ? 'bg-ink text-paper' : 'text-ink-muted hover:text-ink'
            }`}
          >
            {t(option)}
          </button>
        );
      })}
    </div>
  );
}
