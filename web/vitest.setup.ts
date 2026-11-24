/**
 * Global test harness for the web suite.
 *
 * The suite renders real Mantine components. Mantine's `Transition` (`use-transition.mjs`) arms a
 * `requestAnimationFrame` chain that ends in a `window.setTimeout`, and many other dependencies
 * (SWR retries, jsdom's selection, `user-event`'s wait, `waitFor`) leave a timer pending when a
 * test ends. Nothing cancelled those at the file boundary, so a callback that outlived its file
 * fired after Vitest had torn the jsdom environment down and threw `ReferenceError: window is not
 * defined` out of React's `dispatchSetState` — an *unhandled* error, so `vitest run` exited 1 even
 * though every test passed (the file it was attributed to varied per run).
 *
 * Two things fix that, both scoped to one test at a time:
 *
 *   1. `cleanup()` after each test. `@testing-library/react` only auto-registers it when `afterEach`
 *      is a global, and this project runs Vitest with `globals: false`, so rendered trees were never
 *      unmounted; unmounting runs each component's effect cleanup, which clears its own timers.
 *   2. A sweep of whatever is still pending. `vi.clearAllTimers()` cannot help here — the leaked
 *      timers are real, not fake, and no handle is reachable — so `setTimeout`/`requestAnimationFrame`
 *      are wrapped to record the handles they return and the recorded ones are cancelled after the
 *      test. The wrappers keep returning the real handles, so `.unref()` and friends still work, and
 *      a test that enables fake timers simply stops routing through them (Vitest restores the
 *      wrapper when it restores real timers).
 *
 * This is deliberately not `dangerouslyIgnoreUnhandledErrors`: a genuine post-teardown error that
 * is not a leaked timer still fails the run.
 */
import { afterEach, beforeEach, vi } from 'vitest';
import { cleanup } from '@testing-library/react';

type TimerHandle = ReturnType<typeof setTimeout>;

const realSetTimeout = globalThis.setTimeout;
const realClearTimeout = globalThis.clearTimeout;
const realRequestAnimationFrame = globalThis.requestAnimationFrame;
const realCancelAnimationFrame = globalThis.cancelAnimationFrame;

const pendingTimers = new Set<TimerHandle>();
const pendingFrames = new Set<number>();

// `globalThis === window` under Vitest's jsdom environment, so this covers `window.setTimeout` too.
globalThis.setTimeout = ((handler: TimerHandler, timeout?: number, ...args: unknown[]) => {
  const handle = realSetTimeout(handler as (...a: unknown[]) => void, timeout, ...args);
  pendingTimers.add(handle);
  return handle;
}) as unknown as typeof setTimeout;

globalThis.clearTimeout = ((handle?: TimerHandle) => {
  if (handle !== undefined) pendingTimers.delete(handle);
  return realClearTimeout(handle as never);
}) as unknown as typeof clearTimeout;

// A few files pin `@vitest-environment node`, where there is no animation frame at all.
if (typeof realRequestAnimationFrame === 'function' && typeof realCancelAnimationFrame === 'function') {
  globalThis.requestAnimationFrame = ((callback: FrameRequestCallback) => {
    const id = realRequestAnimationFrame(callback);
    pendingFrames.add(id);
    return id;
  }) as unknown as typeof requestAnimationFrame;

  globalThis.cancelAnimationFrame = ((id: number) => {
    pendingFrames.delete(id);
    return realCancelAnimationFrame(id);
  }) as unknown as typeof cancelAnimationFrame;
}

beforeEach(() => {
  // Only the bookkeeping resets: a timer armed at module scope keeps running.
  pendingTimers.clear();
  pendingFrames.clear();
});

afterEach(() => {
  cleanup();
  for (const handle of pendingTimers) realClearTimeout(handle);
  for (const id of pendingFrames) realCancelAnimationFrame(id);
  pendingTimers.clear();
  pendingFrames.clear();
});

/**
 * i18n without a provider. Hundreds of component tests render a bare component and assert English
 * text; once a component calls useT()/useFormat() it would throw (next-intl needs a provider). So,
 * for tests only, next-intl's hooks fall back to the English catalogue (web/messages/en) whenever no
 * provider is mounted. A test that wants another language uses `renderWithI18n` (web/test/i18n.tsx),
 * whose real provider wins because the real hook is tried first.
 */
vi.mock('next-intl', async (importOriginal) => {
  const real = await importOriginal<typeof import('next-intl')>();
  const { createTranslator } = await import('use-intl/core');
  const { loadMessages } = await import('./i18n/messages');
  const english = () => loadMessages('en');
  const quiet = { onError: () => undefined, getMessageFallback: ({ namespace, key }: { namespace?: string; key: string }) => [namespace, key].filter(Boolean).join('.') };
  // One translator per namespace for the whole test file: the real hook returns a stable function, so the
  // fallback must too, or a component that lists `t` in an effect/memo dependency array re-runs every render.
  const fallbacks = new Map<string, unknown>();
  return {
    ...real,
    useTranslations: (namespace?: string) => {
      try {
        return real.useTranslations(namespace as never);
      } catch {
        const key = namespace ?? '';
        let t = fallbacks.get(key);
        if (!t) {
          t = createTranslator({ locale: 'en', messages: english() as never, namespace: namespace as never, ...quiet });
          fallbacks.set(key, t);
        }
        return t as never;
      }
    },
    useLocale: () => {
      try {
        return real.useLocale();
      } catch {
        return 'en';
      }
    },
  };
});
