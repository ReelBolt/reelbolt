/**
 * R02.2: the edit journal — "never lose an edit".
 *
 * The autosave PATCH is the only thing that makes an edit durable on the server. Between an edit and
 * its PATCH (the debounce, a retry backoff, a 401, a server error, a tab killed by the browser) the
 * edit lives only in memory. The journal closes that gap: ONE entry per timeline —
 * `{ baseRevision, baseDoc, localDoc, ops, savedAt, tabId }`, the same shape the offline queue uses so
 * the same reconciler can rebase it — written to two places:
 *
 *   - **localStorage** (synchronous, so a tab killed within the same task still has it) — but only
 *     when the entry fits {@link SYNC_POINTER_LIMIT} (~200 KB); a bigger document leaves a small
 *     pointer, and the IndexedDB copy is the one that carries it.
 *   - **IndexedDB** (one store, key = timelineId) — the async put is STARTED synchronously inside the
 *     page-hide handler, so the browser has it in flight before the page goes away.
 *
 * Writes for one timeline are chained so a late `put` can never resurrect an entry that was already
 * cleared. The entry is cleared when the server confirms the revision. On load the newest of the two
 * copies is offered back to the user (see `SyncClient.checkRecovery`).
 *
 * Truth rule: when NEITHER store is usable the journal says so (`probe()` → `'memory'`) and the status
 * chip tells the user "Edits are held in memory only" — no false promise of safety.
 */

import type { QueueEntry, QueueStorage } from './offline-queue';
import { browserQueueStorage } from './offline-queue';
import { ANONYMOUS_SCOPE, JOURNAL_PREFIX, userScope } from './user-scope';

export const JOURNAL_VERSION = 1;
/** The largest entry (in characters of JSON) the synchronous localStorage copy carries. */
export const SYNC_POINTER_LIMIT = 200 * 1024;

/** An entry older than this is never offered and is evicted when the journal opens. */
export const JOURNAL_MAX_AGE_MS = 30 * 24 * 60 * 60 * 1000;
/** The most entries kept on the device (all users): beyond it the oldest are evicted when the journal opens. */
export const JOURNAL_MAX_ENTRIES = 25;

const DB_NAME = 'reelbolt-editor';
const STORE_NAME = 'journal';

export interface JournalEntry extends Omit<QueueEntry, 'version'> {
  version: typeof JOURNAL_VERSION;
  tabId: string;
}

/** What is stored in localStorage: the whole entry when it fits, else a pointer to the IndexedDB copy. */
type LocalRecord = { kind: 'entry'; entry: JournalEntry } | { kind: 'pointer'; timelineId: string; savedAt: number; tabId: string };

/** Per user: `reelbolt:editor:journal:{scope}:{timelineId}`. Reading as another user never reaches this user's entry, even by key. */
export const journalKey = (userId: string, timelineId: string): string => `${JOURNAL_PREFIX}${userId}:${timelineId}`;
/** The pre-per-user key (no user in it): never read, deleted on sight. */
const legacyJournalKey = (timelineId: string): string => `${JOURNAL_PREFIX}${timelineId}`;
const chainKey = (userId: string, timelineId: string): string => `${userId}\u0000${timelineId}`;

export interface EditJournalOptions {
  /** The IndexedDB factory (default `indexedDB`; `null` = unavailable). Injectable for tests. */
  idb?: IDBFactory | null;
  /** The synchronous store (default `localStorage`; `null` = unavailable). */
  storage?: QueueStorage | null;
  /** The clock (default `Date.now`). Injectable for tests. */
  now?: () => number;
}

const defaultIdb = (): IDBFactory | null => {
  try {
    return typeof indexedDB === 'undefined' ? null : indexedDB;
  } catch {
    return null;
  }
};

const isEntry = (value: unknown, userId: string, timelineId: string): value is JournalEntry => {
  const e = value as Partial<JournalEntry> | null;
  return !!e && e.version === JOURNAL_VERSION && e.userId === userId && e.timelineId === timelineId && Array.isArray(e.ops) && typeof e.baseRevision === 'number' && !!e.localDoc && !!e.baseDoc;
};

