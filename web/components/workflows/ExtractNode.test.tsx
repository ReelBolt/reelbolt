// @vitest-environment jsdom
import { render, screen, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { ReactFlowProvider } from '@xyflow/react';
import { ExtractNode } from './nodes/ExtractNode';
import type { ExtractNodeData } from './nodes/ExtractNode';
import type { ExtractStepConfig as ExtractStepConfigValue } from '@/lib/types/workflow';

afterEach(() => cleanup());

beforeAll(() => {
  // Mock matchMedia
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation(query => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });

  // Mock ResizeObserver
  // A `function`, not an arrow: vitest 4+ refuses `new` on an arrow-function mock.
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
});

const renderWithProviders = (component: React.ReactElement) => {
  return render(
    <MantineProvider>
      <ReactFlowProvider>
        {component}
      </ReactFlowProvider>
    </MantineProvider>,
  );
};

describe('ExtractNode', () => {
  it('renders without throwing when extractConfig is a partial object (missing operation)', () => {
    const nodeData: ExtractNodeData = {
      step: {
        id: 'step-1',
        stepType: 'Extract',
        label: 'Test Extract Step',
        agentDefinitionId: '',
        extractConfig: { version: 1 } as ExtractStepConfigValue, // Partial config missing 'operation' - simulates JSON deserialization with incomplete data
        videoAnalyzeConfig: null,
        videoCompileConfig: null,
        thumbnailGenerateConfig: null,
        voiceoverConfig: null,
        videoGenerateConfig: null,
        conditionExpression: null,
        trueBranchStepOrder: null,
        falseBranchStepOrder: null,
        loopSourceExpression: null,
        loopTargetStepOrder: null,
        maxIterations: 0,
        minScore: null,
        parallelAgentIds: [],
        inputMappingJson: null,
        cacheMode: null,
        agentInputContextMode: null,
        selectedPriorStepOrders: [],
      },
      stepNumber: 1,
      allSteps: [],
      currentStepIndex: 0,
      expanded: false,
      pinned: false,
      onExpandChange: vi.fn(),
      onTogglePin: vi.fn(),
      onChange: vi.fn(),
      onRemove: vi.fn(),
    };

    // Should not throw when rendering - this verifies that the merge-over-defaults
    // pattern correctly handles partial extractConfig objects. Without the fix,
    // this would crash when trying to call .toLowerCase() on undefined config.operation
    expect(() => {
      renderWithProviders(<ExtractNode data={nodeData} />);
    }).not.toThrow();

    // Verify the default operation chip renders with the merged-default operation (Project), in
    // the plain-language wording the card now uses instead of the raw operation name.
    expect(screen.getByText('Keeps the key details')).toBeTruthy();
  });

  it('renders without throwing with a full extractConfig', () => {
    const nodeData: ExtractNodeData = {
      step: {
        id: 'step-1',
        stepType: 'Extract',
        label: 'Test Extract Step',
        agentDefinitionId: '',
        extractConfig: {
          version: 1,
          operation: 'Resolve',
          inputs: {},
          path: null,
          fields: null,
          idField: null,
          idPrefix: 'i',
          sortBy: null,
          take: null,
          skip: 0,
          idsPath: null,
          recordsPath: null,
          onUnknownId: 'Fail',
          categories: null,
          includeExtensions: null,
          excludePathContains: null,
          includeSummaries: true,
          includeContent: false,
          maxCharsPerFile: 2000,
          maxOutputChars: 24000,
          expect: null,
        },
        videoAnalyzeConfig: null,
        videoCompileConfig: null,
        thumbnailGenerateConfig: null,
        voiceoverConfig: null,
        videoGenerateConfig: null,
        conditionExpression: null,
        trueBranchStepOrder: null,
        falseBranchStepOrder: null,
        loopSourceExpression: null,
        loopTargetStepOrder: null,
        maxIterations: 0,
        minScore: null,
        parallelAgentIds: [],
        inputMappingJson: null,
        cacheMode: null,
        agentInputContextMode: null,
        selectedPriorStepOrders: [],
      },
      stepNumber: 1,
      allSteps: [],
      currentStepIndex: 0,
      expanded: false,
      pinned: false,
      onExpandChange: vi.fn(),
      onTogglePin: vi.fn(),
      onChange: vi.fn(),
      onRemove: vi.fn(),
    };

    // Should not throw when rendering with a full config
    expect(() => {
      renderWithProviders(<ExtractNode data={nodeData} />);
    }).not.toThrow();
  });
});
