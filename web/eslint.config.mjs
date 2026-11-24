import { readdirSync, readFileSync } from 'node:fs';
import coreWebVitals from 'eslint-config-next/core-web-vitals';
import nextTypescript from 'eslint-config-next/typescript';
import i18next from 'eslint-plugin-i18next';
import rbI18n from './i18n/eslint-rules.mjs';
import { literalStringOptions } from './i18n/lint-options.mjs';

// Areas whose user-facing strings have all moved into web/messages. The list grows as each lane
// finishes (i18n/migrated.json); inside these globs a hard-coded string is an ERROR, elsewhere it is not checked.
// Each area lane records its globs in its own file (i18n/migrated/<area>.json) so two lanes never edit the same file.
const migrated = [
  ...JSON.parse(readFileSync(new URL('./i18n/migrated.json', import.meta.url), 'utf8')).globs,
  ...readdirSync(new URL('./i18n/migrated/', import.meta.url)).filter((f) => f.endsWith('.json')).flatMap(
    (f) => JSON.parse(readFileSync(new URL(`./i18n/migrated/${f}`, import.meta.url), 'utf8')).globs,
  ),
];


// Flat config. Next.js 16 removed `next lint`, which is what used to load the
// old .eslintrc.json, and eslint-config-next 16 publishes its shareable
// configs as flat arrays only — so the lint script calls eslint directly.
export default [
  {
    ignores: ['.next/**', 'out/**', 'next-env.d.ts', '.scratch/**', 'i18n/__tests__/fixtures/**'],
  },
  ...coreWebVitals,
  ...nextTypescript,
  ...(migrated.length
    ? [
        {
          files: migrated,
          ignores: ['**/*.test.{ts,tsx}', '**/__tests__/**', '**/*.stories.tsx'],
          plugins: { i18next, rb: rbI18n },
          rules: {
            'i18next/no-literal-string': ['error', literalStringOptions],
            'rb/no-literal-notification': 'error',
          },
        },
      ]
    : []),
  {
    // A caught error's raw `.message` is the server's English text (or a stack-ish string). Anything a person
    // reads goes through describeError(t, err, fallback) (lib/api/describe-error.ts). Where the raw text is
    // genuinely needed (parsing, logging, a non-API error with an own wording), disable the line WITH a reason:
    //   // eslint-disable-next-line no-restricted-syntax -- <why this is not shown as the server wrote it>
    files: ['app/**/*.{ts,tsx}', 'components/**/*.{ts,tsx}'],
    ignores: ['**/*.test.{ts,tsx}', '**/__tests__/**', '**/*.stories.tsx'],
    rules: {
      'no-restricted-syntax': [
        'error',
        {
          selector: `JSXExpressionContainer MemberExpression[property.name="message"][object.name=/^(e|err|error|cause|ex|exception|failed)$/]`,
          message: 'Do not print a caught error\'s .message in JSX; use describeError(t, err) from lib/api/describe-error.',
        },
        {
          selector: `CallExpression[callee.object.name="notifications"][callee.property.name="show"] MemberExpression[property.name="message"][object.name=/^(e|err|error|cause|ex|exception|failed)$/]`,
          message: 'Do not put a caught error\'s .message in a notification; use describeError(t, err) from lib/api/describe-error.',
        },
        {
          selector: `ConditionalExpression[test.type="BinaryExpression"][test.operator="instanceof"][test.right.name="Error"] > MemberExpression[property.name="message"]`,
          message: '`err instanceof Error ? err.message : ...` shows the raw server text; use describeError(t, err, fallback).',
        },
        {
          selector: `LogicalExpression[operator="&&"][left.type="BinaryExpression"][left.operator="instanceof"][left.right.name="Error"][right.type="MemberExpression"][right.property.name="message"]`,
          message: '`err instanceof Error && err.message` (alone, in a ternary test or before `|| fallback`) shows the raw server text; use describeError(t, err, fallback).',
        },
      ],
    },
  },
];