function request<T>(req: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error ?? new Error('IndexedDB request failed'));
  });
}

export class EditJournal {
  private readonly idb: IDBFactory | null;
  private readonly storage: QueueStorage | null;
  private readonly now: () => number;
  private evicted: Promise<void> | null = null;
  private db: Promise<IDBDatabase | null> | null = null;
  /** The open database once the first `open()` resolved: lets a write start its transaction synchronously. */
  private opened: IDBDatabase | null = null;
  /** Per-timeline chain: puts and deletes reach IndexedDB in the order they were asked for. */
  private readonly chains = new Map<string, Promise<void>>();
  /** The last IndexedDB put failed (quota / blocked): the journal is not keeping edits safe, so `probe()` says `'memory'`. */
  private writeFailed = false;
  /** Called when a put fails, so the status chip can say so at once. */
  onWriteFailure: (() => void) | null = null;

  constructor(options: EditJournalOptions = {}) {
    this.idb = options.idb === undefined ? defaultIdb() : options.idb;
    this.storage = options.storage === undefined ? browserQueueStorage : options.storage;
    this.now = options.now ?? Date.now;
  }

  private open(): Promise<IDBDatabase | null> {
    if (this.db) return this.db;
    const factory = this.idb;
    if (!factory) {
      this.db = Promise.resolve(null);
      return this.db;
    }
    this.db = new Promise<IDBDatabase | null>((resolve) => {
      try {
        const req = factory.open(DB_NAME, 3);
        req.onupgradeneeded = (event) => {
          const db = req.result;
          // v1 keyed entries by timelineId alone (no user): drop that store, its entries are never read.
          if (event.oldVersion < 2) {
            if (db.objectStoreNames.contains(STORE_NAME)) db.deleteObjectStore(STORE_NAME);
            db.createObjectStore(STORE_NAME, { keyPath: ['userId', 'timelineId'] });
            return;
          }
          // v2 keyed by [userId, timelineId], but `userId` was the raw email. v3 re-keys every record
          // to the scope, inside the same upgrade transaction: collect, then delete + put (never put in place).
          if (event.oldVersion < 3) {
            if (!db.objectStoreNames.contains(STORE_NAME)) {
              db.createObjectStore(STORE_NAME, { keyPath: ['userId', 'timelineId'] });
              return;
            }
            const tx = req.transaction;
            if (!tx) return;
            const store = tx.objectStore(STORE_NAME);
            const all = store.getAll();
            all.onsuccess = () => {
              for (const record of all.result as JournalEntry[]) {
                if (!record || typeof record.userId !== 'string' || typeof record.timelineId !== 'string') continue;
                const scoped = userScope(record.userId);
                store.delete([record.userId, record.timelineId]);
                store.put({ ...record, userId: scoped });
              }
            };
          }
        };
        req.onsuccess = () => {
          this.opened = req.result;
          // Another tab deleting / upgrading the database (sign-out wipe) must not be blocked by this connection.
          req.result.onversionchange = () => this.close();
          resolve(req.result);
        };
        req.onerror = () => resolve(null);
        req.onblocked = () => resolve(null);
      } catch {
        resolve(null);
      }
    });
    return this.db;
  }

  /**
   * Runs `task` after everything already queued for the timeline. With the database open and nothing
   * queued, `task` starts in THIS task (a page-hide handler needs the transaction begun before the page goes).
   */
  private chain(userId: string, timelineId: string, task: (db: IDBDatabase | null) => Promise<void>): Promise<void> {
    const ck = chainKey(userId, timelineId);
    const previous = this.chains.get(ck);
    let next: Promise<void>;
    if (!previous && this.opened) {
      next = task(this.opened).catch(() => undefined);
    } else {
      next = (previous ?? Promise.resolve()).then(() => this.open()).then(task).catch(() => undefined);
    }
    this.chains.set(ck, next);
    void next.then(() => {
      if (this.chains.get(ck) === next) this.chains.delete(ck);
    });
    return next;
  }

