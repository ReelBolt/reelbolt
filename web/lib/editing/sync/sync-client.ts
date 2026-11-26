/**
 * The autosave / sync client.
 *
 * Watches an {@link EditorStore}; whenever the document is dirty it schedules a debounced flush that
 * sends ONE `PATCH` with `baseRevision` (the revision the store last saved at), the full post-edit
 * document, and the ops applied since the last save. The server answers with the new revision, the
 * store is told, and the pending ops are cleared.
 *
 * ## Local validation BEFORE commit — the rule this module exists for
 *
 * The server's `EditTimelineValidator` is the only thing the server trusts, and a document it
 * rejects is a `400` with no write. The client must never find that out the hard way: before every
 * flush the document is checked with the engine's own `assertInvariants` (ordering, overlap,
 * integer frames, ids) — a document that fails it is NOT sent, the save state becomes `invalid`, and
 * the user is told which invariant broke. Ownership of `AssetRef`s is the server's check and cannot
 * be done here (the client has no ownership map), so a `400` can still happen; it is surfaced as
 * `rejected` with the server's violations, and nothing has been written.
 *
 * ## Conflict handling
 *
 * A `409` means another writer advanced the revision: this tab is now stale. The client stops
 * saving, records the server's `currentRevision`, enters `conflict` and freezes the store
 * (`EditorStore.setReadOnly`) — every document write path refuses while the read-only banner shows.
 * It never retries a conflict (the retry would conflict again) and never force-writes. The user
 * reloads or opens a fresh copy; both are explicit.
 *
 * One exception: a request that failed with a network error or 5xx may still have landed. The client
 * remembers every such request (its document and op count) until the revision moves. When a later
 * request at the same base answers `409` with `currentRevision = base + 1`, the client reads the head
 * and compares it with the documents those uncertain requests SENT (not the current one — the user
 * may have edited during the backoff). On a match the save counts as done at `base + 1` for exactly
 * the ops that request carried, and whatever was edited since is sent next on the new base.
 *
 * ## One flush at a time
 *
 * There is never more than one PATCH in flight. A `flush()` while one is running returns THAT
 * promise and asks for a follow-up; the follow-up runs inside the same promise once the first
 * answers, against the revision it produced. Two requests never carry the same `baseRevision`.
 *
 * ## Retry
 *
 * Network/5xx failures retry with exponential backoff up to `maxRetries`, then settle in `error`
 * with the message; a later edit (or `retry()`, the chip's Retry button) re-arms the retry. Only a
 * DOCUMENT change schedules an autosave: playhead/selection ticks neither restart the debounce nor
 * cut a backoff short. `attachLifecycle` flushes on `pagehide` and `visibilitychange`→hidden with
 * `keepalive` (bodies within the shared 64 KiB quota) — while a normal PATCH is in flight, a "last
 * chance" keepalive PATCH of the newest document on the same base, even when nothing changed since
 * (see `sendLastChance`) — and `dispose()` (route
 * change) flushes before it stops.
 *
 * ## Reasons
 *
 * Every refusal carries `reasons`: plain sentences mapped from the server's answer (`[{path,
 * message}]` violations or `{message, errors}`) with paths turned into "Track 1, clip 2" and ids
 * and field names removed — what the status chip lists.
 *
 * ## Offline (P9)
 *
 * A network failure while the browser reports itself offline puts the client in `offline`: edits
 * keep landing in the store, and every change persists ONE queue entry (base revision, base document,
 * ops since, local document — `offline-queue.ts`) so a closed tab loses nothing. `reconnect()` (wired
 * to the window's `online` event by `attachLifecycle`) reconciles the entry against the server:
 * fast-forward when the server has not moved, a PROVEN rebase when it has and the two op logs
 * commute, otherwise `conflict` with the queue kept for download. `restoreQueued()` runs the same
 * reconciliation for an entry left by an earlier session. A queued op is never applied blind.
 *
 * ## Never lose an edit (R02.2), recover from a conflict (R02.1), plain failures (R02.3), view-only (R02.5)
 *
 * - The edit journal (`journal.ts`) keeps the unsaved document on this device (localStorage pointer +
 *   IndexedDB) while the PATCH has not landed: written shortly after an edit, synchronously on page hide,
 *   cleared when the server confirms. `checkRecovery()` offers a journal newer than the server back.
 * - A `401` is `authExpired`: nothing is lost, saving pauses, the app signs the user in IN PLACE and
 *   `resumeAfterSignIn()` flushes.
 * - A `409` first tries an ONLINE rebase through the same reconciler as the offline queue: edits that
 *   commute with the server's are merged silently (`mergedAt` is stamped for the toast); overlapping
 *   edits become a `conflict` with `rebaseReason`, and the user chooses `saveAsCopy()` (a new timeline
 *   holding the local document — nothing lost) or the latest (after downloading `getLocalEntry()`).
 * - Two tabs of this browser: `presence` (BroadcastChannel) makes the later tab `otherTab` (read-only)
 *   until the user takes over; the earlier one flushes and becomes `takenOver`.
 * - `canEdit: false` opens the document view-only: the store is frozen from the start and nothing is
 *   ever PATCHed or journalled.
 *
 * Fully injectable (api, timers, clock, journal) so the tests run it under fake timers with a fake API.
 */

import { ApiError } from '../../api/client';
import { assertInvariants, EditEngineError } from '../engine';
import { refusalSentence } from '../refusal';
import type { EditorStore } from '../store/editor-store';
import { browserQueueStorage, queueKey, readQueue, reconcileQueue, type QueueEntry, type QueueStorage } from './offline-queue';
import type { EditTimeline } from '../types';
import { getUserFromCookie } from '../../hooks/use-auth';
import { ANONYMOUS_SCOPE, migrateEditorStorageScope, onEditorSignOut, userIdFromEmail, userScopeFromEmail, wipeEditorLocalData, lastEditorSignOutAt } from './user-scope';
import { EditJournal, JOURNAL_VERSION, newTabId, type JournalEntry } from './journal';
import { TabPresence, type PresenceRole } from './tab-presence';
import {
  defaultSyncApi,
  type EditTimelineConflict,
  type EditTimelineSyncApi,
  type EditTimelineViolation,
  type WireOp,
} from './api';

export type SaveStatus =
  | 'saved'
  | 'dirty'
  | 'saving'
  | 'retrying'
  | 'invalid'
  | 'rejected'
  | 'conflict'
  | 'error'
  | 'offline'
  | 'reconnecting'
  /** R02.2: the session expired; edits are kept and the user signs in again in place. */
  | 'authExpired'
  /** R02.1: this tab was opened while the timeline is open in another tab of this browser. */
  | 'otherTab'
  /** R02.1: the user took the timeline over in another tab; this one is read-only. */
  | 'takenOver';

/** R02.3: which kind of failure an `error`/`rejected` state is, so the chip can say it in its own words. */
export type SaveProblem = 'serverError' | 'tooLarge' | 'noPermission' | 'deleted';

export interface SaveState {
  status: SaveStatus;
  /** The revision the server last confirmed. */
  revision: number;
  lastSavedAt: number | null;
  /** For `conflict`: the revision the server is at. */
  conflictRevision: number | null;
  /** For `rejected`: the server's violations. */
  violations: EditTimelineViolation[];
  /** For `invalid` / `error`: a human-readable reason. */
  message: string | null;
  /** For `invalid` / `rejected` / `error` / `retrying`: the reasons, as plain sentences for the chip. */
  reasons?: string[];
  /** For `retrying`: how many attempts have failed so far. */
  attempt: number;
  /** P9: ops waiting in the offline queue (0 when online). */
  queuedOps?: number;
  /** P9: a `conflict` that came from refusing to rebase queued offline edits (they are still stored). */
  offlineRefused?: boolean;
  /** R02.3: the failure class behind an `error`/`rejected` state. */
  problem?: SaveProblem | null;
  /** R02.5: the document was opened view-only (`canEdit: false`): never written, never journalled. */
  viewOnly?: boolean;
  /** R02.2: `'memory'` when neither IndexedDB nor localStorage works — edits live in this tab only. */
  durability?: 'device' | 'memory';
  /** R02.2: a journal from an earlier visit is newer than the server — offered as Restore / Discard. */
  recovery?: { savedAt: number } | null;
  /** Signed in again as a DIFFERENT account: the unsaved edits belong to `previous` and were not sent. */
  accountChanged?: { previous: string } | null;
  /** R02.1: stamped when an online rebase merged this tab's edits with another writer's (drives the toast). */
  mergedAt?: number | null;
  /** R02.1: for an overlapping `conflict`: why the edits could not be merged (plain words). */
  rebaseReason?: string | null;
  /** R02.2: stamped when the user asks (the chip's "Sign in") to see the sign-in dialog again. */
  signInRequestedAt?: number | null;
}

