import { describe, expect, it } from 'vitest';
import { render } from '@testing-library/react';
import type { ReactElement } from 'react';
import { IntlProvider } from '@/i18n/provider';
import { useT } from '@/i18n';
import type { T } from '@/i18n/translator';

const seen: T[] = [];
function Probe(): ReactElement {
  const t = useT('common');
  seen.push(t);
  return <p>{t('save')}</p>;
}

function ui(locale: string, save: string): ReactElement {
  return (
    <IntlProvider locale={locale} messages={{ common: { save } }}>
      <Probe />
    </IntlProvider>
  );
}

describe('IntlProvider stability', () => {
  it('keeps `t` when a new messages object with equal content arrives, and updates on a real change', () => {
    seen.length = 0;
    const { rerender, container } = render(ui('en', 'Save'));
    const first = seen[seen.length - 1];
    rerender(ui('en', 'Save'));
    expect(seen[seen.length - 1]).toBe(first);

    rerender(ui('it', 'Salva'));
    expect(container.textContent).toBe('Salva');
    expect(seen[seen.length - 1]).not.toBe(first);

    rerender(ui('it', 'Salva ora'));
    expect(container.textContent).toBe('Salva ora');
  });
});
