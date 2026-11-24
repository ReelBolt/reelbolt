/** @vitest-environment node */
/**
 * The struct-as-allowlist (alpha-launch T-018). What these protect: a caller cannot smuggle a field
 * the allowlist does not name; a route never carries a query string or an opaque segment; and the
 * browser's redaction is the same as the server's, pinned by the shared corpus.
 */
import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import {
  ALPHA_FUNNEL_EVENTS,
  CLIENT_ERROR_FIELDS,
  MAX_NAME_LENGTH,
  ROUTE_PLACEHOLDER,
  STATIC_ROUTE_SEGMENTS,
  buildClientErrorPayload,
  isAlphaFunnelEvent,
  routePath,
} from './allowlist';

/** The contract the server also reads (Services/Telemetry/route-redaction.corpus.json). */
interface Corpus {
  staticSegments: string[];
  cases: { input: string; expected: string }[];
}

const CORPUS_URL = new URL(
  '../../../inference/src/ReelBolt.Inference.Api/Services/Telemetry/route-redaction.corpus.json',
  import.meta.url,
);
const CORPUS = JSON.parse(readFileSync(CORPUS_URL, 'utf8')) as Corpus;

describe('the shared route-redaction contract', () => {
  it('uses exactly the corpus static segments, so the two sides cannot drift', () => {
    expect([...STATIC_ROUTE_SEGMENTS].sort()).toEqual([...CORPUS.staticSegments].sort());
  });

  it('redacts every corpus case the way the server does', () => {
    for (const testCase of CORPUS.cases) {
      expect(routePath(testCase.input), `route ${testCase.input}`).toBe(testCase.expected);
    }
  });
});

describe('routePath', () => {
  it('replaces an opaque segment with the placeholder, so a token never ships', () => {
    expect(routePath('/invite/u1.abc.def')).toBe(`/invite/${ROUTE_PLACEHOLDER}`);
    expect(routePath('/app/projects/2f0d5b7c-1a34-4c2e-9f10-8b6a1d2e3f40')).toBe(
      `/app/projects/${ROUTE_PLACEHOLDER}`,
    );
  });

  it('drops the query string and fragment', () => {
    expect(routePath('/app/projects/p1?tab=recent&token=secret#top')).toBe(
      `/app/projects/${ROUTE_PLACEHOLDER}`,
    );
  });

  it('refuses anything that is not a bare pathname', () => {
    expect(routePath('https://evil.example/app/projects')).toBe('');
    expect(routePath('//evil.example/app')).toBe('');
    expect(routePath('projects/p1')).toBe('');
    expect(routePath(undefined)).toBe('');
    expect(routePath(42)).toBe('');
  });

  it('keeps the settings pages by name, the workspace avatar library included', () => {
    expect(routePath('/app/settings/avatar-library')).toBe('/app/settings/avatar-library');
    expect(routePath('/app/settings/avatar-library?item=2f0d5b7c')).toBe('/app/settings/avatar-library');
  });

  it('is case-sensitive, so an unexpected spelling is redacted rather than trusted', () => {
    expect(routePath('/APP/Projects')).toBe(`/${ROUTE_PLACEHOLDER}/${ROUTE_PLACEHOLDER}`);
  });
});

describe('buildClientErrorPayload', () => {
  it('reads only the allowlisted fields and drops everything else', () => {
    const payload = buildClientErrorPayload({
      name: 'TypeError',
      route: '/app',
      // Nothing below is allowlisted: an error message, a form value, a prompt, a project name.
      message: 'failed for secret@example.com',
      stack: 'at ProjectList',
      formValue: 'secret@example.com',
      prompt: 'write me a film about my family',
      projectName: 'My private project',
    } as never);

    expect(Object.keys(payload).sort()).toEqual([...CLIENT_ERROR_FIELDS].sort());
    const serialized = JSON.stringify(payload);
    expect(serialized).not.toContain('secret@example.com');
    expect(serialized).not.toContain('my family');
    expect(serialized).not.toContain('My private project');
    expect(serialized).not.toContain('ProjectList');
  });

  it('bounds the error name', () => {
    expect(buildClientErrorPayload({ name: 'x'.repeat(MAX_NAME_LENGTH + 50) }).name).toHaveLength(MAX_NAME_LENGTH);
  });

  it('falls back to Error when no name is given', () => {
    expect(buildClientErrorPayload({}).name).toBe('Error');
  });
});

describe('isAlphaFunnelEvent', () => {
  it('accepts the four events and nothing else', () => {
    expect(isAlphaFunnelEvent('alpha.signup')).toBe(true);
    expect(isAlphaFunnelEvent('alpha.first_export_initiated')).toBe(true);
    expect(isAlphaFunnelEvent('alpha.first_export')).toBe(false);
    expect(isAlphaFunnelEvent('alpha.made_up')).toBe(false);
    expect(isAlphaFunnelEvent(undefined)).toBe(false);
  });

  it('carries the renamed export event and no longer the old one', () => {
    expect(ALPHA_FUNNEL_EVENTS).toContain('alpha.first_export_initiated');
    expect(ALPHA_FUNNEL_EVENTS).not.toContain('alpha.first_export');
  });
});

describe('run events', () => {
  it('are allowlisted funnel events', () => {
    expect(ALPHA_FUNNEL_EVENTS).toEqual(expect.arrayContaining(['alpha.run_page_opened', 'alpha.run_clicked', 'alpha.run_blocked']));
  });
});