/** Whether the store is frozen for this save state (the conflict banner, another tab, view-only). */
export function isReadOnlySave(state: SaveState): boolean {
  return state.viewOnly === true || state.status === 'conflict' || state.status === 'otherTab' || state.status === 'takenOver';
}

export interface SyncClientOptions {
  store: EditorStore;
  /** Subscribes to "another tab signed out" (default: the BroadcastChannel / storage-event pair in user-scope). Returns the unsubscribe. */
  onSignOut?: (handler: () => void) => () => void;
  /** Wipes this device's journal / queue entries when a sign-out is heard (default `wipeEditorLocalData`, without re-announcing). */
  wipeLocalData?: () => Promise<void>;
  api?: EditTimelineSyncApi;
  debounceMs?: number;
  maxRetries?: number;
  retryBaseMs?: number;
  /** Injectable timers, for tests. Default: the globals. */
  setTimeout?: (fn: () => void, ms: number) => unknown;
  clearTimeout?: (handle: unknown) => void;
  now?: () => number;
  /** P9: where the offline queue persists (default `localStorage`; `null` keeps it in memory only). */
  queueStorage?: QueueStorage | null;
  /** P9: whether the browser believes it is online (default `navigator.onLine`). */
  isOnline?: () => boolean;
  /** R02.5: `false` opens the timeline view-only (the server said the member cannot edit). Default `true`. */
  canEdit?: boolean;
  /** R02.2: the edit journal (default: the browser's; `null` = none). */
  journal?: EditJournal | null;
  /** A different account signed in while nothing was pending: the app rebuilds the editor for the new identity. */
  onIdentityChanged?: () => void;
  /** R02.2: this tab's id, recorded in journal entries and used by the presence channel. */
  tabId?: string;
  /** R02.1: watch for the same timeline open in another tab of this browser (BroadcastChannel). Default off. */
  presence?: boolean;
  /** Who is signed in now (default: the user cookie's email). Read at construction and again after an in-place sign-in. */
  userId?: () => string;
  /** R02.2: delay between an edit and its journal write, ms. Default 250. */
  journalDelayMs?: number;
}

export type SaveStateListener = (state: SaveState) => void;

/** What one PATCH carried — enough to recognise it later on the server and to mark exactly it saved. */
interface SentRequest {
  baseRevision: number;
  doc: EditTimeline;
  /** How many of the store's pending ops (from the front) the request carried. */
  opCount: number;
  /** How many non-engine ops (`attachOps`) it carried — the front of `extraOps` after a failure. */
  extraCount: number;
}

export class SyncClient {
  private readonly store: EditorStore;
  private readonly api: EditTimelineSyncApi;
  private readonly debounceMs: number;
  private readonly maxRetries: number;
  private readonly retryBaseMs: number;
  private readonly setTimer: (fn: () => void, ms: number) => unknown;
  private readonly clearTimer: (handle: unknown) => void;
  private readonly now: () => number;

  private state: SaveState;
  private readonly listeners = new Set<SaveStateListener>();
  private unsubscribeStore: (() => void) | null = null;
  private debounceHandle: unknown = null;
  private retryHandle: unknown = null;
  private inFlight: Promise<void> | null = null;
  private disposed = false;
  private extraOps: WireOp[] = [];
  private detachLifecycle: (() => void) | null = null;
  private readonly queueStorage: QueueStorage | null;
  private readonly isOnline: () => boolean;
  /** The document as of the last confirmed revision — the base an offline queue is reconciled from. */
  private savedDoc: EditTimeline;
  private memoryQueue: QueueEntry | null = null;
  /** The document reference the last store notification carried: only a change of it is an edit. */
  private lastSeenDoc: EditTimeline;
  /** A flush was asked for while one was in flight: run again inside the same promise. */
  private followUp = false;
  /** A flush in this run was asked to outlive the page. */
  private keepaliveRequested = false;
  /** Requests at the current base that failed with network/5xx and may have landed (the 409 exception). */
  private uncertainSends: SentRequest[] = [];
  /** The PATCH on the wire right now, and the non-engine ops it carried. */
  private inFlightRequest: (SentRequest & { extras: WireOp[]; keepalive: boolean }) | null = null;
  /** The keepalive "last chance" PATCH sent on hide while another request was in flight. */
  private lastChance: Promise<{ sent: SentRequest; result: { ok: true; revision: number } | { ok: false; error: unknown } }> | null = null;
  private readonly journal: EditJournal | null;
  private readonly tabId: string;
  private readonly currentUser: () => string;
  private readonly onIdentityChanged: (() => void) | null;
  /** The opaque scope this session (and every journal / queue entry it writes) belongs to. */
  private readonly sessionUser: string;
  /** The human-readable identity the "you signed in as…" dialog shows. In memory only; never a key. */
  private readonly sessionIdentity: string;
  private readonly viewOnly: boolean;
  private readonly wantPresence: boolean;
  private readonly journalDelayMs: number;
  private journalHandle: unknown = null;
  /** Another tab (or this one) signed out: nothing is journalled or queued from now on. */
  private signedOut = false;
  /** When this session began (ms): a deliberate sign-out recorded at or after it ends journalling for this tab. */
  private readonly sessionStartedAt = Date.now();
  private unsubscribeSignOut: (() => void) | null = null;
  private wipeLocalData: () => Promise<void> = () => wipeEditorLocalData({ broadcast: false });
  private presence: TabPresence | null = null;
  /** The journal entry offered for recovery (kept until the user chooses). */
  private recoveryEntry: JournalEntry | null = null;

  constructor(options: SyncClientOptions) {
    // Before any journal / queue read or write: move keys from the old email-form to the scoped form.
    migrateEditorStorageScope(getUserFromCookie()?.email);
    this.store = options.store;
    this.api = options.api ?? defaultSyncApi;
    this.debounceMs = options.debounceMs ?? 500;
    this.maxRetries = options.maxRetries ?? 5;
    this.retryBaseMs = options.retryBaseMs ?? 1000;
    this.setTimer = options.setTimeout ?? ((fn, ms) => globalThis.setTimeout(fn, ms));
    this.clearTimer = options.clearTimeout ?? ((handle) => globalThis.clearTimeout(handle as number));
    this.now = options.now ?? (() => Date.now());
    this.queueStorage = options.queueStorage === undefined ? browserQueueStorage : options.queueStorage;
    this.isOnline = options.isOnline ?? (() => (typeof navigator === 'undefined' ? true : navigator.onLine !== false));
    this.savedDoc = this.store.getState().doc;
    this.lastSeenDoc = this.savedDoc;
    this.viewOnly = options.canEdit === false;
    this.tabId = options.tabId ?? newTabId();
    this.currentUser = options.userId ?? (() => userScopeFromEmail(getUserFromCookie()?.email));
    this.sessionUser = this.currentUser();
    // Storage and comparisons use the scope; the dialog still names the account, so keep the raw id in memory.
    this.sessionIdentity = options.userId ? this.sessionUser : userIdFromEmail(getUserFromCookie()?.email);
    this.onIdentityChanged = options.onIdentityChanged ?? null;
    this.wantPresence = options.presence === true;
    this.journalDelayMs = options.journalDelayMs ?? 250;
    this.journal = this.viewOnly ? null : options.journal === undefined ? new EditJournal() : options.journal;
    if (this.journal) {
      // A failed put (quota / blocked) is no longer silent: the chip says edits are held in memory only.
      this.journal.onWriteFailure = () => {
        if (!this.disposed) this.setState({ durability: 'memory' });
      };
    }

    this.state = {
      status: 'saved',
      revision: this.store.getState().revision,
      lastSavedAt: null,
      conflictRevision: null,
      violations: [],
      message: null,
      attempt: 0,
      reasons: [],
      queuedOps: 0,
      offlineRefused: false,
      problem: null,
      viewOnly: this.viewOnly,
      recovery: null,
      mergedAt: null,
      rebaseReason: null,
    };
    // R02.5: a view-only document is frozen from the start — no edit can be made, so none can be sent.
    if (this.viewOnly) this.store.setReadOnly(true);

    this.unsubscribeStore = this.store.subscribe(() => this.onStoreChange());

    if (options.wipeLocalData) this.wipeLocalData = options.wipeLocalData;
    this.unsubscribeSignOut = (options.onSignOut ?? onEditorSignOut)(() => this.handleSignOut());

    // R02.2: tell the truth about durability. `warm()` also opens IndexedDB so a hide-time write can start at once.
    if (this.journal) {
      const journal = this.journal;
      void journal.warm();
      void journal.probe().then((durability) => {
        if (!this.disposed && durability === 'memory') this.setState({ durability });
      });
    }
  }

