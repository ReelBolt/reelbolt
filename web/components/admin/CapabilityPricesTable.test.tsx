// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ModelCapability, ModelCapabilityPrice } from '@/lib/types/inference-provider';
import { CapabilityPricesTable } from './CapabilityPricesTable';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

const price: ModelCapabilityPrice = {
  id: 'price-1',
  unit: 'LlmInputTokens',
  variant: null,
  costUsdPerUnit: 0.000002,
  creditsMillisPerUnitOverride: null,
  effectiveCreditsMillisPerUnit: 4,
  effectiveMarginPercent: 40,
  effectiveFrom: '2026-01-01T00:00:00Z',
  effectiveTo: null,
  notes: null,
};

const capability = {
  id: 'cap-1',
  capability: 'Chat',
  isDefault: true,
  isEnabled: true,
  priceSource: 'Table',
  lastTestAt: null,
  lastTestOk: null,
  lastTestError: null,
  prices: [price],
} as ModelCapability;

describe('CapabilityPricesTable', () => {
  // Below `sm` each price is a stacked card, so every value carries its own label (hidden by CSS
  // once the real header row shows). The labels are in the DOM either way, next to the header row.
  it('labels every cell for the stacked mobile layout', () => {
    render(
      <MantineProvider>
        <CapabilityPricesTable
          capability={capability}
          label="GPT-4o · Chat"
          now={new Date('2026-06-01T00:00:00Z')}
          onAdd={vi.fn()}
          onRetire={vi.fn()}
        />
      </MantineProvider>,
    );

    for (const label of ['Unit', 'Variant', 'Cost', 'Credits override', 'Effective credits', 'Margin', 'From', 'To', 'Status']) {
      expect(screen.getAllByText(label).length).toBeGreaterThanOrEqual(2);
    }
  });
});
