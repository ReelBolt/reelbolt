// @vitest-environment jsdom
import React from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { installDomStubs } from '../../background/__tests__/helpers';
import { AudioJobStatus } from '../AudioJobStatus';

installDomStubs();

afterEach(cleanup);

describe('AudioJobStatus', () => {
  it('shows a running chip from a local job when the activity feed has no row', () => {
    render(
      <MantineProvider>
        <AudioJobStatus kind="loudness" executionId="e1" />
      </MantineProvider>,
    );
    const chip = screen.getByTestId('inline-job-status');
    expect(chip.querySelector('[data-state="running"]')).toBeTruthy();
  });
});
