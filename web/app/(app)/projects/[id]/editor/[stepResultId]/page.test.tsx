// @vitest-environment jsdom
import React, { Suspense } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';

vi.mock('next/navigation', () => ({ useRouter: () => ({ replace: vi.fn(), push: vi.fn() }) }));
vi.mock('next/link', () => ({ default: ({ children, href, ...rest }: { children: React.ReactNode; href: string }) => <a href={href} {...rest}>{children}</a> }));
vi.mock('@/lib/hooks/use-outputs', () => ({ useOutputVideos: () => ({ data: [], error: undefined }) }));
const open = vi.hoisted(() => ({ fn: vi.fn() }));
vi.mock('@/lib/editing/open-timeline', async (importOriginal) => ({ ...(await importOriginal<object>()), openTimelineForRender: open.fn }));

import OpenRenderInEditorPage from './page';

afterEach(cleanup);
/** A promise React's `use` reads synchronously (already fulfilled), so the page does not suspend. */
const settled = <T,>(value: T): Promise<T> => Object.assign(Promise.resolve(value), { status: 'fulfilled', value });
vi.stubGlobal('matchMedia', (q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true }));

describe('open a render in the editor', () => {
  it('says in words that a video that hides part of the picture cannot be opened, not the generic failure', async () => {
    open.fn.mockRejectedValue(new ApiError('server english', 409, [], { code: 'SEED_WOULD_REVEAL_CENSORED' }));
    render(
      <MantineProvider>
        <Suspense fallback={null}>
          <OpenRenderInEditorPage params={settled({ id: 'p1', stepResultId: 'sr1' })} />
        </Suspense>
      </MantineProvider>,
    );
    const failed = await screen.findByTestId('open-render-failed');
    expect(failed.textContent).toContain('hides part of the picture');
    expect(failed.textContent).not.toContain('could not be opened in the editor');
    expect(failed.textContent).not.toContain('server english');
  });
});
