/**
 * T-200 — dev-only static file server for the demuxer harness.  REMOVABLE (see README.md).
 *
 * WHY NOT `next dev`: every agent on this worktree shares `web/.next/`, and `next dev`
 * writes it — a dev server here would race a colleague's `next build`.  This server also
 * removes Next, React and a bundler from the measurement, which is the point: the subject
 * is the demuxers, not a framework.
 *
 * Root is the `web/` directory, so the harness page can import the two demuxers by the
 * relative URLs the ticket specifies:
 *
 *   /node_modules/mediabunny/dist/modules/src/index.js
 *   /node_modules/mp4box/dist/mp4box.all.mjs
 *
 * Usage:
 *   node lib/editing/preview/spike/serve.mjs [port]     # port 0 = pick a free one
 *
 * Prints `SPIKE_SERVER_READY <origin>` on stdout once listening, then serves until killed.
 */
import { createServer } from 'node:http';
import { createReadStream, statSync } from 'node:fs';
import { extname, normalize, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = fileURLToPath(new URL('.', import.meta.url));
/** `web/`, i.e. three levels up from `web/lib/editing/preview/spike/`. */
const WEB_ROOT = resolve(HERE, '..', '..', '..', '..');

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.mp4': 'video/mp4',
  '.map': 'application/json; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
};

const port = Number(process.argv[2] ?? 0);

const server = createServer((req, res) => {
  const url = new URL(req.url ?? '/', 'http://localhost');
  const rel = url.pathname === '/' ? '/lib/editing/preview/spike/harness.html' : url.pathname;

  // Path-traversal guard: resolve, then require the result to stay under WEB_ROOT.
  const abs = resolve(WEB_ROOT, '.' + normalize(decodeURIComponent(rel)));
  if (abs !== WEB_ROOT && !abs.startsWith(WEB_ROOT + sep)) {
    res.writeHead(403).end('forbidden');
    return;
  }

  let stat;
  try {
    stat = statSync(abs);
    if (stat.isDirectory()) {
      res.writeHead(403).end('forbidden');
      return;
    }
  } catch {
    res.writeHead(404).end('not found: ' + rel);
    return;
  }

  // Range support, so the fixture can be fetched in pieces if a demuxer ever needs it.
  const mime = MIME[extname(abs)] ?? 'application/octet-stream';
  const range = req.headers.range;
  if (range) {
    const m = /^bytes=(\d*)-(\d*)$/.exec(range);
    if (m) {
      const start = m[1] === '' ? Math.max(0, stat.size - Number(m[2])) : Number(m[1]);
      const end = m[1] === '' || m[2] === '' ? stat.size - 1 : Math.min(Number(m[2]), stat.size - 1);
      if (start <= end && start < stat.size) {
        res.writeHead(206, {
          'Content-Type': mime,
          'Content-Range': `bytes ${start}-${end}/${stat.size}`,
          'Accept-Ranges': 'bytes',
          'Content-Length': end - start + 1,
          'Cache-Control': 'no-store',
        });
        createReadStream(abs, { start, end }).pipe(res);
        return;
      }
    }
    res.writeHead(416, { 'Content-Range': `bytes */${stat.size}` }).end();
    return;
  }

  res.writeHead(200, {
    'Content-Type': mime,
    'Content-Length': stat.size,
    'Accept-Ranges': 'bytes',
    // No caching: the harness must always measure the real file, and repeated
    // `fetch` of the fixture is itself one of the measured latencies.
    'Cache-Control': 'no-store',
  });
  createReadStream(abs).pipe(res);
});

server.listen(port, '127.0.0.1', () => {
  const { port: bound } = server.address();
  console.log(`SPIKE_SERVER_READY http://127.0.0.1:${bound}`);
});
