// @vitest-environment jsdom
import React from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { installDomStubs } from '../../background/__tests__/helpers';
import { RenderChecks } from '../RenderChecks';
import { renderCheckMessage } from '@/lib/editing/audio/render-checks';

installDomStubs();
afterEach(cleanup);

describe('renderCheckMessage', () => {
  it('maps kind and numbers to a key', () => {
    expect(renderCheckMessage({ kind: 'long_silence', severity: 'medium', startSec: 3, endSec: 9.25 })).toEqual({ key: 'silence', values: { from: '3.0', to: '9.3' } });
    expect(renderCheckMessage({ kind: 'loudness_off_target', severity: 'low', measured: -22, limit: -16 }).key).toBe('loudnessQuiet');
    expect(renderCheckMessage({ kind: 'loudness_off_target', severity: 'low', measured: null, limit: null }).key).toBe('loudnessGeneric');
    expect(renderCheckMessage({ kind: 'nope' as 'clipping', severity: 'low' }).key).toBe('other');
  });
});

describe('RenderChecks', () => {
  it('renders nothing without checks', () => {
    render(<MantineProvider><RenderChecks checks={[]} /></MantineProvider>);
    expect(screen.queryByTestId('render-checks')).toBeNull();
  });

  it('shows a sentence with a severity word and never the engine detail', () => {
    render(
      <MantineProvider>
        <RenderChecks checks={[
          { kind: 'long_silence', severity: 'medium', startSec: 3, endSec: 9, measured: 6, limit: 3, detail: 'English, do not display' },
          { kind: 'clipping', severity: 'high' },
        ]} />
      </MantineProvider>,
    );
    const rows = screen.getAllByTestId('render-check');
    expect(rows[0].getAttribute('data-kind')).toBe('clipping');
    expect(rows[1].textContent).toContain('silent from 3.0 s to 9.0 s');
    expect(rows[1].textContent).toContain('Worth a look');
    expect(rows[1].querySelector('svg')).toBeTruthy();
    expect(screen.queryByText(/do not display/)).toBeNull();
  });
});
