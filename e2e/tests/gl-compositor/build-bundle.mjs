#!/usr/bin/env node
/**
 * Bundles `harness-entry.ts` (and the product modules it imports) into a single IIFE for the
 * real-WebGL2 smoke test. Uses the Vite that already sits in `web/node_modules` (no install, no
 * network) and writes the bundle to a temp dir that is never committed.
 *
 *   node e2e/tests/gl-compositor/build-bundle.mjs [outDir]
 *
 * Prints the bundle path on the last stdout line.
 */

import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..', '..');
const outDir = path.resolve(process.argv[2] ?? process.env.GL_COMPOSITOR_BUNDLE_DIR ?? '/tmp/gl-compositor-bundle');

const viteEntry = path.join(repoRoot, 'web', 'node_modules', 'vite', 'dist', 'node', 'index.js');
const { build } = await import(pathToFileURL(viteEntry).href);

mkdirSync(outDir, { recursive: true });
await build({
  configFile: false,
  root: repoRoot,
  logLevel: 'warn',
  clearScreen: false,
  build: {
    outDir,
    emptyOutDir: true,
    minify: false,
    target: 'es2022',
    sourcemap: false,
    lib: {
      entry: path.join(here, 'harness-entry.ts'),
      formats: ['iife'],
      name: 'GlCompositorHarness',
      fileName: () => 'gl-compositor-harness.js',
    },
    rollupOptions: {
      output: { extend: true },
    },
  },
});

console.log(path.join(outDir, 'gl-compositor-harness.js'));
