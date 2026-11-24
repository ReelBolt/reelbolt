// Redaction and safe-output helpers shared by the perf harness. Kept in one module so the rules
// cannot drift between the recorder (`measure.mjs`) and the analyser (`analyze.mjs`), and so the
// unit test in `sanitize.test.mjs` pins them.
import fs from 'node:fs';
import path from 'node:path';

/** Query parameters that carry a capability or a signature. */
const SENSITIVE_PARAM =
  /([?&])(token|access_token|id_token|code|sig|signature|expires|x-amz-[^=&#]*|x-goog-[^=&#]*)=[^&#]*/gi;

/** Response/request headers that carry a credential or a redirect to a capability URL. */
const SENSITIVE_HEADER = /^(cookie|set-cookie|authorization|proxy-authorization|location|x-api-key|x-auth-token|x-amz-.*|x-goog-.*)$/i;

/** `/api/v1/output-links/{token}` is a capability URL: the path alone fetches the video. */
const OUTPUT_LINK_PATH = /\/api\/v1\/output-links\/[^/?#]+/g;

/**
 * Redact capability-bearing URL parts. Works on absolute and relative URLs alike (a plain string
 * rewrite) so it cannot accidentally rewrite a relative URL into a placeholder origin.
 */
export function redactUrl(raw) {
  if (typeof raw !== 'string' || raw.length === 0) return raw;
  return raw
    .replace(OUTPUT_LINK_PATH, '/api/v1/output-links/{token}')
    .replace(SENSITIVE_PARAM, '$1$2=REDACTED');
}

/** Write a file only the current user can read. */
export function writePrivate(file, data) {
  fs.writeFileSync(file, data, { mode: 0o600 });
  try { fs.chmodSync(file, 0o600); } catch { /* filesystems without modes */ }
}

/** Resolve the nearest existing ancestor, so a not-yet-created path is still canonicalised. */
function canonical(target) {
  let current = path.resolve(target);
  const tail = [];
  while (!fs.existsSync(current)) {
    const parent = path.dirname(current);
    if (parent === current) break;
    tail.unshift(path.basename(current));
    current = parent;
  }
  return path.join(fs.realpathSync(current), ...tail);
}

/**
 * The output directory holds a HAR (session cookies for the account) and the browser's
 * storageState. Refuse anything inside the repository — including through a symlink, which is why
 * both sides are canonicalised rather than compared lexically.
 */
export function assertSafeOutDir(out, repo) {
  const target = canonical(out);
  const root = canonical(repo);
  if (target === root || target.startsWith(root + path.sep)) {
    throw new Error(`output must not be inside the repository (it would hold session cookies and HARs): ${target}`);
  }
}

/**
 * Strip credentials from a recorded HAR. If sanitisation throws, the HAR is deleted rather than
 * left on disk unsanitized.
 */
export function sanitizeHar(file) {
  if (!fs.existsSync(file)) return;
  try {
    const har = JSON.parse(fs.readFileSync(file, 'utf8'));
    for (const entry of har.log?.entries || []) {
      for (const side of ['request', 'response']) {
        const headers = entry[side]?.headers;
        if (Array.isArray(headers)) {
          entry[side].headers = headers.filter((h) => !SENSITIVE_HEADER.test(String(h.name)));
        }
        if (entry[side]?.cookies) delete entry[side].cookies;
      }
      if (entry.request?.url) entry.request.url = redactUrl(entry.request.url);
      if (entry.response?.redirectURL) entry.response.redirectURL = redactUrl(entry.response.redirectURL);
      if (Array.isArray(entry.request?.queryString)) {
        for (const q of entry.request.queryString) {
          if (SENSITIVE_HEADER.test(String(q.name)) || /^(token|access_token|id_token|code|sig|signature|expires|x-amz-.*|x-goog-.*)$/i.test(String(q.name))) {
            q.value = 'REDACTED';
          }
        }
      }
      // The login body is a plaintext password, and nothing after login needs a body.
      delete entry.request?.postData;
      delete entry.request?.postDataMimeType;
      delete entry.response?.content?.text;
    }
    writePrivate(file, JSON.stringify(har));
  } catch (error) {
    try { fs.unlinkSync(file); } catch { /* the file may already be gone */ }
    throw error;
  }
}
