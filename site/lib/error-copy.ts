// Copy for app/global-error.tsx, which renders with no providers and so cannot read the catalogue.
// Keep in step with `layout.errorpage` in messages/<locale>/layout.json (lib/error-copy.test.ts checks it).
export interface ErrorCopy {
  heading: string;
  retry: string;
  home: string;
}

export const ERROR_COPY: Record<'en' | 'it', ErrorCopy> = {
  en: { heading: 'Something went wrong loading this page', retry: 'Try again', home: 'Back to the home page' },
  it: { heading: 'Qualcosa è andato storto nel caricare questa pagina', retry: 'Riprova', home: 'Torna alla home' },
};

/** Italian when the browser asks for it, otherwise English. */
export function errorCopyFor(language: string | undefined): ErrorCopy {
  return language && language.toLowerCase().startsWith('it') ? ERROR_COPY.it : ERROR_COPY.en;
}