  /** Releases the IndexedDB connection (so a `deleteDatabase` is not blocked). A later call reopens it. */
  close(): void {
    const opened = this.opened;
    this.opened = null;
    this.db = null;
    try {
      opened?.close();
    } catch {
      // already closed
    }
  }

  /** Opens the database ahead of the first write, so a later write can start synchronously. */
  warm(): Promise<void> {
    return this.open().then(() => this.evictStale());
  }

  private isExpired(savedAt: number): boolean {
    return !Number.isFinite(savedAt) || this.now() - savedAt > JOURNAL_MAX_AGE_MS;
  }

  /**
   * Eviction on open: entries older than {@link JOURNAL_MAX_AGE_MS} go, and so do the oldest beyond
   * {@link JOURNAL_MAX_ENTRIES}, from both stores. Runs once per journal instance; best effort, never throws.
   */
  evictStale(): Promise<void> {
    this.evicted ??= this.doEvict().catch(() => undefined);
    return this.evicted;
  }

  private async doEvict(): Promise<void> {
    const seen = new Map<string, { userId: string; timelineId: string; savedAt: number }>();
    const note = (userId: string, timelineId: string, savedAt: number) => {
      const key = chainKey(userId, timelineId);
      const have = seen.get(key);
      if (!have || savedAt > have.savedAt) seen.set(key, { userId, timelineId, savedAt });
    };
    const storage = this.storage;
    if (storage?.keys) {
      for (const key of storage.keys(JOURNAL_PREFIX)) {
        try {
          const record = JSON.parse(storage.get(key) ?? 'null') as { kind?: string; entry?: Partial<JournalEntry>; timelineId?: string; savedAt?: number } | null;
          const e = record?.kind === 'entry' ? record.entry : record;
          const timelineId = e?.timelineId;
          const savedAt = e?.savedAt;
          const userId = record?.kind === 'entry' ? record.entry?.userId : key.slice(JOURNAL_PREFIX.length, key.length - (timelineId?.length ?? 0) - 1);
          if (typeof timelineId === 'string' && typeof savedAt === 'number' && typeof userId === 'string') note(userId, timelineId, savedAt);
          else storage.remove(key); // unreadable: nothing could ever restore it
        } catch {
          storage.remove(key);
        }
      }
    }
    try {
      const db = await this.open();
      if (db) {
        const all = await request(db.transaction(STORE_NAME, 'readonly').objectStore(STORE_NAME).getAll());
        for (const e of all as Partial<JournalEntry>[]) {
          if (typeof e.userId === 'string' && typeof e.timelineId === 'string') note(e.userId, e.timelineId, typeof e.savedAt === 'number' ? e.savedAt : 0);
        }
      }
    } catch {
      // IndexedDB unreachable: the localStorage pass above still counted
    }
    const live = [...seen.values()].filter((e) => !this.isExpired(e.savedAt)).sort((a, b) => b.savedAt - a.savedAt);
    const doomed = [...[...seen.values()].filter((e) => this.isExpired(e.savedAt)), ...live.slice(JOURNAL_MAX_ENTRIES)];
    await Promise.all(doomed.map((e) => this.clear(e.userId, e.timelineId)));
  }

  /**
   * Which kind of durability this browser gives: `'device'` when at least one store works, else
   * `'memory'`. Probes by really writing (a private window can expose `localStorage` and refuse the write).
   */
  async probe(): Promise<'device' | 'memory'> {
    if (this.writeFailed) return 'memory';
    if (this.storage) {
      const key = journalKey('__probe__', '__probe__');
      try {
        this.storage.set(key, '1');
        const ok = this.storage.get(key) === '1';
        this.storage.remove(key);
        if (ok) return 'device';
      } catch {
        // fall through to IndexedDB
      }
    }
    return (await this.open()) ? 'device' : 'memory';
  }