  // ── observation ───────────────────────────────────────────────────────────

  getState(): SaveState {
    return this.state;
  }

  subscribe(listener: SaveStateListener): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  private setState(patch: Partial<SaveState>): void {
    this.state = { ...this.state, ...patch };
    // The read-only banner and the store's write guard are one fact: a conflicted, superseded or view-only tab cannot edit.
    this.store.setReadOnly(isReadOnlySave(this.state));
    for (const listener of this.listeners) listener(this.state);
  }

  /**
   * THE guard every send path goes through (flush, retry, keepalive / pagehide / unload, reconnect, queue and
   * journal replay, save-as-copy, resume): true only when the account in the browser right now is the one this
   * session was opened as. A different account sets `accountChanged`; an unreadable / signed-out account
   * ('anonymous', even on both sides) never matches and asks the user to sign in again. Either way NOTHING is sent.
   */
  private mayTalk(): boolean {
    if (this.viewOnly) return true; // view-only never sends anything anyway
    const now = this.currentUser();
    if (now !== ANONYMOUS_SCOPE && this.sessionUser !== ANONYMOUS_SCOPE && now === this.sessionUser) return true;
    if (this.retryHandle !== null) {
      this.clearTimer(this.retryHandle);
      this.retryHandle = null;
    }
    const different = now !== ANONYMOUS_SCOPE && now !== this.sessionUser;
    const patch: Partial<SaveState> = { status: 'authExpired', message: SIGN_IN_AGAIN, attempt: 0, reasons: [SIGN_IN_AGAIN], problem: null };
    if (different || this.sessionUser === ANONYMOUS_SCOPE) patch.accountChanged = { previous: this.sessionIdentity };
    if (this.state.status !== 'authExpired') patch.signInRequestedAt = this.now();
    this.setState(patch);
    return false;
  }

  // ── scheduling ────────────────────────────────────────────────────────────

  private onStoreChange(): void {
    if (this.disposed) return;
    const storeState = this.store.getState();
    // View-state ticks (playhead, selection, zoom) and our own markSaved/setReadOnly commits keep the
    // same document: they must neither restart the debounce (autosave starved during playback) nor
    // cut a retry backoff short (a failing PATCH re-sent on every click).
    const docChanged = storeState.doc !== this.lastSeenDoc;
    this.lastSeenDoc = storeState.doc;
    if (!docChanged) return;
    if (!storeState.dirty) return;
    if (this.viewOnly) return; // R02.5: nothing is ever sent or journalled
    this.scheduleJournal();
    if (this.state.status === 'conflict' || this.state.status === 'otherTab' || this.state.status === 'takenOver') return; // stale tab: never auto-save again
    if (this.state.status === 'authExpired') return; // kept in the journal until the user signs in again
    if (this.state.status === 'offline' || this.state.status === 'reconnecting') {
      // Offline: keep the persisted queue current; nothing is sent until reconnect() reconciles.
      this.persistQueue();
      return;
    }

    if (this.state.status !== 'saving' && this.state.status !== 'retrying') {
      this.setState({ status: 'dirty' });
    }
    this.scheduleFlush(this.debounceMs);
  }

  private scheduleFlush(ms: number): void {
    if (this.debounceHandle !== null) this.clearTimer(this.debounceHandle);
    this.debounceHandle = null;
    if (this.disposed) return;
    this.debounceHandle = this.setTimer(() => {
      this.debounceHandle = null;
      void this.flush();
    }, ms);
  }

  /** Queues a non-engine op (today: `restore`) to ride along with the next PATCH. */
  attachOps(ops: WireOp[]): void {
    if (this.viewOnly) return;
    this.extraOps.push(...ops);
  }

  /**
   * Flushes now (cancelling any pending debounce). Resolves when the attempt has finished — saved,
   * refused, conflicted or scheduled for retry. While a flush is in flight every caller gets THAT
   * promise; if the document moved on meanwhile, the follow-up PATCH runs inside it.
   */
  flush(): Promise<void> {
    if (this.disposed) return Promise.resolve();
    return this.runFlush(false);
  }

  /** The chip's Retry: re-sends now after a refusal or an error, or cuts a retry backoff short. */
  retry(): Promise<void> {
    if (this.disposed) return Promise.resolve();
    const status = this.state.status;
    if (status !== 'rejected' && status !== 'error' && status !== 'invalid' && status !== 'retrying') return Promise.resolve();
    if (this.retryHandle !== null) this.clearTimer(this.retryHandle);
    this.retryHandle = null;
    this.setState({ status: 'dirty', violations: [], reasons: [], message: null });
    return this.runFlush(false);
  }

  private canSend(): boolean {
    const status = this.state.status;
    if (this.viewOnly) return false;
    if (status === 'conflict' || status === 'offline' || status === 'reconnecting' || status === 'authExpired' || status === 'otherTab' || status === 'takenOver') return false;
    return this.store.getState().dirty;
  }

  private runFlush(keepalive: boolean): Promise<void> {
    if (this.debounceHandle !== null) {
      this.clearTimer(this.debounceHandle);
      this.debounceHandle = null;
    }
    if (keepalive) this.keepaliveRequested = true;
    if (this.inFlight) {
      this.followUp = true;
      return this.inFlight;
    }
    if (!this.canSend()) {
      this.keepaliveRequested = false;
      return Promise.resolve();
    }
    const run = async (): Promise<void> => {
      try {
        do {
          this.followUp = false;
          if (this.debounceHandle !== null) {
            this.clearTimer(this.debounceHandle);
            this.debounceHandle = null;
          }
          await this.attempt(this.keepaliveRequested);
          await this.settleLastChance();
        } while (this.followUp && this.state.status === 'dirty' && this.canSend());
      } finally {
        this.inFlight = null;
        this.followUp = false;
        this.keepaliveRequested = false;
      }
    };
    this.inFlight = run();
    return this.inFlight;
  }

  private async attempt(keepalive: boolean): Promise<void> {
    if (!this.mayTalk()) return;
    if (this.retryHandle !== null) {
      this.clearTimer(this.retryHandle);
      this.retryHandle = null;
    }
    const storeState = this.store.getState();
    const doc = storeState.doc;

    // Local validation BEFORE commit: a document the engine's invariants refuse is never sent.
    try {
      assertInvariants(doc);
    } catch (error) {
      const message = error instanceof EditEngineError ? error.message : String(error);
      this.setState({ status: 'invalid', message, violations: [], reasons: [refusalSentence(error, doc)], problem: null });
      return;
    }

    const baseRevision = storeState.revision;
    const ops: WireOp[] = [...storeState.pendingOps, ...this.extraOps];
    const extraSent = this.extraOps;
    this.extraOps = [];

    this.setState({ status: this.state.attempt > 0 ? 'retrying' : 'saving', message: null });

    const sent: SentRequest = { baseRevision, doc, opCount: storeState.pendingOps.length, extraCount: extraSent.length };
    const request = { baseRevision, ops, document: doc };
    this.inFlightRequest = { ...sent, extras: extraSent, keepalive };
    try {
      const response = keepalive
        ? await this.api.patchEditTimeline(storeState.projectId, storeState.timelineId, request, { keepalive: true })
        : await this.api.patchEditTimeline(storeState.projectId, storeState.timelineId, request);
      this.inFlightRequest = null;
      this.onSaved(response.revision, sent.opCount, doc);
    } catch (error) {
      this.inFlightRequest = null;
      this.extraOps = [...extraSent, ...this.extraOps];
      // A last-chance request sent beside this one (same base) may be what moved the revision.
      if (this.lastChance && isConflictAt(error, baseRevision + 1)) {
        await this.settleLastChance();
        if (this.store.getState().revision !== baseRevision) return;
      }
      // An earlier request that failed in transit may have landed: this one then meets its revision.
      if (isConflictAt(error, baseRevision + 1) && this.uncertainSends.some((u) => u.baseRevision === baseRevision)) {
        const landed = await this.landedEarlier(baseRevision);
        if (landed === 'unknown') {
          // The head could not be read: whether our request landed is unknown, and a transient blip
          // must never become a read-only conflict. Keep the uncertain requests and try again.
          this.retryLater(UNCONFIRMED_SAVE, [UNCONFIRMED_SAVE]);
          return;
        }
        if (landed) {
          this.extraOps = this.extraOps.slice(landed.extraCount);
          this.onSaved(baseRevision + 1, landed.opCount, landed.doc);
          return;
        }
      }
      if (isTransient(error)) this.uncertainSends.push(sent);
      // R02.1: another writer moved the revision — merge online when the edits commute, else ask the user.
      if (error instanceof ApiError && error.status === 409 && !this.disposed) {
        const body = error.body as Partial<EditTimelineConflict> | undefined;
        if (await this.rebaseAfterConflict(typeof body?.currentRevision === 'number' ? body.currentRevision : null)) return;
      }
      this.handleFailure(error);
    }
  }

