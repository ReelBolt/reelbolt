import { beforeEach, describe, expect, it, vi } from 'vitest';
import { doc, media, track } from '../../__tests__/fixtures';
import { ANONYMOUS_SCOPE, userScopeFromEmail, wipeEditorLocalData } from '../../sync/user-scope';
import { clearClipClipboardForTests, clipboardKey, copyClips, parseOwnedClipboard, readClipClipboard, writeClipClipboard } from '..';

const OWNER_A = userScopeFromEmail('a@x.com');
const OWNER_B = userScopeFromEmail('b@x.com');

const who = vi.hoisted(() => ({ email: 'a@x.com' as string | null }));
vi.mock('../../../hooks/use-auth', () => ({ getUserFromCookie: () => (who.email ? { email: who.email } : null) }));

const payload = () => copyClips(doc([track('v1', 'video', [media('a', 0, 30)])]), ['a'])!;

beforeEach(() => {
  who.email = 'a@x.com';
  clearClipClipboardForTests();
});

describe('clipboard ownership', () => {
  it('another user on the same browser cannot read or paste the clips', () => {
    writeClipClipboard(payload());
    expect(readClipClipboard()).not.toBeNull();
    who.email = 'b@x.com';
    expect(readClipClipboard()).toBeNull();
  });
  it('refuses foreign or unstamped system-clipboard text, and anonymous never matches', () => {
    writeClipClipboard(payload());
    const text = localStorage.getItem(clipboardKey(OWNER_A));
    expect(parseOwnedClipboard(text, OWNER_A)).not.toBeNull();
    expect(parseOwnedClipboard(text, OWNER_B)).toBeNull();
    expect(parseOwnedClipboard(text, ANONYMOUS_SCOPE)).toBeNull();
    expect(parseOwnedClipboard(JSON.stringify(payload()), OWNER_A)).toBeNull();
  });
  it('signed-out (anonymous) neither writes nor reads', () => {
    who.email = null;
    writeClipClipboard(payload());
    expect(readClipClipboard()).toBeNull();
    expect(localStorage.length).toBe(0);
  });
  it('sign-out wipe removes the clipboard key', async () => {
    writeClipClipboard(payload());
    expect(localStorage.getItem(clipboardKey(OWNER_A))).not.toBeNull();
    await wipeEditorLocalData({ broadcast: false });
    expect(localStorage.getItem(clipboardKey(OWNER_A))).toBeNull();
  });
  it('drops the legacy unscoped key without adopting it', () => {
    localStorage.setItem('reelforge-editor-clip-clipboard', JSON.stringify(payload()));
    expect(readClipClipboard()).toBeNull();
    expect(localStorage.getItem('reelforge-editor-clip-clipboard')).toBeNull();
  });
});
