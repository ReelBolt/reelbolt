// @vitest-environment jsdom
import { describe, expect, it } from 'vitest';
import type { UserInfo } from '../types/auth';
import { parseUserCookie, readUserCookie, serializeUserCookie, writeUserCookie } from './user-cookie';

const base: UserInfo = { email: 'a@b.c', mustChangePassword: false };

describe('user cookie', () => {
  it("reads Go's '+'-for-space encoding as a space", () => {
    const goStyle = 'displayName%22%3A%22Ada+L.%22';
    const raw = `%7B%22email%22%3A%22a%40b.c%22%2C%22mustChangePassword%22%3Afalse%2C%22${goStyle}%2C%22orgName%22%3A%22My+Team%22%7D`;
    const u = parseUserCookie(raw);
    expect(u?.displayName).toBe('Ada L.');
    expect(u?.orgName).toBe('My Team');
  });

  it.each(['Ada L.', 'a+b c', 'Zoë 山田 🎬', '100%', '%41', 'say "hi" \\ ok', '%'])('round-trips %s', (name) => {
    const u = { ...base, displayName: name, orgName: name };
    expect(parseUserCookie(serializeUserCookie(u))).toEqual(u);
    writeUserCookie(u);
    expect(readUserCookie()).toEqual(u);
  });

  it('returns null for garbage', () => {
    expect(parseUserCookie('%7Bnot-json')).toBeNull();
    expect(parseUserCookie(undefined)).toBeNull();
  });

  const goEscape = (u: object) => encodeURIComponent(JSON.stringify(u)).replace(/%20/g, '+');
  const tricky = { ...base, displayName: '100% %41 Ada+L "q"', orgName: 'My Team' };

  it('parses the raw Go-escaped string from document.cookie', () => {
    expect(parseUserCookie(goEscape(tricky))).toEqual({ ...tricky, displayName: '100% %41 Ada+L "q"' });
  });

  it('parses the value as given when Next has already decoded it once, and never decodes twice', () => {
    // What request.cookies.get() returns. Next does not map '+' to a space, which is fine: the server
    // reads flags, not names.
    const decodedOnce = decodeURIComponent(goEscape({ ...base, displayName: '100%%41"q"' }));
    expect(parseUserCookie(decodedOnce)?.displayName).toBe('100%%41"q"');
    // Would have thrown (URIError) or changed the text if decoded again.
    expect(() => parseUserCookie('{"displayName":"100%"}')).not.toThrow();
    expect(parseUserCookie('{"displayName":"100%"}')?.displayName).toBe('100%');
  });

  it('returns null, not a throw, for non-objects and malformed values', () => {
    for (const bad of ['%E0%A4%A', '"str"', '[1]', '123', '{', '%']) {
      expect(() => parseUserCookie(bad)).not.toThrow();
      expect(parseUserCookie(bad)).toBeNull();
    }
  });
});
