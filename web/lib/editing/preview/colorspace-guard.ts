/**
 * Keeps a decoded frame's colour space inside the values mediabunny accepts.
 *
 * mediabunny wraps every decoded `VideoFrame` in a `VideoSample` and copies `frame.colorSpace` into a
 * `VideoSampleColorSpace`, which THROWS on any primaries/transfer/matrix value outside its maps (the
 * WebCodecs enums). WebKit's decoder reports a stream that does not signal its primaries or transfer
 * as `"unspecified"`: every frame of such a source failed, and the viewer drew that layer black. An
 * ffmpeg output that tags its matrix but not its primaries is common (the seeded vertical clip is one).
 *
 * The guard wraps the `VideoColorSpace` getters so a value outside those enums reads as `null`
 * ("not known"), which the WebCodecs IDL allows and mediabunny treats as unspecified. Drawing and
 * texture upload use the frame's internal colour information, never these getters, so the picture is
 * unchanged. Installed once, before the first source opens.
 */

/** The values mediabunny validates against (its COLOR_PRIMARIES / TRANSFER / MATRIX maps). */
export const KNOWN_COLOR_SPACE_VALUES = {
  primaries: ['bt709', 'bt470bg', 'smpte170m', 'bt2020', 'smpte432'],
  transfer: ['bt709', 'smpte170m', 'linear', 'iec61966-2-1', 'pq', 'hlg'],
  matrix: ['rgb', 'bt709', 'bt470bg', 'smpte170m', 'bt2020-ncl'],
} as const;

const GUARDED = Symbol.for('reelbolt.videoColorSpaceGuard');

/**
 * Installs the guard on `colorSpaceClass.prototype` (the browser's `VideoColorSpace` by default).
 * Returns true when it was installed now; false when already installed or there is nothing to guard.
 */
export function guardVideoColorSpace(colorSpaceClass: unknown = (globalThis as { VideoColorSpace?: unknown }).VideoColorSpace): boolean {
  if (typeof colorSpaceClass !== 'function') return false;
  const proto = (colorSpaceClass as { prototype?: Record<PropertyKey, unknown> }).prototype;
  if (!proto || proto[GUARDED]) return false;
  let installed = false;
  for (const key of ['primaries', 'transfer', 'matrix'] as const) {
    const descriptor = Object.getOwnPropertyDescriptor(proto, key);
    const read = descriptor?.get;
    if (!descriptor || !read || !descriptor.configurable) continue;
    const known = new Set<string>(KNOWN_COLOR_SPACE_VALUES[key]);
    Object.defineProperty(proto, key, {
      ...descriptor,
      get(this: unknown) {
        const value = read.call(this);
        return typeof value === 'string' && !known.has(value) ? null : value;
      },
    });
    installed = true;
  }
  if (installed) Object.defineProperty(proto, GUARDED, { value: true });
  return installed;
}
