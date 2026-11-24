// @vitest-environment jsdom
/** T-002 (R12.3): the import result sheet and the candidate picker end in one relink call with the chosen file. */

import React, { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ImportResultSheet } from '../interchange/ImportResultSheet';
import { OfflineRelink } from '../interchange/OfflineRelink';
import { importSheetModel } from '@/lib/editing/interchange/handoff-ui';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
afterEach(() => cleanup());

const model = importSheetModel({
  matched: [{ name: 'intro.mov', fileId: 'f0', how: 'nameAndDuration' }],
  offline: [
    { itemId: 'm-amb', name: 'Shared.mov', reason: 'ambiguous' },
    { itemId: 'm-gone', name: 'gone.mov', reason: 'notFound' },
    { itemId: 'm-len', name: 'long.mov', reason: 'durationMismatch' },
  ],
  ambiguous: [
    {
      name: 'Shared.mov',
      candidates: [
        { fileId: 'fa', name: 'Shared.mov', durationSec: 10, sizeBytes: 5 * 1024 * 1024 },
        { fileId: 'fb', name: 'Shared.mov', durationSec: 20, sizeBytes: 9 * 1024 * 1024 },
      ],
    },
  ],
});
const doc = {
  markers: [
    { id: 'm-amb', name: 'Offline: Shared.mov', frame: 0, durationFrames: 30 },
    { id: 'm-gone', name: 'Offline: gone.mov', frame: 40, durationFrames: 30 },
  ],
} as never;

function Harness({ onRelink }: { onRelink: (markerId: string, ref: string) => string | null }) {
  const [sheet, setSheet] = useState<typeof model | null>(model);
  const [request, setRequest] = useState<string | null>(null);
  const hints = Object.fromEntries(model.offline.map((o) => [o.name.toLowerCase(), { reasonWords: o.reasonWords, candidates: o.candidates }]));
  return (
    <MantineProvider>
      <OfflineRelink doc={doc} options={[{ value: 'file:fz', label: 'other.mov' }]} readOnly={false} onSeek={() => {}} onRelink={onRelink} hints={hints} requestedMarkerId={request} onRequestHandled={() => setRequest(null)} />
      <ImportResultSheet model={sheet} readOnly={false} onClose={() => setSheet(null)} onRelink={(id) => { setSheet(null); setRequest(id); }} />
    </MantineProvider>
  );
}

describe('ImportResultSheet', () => {
  it('lists matched files and every offline reason in words', () => {
    render(<Harness onRelink={() => null} />);
    expect((screen.getByTestId('import-headline')).textContent).toContain('1 file matched, 3 offline.');
    expect((screen.getByTestId('import-matched')).textContent).toContain('intro.mov: Matched by name and length');
    expect((screen.getByTestId('import-reason-Shared.mov')).textContent).toContain('Two files share this name; pick one');
    expect((screen.getByTestId('import-reason-gone.mov')).textContent).toContain('No file with this name in the project');
    expect((screen.getByTestId('import-reason-long.mov')).textContent).toContain("The file's length differs from the clip");
    expect((screen.getByTestId('import-relink-Shared.mov')).textContent).toContain('Pick a file');
  });

  it('an ambiguous row offers a picker and relinks to the chosen file', async () => {
    const onRelink = vi.fn(() => null);
    render(<Harness onRelink={onRelink} />);
    fireEvent.click(screen.getByTestId('import-relink-Shared.mov'));
    expect((await screen.findByTestId('offline-candidates')).textContent).toContain('Shared.mov, 0:20, 9 MB');
    fireEvent.click(screen.getByTestId('offline-candidate-fb'));
    fireEvent.click(screen.getByTestId('offline-confirm'));
    expect(onRelink).toHaveBeenCalledWith('m-amb', 'file:fb');
  });

  it('a not-found row opens the normal relink dialog without a picker', async () => {
    render(<Harness onRelink={() => null} />);
    fireEvent.click(screen.getByTestId('import-relink-gone.mov'));
    expect(await screen.findByTestId('offline-source')).toBeTruthy();
    expect(screen.queryByTestId('offline-candidates')).toBeNull();
  });
});
