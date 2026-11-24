import { readFileSync } from 'node:fs';
import i18next from 'eslint-plugin-i18next';
import coreWebVitals from 'eslint-config-next/core-web-vitals';
import nextTypescript from 'eslint-config-next/typescript';

// Flat config. eslint-config-next 16 ships its shareable configs as flat
// arrays only, and `next lint` — which is what loaded the old .eslintrc.json —
// is deprecated in Next.js 15 and removed in 16, so `eslint` is invoked
// directly from the lint script instead.
// Files whose text already comes from the catalogue (messages/<locale>/*.json). A migrated file may not
// gain a literal string in its markup again: the list only grows. See docs/i18n.md.
const migrated = JSON.parse(readFileSync(new URL('./i18n/migrated.json', import.meta.url), 'utf8'));

export default [
  {
    ignores: ['.next/**', 'out/**', 'next-env.d.ts', 'functions/_pick-locale.js'],
  },
  ...coreWebVitals,
  ...nextTypescript,
  {
    rules: {
      '@next/next/no-img-element': 'error',
      'jsx-a11y/alt-text': 'error',
      // `app/[locale]` matches every single-segment URL, so the rule would flag links to /docs/ and
      // to the English-only legal pages. Internal links go through @/i18n/link or a plain <a>
      // on purpose (see i18n/link.tsx).
      '@next/next/no-html-link-for-pages': 'off',
    },
  },
  {
    ...i18next.configs['flat/recommended'],
    files: migrated,
  },
];
