/**
 * T-200 — dev-only reachability guard for the demuxer spike.  REMOVABLE (see
 * `lib/editing/preview/spike/README.md`).
 *
 * This route exists for ONE reason: acceptance criterion 5. The spike's actual harness runs
 * on a tiny static server (`lib/editing/preview/spike/serve.mjs`) so that no Next dev server
 * races a colleague's `next build` over the shared `web/.next/`, which means the harness is
 * otherwise invisible to the product build. This page makes it visible to the build and
 * therefore provable: in a PRODUCTION build the guard below fires and the route is a 404,
 * so nothing about the spike can be reached from the shipped app.
 *
 * The check is a literal `process.env.NODE_ENV === 'production'` read at render time, and
 * `next build` sets that variable — so the route is rendered (and 404'd) at build time.
 * A Vitest test asserts the guard fires; see `./spike-guard.test.tsx`.
 *
 * Deliberately NOT imported by anything under `app/(app)/**` or `components/**`, and it
 * imports nothing from the spike directory itself — asserted by `./spike-isolation.test.ts`.
 */
import { notFound } from 'next/navigation';

export default function SpikePage() {
  if (process.env.NODE_ENV === 'production') {
    notFound();
  }

  return (
    <main style={{ font: '13px/1.6 ui-monospace, monospace', margin: 24, maxWidth: 720 }}>
      <h1>T-200 — demuxer spike (dev only)</h1>
      <p>
        This route renders nothing useful on purpose. It is the production-build reachability
        guard for the throwaway mediabunny-vs-mp4box measurement harness; in a production
        build it returns <code>notFound()</code>.
      </p>
      <p>
        Run the harness with a static server instead of <code>next dev</code>, so the shared{' '}
        <code>web/.next/</code> is never touched:
      </p>
      <pre>{`cd web
node lib/editing/preview/spike/serve.mjs 0     # then open the printed URL
node lib/editing/preview/spike/run-spike.mjs   # or drive it with Playwright`}</pre>
      <p>
        Everything under <code>lib/editing/preview/spike/</code> and this route are declared
        REMOVABLE in <code>lib/editing/preview/spike/README.md</code>.
      </p>
    </main>
  );
}