  private onSaved(revision: number, sentOpCount: number, doc: EditTimeline): void {
    this.uncertainSends = [];
    if (this.retryHandle !== null) this.clearTimer(this.retryHandle); // a last-chance save beat a retry
    this.retryHandle = null;
    // Only the ops this request carried are cleared; anything that arrived while it was in flight
    // stays pending (and the store stays dirty) so the next flush sends exactly that against the
    // revision the server just produced. Undo history is never touched by a save.
    this.store.markSaved(revision, sentOpCount);
    this.savedDoc = doc;
    this.clearQueue();
    if (!this.store.getState().dirty) this.clearJournal();
    if (this.store.getState().dirty) {
      this.setState({ status: 'dirty', revision, lastSavedAt: this.now(), attempt: 0, reasons: [], violations: [], message: null });
      this.scheduleFlush(this.debounceMs);
      return;
    }

    this.setState({
      status: 'saved',
      revision,
      lastSavedAt: this.now(),
      attempt: 0,
      violations: [],
      reasons: [],
      message: null,
      conflictRevision: null,
      problem: null,
      rebaseReason: null,
    });
  }

  /**
   * The uncertain request at `baseRevision` the server's head (at `baseRevision + 1`) holds — newest
   * first — null when the head is elsewhere or holds a document none of them sent, `'unknown'` when
   * the head could not be read.
   */
  private async landedEarlier(baseRevision: number): Promise<SentRequest | null | 'unknown'> {
    let head: { revision: number; document: EditTimeline };
    try {
      const { projectId, timelineId } = this.store.getState();
      head = await this.api.getEditTimeline(projectId, timelineId);
    } catch {
      return 'unknown';
    }
    if (head.revision !== baseRevision + 1) return null;
    for (let i = this.uncertainSends.length - 1; i >= 0; i--) {
      const candidate = this.uncertainSends[i];
      if (candidate.baseRevision === baseRevision && sameDocument(head.document, candidate.doc)) return candidate;
    }
    return null;
  }

  private handleFailure(error: unknown): void {
    if (!isTransient(error)) this.uncertainSends = [];
    const reasons = reasonsFor(error);
    if (error instanceof ApiError) {
      if (error.status === 401) {
        // R02.2: the session expired. Nothing is lost: the edit stays in the store and the journal,
        // saving pauses, and the app signs the user in again in place (`resumeAfterSignIn`).
        this.writeJournal();
        this.setState({ status: 'authExpired', message: SIGN_IN_AGAIN, attempt: 0, reasons: [SIGN_IN_AGAIN], problem: null, signInRequestedAt: this.now() });
        return;
      }
      if (error.status === 409) {
        const body = error.body as Partial<EditTimelineConflict> | undefined;
        this.writeJournal();
        this.setState({
          status: 'conflict',
          conflictRevision: typeof body?.currentRevision === 'number' ? body.currentRevision : null,
          message: 'This timeline was changed elsewhere. Your edits here are not saved.',
          attempt: 0,
          reasons,
          problem: null,
        });
        return;
      }
      if (error.status === 400) {
        const body = error.body;
        const violations = Array.isArray(body)
          ? (body as unknown[]).filter(isViolation)
          : [];
        this.writeJournal();
        this.setState({
          status: 'rejected',
          violations,
          message: violations.length > 0 ? 'The server rejected the document.' : error.message,
          attempt: 0,
          reasons,
          problem: null,
        });
        return;
      }
      if (error.status === 413) {
        this.writeJournal();
        this.setState({ status: 'rejected', violations: [], message: 'The document is too large to save.', attempt: 0, reasons, problem: 'tooLarge' });
        return;
      }
      if (error.status === 404 || error.status === 403) {
        this.writeJournal();
        this.setState({ status: 'error', message: error.message, attempt: 0, reasons, problem: error.status === 404 ? 'deleted' : 'noPermission' });
        return;
      }
    }
    // P9: the browser is offline — queue instead of burning retries against a dead network.
    if (!(error instanceof ApiError) && !this.isOnline()) {
      this.enterOffline();
      return;
    }

    // Network / 5xx: retry with backoff.
    this.retryLater(messageOf(error), reasons);
  }

  /** Schedules the next attempt with exponential backoff, or settles in `error` after `maxRetries`. */
  private retryLater(message: string, reasons: string[]): void {
    const attempt = this.state.attempt + 1;
    if (attempt > this.maxRetries) {
      this.writeJournal();
      this.setState({ status: 'error', message, attempt: 0, reasons, problem: 'serverError' });
      return;
    }
    const delay = this.retryBaseMs * 2 ** (attempt - 1);
    this.setState({ status: 'retrying', attempt, message, reasons });
    if (this.retryHandle !== null) this.clearTimer(this.retryHandle);
    this.retryHandle = null;
    if (this.disposed) return;
    this.retryHandle = this.setTimer(() => {
      this.retryHandle = null;
      void this.flush();
    }, delay);
  }

  /** Clears a `conflict`/`error`/`rejected` state after the caller reloaded the document. */
  reset(revision: number): void {
    this.savedDoc = this.store.getState().doc;
    this.lastSeenDoc = this.savedDoc;
    this.uncertainSends = [];
    this.setState({
      status: 'saved',
      revision,
      conflictRevision: null,
      violations: [],
      reasons: [],
      message: null,
      attempt: 0,
      offlineRefused: false,
      problem: null,
      rebaseReason: null,
    });
  }

  // ── offline queue (P9) ────────────────────────────────────────────────────

  /** The queued offline edits, if any (for a "download my edits" escape hatch after a refusal). */
  getQueuedEntry(): QueueEntry | null {
    const state = this.store.getState();
    return (this.queueStorage ? readQueue(this.queueStorage, this.sessionUser, state.timelineId) : null) ?? this.memoryQueue;
  }

  /** Discards the stored queue (after the user downloaded it or chose to drop it). */
  discardQueue(): void {
    this.clearQueue();
    this.clearJournal();
    this.setState({ queuedOps: 0 });
  }

  private enterOffline(): void {
    this.persistQueue();
    this.setState({ status: 'offline', message: 'You are offline. Edits are kept on this device and saved when the connection returns.', attempt: 0 });
  }

  private persistQueue(): void {
    if (!this.mayPersist()) return;
    const state = this.store.getState();
    const entry: QueueEntry = {
      version: 1,
      projectId: state.projectId,
      timelineId: state.timelineId,
      baseRevision: state.revision,
      baseDoc: this.savedDoc,
      localDoc: state.doc,
      ops: [...state.pendingOps, ...this.extraOps],
      savedAt: this.now(),
      userId: this.sessionUser,
    };
    this.memoryQueue = entry;
    this.queueStorage?.set(queueKey(this.sessionUser, state.timelineId), JSON.stringify(entry));
    if (this.state.queuedOps !== entry.ops.length) this.setState({ queuedOps: entry.ops.length });
  }

  private clearQueue(): void {
    this.memoryQueue = null;
    this.queueStorage?.remove(queueKey(this.sessionUser, this.store.getState().timelineId));
    if (this.state.queuedOps !== 0) this.setState({ queuedOps: 0 });
  }

