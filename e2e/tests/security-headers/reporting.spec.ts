/**
 * The CSP/COEP reporting pipeline end to end (T-003 endpoint, T-004 nginx wiring): a real browser provokes
 * a violation, posts the report to /api/v1/telemetry/csp-report through nginx, and a row with ONLY closed-set
 * fields lands in csp_violation_counts. A hostile report carrying a review token and a query string proves
 * nothing sensitive is stored. Needs the stack's postgres (fixtures/stack psql).
 */
import { test, expect, chromium } from '@playwright/test';
import { loadStack, psql, sqlStr } from '../../fixtures/stack';

const uniq = Date.now().toString(36);
const ENDPOINT = '/api/v1/telemetry/csp-report';

const rows = (where: string) => psql(`select report_type, disposition, effective_directive, blocked_origin, document_path, ua_family, count from csp_violation_counts where ${where} order by 1,2,3,4,5`);

test('the endpoint is wired: the header advertises it and a POST answers 204 with an empty body', async ({ request }) => {
  const { baseUrl } = loadStack();
  const page = await request.get(`${baseUrl}/`);
  const h = page.headers();
  expect(h['reporting-endpoints']).toContain(`csp-endpoint="${ENDPOINT}"`);
  expect(h['content-security-policy-report-only']).toMatch(/report-uri \/api\/v1\/telemetry\/csp-report/);
  expect(h['content-security-policy-report-only']).toMatch(/report-to csp-endpoint/);
  const res = await request.post(`${baseUrl}${ENDPOINT}`, { data: '{"csp-report":{}}', headers: { 'content-type': 'application/csp-report' } });
  expect(res.status()).toBe(204);
  expect(await res.text()).toBe('');
  const junk = await request.post(`${baseUrl}${ENDPOINT}`, { data: 'not json', headers: { 'content-type': 'text/plain' } });
  expect(junk.status(), 'malformed input is still a 204').toBe(204);
});

// Chrome registers a Reporting-Endpoints / report-to endpoint only on an HTTPS origin (on plain http the endpoint is never
// registered and nothing is posted; the legacy report-uri is ignored when report-to is present). So this test drives the
// stack's self-signed HTTPS port. Production is HTTPS only.
const HTTPS_URL = process.env.SECHDR_HTTPS_URL ?? 'https://localhost:9412';

test('a browser violation on the site and on /app is reported and stored as directive + origin only', async () => {
  test.setTimeout(240_000);
  // The default headless shell never delivers Reporting API reports; the full Chrome build in new headless mode does.
  const browser = await chromium.launch({ channel: 'chromium' });
  const baseUrl = HTTPS_URL;
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage();
  const siteHost = `https://site-${uniq}.example.invalid`; // stored as scheme + host, never the path or query
  const appHost = `https://app-${uniq}.example.invalid`;
  const posts: Array<{ url: string; type: string; status: number }> = [];
  page.on('requestfinished', async (r) => {
    if (r.method() === 'POST' && r.url().endsWith(ENDPOINT)) posts.push({ url: r.url(), type: r.headers()['content-type'] ?? '', status: (await r.response())?.status() ?? 0 });
  });
  for (const [path, host] of [['/', siteHost], ['/app/login', appHost]] as const) {
    await page.goto(`${baseUrl}${path}`);
    await page.waitForLoadState('networkidle').catch(() => undefined);
    // script-src does not allow this host; the policy is report-only so the page is unaffected.
    await page.evaluate((src) => {
      const s = document.createElement('script');
      s.src = src;
      document.head.append(s);
    }, `${host}/probe.js?token=SECRET-QUERY`);
  }
  await expect
    .poll(() => rows(`blocked_origin in (${sqlStr(siteHost)}, ${sqlStr(appHost)})`).split('\n').filter(Boolean).length, { timeout: 150_000, intervals: [2000], message: 'both violations land in csp_violation_counts (the Reporting API batches deliveries, about a minute in Chrome)' })
    .toBeGreaterThanOrEqual(2);
  const stored = rows(`blocked_origin in (${sqlStr(siteHost)}, ${sqlStr(appHost)})`).split('\n').map((l) => l.split('|'));
  const bySite = stored.find((c) => c[3] === siteHost)!;
  const byApp = stored.find((c) => c[3] === appHost)!;
  for (const [row, docPath] of [[bySite, '/'], [byApp, '/app/login']] as const) {
    expect(row[0], 'report_type').toBe('csp');
    expect(row[1], 'disposition').toBe('report');
    expect(row[2], 'effective_directive').toMatch(/^script-src/);
    expect(row[4], 'document_path').toBe(docPath);
    expect(row[5], 'ua_family').toBe('chrome');
  }
  // The query string of the blocked URL never reaches the table.
  expect(psql(`select count(*) from csp_violation_counts where blocked_origin ilike '%SECRET%' or document_path ilike '%SECRET%'`)).toBe('0');
  test.info().annotations.push({ type: 'browser-posts', description: JSON.stringify(posts) });
  await context.close();
  await browser.close();
});

test('only allowlisted columns exist, and a hostile report with a review token and query stores nothing sensitive', async ({ request }) => {
  const { baseUrl } = loadStack();
  expect(psql(`select string_agg(column_name, ',' order by column_name) from information_schema.columns where table_name = 'csp_violation_counts' and column_name not in ('id')`))
    .toBe('blocked_origin,count,day,disposition,document_path,effective_directive,report_type,ua_family');

  const token = `TOK${uniq}abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG`;
  const evil = `https://evil-${uniq}.example.invalid`;
  const report = {
    'csp-report': {
      'document-uri': `http://localhost/app/review/g/${token}?email=victim@example.com&x=1#frag`,
      referrer: `http://localhost/app/review/g/${token}`,
      'blocked-uri': `${evil}/leak/${token}?q=secret-value`,
      'effective-directive': 'script-src-elem',
      'violated-directive': 'script-src-elem',
      disposition: 'report',
      'source-file': `http://localhost/app/review/g/${token}/chunk.js`,
      'script-sample': `secret-sample-${token}`,
      'line-number': 12,
      'original-policy': 'default-src self',
      'status-code': 200,
    },
  };
  const res = await request.post(`${baseUrl}${ENDPOINT}`, { data: JSON.stringify(report), headers: { 'content-type': 'application/csp-report', 'user-agent': 'Mozilla/5.0 Chrome/130 hostile-ua-token' } });
  expect(res.status()).toBe(204);
  expect(await res.text()).toBe('');

  await expect.poll(() => rows(`blocked_origin = ${sqlStr(evil)}`), { timeout: 20_000, intervals: [500] }).not.toBe('');
  const row = rows(`blocked_origin = ${sqlStr(evil)}`).split('|');
  expect(row[4], 'document_path of a guest review page').toBe('(sensitive)');
  expect(row[3], 'blocked_origin is scheme + host only').toBe(evil);
  // Nothing from the token, the query, the sample or the User-Agent is anywhere in the table.
  const dump = psql(`select string_agg(t::text, ' ') from csp_violation_counts t`);
  for (const needle of [token, 'victim@example.com', 'secret-value', 'secret-sample', 'hostile-ua-token', '#frag', 'chunk.js']) {
    expect(dump, `table must not contain "${needle}"`).not.toContain(needle);
  }
});
