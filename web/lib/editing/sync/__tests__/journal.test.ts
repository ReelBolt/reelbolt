/** @vitest-environment node */
/**
 * R02.2 the edit journal (fake IndexedDB): one entry per timeline, a synchronous localStorage copy up to ~200 KB
 * and a pointer beyond it, the newest of the two copies wins on read, a clear is never undone by a late write,
 * and "neither store works" is reported as memory-only (the chip must not promise safety it cannot give).
 */

import { IDBFactory } from 'fake-indexeddb';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { EditJournal, JOURNAL_MAX_AGE_MS, JOURNAL_MAX_ENTRIES, JOURNAL_VERSION, journalKey, SYNC_POINTER_LIMIT, type JournalEntry } from '../journal';
import type { QueueStorage } from '../offline-queue';
import { userScopeFromEmail } from '../user-scope';
import { standardDoc } from '../../__tests__/fixtures';

const USER = userScopeFromEmail('me@x.test');

const memoryStorage = (): QueueStorage & { data: Map<string, string> } => {
  const data = new Map<string, string>();
  return { data, get: (k) => data.get(k) ?? null, set: (k, v) => void data.set(k, v), remove: (k) => void data.delete(k), keys: (prefix) => [...data.keys()].filter((k) => k.startsWith(prefix)) };
};

const entry = (over: Partial<JournalEntry> = {}): JournalEntry => {
  const base = standardDoc();
  const local = structuredClone(base);
  local.tracks[0].items[0].name = 'Edited';
  return { version: JOURNAL_VERSION, projectId: 'p', timelineId: 't', baseRevision: 7, baseDoc: base, localDoc: local, ops: [{ type: 'setProperty', itemId: 'c1', key: 'name', value: 'Edited' }], savedAt: 1000, tabId: 'tab-a', userId: USER, ...over };
};

