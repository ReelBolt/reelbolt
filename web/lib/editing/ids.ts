/**
 * The one id generator for everything the editor mints into an EditTimeline document.
 *
 * Every id field the server deserializes (timeline, track, item, marker, sequence, angle, bus,
 * colour group, grade node) is a .NET `Guid`, so an id that is not a canonical 8-4-4-4-12 hex
 * string fails model binding and the whole save is refused. `crypto.randomUUID` only exists in a
 * secure context (HTTPS or localhost) and on Safari 15.4+, so it is deliberately NOT used: a LAN or
 * self-hosted editor served over plain HTTP still gets real v4 UUIDs from `crypto.getRandomValues`,
 * which every browser exposes in any context. `Math.random` is the last resort when even that is
 * missing (old test runtimes); it still produces a well-formed v4 UUID.
 */

/** The canonical `Guid` text form System.Text.Json binds: 8-4-4-4-12 hex digits, either case. */
export const GUID_PATTERN = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;

/** True when `value` is a string the server binds as a `Guid`. */
export function isGuid(value: unknown): value is string {
  return typeof value === 'string' && GUID_PATTERN.test(value);
}

type RandomSource = { getRandomValues?: (array: Uint8Array) => Uint8Array };

function randomBytes(count: number): Uint8Array {
  const bytes = new Uint8Array(count);
  const source = (globalThis as { crypto?: RandomSource }).crypto;
  if (typeof source?.getRandomValues === 'function') {
    source.getRandomValues(bytes);
    return bytes;
  }
  for (let i = 0; i < count; i++) bytes[i] = Math.floor(Math.random() * 256);
  return bytes;
}

const HEX = Array.from({ length: 256 }, (_, i) => i.toString(16).padStart(2, '0'));

/** A fresh RFC 4122 version-4 UUID, lower case, without needing `crypto.randomUUID`. */
export function newGuid(): string {
  const b = randomBytes(16);
  b[6] = (b[6] & 0x0f) | 0x40; // version 4
  b[8] = (b[8] & 0x3f) | 0x80; // RFC 4122 variant
  const h = Array.from(b, (byte) => HEX[byte]);
  return `${h[0]}${h[1]}${h[2]}${h[3]}-${h[4]}${h[5]}-${h[6]}${h[7]}-${h[8]}${h[9]}-${h[10]}${h[11]}${h[12]}${h[13]}${h[14]}${h[15]}`;
}
