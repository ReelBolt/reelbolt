/**
 * The user scope: the email never reaches a storage key. `sha256Hex` is proved against `node:crypto`
 * (including non-ASCII UTF-8), and the one-time migration is proved to move every legacy email-form
 * key to its scoped form, rewrite the identity inside the value, keep a scoped key when both exist,
 * stay idempotent, and leave nothing with `@` in its key.
 */

import { createHash } from 'node:crypto';
import { beforeEach, describe, expect, it } from 'vitest';
import { sha256Hex } from '../scope-hash';
import {
  ANONYMOUS_SCOPE,
  BACKGROUND_RESULTS_PREFIX,
  CLEANUP_CONSENT_PREFIX,
  CLIPBOARD_PREFIX,
  FOLLOW_RUN_PREFIX,
  DISPLAY_PREFIX,
  JOURNAL_PREFIX,
  PREFS_HISTORY_PREFIX,
  QUEUE_PREFIX,
  migrateEditorStorageScope,
  userIdFromEmail,
  userScope,
  userScopeFromEmail,
  PICREVIEW_PREFIX,
  wipeEditorLocalData,
} from '../user-scope';

const nodeHash = (input: string): string => createHash('sha256').update(input, 'utf8').digest('hex');

const displayKey = (user: string) => `${DISPLAY_PREFIX}:${user}`;
const historyKey = (user: string) => `${PREFS_HISTORY_PREFIX}:${user}`;
const clipKey = (user: string) => `${CLIPBOARD_PREFIX}${user}`;
const journalKey = (user: string, timelineId: string) => `${JOURNAL_PREFIX}${user}:${timelineId}`;
const queueKey = (user: string, timelineId: string) => `${QUEUE_PREFIX}${user}:${timelineId}`;

const snapshot = (): Array<[string, string | null]> => Object.keys(localStorage).sort().map((key) => [key, localStorage.getItem(key)]);

beforeEach(() => {
  window.localStorage.clear();
});

describe('scope hash', () => {
  it('sha256Hex matches node:crypto for ASCII, empty, multi-block and non-ASCII UTF-8', () => {
    for (const input of [
      '',
      'a@x.test',
      'anonymous',
      'utente@example.com',
      'naïve café 😀 日本語',
      'Ω≈ç√∫˜µ≤≥÷',
      // The padding / second-block path: 55, 56, 63, 64, 65 bytes and a long address.
      'a'.repeat(55),
      'b'.repeat(56),
      'c'.repeat(63),
      'd'.repeat(64),
      'e'.repeat(65),
      'a.very.long.address.with.many.parts@subdomain.example.com',
      // A lone surrogate and a valid pair: both must encode as U+FFFD / four bytes like TextEncoder.
      '\ud800',
      '😀',
      'a\udfffb',
    ]) {
      expect(sha256Hex(input)).toBe(nodeHash(input));
    }
  });

  it('userScope is the first 16 hex characters of sha256(raw id)', () => {
    for (const id of ['a@x.test', 'anonymous', 'user-1', 'naïve café 😀']) {
      expect(userScope(id)).toBe(nodeHash(id).slice(0, 16));
      expect(userScope(id)).toMatch(/^[0-9a-f]{16}$/);
    }
  });

  it('userScopeFromEmail hashes the legacy raw id, including the anonymous one', () => {
    expect(userScopeFromEmail('  A@X.Test ')).toBe(userScope('a@x.test'));
    expect(userScopeFromEmail(null)).toBe(userScope(userIdFromEmail(null)));
    expect(userScopeFromEmail(undefined)).toBe(ANONYMOUS_SCOPE);
    expect(userScopeFromEmail(null)).not.toBe('anonymous');
  });

  it('no scope contains the email, its local part or its domain', () => {
    const scope = userScopeFromEmail('secret.person@example.com');
    expect(scope).not.toContain('@');
    expect(scope).not.toContain('secret.person');
    expect(scope).not.toContain('example.com');
  });
});