  /**
   * Writes the entry. The localStorage copy is written BEFORE this returns and the IndexedDB `put` is
   * started in the same task (inside the transaction opened synchronously when the database is already
   * open); the returned promise settles when IndexedDB has it.
   */
  write(input: JournalEntry): Promise<void> {
    const userId = input.userId || ANONYMOUS_SCOPE;
    const entry: JournalEntry = { ...input, userId };
    if (this.storage) {
      this.storage.remove(legacyJournalKey(entry.timelineId));
      const json = JSON.stringify(entry);
      const record: LocalRecord =
        json.length <= SYNC_POINTER_LIMIT ? { kind: 'entry', entry } : { kind: 'pointer', timelineId: entry.timelineId, savedAt: entry.savedAt, tabId: entry.tabId };
      this.storage.set(journalKey(userId, entry.timelineId), JSON.stringify(record));
    }
    return this.chain(userId, entry.timelineId, async (db) => {
      if (!db) return;
      try {
        const tx = db.transaction(STORE_NAME, 'readwrite');
        tx.objectStore(STORE_NAME).put(entry);
        await new Promise<void>((resolve, reject) => {
          tx.oncomplete = () => resolve();
          tx.onerror = () => reject(tx.error ?? new Error('IndexedDB write failed'));
          tx.onabort = () => reject(tx.error ?? new Error('IndexedDB write aborted'));
        });
        this.writeFailed = false;
      } catch (error) {
        // Not silent: the journal is not durable now, and the chip must say so.
        this.writeFailed = true;
        this.onWriteFailure?.();
        throw error;
      }
    });
  }

  /** The newest stored entry for a timeline, from either store; null when there is none. */
  async read(userId: string, timelineId: string): Promise<JournalEntry | null> {
    this.storage?.remove(legacyJournalKey(timelineId));
    let fromLocal: JournalEntry | null = null;
    if (this.storage) {
      try {
        const raw = this.storage.get(journalKey(userId, timelineId));
        if (raw) {
          const record = JSON.parse(raw) as LocalRecord;
          if (record.kind === 'entry' && isEntry(record.entry, userId, timelineId)) fromLocal = record.entry;
        }
      } catch {
        fromLocal = null;
      }
    }
    // Reads wait for the writes already queued, so a read right after a write sees it.
    await (this.chains.get(chainKey(userId, timelineId)) ?? Promise.resolve());
    let fromIdb: JournalEntry | null = null;
    try {
      const db = await this.open();
      if (db) {
        const stored = await request(db.transaction(STORE_NAME, 'readonly').objectStore(STORE_NAME).get([userId, timelineId]));
        if (isEntry(stored, userId, timelineId)) fromIdb = stored;
      }
    } catch {
      fromIdb = null;
    }
    const newest = fromLocal && fromIdb ? (fromLocal.savedAt >= fromIdb.savedAt ? fromLocal : fromIdb) : (fromLocal ?? fromIdb);
    // An expired entry is never offered: it is dropped here too (the open-time sweep may not have reached it yet).
    if (newest && this.isExpired(newest.savedAt)) {
      void this.clear(userId, timelineId);
      return null;
    }
    return newest;
  }

  /** Forgets the timeline's entry in both stores (the server confirmed the revision, or the user chose). */
  clear(userId: string, timelineId: string): Promise<void> {
    this.storage?.remove(journalKey(userId, timelineId));
    this.storage?.remove(legacyJournalKey(timelineId));
    return this.chain(userId, timelineId, async (db) => {
      if (!db) return;
      const tx = db.transaction(STORE_NAME, 'readwrite');
      tx.objectStore(STORE_NAME).delete([userId, timelineId]);
      await new Promise<void>((resolve, reject) => {
        tx.oncomplete = () => resolve();
        tx.onerror = () => reject(tx.error ?? new Error('IndexedDB delete failed'));
        tx.onabort = () => reject(tx.error ?? new Error('IndexedDB delete aborted'));
      });
    });
  }
}

/** A short random id for this tab (the journal's `tabId`, the presence channel's sender). */
export function newTabId(): string {
  const bytes = new Uint8Array(8);
  if (typeof crypto !== 'undefined' && typeof crypto.getRandomValues === 'function') crypto.getRandomValues(bytes);
  else for (let i = 0; i < bytes.length; i++) bytes[i] = Math.floor(Math.random() * 256);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}
