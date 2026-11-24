/** @vitest-environment node */
/**
 * R02.1 two tabs of one browser: a fake BroadcastChannel hub connects TabPresence instances.
 * The later tab becomes `other` (read-only, notice); taking over makes the editing tab flush, become
 * `superseded` and answer `released`.
 */

import { describe, expect, it, vi } from 'vitest';
import { TabPresence, type PresenceChannel, type PresenceRole } from '../tab-presence';

function hub() {
  const members: Array<{ listener: (event: MessageEvent) => void; id: number }> = [];
  let next = 0;
  const factory = (): PresenceChannel => {
    const id = next++;
    const me: { listener: (event: MessageEvent) => void; id: number } = { listener: () => undefined, id };
    members.push(me);
    return {
      postMessage: (message) => {
        // BroadcastChannel delivers to every OTHER channel object, asynchronously.
        for (const other of members) if (other.id !== id) queueMicrotask(() => other.listener({ data: message } as MessageEvent));
      },
      addEventListener: (_type, listener) => {
        me.listener = listener as (event: MessageEvent) => void;
      },
      close: () => undefined,
    };
  };
  return factory;
}

const tab = (factory: () => PresenceChannel, tabId: string) => {
  const roles: PresenceRole[] = [];
  let owner = true;
  const releaseFor = vi.fn(async () => undefined);
  const presence = new TabPresence({
    timelineId: 't',
    tabId,
    isOwner: () => owner,
    onRole: (role) => {
      roles.push(role);
      owner = role === 'owner';
    },
    releaseFor,
    channelFactory: factory,
    takeoverTimeoutMs: 500,
  });
  return { presence, roles, releaseFor };
};

const settle = () => new Promise<void>((resolve) => setTimeout(resolve, 5));

describe('TabPresence', () => {
  it('the second tab learns it is not the editing one; the first is undisturbed', async () => {
    const factory = hub();
    const first = tab(factory, 'a');
    const second = tab(factory, 'b');
    expect(first.presence.start()).toBe(true);
    await settle();
    expect(second.presence.start()).toBe(true);
    await settle();
    expect(first.roles).toEqual([]);
    expect(second.roles).toEqual(['other']);
  });

  it('taking over: the editing tab saves first, becomes superseded, and the taker is released', async () => {
    const factory = hub();
    const first = tab(factory, 'a');
    const second = tab(factory, 'b');
    first.presence.start();
    await settle();
    second.presence.start();
    await settle();
    await second.presence.takeOver();
    expect(first.releaseFor).toHaveBeenCalledTimes(1);
    expect(first.roles).toEqual(['superseded']);
  });

  it('a lone tab hears nothing and keeps editing; a browser without a channel means no detection', async () => {
    const factory = hub();
    const only = tab(factory, 'a');
    expect(only.presence.start()).toBe(true);
    await settle();
    expect(only.roles).toEqual([]);
    const none = new TabPresence({ timelineId: 't', tabId: 'x', isOwner: () => true, onRole: () => undefined, releaseFor: async () => undefined, channelFactory: () => null });
    expect(none.start()).toBe(false);
    await expect(none.takeOver()).resolves.toBeUndefined();
  });
});
