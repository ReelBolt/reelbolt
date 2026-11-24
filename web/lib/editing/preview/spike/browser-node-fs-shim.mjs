/**
 * T-200 — browser shim for `node:fs/promises`.  REMOVABLE (see README.md).
 *
 * WHY THIS FILE EXISTS AT ALL
 * ───────────────────────────
 * The ticket's environment note said both demuxers "ship browser-ready ESM with no bare
 * imports on their browser entry path", so "no bundler and no import map are required".
 * That is FALSE for mediabunny 1.61.0, and the browser says so plainly:
 *
 *     Access to script at 'node:fs/promises' from origin 'http://127.0.0.1:PORT'
 *     has been blocked by CORS policy: Cross origin requests are only supported for
 *     protocol schemes: chrome, chrome-untrusted, data, http, https.
 *     requestfailed: node:fs/promises net::ERR_FAILED
 *
 * The chain is static and unavoidable:
 *
 *     dist/modules/src/index.js
 *       → ./source.js          (line 9: `import * as nodeAlias from './node.js'`)
 *          → ./node.js         (line 9: `export * as fs from 'node:fs/promises'`)
 *
 * Because the URL scheme itself (`node:`) is rejected, a server CANNOT intercept this —
 * only a resolution-time rewrite can. mediabunny's own `package.json` expresses exactly
 * this rewrite for bundlers:
 *
 *     "browser": { "./dist/modules/src/node.js": false, "node:fs/promises": false }
 *
 * i.e. "replace the Node-only module with nothing". `false` is a bundler convention that
 * native browser ESM ignores, so the harness reproduces the library's own intent with a
 * one-entry import map in `harness.html` — the smallest possible adaptation, and NOT a
 * silent change of approach.
 *
 * WHY IT CANNOT AFFECT THE MEASUREMENT
 * ────────────────────────────────────
 * `node.fs` is touched in exactly two places in the whole library:
 *
 *     source.js:985  const fileHandle = await node.fs.open(filePath, 'r');
 *     target.js:496  this._fileHandle = await node.fs.open(filePath, 'w');
 *
 * both inside the Node-only `FilePathSource` / `FilePathTarget` classes, which a browser
 * harness never constructs (it uses `BlobSource`). Nothing on the inspect/seek/read path
 * can reach this module. The stub therefore throws loudly rather than returning a
 * plausible-looking object, so a hypothetical reach is a hard failure rather than a wrong
 * number.
 */

const unsupported = (what) => () => {
  throw new Error(
    `node:fs/promises.${what} is not available in the browser (T-200 spike shim). ` +
      'mediabunny only calls it from FilePathSource/FilePathTarget, which the harness does not use.',
  );
};

export const open = unsupported('open');
export const readFile = unsupported('readFile');
export const writeFile = unsupported('writeFile');
export const stat = unsupported('stat');
export const readdir = unsupported('readdir');

const shim = { open, readFile, writeFile, stat, readdir };

export default shim;