  /** The server's op logs for revisions (base, current], oldest first — or null when they cannot all be read. */
  private async serverOpsSince(baseRevision: number, currentRevision: number): Promise<WireOp[] | null> {
    const read = this.api.getEditTimelineHistory;
    if (!read) return null;
    const { projectId, timelineId } = this.store.getState();
    const byRevision = new Map<number, WireOp[]>();
    let before: number | undefined;
    for (let page = 0; page < 10; page++) {
      const history = await read(projectId, timelineId, { limit: 200, before });
      for (const entry of history.entries) {
        if (entry.revision > baseRevision && entry.revision <= currentRevision) byRevision.set(entry.revision, Array.isArray(entry.ops) ? (entry.ops as WireOp[]) : []);
      }
      const oldest = Math.min(...history.entries.map((e) => e.revision));
      if (history.entries.length === 0 || oldest <= baseRevision + 1) break;
      before = oldest;
    }
    const ops: WireOp[] = [];
    for (let revision = baseRevision + 1; revision <= currentRevision; revision++) {
      const logged = byRevision.get(revision);
      if (!logged) return null; // a revision with no op row (e.g. the seed) cannot be accounted for
      ops.push(...logged);
    }
    return ops;
  }

  /**
   * Reconnect: reconcile the queue against the server and either send it, send it rebased, or refuse
   * (read-only, queue kept). Safe to call when not offline (no-op).
   */
  async reconnect(): Promise<void> {
    if (this.disposed || this.state.status !== 'offline') return;
    const entry = this.getQueuedEntry();
    if (!entry) {
      this.setState({ status: this.store.getState().dirty ? 'dirty' : 'saved', message: null });
      return;
    }
    await this.reconcile(entry, 'offline');
  }

  /**
   * Startup: a queue left by an earlier session for this timeline is reconciled against the document
   * the page just loaded (the store's doc and revision are the server's). Returns what happened.
   */
  async restoreQueued(): Promise<'none' | 'sent' | 'rebased' | 'refused'> {
    if (this.viewOnly) return 'none';
    const entry = this.queueStorage ? readQueue(this.queueStorage, this.sessionUser, this.store.getState().timelineId) : null;
    if (!entry) return 'none';
    if (entry.userId !== this.sessionUser) {
      // Written by another user (or by an older version that did not say): never sent, deleted.
      this.queueStorage?.remove(queueKey(this.sessionUser, this.store.getState().timelineId));
      return 'none';
    }
    this.memoryQueue = entry;
    this.savedDoc = this.store.getState().doc;
    return this.reconcile(entry, 'offline');
  }

  /**
   * Reconciles `entry` against the server. `mode` says who asked:
   *   - `offline` / `recovered`: sends the result itself (a flush) once it is applied;
   *   - `conflict`: called from inside a flush after a `409` — it must NOT flush (that would await the
   *     flush it runs in); it queues a follow-up instead, and an overlap becomes a `conflict` the user resolves.
   * `rebuild` re-reads the store's current edits: whatever the user changed while the server was being read
   * is folded in (the reconciliation is pure and cheap, so it simply runs again on the same server data).
   */
  private async reconcile(entry: QueueEntry, mode: 'offline' | 'recovered' | 'conflict', rebuild?: () => QueueEntry): Promise<'sent' | 'rebased' | 'refused'> {
    if (!this.mayTalk()) return 'refused';
    if (mode === 'conflict') this.setState({ status: 'saving', message: null });
    else this.setState({ status: 'reconnecting', message: 'Reconnecting — checking the timeline for changes made elsewhere…', queuedOps: entry.ops.length });
    const { projectId, timelineId } = this.store.getState();
    let serverRevision: number;
    let serverDoc: EditTimeline;
    try {
      const current = await this.api.getEditTimeline(projectId, timelineId);
      serverRevision = current.revision;
      serverDoc = current.document;
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        this.handleFailure(error);
        return 'refused';
      }
      if (mode === 'conflict') {
        this.handleFailure(error);
        return 'refused';
      }
      if (!(error instanceof ApiError)) {
        this.setState({ status: 'offline', message: 'Still offline. Edits are kept on this device.' });
        return 'refused';
      }
      this.setState({ status: 'error', message: messageOf(error), problem: null });
      return 'refused';
    }

    let serverOps: WireOp[] | null = null;
    if (serverRevision !== entry.baseRevision) {
      try {
        serverOps = await this.serverOpsSince(entry.baseRevision, serverRevision);
      } catch {
        serverOps = null;
      }
    }

    // Edits made while the server was being read are part of what is rebased, not lost to it.
    if (rebuild && this.store.getState().doc !== entry.localDoc) entry = rebuild();

    const outcome = reconcileQueue(entry, { revision: serverRevision, doc: serverDoc }, serverOps);
    if (outcome.kind === 'refused' || (mode === 'conflict' && outcome.kind === 'fastForward')) {
      const reason = outcome.kind === 'refused' ? outcome.reason : 'the server did not accept the save, yet shows nothing new';
      this.writeJournal();
      if (mode === 'conflict') {
        this.setState({
          status: 'conflict',
          conflictRevision: serverRevision,
          offlineRefused: false,
          rebaseReason: reason,
          message: 'Your edits overlap with changes made elsewhere.',
          attempt: 0,
        });
        return 'refused';
      }
      this.setState({
        status: 'conflict',
        conflictRevision: serverRevision,
        offlineRefused: true,
        message: `Your offline edits were not applied: ${outcome.kind === 'refused' ? outcome.reason : reason}. They are kept on this device — download them, then reload.`,
      });
      return 'refused';
    }

