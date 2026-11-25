/**
 * Collects every browser-reported header-policy problem of a flow: CSP-Report-Only violations
 * (securitypolicyviolation events), COEP-Report-Only issues (ReportingObserver type "coep"),
 * console messages about CSP / CORP / COEP, and responses the browser refused
 * (net::ERR_BLOCKED_BY_RESPONSE, which is what CORP / COEP / XFO enforcement looks like).
 */
import type { BrowserContext, Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

export interface Finding {
  flow: string;
  kind: 'csp' | 'coep' | 'console' | 'blocked';
  directive: string;
  blocked: string;
  source: string;
  disposition?: string;
}

export interface Allow {
  kind?: string;
  directive?: string;
  blocked?: string; // regex
  source?: string; // regex
  flow?: string; // regex
  reason: string;
}

export function loadAllowlist(): Allow[] {
  return JSON.parse(fs.readFileSync(path.join(__dirname, 'allowlist.json'), 'utf8')).allow as Allow[];
}

export function isAllowed(f: Finding, allow: Allow[]): boolean {
  return allow.some(
    (a) =>
      (!a.kind || a.kind === f.kind) &&
      (!a.directive || a.directive === f.directive) &&
      (!a.blocked || new RegExp(a.blocked).test(f.blocked)) &&
      (!a.source || new RegExp(a.source).test(f.source)) &&
      (!a.flow || new RegExp(a.flow).test(f.flow)),
  );
}

const INIT = () => {
  const send = (v: unknown) => {
    try {
      (window as any).__rfv?.(JSON.stringify(v));
    } catch {
      /* binding not ready */
    }
  };
  document.addEventListener('securitypolicyviolation', (e) => {
    send({
      kind: 'csp',
      directive: e.effectiveDirective || e.violatedDirective,
      blocked: e.blockedURI || '(inline)',
      source: `${e.sourceFile || location.href}:${e.lineNumber}`,
      sample: e.sample,
      disposition: e.disposition,
    });
  });
  try {
    new ReportingObserver(
      (reports) => {
        for (const r of reports) {
          if (r.type === 'coep') {
            const b: any = r.body;
            send({ kind: 'coep', directive: 'coep', blocked: b?.blockedURL || `(${b?.destination ?? 'document'})`, source: JSON.stringify(b?.toJSON?.() ?? b) + ' @ ' + location.href });
          }
        }
      },
      { buffered: true },
    ).observe();
  } catch {
    /* unsupported */
  }
};

const CONSOLE = /Content[- ]Security[- ]Policy|Refused to|Cross-Origin-(Embedder|Resource|Opener)|COEP|CORP|blocked by response/i;

/** The origin-sensitive third parties the page asked for, with the resource type (for the policy check). */
export interface External {
  flow: string;
  origin: string;
  type: string;
  url: string;
}

export class Collector {
  findings: Finding[] = [];
  externals: External[] = [];
  flow = 'init';
  private seen = new Set<string>();

  async attach(context: BrowserContext, baseURL: string): Promise<void> {
    await context.exposeBinding('__rfv', (_src, json: string) => this.add(JSON.parse(json)));
    await context.addInitScript(INIT);
    const own = new URL(baseURL).origin;
    context.on('request', (r) => {
      const u = new URL(r.url());
      if (u.origin !== own && /^https?:$/.test(u.protocol)) this.externals.push({ flow: this.flow, origin: u.origin, type: r.resourceType(), url: r.url().slice(0, 160) });
    });
    context.on('requestfailed', (r) => {
      const err = r.failure()?.errorText ?? '';
      if (/ERR_BLOCKED_BY_RESPONSE/.test(err)) this.add({ kind: 'blocked', directive: 'corp/coep/xfo', blocked: r.url(), source: r.frame()?.url() ?? '' });
    });
  }

  watch(page: Page): void {
    page.on('console', (m) => {
      const t = m.text();
      if (CONSOLE.test(t)) this.add({ kind: 'console', directive: m.type(), blocked: t.slice(0, 300), source: m.location().url });
    });
  }

  private add(f: Omit<Finding, 'flow'>): void {
    const finding = { ...f, flow: this.flow };
    const key = `${finding.flow}|${finding.kind}|${finding.directive}|${finding.blocked}|${finding.source}`;
    if (this.seen.has(key)) return;
    this.seen.add(key);
    this.findings.push(finding);
  }
}
