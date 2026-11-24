'use client';

import { useEffect } from 'react';

const COPY = {
  en: {
    title: 'Something went wrong',
    body: 'ReelBolt hit an unexpected problem. Reload the page, or go back to the dashboard.',
    reload: 'Reload page',
    home: 'Back to the dashboard',
  },
  it: {
    title: 'Qualcosa è andato storto',
    body: 'ReelBolt ha avuto un problema imprevisto. Ricarica la pagina, oppure torna alla dashboard.',
    reload: 'Ricarica la pagina',
    home: 'Torna alla dashboard',
  },
} as const;

function pickLang(): 'en' | 'it' {
  if (typeof document === 'undefined') return 'en';
  const raw = document.documentElement.lang || (typeof navigator === 'undefined' ? '' : navigator.language) || '';
  return raw.toLowerCase().startsWith('it') ? 'it' : 'en';
}

/** Replaces the root layout, so no providers: own html/body, inline styles, plain strings. */
export default function GlobalError({ error }: { error: Error & { digest?: string }; reset: () => void }) {
  useEffect(() => {
    console.error('Application failed', error);
  }, [error]);
  const lang = pickLang();
  const c = COPY[lang];
  const button = { padding: '8px 16px', fontSize: 14, borderRadius: 8, border: '1px solid #888', background: 'transparent', color: 'inherit', textDecoration: 'none' } as const;
  return (
    <html lang={lang}>
      <body style={{ fontFamily: 'system-ui, sans-serif', margin: 0, minHeight: '100vh', display: 'grid', placeItems: 'center', padding: 16 }}>
        <div role="alert" style={{ maxWidth: 480 }}>
          <h1 style={{ fontSize: 20 }}>{c.title}</h1>
          <p>{c.body}</p>
          <div style={{ display: 'flex', gap: 8 }}>
            <button type="button" style={button} onClick={() => window.location.reload()}>{c.reload}</button>
            <a href="/app/dashboard" style={button}>{c.home}</a>
          </div>
        </div>
      </body>
    </html>
  );
}