describe('migrateEditorStorageScope', () => {
  it('moves every legacy email-form key, rewriting userId / owner, then deletes the old keys', () => {
    const legacy = 'a@x.test';
    const scope = userScopeFromEmail(legacy);
    const display = JSON.stringify({ density: 'compact', surround: 'dark' });
    const history = JSON.stringify([{ keymap: { undo: 'KeyQ' }, layouts: [], replacedAt: '2026-01-01' }]);
    const clipboard = JSON.stringify({ owner: legacy, clips: [{ id: 'c1' }], anchors: [] });
    const journalEntry = JSON.stringify({ kind: 'entry', entry: { version: 1, projectId: 'p', timelineId: 'tl-1', userId: legacy, savedAt: 5, tabId: 'tab', ops: [], baseDoc: {}, localDoc: {} } });
    const journalPointer = JSON.stringify({ kind: 'pointer', timelineId: 'tl-2', savedAt: 6, tabId: 'tab' });
    const queue = JSON.stringify({ version: 1, projectId: 'p', timelineId: 'tl-1', baseRevision: 7, baseDoc: {}, localDoc: {}, ops: [], savedAt: 7, userId: legacy });

    localStorage.setItem(displayKey(legacy), display);
    localStorage.setItem(historyKey(legacy), history);
    localStorage.setItem(clipKey(legacy), clipboard);
    localStorage.setItem(journalKey(legacy, 'tl-1'), journalEntry);
    localStorage.setItem(journalKey(legacy, 'tl-2'), journalPointer);
    localStorage.setItem(queueKey(legacy, 'tl-1'), queue);

    migrateEditorStorageScope(legacy);

    // Values survive under the scoped key; the identity inside them is rewritten.
    expect(localStorage.getItem(displayKey(scope))).toBe(display);
    expect(localStorage.getItem(historyKey(scope))).toBe(history);
    expect(JSON.parse(localStorage.getItem(clipKey(scope)) ?? 'null').owner).toBe(scope);
    expect(JSON.parse(localStorage.getItem(journalKey(scope, 'tl-1')) ?? 'null').entry.userId).toBe(scope);
    expect(JSON.parse(localStorage.getItem(journalKey(scope, 'tl-2')) ?? 'null')).toEqual(JSON.parse(journalPointer));
    expect(JSON.parse(localStorage.getItem(queueKey(scope, 'tl-1')) ?? 'null').userId).toBe(scope);

    // Old keys are gone.
    for (const old of [displayKey(legacy), historyKey(legacy), clipKey(legacy), journalKey(legacy, 'tl-1'), journalKey(legacy, 'tl-2'), queueKey(legacy, 'tl-1')]) {
      expect(localStorage.getItem(old)).toBeNull();
    }

    // The acceptance is about key names: none may carry the email.
    expect(Object.keys(localStorage).some((key) => key.includes('@'))).toBe(false);
    // …and the identity inside the values is the scope, not the address.
    expect(Object.keys(localStorage).some((key) => (localStorage.getItem(key) ?? '').includes('@'))).toBe(false);
  });

  it('is idempotent: a second call changes nothing', () => {
    const legacy = 'idem@x.test';
    localStorage.setItem(displayKey(legacy), '{"density":"compact"}');
    migrateEditorStorageScope(legacy);
    const first = snapshot();
    migrateEditorStorageScope(legacy);
    expect(snapshot()).toEqual(first);
  });

  it('a first run with an empty store neither writes nor throws', () => {
    expect(() => migrateEditorStorageScope('empty@x.test')).not.toThrow();
    expect(localStorage.length).toBe(0);
  });

  it('keeps the scoped key when both forms exist (new wins, old dropped)', () => {
    const legacy = 'both@x.test';
    const scope = userScopeFromEmail(legacy);
    localStorage.setItem(displayKey(legacy), 'OLD');
    localStorage.setItem(displayKey(scope), 'NEW');

    migrateEditorStorageScope(legacy);

    expect(localStorage.getItem(displayKey(scope))).toBe('NEW');
    expect(localStorage.getItem(displayKey(legacy))).toBeNull();
  });

  it('migrates the anonymous form like any other', () => {
    localStorage.setItem(displayKey(userIdFromEmail(null)), '{"surround":"dark"}');
    migrateEditorStorageScope(null);
    expect(localStorage.getItem(displayKey(ANONYMOUS_SCOPE))).toBe('{"surround":"dark"}');
    expect(Object.keys(localStorage).some((key) => key.endsWith(':anonymous'))).toBe(false);
  });
});

describe('wipeEditorLocalData', () => {
  it('removes both the legacy email-form and the scoped keys', async () => {
    const legacy = 'a@x.test';
    const scope = userScopeFromEmail(legacy);
    localStorage.setItem(journalKey(legacy, 'tl'), '{}');
    localStorage.setItem(journalKey(scope, 'tl'), '{}');
    localStorage.setItem(queueKey(legacy, 'tl'), '{}');
    localStorage.setItem(queueKey(scope, 'tl'), '{}');
    localStorage.setItem(historyKey(legacy), '[]');
    localStorage.setItem(historyKey(scope), '[]');
    localStorage.setItem(clipKey(legacy), '{}');
    localStorage.setItem(clipKey(scope), '{}');
    localStorage.setItem(`${PICREVIEW_PREFIX}${scope}.doc`, '["l"]');

    await wipeEditorLocalData({ broadcast: false });

    expect(Object.keys(localStorage)).toEqual([]);
  });
  it('removes background results (prompts, briefs, AI answers) on sign-out', async () => {
    localStorage.setItem(`${BACKGROUND_RESULTS_PREFIX}${userScopeFromEmail('a@x.test')}.tl`, '[{"intent":{"prompt":"secret"}}]');
    localStorage.setItem('unrelated', 'kept');
    await wipeEditorLocalData({ broadcast: false });
    expect(Object.keys(localStorage)).toEqual(['unrelated']);
  });

  it('removes the saved follow-subject run ids from sessionStorage', async () => {
    sessionStorage.setItem(`${FOLLOW_RUN_PREFIX}abc:p:c`, 'e1');
    sessionStorage.setItem('other', 'keep');
    await wipeEditorLocalData({ broadcast: false });
    expect(sessionStorage.getItem(`${FOLLOW_RUN_PREFIX}abc:p:c`)).toBeNull();
    expect(sessionStorage.getItem('other')).toBe('keep');
  });

  it('removes the cleanup-consent entries of every user, old raw-email form included', async () => {
    const consentKey = (user: string) => `${CLEANUP_CONSENT_PREFIX}v1:${JSON.stringify([user, 'o1', 'p1'])}`;
    localStorage.setItem(consentKey(userScopeFromEmail('a@x.test')), '{}');
    localStorage.setItem(consentKey(userScopeFromEmail('b@x.test')), '{}');
    localStorage.setItem(consentKey('c@x.test'), '{}'); // written by the old raw-email code
    localStorage.setItem(displayKey(userScopeFromEmail('a@x.test')), '{"theme":"dark"}'); // prefs survive

    await wipeEditorLocalData({ broadcast: false });

    expect(Object.keys(localStorage).filter((key) => key.startsWith(CLEANUP_CONSENT_PREFIX))).toEqual([]);
    expect(localStorage.getItem(displayKey(userScopeFromEmail('a@x.test')))).toBe('{"theme":"dark"}');
  });
});