    if (outcome.kind === 'fastForward') {
      this.store.replaceDocument(entry.localDoc, entry.baseRevision, { markDirty: entry.ops.length > 0, pendingOps: entry.ops.filter((o): o is Exclude<WireOp, { type: 'restore' }> => o.type !== 'restore') });
      this.extraOps = entry.ops.filter((o) => o.type === 'restore');
      this.savedDoc = entry.baseDoc;
    } else {
      this.store.replaceDocument(outcome.doc, serverRevision, { markDirty: true, pendingOps: outcome.ops });
      this.extraOps = [];
      this.savedDoc = serverDoc;
    }
    this.lastSeenDoc = this.store.getState().doc;
    this.setState({
      status: 'dirty',
      revision: this.store.getState().revision,
      message: null,
      offlineRefused: false,
      rebaseReason: null,
      mergedAt: outcome.kind === 'rebased' && mode !== 'offline' ? this.now() : this.state.mergedAt,
    });
    if (mode === 'conflict') {
      this.followUp = true;
      return 'rebased';
    }
    this.writeJournal();
    await this.flush();
    return outcome.kind === 'fastForward' ? 'sent' : 'rebased';
  }

  /** The store's unsaved edits as a reconcilable entry (also what "download my edits" writes). */
  private currentEntry(): JournalEntry {
    const state = this.store.getState();
    return {
      version: JOURNAL_VERSION,
      projectId: state.projectId,
      timelineId: state.timelineId,
      baseRevision: state.revision,
      baseDoc: this.savedDoc,
      localDoc: state.doc,
      ops: [...state.pendingOps, ...this.extraOps],
      savedAt: this.now(),
      tabId: this.tabId,
      userId: this.sessionUser,
    };
  }

  /** The unsaved edits of this tab as a file-ready entry (the "recovery file" before taking the latest). */
  getLocalEntry(): QueueEntry & { tabId?: string } {
    return { ...asQueueEntry(this.currentEntry()), tabId: this.tabId };
  }

  /** `true` when the 409 was fully handled here (merged and queued a follow-up, or became an overlap `conflict`). */
  private async rebaseAfterConflict(currentRevision: number | null): Promise<boolean> {
    if (currentRevision !== null && currentRevision <= this.store.getState().revision) return false;
    // Whatever happened (merged, an overlap the user must resolve, a 401, a retry), the state says it.
    await this.reconcile(this.getQueuedEntryFromStore(), 'conflict', () => this.getQueuedEntryFromStore());
    return true;
  }

  private getQueuedEntryFromStore(): QueueEntry {
    return asQueueEntry(this.currentEntry());
  }

  // ── R02.2 the journal ─────────────────────────────────────────────────────

  /**
   * Another tab of this browser signed out: this tab stops journalling and queueing (a late write would put a
   * signed-out user's unsaved document back on the device) and wipes the per-user entries. The edits stay in
   * memory behind the sign-in banner.
   */
  private handleSignOut(): void {
    if (this.disposed || this.signedOut) return;
    this.signedOut = true;
    if (this.journalHandle !== null) {
      this.clearTimer(this.journalHandle);
      this.journalHandle = null;
    }
    this.journal?.close(); // this tab's own connection would block the database delete
    void this.wipeLocalData().catch(() => undefined);
    if (!this.viewOnly) this.setState({ status: 'authExpired', message: SIGN_IN_AGAIN, attempt: 0, reasons: [SIGN_IN_AGAIN], problem: null, signInRequestedAt: this.now() });
  }

  /** The journal / offline queue may be written only for the account this session was opened as, and never after a sign-out. */
  private mayPersist(): boolean {
    if (this.signedOut) return false;
    // Durable cross-tab fact: a deliberate sign-out after this session began (even one this tab never heard).
    if (lastEditorSignOutAt() >= this.sessionStartedAt) {
      this.signedOut = true;
      return false;
    }
    const now = this.currentUser();
    return now === ANONYMOUS_SCOPE || now === this.sessionUser; // an expired session keeps its journal; ANOTHER account never writes into it
  }

  private scheduleJournal(): void {
    if (!this.mayPersist()) return;
    if (!this.journal || this.disposed) return;
    if (this.journalHandle !== null) return; // a write is already due: it will carry the newest document
    this.journalHandle = this.setTimer(() => {
      this.journalHandle = null;
      this.writeJournal();
    }, this.journalDelayMs);
  }

  /** Writes the unsaved document to the journal NOW (the localStorage copy is synchronous). */
  private writeJournal(): void {
    if (!this.journal || this.viewOnly || !this.mayPersist()) return;
    if (this.journalHandle !== null) {
      this.clearTimer(this.journalHandle);
      this.journalHandle = null;
    }
    if (!this.store.getState().dirty && this.extraOps.length === 0) return;
    void this.journal.write(this.currentEntry()).catch(() => undefined);
  }

  private clearJournal(): void {
    if (!this.journal) return;
    if (this.journalHandle !== null) {
      this.clearTimer(this.journalHandle);
      this.journalHandle = null;
    }
    void this.journal.clear(this.sessionUser, this.store.getState().timelineId).catch(() => undefined);
  }

  /**
   * On load: is there a journal from an earlier visit that holds edits the server does not? When it does,
   * `state.recovery` is set and the app offers Restore / Discard; an entry the server already reflects
   * (the save landed just before the tab died) is dropped silently. Call after `restoreQueued()`.
   */
  async checkRecovery(): Promise<'none' | 'offered'> {
    if (!this.journal || this.viewOnly || this.disposed) return 'none';
    const { timelineId, doc, revision } = this.store.getState();
    const entry = await this.journal.read(this.sessionUser, timelineId);
    if (this.disposed || !entry) return 'none';
    if (entry.userId !== this.sessionUser) {
      void this.journal.clear(this.sessionUser, timelineId).catch(() => undefined);
      return 'none';
    }
    if (this.store.getState().dirty || entry.tabId === this.tabId) return 'none'; // edits already live here
    if (entry.baseRevision > revision || sameDocument(entry.localDoc, doc)) {
      // The server is older than what the journal was based on (a restored backup), or already holds it.
      void this.journal.clear(this.sessionUser, timelineId).catch(() => undefined);
      return 'none';
    }
    this.recoveryEntry = entry;
    this.setState({ recovery: { savedAt: entry.savedAt } });
    return 'offered';
  }

  /** Restore: rebase the recovered edits onto what the server holds now (the offline reconciler) and save. */
  async restoreRecovered(): Promise<'sent' | 'rebased' | 'refused' | 'none'> {
    const entry = this.recoveryEntry;
    if (!entry || this.viewOnly) return 'none';
    this.recoveryEntry = null;
    this.setState({ recovery: null });
    this.savedDoc = this.store.getState().doc;
    this.memoryQueue = asQueueEntry(entry);
    return this.reconcile(this.memoryQueue, 'recovered');
  }

  /** Discard: forget the recovered edits for good. */
  discardRecovered(): void {
    this.recoveryEntry = null;
    this.clearJournal();
    this.setState({ recovery: null });
  }

  // ── R02.2 signing in again, in place ──────────────────────────────────────

  /** The chip's "Sign in": asks the app to show the sign-in dialog again (the user closed it). */
  requestSignIn(): void {
    if (this.state.status === 'authExpired') this.setState({ signInRequestedAt: this.now() });
  }

  /** After the app re-authenticated: send what is waiting, without a reload. No-op unless `authExpired`. */
  async resumeAfterSignIn(): Promise<void> {
    if (this.disposed || this.state.status !== 'authExpired') return;
    const now = this.currentUser();
    const pending = this.store.getState().dirty || this.extraOps.length > 0;
    if (now === ANONYMOUS_SCOPE) return; // still no readable account: stay on "sign in again"
    if (now !== this.sessionUser || this.sessionUser === ANONYMOUS_SCOPE) {
      // A different account signed in: the pending edits are NOT flushed under the new identity.
      if (pending) this.setState({ accountChanged: { previous: this.sessionIdentity } });
      else this.onIdentityChanged?.(); // nothing to protect: rebuild the editor for the new identity
      return;
    }
    this.setState({ status: this.store.getState().dirty || this.extraOps.length > 0 ? 'dirty' : 'saved', message: null, reasons: [], attempt: 0 });
    await this.flush();
  }

  // ── R02.1 keep my copy ────────────────────────────────────────────────────

  /**
   * "Save my edits as a new timeline": duplicates the timeline (named "<name> (my edits)") and writes this
   * tab's document into the copy, so the user's work exists as its own timeline and nothing is lost either
   * way. Resolves with the new timeline's id; throws when the server refused (the edits stay in the journal).
   */
  async saveAsCopy(): Promise<{ id: string; name: string }> {
    const duplicate = this.api.duplicateEditTimeline;
    if (!duplicate) throw new Error('Saving a copy is not available.');
    if (!this.mayTalk()) throw new Error('You are signed in as a different account. Sign in again to save a copy.');
    const { projectId, timelineId, doc } = this.store.getState();
    this.writeJournal();
    const base = (doc.name || 'Timeline').slice(0, 180);
    const name = `${base} (my edits)`;
    const copy = await duplicate(projectId, timelineId, name);
    const document: EditTimeline = { ...doc, id: copy.id, name, projectId };
    await this.api.patchEditTimeline(projectId, copy.id, { baseRevision: copy.revision, ops: [], document });
    return { id: copy.id, name };
  }

  /** The user chose to leave this tab's edits behind (after a copy or a recovery file): forget them. */
  discardLocalEdits(): void {
    this.clearQueue();
    this.clearJournal();
    this.extraOps = [];
  }

  // ── R02.1 two tabs of this browser ────────────────────────────────────────

  private startPresence(): void {
    if (!this.wantPresence || this.viewOnly || this.presence || this.disposed) return;
    const presence = new TabPresence({
      timelineId: this.store.getState().timelineId,
      tabId: this.tabId,
      isOwner: () => this.state.status !== 'otherTab' && this.state.status !== 'takenOver',
      onRole: (role) => this.onPresenceRole(role),
      releaseFor: async () => {
        await this.flush();
        this.writeJournal();
      },
    });
    if (!presence.start()) return;
    this.presence = presence;
  }

  private onPresenceRole(role: PresenceRole): void {
    if (this.disposed) return;
    if (role === 'other' && this.state.status !== 'otherTab') this.setState({ status: 'otherTab', message: 'This timeline is open in another tab.' });
    if (role === 'superseded') this.setState({ status: 'takenOver', message: 'Editing moved to another tab.' });
  }

  /**
   * Take the timeline over from the other tab: it saves what it holds and lets go, then this resolves —
   * the caller reloads the document (this tab's copy may be older than what the other tab saved).
   */
  async takeOver(): Promise<void> {
    if (this.presence) await this.presence.takeOver();
  }

  // ── page lifecycle ────────────────────────────────────────────────────────

  /**
   * The page is going away while a normal (non-keepalive) PATCH is in flight: the browser aborts it
   * with the page and the server honours the abort, and a queued follow-up would die too. So the
   * NEWEST document goes now, with keepalive, on the IN-FLIGHT request's base and carrying all ops
   * since that base — when nothing changed since, that is the in-flight body again, identically
   * (C-319-1); its `409` when the first one landed is the same document, so harmless. Only when the
   * in-flight request already asked for keepalive and nothing changed is nothing sent.
   *
   * Why that base and not `base + 1`: the revision check then guarantees that exactly one of the two
   * requests can win and neither can overwrite anyone. If the in-flight request lands, this one is a
   * harmless `409` (the edits made after it are lost only if the page really unloads — as before). If
   * the in-flight request is lost (aborted by the unload), this one saves everything. Basing it on
   * `base + 1` would save more often, but if the in-flight request lost to ANOTHER writer at
   * `base + 1`, it would silently overwrite their revision — never acceptable.
   *
   * If the page survives (bfcache, a tab switch), the run loop settles the outcome after the in-flight
   * attempt: a success is a save, a 5xx/network failure is remembered as an uncertain request, and the
   * expected 409 is ignored.
   */
  private sendLastChance(): void {
    const inFlight = this.inFlightRequest;
    if (!inFlight || this.lastChance) return;
    const storeState = this.store.getState();
    // Nothing beyond it: a keepalive request outlives the page already, but a normal one is aborted by
    // the unload (and the server honours the abort), so it goes again, identically, with keepalive.
    const unchanged = storeState.doc === inFlight.doc && this.extraOps.length === 0;
    if (unchanged && inFlight.keepalive) return;
    try {
      assertInvariants(storeState.doc);
    } catch {
      return; // never send a document the engine refuses; the normal flush reports it
    }
    const extras = [...inFlight.extras, ...this.extraOps];
    if (!this.mayTalk()) return;
    const sent: SentRequest = { baseRevision: inFlight.baseRevision, doc: storeState.doc, opCount: storeState.pendingOps.length, extraCount: extras.length };
    const request = { baseRevision: sent.baseRevision, ops: [...storeState.pendingOps, ...extras], document: sent.doc };
    this.lastChance = this.api
      .patchEditTimeline(storeState.projectId, storeState.timelineId, request, { keepalive: true })
      .then(
        (response) => ({ sent, result: { ok: true as const, revision: response.revision } }),
        (error: unknown) => ({ sent, result: { ok: false as const, error } }),
      );
  }

  /** Folds a last-chance PATCH's outcome in, once the request that was in flight beside it settled. */
  private async settleLastChance(): Promise<void> {
    const pending = this.lastChance;
    if (!pending) return;
    const { sent, result } = await pending;
    this.lastChance = null;
    // Both carried the same base: if the store moved past it, the in-flight request won (expected 409).
    if (this.store.getState().revision !== sent.baseRevision || this.state.status === 'conflict') return;
    if (result.ok) {
      // extraOps is [...the in-flight request's extras, ...attached since] after its failure.
      this.extraOps = this.extraOps.slice(sent.extraCount);
      this.onSaved(result.revision, sent.opCount, sent.doc);
    } else if (isTransient(result.error)) {
      this.uncertainSends.push(sent);
    }
  }

  /**
   * Flushes on `beforeunload`, `visibilitychange` (hidden) and `pagehide` with `keepalive`, so the
   * request outlives the page; warns on `beforeunload` while dirty.
   *
   * `beforeunload` is the earliest of the three on a full navigation: `pagehide` fires only once the
   * next page has answered, and a keepalive PATCH first issued there was lost about one time in four
   * on mobile Chromium (T-073). Sending at `beforeunload` costs nothing if the user stays: it is the
   * autosave a little early, and the `pagehide` after it only joins the save already in flight.
   */
  attachLifecycle(target: Window): () => void {
    const onHide = () => {
      if (this.disposed || this.viewOnly || !this.store.getState().dirty) return;
      // R02.2: the journal write starts NOW (localStorage is synchronous, the IndexedDB put is begun in this task).
      this.writeJournal();
      if (this.inFlightRequest) this.sendLastChance();
      void this.runFlush(true);
    };
    const onVisibility = () => {
      if (target.document.visibilityState === 'hidden') onHide();
    };
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      if (this.store.getState().dirty) {
        onHide();
        event.preventDefault();
        event.returnValue = '';
      }
    };
    // A client-side route change (Next <Link>) fires none of the three events above and the editor only unmounts
    // (and dispose() flushes) once the next route's payload arrives: 0.5-7 s on a loaded machine, so the 500 ms
    // debounce, not the leave, saved the edit. Sending when the user ACTS on a same-origin link starts the flush
    // within the 300 ms contract (P3-SYNC-10); like beforeunload it is only the autosave a little early.
    const onLinkClick = (event: MouseEvent) => {
      if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
      const a = (event.target as Element | null)?.closest?.('a[href]') as HTMLAnchorElement | null;
      if (!a || (a.target && a.target !== '_self') || a.hasAttribute('download')) return;
      if (a.origin !== target.location.origin || (a.pathname === target.location.pathname && a.search === target.location.search)) return;
      onHide();
    };
    const onOnline = () => {
      void this.reconnect();
    };
    const onOffline = () => {
      if (this.state.status === 'retrying' || this.state.status === 'dirty') this.enterOffline();
    };
    target.addEventListener('pagehide', onHide);
    target.document.addEventListener('visibilitychange', onVisibility);
    target.addEventListener('beforeunload', onBeforeUnload);
    target.document.addEventListener('click', onLinkClick, true);
    target.addEventListener('online', onOnline);
    target.addEventListener('offline', onOffline);
    this.startPresence();
    this.detachLifecycle = () => {
      target.removeEventListener('online', onOnline);
      target.removeEventListener('offline', onOffline);
      target.removeEventListener('pagehide', onHide);
      target.document.removeEventListener('visibilitychange', onVisibility);
      target.removeEventListener('beforeunload', onBeforeUnload);
      target.document.removeEventListener('click', onLinkClick, true);
    };
    return this.detachLifecycle;
  }

  /**
   * Stops the client. A pending edit is flushed FIRST (with `keepalive`: a route change can be
   * followed by an unload), then timers, the store subscription and the lifecycle listeners go.
   */
  dispose(): void {
    if (this.disposed) return;
    if (this.canSend() || this.inFlight) void this.runFlush(true);
    this.writeJournal();
    this.disposed = true;
    this.presence?.close();
    this.presence = null;
    this.unsubscribeSignOut?.();
    this.unsubscribeSignOut = null;
    if (this.journalHandle !== null) this.clearTimer(this.journalHandle);
    this.journalHandle = null;
    if (this.debounceHandle !== null) this.clearTimer(this.debounceHandle);
    if (this.retryHandle !== null) this.clearTimer(this.retryHandle);
    this.debounceHandle = null;
    this.retryHandle = null;
    this.unsubscribeStore?.();
    this.unsubscribeStore = null;
    this.detachLifecycle?.();
    this.detachLifecycle = null;
    this.listeners.clear();
  }
}

