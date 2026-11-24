// @vitest-environment jsdom
/** Typing into the sub-shot inputs with a stateful parent: what the user types is what is saved (QA-SQF-05/26/27). */
import { useState } from 'react';
import { render, screen, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import type { Mock } from 'vitest';
import { VideoAnalyzeStepConfig, createDefaultVideoAnalyzeStepConfig } from './VideoAnalyzeStepConfig';
import type { VideoAnalyzeStepConfig as Cfg } from '@/lib/types/workflow';

vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: [] }) }));

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
  Element.prototype.scrollIntoView = vi.fn();
});
afterEach(() => cleanup());

function harness(extra: Partial<Cfg> = {}) {
  const onChange: Mock = vi.fn();
  function H() {
    const [config, setConfig] = useState({ ...createDefaultVideoAnalyzeStepConfig(), subShotMode: 'Content', ...extra } as Cfg);
    return (
      <MantineProvider>
        <VideoAnalyzeStepConfig config={config} onChange={(n) => { onChange(n); setConfig(n); }} projectId="p1" />
      </MantineProvider>
    );
  }
  render(<H />);
  const last = () => onChange.mock.calls.at(-1)?.[0] as Record<string, unknown> | undefined;
  return { onChange, last };
}
const box = (re: RegExp) => screen.getByRole('textbox', { name: re }) as HTMLInputElement;

describe('VideoAnalyzeStepConfig sub-shot inputs keep what is typed', () => {
  const cases: [RegExp, string, string, number][] = [
    [/^Shortest part/, 'minSubShotSec', '1.5', 1.5],
    [/^Shortest part/, 'minSubShotSec', '0.5', 0.5],
    [/^Shortest part/, 'minSubShotSec', '3', 3],
    [/^Longest part/, 'maxSubShotSec', '4.5', 4.5],
    [/^Longest part/, 'maxSubShotSec', '10', 10],
  ];
  for (const [label, key, typed, expected] of cases) {
    it(`typing ${typed} into ${label.source.slice(1)} saves ${expected}, mid-typing and after blur`, async () => {
      const user = userEvent.setup();
      const { last } = harness();
      await user.click(box(label));
      await user.keyboard(typed);
      expect(last()![key]).toBe(expected);
      await user.tab();
      expect(last()![key]).toBe(expected);
    });
  }

  const blurCases: [RegExp, string, string, number, number][] = [
    // [field, key, typed, value while typing, value after blur]
    [/^Shortest part/, 'minSubShotSec', '99', 99, 10],
    [/^Shortest part/, 'minSubShotSec', '0.2', 0.2, 0.5],
    [/^Longest part/, 'maxSubShotSec', '1', 1, 2.4],
  ];
  for (const [label, key, typed, typing, blurred] of blurCases) {
    it(`${label.source.slice(1)}: ${typed} stays ${typing} while typing and becomes ${blurred} on blur`, async () => {
      const user = userEvent.setup();
      const { last } = harness();
      await user.click(box(label));
      await user.keyboard(typed);
      expect(last()![key]).toBe(typing);
      await user.tab();
      expect(last()![key]).toBe(blurred);
    });
  }

  const trailingPoint: [RegExp, string, string, number][] = [
    [/^Shortest part/, 'minSubShotSec', '99.', 10],
    [/^Shortest part/, 'minSubShotSec', '0.', 0.5],
    [/^Longest part/, 'maxSubShotSec', '1.', 2.4],
  ];
  for (const [label, key, typed, blurred] of trailingPoint) {
    it(`${label.source.slice(1)}: "${typed}" then blur saves the clamped ${blurred}`, async () => {
      const user = userEvent.setup();
      const { last } = harness();
      await user.click(box(label));
      await user.keyboard(typed);
      await user.tab();
      expect(last()![key]).toBe(blurred);
    });
  }

  it('clears the key when the field is emptied', async () => {
    const user = userEvent.setup();
    const { last } = harness({ maxSubShotSec: 5 } as Partial<Cfg>);
    await user.clear(box(/^Longest part/));
    expect(last()!.maxSubShotSec).toBeUndefined();
  });
});
