import path from 'node:path';
import { ESLint } from 'eslint';
import { describe, expect, it } from 'vitest';
import i18next from 'eslint-plugin-i18next';
import tsParser from '@typescript-eslint/parser';
import rb from '../eslint-rules.mjs';
import { literalStringOptions } from '../lint-options.mjs';

// The same options object eslint.config.mjs uses (i18n/lint-options.mjs), applied to fixtures.

async function lint(file: string) {
  const eslint = new ESLint({ ignore: false,
    cwd: path.join(__dirname, '..', '..'),
    overrideConfigFile: true,
    overrideConfig: [
      {
        files: ['**/*.tsx'],
        languageOptions: { parser: tsParser, parserOptions: { ecmaFeatures: { jsx: true } } },
        plugins: { i18next, rb: rb as never },
        rules: {
          'i18next/no-literal-string': ['error', literalStringOptions],
          'rb/no-literal-notification': 'error',
        },
      },
    ],
  });
  const [result] = await eslint.lintFiles([path.join(__dirname, 'fixtures', file)]);
  return result.messages;
}

describe('ESLint i18n ratchet', () => {
  it('flags literal text, text attributes and notification strings', async () => {
    const messages = await lint('hardcoded.fixture.tsx');
    const lines = messages.map((m) => `${m.ruleId}@${m.line}`);
    expect(lines).toEqual(expect.arrayContaining(['i18next/no-literal-string@6', 'i18next/no-literal-string@8', 'rb/no-literal-notification@3', 'rb/no-literal-notification@4']));
    expect(messages.length).toBe(4);
  });

  it('does not excuse ordinary functions whose names merely start with t', async () => {
    const messages = await lint('callees.fixture.tsx');
    const lines = messages.map((m) => `${m.ruleId}@${m.line}`);
    // toast('..'), top('..'), trim('..'), obj.title('..') are NOT translators and must still be reported.
    expect(lines).toEqual([12, 13, 14, 15, 16, 17].map((n) => `i18next/no-literal-string@${n}`));
  });
  it('accepts a translated component', async () => {
    expect(await lint('translated.fixture.tsx')).toEqual([]);
  });
});