/** A journal entry as the offline reconciler's queue entry (the same fields, minus the tab). */
function asQueueEntry(entry: JournalEntry): QueueEntry {
  return {
    version: 1,
    projectId: entry.projectId,
    timelineId: entry.timelineId,
    baseRevision: entry.baseRevision,
    baseDoc: entry.baseDoc,
    localDoc: entry.localDoc,
    ops: entry.ops,
    savedAt: entry.savedAt,
    userId: entry.userId,
  };
}

export function createSyncClient(options: SyncClientOptions): SyncClient {
  return new SyncClient(options);
}

function isViolation(value: unknown): value is EditTimelineViolation {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as { path?: unknown }).path === 'string' &&
    typeof (value as { message?: unknown }).message === 'string'
  );
}

const UNCONFIRMED_SAVE = 'The last save could not be confirmed. It will try again.';

/** A network error or 5xx: the request may have reached the server and been applied. */
function isTransient(error: unknown): boolean {
  return !(error instanceof ApiError) || error.status >= 500;
}

function isConflictAt(error: unknown, revision: number): boolean {
  if (!(error instanceof ApiError) || error.status !== 409) return false;
  const body = error.body as Partial<EditTimelineConflict> | undefined;
  return body?.currentRevision === revision;
}

/**
 * Structural equality of two documents, insensitive to key order and to a property that is absent
 * on one side and `null`/`undefined` on the other (the server may write optional fields as `null`).
 */
export function sameDocument(a: unknown, b: unknown): boolean {
  return canonicalJson(a) === canonicalJson(b);
}

function canonicalJson(value: unknown): string {
  return JSON.stringify(value, (_key, v: unknown) => {
    if (v && typeof v === 'object' && !Array.isArray(v)) {
      const out: Record<string, unknown> = {};
      for (const key of Object.keys(v).sort()) {
        const inner = (v as Record<string, unknown>)[key];
        if (inner !== null && inner !== undefined) out[key] = inner;
      }
      return out;
    }
    return v;
  });
}

// ── reasons: the server's answer as plain sentences ─────────────────────────

const GUID = /\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi;
const PATH_TOKEN = /\b[a-zA-Z]+\[\d+\](?:\.[a-zA-Z]+(?:\[\d+\])?)*/g;
const CAMEL = /\b[a-z]+(?:[A-Z][a-z0-9]*)+\b/g;
const PLACE_NAMES: Record<string, string> = {
  tracks: 'track',
  items: 'clip',
  sequences: 'sequence',
  buses: 'bus',
  markers: 'marker',
  angles: 'angle',
  transitions: 'transition',
  keys: 'keyframe',
  colorGroups: 'colour group',
};
const MAX_REASONS = 8;

