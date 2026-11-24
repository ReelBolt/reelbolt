import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithI18n } from '@/test/i18n';
import { WorkspaceBar } from '../shell/WorkspaceBar';
import { EarlyPreviewLabel } from '../shell/EarlyPreviewLabel';
import { PhoneDock } from '../phone/PhoneDock';

// Mantine's color-scheme manager reads `window.matchMedia`, which jsdom does not implement.
global.matchMedia = (query: string): MediaQueryList =>
  ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => true,
  }) as MediaQueryList;

describe('editor chrome in Italian', () => {
  it('labels the workspace bar and tooltips in Italian', () => {
    renderWithI18n(<WorkspaceBar active="edit" onChange={() => undefined} />);
    expect(screen.getByRole('navigation', { name: 'Aree di lavoro' })).toBeTruthy();
    expect(screen.getByTestId('workspace-deliver').textContent).toBe('Consegna');
  });

  it('keeps the Early preview label as "Anteprima anticipata"', () => {
    renderWithI18n(<EarlyPreviewLabel />);
    expect(screen.getByText('Anteprima anticipata')).toBeTruthy();
  });

  it('labels the phone dock in Italian', () => {
    renderWithI18n(<PhoneDock active={null} onSelect={() => undefined} />);
    expect(screen.getByTestId('dock-look').textContent).toBe('Look');
  });
});
