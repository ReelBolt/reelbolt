'use client';

import { errorCopyFor } from '@/lib/error-copy';

/** Last resort when the root layout itself fails: own html/body, inline styles, no providers. */
export default function GlobalError({ reset }: { error: Error & { digest?: string }; reset: () => void }) {
  const copy = errorCopyFor(typeof navigator === 'undefined' ? undefined : navigator.language);
  return (
    <html lang="en">
      <body style={{ margin: 0, fontFamily: 'system-ui, sans-serif', color: '#111', background: '#fff' }}>
        <main style={{ maxWidth: 560, margin: '0 auto', padding: '96px 24px', textAlign: 'center' }}>
          <h1 style={{ fontSize: 28, margin: 0 }}>{copy.heading}</h1>
          <p style={{ marginTop: 24 }}>
            <button
              type="button"
              onClick={reset}
              style={{ padding: '12px 20px', fontSize: 16, fontWeight: 600 }}
            >
              {copy.retry}
            </button>
          </p>
          <p>
            <a href="/" style={{ color: '#111' }}>
              {copy.home}
            </a>
          </p>
        </main>
      </body>
    </html>
  );
}
