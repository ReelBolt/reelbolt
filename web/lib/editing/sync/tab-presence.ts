/**
 * R02.1: two tabs of the same browser on the same timeline.
 *
 * Two tabs editing one timeline is the commonest way to conflict with yourself. A `BroadcastChannel`
 * per timeline lets them find each other:
 *
 *   - A tab that opens announces itself (`hello`). A tab that is editing answers (`here`), and the
 *     newcomer becomes `other` — read-only with a notice "Open in another tab" and a choice to take over.
 *   - `takeOver()` asks the editing tab to let go (`takeover`). It saves what it holds (`releaseFor`),
 *     becomes `superseded` (read-only, with a plain banner) and answers `released`; the taker then reloads
 *     the document, so it starts from what the other tab just saved.
 *
 * The channel is a courtesy, not a lock: a browser without `BroadcastChannel`, or a tab that never
 * answers, simply means no detection — the server's revision check and the online rebase still protect
 * every write. Fully injectable (`channelFactory`, timers) for tests.
 */

export type PresenceRole = 'owner' | 'other' | 'superseded';

type Message =
  | { t: 'hello'; from: string }
  | { t: 'here'; from: string; to: string }
  | { t: 'takeover'; from: string }
  | { t: 'released'; from: string; to: string };

export interface PresenceChannel {
  postMessage(message: Message): void;
  addEventListener(type: 'message', listener: (event: MessageEvent<Message>) => void): void;
  close(): void;
}

export interface TabPresenceOptions {
  timelineId: string;
  tabId: string;
  /** Whether this tab is editing right now (and so answers a newcomer / yields to a taker). */
  isOwner: () => boolean;
  onRole: (role: PresenceRole) => void;
  /** Saves what this tab holds before it lets go. */
  releaseFor: () => Promise<void>;
  channelFactory?: (name: string) => PresenceChannel | null;
  /** How long `takeOver()` waits for the other tab to answer, ms. Default 4000. */
  takeoverTimeoutMs?: number;
}

const defaultFactory = (name: string): PresenceChannel | null => {
  try {
    return typeof BroadcastChannel === 'undefined' ? null : (new BroadcastChannel(name) as unknown as PresenceChannel);
  } catch {
    return null;
  }
};

export class TabPresence {
  private channel: PresenceChannel | null = null;
  private waiting: ((released: boolean) => void) | null = null;

  constructor(private readonly options: TabPresenceOptions) {}

  /** Starts listening and announces this tab. `false` when the browser has no channel (no detection). */
  start(): boolean {
    const factory = this.options.channelFactory ?? defaultFactory;
    const channel = factory(`reelbolt-editor-tab:${this.options.timelineId}`);
    if (!channel) return false;
    this.channel = channel;
    channel.addEventListener('message', (event) => void this.onMessage(event.data));
    channel.postMessage({ t: 'hello', from: this.options.tabId });
    return true;
  }

  private async onMessage(message: Message): Promise<void> {
    const { tabId } = this.options;
    if (!message || message.from === tabId) return;
    switch (message.t) {
      case 'hello':
        if (this.options.isOwner()) this.channel?.postMessage({ t: 'here', from: tabId, to: message.from });
        return;
      case 'here':
        if (message.to === tabId && this.options.isOwner()) this.options.onRole('other');
        return;
      case 'takeover':
        if (!this.options.isOwner()) return;
        try {
          await this.options.releaseFor();
        } finally {
          this.options.onRole('superseded');
          this.channel?.postMessage({ t: 'released', from: tabId, to: message.from });
        }
        return;
      case 'released':
        if (message.to === tabId) this.waiting?.(true);
        return;
    }
  }

  /** Asks the editing tab to let go; resolves when it did (or after the timeout — the caller reloads either way). */
  takeOver(): Promise<void> {
    const channel = this.channel;
    if (!channel) return Promise.resolve();
    return new Promise<void>((resolve) => {
      const timer = setTimeout(() => done(false), this.options.takeoverTimeoutMs ?? 4000);
      const done = (released: boolean) => {
        clearTimeout(timer);
        this.waiting = null;
        void released;
        resolve();
      };
      this.waiting = done;
      channel.postMessage({ t: 'takeover', from: this.options.tabId });
    });
  }

  close(): void {
    this.channel?.close();
    this.channel = null;
  }
}
