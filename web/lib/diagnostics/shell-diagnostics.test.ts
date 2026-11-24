// @vitest-environment jsdom
import { describe, expect, it } from 'vitest';
import { buildDiagnosticsReport, capDiagnostics, redactKeyName, reduceUserAgent, collectShellDiagnostics, jsonDepth, MAX_DIAGNOSTICS_BYTES } from './shell-diagnostics';

describe('shell diagnostics', () => {
  it('reports key and cookie names but never values', async () => {
    localStorage.setItem('reelbolt.theme', 'SECRET_LOCAL_VALUE');
    sessionStorage.setItem('reelbolt.tour', 'SECRET_SESSION_VALUE');
    document.cookie = 'reelbolt_user=alice@example.com%20SECRET_COOKIE';
    document.cookie = 'other=SECRET_OTHER';
    document.body.innerHTML =
      '<div class="mantine-AppShell-root" data-resizing="true"><button class="mantine-Burger-root" data-opened="true"></button><nav class="mantine-AppShell-navbar"><a></a></nav></div>';
    const report = await buildDiagnosticsReport({ win: window, doc: document, buildId: 'abc123' });
    console.log(report);
    for (const secret of ['SECRET_LOCAL_VALUE', 'SECRET_SESSION_VALUE', 'SECRET_COOKIE', 'SECRET_OTHER', 'alice@example.com']) {
      expect(report).not.toContain(secret);
    }
    const parsed = JSON.parse(report);
    expect(parsed.storage.localStorageKeys).toEqual(['reelbolt.theme']);
    expect(parsed.storage.sessionStorageKeys).toEqual(['reelbolt.tour']);
    expect(parsed.storage.cookieNames).toEqual(['reelbolt_user', '<other:<=16>']);
    expect(parsed.storage.reelboltUserCookieCount).toBe(1);
    expect(parsed.buildId).toBe('abc123');
    expect(parsed.shell.burgerOpened).toBe(true);
    expect(parsed.shell.navbar.children).toBe(1);
    expect(parsed.shell.appShellRootData).toEqual({ 'data-resizing': 'true' });
  });
});

describe('capDiagnostics', () => {
  it('drops the largest fields and flags truncated when over the cap', () => {
    const big = { time: 't', buildId: 'b', shell: { huge: 'x'.repeat(20000), small: 1 }, storage: { a: [1] } };
    const out = capDiagnostics(big);
    expect(new TextEncoder().encode(JSON.stringify(out)).length).toBeLessThanOrEqual(MAX_DIAGNOSTICS_BYTES);
    expect(out.truncated).toBe(true);
    expect((out.shell as Record<string, unknown>).huge).toBeUndefined();
  });
});

describe('realistic full collection', () => {
  it('is at most 8 deep and at most 16384 bytes', async () => {
    for (let i = 0; i < 300; i += 1) localStorage.setItem(`key-${i}-${'k'.repeat(150)}`, 'v');
    for (let i = 0; i < 300; i += 1) document.cookie = `cookie${i}${'c'.repeat(150)}=v`;
    const longClasses = Array.from({ length: 60 }, (_, i) => `cls-${i}-${'x'.repeat(40)}`).join(' ');
    document.head.innerHTML = Array.from({ length: 10 }, () =>
      `<style data-mantine-styles="inline">.app-shell{${'a:b;'.repeat(2000)}}</style>`).join('');
    document.body.innerHTML =
      `<div class="mantine-AppShell-root"><nav class="mantine-AppShell-navbar ${longClasses}"><a></a></nav>` +
      Array.from({ length: 100 }, () => `<div class="mantine-Modal-root ${longClasses}"></div>`).join('') + '</div>';
    const report = await collectShellDiagnostics({ win: window, doc: document, buildId: 'abc' });
    expect(jsonDepth(report)).toBeLessThanOrEqual(8);
    expect(new TextEncoder().encode(JSON.stringify(report)).length).toBeLessThanOrEqual(MAX_DIAGNOSTICS_BYTES);
    expect(jsonDepth({ a: [{ b: 1 }] })).toBe(3);
  });
});

describe('redactKeyName', () => {
  it('redacts emails and ids inside known keys', () => {
    expect(redactKeyName('reelbolt:editor:display:alice@example.com')).toBe('reelbolt:editor:display:<user>');
    expect(redactKeyName('reelbolt:editor:journal:11111111-1111-4111-8111-111111111111')).toBe('reelbolt:editor:journal:<id>');
    expect(redactKeyName('reelbolt:editor:clipboard:bob', ['bob'])).toBe('reelbolt:editor:clipboard:<user>');
    expect(redactKeyName('reelbolt:editor:theme')).toBe('reelbolt:editor:theme');
  });
  it('never leaks an email from an unknown key', () => {
    expect(redactKeyName('zzz-carol@example.com')).toMatch(/^<other:/);
  });
  it('never outputs anything email-like (property style)', () => {
    const prefixes = ['reelbolt:', 'rb_', 'zzz', 'reelforge-', ''];
    const locals = ['a', 'alice.b', 'x_y+z', 'UPPER'];
    const doms = ['example.com', 'a.b.co', 'x.io'];
    for (const p of prefixes) for (const l of locals) for (const d of doms) for (const sep of ['', ':', '/']) {
      expect(redactKeyName(`${p}${sep}${l}@${d}`)).not.toMatch(/[^\s]+@[^\s]+\.[a-z]+/i);
      expect(redactKeyName(`${p}x:${l}@${d}:tail`)).not.toContain('@');
    }
  });
  it('reduces the user agent and drops unlisted data attributes', async () => {
    expect(reduceUserAgent('Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Chrome/126.0.0.0 Mobile Safari/537.36'))
      .toBe('Chrome 126 on Android');
    document.body.innerHTML = '<div class="mantine-AppShell-root" data-resizing="true" data-secret="x"></div>';
    const r = JSON.parse(await buildDiagnosticsReport({ win: window, doc: document }));
    expect(r.shell.appShellRootData).toEqual({ 'data-resizing': 'true' });
  });
});

describe('redactKeyName percent-encoded addresses', () => {
  it('redacts a %40-encoded email in a known key', () => {
    const out = redactKeyName('reelbolt:editor:display:bob%40example.com');
    expect(out).not.toMatch(/example|%40|@/i);
  });
});
