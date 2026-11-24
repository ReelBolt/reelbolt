import { beforeAll, describe, expect, it } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { PasswordInput } from './PasswordInput';

describe('PasswordInput', () => {
  beforeAll(() => {
    window.matchMedia = ((query: string) =>
      ({ matches: false, media: query, addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {} }) as unknown as MediaQueryList);
  });

  it('has a keyboard-reachable, named, pressed-state eye toggle', () => {
    render(
      <MantineProvider>
        <PasswordInput label="Password" />
      </MantineProvider>,
    );
    const toggle = screen.getByRole('button', { name: 'Show password' });
    expect(toggle.getAttribute('tabindex')).toBe('0');
    expect(toggle.getAttribute('aria-pressed')).toBe('false');
    fireEvent.mouseDown(toggle);
    expect(screen.getByRole('button', { name: 'Show password' }).getAttribute('aria-pressed')).toBe('true');
    expect((screen.getByLabelText('Password') as HTMLInputElement).type).toBe('text');
  });
});