// The journal expires entries by wall clock; these fixtures use tiny savedAt values, so the clock sits just after them.
beforeEach(() => {
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(5000);
});
afterEach(() => vi.useRealTimers());
describe('EditJournal', () => {
  it('round-trips one entry per timeline through IndexedDB and clears it', async () => {
    const journal = new EditJournal({ idb: new IDBFactory(), storage: null });
    await journal.write(entry());
    await journal.write(entry({ baseRevision: 8, savedAt: 2000 }));
    const read = await journal.read(USER, 't');
    expect(read?.baseRevision).toBe(8);
    expect(read?.tabId).toBe('tab-a');
    expect(await journal.read(USER, 'other')).toBeNull();
    await journal.clear(USER, 't');
    expect(await journal.read(USER, 't')).toBeNull();
  });

  it('writes a synchronous localStorage copy before the async put settles', async () => {
    const storage = memoryStorage();
    const journal = new EditJournal({ idb: new IDBFactory(), storage });
    await journal.warm();
    const pending = journal.write(entry());
    // Nothing awaited yet: the pointer/last doc is already readable (a kill within one task still restores).
    expect(JSON.parse(storage.data.get(journalKey(USER, 't')) ?? 'null')).toMatchObject({ kind: 'entry', entry: { baseRevision: 7 } });
    await pending;
  });

  it('keeps only a pointer in localStorage when the entry is over the limit; IndexedDB carries the document', async () => {
    const storage = memoryStorage();
    const journal = new EditJournal({ idb: new IDBFactory(), storage });
    const big = entry();
    big.localDoc.name = 'x'.repeat(SYNC_POINTER_LIMIT + 10);
    await journal.write(big);
    expect(JSON.parse(storage.data.get(journalKey(USER, 't')) ?? 'null')).toMatchObject({ kind: 'pointer', timelineId: 't' });
    expect((await journal.read(USER, 't'))?.localDoc.name).toHaveLength(SYNC_POINTER_LIMIT + 10);
  });

  it('reads the newest of the two copies', async () => {
    const storage = memoryStorage();
    const idb = new IDBFactory();
    const journal = new EditJournal({ idb, storage });
    await journal.write(entry({ savedAt: 1000, baseRevision: 7 }));
    // A later synchronous-only copy (the tab died before the async put): localStorage is newer.
    storage.set(journalKey(USER, 't'), JSON.stringify({ kind: 'entry', entry: entry({ savedAt: 5000, baseRevision: 9 }) }));
    expect((await journal.read(USER, 't'))?.baseRevision).toBe(9);
  });

  it('a clear is never undone by a write that was still in flight', async () => {
    const journal = new EditJournal({ idb: new IDBFactory(), storage: memoryStorage() });
    void journal.write(entry());
    await journal.clear(USER, 't');
    expect(await journal.read(USER, 't')).toBeNull();
  });

  it('ignores a corrupt or foreign record', async () => {
    const storage = memoryStorage();
    const journal = new EditJournal({ idb: null, storage });
    storage.set(journalKey(USER, 't'), '{not json');
    expect(await journal.read(USER, 't')).toBeNull();
    storage.set(journalKey(USER, 't'), JSON.stringify({ kind: 'entry', entry: entry({ timelineId: 'someone-else' }) }));
    expect(await journal.read(USER, 't')).toBeNull();
  });

  it('says memory when neither store works, device when either does', async () => {
    expect(await new EditJournal({ idb: null, storage: null }).probe()).toBe('memory');
    expect(await new EditJournal({ idb: new IDBFactory(), storage: null }).probe()).toBe('device');
    expect(await new EditJournal({ idb: null, storage: memoryStorage() }).probe()).toBe('device');
    const refusing: QueueStorage = { get: () => null, set: () => undefined, remove: () => undefined };
    expect(await new EditJournal({ idb: null, storage: refusing }).probe()).toBe('memory');
  });

  it('v3 upgrade re-keys a v2 journal entry from the raw id to the scope', async () => {
    const idb = new IDBFactory();
    const base = standardDoc();
    // Seed a v2 database exactly as the previous version left it: keyed by [raw id, timelineId].
    const seeded = await new Promise<IDBDatabase>((resolve, reject) => {
      const req = idb.open('reelbolt-editor', 2);
      req.onupgradeneeded = () => {
        req.result.createObjectStore('journal', { keyPath: ['userId', 'timelineId'] });
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });
    await new Promise<void>((resolve, reject) => {
      const tx = seeded.transaction('journal', 'readwrite');
      tx.objectStore('journal').put(entry({ userId: 'me@x.test', baseDoc: base, localDoc: base }));
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
    });
    seeded.close();

    const journal = new EditJournal({ idb, storage: null });
    expect((await journal.read(USER, 't'))?.userId).toBe(USER);
    expect(await journal.read('me@x.test', 't')).toBeNull();
  });

  describe('expiry and cap', () => {
    const DAY = 24 * 60 * 60 * 1000;
    it('never offers an entry older than 30 days and drops it', async () => {
      const storage = memoryStorage();
      const now = 100 * DAY;
      const journal = new EditJournal({ idb: new IDBFactory(), storage, now: () => now });
      await journal.write(entry({ savedAt: now - JOURNAL_MAX_AGE_MS - 1 }));
      expect(await journal.read(USER, 't')).toBeNull();
      expect(storage.data.has(journalKey(USER, 't'))).toBe(false);
      await journal.write(entry({ savedAt: now - JOURNAL_MAX_AGE_MS + DAY }));
      expect((await journal.read(USER, 't'))?.savedAt).toBe(now - JOURNAL_MAX_AGE_MS + DAY);
    });

    it('evicts expired entries from both stores when it opens, keeping fresh ones', async () => {
      const storage = memoryStorage();
      const idb = new IDBFactory();
      const now = 100 * DAY;
      const writer = new EditJournal({ idb, storage, now: () => 0 });
      await writer.write(entry({ timelineId: 'old', savedAt: now - 31 * DAY }));
      await writer.write(entry({ timelineId: 'fresh', savedAt: now - DAY }));
      const opener = new EditJournal({ idb, storage, now: () => now });
      await opener.warm();
      expect(storage.data.has(journalKey(USER, 'old'))).toBe(false);
      expect(storage.data.has(journalKey(USER, 'fresh'))).toBe(true);
      const reader = new EditJournal({ idb, storage: null, now: () => 0 });
      expect(await reader.read(USER, 'old')).toBeNull();
      expect((await reader.read(USER, 'fresh'))?.timelineId).toBe('fresh');
    });

    it('keeps only the newest entries when over the size cap', async () => {
      const storage = memoryStorage();
      const idb = new IDBFactory();
      const now = 100 * DAY;
      const writer = new EditJournal({ idb, storage, now: () => now });
      for (let i = 0; i < JOURNAL_MAX_ENTRIES + 3; i++) await writer.write(entry({ timelineId: `t${i}`, savedAt: now - (JOURNAL_MAX_ENTRIES + 3 - i) * 1000 }));
      await new EditJournal({ idb, storage, now: () => now }).warm();
      expect(storage.data.size).toBe(JOURNAL_MAX_ENTRIES);
      expect(storage.data.has(journalKey(USER, 't0'))).toBe(false);
      expect(storage.data.has(journalKey(USER, `t${JOURNAL_MAX_ENTRIES + 2}`))).toBe(true);
    });
  });
});