/** "tracks[0].items[1].fadeIn" → "Track 1, clip 2"; null when the path names no place. */
export function placeOf(path: string): string | null {
  const parts: string[] = [];
  for (const match of path.matchAll(/([a-zA-Z]+)\[(\d+)\]/g)) {
    const name = PLACE_NAMES[match[1]];
    if (name) parts.push(`${name} ${Number(match[2]) + 1}`);
  }
  if (parts.length === 0) return null;
  return capitalize(parts.join(', '));
}

function capitalize(text: string): string {
  return text.length === 0 ? text : text[0].toUpperCase() + text.slice(1);
}

/** Every op name the client sends (`WireOp['type']`); the record keeps it exhaustive at compile time. */
const OP_NAMES: Readonly<Record<WireOp['type'], true>> = {
  insert: true, overwrite: true, lift: true, extract: true, trim: true, blade: true, deleteGap: true,
  closeGaps: true, move: true, setProperty: true, addMarker: true, setMarker: true, removeMarker: true,
  append: true, replace: true, fitToFill: true, placeOnTop: true, join: true, nudge: true, addTrack: true,
  removeTrack: true, moveTrack: true, setTrackProperty: true, setTimelineSettings: true, setInOut: true,
  setKeyframe: true, removeKeyframe: true, moveKeyframe: true, retime: true, addTransition: true,
  removeTransition: true, setColorGroup: true, setSequence: true, setBus: true, restore: true,
};

function isOpName(word: string): boolean {
  return Object.prototype.hasOwnProperty.call(OP_NAMES, word);
}

function stripOpPrefix(message: string): string {
  // EditEngineError (and an op-level server message) writes "<opType>: <message>"; the op name is not for people.
  const match = /^([a-z][A-Za-z]*):\s+/.exec(message);
  return match && isOpName(match[1]) ? message.slice(match[0].length) : message;
}

/**
 * Whether the text still names an op: a camelCase op name anywhere (humanising would spell it out as
 * "set keyframe"), or any op name in quotes ('insert'). Single-word op names unquoted are plain English.
 */
function mentionsOpName(text: string): boolean {
  for (const match of text.matchAll(/(['"`]?)\b([a-z]+(?:[A-Z][a-z0-9]*)*)\b\1/g)) {
    const word = match[2];
    if (!isOpName(word)) continue;
    if (match[1] !== '' || /[A-Z]/.test(word)) return true;
  }
  return false;
}

/** One server/engine message as a plain sentence: no ids, no array paths, no camelCase field names. */
export function humanizeMessage(message: string, capitalizeFirst: boolean): string {
  let text = message
    .replace(GUID, '')
    .replace(PATH_TOKEN, (token) => (placeOf(token) ?? '').toLowerCase())
    .replace(CAMEL, (word) => word.replace(/([A-Z])/g, ' $1').toLowerCase())
    .replace(/'\s*'/g, '')
    .replace(/\s{2,}/g, ' ')
    .trim();
  if (capitalizeFirst) text = capitalize(text);
  if (text.length > 0 && !/[.!?…]$/.test(text)) text += '.';
  return text;
}

/**
 * Text that is not a sentence for people: a JSON/HTML body, a .NET exception or stack trace, a URL,
 * a System.Text.Json / model-binding error (JSON paths, line/byte positions, type discriminators, the
 * default "The X field is required."), a dotted .NET type name, or an op name.
 * The chip falls back to a generic plain sentence instead of showing it.
 */
function looksRaw(message: string): boolean {
  const text = message.trim();
  return (
    /^[{[<]/.test(text) ||
    /\b(?:System|Microsoft|Npgsql)\.[A-Z]/.test(text) ||
    /\b[A-Z][A-Za-z0-9_]*(?:\.[A-Z][A-Za-z0-9_`]*)+\b/.test(text) ||
    /[A-Za-z]+Exception\b/.test(text) ||
    /^\s*at\s+\S+\(/m.test(text) ||
    /https?:\/\//.test(text) ||
    /"\w+"\s*:/.test(text) ||
    /\bJSON\b/.test(text) ||
    /\bPath:\s*\$|(?:^|[\s'"(])\$[.[]|\bLineNumber\b|\bBytePositionInLine\b/.test(text) ||
    /\b(?:type )?discriminator\b|\bmetadata propert/i.test(text) ||
    /^The \S+ field is required\.?$/.test(text) ||
    mentionsOpName(text)
  );
}

/** The chip's reason list for a failed save. */
export function reasonsFor(error: unknown): string[] {
  const out: string[] = [];
  const add = (reason: string) => {
    if (reason.length > 1 && !out.includes(reason)) out.push(reason);
  };
  if (error instanceof ApiError) {
    if (error.status === 409) add('This timeline was changed in another tab or by someone else.');
    else if (error.status === 413) add('The timeline is too large to save.');
    else if (error.status === 403) add('You no longer have access to this project.');
    else if (error.status === 404) add('This timeline no longer exists. It may have been deleted.');
    else if (error.status >= 500) add('The server could not save the timeline. It will try again.');
    else {
      const body = error.body;
      const violations = Array.isArray(body) ? (body as unknown[]).filter(isViolation) : [];
      for (const violation of violations) {
        const message = stripOpPrefix(violation.message);
        if (looksRaw(message)) continue;
        const place = placeOf(violation.path);
        add(place ? `${place}: ${humanizeMessage(message, false)}` : humanizeMessage(message, true));
      }
      if (out.length === 0) for (const entry of error.errors.map(stripOpPrefix)) if (!looksRaw(entry)) add(humanizeMessage(entry, true));
      const message = stripOpPrefix(error.message);
      if (out.length === 0 && message.trim() && !looksRaw(message)) add(humanizeMessage(message, true));
      if (out.length === 0) add('The server refused the timeline.');
    }
  } else {
    add('The server could not be reached. Check your connection.');
  }
  if (out.length > MAX_REASONS) {
    const more = out.length - MAX_REASONS;
    return [...out.slice(0, MAX_REASONS), `And ${more} more problem${more === 1 ? '' : 's'}.`];
  }
  return out;
}

function messageOf(error: unknown): string {
  return error instanceof Error && error.message.trim().length > 0 ? error.message : 'The save request failed.';
}

/** What the status chip says when the user's edits cannot be written to this device either (R02.2). */
export const MEMORY_ONLY = 'Edits are held in memory only';
/** What the chip says after a `401` (R02.2). */
export const SIGN_IN_AGAIN = 'Sign in again to keep saving';

/**
 * The chip label for a save state — one place, so every surface says the same thing. Plain sentences, one per
 * failure class (R02.3); never a revision counter, an op name or an id. A refused document says WHY, in the
 * words `refusal.ts` / the server's violations were turned into (`state.reasons[0]`).
 */
export function describeSaveState(state: SaveState): string {
  const memory = state.durability === 'memory' ? ` · ${MEMORY_ONLY}` : '';
  const why = state.reasons && state.reasons.length > 0 ? ` ${state.reasons[0]}` : '';
  if (state.viewOnly) return 'View only';
  switch (state.status) {
    case 'saved':
      return 'Saved';
    case 'dirty':
      return `Unsaved changes${memory}`;
    case 'saving':
      return 'Saving…';
    case 'retrying':
      return `Can’t reach the server. Trying again…${memory}`;
    case 'invalid':
      return `Not saved.${why || ' This edit breaks a timeline rule.'}`;
    case 'rejected':
      if (state.problem === 'tooLarge') return 'Not saved. This timeline is too large to save. Remove some media or clips, then try again.';
      return `Not saved. The server refused this change.${why}`;
    case 'conflict':
      return 'Not saved. This timeline was changed somewhere else.';
    case 'error':
      if (state.problem === 'noPermission') return 'Not saved. You no longer have permission to change this timeline.';
      if (state.problem === 'deleted') return 'Not saved. This timeline was deleted.';
      return `Not saved. The server had a problem.${memory}`;
    case 'offline':
      return `You are offline. ${state.queuedOps ?? 0} change${(state.queuedOps ?? 0) === 1 ? '' : 's'} will save when you are back${memory}`;
    case 'reconnecting':
      return 'Back online. Checking for changes…';
    case 'authExpired':
      return SIGN_IN_AGAIN;
    case 'otherTab':
      return 'Open in another tab';
    case 'takenOver':
      return 'Editing moved to another tab';
    default:
      return '';
  }
}
